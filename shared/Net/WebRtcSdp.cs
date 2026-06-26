using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace StellarAllegiance.Net;

// Shared WebRTC/ICE plumbing for the non-trickle SDP handshake, compiled into BOTH the Godot
// client (offerer) and the sim server (answerer) via a <Compile Include Link> — they hit the same
// SIPSorcery 10.0.9 quirks and must serialize candidates identically for the offer/answer to interop.
//
// This file lives under shared/ but is excluded from Shared.csproj (which is dependency-free
// deterministic math); it depends on SIPSorcery, which only the client and server reference.
public static class WebRtcSdp
{
    // Subscribe to ICE gathering and accumulate every candidate as its SDP "a=candidate:" line.
    // SIPSorcery drops candidates gathered AFTER createOffer/createAnswer from localDescription
    // (the offerer loses them outright; the answerer loses its srflx on the 2nd+ peer connection),
    // so a non-trickle SDP ends up host-only and unroutable off-LAN. Pair the returned queue with
    // EnsureCandidatesInSdp to re-inject what was dropped.
    public static ConcurrentQueue<string> CollectCandidates(RTCPeerConnection pc)
    {
        var gathered = new ConcurrentQueue<string>();
        pc.onicecandidate += c => { if (c is not null) gathered.Enqueue(BuildCandidateAttr(c)); };
        return gathered;
    }

    // Non-trickle ICE: wait (bounded) for candidate gathering so the SDP is complete in one round
    // trip; fall through with whatever gathered if STUN is slow (host candidates suffice on a LAN).
    // When needSrflx (a STUN server is configured), resolve as soon as the srflx arrives — off-LAN
    // reachability hinges on it — with a longer ceiling, since a slow cellular STUN RTT can exceed
    // the LAN-tuned 3s cap and leave the SDP host-only (unroutable off-LAN).
    public static async Task WaitForIceGathering(RTCPeerConnection pc, bool needSrflx, CancellationToken ct)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(RTCIceGatheringState s) { if (s == RTCIceGatheringState.complete) tcs.TrySetResult(); }
        void OnCand(RTCIceCandidate c) { if (needSrflx && c is { type: RTCIceCandidateType.srflx }) tcs.TrySetResult(); }
        pc.onicegatheringstatechange += OnState;
        pc.onicecandidate += OnCand;
        try
        {
            // Re-check: gathering may have completed while we were wiring the handlers (OnState
            // won't fire retroactively, so without this we'd block until the timeout).
            if (pc.iceGatheringState == RTCIceGatheringState.complete) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(needSrflx ? 8 : 3));
            await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) { /* proceed with candidates gathered so far */ }
        finally { pc.onicegatheringstatechange -= OnState; pc.onicecandidate -= OnCand; }
    }

    // Insert any gathered a=candidate line not already present in the SDP, placed right after the
    // existing candidate block of the (single) data m-section so the ufrag/mid context matches.
    // Works around SIPSorcery 10.0.9 dropping late-gathered candidates from localDescription;
    // idempotent (skips duplicates, e.g. the srflx SIPSorcery reports twice).
    public static string EnsureCandidatesInSdp(string sdp, IEnumerable<string> candidateLines)
    {
        if (string.IsNullOrEmpty(sdp)) return sdp;
        var lines = sdp.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

        // Candidate values already on the wire (compare on the full line, ignore "a=" is part of it).
        var present = new HashSet<string>(
            lines.Where(l => l.StartsWith("a=candidate", StringComparison.Ordinal))
                 .Select(l => l.Trim()), StringComparer.Ordinal);

        int lastCand = lines.FindLastIndex(l => l.StartsWith("a=candidate", StringComparison.Ordinal));
        // Fall back to just after the data m-section's a=mid (or the m= line) if no host candidate landed.
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

    // Serialize an RTCIceCandidate to its SDP "a=candidate:..." attribute line deterministically
    // from its W3C properties — we don't rely on SIPSorcery's ToString() (format unverified). Shape
    // per RFC 8839: candidate:<foundation> <component> <proto> <priority> <addr> <port> typ <type>
    // [raddr <relAddr> rport <relPort>].
    private static string BuildCandidateAttr(RTCIceCandidate c)
    {
        var line = $"candidate:{c.foundation} {(int)c.component} {c.protocol.ToString().ToLowerInvariant()} " +
                   $"{c.priority} {c.address} {c.port} typ {c.type.ToString().ToLowerInvariant()}";
        if ((c.type is RTCIceCandidateType.srflx or RTCIceCandidateType.relay or RTCIceCandidateType.prflx)
            && !string.IsNullOrEmpty(c.relatedAddress))
            line += $" raddr {c.relatedAddress} rport {c.relatedPort}";
        return "a=" + line;
    }
}
