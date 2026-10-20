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
}
