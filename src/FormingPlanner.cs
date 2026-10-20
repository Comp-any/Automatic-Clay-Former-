using System;

namespace AutomaticClayFormer
{
    /// <summary>
    /// One clay forming click: which voxel to click, which tool mode to use, and whether it removes clay.
    /// </summary>
    public readonly struct FormingAction
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;
        public readonly int ToolMode;
        public readonly bool Remove;

        /// <summary>How many voxels this click fixes.</summary>
        public readonly int Gain;

        public FormingAction(int x, int y, int z, int toolMode, bool remove, int gain)
        {
            X = x;
            Y = y;
            Z = z;
            ToolMode = toolMode;
            Remove = remove;
            Gain = gain;
        }

        public override string ToString()
        {
            return (Remove ? "remove" : "add") + " mode " + ToolMode + " at " + X + "," + Y + "," + Z + " (fixes " + Gain + ")";
        }
    }

    /// <summary>
    /// Decides the next clay forming click. It has no game dependencies, so it can be tested outside the game.
    ///
    /// It follows the rules of the vanilla clay form (BlockEntityClayForm, game 1.22):
    /// - only the lowest layer that differs from the recipe can be edited
    /// - tool modes 0, 1 and 2 add or remove a 1x1, 2x2 or 3x3 area; adding only fills cells inside the
    ///   bounding box of that recipe layer
    /// - tool mode 3 ("duplicate layer") fills the first 4 empty cells, in x-then-z order, that are filled on
    ///   the layer below, no matter which voxel was clicked
    ///
    /// Every click it picks only moves voxels towards the recipe. It never adds a voxel that has to be
    /// removed again, so no clicks or clay are wasted.
    /// </summary>
    public static class FormingPlanner
    {
        public const int Size = 16;

        public const int ToolOne = 0;
        public const int ToolTwo = 1;
        public const int ToolThree = 2;
        public const int ToolCopyLayer = 3;

        const int CopyLayerQuantity = 4;

        public static bool HasValidShape(bool[,,] voxels)
        {
            return voxels != null
                && voxels.GetLength(0) == Size
                && voxels.GetLength(1) == Size
                && voxels.GetLength(2) == Size;
        }

        /// <summary>The lowest layer that differs from the recipe, or -1 if the form matches it.</summary>
        public static int FirstMismatchLayer(bool[,,] current, bool[,,] recipe)
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    for (int z = 0; z < Size; z++)
                    {
                        if (current[x, y, z] != recipe[x, y, z]) return y;
                    }
                }
            }

            return -1;
        }

        public static int CountMismatches(bool[,,] current, bool[,,] recipe)
        {
            int count = 0;
            for (int x = 0; x < Size; x++)
            {
                for (int y = 0; y < Size; y++)
                {
                    for (int z = 0; z < Size; z++)
                    {
                        if (current[x, y, z] != recipe[x, y, z]) count++;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Picks the next click. Returns false when the form already matches the recipe.
        /// </summary>
        /// <param name="preferredToolMode">The tool mode currently selected. Used to break ties so the tool isn't switched needlessly.</param>
        public static bool TryPlan(bool[,,] current, bool[,,] recipe, int preferredToolMode, out FormingAction action)
        {
            action = default;
            if (!HasValidShape(current) || !HasValidShape(recipe)) return false;

            int layer = FirstMismatchLayer(current, recipe);
            if (layer < 0) return false;

            // Remove misplaced clay first, then add what is missing. Removing first means a stale copy of the
            // form can't trick an area add into refilling a cell that was just cleared.
            // Exception: the game deletes a clay form that has no voxels left, so if none of the current
            // voxels belong to the recipe, add something first.
            bool hasCorrectVoxel = HasCorrectVoxel(current, recipe);

            if (hasCorrectVoxel && TryPlanArea(current, recipe, layer, true, preferredToolMode, out action)) return true;
            if (TryPlanAdd(current, recipe, layer, preferredToolMode, out action)) return true;
            return TryPlanArea(current, recipe, layer, true, preferredToolMode, out action);
        }

        static bool TryPlanAdd(bool[,,] current, bool[,,] recipe, int layer, int preferredToolMode, out FormingAction action)
        {
            bool found = TryPlanArea(current, recipe, layer, false, preferredToolMode, out action);

            if (TryPlanCopyLayer(current, recipe, layer, out FormingAction copy) && (!found || IsBetter(copy, action, preferredToolMode)))
            {
                action = copy;
                found = true;
            }

            return found;
        }

        static bool IsBetter(FormingAction candidate, FormingAction best, int preferredToolMode)
        {
            if (candidate.Gain != best.Gain) return candidate.Gain > best.Gain;
            return candidate.ToolMode == preferredToolMode && best.ToolMode != preferredToolMode;
        }

        static bool HasCorrectVoxel(bool[,,] current, bool[,,] recipe)
        {
            for (int x = 0; x < Size; x++)
            {
                for (int y = 0; y < Size; y++)
                {
                    for (int z = 0; z < Size; z++)
                    {
                        if (current[x, y, z] && recipe[x, y, z]) return true;
                    }
                }
            }

            return false;
        }

        /// <summary>The cells a tool mode touches, as offsets from the clicked voxel. Same formula as the game.</summary>
        static void GetToolOffsets(int toolMode, out int low, out int high)
        {
            low = -(int)Math.Ceiling(toolMode / 2f);
            high = toolMode / 2;
        }

        /// <summary>Bounding box of a recipe layer, built the same way as BlockEntityClayForm.LayerBounds.</summary>
        static void GetLayerBounds(bool[,,] recipe, int layer, out int x1, out int z1, out int x2, out int z2)
        {
            x1 = 8;
            z1 = 8;
            x2 = 8;
            z2 = 8;

            for (int x = 0; x < Size; x++)
            {
                for (int z = 0; z < Size; z++)
                {
                    if (!recipe[x, layer, z]) continue;
                    x1 = Math.Min(x1, x);
                    x2 = Math.Max(x2, x);
                    z1 = Math.Min(z1, z);
                    z2 = Math.Max(z2, z);
                }
            }
        }

        /// <summary>Finds the best 1x1, 2x2 or 3x3 add (or remove) on the layer.</summary>
        static bool TryPlanArea(bool[,,] current, bool[,,] recipe, int layer, bool remove, int preferredToolMode, out FormingAction action)
        {
            action = default;
            bool found = false;

            GetLayerBounds(recipe, layer, out int bx1, out int bz1, out int bx2, out int bz2);
            bool[,,] evaluated = new bool[ToolThree + 1, Size, Size];

            for (int x = 0; x < Size; x++)
            {
                for (int z = 0; z < Size; z++)
                {
                    bool filled = current[x, layer, z];
                    bool wanted = recipe[x, layer, z];
                    bool needsWork = remove ? (filled && !wanted) : (!filled && wanted);
                    if (!needsWork) continue;

                    for (int mode = ToolOne; mode <= ToolThree; mode++)
                    {
                        GetToolOffsets(mode, out int low, out int high);

                        // Every click position whose area covers this cell
                        for (int ax = x - high; ax <= x - low; ax++)
                        {
                            for (int az = z - high; az <= z - low; az++)
                            {
                                if (ax < 0 || ax >= Size || az < 0 || az >= Size) continue;
                                if (evaluated[mode, ax, az]) continue;
                                evaluated[mode, ax, az] = true;

                                int gain = remove
                                    ? EvaluateRemove(current, recipe, layer, ax, az, low, high)
                                    : EvaluateAdd(current, recipe, layer, ax, az, low, high, bx1, bz1, bx2, bz2);

                                if (gain <= 0) continue;

                                FormingAction candidate = new FormingAction(ax, layer, az, mode, remove, gain);
                                if (!found || IsBetter(candidate, action, preferredToolMode))
                                {
                                    action = candidate;
                                    found = true;
                                }
                            }
                        }
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// How many wanted voxels an add would place, or -1 if it would also place an unwanted one.
        /// Mirrors BlockEntityClayForm.OnAdd: only empty cells inside the layer bounds get filled.
        /// </summary>
        static int EvaluateAdd(bool[,,] current, bool[,,] recipe, int layer, int ax, int az, int low, int high, int bx1, int bz1, int bx2, int bz2)
        {
            int gain = 0;
            for (int dx = low; dx <= high; dx++)
            {
                for (int dz = low; dz <= high; dz++)
                {
                    int cx = ax + dx;
                    int cz = az + dz;
                    if (cx < bx1 || cx > bx2 || cz < bz1 || cz > bz2) continue;
                    if (current[cx, layer, cz]) continue;
                    if (!recipe[cx, layer, cz]) return -1;
                    gain++;
                }
            }

            return gain;
        }

        /// <summary>
        /// How many unwanted voxels a removal would clear, or -1 if it would also clear a wanted one.
        /// Mirrors BlockEntityClayForm.OnRemove: every filled cell in the area gets cleared.
        /// </summary>
        static int EvaluateRemove(bool[,,] current, bool[,,] recipe, int layer, int ax, int az, int low, int high)
        {
            int gain = 0;
            for (int dx = low; dx <= high; dx++)
            {
                for (int dz = low; dz <= high; dz++)
                {
                    int cx = ax + dx;
                    int cz = az + dz;
                    if (cx < 0 || cx >= Size || cz < 0 || cz >= Size) continue;
                    if (!current[cx, layer, cz]) continue;
                    if (recipe[cx, layer, cz]) return -1;
                    gain++;
                }
            }

            return gain;
        }

        /// <summary>
        /// "Duplicate layer" fills the first 4 empty cells (x-then-z order) that are filled on the layer below.
        /// Mirrors BlockEntityClayForm.OnCopyLayer. Only used when every cell it would fill is wanted.
        /// </summary>
        static bool TryPlanCopyLayer(bool[,,] current, bool[,,] recipe, int layer, out FormingAction action)
        {
            action = default;
            if (layer <= 0 || layer >= Size) return false;

            int remaining = CopyLayerQuantity;
            int gain = 0;

            for (int x = 0; x < Size && remaining > 0; x++)
            {
                for (int z = 0; z < Size && remaining > 0; z++)
                {
                    if (current[x, layer - 1, z] && !current[x, layer, z])
                    {
                        if (!recipe[x, layer, z]) return false;
                        gain++;
                        remaining--;
                    }
                }
            }

            if (gain == 0) return false;

            // The clicked voxel doesn't matter for this tool, but it has to be on the current layer.
            action = new FormingAction(Size / 2, layer, Size / 2, ToolCopyLayer, false, gain);
            return true;
        }
    }
}
