using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AutomaticClayFormer.Tests
{
    /// <summary>
    /// Runs the planner against every vanilla clay forming recipe and a batch of random shapes, checking that:
    /// - every click fixes exactly the voxels the planner said it would, and never breaks a correct one
    /// - the form never ends up empty (the game would delete it)
    /// - every recipe gets finished
    /// - clay used is in line with the game's own estimate shown in the recipe picker
    /// Prints GitHub Actions annotations so results show up on the workflow run.
    /// </summary>
    public static class Program
    {
        const int MaxActions = 4096;

        class Result
        {
            public string Name;
            public int Voxels;
            public int Actions;
            public int ToolSwitches;
            public int ClayUsed;
            public int ClayEstimate;
            public string Error;
            public bool[,,] Recipe;
        }

        public static int Main(string[] args)
        {
            string gameDir = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (string.IsNullOrEmpty(gameDir))
            {
                Console.WriteLine("::error::Pass the Vintage Story folder as the first argument");
                return 2;
            }

            List<Result> results = new List<Result>();
            List<string> skipped = new List<string>();

            string recipeDir = Path.Combine(gameDir, "assets", "survival", "recipes", "clayforming");
            if (!Directory.Exists(recipeDir))
            {
                Console.WriteLine("::error::Clay forming recipes not found at " + recipeDir);
                return 2;
            }

            foreach (string file in Directory.GetFiles(recipeDir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string shortName = Path.GetRelativePath(recipeDir, file);
                JToken root;
                try
                {
                    root = JToken.Parse(File.ReadAllText(file));
                }
                catch (Exception e)
                {
                    skipped.Add(shortName + ": " + e.Message);
                    continue;
                }

                IEnumerable<JToken> recipes = root is JArray array ? array : new[] { root };
                int index = 0;
                foreach (JToken recipe in recipes)
                {
                    string name = shortName + (index > 0 ? "#" + index : "");
                    index++;

                    bool[,,] voxels;
                    try
                    {
                        string[][] pattern = ReadPattern(Get(recipe, "pattern"));
                        if (pattern == null)
                        {
                            skipped.Add(name + ": no pattern");
                            continue;
                        }

                        JToken rotateToken = Get(recipe, "rotateRecipe");
                        bool rotate = rotateToken != null && rotateToken.Type == JTokenType.Boolean && (bool)rotateToken;
                        voxels = GenVoxels(pattern, rotate);
                    }
                    catch (Exception e)
                    {
                        skipped.Add(name + ": " + e.Message);
                        continue;
                    }

                    try
                    {
                        results.Add(Run(name, voxels));
                    }
                    catch (Exception e)
                    {
                        results.Add(new Result { Name = name, Error = "crashed: " + e });
                    }
                }
            }

            int vanillaCount = results.Count;

            // On a server without the mod only the selected tool is used: with the 1x1 tool every recipe must still finish
            List<Result> singleTool = new List<Result>();
            foreach (Result r in results.ToList())
            {
                if (r.Recipe != null) singleTool.Add(Run("1x1 only: " + r.Name, r.Recipe, FormingPlanner.ToolOne));
            }

            Random random = new Random(20261020);
            for (int i = 0; i < 500; i++)
            {
                results.Add(Run("random#" + i, RandomRecipe(random)));
            }

            foreach (Result r in results)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-60} voxels {1,4}  clicks {2,4}  tool switches {3,3}  clay {4,3} (game estimate {5,3})  {6}",
                    r.Name, r.Voxels, r.Actions, r.ToolSwitches, r.ClayUsed, r.ClayEstimate, r.Error ?? "ok"));
            }

            foreach (string s in skipped) Console.WriteLine("Skipped " + s);

            foreach (Result r in singleTool)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-60} clicks {1,4}  tool switches {2,3}  {3}",
                    r.Name, r.Actions, r.ToolSwitches, r.Error ?? "ok"));
            }

            List<Result> singleToolOk = singleTool.Where(r => r.Error == null).ToList();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "::notice title=1x1 tool only::{0} of {1} vanilla recipes finished with only the 1x1 tool (servers without the mod). Clicks: avg {2:0.0}, max {3}.",
                singleToolOk.Count, singleTool.Count,
                singleToolOk.Count > 0 ? singleToolOk.Average(r => r.Actions) : 0,
                singleToolOk.Count > 0 ? singleToolOk.Max(r => r.Actions) : 0));

            List<Result> failed = results.Concat(singleTool).Where(r => r.Error != null).ToList();
            foreach (Result r in failed.Take(10))
            {
                Console.WriteLine("::error title=Planner failed::" + r.Name + ": " + r.Error);
            }

            List<Result> vanilla = results.Take(vanillaCount).Where(r => r.Error == null).ToList();
            if (vanilla.Count > 0)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "::notice title=Vanilla recipes::{0} recipes finished, {1} failed, {2} skipped. Clicks: avg {3:0.0}, max {4}. Clay: {5} matched the game's estimate, {6} used 1 less, {7} used 1 more.",
                    vanilla.Count,
                    failed.Count(r => !r.Name.StartsWith("random#", StringComparison.Ordinal)),
                    skipped.Count,
                    vanilla.Average(r => r.Actions),
                    vanilla.Max(r => r.Actions),
                    vanilla.Count(r => r.ClayUsed == r.ClayEstimate),
                    vanilla.Count(r => r.ClayUsed < r.ClayEstimate),
                    vanilla.Count(r => r.ClayUsed > r.ClayEstimate)));
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "::notice title=Random shapes::{0} of {1} random shapes finished",
                results.Skip(vanillaCount).Count(r => r.Error == null), results.Count - vanillaCount));

            if (vanillaCount == 0)
            {
                Console.WriteLine("::error::No vanilla recipes were tested");
                return 1;
            }

            return failed.Count == 0 ? 0 : 1;
        }

        static Result Run(string name, bool[,,] recipe, int onlyToolMode = -1)
        {
            Result result = new Result { Name = name, Recipe = recipe };
            int voxelCount = 0;
            foreach (bool v in recipe)
            {
                if (v) voxelCount++;
            }

            result.Voxels = voxelCount;
            // Same formula the game uses for the clay count in the recipe picker
            result.ClayEstimate = (int)Math.Ceiling(Math.Max(1, (voxelCount - 64) / 25f));

            VanillaClayForm form = new VanillaClayForm(recipe);
            int mismatches = FormingPlanner.CountMismatches(form.Voxels, recipe);

            while (true)
            {
                if (!FormingPlanner.TryPlan(form.Voxels, recipe, form.ToolMode, out FormingAction action, onlyToolMode)) break;

                if (onlyToolMode >= 0 && action.ToolMode != onlyToolMode)
                {
                    result.Error = "click " + (result.Actions + 1) + " (" + action + ") used another tool than the only one allowed";
                    break;
                }

                if (action.ToolMode != form.ToolMode)
                {
                    result.ToolSwitches++;
                    form.ToolMode = action.ToolMode;
                }

                bool[,,] before = (bool[,,])form.Voxels.Clone();
                form.Use(action.X, action.Y, action.Z, action.Remove);
                result.Actions++;

                int now = FormingPlanner.CountMismatches(form.Voxels, recipe);
                int broken = CountBroken(before, form.Voxels, recipe);

                if (broken > 0)
                {
                    result.Error = "click " + result.Actions + " (" + action + ") broke " + broken + " correct voxel(s)";
                    break;
                }

                if (mismatches - now != action.Gain)
                {
                    result.Error = "click " + result.Actions + " (" + action + ") fixed " + (mismatches - now) + " voxel(s) instead of " + action.Gain;
                    break;
                }

                if (!form.HasAnyVoxel())
                {
                    result.Error = "click " + result.Actions + " (" + action + ") emptied the form, the game would delete it";
                    break;
                }

                mismatches = now;

                if (result.Actions > MaxActions)
                {
                    result.Error = "did not finish within " + MaxActions + " clicks";
                    break;
                }
            }

            result.ClayUsed = form.ClayUsed;

            if (result.Error == null && FormingPlanner.CountMismatches(form.Voxels, recipe) != 0)
            {
                result.Error = "stopped before finishing";
            }

            // The game charges a clay when the pool is empty, so you can use at most one more than its estimate.
            if (result.Error == null && result.ClayUsed > result.ClayEstimate + 1)
            {
                result.Error = "used " + result.ClayUsed + " clay, the game estimates " + result.ClayEstimate;
            }

            return result;
        }

        /// <summary>Voxels that matched the recipe before a click and don't after it.</summary>
        static int CountBroken(bool[,,] before, bool[,,] after, bool[,,] recipe)
        {
            int broken = 0;
            for (int x = 0; x < 16; x++)
            {
                for (int y = 0; y < 16; y++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        if (before[x, y, z] == recipe[x, y, z] && after[x, y, z] != recipe[x, y, z]) broken++;
                    }
                }
            }

            return broken;
        }

        static JToken Get(JToken token, string property)
        {
            if (!(token is JObject obj)) return null;
            return obj.Properties().FirstOrDefault(p => string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        static string[][] ReadPattern(JToken token)
        {
            if (!(token is JArray layers) || layers.Count == 0) return null;
            try
            {
                return layers.Select(layer => ((JArray)layer).Select(row => row.Value<string>()).ToArray()).ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Same as LayeredVoxelRecipe.GenVoxels in the game.</summary>
        static bool[,,] GenVoxels(string[][] pattern, bool rotate)
        {
            int length = pattern[0][0].Length;
            int width = pattern[0].Length;
            int height = pattern.Length;

            if (width > 16 || height > 16 || length > 16) throw new InvalidOperationException("pattern is larger than 16");

            bool[,,] voxels = new bool[16, 16, 16];
            int startX = (16 - width) / 2;
            int startZ = (16 - length) / 2;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < length; z++)
                    {
                        bool filled = pattern[y][x][z] != '_' && pattern[y][x][z] != ' ';
                        if (rotate) voxels[z + startZ, y, x + startX] = filled;
                        else voxels[x + startX, y, z + startZ] = filled;
                    }
                }
            }

            return voxels;
        }

        /// <summary>Random stacks of discs, rings, rectangles and noise, roughly like clay items.</summary>
        static bool[,,] RandomRecipe(Random random)
        {
            bool[,,] voxels = new bool[16, 16, 16];
            int height = random.Next(1, 13);

            for (int y = 0; y < height; y++)
            {
                int kind = y > 0 ? random.Next(6) : random.Next(5);
                double cx = 7.5 + random.Next(-2, 3);
                double cz = 7.5 + random.Next(-2, 3);
                double radius = 1.5 + random.NextDouble() * 6;
                int halfW = random.Next(1, 8);
                int halfD = random.Next(1, 8);
                bool any = false;

                for (int x = 0; x < 16; x++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        double d = Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                        bool filled;
                        switch (kind)
                        {
                            case 0: filled = d <= radius; break;
                            case 1: filled = d <= radius && d > radius - 1.2; break;
                            case 2: filled = Math.Abs(x - 7.5) <= halfW && Math.Abs(z - 7.5) <= halfD; break;
                            case 3: filled = random.NextDouble() < 0.5 && Math.Abs(x - 7.5) <= halfW && Math.Abs(z - 7.5) <= halfD; break;
                            case 4: filled = (Math.Abs(x - 7.5) <= halfW && Math.Abs(z - 7.5) <= halfD) && !(Math.Abs(x - 7.5) < halfW && Math.Abs(z - 7.5) < halfD); break;
                            default: filled = voxels[x, y - 1, z]; break;
                        }

                        voxels[x, y, z] = filled;
                        any |= filled;
                    }
                }

                // The bottom layer of a real recipe is never empty
                if (y == 0 && !any) voxels[8, 0, 8] = true;
            }

            return voxels;
        }
    }
}
