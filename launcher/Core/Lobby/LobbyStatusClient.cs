using System.Net;
using System.Net.Http.Headers;

namespace StellarAllegiance.Launcher.Lobby;

// Drives the "N SERVERS · M PILOTS ONLINE" status strip: a background reconnect loop over the public
// lobby's anonymous `GET /servers/live` SSE stream (public-lobby/PublicLobby.cs). UI-free — the shell
// subscribes to Changed and marshals to its own thread.
public interface ILobbyStatus : IAsyncDisposable
{
    // Raised with a parsed snapshot on every `snapshot` event, and with null whenever there is
    // currently no live connection (just disconnected, about to retry, or the session ended) — the
    // UI's cue to hide/dim the strip. May fire on a background thread.
    event Action<LobbySnapshot?>? Changed;

    void Start();
    void Stop();
}

public sealed class LobbyStatusClient : ILobbyStatus
{
    // Bounds only the connect/response-headers phase of each attempt — the body read afterward is
    // meant to sit open for minutes, so it is bounded by the session cap instead (see RunAsync).
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    // The public stream caps at 500 concurrent connections shared with the whole public website
    // (public-lobby/PublicLobby.cs PublicStreams), so a single Start() session self-limits rather
    // than holding a slot open indefinitely; the UI is expected to dim the strip once this fires.
    private static readonly TimeSpan SessionCap = TimeSpan.FromMinutes(5);

    private readonly string _lobbyBase;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Func<double> _jitter;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public event Action<LobbySnapshot?>? Changed;

    public LobbyStatusClient(
        string lobbyBase,
        HttpMessageHandler? handler = null,
        TimeProvider? time = null,
        Func<double>? jitter = null
    )
    {
        _lobbyBase = lobbyBase.TrimEnd('/');
        // disposeHandler: false when the handler is caller-supplied (tests own it and may reuse or
        // inspect it after we're done) — only a handler we create ourselves gets disposed with us.
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // SSE is long-lived; ConnectTimeout below bounds the connect phase instead.
        _time = time ?? TimeProvider.System;
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    public void Start()
    {
        lock (_gate)
        {
            bool alreadyRunning = _cts is { IsCancellationRequested: false } && _runTask is { IsCompleted: false };
            if (alreadyRunning)
                return;
            var cts = new CancellationTokenSource();
            _cts = cts;
            _runTask = Task.Run(() => RunAsync(cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
            cts = _cts;
        cts?.Cancel(); // safe on an already-cancelled or disposed-later source; Stop() is idempotent
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        Task? runTask;
        lock (_gate)
            runTask = _runTask;
        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch
            {
                // RunAsync is written to never throw, but disposal must never fail on this regardless.
            }
        }
        _http.Dispose();
    }

    private enum StreamOutcome
    {
        Ended, // network error, non-success status, or the server closed the stream
        Throttled503, // server is at its connection cap — apply the politeness floor
        DeadlineReached, // the session cap was hit mid-read
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + SessionCap;
        int attempt = 0;
        var parser = new SseLineParser();

        while (true)
        {
            if (ct.IsCancellationRequested)
                return; // deliberate Stop()/Dispose — the caller already knows, no extra event
            if (_time.GetUtcNow() >= deadline)
                break; // session budget spent before another attempt even started

            StreamOutcome outcome;
            try
            {
                outcome = await StreamOnceAsync(
                        parser,
                        ct,
                        deadline,
                        snap =>
                        {
                            attempt = 0; // a live snapshot means the connection is healthy again
                            Changed?.Invoke(snap);
                        }
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // Stop()/Dispose fired mid-request
            }
            catch
            {
                // Network error, TLS failure, malformed stream, the 10s connect timeout tripping,
                // ... none of it may escape the loop.
                outcome = StreamOutcome.Ended;
            }

            if (ct.IsCancellationRequested)
                return;
            if (outcome == StreamOutcome.DeadlineReached)
                break; // one final "went stale" notice below, not an extra mid-loop one

            Changed?.Invoke(null); // that connection is gone — strip shows "no data" until we reconnect

            attempt++;
            var delay = LobbyBackoff.Delay(attempt, _jitter(), after503: outcome == StreamOutcome.Throttled503);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // Stop()/Dispose during the backoff wait
            }
        }

        Changed?.Invoke(null); // session cap reached: UI dims the strip as stale
    }

    // Runs one connect-and-read cycle to completion (or failure/cap). Never throws for anything
    // short of the caller's own cancellation token firing.
    private async Task<StreamOutcome> StreamOnceAsync(
        SseLineParser parser,
        CancellationToken ct,
        DateTimeOffset deadline,
        Action<LobbySnapshot> onSnapshot
    )
    {
        parser.Reset(); // a line left mid-block by the previous (failed) connection must not bleed in

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_lobbyBase}/servers/live");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            return StreamOutcome.Throttled503;
        if (!response.IsSuccessStatusCode)
            return StreamOutcome.Ended;

        // From here on only `ct` (external stop) bounds the read — the connect timeout above does
        // not apply to a stream that is supposed to sit open for minutes.
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            if (_time.GetUtcNow() >= deadline)
                return StreamOutcome.DeadlineReached;

            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                return StreamOutcome.Ended; // server closed the stream

            var evt = parser.Feed(line);
            if (evt is { Event: "snapshot" } e)
            {
                var snap = LobbySnapshot.TryParse(e.Data);
                if (snap is not null)
                    onSnapshot(snap);
            }
        }
    }
}
