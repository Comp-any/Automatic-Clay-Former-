using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AutomaticClayFormer
{
    /// <summary>
    /// Client side: picks the nearest clay form in range that has a recipe selected, and works it one click at a time
    /// while you hold matching clay.
    ///
    /// Each click goes through the vanilla BlockEntityClayForm.OnUseOver, exactly like clicking by hand: the client
    /// shows the change right away and sends the click to the server, which applies the same change (and charges clay,
    /// see ServerClayChargingPatch).
    /// </summary>
    public class ClientFormer
    {
        const int ScanIntervalMs = 250;
        const int StallTimeoutMs = 4000;
        const int StallCooldownMs = 10000;
        const int FinishedCooldownMs = 3000;
        const int OutOfClayPauseMs = 3000;
        const int ProblemMessageIntervalMs = 10000;
        const int StartedMessageRepeatMs = 30000;
        const int ErrorPauseMs = 5000;

        readonly ICoreClientAPI capi;
        readonly ClayFormerModSystem mod;
        readonly IClientNetworkChannel channel;

        readonly Dictionary<BlockPos, long> cooldownUntil = new Dictionary<BlockPos, long>();

        BlockPos targetPos;
        bool toolModeSynced;
        long lastTickMs = -1;
        long lastScanMs = long.MinValue / 2;
        double budget;

        int bestMismatches;
        long lastProgressMs;
        long lastActionMs = long.MinValue / 2;
        long pausedUntilMs;

        BlockPos lastAnnouncedPos;
        long lastAnnouncedMs = long.MinValue / 2;
        long lastProblemMessageMs = long.MinValue / 2;
        bool warnedNoServerSupport;
        bool loggedError;

        public ClientFormer(ICoreClientAPI capi, ClayFormerModSystem mod, IClientNetworkChannel channel)
        {
            this.capi = capi;
            this.mod = mod;
            this.channel = channel;
        }

        ClayFormerConfig Config => mod.Config;

        long Now => capi.World.ElapsedMilliseconds;

        public void OnGameTick(float dt)
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                // Never let an unexpected error break the game loop. Log it once and back off for a bit.
                if (!loggedError)
                {
                    capi.Logger.Error("[Automatic Clay Former] Unexpected error, pausing automatic forming briefly: " + e);
                    loggedError = true;
                }

                ClearTarget();
                pausedUntilMs = Now + ErrorPauseMs;
            }
        }

        /// <summary>Called when leaving a world.</summary>
        public void Reset()
        {
            ClearTarget();
            cooldownUntil.Clear();
            lastTickMs = -1;
            budget = 0;
            pausedUntilMs = 0;
            lastAnnouncedPos = null;
            warnedNoServerSupport = false;
            loggedError = false;
        }

        /// <summary>The server refused an add because there was no matching clay to pay with.</summary>
        public void OnServerStatus(FormerStatus status)
        {
            if (status == null || status.Code != FormerStatus.OutOfClay) return;

            pausedUntilMs = Now + OutOfClayPauseMs;
            budget = 0;
            ShowProblem(Lang.Get("automaticclayformer:msg-noclay"));
        }

        void Tick()
        {
            long now = Now;
            long elapsed = lastTickMs < 0 ? 0 : Math.Max(0, now - lastTickMs);
            lastTickMs = now;

            if (!Config.Enabled || now < pausedUntilMs)
            {
                budget = 0;
                return;
            }

            IClientPlayer player = capi.World.Player;
            if (player?.Entity == null) return;

            ItemSlot slot = player.InventoryManager?.ActiveHotbarSlot;
            if (slot?.Itemstack == null || !(slot.Itemstack.Collectible is ItemClay))
            {
                // Ran out of clay in the middle of forming
                if (targetPos != null && slot != null && slot.Empty)
                {
                    ShowProblem(Lang.Get("automaticclayformer:msg-noclay"));
                }

                ClearTarget();
                budget = 0;
                return;
            }

            if (!channel.Connected)
            {
                // The server doesn't have this mod, so it can't charge clay for automatic clicks. Stay off.
                budget = 0;
                if (!warnedNoServerSupport && now - lastScanMs >= ScanIntervalMs)
                {
                    lastScanMs = now;
                    if (FindNearestForm(player, slot, now) != null)
                    {
                        Chat(Lang.Get("automaticclayformer:msg-noserver"), true);
                        warnedNoServerSupport = true;
                    }
                }

                return;
            }

            BlockEntityClayForm form = GetTarget(player, slot, now);
            if (form == null)
            {
                budget = 0;
                return;
            }

            double rate = Config.ActionsPerSecond;

            // After a break (paused, out of range, swapped items), start the stall timer fresh
            long activityGapMs = 1000 + (long)(2000 / rate);
            if (now - lastActionMs > activityGapMs)
            {
                bestMismatches = int.MaxValue;
                lastProgressMs = now;
            }

            // Allow small bursts so the speed setting holds even when frames are uneven
            budget = Math.Min(budget + elapsed * rate / 1000.0, Math.Max(1.0, rate * 0.2));

            while (budget >= 1.0 && targetPos != null)
            {
                if (!DoOneClick(player, slot, form, now)) break;
                budget -= 1.0;
            }
        }

        BlockEntityClayForm GetTarget(IClientPlayer player, ItemSlot slot, long now)
        {
            if (targetPos != null)
            {
                BlockEntityClayForm current = capi.World.BlockAccessor.GetBlockEntity(targetPos) as BlockEntityClayForm;
                if (IsWorkable(current, player, slot, now)) return current;
                ClearTarget();
            }

            if (now - lastScanMs < ScanIntervalMs) return null;
            lastScanMs = now;

            BlockEntityClayForm found = FindNearestForm(player, slot, now);
            if (found == null) return null;

            targetPos = found.Pos.Copy();
            toolModeSynced = false;
            bestMismatches = int.MaxValue;
            lastProgressMs = now;

            // Don't repeat the message when walking in and out of range of the same form
            if (lastAnnouncedPos == null || !lastAnnouncedPos.Equals(targetPos) || now - lastAnnouncedMs > StartedMessageRepeatMs)
            {
                Chat(Lang.Get("automaticclayformer:msg-started", ClayFormerUtil.OutputName(found)), false);
            }

            lastAnnouncedPos = targetPos.Copy();
            lastAnnouncedMs = now;
            return found;
        }

        BlockEntityClayForm FindNearestForm(IClientPlayer player, ItemSlot slot, long now)
        {
            double range = EffectiveRange(player);
            Vec3d eye = EyePosition(player);
            int reach = (int)Math.Ceiling(range) + 1;

            int baseX = (int)Math.Floor(eye.X);
            int baseY = (int)Math.Floor(eye.Y);
            int baseZ = (int)Math.Floor(eye.Z);

            IBlockAccessor accessor = capi.World.BlockAccessor;
            BlockPos pos = new BlockPos(baseX, baseY, baseZ, player.Entity.Pos.Dimension);
            BlockEntityClayForm best = null;
            double bestDistance = double.MaxValue;

            for (int dx = -reach; dx <= reach; dx++)
            {
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dz = -reach; dz <= reach; dz++)
                    {
                        pos.Set(baseX + dx, baseY + dy, baseZ + dz);
                        if (pos.Y < 0) continue;

                        if (!(accessor.GetBlockEntity(pos) is BlockEntityClayForm form)) continue;
                        if (!IsWorkable(form, player, slot, now)) continue;

                        // Skip forms that already match their recipe and are just waiting for the server to finish them
                        if (FormingPlanner.FirstMismatchLayer(form.Voxels, form.SelectedRecipe.Voxels) < 0) continue;

                        double distance = DistanceTo(eye, form.Pos);
                        if (distance < bestDistance)
                        {
                            best = form;
                            bestDistance = distance;
                        }
                    }
                }
            }

            return best;
        }

        bool IsWorkable(BlockEntityClayForm form, IClientPlayer player, ItemSlot slot, long now)
        {
            if (form?.SelectedRecipe == null) return false;
            if (!FormingPlanner.HasValidShape(form.Voxels) || !FormingPlanner.HasValidShape(form.SelectedRecipe.Voxels)) return false;
            if (!ClayFormerUtil.IsMatchingClay(slot.Itemstack, form)) return false;
            if (cooldownUntil.TryGetValue(form.Pos, out long until) && now < until) return false;
            if (DistanceTo(EyePosition(player), form.Pos) > EffectiveRange(player)) return false;
            if (!capi.World.Claims.TryAccess(player, form.Pos, EnumBlockAccessFlags.Use)) return false;
            return true;
        }

        /// <summary>Does one click. Returns false when nothing was done.</summary>
        bool DoOneClick(IClientPlayer player, ItemSlot slot, BlockEntityClayForm form, long now)
        {
            bool[,,] recipe = form.SelectedRecipe?.Voxels;
            if (recipe == null)
            {
                ClearTarget();
                return false;
            }

            int mismatches = FormingPlanner.CountMismatches(form.Voxels, recipe);
            if (mismatches == 0)
            {
                // The server finishes the item once it sees the last click
                Chat(Lang.Get("automaticclayformer:msg-finished", ClayFormerUtil.OutputName(form)), false);
                SetCooldown(form.Pos, now + FinishedCooldownMs);
                ClearTarget();
                return false;
            }

            if (mismatches < bestMismatches)
            {
                bestMismatches = mismatches;
                lastProgressMs = now;
            }
            else if (now - lastProgressMs > StallTimeoutMs)
            {
                // The server keeps undoing our clicks (out of reach, no permission, ...). Don't keep hammering it.
                ShowProblem(Lang.Get("automaticclayformer:msg-stalled", StallCooldownMs / 1000));
                SetCooldown(form.Pos, now + StallCooldownMs);
                ClearTarget();
                return false;
            }

            BlockSelection blockSel = new BlockSelection { Position = form.Pos };
            int currentMode = slot.Itemstack.Collectible.GetToolMode(slot, player, blockSel);

            if (!FormingPlanner.TryPlan(form.Voxels, recipe, currentMode, out FormingAction action)) return false;

            if (action.ToolMode != currentMode || !toolModeSynced)
            {
                slot.Itemstack.Collectible.SetToolMode(slot, player, blockSel, action.ToolMode);
                channel.SendPacket(new ToolModeRequest
                {
                    X = form.Pos.X,
                    Y = form.Pos.Y,
                    Z = form.Pos.Z,
                    Dim = form.Pos.dimension,
                    Mode = action.ToolMode
                });
                toolModeSynced = true;
            }

            form.OnUseOver(player, new Vec3i(action.X, action.Y, action.Z), BlockFacing.UP, action.Remove);
            lastActionMs = now;
            return true;
        }

        void SetCooldown(BlockPos pos, long until)
        {
            cooldownUntil[pos.Copy()] = until;

            // Keep the list small
            if (cooldownUntil.Count > 64)
            {
                long now = Now;
                List<BlockPos> expired = new List<BlockPos>();
                foreach (KeyValuePair<BlockPos, long> entry in cooldownUntil)
                {
                    if (entry.Value <= now) expired.Add(entry.Key);
                }

                foreach (BlockPos key in expired) cooldownUntil.Remove(key);
            }
        }

        void ClearTarget()
        {
            targetPos = null;
            toolModeSynced = false;
        }

        double EffectiveRange(IClientPlayer player)
        {
            double range = Config.Range;
            float reach = player.WorldData?.PickingRange ?? 0f;
            if (reach > 0) range = Math.Min(range, reach);
            return range;
        }

        static Vec3d EyePosition(IClientPlayer player)
        {
            return player.Entity.Pos.XYZ.Add(player.Entity.LocalEyePos);
        }

        static double DistanceTo(Vec3d eye, BlockPos pos)
        {
            double dx = pos.X + 0.5 - eye.X;
            double dy = pos.Y + 0.5 - eye.Y;
            double dz = pos.Z + 0.5 - eye.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        void ShowProblem(string message)
        {
            long now = Now;
            if (now - lastProblemMessageMs < ProblemMessageIntervalMs) return;
            lastProblemMessageMs = now;
            Chat(message, true);
        }

        void Chat(string message, bool always)
        {
            if (!always && !Config.ChatMessages) return;
            capi.ShowChatMessage(Lang.Get("automaticclayformer:prefix") + " " + message);
        }
    }
}
