using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AutomaticClayFormer
{
    /// <summary>
    /// Automatic Clay Former. The client does the clicking (ClientFormer); the server charges clay for it
    /// (ServerClayChargingPatch) and keeps the tool mode in sync.
    /// </summary>
    public class ClayFormerModSystem : ModSystem
    {
        public const string ModId = "automaticclayformer";
        const string ChannelName = "automaticclayformer";
        const string HarmonyId = "demonic69.automaticclayformer";
        const int TickIntervalMs = 25;

        // ---- Server side ----

        internal static ClayFormerModSystem ServerInstance { get; private set; }

        Harmony harmony;
        IServerNetworkChannel serverChannel;

        /// <summary>Players whose game has this mod. Only they get status messages.</summary>
        readonly HashSet<string> playersWithMod = new HashSet<string>();

        /// <summary>Players whose game has 2.1.0 or newer (it said hello), so it takes /clayformer settings.</summary>
        readonly HashSet<string> playersWithCommands = new HashSet<string>();

        // ---- Client side ----

        ICoreClientAPI capi;
        IClientNetworkChannel clientChannel;
        ClientFormer former;
        long tickListenerId;
        long helloListenerId;
        bool helloSent;

        public ClayFormerConfig Config { get; private set; } = new ClayFormerConfig();

        public override void StartServerSide(ICoreServerAPI api)
        {
            ServerInstance = this;

            // Message types are numbered in the order they're registered: keep the client's order the same, and add new
            // ones at the end so older versions still agree on the first ones.
            serverChannel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ToolModeRequest>()
                .RegisterMessageType<FormerStatus>()
                .RegisterMessageType<SettingsCommand>()
                .SetMessageHandler<ToolModeRequest>(OnToolModeRequest);

            api.Event.PlayerDisconnect += player =>
            {
                playersWithMod.Remove(player.PlayerUID);
                playersWithCommands.Remove(player.PlayerUID);
            };

            RegisterServerCommands(api);

            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(ClayFormerModSystem).Assembly);
        }

        void OnToolModeRequest(IServerPlayer fromPlayer, ToolModeRequest request)
        {
            if (fromPlayer == null || request == null) return;
            playersWithMod.Add(fromPlayer.PlayerUID);

            if (request.Mode == ToolModeRequest.Hello)
            {
                playersWithCommands.Add(fromPlayer.PlayerUID);
                return;
            }

            if (request.Mode < FormingPlanner.ToolOne || request.Mode > FormingPlanner.ToolCopyLayer) return;

            ItemSlot slot = fromPlayer.InventoryManager?.ActiveHotbarSlot;
            if (!(slot?.Itemstack?.Collectible is ItemClay clay)) return;

            BlockSelection blockSel = new BlockSelection { Position = new BlockPos(request.X, request.Y, request.Z, request.Dim) };
            clay.SetToolMode(slot, fromPlayer, blockSel, request.Mode);
        }

        internal void NotifyOutOfClay(IServerPlayer player, BlockPos pos)
        {
            if (player == null || serverChannel == null || !playersWithMod.Contains(player.PlayerUID)) return;

            serverChannel.SendPacket(new FormerStatus { Code = FormerStatus.OutOfClay, X = pos.X, Y = pos.Y, Z = pos.Z }, player);
        }

        // ---- Client ----

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            Config = LoadConfig(api);

            clientChannel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ToolModeRequest>()
                .RegisterMessageType<FormerStatus>()
                .RegisterMessageType<SettingsCommand>()
                .SetMessageHandler<FormerStatus>(status => former?.OnServerStatus(status))
                .SetMessageHandler<SettingsCommand>(OnSettingsCommand);

            former = new ClientFormer(api, this, clientChannel);
            tickListenerId = api.World.RegisterGameTickListener(former.OnGameTick, TickIntervalMs);
            helloListenerId = api.World.RegisterGameTickListener(dt => TrySendHello(), 1000);
            api.Event.LeftWorld += OnLeftWorld;
        }

        /// <summary>
        /// Tells the server once that this game has the mod, so /clayformer works. The channel connects only when
        /// the server has the mod too; until then (or on a server without it) this just waits.
        /// </summary>
        void TrySendHello()
        {
            if (helloSent || capi == null || clientChannel == null || !clientChannel.Connected) return;
            clientChannel.SendPacket(new ToolModeRequest { Mode = ToolModeRequest.Hello });
            helloSent = true;
            if (helloListenerId != 0) capi.World.UnregisterGameTickListener(helloListenerId);
            helloListenerId = 0;
        }

        void OnLeftWorld()
        {
            former?.Reset();
        }

        static ClayFormerConfig LoadConfig(ICoreAPI api)
        {
            ClayFormerConfig config;
            try
            {
                config = api.LoadModConfig<ClayFormerConfig>(ClayFormerConfig.FileName);
            }
            catch (Exception e)
            {
                // Keep the broken file so the player can fix it, and run with defaults for now
                api.Logger.Error("[Automatic Clay Former] Could not read ModConfig/" + ClayFormerConfig.FileName + ", using default settings: " + e.Message);
                return new ClayFormerConfig();
            }

            if (config == null) config = new ClayFormerConfig();
            config.Clamp();

            // Writes the file the first time, and adds any settings that are new since it was written
            TryStoreConfig(api, config);
            return config;
        }

        static void TryStoreConfig(ICoreAPI api, ClayFormerConfig config)
        {
            try
            {
                api.StoreModConfig(config, ClayFormerConfig.FileName);
            }
            catch (Exception e)
            {
                api.Logger.Error("[Automatic Clay Former] Could not save ModConfig/" + ClayFormerConfig.FileName + ": " + e.Message);
            }
        }

        void SaveConfig()
        {
            Config.Clamp();
            TryStoreConfig(capi, Config);
        }

        // ---- /clayformer ----

        /// <summary>
        /// /clayformer runs on the server (a "/" command), but the settings are each player's own, kept in their
        /// game's ModConfig. So the server checks the command and hands it to the player's game
        /// (<see cref="OnSettingsCommand"/>), which applies it and shows the result.
        /// </summary>
        void RegisterServerCommands(ICoreServerAPI api)
        {
            CommandArgumentParsers parsers = api.ChatCommands.Parsers;

            api.ChatCommands.Create("clayformer")
                .WithDescription("Automatic Clay Former: show your settings, or change them with a subcommand")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(args => Relay(args, new SettingsCommand { Action = SettingsCommand.Show }))

                .BeginSubCommand("on")
                    .WithDescription("Turn automatic clay forming on")
                    .HandleWith(args => Relay(args, new SettingsCommand { Action = SettingsCommand.On }))
                .EndSubCommand()

                .BeginSubCommand("off")
                    .WithDescription("Turn automatic clay forming off")
                    .HandleWith(args => Relay(args, new SettingsCommand { Action = SettingsCommand.Off }))
                .EndSubCommand()

                .BeginSubCommand("range")
                    .WithDescription("How far away clay forms get worked, in blocks (" + Format(ClayFormerConfig.MinRange) + " to " + Format(ClayFormerConfig.MaxRange) + ", capped at your reach)")
                    .WithArgs(parsers.Float("blocks"))
                    .HandleWith(args => Relay(args, new SettingsCommand { Action = SettingsCommand.Range, Value = (float)args[0] }))
                .EndSubCommand()

                .BeginSubCommand("speed")
                    .WithDescription("Forming clicks per second (" + Format(ClayFormerConfig.MinActionsPerSecond) + " to " + Format(ClayFormerConfig.MaxActionsPerSecond) + ")")
                    .WithArgs(parsers.Float("clicksPerSecond"))
                    .HandleWith(args => Relay(args, new SettingsCommand { Action = SettingsCommand.Speed, Value = (float)args[0] }))
                .EndSubCommand()

                .BeginSubCommand("messages")
                    .WithDescription("Switch chat messages when forming starts and finishes on or off (or add on or off)")
                    .WithArgs(parsers.OptionalBool("show", "on"))
                    .HandleWith(args => Relay(args, args.Parsers[0].IsMissing
                        ? new SettingsCommand { Action = SettingsCommand.MessagesToggle }
                        : new SettingsCommand { Action = SettingsCommand.Messages, Flag = (bool)args[0] }))
                .EndSubCommand()

                .BeginSubCommand("help")
                    .WithDescription("How to use Automatic Clay Former, and its commands")
                    .HandleWith(Help)
                .EndSubCommand();
        }

        /// <summary>/clayformer help: answered by the server itself, so it works even before the player's game has the mod.</summary>
        TextCommandResult Help(TextCommandCallingArgs args)
        {
            string text = Lang.Get("automaticclayformer:cmd-help");
            if (args.Caller.Player is IServerPlayer player && !playersWithCommands.Contains(player.PlayerUID))
            {
                text += "\n" + Lang.Get("automaticclayformer:cmd-notinstalled");
            }

            return TextCommandResult.Success(text);
        }

        TextCommandResult Relay(TextCommandCallingArgs args, SettingsCommand command)
        {
            if (!(args.Caller.Player is IServerPlayer player)) return TextCommandResult.Error(Lang.Get("automaticclayformer:cmd-playeronly"));
            if (!playersWithCommands.Contains(player.PlayerUID)) return TextCommandResult.Error(Lang.Get("automaticclayformer:cmd-notinstalled"));

            serverChannel.SendPacket(command, player);
            return TextCommandResult.Success(); // the player's game shows the result
        }

        /// <summary>The player's game: apply a /clayformer command the server passed on, and say what changed.</summary>
        void OnSettingsCommand(SettingsCommand command)
        {
            if (command == null || capi == null) return;

            string message;
            switch (command.Action)
            {
                case SettingsCommand.On:
                    Config.Enabled = true;
                    SaveConfig();
                    message = Lang.Get("automaticclayformer:cmd-on");
                    break;

                case SettingsCommand.Off:
                    Config.Enabled = false;
                    SaveConfig();
                    message = Lang.Get("automaticclayformer:cmd-off");
                    break;

                case SettingsCommand.Range:
                    Config.Range = command.Value;
                    SaveConfig();
                    message = Lang.Get("automaticclayformer:cmd-range", Format(Config.Range), Format(Reach()));
                    break;

                case SettingsCommand.Speed:
                    Config.ActionsPerSecond = command.Value;
                    SaveConfig();
                    message = Lang.Get("automaticclayformer:cmd-speed", Format(Config.ActionsPerSecond));
                    break;

                case SettingsCommand.Messages:
                case SettingsCommand.MessagesToggle:
                    Config.ChatMessages = command.Action == SettingsCommand.MessagesToggle ? !Config.ChatMessages : command.Flag;
                    SaveConfig();
                    message = Lang.Get(Config.ChatMessages ? "automaticclayformer:cmd-messages-on" : "automaticclayformer:cmd-messages-off");
                    break;

                default:
                    message = DescribeSettings();
                    break;
            }

            capi.ShowChatMessage(Lang.Get("automaticclayformer:prefix") + " " + message);
        }

        string DescribeSettings()
        {
            return Lang.Get("automaticclayformer:cmd-status",
                Lang.Get(Config.Enabled ? "automaticclayformer:on" : "automaticclayformer:off"),
                Format(Config.Range),
                Format(Reach()),
                Format(Config.ActionsPerSecond),
                Lang.Get(Config.ChatMessages ? "automaticclayformer:on" : "automaticclayformer:off"));
        }

        float Reach()
        {
            return capi?.World?.Player?.WorldData?.PickingRange ?? GlobalConstants.DefaultPickingRange;
        }

        static string Format(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public override void Dispose()
        {
            // Note: the old version called ((ModSystem)this).Dispose() here. That is a virtual call, so it called this
            // same method again forever and crashed the game with a stack overflow when leaving a world.
            if (capi != null)
            {
                try
                {
                    if (tickListenerId != 0) capi.World?.UnregisterGameTickListener(tickListenerId);
                }
                catch (Exception)
                {
                    // The world may already be torn down when leaving; nothing left to clean up then.
                }

                try
                {
                    if (helloListenerId != 0) capi.World?.UnregisterGameTickListener(helloListenerId);
                }
                catch (Exception)
                {
                    // as above
                }

                tickListenerId = 0;
                helloListenerId = 0;
                capi.Event.LeftWorld -= OnLeftWorld;
                former = null;
                capi = null;
            }

            if (harmony != null)
            {
                harmony.UnpatchAll(HarmonyId);
                harmony = null;
            }

            playersWithMod.Clear();
            playersWithCommands.Clear();
            if (ServerInstance == this) ServerInstance = null;

            base.Dispose();
        }
    }
}
