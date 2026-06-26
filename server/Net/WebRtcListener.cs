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

            // Collect every gathered a=candidate line. SIPSorcery 10.0.9 drops the srflx from the
            // answerer's localDescription on the 2nd+ peer connection (gather completes, sawSrflx=true,
            // yet the answer SDP is host-only) — same quirk the offerer hits on the client. We re-inject
            // these into the answer SDP below so the client always gets a public address to punch toward.
            var gatheredCands = new System.Collections.Concurrent.ConcurrentQueue<string>();
            pc.onicecandidate += c => { if (c is not null) gatheredCands.Enqueue(BuildCandidateAttr(c)); };

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
                // Dispose on disconnected too (not just failed/closed): a client that restarts
                // leaves its server-side pc in `disconnected` for SIPSorcery's long consent-timeout,
                // and a leaked pc keeps holding its ICE/STUN sockets while the next offer comes in.
                if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed
                      or RTCPeerConnectionState.disconnected)
                {
                    if (s == RTCPeerConnectionState.failed)
                        Console.WriteLine($"[WebRtc] connection failed (ticket {offer.Ticket})");
                    pc.Dispose();
                }
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
            // The client can only reach us off-LAN via our srflx, so wait for it (not just the
            // 3s cap) whenever a STUN server is configured. No ICE servers -> LAN-only fast path.
            await WaitForIceGathering(pc, needSrflx: _iceServers.Count > 0, ct);

            var answerSdp = pc.localDescription.sdp.ToString();
            // Re-inject any gathered candidate (esp. our srflx) that SIPSorcery left out of the
            // answerer's localDescription, else the answer is host-only and unroutable off-LAN.
            answerSdp = EnsureCandidatesInSdp(answerSdp, gatheredCands.ToArray());
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
    // When needSrflx (a STUN server is configured), resolve as soon as the srflx candidate arrives
    // — the client can only reach us off-LAN through it — with a longer ceiling, since a slow STUN
    // RTT can exceed the LAN-tuned 3s cap and leave the answer host-only (unroutable off-LAN).
    private static async Task WaitForIceGathering(RTCPeerConnection pc, bool needSrflx, CancellationToken ct)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete)
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(RTCIceGatheringState s) { if (s == RTCIceGatheringState.complete) tcs.TrySetResult(); }
        void OnCand(RTCIceCandidate c) { if (needSrflx && c is { type: RTCIceCandidateType.srflx }) tcs.TrySetResult(); }
        pc.onicegatheringstatechange += OnState;
        pc.onicecandidate += OnCand;
        try
        {
            if (pc.iceGatheringState == RTCIceGatheringState.complete)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(needSrflx ? 8 : 3));
            await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) { /* proceed with candidates gathered so far */ }
        finally { pc.onicegatheringstatechange -= OnState; pc.onicecandidate -= OnCand; }
    }

    // Serialize an RTCIceCandidate to its SDP "a=candidate:..." attribute line deterministically
    // from its W3C properties (per RFC 8839), mirroring the client offerer's serializer.
    private static string BuildCandidateAttr(RTCIceCandidate c)
    {
        var line = $"candidate:{c.foundation} {(int)c.component} {c.protocol.ToString().ToLowerInvariant()} " +
                   $"{c.priority} {c.address} {c.port} typ {c.type.ToString().ToLowerInvariant()}";
        if ((c.type is RTCIceCandidateType.srflx or RTCIceCandidateType.relay or RTCIceCandidateType.prflx)
            && !string.IsNullOrEmpty(c.relatedAddress))
            line += $" raddr {c.relatedAddress} rport {c.relatedPort}";
        return "a=" + line;
    }

    // Insert any gathered a=candidate line not already present in the SDP, after the existing
    // candidate block of the data m-section. Works around SIPSorcery 10.0.9 dropping the srflx from
    // the answerer's localDescription on subsequent peer connections; idempotent (skips duplicates).
    private static string EnsureCandidatesInSdp(string sdp, IEnumerable<string> candidateLines)
    {
        if (string.IsNullOrEmpty(sdp)) return sdp;
        var lines = sdp.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

        var present = new HashSet<string>(
            lines.Where(l => l.StartsWith("a=candidate", StringComparison.Ordinal))
                 .Select(l => l.Trim()), StringComparer.Ordinal);

        int lastCand = lines.FindLastIndex(l => l.StartsWith("a=candidate", StringComparison.Ordinal));
        if (lastCand < 0)
        {
            lastCand = lines.FindIndex(l => l.StartsWith("a=mid:", StringComparison.Ordinal));
            if (lastCand < 0) lastCand = lines.FindLastIndex(l => l.StartsWith("m=", StringComparison.Ordinal));
        }
        if (lastCand < 0) return sdp;   // shape we don't recognize — leave untouched

        foreach (var raw in candidateLines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("a=candidate", StringComparison.Ordinal)) continue;
            if (!present.Add(line)) continue;        // dup
            lines.Insert(++lastCand, line);
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    // the public lobby's /pending JSON shape (camelCase; web JSON defaults are case-insensitive).
    private sealed record PendingOfferDto(string Ticket, string SdpOffer);
}
