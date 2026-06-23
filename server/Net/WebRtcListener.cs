using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SIPSorcery.Net;

namespace SimServer.Net;

// WebRTC transport for one connected client: wraps a SIPSorcery RTCDataChannel so ClientHub sees
// the same byte-frame seam as a WebSocket. Inbound DataChannel messages are queued for
// ReceiveAsync; SendAsync writes straight to the channel. Reliable + ordered (SIPSorcery's
// default), so the v7 protocol's TCP-like delivery assumption holds.
public sealed class WebRtcTransport : IClientTransport
{
    private readonly RTCPeerConnection _pc;
    private readonly RTCDataChannel _dc;
    private readonly Channel<byte[]> _rx =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    public WebRtcTransport(RTCPeerConnection pc, RTCDataChannel dc)
    {
        _pc = pc;
        _dc = dc;
        _dc.onmessage += (_, _, data) => _rx.Writer.TryWrite(data);
        _dc.onclose += () => _rx.Writer.TryComplete();
        _dc.onerror += _ => _rx.Writer.TryComplete();
        _pc.onconnectionstatechange += s =>
        {
            if (s is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed
                  or RTCPeerConnectionState.disconnected)
                _rx.Writer.TryComplete();
        };
    }

    public async ValueTask<int> ReceiveAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            if (!await _rx.Reader.WaitToReadAsync(ct))
                return -1;   // channel closed
            if (!_rx.Reader.TryRead(out var frame))
                return 0;
            int n = Math.Min(frame.Length, buffer.Length);
            Array.Copy(frame, buffer, n);
            return n;
        }
        catch (ChannelClosedException) { return -1; }
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
        catch { /* channel tore down mid-send — receive loop will observe the close */ }
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason, CancellationToken ct)
    {
        try { _pc.close(); } catch { }
        _rx.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

// The server's WebRTC side: it is the ANSWERER. Clients (offerers) post SDP offers to the public lobby
// keyed by our SessionId; this listener long-polls /pending, builds a peer connection per offer,
// answers it (non-trickle ICE — gather candidates into the SDP before replying), and on
// DataChannel open hands the transport to the SAME ClientHub.HandleConnection the WebSocket path
// uses. Started only when the server registered a public name (see Program.cs / LobbyRegistrar).
public sealed class WebRtcListener
{
    private readonly ClientHub _hub;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _shareBase;     // http://host:port
    private readonly string _sessionId;
    private readonly List<RTCIceServer> _iceServers;

    public WebRtcListener(ClientHub hub, string shareBase, string sessionId, List<RTCIceServer> iceServers)
    {
        _hub = hub;
        _shareBase = shareBase.TrimEnd('/');
        _sessionId = sessionId;
        _iceServers = iceServers;
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => RunLoop(ct), ct);

    private async Task RunLoop(CancellationToken ct)
    {
        Console.WriteLine($"[WebRtc] signaling listener up (session {_sessionId}, {_iceServers.Count} ICE server(s))");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Long-poll the public lobby for offers addressed to us (returns promptly when one
                // lands, else after the relay's max-wait).
                var pending = await _http.GetFromJsonAsync<List<PendingOfferDto>>(
                    $"{_shareBase}/servers/{_sessionId}/pending", ct);
                if (pending is not null)
                    foreach (var offer in pending)
                        _ = AnswerOffer(offer, ct);   // each client handshake runs independently
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                Console.WriteLine($"[WebRtc] poll error: {e.Message}");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task AnswerOffer(PendingOfferDto offer, CancellationToken ct)
    {
        try
        {
            var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = _iceServers });

            // --- ICE diagnostics ------------------------------------------------------------
            // Make a failed remote join observable: log every candidate we gather (by type) and
            // every ICE/connection state transition. Read the candidate-type tallies on a failure:
            //   no "srflx"      => STUN unreachable from here; we shipped host-only candidates and
            //                      no off-LAN client can route to us.
            //   srflx but ICE  => symmetric NAT on one/both ends; STUN can't punch (would need TURN).
            //     never connects
            int hostCands = 0, srflxCands = 0, relayCands = 0, otherCands = 0;
            pc.onicecandidate += c =>
            {
                if (c is null) return;
                switch (c.type)
                {
                    case RTCIceCandidateType.host:  Interlocked.Increment(ref hostCands);  break;
                    case RTCIceCandidateType.srflx: Interlocked.Increment(ref srflxCands); break;
                    case RTCIceCandidateType.relay: Interlocked.Increment(ref relayCands); break;
                    default:                        Interlocked.Increment(ref otherCands); break;
                }
                Console.WriteLine($"[WebRtc] cand (ticket {offer.Ticket}) {c.type} {c.address}:{c.port} ({c.protocol})");
            };
            pc.oniceconnectionstatechange += s =>
                Console.WriteLine($"[WebRtc] ICE state (ticket {offer.Ticket}): {s}");
            pc.onicegatheringstatechange += s =>
            {
                if (s == RTCIceGatheringState.complete)
                    Console.WriteLine($"[WebRtc] ICE gather complete (ticket {offer.Ticket}): " +
                        $"{hostCands} host / {srflxCands} srflx / {relayCands} relay / {otherCands} other");
            };
            // --------------------------------------------------------------------------------

            pc.ondatachannel += dc =>
            {
                // Build the transport NOW (not on open): it attaches onmessage immediately so a
                // Hello the client sends the instant its side opens is buffered, not dropped.
                var transport = new WebRtcTransport(pc, dc);
                int started = 0;
                void Ready()
                {
                    if (Interlocked.Exchange(ref started, 1) != 0) return;
                    Console.WriteLine($"[WebRtc] datachannel open (ticket {offer.Ticket})");
                    _ = _hub.HandleConnection(transport, ct);
                }
                // By the time ondatachannel fires the channel is often ALREADY open, so a late
                // onopen never fires — handle both: subscribe AND check current state (Ready is
                // idempotent via the guard).
                dc.onopen += Ready;
                if (dc.readyState == RTCDataChannelState.open) Ready();
            };
            pc.onconnectionstatechange += s =>
            {
                Console.WriteLine($"[WebRtc] conn state (ticket {offer.Ticket}): {s}");
                if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                    pc.Dispose();
            };

            var set = pc.setRemoteDescription(
                new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer.SdpOffer });
            if (set != SetDescriptionResultEnum.OK)
            {
                Console.WriteLine($"[WebRtc] bad offer ({set}) for ticket {offer.Ticket}");
                pc.Dispose();
                return;
            }

            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer);
            await WaitForIceGathering(pc, ct);

            var answerSdp = pc.localDescription.sdp.ToString();
            using var resp = await _http.PostAsJsonAsync(
                $"{_shareBase}/connect/{offer.Ticket}/answer", new { sdpAnswer = answerSdp }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[WebRtc] answer post failed ({(int)resp.StatusCode}) ticket {offer.Ticket}");
                pc.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Console.WriteLine($"[WebRtc] answer error (ticket {offer.Ticket}): {e.Message}");
        }
    }

    // Non-trickle ICE: wait for candidate gathering to finish so the answer SDP is complete in a
    // single round trip. Bounded so a stuck STUN/TURN query can't hang the handshake — we then
    // reply with whatever candidates gathered (host candidates always succeed on a LAN).
    private static async Task WaitForIceGathering(RTCPeerConnection pc, CancellationToken ct)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete)
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RTCIceGatheringState s) { if (s == RTCIceGatheringState.complete) tcs.TrySetResult(); }
        pc.onicegatheringstatechange += Handler;
        try
        {
            if (pc.iceGatheringState == RTCIceGatheringState.complete)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) { /* proceed with candidates gathered so far */ }
        finally { pc.onicegatheringstatechange -= Handler; }
    }

    // the public lobby's /pending JSON shape (camelCase; web JSON defaults are case-insensitive).
    private sealed record PendingOfferDto(string Ticket, string SdpOffer);
}
