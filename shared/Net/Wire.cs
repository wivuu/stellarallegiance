namespace StellarAllegiance.Shared;

// Wire-level constants shared verbatim by the server protocol writer and the Godot client
// reader. Single source of truth: server/Net/Protocol.cs and client GameNetClient alias these
// consts instead of duplicating the values.
public static class Wire
{
    // Wire-format version. Bump whenever a frame layout changes. The client checks this in the
    // Welcome handshake and refuses to play against a skewed server instead of misreading frames.
    public const byte ProtocolVersion = 23;

    // Sentinel team byte for a pilot who hasn't picked a side ("NOAT" — not on a team). It
    // travels on the wire anywhere a team byte does and never indexes a real team array.
    public const byte NoTeam = 0xFF;
}
