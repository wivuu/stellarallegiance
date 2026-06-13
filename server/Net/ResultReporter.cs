using System.Net.Http.Headers;
using System.Text;

namespace SimServer.Net;

// One-shot result writeback to STDB. When the native match ends, the sim server is the
// authority — but STDB still owns the durable lobby/post-match flow, so it must learn the
// winner. We POST the module's `report_match_result` reducer over STDB's HTTP API
// (POST /v1/database/{db}/call/{reducer}, body = JSON arg array, Bearer = the DB owner's
// token so RequireOwner passes). Fire-and-forget + logged: a failed writeback never stalls
// the sim thread, and the reducer is idempotent (it no-ops once the match is Ended).
//
// Config via env (all optional — absent token disables writeback, logged once):
//   STDB_HTTP   base url of the STDB host           (default http://localhost:3000)
//   STDB_DB     database name/identity              (default stellar-allegiance)
//   STDB_TOKEN  Bearer token of the DB owner identity
public sealed class ResultReporter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _baseUrl;
    private readonly string _db;
    private readonly string? _token;

    public ResultReporter()
    {
        _baseUrl = (Environment.GetEnvironmentVariable("STDB_HTTP") ?? "http://localhost:3000").TrimEnd('/');
        _db = Environment.GetEnvironmentVariable("STDB_DB") ?? "stellar-allegiance";
        _token = Environment.GetEnvironmentVariable("STDB_TOKEN");
    }

    // Fire-and-forget: the sim thread calls this on the JustEnded step and keeps stepping.
    public void ReportWinner(byte winner)
    {
        if (string.IsNullOrEmpty(_token))
        {
            Console.WriteLine($"[Result] match ended (winner team {winner}) — writeback skipped (no STDB_TOKEN)");
            return;
        }
        _ = PostAsync(winner);
    }

    private async Task PostAsync(byte winner)
    {
        try
        {
            var url = $"{_baseUrl}/v1/database/{_db}/call/report_match_result";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent($"[{winner}]", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
                Console.WriteLine($"[Result] reported winner team {winner} to STDB ({_db})");
            else
                Console.WriteLine($"[Result] writeback failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Result] writeback error: {e.Message}");
        }
    }
}
