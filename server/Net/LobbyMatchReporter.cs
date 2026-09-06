using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SimServer.Backend;
using StellarAllegiance.Shared.Lobby;

namespace SimServer.Net;

// Reports matches to the public lobby (plan §1.3): POST /matches at start, POST /matches/{id}/result
// at the end, with this server's access token. Everything is SPOOLED to disk first (one JSON file per
// item, chronological names) and sent by a background worker with backoff, so a lobby outage or a
// server crash never loses a result: unsent files are re-sent on the next boot. Terminal answers:
// 2xx and 409 (already final) delete the file; 422 (plausibility) is logged and dropped; 400/403 are
// our bugs and are dropped loudly. Unlisted matches (no ListingId at start) are only logged.
public sealed class LobbyMatchReporter : IMatchResultSink, IAsyncDisposable
{
    public const string SpoolEnvVar = "SIM_REPORT_SPOOL";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http;
    readonly string _spoolDir;
    readonly ILogger _log;
    readonly TimeSpan _initialBackoff;
    readonly TimeSpan _maxBackoff;
    readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    readonly CancellationTokenSource _cts = new();
    readonly Task _worker;
    int _pending;

    // Set once the registrar exists (it is created after the sim loop); NoLobbyIdentity until then.
    public ILobbyIdentity Identity { get; set; } = NoLobbyIdentity.Instance;

    public LobbyMatchReporter(
        HttpClient http,
        string spoolDir,
        ILogger log,
        TimeSpan? initialBackoff = null,
        TimeSpan? maxBackoff = null
    )
    {
        _http = http;
        _spoolDir = spoolDir;
        _log = log;
        _initialBackoff = initialBackoff ?? TimeSpan.FromSeconds(5);
        _maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(60);
        Directory.CreateDirectory(_spoolDir);
        // Crash/offline recovery: whatever is still spooled goes first, in the order it was written.
        foreach (var file in Directory.GetFiles(_spoolDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            Enqueue(file);
        _worker = Task.Run(WorkerAsync);
    }

    public static string ResolveDefaultSpoolDir()
    {
        var env = (Environment.GetEnvironmentVariable(SpoolEnvVar) ?? "").Trim();
        if (env.Length > 0)
            return env;
        return Path.Combine(
            Path.GetDirectoryName(LobbyCredentialStore.ResolveDefaultPath()) ?? AppContext.BaseDirectory,
            "report-spool"
        );
    }

    public int Pending => Volatile.Read(ref _pending);

    public void OnMatchStarted(MatchStartInfo start)
    {
        if (start.ListingId is null)
        {
            Log.MatchNotListed(_log, start.MatchId);
            return;
        }
        Spool(new SpoolItem("start", start, null));
    }

    public void ReportResult(MatchResultInfo result)
    {
        var anonymous = result.Pilots.Where(p => p.PlayerId is null).Select(p => p.DisplayName).ToArray();
        if (result.ListingId is null)
        {
            Log.MatchResultUnlisted(
                _log,
                result.MatchId,
                result.WinnerTeam?.ToString() ?? "none",
                result.EndReason,
                result.Pilots.Length
            );
            return;
        }
        if (anonymous.Length > 0)
            Log.MatchReportHasAnonymousPilots(_log, result.MatchId, string.Join(", ", anonymous));
        Spool(new SpoolItem("result", null, result));
    }

    /// <summary>Wait until the spool is empty (or the timeout passes) — used at shutdown and by tests.</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Pending > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        return Pending == 0;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        { /* shutting down */
        }
    }

    void Spool(SpoolItem item)
    {
        var id = item.Start?.MatchId ?? item.Result!.MatchId;
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{id:N}.{item.Kind}.json";
        var path = Path.Combine(_spoolDir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(item, Json));
        File.Move(tmp, path, overwrite: true);
        Log.MatchReportSpooled(_log, item.Kind, id);
        Enqueue(path);
    }

    void Enqueue(string path)
    {
        Interlocked.Increment(ref _pending);
        _queue.Writer.TryWrite(path);
    }

    async Task WorkerAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await SendUntilTerminalAsync(path, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    Log.MatchReportDropped(_log, Path.GetFileName(path), "unexpected: " + e.Message);
                    TryDelete(path);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task SendUntilTerminalAsync(string path, CancellationToken ct)
    {
        SpoolItem? item;
        try
        {
            item = JsonSerializer.Deserialize<SpoolItem>(await File.ReadAllTextAsync(path, ct), Json);
        }
        catch (Exception e)
        {
            Log.MatchReportDropped(_log, Path.GetFileName(path), "unreadable spool file: " + e.Message);
            TryDelete(path);
            return;
        }
        if (item is null || (item.Start is null && item.Result is null))
        {
            TryDelete(path);
            return;
        }

        var backoff = _initialBackoff;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var identity = Identity;
            string? failure;
            if (!identity.IsVerified || identity.GameServerId is not { } gameServerId || identity.LobbyBase is null)
                failure = "server not authenticated with the lobby yet";
            else
            {
                var outcome = await TrySendAsync(item, identity, gameServerId, ct);
                if (outcome.Terminal)
                {
                    if (outcome.Dropped)
                        Log.MatchReportDropped(_log, Path.GetFileName(path), outcome.Detail ?? "");
                    else
                        Log.MatchReportSent(
                            _log,
                            item.Kind,
                            item.Start?.MatchId ?? item.Result!.MatchId,
                            outcome.Detail ?? ""
                        );
                    TryDelete(path);
                    return;
                }
                failure = outcome.Detail;
            }
            Log.MatchReportRetry(_log, Path.GetFileName(path), attempt, failure ?? "?", (int)backoff.TotalSeconds);
            await Task.Delay(backoff, ct);
            backoff = backoff * 2 > _maxBackoff ? _maxBackoff : backoff * 2;
        }
    }

    readonly record struct SendOutcome(bool Terminal, bool Dropped, string? Detail);

    async Task<SendOutcome> TrySendAsync(SpoolItem item, ILobbyIdentity identity, Guid gameServerId, CancellationToken ct)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var token = await identity.GetAccessTokenAsync(forceRefresh: pass == 1, ct);
            if (token is null)
                return new(false, false, "no access token");
            using var req = BuildRequest(item, identity.LobbyBase!, gameServerId);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException e)
            {
                return new(false, false, "network: " + e.Message);
            }
            using (resp)
            {
                var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct);
                switch (resp.StatusCode)
                {
                    case HttpStatusCode.OK:
                    case HttpStatusCode.Accepted:
                    case HttpStatusCode.NoContent:
                        return new(true, false, ((int)resp.StatusCode).ToString());
                    case HttpStatusCode.Conflict:
                        return new(true, false, "409 already final");
                    case HttpStatusCode.UnprocessableEntity:
                        return new(true, true, "422 rejected by the lobby: " + body);
                    case HttpStatusCode.BadRequest:
                    case HttpStatusCode.Forbidden:
                    case HttpStatusCode.NotFound:
                        return new(true, true, $"{(int)resp.StatusCode} {body}");
                    case HttpStatusCode.Unauthorized:
                        if (pass == 0)
                            continue; // refresh once, then retry
                        return new(false, false, "401 after refresh");
                    default:
                        return new(false, false, $"{(int)resp.StatusCode} {body}");
                }
            }
        }
        return new(false, false, "unauthorized");
    }

    static HttpRequestMessage BuildRequest(SpoolItem item, string lobbyBase, Guid gameServerId)
    {
        var baseUrl = lobbyBase.TrimEnd('/');
        if (item.Start is { } s)
            return new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/matches")
            {
                Content = JsonContent.Create(new MatchStartRequest(s.MatchId, s.ListingId!, s.Map, s.StartedAt)),
            };
        var r = item.Result!;
        var report = new MatchResultReport(
            r.MatchId,
            gameServerId,
            r.ListingId!,
            r.Map,
            r.StartedAt,
            r.EndedAt,
            r.WinnerTeam,
            r.EndReason,
            r.Teams,
            r.Pilots
        );
        return new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/matches/{r.MatchId}/result")
        {
            Content = JsonContent.Create(report),
        };
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
    }

    // One spooled item: exactly one of Start/Result is set.
    public sealed record SpoolItem(string Kind, MatchStartInfo? Start, MatchResultInfo? Result);
}
