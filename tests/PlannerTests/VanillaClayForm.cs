using System;

namespace AutomaticClayFormer.Tests
{
    /// <summary>
    /// The server side rules of a vanilla clay form (BlockEntityClayForm, game 1.22), plus the clay charging this mod
    /// adds on the server. Kept as close to the game code as possible so the planner is tested against real behavior.
    /// </summary>
    public class VanillaClayForm
    {
        const int Size = 16;

        public readonly bool[,,] Voxels = new bool[Size, Size, Size];
        public readonly bool[,,] Recipe;
        public int AvailableVoxels;
        public int ClayUsed;
        public int ToolMode;

        public VanillaClayForm(bool[,,] recipe)
        {
            Recipe = recipe;

            // CreateInitialWorkItem: an 8x8 square on the bottom layer
            for (int x = 4; x < 12; x++)
            {
                for (int z = 4; z < 12; z++)
                {
                    Voxels[x, 0, z] = true;
                }
            }

            // PutClay: placing the form takes one clay and gives 25 voxels
            AvailableVoxels = 25;
            ClayUsed = 1;
        }

        /// <summary>BlockEntityClayForm.OnUseOver(IPlayer, Vec3i, BlockFacing.UP, bool), with the mod's charging prefix.</summary>
        public void Use(int x, int y, int z, bool mouseBreakMode)
        {
            // ServerClayChargingPatch
            if (!mouseBreakMode && AvailableVoxels <= 0)
            {
                ClayUsed++;
                AvailableVoxels += 25;
            }

            int toolMode = ToolMode;
            int layer = NextNotMatchingRecipeLayer();

            if (toolMode == 3)
            {
                if (!mouseBreakMode) OnCopyLayer(layer);
                else toolMode = 1;
            }

            if (toolMode != 3)
            {
                if (mouseBreakMode) OnRemove(layer, x, y, z, toolMode);
                else OnAdd(layer, x, y, z, toolMode);
            }
        }

        public bool HasAnyVoxel()
        {
            foreach (bool voxel in Voxels)
            {
                if (voxel) return true;
            }

            return false;
        }

        int NextNotMatchingRecipeLayer()
        {
            for (int layer = 0; layer < Size; layer++)
            {
                for (int x = 0; x < Size; x++)
                {
                    for (int z = 0; z < Size; z++)
                    {
                        if (Voxels[x, layer, z] != Recipe[x, layer, z]) return layer;
                    }
                }
            }

            return 16;
        }

        void LayerBounds(int layer, out int x1, out int z1, out int x2, out int z2)
        {
            x1 = 8;
            z1 = 8;
            x2 = 8;
            z2 = 8;
            for (int x = 0; x < Size; x++)
            {
                for (int z = 0; z < Size; z++)
                {
                    if (Recipe[x, layer, z])
                    {
                        x1 = Math.Min(x1, x);
                        x2 = Math.Max(x2, x);
                        z1 = Math.Min(z1, z);
                        z2 = Math.Max(z2, z);
                    }
                }
            }
        }

        void OnRemove(int layer, int vx, int vy, int vz, int radius)
        {
            if (vy != layer) return;
            if (layer < 0 || layer >= 16) return;

            for (int dx = -(int)Math.Ceiling(radius / 2f); dx <= radius / 2; dx++)
            {
                int ox = vx + dx;
                if (ox < 0 || ox >= 16) continue;

                for (int dz = -(int)Math.Ceiling(radius / 2f); dz <= radius / 2; dz++)
                {
                    int oz = vz + dz;
                    if (oz >= 0 && oz < 16 && Voxels[ox, vy, oz])
                    {
                        Voxels[ox, vy, oz] = false;
                        AvailableVoxels++;
                    }
                }
            }
        }

        void OnCopyLayer(int layer)
        {
            if (layer <= 0 || layer > 15) return;

            int quantity = 4;
            for (int x = 0; x < 16; x++)
            {
                for (int z = 0; z < 16; z++)
                {
                    if (Voxels[x, layer - 1, z] && !Voxels[x, layer, z])
                    {
                        quantity--;
                        Voxels[x, layer, z] = true;
                        AvailableVoxels--;
                    }

                    if (quantity == 0) return;
                }
            }
        }

        void OnAdd(int layer, int vx, int vy, int vz, int radius)
        {
            // facing is UP, which is vertical, so vanilla goes straight to the area add
            if (vy != layer) return;
            if (layer < 0 || layer >= 16) return;

            LayerBounds(layer, out int x1, out int z1, out int x2, out int z2);

            for (int dx = -(int)Math.Ceiling(radius / 2f); dx <= radius / 2; dx++)
            {
                int ox = vx + dx;
                for (int dz = -(int)Math.Ceiling(radius / 2f); dz <= radius / 2; dz++)
                {
                    int oz = vz + dz;
                    bool inBounds = ox >= x1 && ox <= x2 && vy >= 0 && vy < 16 && oz >= z1 && oz <= z2;
                    if (inBounds && !Voxels[ox, vy, oz])
                    {
                        AvailableVoxels--;
                        Voxels[ox, vy, oz] = true;
                    }
                }
            }
        }
    }
}
