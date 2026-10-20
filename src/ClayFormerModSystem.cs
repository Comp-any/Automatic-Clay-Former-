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

        // ---- Client side ----

        ICoreClientAPI capi;
        IClientNetworkChannel clientChannel;
        ClientFormer former;
        long tickListenerId;

        public ClayFormerConfig Config { get; private set; } = new ClayFormerConfig();

        public override void StartServerSide(ICoreServerAPI api)
        {
            ServerInstance = this;

            serverChannel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ToolModeRequest>()
                .RegisterMessageType<FormerStatus>()
                .SetMessageHandler<ToolModeRequest>(OnToolModeRequest);

            api.Event.PlayerDisconnect += player => playersWithMod.Remove(player.PlayerUID);

            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(ClayFormerModSystem).Assembly);
        }

        void OnToolModeRequest(IServerPlayer fromPlayer, ToolModeRequest request)
        {
            if (fromPlayer == null || request == null) return;
            playersWithMod.Add(fromPlayer.PlayerUID);

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
                .SetMessageHandler<FormerStatus>(status => former?.OnServerStatus(status));

            former = new ClientFormer(api, this, clientChannel);
            tickListenerId = api.World.RegisterGameTickListener(former.OnGameTick, TickIntervalMs);
            api.Event.LeftWorld += OnLeftWorld;

            RegisterCommands(api);
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

        void RegisterCommands(ICoreClientAPI api)
        {
            CommandArgumentParsers parsers = api.ChatCommands.Parsers;

            api.ChatCommands.Create("clayformer")
                .WithDescription("Automatic Clay Former: show settings, or change them with a subcommand")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(args => TextCommandResult.Success(DescribeSettings()))

                .BeginSubCommand("on")
                    .WithDescription("Turn automatic clay forming on")
                    .HandleWith(args =>
                    {
                        Config.Enabled = true;
                        SaveConfig();
                        return TextCommandResult.Success(Lang.Get("automaticclayformer:cmd-on"));
                    })
                .EndSubCommand()

                .BeginSubCommand("off")
                    .WithDescription("Turn automatic clay forming off")
                    .HandleWith(args =>
                    {
                        Config.Enabled = false;
                        SaveConfig();
                        return TextCommandResult.Success(Lang.Get("automaticclayformer:cmd-off"));
                    })
                .EndSubCommand()

                .BeginSubCommand("range")
                    .WithDescription("How far away clay forms get worked, in blocks (" + Format(ClayFormerConfig.MinRange) + " to " + Format(ClayFormerConfig.MaxRange) + ", capped at your reach)")
                    .WithArgs(parsers.Float("blocks"))
                    .HandleWith(args =>
                    {
                        Config.Range = (float)args[0];
                        SaveConfig();
                        return TextCommandResult.Success(Lang.Get("automaticclayformer:cmd-range", Format(Config.Range), Format(Reach())));
                    })
                .EndSubCommand()

                .BeginSubCommand("speed")
                    .WithDescription("Forming clicks per second (" + Format(ClayFormerConfig.MinActionsPerSecond) + " to " + Format(ClayFormerConfig.MaxActionsPerSecond) + ")")
                    .WithArgs(parsers.Float("clicksPerSecond"))
                    .HandleWith(args =>
                    {
                        Config.ActionsPerSecond = (float)args[0];
                        SaveConfig();
                        return TextCommandResult.Success(Lang.Get("automaticclayformer:cmd-speed", Format(Config.ActionsPerSecond)));
                    })
                .EndSubCommand()

                .BeginSubCommand("messages")
                    .WithDescription("Show chat messages when forming starts and finishes (on or off)")
                    .WithArgs(parsers.Bool("show", "on"))
                    .HandleWith(args =>
                    {
                        Config.ChatMessages = (bool)args[0];
                        SaveConfig();
                        return TextCommandResult.Success(Lang.Get(Config.ChatMessages ? "automaticclayformer:cmd-messages-on" : "automaticclayformer:cmd-messages-off"));
                    })
                .EndSubCommand();
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

                tickListenerId = 0;
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
            if (ServerInstance == this) ServerInstance = null;

            base.Dispose();
        }
    }
}
