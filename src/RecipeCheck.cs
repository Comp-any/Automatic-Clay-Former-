using System;

namespace AutomaticClayFormer
{
    /// <summary>What happened when a recipe was worked on a simulated clay form.</summary>
    public class RecipeSimulation
    {
        public bool Finished;
        public int Clicks;
        public int ToolSwitches;
        public int ClayUsed;

        /// <summary>Why it didn't finish (in English, for the log and the doctor's details); null when it did.</summary>
        public string Error;
    }

    /// <summary>
    /// Works a recipe on a simulated clay form (<see cref="ClayFormSimulator"/>) with the planner, click by click, the
    /// way automatic forming would, and says whether it finishes. The tests run it on every vanilla recipe; with
    /// Vintage Doctor installed, the server runs it on every clay forming recipe in the game, other mods' included, so
    /// a recipe automatic forming can't finish shows up there instead of as a form that never gets done.
    /// Plain C#, no game objects: safe on a background thread.
    /// </summary>
    public static class RecipeCheck
    {
        /// <summary>More than any recipe needs (a full 16x16x16 form is 4096 voxels).</summary>
        public const int MaxClicks = 4096;

        /// <param name="onlyToolMode">Use only this tool (-1: any), as on a server without the mod.</param>
        public static RecipeSimulation Simulate(bool[,,] recipe, int onlyToolMode = -1)
        {
            RecipeSimulation result = new RecipeSimulation();

            if (!FormingPlanner.HasValidShape(recipe))
            {
                result.Error = "the recipe's shape isn't 16 by 16 by 16 voxels";
                return result;
            }

            if (CountVoxels(recipe) == 0)
            {
                result.Error = "the recipe has no voxels at all";
                return result;
            }

            ClayFormSimulator form = new ClayFormSimulator(recipe);
            int mismatches = FormingPlanner.CountMismatches(form.Voxels, recipe);

            while (mismatches > 0)
            {
                if (!FormingPlanner.TryPlan(form.Voxels, recipe, form.ToolMode, out FormingAction action, onlyToolMode))
                {
                    result.Error = "the planner found no next click with " + mismatches + " voxel(s) still wrong";
                    break;
                }

                if (onlyToolMode >= 0 && action.ToolMode != onlyToolMode)
                {
                    result.Error = "click " + (result.Clicks + 1) + " (" + action + ") used another tool than the only one allowed";
                    break;
                }

                if (action.ToolMode != form.ToolMode)
                {
                    result.ToolSwitches++;
                    form.ToolMode = action.ToolMode;
                }

                bool[,,] before = (bool[,,])form.Voxels.Clone();
                form.Use(action.X, action.Y, action.Z, action.Remove);
                result.Clicks++;

                int now = FormingPlanner.CountMismatches(form.Voxels, recipe);
                int broken = CountBroken(before, form.Voxels, recipe);

                if (broken > 0)
                {
                    result.Error = "click " + result.Clicks + " (" + action + ") broke " + broken + " correct voxel(s)";
                    break;
                }

                if (mismatches - now != action.Gain)
                {
                    result.Error = "click " + result.Clicks + " (" + action + ") fixed " + (mismatches - now) + " voxel(s) instead of " + action.Gain;
                    break;
                }

                if (!form.HasAnyVoxel())
                {
                    result.Error = "click " + result.Clicks + " (" + action + ") emptied the form, which makes the game delete it";
                    break;
                }

                mismatches = now;

                if (result.Clicks > MaxClicks)
                {
                    result.Error = "did not finish within " + MaxClicks + " clicks";
                    break;
                }
            }

            result.ClayUsed = form.ClayUsed;
            result.Finished = result.Error == null && FormingPlanner.CountMismatches(form.Voxels, recipe) == 0;
            if (!result.Finished && result.Error == null) result.Error = "stopped before finishing";
            return result;
        }

        /// <summary>The game's own estimate of the clay a recipe takes, as shown in the recipe picker.</summary>
        public static int ClayEstimate(bool[,,] recipe)
        {
            return (int)Math.Ceiling(Math.Max(1, (CountVoxels(recipe) - 64) / 25f));
        }

        public static int CountVoxels(bool[,,] voxels)
        {
            int count = 0;
            foreach (bool v in voxels)
            {
                if (v) count++;
            }

            return count;
        }

        /// <summary>Voxels that matched the recipe before a click and don't after it.</summary>
        static int CountBroken(bool[,,] before, bool[,,] after, bool[,,] recipe)
        {
            int broken = 0;
            int sx = recipe.GetLength(0), sy = recipe.GetLength(1), sz = recipe.GetLength(2);
            for (int x = 0; x < sx; x++)
            {
                for (int y = 0; y < sy; y++)
                {
                    for (int z = 0; z < sz; z++)
                    {
                        if (before[x, y, z] == recipe[x, y, z] && after[x, y, z] != recipe[x, y, z]) broken++;
                    }
                }
            }

            return broken;
        }
    }
}
