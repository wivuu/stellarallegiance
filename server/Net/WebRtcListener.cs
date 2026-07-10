using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using StellarAllegiance.Net;

namespace SimServer.Net;

// WebRTC transport for one connected client: wraps a SIPSorcery RTCDataChannel so ClientHub sees
// the same byte-frame seam as a WebSocket. Inbound DataChannel messages are queued for
// ReceiveAsync; SendAsync writes straight to the channel. Reliable + ordered (SIPSorcery's
// default), so the v7 protocol's TCP-like delivery assumption holds.
public sealed class WebRtcTransport : IClientTransport
{
    private readonly RTCPeerConnection _pc;
    private readonly RTCDataChannel _dc;
    private readonly Channel<byte[]> _rx = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    public WebRtcTransport(RTCPeerConnection pc, RTCDataChannel dc)
    {
        _pc = pc;
        _dc = dc;
        _dc.onmessage += (_, _, data) => _rx.Writer.TryWrite(data);
        _dc.onclose += () => _rx.Writer.TryComplete();
        _dc.onerror += _ => _rx.Writer.TryComplete();
        _pc.onconnectionstatechange += s =>
        {
            if (s is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
                _rx.Writer.TryComplete();
        };
    }

    public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            if (!await _rx.Reader.WaitToReadAsync(ct))
                return -1; // channel closed
            if (!_rx.Reader.TryRead(out var frame))
                return 0;
            int n = Math.Min(frame.Length, buffer.Length);
            Array.Copy(frame, buffer, n);
            return n;
        }
        catch (ChannelClosedException)
        {
            return -1;
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            if (_dc.readyState == RTCDataChannelState.open)
            {
                // Send the frame slice straight from the caller's pooled buffer — no copy.
                // SIPSorcery copies the bytes into its own SCTP chunks synchronously
                // (SctpDataSender.SendData -> Buffer.BlockCopy) before send() returns, so it
                // never retains our array. That's why ClientHub.SendLoop can recycle frame.Buf
                // the instant this ValueTask completes. The old data.ToArray() was a redundant
                // second copy on top of SIPSorcery's; pass the backing array + range instead.
                if (MemoryMarshal.TryGetArray(data, out var seg) && seg.Array is not null)
                    _dc.send(seg.Array, seg.Offset, seg.Count);
                else
                    _dc.send(data.ToArray());
            }
        }
        catch
        { /* channel tore down mid-send — receive loop will observe the close */
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason, CancellationToken ct)
    {
        try
        {
            _pc.close();
        }
        catch { }
        _rx.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

// The server's WebRTC side: it is the ANSWERER. Clients (offerers) post SDP offers to the public
// lobby; the lobby pushes them down the WS connection to LobbyRegistrar, which writes them into
// _offers. This listener drains that channel, builds a peer connection per offer, answers it
// (non-trickle ICE — gather candidates into the SDP before replying), and on DataChannel open
// hands the transport to the SAME ClientHub.HandleConnection the WebSocket path uses.
// Started only when the server registered a public name and is in NAT mode.
public sealed class WebRtcListener
{
    private readonly ClientHub _hub;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _shareBase; // http://host:port — still needed to POST answers
    private readonly ChannelReader<PendingOfferDto> _offers;
    private readonly List<RTCIceServer> _iceServers;
    private readonly ILogger _log;

    internal WebRtcListener(
        ClientHub hub,
        string shareBase,
        ChannelReader<PendingOfferDto> offers,
        List<RTCIceServer> iceServers,
        ILogger log
    )
    {
        _hub = hub;
        _shareBase = shareBase.TrimEnd('/');
        _offers = offers;
        _iceServers = iceServers;
        _log = log;
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => RunLoop(ct), ct);

    private async Task RunLoop(CancellationToken ct)
    {
        Log.WebRtcListenerUp(_log, _iceServers.Count);
        try
        {
            await foreach (var offer in _offers.ReadAllAsync(ct))
                _ = AnswerOffer(offer, ct); // each client handshake runs independently
        }
        catch (OperationCanceledException) { }
    }

    private async Task AnswerOffer(PendingOfferDto offer, CancellationToken ct)
    {
        try
        {
            var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = _iceServers });

            // Collect every candidate as it gathers so we can re-inject the ones SIPSorcery drops
            // from the answerer's localDescription (see WebRtcSdp / EnsureCandidatesInSdp).
            var gatheredCands = WebRtcSdp.CollectCandidates(pc);

            pc.ondatachannel += dc =>
            {
                // Build the transport NOW (not on open): it attaches onmessage immediately so a
                // Hello the client sends the instant its side opens is buffered, not dropped.
                var transport = new WebRtcTransport(pc, dc);
                int started = 0;
                void Ready()
                {
                    if (Interlocked.Exchange(ref started, 1) != 0)
                        return;
                    Log.WebRtcDataChannelOpen(_log, offer.Ticket);
                    _ = _hub.HandleConnection(transport, ct);
                }
                // By the time ondatachannel fires the channel is often ALREADY open, so a late
                // onopen never fires — handle both: subscribe AND check current state (Ready is
                // idempotent via the guard).
                dc.onopen += Ready;
                if (dc.readyState == RTCDataChannelState.open)
                    Ready();
            };
            pc.onconnectionstatechange += s =>
            {
                // Dispose on disconnected too (not just failed/closed): a client that restarts
                // leaves its server-side pc in `disconnected` for SIPSorcery's long consent-timeout,
                // and a leaked pc keeps holding its ICE/STUN sockets while the next offer comes in.
                if (
                    s
                    is RTCPeerConnectionState.failed
                        or RTCPeerConnectionState.closed
                        or RTCPeerConnectionState.disconnected
                )
                {
                    if (s == RTCPeerConnectionState.failed)
                        Log.WebRtcConnectionFailed(_log, offer.Ticket);
                    pc.Dispose();
                }
            };

            var set = pc.setRemoteDescription(
                new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer.SdpOffer }
            );
            if (set != SetDescriptionResultEnum.OK)
            {
                Log.WebRtcBadOffer(_log, set.ToString(), offer.Ticket);
                pc.Dispose();
                return;
            }

            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer);
            // The client can only reach us off-LAN via our srflx, so wait for it (not just the
            // 3s cap) whenever a STUN server is configured. No ICE servers -> LAN-only fast path.
            await WebRtcSdp.WaitForIceGathering(pc, needSrflx: _iceServers.Count > 0, ct);

            // Re-inject any gathered candidate (esp. our srflx) that SIPSorcery left out of the
            // answerer's localDescription, else the answer is host-only and unroutable off-LAN.
            var answerSdp = WebRtcSdp.EnsureCandidatesInSdp(pc.localDescription.sdp.ToString(), gatheredCands.ToArray());
            using var resp = await _http.PostAsJsonAsync(
                $"{_shareBase}/connect/{offer.Ticket}/answer",
                new { sdpAnswer = answerSdp },
                ct
            );
            if (!resp.IsSuccessStatusCode)
            {
                Log.WebRtcAnswerPostFailed(_log, (int)resp.StatusCode, offer.Ticket);
                pc.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.WebRtcAnswerError(_log, offer.Ticket, e.Message);
        }
    }
}

// Shared within SimServer.Net: written by LobbyRegistrar's WS receive loop, read by WebRtcListener.
internal sealed record PendingOfferDto(string Ticket, string SdpOffer);
