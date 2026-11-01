using ProtoBuf;

namespace AutomaticClayFormer
{
    /// <summary>
    /// Client to server: switch the tool mode of the clay in the player's hand. Sent before a forming click that
    /// needs a different tool, so the server applies the click with the same tool as the client.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class ToolModeRequest
    {
        /// <summary>
        /// Mode of the "hello" the client sends once on joining: this game has the mod (2.1.0 or newer), so it can
        /// take /clayformer settings from the server. It's a ToolModeRequest rather than a new message type so a
        /// 2.0.0 server, which doesn't know newer types, just ignores it as an invalid tool mode.
        /// </summary>
        public const int Hello = -21;

        /// <summary>
        /// Sent as well by 2.2.0 and newer: this game also knows /clayformer unmodded. Sent separately so a 2.1.0
        /// server still gets its <see cref="Hello"/> (and ignores this one as an invalid tool mode).
        /// </summary>
        public const int Hello2 = -22;

        public int X;
        public int Y;
        public int Z;
        public int Dim;
        public int Mode;
    }

    /// <summary>Server to client: something the client should react to.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class FormerStatus
    {
        /// <summary>The server refused to add clay because the player had no matching clay to pay with.</summary>
        public const int OutOfClay = 1;

        public int Code;
        public int X;
        public int Y;
        public int Z;
    }

    /// <summary>
    /// Server to client: the player typed /clayformer. The settings live in each player's own game (their
    /// ModConfig), so the server passes the command on and the player's game applies it and shows the result.
    /// Only sent to games that said hello (2.1.0 or newer), which know this message type.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SettingsCommand
    {
        public const int Show = 0;
        public const int On = 1;
        public const int Off = 2;
        public const int Range = 3;
        public const int Speed = 4;
        public const int Messages = 5;
        /// <summary>/clayformer messages with nothing after it: switch chat messages to the other way.</summary>
        public const int MessagesToggle = 6;
        /// <summary>/clayformer unmodded on|off (2.2.0): also work on servers without the mod.</summary>
        public const int Unmodded = 7;
        /// <summary>/clayformer unmodded with nothing after it.</summary>
        public const int UnmoddedToggle = 8;

        public int Action;
        public float Value;
        public bool Flag;
    }
}
