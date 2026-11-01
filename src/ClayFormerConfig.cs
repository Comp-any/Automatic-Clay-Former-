using System;

namespace AutomaticClayFormer
{
    /// <summary>
    /// Client settings, saved to ModConfig/AutomaticClayFormer.json. Can also be changed in game with /clayformer
    /// </summary>
    public class ClayFormerConfig
    {
        public const string FileName = "AutomaticClayFormer.json";

        public const float MinRange = 1f;
        public const float MaxRange = 16f;
        public const float MinActionsPerSecond = 1f;
        public const float MaxActionsPerSecond = 100f;

        /// <summary>Turns automatic forming on or off.</summary>
        public bool Enabled = true;

        /// <summary>
        /// How far away (in blocks, from your eyes to the clay form) forming happens. The game only accepts clay forming
        /// within your reach (4.5 blocks in survival), so anything above that has no extra effect.
        /// </summary>
        public float Range = 4.5f;

        /// <summary>How many forming clicks per second.</summary>
        public float ActionsPerSecond = 20f;

        /// <summary>Show chat messages when forming starts and finishes. Problems are always shown.</summary>
        public bool ChatMessages = true;

        /// <summary>
        /// Also form automatically on servers that don't have the mod. Off by default: without the mod on the server
        /// nothing takes clay for automatic forming, so it's free there and may count as cheating. A warning is shown
        /// in chat the first time it works on such a server.
        /// </summary>
        public bool WorkOnUnmoddedServers = false;

        public void Clamp()
        {
            Range = Clamp(Range, MinRange, MaxRange, 4.5f);
            ActionsPerSecond = Clamp(ActionsPerSecond, MinActionsPerSecond, MaxActionsPerSecond, 20f);
        }

        static float Clamp(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
