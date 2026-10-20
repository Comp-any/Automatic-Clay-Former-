using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace AutomaticClayFormer
{
    public static class ClayFormerUtil
    {
        /// <summary>Clay gives this many voxels, same as the vanilla game.</summary>
        public const int VoxelsPerClay = 25;

        /// <summary>
        /// True if the stack is clay of the same type (blue, red, fire...) as the clay form. The vanilla game only lets
        /// you work a clay form with its own clay type.
        /// </summary>
        public static bool IsMatchingClay(ItemStack stack, BlockEntityClayForm form)
        {
            if (stack == null || !(stack.Collectible is ItemClay)) return false;

            string formType = form?.BaseMaterial?.Collectible?.Variant?["type"];
            if (formType == null) return true;

            return stack.Collectible.Variant?["type"] == formType;
        }

        public static string OutputName(BlockEntityClayForm form)
        {
            return form?.SelectedRecipe?.Output?.ResolvedItemstack?.GetName() ?? "?";
        }
    }
}
