namespace AutomaticClayFormer
{
    /// <summary>
    /// Server settings, saved to the server's ModConfig/AutomaticClayFormerServer.json (a different file from the
    /// players' own settings, which in single player sit in the same folder). Server admins can change them in game
    /// with /clayformer server.
    /// </summary>
    public class ClayFormerServerConfig
    {
        public const string FileName = "AutomaticClayFormerServer.json";

        /// <summary>
        /// Lets players form automatically on this server. Off: every player's game (2.3.0 or newer) stops forming
        /// automatically while on this server, whatever their own settings say.
        /// </summary>
        public bool AllowAutomaticForming = true;
    }
}
