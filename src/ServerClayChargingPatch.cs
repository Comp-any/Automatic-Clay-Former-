using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AutomaticClayFormer
{
    /// <summary>
    /// Server side: makes every add on a clay form cost clay, the same way forming by hand does.
    ///
    /// In vanilla, clay is only taken in ItemClay.OnHeldInteractStop (releasing right click on the form): when the
    /// form has no voxels left in its pool, one clay is taken from your hand and 25 voxels are added. Automatic
    /// forming calls BlockEntityClayForm.OnUseOver directly, so that step never ran and the pool just went
    /// negative. That was the "only uses one clay" bug.
    ///
    /// This runs the same rule right before the server applies an add: if the pool is empty, take one matching clay
    /// from the player's hand. With nothing to pay with, the add is refused and the client is resynced.
    /// Forming by hand is unaffected because vanilla has already refilled the pool by then.
    /// </summary>
    [HarmonyPatch(typeof(BlockEntityClayForm), nameof(BlockEntityClayForm.OnUseOver), new[] { typeof(IPlayer), typeof(Vec3i), typeof(BlockFacing), typeof(bool) })]
    public static class ServerClayChargingPatch
    {
        public static bool Prefix(BlockEntityClayForm __instance, IPlayer byPlayer, bool mouseBreakMode)
        {
            if (__instance?.Api == null || __instance.Api.Side != EnumAppSide.Server) return true;

            // Removing clay is free (it goes back into the form's pool), and without a recipe vanilla does nothing.
            if (mouseBreakMode || __instance.SelectedRecipe == null) return true;
            if (__instance.AvailableVoxels > 0) return true;

            ItemSlot slot = byPlayer?.InventoryManager?.ActiveHotbarSlot;
            if (slot != null && ClayFormerUtil.IsMatchingClay(slot.Itemstack, __instance))
            {
                slot.TakeOut(1);
                slot.MarkDirty();
                __instance.AvailableVoxels += ClayFormerUtil.VoxelsPerClay;
                return true;
            }

            // Nothing to pay with: refuse the add and send the real state back so the client undoes its preview.
            __instance.MarkDirty(true);
            ClayFormerModSystem.ServerInstance?.NotifyOutOfClay(byPlayer as IServerPlayer, __instance.Pos);
            return false;
        }
    }
}
