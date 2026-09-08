using System.Buffers.Binary;
using System.Text;

namespace SimServer.Net;

// The MsgHello payload, both directions of the wire (parse on the server, build for tests/bots).
// v9 layout: [1][u8 secretLen][secret][u8 nameLen][name][u8 tokenLen][reconnectToken]; proto 38
// appends [u16 LE joinTokenLen][joinToken] — a lobby-issued ES256 JWT (~300-400 B, so not u8;
// public-lobby/CONTEXT.md "Join Token"). Every field is optional: a frame that runs out of bytes
// at any point leaves the remaining fields "" (an old client never sends the tail), so parsing
// never rejects — the caller decides what a missing token means for THIS server.
public readonly record struct HelloFrame(string Secret, string Name, string ReconnectToken, string JoinToken)
{
    public const int MaxJoinTokenBytes = 4096;

    public static HelloFrame Parse(ReadOnlySpan<byte> frame)
    {
        string secret = "",
            name = "",
            reconnect = "",
            join = "";
        if (frame.Length <= 1)
            return new(secret, name, reconnect, join);

        int secLen = frame[1];
        int o = 2 + secLen;
        if (frame.Length < o + 1)
            return new(secret, name, reconnect, join);
        secret = Encoding.UTF8.GetString(frame.Slice(2, secLen));

        int nameLen = frame[o];
        o += 1;
        if (frame.Length < o + nameLen)
            return new(secret, name, reconnect, join);
        name = Encoding.UTF8.GetString(frame.Slice(o, nameLen));
        o += nameLen;

        if (frame.Length < o + 1)
            return new(secret, name, reconnect, join);
        int tokLen = frame[o];
        o += 1;
        if (frame.Length < o + tokLen)
            return new(secret, name, reconnect, join);
        if (tokLen > 0)
            reconnect = Encoding.UTF8.GetString(frame.Slice(o, tokLen));
        o += tokLen;

        if (frame.Length < o + 2)
            return new(secret, name, reconnect, join);
        int joinLen = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(o, 2));
        o += 2;
        if (joinLen > 0 && joinLen <= MaxJoinTokenBytes && frame.Length >= o + joinLen)
            join = Encoding.UTF8.GetString(frame.Slice(o, joinLen));
        return new(secret, name, reconnect, join);
    }

    // Mirrors client/scripts/GameNetClient.cs SendHello (kept here for tests and simbot).
    public static byte[] Build(string secret, string name, string reconnectToken, string joinToken)
    {
        var sec = Encoding.UTF8.GetBytes(secret);
        var nm = Encoding.UTF8.GetBytes(name);
        var tok = Encoding.UTF8.GetBytes(reconnectToken);
        var join = Encoding.UTF8.GetBytes(joinToken);
        if (sec.Length > 255 || nm.Length > 255 || tok.Length > 255 || join.Length > MaxJoinTokenBytes)
            throw new ArgumentException("hello field too long");
        var f = new byte[2 + sec.Length + 1 + nm.Length + 1 + tok.Length + 2 + join.Length];
        int o = 0;
        f[o++] = Protocol.MsgHello;
        f[o++] = (byte)sec.Length;
        sec.CopyTo(f, o);
        o += sec.Length;
        f[o++] = (byte)nm.Length;
        nm.CopyTo(f, o);
        o += nm.Length;
        f[o++] = (byte)tok.Length;
        tok.CopyTo(f, o);
        o += tok.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(o, 2), (ushort)join.Length);
        o += 2;
        join.CopyTo(f, o);
        return f;
    }
}
