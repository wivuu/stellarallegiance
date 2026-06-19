using System.Net.WebSockets;

namespace SimServer.Net;

// The byte-frame seam under ClientHub. The hub speaks whole binary protocol frames (one frame ==
// one message) and no longer cares whether they ride a WebSocket (direct/LAN joins) or a WebRTC
// DataChannel (NAT'd public-lobby joins). Both transports are message-oriented and reliable+
// ordered, so the v7 protocol's TCP-like delivery assumption holds unchanged.
public interface IClientTransport
{
    // Receive one whole frame into buffer. Returns the byte count, 0 for an empty frame
    // (skip), or -1 when the transport has closed (the receive loop then exits).
    ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct);

    // Send one whole frame.
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    // Best-effort close (graceful handshake where the transport supports it).
    ValueTask CloseAsync(string reason, CancellationToken ct);
}

// WebSocket transport — the existing direct ws://host:8090/game path, unchanged in behaviour
// (single-shot receive into the caller's buffer; frames are small and never fragmented here).
public sealed class WebSocketTransport : IClientTransport
{
    private readonly WebSocket _socket;
    public WebSocketTransport(WebSocket socket) => _socket = socket;

    public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
    {
        var result = await _socket.ReceiveAsync(buffer, ct);
        return result.MessageType == WebSocketMessageType.Close ? -1 : result.Count;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        _socket.SendAsync(data, WebSocketMessageType.Binary, true, ct);

    public async ValueTask CloseAsync(string reason, CancellationToken ct)
    {
        try { await _socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, ct); }
        catch { /* socket already torn down */ }
    }
}
