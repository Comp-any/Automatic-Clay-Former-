using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace AutomaticClayFormer
{
    /// <summary>
    /// Automatic Clay Former's checks in Vintage Doctor (its window, Ctrl+Shift+D or /doctor), over the event bus only:
    /// no reference to Vintage Doctor, so nothing changes without it (nobody pushes these events then), and it is only
    /// made when Vintage Doctor is installed on that side.
    ///
    /// On the server: whether the clay charging patch is in place (and who else patches that method), the server's
    /// on/off switch (with a fix to switch it back on), which players' games have which version, and a check of every
    /// clay forming recipe in the game, other mods' included: each is worked on a simulated form the way automatic
    /// forming would (<see cref="RecipeCheck"/>), on a background thread after the server starts.
    ///
    /// In the player's game: whether automatic forming is on here and why not (the setting, the server's switch, a
    /// server without the mod), settings worth a second look, and what went wrong this session.
    ///
    /// Plus a page of its own, "Automatic Clay Former isn't shaping my clay" (/doctor clayformer): the server's half
    /// above, and the player's game going through every reason a form isn't being worked; and a card on the doctor's
    /// item and block pages for things made by clay forming.
    ///
    /// The JSON half (what this mod's log lines mean, the two older clay formers, the config files) is in
    /// assets/automaticclayformer/config/vintagedoctor.json.
    /// </summary>
    public class DoctorBridge
    {
        public const string DoctorModId = "vintagedoctor";
        public const string Topic = "clayformer";

        const string Mod = ClayFormerModSystem.ModId;
        const string FixServerOn = "automaticclayformer:server-on";
        const string FixEnable = "automaticclayformer:enable";
        const string FixUnmoddedOff = "automaticclayformer:unmodded-off";

        /// <summary>How far the page looks for a clay form when the player isn't looking at one.</summary>
        const double NearbyFormDistance = 10;

        readonly ClayFormerModSystem mod;
        readonly ICoreAPI api;
        readonly ICoreServerAPI sapi;
        readonly ICoreClientAPI capi;
        readonly string side;

        /// <summary>The server's check of every clay forming recipe; null until it has finished.</summary>
        volatile RecipeReport recipes;
        volatile bool recipeCheckRunning;

        public DoctorBridge(ClayFormerModSystem mod, ICoreAPI api)
        {
            this.mod = mod;
            this.api = api;
            sapi = api as ICoreServerAPI;
            capi = api as ICoreClientAPI;
            side = api.Side == EnumAppSide.Server ? "server" : "client";

            api.Event.RegisterEventBusListener(OnCollect, 0.5, "vintagedoctor:collect");
            api.Event.RegisterEventBusListener(OnFix, 0.5, "vintagedoctor:fix");
            api.Event.RegisterEventBusListener(OnTopics, 0.5, "vintagedoctor:topics");
            api.Event.RegisterEventBusListener(OnPage, 0.5, "vintagedoctor:page");
            if (sapi != null)
            {
                api.Event.RegisterEventBusListener(OnAbout, 0.5, "vintagedoctor:about");
                sapi.Event.ServerRunPhase(EnumServerRunPhase.RunGame, StartRecipeCheck);
            }
        }

        // ================================================================ the doctor's events

        void OnCollect(string eventName, ref EnumHandling handling, IAttribute data)
        {
            if (!(data is ITreeAttribute tree) || tree.GetString("side") != side) return;
            string onlyMod = tree.GetString("mod");
            if (onlyMod != null && onlyMod != Mod) return;

            try
            {
                string lang = LangOf(tree);
                List<Finding> findings = sapi != null ? ServerFindings(lang, tree.GetBool("compact"), false) : ClientFindings(lang);
                ITreeAttribute results = tree.GetOrAddTreeAttribute("results");
                foreach (Finding f in findings) f.WriteTo(results.GetOrAddTreeAttribute(Mod + ":" + f.Key));
            }
            catch (Exception e)
            {
                api.Logger.Warning("[Automatic Clay Former] Vintage Doctor's check-up failed: {0}", e);
            }
        }

        void OnTopics(string eventName, ref EnumHandling handling, IAttribute data)
        {
            if (!(data is ITreeAttribute tree) || tree.GetString("side") != side) return;
            ITreeAttribute topic = tree.GetOrAddTreeAttribute("topics").GetOrAddTreeAttribute(Topic);
            topic.SetString("mod", Mod);
            topic.SetString("question", "automaticclayformer:doctor-question");
            topic.SetBool("ownHeadings", true);
        }

        void OnPage(string eventName, ref EnumHandling handling, IAttribute data)
        {
            if (!(data is ITreeAttribute tree) || tree.GetString("topic") != Topic || tree.GetString("side") != side) return;

            string lang = LangOf(tree);
            Page page = new Page();
            try
            {
                if (sapi != null) ServerPage(page, lang, tree.GetBool("compact"));
                else ClientPage(page, tree, lang);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[Automatic Clay Former] Vintage Doctor's page failed: {0}", e);
                page.Line(T(lang, "doctor-page-failed", e.Message), 4, 0);
            }

            page.WriteTo(tree);
        }

        /// <summary>A card on the doctor's pages about one item or block: whether automatic forming can make it.</summary>
        void OnAbout(string eventName, ref EnumHandling handling, IAttribute data)
        {
            if (!(data is ITreeAttribute tree) || tree.GetString("side") != side) return;
            string subject = tree.GetString("subject");
            int colon = subject?.IndexOf(':') ?? -1;
            if (colon < 0) return;
            string code = subject.Substring(colon + 1);

            try
            {
                Finding f = AboutFinding(code, LangOf(tree));
                if (f != null) f.WriteTo(tree.GetOrAddTreeAttribute("results").GetOrAddTreeAttribute(Mod + ":" + f.Key));
            }
            catch (Exception e)
            {
                api.Logger.Warning("[Automatic Clay Former] Vintage Doctor's item card failed: {0}", e);
            }
        }

        void OnFix(string eventName, ref EnumHandling handling, IAttribute data)
        {
            if (!(data is ITreeAttribute tree)) return;
            string id = tree.GetString("id");
            if (id != FixServerOn && id != FixEnable && id != FixUnmoddedOff) return;
            string lang = LangOf(tree);

            try
            {
                if (id == FixServerOn && sapi != null)
                {
                    tree.SetString("result", mod.ApplyServerSwitch(true, lang));
                }
                else if (id == FixEnable && capi != null)
                {
                    mod.SetEnabled(true);
                    tree.SetString("result", T(lang, "doctor-fix-enabled"));
                }
                else if (id == FixUnmoddedOff && capi != null)
                {
                    mod.SetWorkOnUnmoddedServers(false);
                    tree.SetString("result", T(lang, "doctor-fix-unmodded-off"));
                }
            }
            catch (Exception e)
            {
                tree.SetString("result", e.Message);
                tree.SetBool("failed", true);
            }
        }

        // ================================================================ the server's half

        List<Finding> ServerFindings(string lang, bool compact, bool forPage)
        {
            List<Finding> list = new List<Finding> { ChargingFinding(lang), SwitchFinding(lang) };
            Finding players = PlayersFinding(lang, compact);
            if (players != null) list.Add(players);
            list.AddRange(RecipeFindings(lang, forPage));
            return list;
        }

        /// <summary>The Harmony prefix that makes automatic forming cost clay. Without it, forming would be free.</summary>
        Finding ChargingFinding(string lang)
        {
            MethodBase original = AccessTools.Method(typeof(BlockEntityClayForm), nameof(BlockEntityClayForm.OnUseOver),
                new[] { typeof(IPlayer), typeof(Vec3i), typeof(BlockFacing), typeof(bool) });
            Patches patches = original == null ? null : Harmony.GetPatchInfo(original);
            bool ours = patches != null && patches.Prefixes.Any(p => p.owner == ClayFormerModSystem.HarmonyId);

            if (!ours)
            {
                Finding missing = new Finding("charging", "problem", T(lang, "doctor-charging-missing"));
                missing.Details.Add(T(lang, "doctor-charging-missing-why"));
                missing.Advice = T(lang, "doctor-charging-missing-advice");
                missing.Link = "log";
                missing.LinkArgument = Mod;
                missing.LinkLabel = T(lang, "doctor-link-log");
                return missing;
            }

            List<string> others = patches.Owners.Where(o => o != ClayFormerModSystem.HarmonyId).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToList();
            if (others.Count == 0) return new Finding("charging", "ok", T(lang, "doctor-charging-ok"));

            Finding shared = new Finding("charging", "info", T(lang, "doctor-charging-shared", others.Count));
            foreach (string owner in others) shared.Details.Add(T(lang, "doctor-charging-shared-owner", owner));
            shared.Advice = T(lang, "doctor-charging-shared-advice");
            return shared;
        }

        Finding SwitchFinding(string lang)
        {
            if (mod.AllowAutomaticForming) return new Finding("switch", "ok", T(lang, "doctor-switch-on"));

            Finding off = new Finding("switch", "info", T(lang, "doctor-switch-off"));
            off.Details.Add(T(lang, "doctor-switch-off-where"));
            off.Fix = FixServerOn;
            off.FixLabel = T(lang, "doctor-fix-server-on-label");
            return off;
        }

        /// <summary>Which of the players online have the mod in their game, and which version (from what their game said).</summary>
        Finding PlayersFinding(string lang, bool compact)
        {
            IPlayer[] online = sapi.World.AllOnlinePlayers;
            if (online == null || online.Length == 0) return null;

            int current = 0, older = 0, oldest = 0, unknown = 0;
            List<string> details = new List<string>();
            foreach (IPlayer player in online.OrderBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase))
            {
                int level = mod.PlayerVersionLevel(player.PlayerUID);
                string what;
                if (level >= 3) { current++; what = T(lang, "doctor-players-current"); }
                else if (level >= 1) { older++; what = T(lang, "doctor-players-older"); }
                else if (level == 0) { oldest++; what = T(lang, "doctor-players-oldest"); }
                else { unknown++; what = T(lang, "doctor-players-unknown"); }

                if (!compact) details.Add(player.PlayerName + ": " + what);
            }

            bool switchIgnored = !mod.AllowAutomaticForming && older + oldest > 0;
            Finding f = new Finding("players", switchIgnored ? "warning" : "info",
                T(lang, "doctor-players", online.Length, current, older + oldest, unknown));
            f.Details.AddRange(details.Take(30));
            if (switchIgnored) f.Advice = T(lang, "doctor-players-switch-ignored");
            else if (older + oldest > 0) f.Advice = T(lang, "doctor-players-update");
            return f;
        }

        IEnumerable<Finding> RecipeFindings(string lang, bool forPage)
        {
            RecipeReport report = recipes;
            if (report == null)
            {
                yield return new Finding("recipes", "info", T(lang, recipeCheckRunning ? "doctor-recipes-checking" : "doctor-recipes-not-yet"));
                yield break;
            }

            if (report.Entries.Count == 0)
            {
                yield return new Finding("recipes", "info", T(lang, "doctor-recipes-none"));
                yield break;
            }

            List<RecipeEntry> broken = report.Entries.Where(e => e.Result != null && !e.Result.Finished).ToList();
            List<RecipeEntry> finished = report.Entries.Where(e => e.Result != null && e.Result.Finished).ToList();

            if (broken.Count == 0)
            {
                Finding ok = new Finding("recipes", "ok", T(lang, "doctor-recipes-ok", report.Entries.Count));
                if (finished.Count > 0)
                {
                    RecipeEntry longest = finished.OrderByDescending(e => e.Result.Clicks).First();
                    ok.Details.Add(T(lang, "doctor-recipes-stats", Format(finished.Average(e => e.Result.Clicks)), longest.Result.Clicks, longest.Name,
                        Format(report.Seconds)));
                }

                yield return ok;
            }
            else
            {
                Finding bad = new Finding("recipes", "warning", T(lang, "doctor-recipes-broken", broken.Count, report.Entries.Count));
                foreach (RecipeEntry e in broken.Take(forPage ? 100 : 15))
                {
                    bad.Details.Add(T(lang, "doctor-recipes-broken-one", e.Name, e.Code, e.Domain,
                        e.EmptyBottom ? T(lang, "doctor-recipes-empty-bottom") : e.Result.Error));
                }

                if (broken.Count > (forPage ? 100 : 15)) bad.Details.Add(T(lang, "doctor-recipes-more", broken.Count - (forPage ? 100 : 15)));
                bad.Advice = broken.All(e => e.EmptyBottom) ? T(lang, "doctor-recipes-broken-advice-recipe") : T(lang, "doctor-recipes-broken-advice");
                yield return bad;
            }

            List<RecipeEntry> notClay = report.Entries.Where(e => !e.ClayIngredient).ToList();
            if (notClay.Count > 0)
            {
                Finding odd = new Finding("ingredients", "warning", T(lang, "doctor-recipes-not-clay", notClay.Count));
                foreach (RecipeEntry e in notClay.Take(15)) odd.Details.Add(T(lang, "doctor-recipes-not-clay-one", e.Name, e.Code, e.Domain, e.Ingredient));
                odd.Advice = T(lang, "doctor-recipes-not-clay-advice");
                yield return odd;
            }
        }

        Finding AboutFinding(string code, string lang)
        {
            RecipeReport report = recipes;
            if (report == null)
            {
                // Only say "still checking" for things clay forming can make
                bool madeByClayForming = sapi.GetClayformingRecipes().Any(r => OutputCode(r) == code);
                return madeByClayForming ? new Finding("about", "info", T(lang, recipeCheckRunning ? "doctor-recipes-checking" : "doctor-recipes-not-yet")) : null;
            }

            List<RecipeEntry> matching = report.Entries.Where(e => e.Code == code && e.Result != null).ToList();
            if (matching.Count == 0) return null;

            RecipeEntry failed = matching.FirstOrDefault(e => !e.Result.Finished);
            if (failed != null)
            {
                Finding bad = new Finding("about", "warning", T(lang, "doctor-about-broken"));
                bad.Details.Add(failed.EmptyBottom ? T(lang, "doctor-recipes-empty-bottom") : failed.Result.Error);
                bad.Advice = failed.EmptyBottom ? T(lang, "doctor-recipes-broken-advice-recipe") : T(lang, "doctor-recipes-broken-advice");
                return bad;
            }

            RecipeEntry entry = matching[0];
            Finding ok = new Finding("about", "ok", T(lang, "doctor-about-ok", entry.Result.Clicks, entry.Result.ClayUsed));
            if (!entry.ClayIngredient) ok.Details.Add(T(lang, "doctor-recipes-not-clay-one", entry.Name, entry.Code, entry.Domain, entry.Ingredient));
            ok.Link = Topic;
            ok.LinkLabel = T(lang, "doctor-link-page");
            return ok;
        }

        void ServerPage(Page page, string lang, bool compact)
        {
            page.Line(T(lang, "doctor-page-server-heading"), 1, 0);
            foreach (Finding f in ServerFindings(lang, compact, true)) page.Card(f);
            page.Line(T(lang, "doctor-page-server-note"), 3, 0);
        }

        // ---- the recipe check

        /// <summary>
        /// Once the server runs: copies what's needed from every clay forming recipe (on the main thread), then works
        /// each on a simulated form on a background thread. A few seconds for the game's own recipes.
        /// </summary>
        void StartRecipeCheck()
        {
            if (recipeCheckRunning || recipes != null) return;

            List<RecipeEntry> entries = new List<RecipeEntry>();
            try
            {
                Item[] clayItems = sapi.World.Items.Where(i => i is ItemClay).ToArray();
                foreach (ClayFormingRecipe r in sapi.GetClayformingRecipes())
                {
                    if (r == null || !r.Enabled) continue;

                    ItemStack output = r.Output?.ResolvedItemstack;
                    RecipeEntry entry = new RecipeEntry
                    {
                        Name = output?.GetName() ?? r.Name?.ToShortString() ?? "?",
                        Code = OutputCode(r) ?? "?",
                        Domain = r.Name?.Domain ?? output?.Collectible?.Code?.Domain ?? "?",
                        Voxels = r.Voxels == null ? null : (bool[,,])r.Voxels.Clone(),
                        Ingredient = r.Ingredient?.Code?.ToString() ?? "?",
                        ClayIngredient = r.Ingredient == null || clayItems.Any(i => r.Ingredient.SatisfiesAsIngredient(new ItemStack(i), false))
                    };
                    entries.Add(entry);
                }
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[Automatic Clay Former] Couldn't read the clay forming recipes for Vintage Doctor: {0}", e.Message);
                return;
            }

            recipeCheckRunning = true;
            Task.Run(() =>
            {
                DateTime start = DateTime.UtcNow;
                foreach (RecipeEntry entry in entries)
                {
                    try
                    {
                        entry.EmptyBottom = entry.Voxels != null && FormingPlanner.HasValidShape(entry.Voxels) && RecipeCheck.CountVoxels(entry.Voxels) > 0 && !HasBottomLayer(entry.Voxels);
                        entry.Result = RecipeCheck.Simulate(entry.Voxels);
                    }
                    catch (Exception e)
                    {
                        entry.Result = new RecipeSimulation { Error = "the check crashed: " + e.Message };
                    }
                }

                RecipeReport report = new RecipeReport { Entries = entries, Seconds = (DateTime.UtcNow - start).TotalSeconds };
                int broken = entries.Count(e => !e.Result.Finished);
                RecipeEntry longest = entries.Where(e => e.Result.Finished).OrderByDescending(e => e.Result.Clicks).FirstOrDefault();
                sapi.Logger.Notification("[Automatic Clay Former] Checked {0} clay forming recipes for Vintage Doctor in {1}s: {2} can't be finished automatically{3}.",
                    entries.Count, Format(report.Seconds), broken,
                    longest == null ? "" : "; the longest, " + longest.Code + ", takes " + longest.Result.Clicks + " clicks");
                recipes = report;
                recipeCheckRunning = false;
            });
        }

        static bool HasBottomLayer(bool[,,] voxels)
        {
            for (int x = 0; x < voxels.GetLength(0); x++)
            {
                for (int z = 0; z < voxels.GetLength(2); z++)
                {
                    if (voxels[x, 0, z]) return true;
                }
            }

            return false;
        }

        static string OutputCode(ClayFormingRecipe r)
        {
            return r?.Output?.ResolvedItemstack?.Collectible?.Code?.ToString();
        }

        class RecipeEntry
        {
            public string Name;
            public string Code;
            public string Domain;
            public string Ingredient;
            public bool ClayIngredient;
            public bool[,,] Voxels;
            public bool EmptyBottom;
            public RecipeSimulation Result;
        }

        class RecipeReport
        {
            public List<RecipeEntry> Entries;
            public double Seconds;
        }

        // ================================================================ the player's game

        List<Finding> ClientFindings(string lang)
        {
            List<Finding> list = new List<Finding> { ModeFinding(lang) };
            Finding settings = SettingsFinding(lang);
            if (settings != null) list.Add(settings);
            Finding recent = RecentFinding(lang);
            if (recent != null) list.Add(recent);
            return list;
        }

        /// <summary>Whether automatic forming works in this world, and if not, why.</summary>
        Finding ModeFinding(string lang)
        {
            ClayFormerConfig config = mod.Config;
            ClientFormer former = mod.Former;
            bool serverHasMod = former?.ServerHasMod ?? false;

            if (!config.Enabled)
            {
                Finding off = new Finding("mode", "info", T(lang, "doctor-mode-off"));
                off.Fix = FixEnable;
                off.FixLabel = T(lang, "doctor-fix-enable-label");
                return off;
            }

            if (!serverHasMod)
            {
                if (config.WorkOnUnmoddedServers)
                {
                    Finding free = new Finding("mode", "warning", T(lang, "doctor-mode-unmodded-free"));
                    free.Details.Add(T(lang, "doctor-mode-unmodded-free-why"));
                    free.Fix = FixUnmoddedOff;
                    free.FixLabel = T(lang, "doctor-fix-unmodded-off-label");
                    return free;
                }

                Finding none = new Finding("mode", "info", T(lang, "doctor-mode-unmodded"));
                none.Advice = T(lang, "doctor-mode-unmodded-advice");
                return none;
            }

            if (!mod.ServerAllowed)
            {
                Finding off = new Finding("mode", "info", T(lang, "doctor-mode-server-off"));
                off.Advice = T(lang, "doctor-mode-server-off-advice");
                return off;
            }

            return new Finding("mode", "ok", T(lang, "doctor-mode-on", Format(config.Range), Format(config.ActionsPerSecond)));
        }

        /// <summary>Settings worth a second look: none of them is wrong as such, so this is a note at most.</summary>
        Finding SettingsFinding(string lang)
        {
            ClayFormerConfig config = mod.Config;
            List<string> notes = new List<string>();

            float reach = mod.Reach();
            if (config.Range > reach + 0.01f) notes.Add(T(lang, "doctor-settings-range", Format(config.Range), Format(reach)));
            if (config.ActionsPerSecond > 40 && !capi.IsSinglePlayer) notes.Add(T(lang, "doctor-settings-speed", Format(config.ActionsPerSecond)));
            if (config.WorkOnUnmoddedServers && (mod.Former?.ServerHasMod ?? false)) notes.Add(T(lang, "doctor-settings-unmodded"));
            if (!config.ChatMessages) notes.Add(T(lang, "doctor-settings-messages"));

            if (notes.Count == 0) return new Finding("settings", "ok", T(lang, "doctor-settings-ok"));

            Finding f = new Finding("settings", "info", T(lang, "doctor-settings-notes", notes.Count));
            f.Details.AddRange(notes);
            f.Advice = T(lang, "doctor-settings-advice");
            return f;
        }

        /// <summary>What went wrong this session: the same messages the chat showed, counted.</summary>
        Finding RecentFinding(string lang)
        {
            ClientFormer former = mod.Former;
            if (former == null || former.Problems.Count == 0) return null;

            bool crashed = former.Problems.Any(p => p.Unexpected);
            Finding f = new Finding("recent", crashed ? "problem" : "info", T(lang, "doctor-recent", former.Problems.Sum(p => p.Count)));
            long now = former.NowMs;
            foreach (ClientFormer.SessionProblem p in former.Problems.OrderByDescending(p => p.LastMs).Take(10))
            {
                f.Details.Add(T(lang, "doctor-recent-one", p.Count, MinutesAgo(lang, now - p.LastMs), p.Text));
            }

            if (crashed)
            {
                f.Advice = T(lang, "doctor-recent-crashed-advice");
                f.Link = "log";
                f.LinkArgument = Mod;
                f.LinkLabel = T(lang, "doctor-link-log");
            }

            return f;
        }

        /// <summary>The player's half of the page: every reason a clay form isn't being worked, in the order they matter.</summary>
        void ClientPage(Page page, ITreeAttribute tree, string lang)
        {
            ClayFormerConfig config = mod.Config;
            ClientFormer former = mod.Former;
            IClientPlayer player = capi.World.Player;
            page.Line(T(lang, "doctor-page-client-heading"), 1, 0);

            if (former == null || player?.Entity == null)
            {
                page.Line(T(lang, "doctor-page-no-player"), 3, 0);
                return;
            }

            bool allGood = true;

            // 1. The player's own switch
            if (config.Enabled) page.Card("ok", T(lang, "doctor-why-enabled"));
            else
            {
                allGood = false;
                page.Card("problem", T(lang, "doctor-why-disabled"));
                page.Button(FixButton(FixEnable, T(lang, "doctor-fix-enable-label")));
            }

            // 2. The server
            if (former.ServerHasMod)
            {
                if (mod.ServerAllowed) page.Card("ok", T(lang, "doctor-why-server-ok"));
                else
                {
                    allGood = false;
                    page.Card("problem", T(lang, "doctor-mode-server-off"));
                    page.Advice(T(lang, "doctor-mode-server-off-advice"));
                }
            }
            else if (config.WorkOnUnmoddedServers)
            {
                page.Card("warning", T(lang, "doctor-mode-unmodded-free"));
                page.Detail(T(lang, "doctor-mode-unmodded-free-why"));
                page.Button(FixButton(FixUnmoddedOff, T(lang, "doctor-fix-unmodded-off-label")));
            }
            else
            {
                allGood = false;
                page.Card("problem", T(lang, "doctor-mode-unmodded"));
                page.Advice(T(lang, "doctor-mode-unmodded-advice"));
            }

            // 3. What's in the player's hand
            ItemSlot slot = player.InventoryManager?.ActiveHotbarSlot;
            ItemStack held = slot?.Itemstack;
            bool holdingClay = held?.Collectible is ItemClay;
            if (holdingClay) page.Card("ok", T(lang, "doctor-why-holding", held.GetName()));
            else
            {
                allGood = false;
                page.Card("problem", T(lang, "doctor-why-not-holding"));
                page.Advice(T(lang, "doctor-why-not-holding-advice"));
            }

            // 4. The clay form: the one looked at, or the nearest
            BlockEntityClayForm form = LookedAtForm(tree, player, out bool lookedAt) ?? former.NearestForm(player, NearbyFormDistance);
            if (form == null)
            {
                page.Card("info", T(lang, "doctor-why-no-form", Format(NearbyFormDistance)));
                page.Advice(T(lang, "doctor-why-no-form-advice"));
                Recent(page, lang, former);
                return;
            }

            double distance = former.DistanceFromEyes(player, form.Pos);
            page.Line(T(lang, lookedAt ? "doctor-why-form-looked-at" : "doctor-why-form-nearest", Format(distance)), 0, 0);

            if (form.SelectedRecipe == null)
            {
                page.Card("problem", T(lang, "doctor-why-no-recipe"));
                page.Advice(T(lang, "doctor-why-no-recipe-advice"));
                Recent(page, lang, former);
                return;
            }

            bool[,,] recipe = form.SelectedRecipe.Voxels;
            int wrong = recipe == null ? -1 : FormingPlanner.CountMismatches(form.Voxels, recipe);
            page.Card("ok", T(lang, "doctor-why-recipe", ClayFormerUtil.OutputName(form), wrong < 0 ? "?" : wrong.ToString(CultureInfo.InvariantCulture)));

            if (holdingClay)
            {
                if (ClayFormerUtil.IsMatchingClay(held, form)) page.Card("ok", T(lang, "doctor-why-clay-matches"));
                else
                {
                    allGood = false;
                    page.Card("problem", T(lang, "doctor-why-clay-differs", form.BaseMaterial?.GetName() ?? "?", held.GetName()));
                    page.Advice(T(lang, "doctor-why-clay-differs-advice"));
                }
            }

            double range = former.RangeFor(player);
            if (distance <= range) page.Card("ok", T(lang, "doctor-why-in-range", Format(distance), Format(range)));
            else
            {
                allGood = false;
                page.Card("problem", T(lang, "doctor-why-out-of-range", Format(distance), Format(range)));
                float reach = mod.Reach();
                page.Advice(config.Range < reach - 0.01f ? T(lang, "doctor-why-out-of-range-setting", Format(config.Range), Format(reach)) : T(lang, "doctor-why-out-of-range-advice"));
            }

            if (!capi.World.Claims.TryAccess(player, form.Pos, EnumBlockAccessFlags.Use))
            {
                allGood = false;
                page.Card("problem", T(lang, "doctor-why-claim"));
            }

            double cooldown = former.CooldownSeconds(form.Pos);
            if (cooldown > 0)
            {
                allGood = false;
                page.Card("info", T(lang, "doctor-why-cooldown", Format(Math.Ceiling(cooldown))));
                page.Detail(T(lang, "doctor-why-cooldown-why"));
            }

            double paused = former.PausedSeconds;
            if (paused > 0)
            {
                allGood = false;
                page.Card("info", T(lang, "doctor-why-paused", Format(Math.Ceiling(paused))));
            }

            if (recipe != null && wrong > 0 && holdingClay)
            {
                BlockSelection blockSel = new BlockSelection { Position = form.Pos };
                int tool = held.Collectible.GetToolMode(slot, player, blockSel);
                bool unmodded = !former.ServerHasMod;
                if (!FormingPlanner.TryPlan(form.Voxels, recipe, tool, out FormingAction _, unmodded ? tool : -1))
                {
                    allGood = false;
                    if (unmodded && FormingPlanner.TryPlan(form.Voxels, recipe, tool, out FormingAction _))
                    {
                        page.Card("problem", T(lang, "doctor-why-tool"));
                        page.Advice(T(lang, "msg-unmodded-tool"));
                    }
                    else
                    {
                        page.Card("problem", T(lang, "doctor-why-no-plan"));
                        page.Advice(T(lang, "doctor-why-no-plan-advice", ClayFormerUtil.OutputName(form)));
                    }
                }
            }

            page.Line(T(lang, allGood ? "doctor-why-all-good" : "doctor-why-fix-above"), allGood ? (byte)2 : (byte)3, 0);
            Recent(page, lang, former);
        }

        void Recent(Page page, string lang, ClientFormer former)
        {
            Finding recent = RecentFinding(lang);
            if (recent != null) page.Card(recent);
        }

        BlockEntityClayForm LookedAtForm(ITreeAttribute tree, IClientPlayer player, out bool lookedAt)
        {
            lookedAt = false;
            BlockPos pos = null;
            if (tree.GetBool("hasLookedAt"))
            {
                pos = new BlockPos(tree.GetInt("lookedAtX"), tree.GetInt("lookedAtY"), tree.GetInt("lookedAtZ"), tree.GetInt("lookedAtDim"));
            }
            else if (player.CurrentBlockSelection?.Position != null)
            {
                pos = player.CurrentBlockSelection.Position;
            }

            if (pos == null || !(capi.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityClayForm form)) return null;
            lookedAt = true;
            return form;
        }

        // ================================================================ helpers

        static string LangOf(ITreeAttribute tree)
        {
            string lang = tree.GetString("language");
            return string.IsNullOrEmpty(lang) ? "en" : lang;
        }

        static string T(string lang, string key, params object[] args)
        {
            return Lang.GetL(lang, "automaticclayformer:" + key, args);
        }

        static string Format(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        static string MinutesAgo(string lang, long ms)
        {
            long minutes = Math.Max(0, ms / 60000);
            return minutes < 1 ? T(lang, "doctor-just-now") : T(lang, "doctor-minutes-ago", minutes);
        }

        static string FixButton(string id, string label)
        {
            return "{{fix|" + id + "||" + Clean(label) + "}}";
        }

        /// <summary>Labels and arguments in a button can't hold the characters that write buttons.</summary>
        static string Clean(string text)
        {
            return (text ?? "").Replace("{", "(").Replace("}", ")").Replace("|", "/");
        }

        /// <summary>One card: what the doctor's check-up shows, or a card line on a page.</summary>
        class Finding
        {
            public readonly string Key;
            public readonly string Level;
            public readonly string Title;
            public readonly List<string> Details = new List<string>();
            public string Advice;
            public string Fix;
            public string FixArgument;
            public string FixLabel;
            public string Link;
            public string LinkArgument;
            public string LinkLabel;

            public Finding(string key, string level, string title)
            {
                Key = key;
                Level = level;
                Title = title;
            }

            public void WriteTo(ITreeAttribute result)
            {
                result.SetString("level", Level);
                result.SetString("title", Title);
                if (Details.Count > 0) result.SetString("details", string.Join("\n", Details));
                if (Advice != null) result.SetString("advice", Advice);
                if (Fix != null)
                {
                    result.SetString("fix", Fix);
                    result.SetString("fixArgument", FixArgument ?? "");
                    if (FixLabel != null) result.SetString("fixLabel", FixLabel);
                }

                if (Link != null)
                {
                    result.SetString("link", Link);
                    result.SetString("linkArgument", LinkArgument ?? "");
                    if (LinkLabel != null) result.SetString("linkLabel", LinkLabel);
                }
            }
        }

        /// <summary>A page for the doctor's window: lines, each with a style (0 text, 1 heading, 2 result, 3 note, 4 warning, 5 card) and an indent.</summary>
        class Page
        {
            readonly List<string> texts = new List<string>();
            readonly List<byte> styles = new List<byte>();
            readonly List<byte> indents = new List<byte>();

            public void Line(string text, byte style, byte indent)
            {
                texts.Add(text ?? "");
                styles.Add(style);
                indents.Add(indent);
            }

            public void Card(string level, string title)
            {
                Line(Mark(level) + " " + title, 5, 0);
            }

            public void Card(Finding f)
            {
                Card(f.Level, f.Title);
                foreach (string d in f.Details) Detail(d);
                if (f.Advice != null) Advice(f.Advice);
                if (f.Fix != null) Button("{{fix|" + f.Fix + "|" + Clean(f.FixArgument) + "|" + Clean(f.FixLabel ?? f.Fix) + "}}");
                if (f.Link != null) Button("{{open|" + f.Link + "|" + Clean(f.LinkArgument) + "|" + Clean(f.LinkLabel ?? f.Link) + "}}");
            }

            public void Detail(string text) => Line(text, 3, 1);

            public void Advice(string text) => Line("→ " + text, 0, 1);

            public void Button(string buttons) => Line(buttons, 0, 1);

            public void WriteTo(ITreeAttribute tree)
            {
                tree["texts"] = new StringArrayAttribute(texts.ToArray());
                tree.SetBytes("styles", styles.ToArray());
                tree.SetBytes("indents", indents.ToArray());
            }

            static string Mark(string level)
            {
                switch (level)
                {
                    case "ok": return "✓";
                    case "warning": return "⚠";
                    case "problem": return "✖";
                    default: return "•";
                }
            }
        }
    }
}
