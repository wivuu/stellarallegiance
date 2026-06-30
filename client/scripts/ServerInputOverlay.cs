using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using StellarAllegiance.Ui;
// Godot ships its own HttpClient; the lobby uses the BCL one.
using HttpClient = System.Net.Http.HttpClient;

// The first screen a player sees when the client is launched WITHOUT --host. Two ways to join:
//   1. Pick from the PUBLIC LOBBY list (the public lobby at ConnectionManager.LobbyBase) — these join
//      over WebRTC (works for NAT'd player-run servers), keyed by the server's SessionId.
//   2. Type an ip-or-hostname:port for a DIRECT WebSocket join (LAN / dev / port-forwarded).
// Either hands off to ConnectionManager, which opens the single native connection.
public partial class ServerInputOverlay : Control
{
    private static readonly Color Dim = DesignTokens.Text2;
    private static readonly Color Bad = DesignTokens.Danger;
    private static readonly Color Faint = DesignTokens.TextDim;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record ServerDto(
        string SessionId,
        string Name,
        string? PublicEndpoint,
        int Players,
        int MaxPlayers,
        string? State
    );

    private ConnectionManager _cm = null!;
    private LineEdit _name = null!;
    private LineEdit _field = null!;
    private Label _error = null!;
    private VBoxContainer _list = null!;
    private Label _listStatus = null!;

    // "A newer build exists" nudge — hidden until the startup check (UpdateChecker) finds one.
    private RichTextLabel _updateBanner = null!;
    private UpdateChecker.UpdateInfo? _update;

    // SSE state — managed from StartSse/StopSse; events are queued cross-thread, drained on main thread.
    private CancellationTokenSource? _sseCts;
    private readonly Dictionary<string, ServerDto> _serverMap = [];
    private readonly ConcurrentQueue<(string Event, string Data)> _sseQueue = [];
    private string? _sseError;

    public void Init(ConnectionManager cm)
    {
        _cm = cm;

        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this); // themes the LineEdits / list / labels below

        var bg = new ColorRect { Color = new Color(DesignTokens.Void, 0.95f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        col.AddThemeConstantOverride("separation", 12);
        center.AddChild(col);

        // Update nudge: sits at the very top, hidden until CheckUpdateAsync finds a newer release.
        _updateBanner = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _updateBanner.AddThemeFontSizeOverride("normal_font_size", 15);
        _updateBanner.MetaClicked += meta => OS.ShellOpen(meta.AsString());
        col.AddChild(_updateBanner);

        var title = Centered("STELLAR ALLEGIANCE", 38);
        title.AddThemeColorOverride("font_color", DesignTokens.TextHi);
        col.AddChild(title);

        // ---- Pilot name (persisted; pre-filled from the last run) ----
        col.AddChild(Centered("Pilot name", 16));
        _name = new LineEdit
        {
            PlaceholderText = "your callsign",
            Text = UserPrefs.PilotName,
            MaxLength = UserPrefs.MaxNameLength,
            CustomMinimumSize = new Vector2(0, 40),
        };
        _name.AddThemeFontSizeOverride("font_size", 18);
        _name.TextSubmitted += _ => Submit();
        col.AddChild(_name);
        _name.GrabFocus();

        // ---- Public server list ----
        var header = new HBoxContainer();
        col.AddChild(header);
        var listTitle = new Label { Text = "Public servers", HorizontalAlignment = HorizontalAlignment.Left };
        listTitle.AddThemeFontSizeOverride("font_size", 18);
        listTitle.AddThemeColorOverride("font_color", DesignTokens.TextHi);
        listTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(listTitle);
        var refresh = new ChamferButton { Text = "Refresh", Variant = ButtonVariant.Secondary, CustomMinimumSize = new Vector2(120, 36) };
        refresh.Pressed += Reload;
        header.AddChild(refresh);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 200) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        col.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);

        _listStatus = Centered("", 15);
        _listStatus.AddThemeColorOverride("font_color", Faint);
        col.AddChild(_listStatus);

        // ---- Manual direct address ----
        col.AddChild(Centered("Or enter an address (direct connect)", 16));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        col.AddChild(row);

        _field = new LineEdit
        {
            PlaceholderText = "ip-or-hostname:port   (e.g. localhost:8090)",
            Text = "localhost:8090",
            CustomMinimumSize = new Vector2(0, 44),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _field.AddThemeFontSizeOverride("font_size", 18);
        _field.TextSubmitted += _ => Submit();
        row.AddChild(_field);

        var connect = new ChamferButton { Text = "Connect", Variant = ButtonVariant.Primary, CustomMinimumSize = new Vector2(140, 44) };
        connect.Pressed += Submit;
        row.AddChild(connect);

        _error = Centered("", 16);
        _error.AddThemeColorOverride("font_color", Bad);
        _error.Visible = false;
        col.AddChild(_error);

        // Subscribe to the lobby SSE stream for live server list updates.
        StartSse();

        // One-shot, fire-and-forget: ask GitHub whether a newer client release is out.
        _ = CheckUpdateAsync();
    }

    public override void _ExitTree()
    {
        StopSse();
        base._ExitTree();
    }

    // Best-effort startup update check; surfaces the banner only if a newer stable release exists.
    private async Task CheckUpdateAsync()
    {
        _update = await UpdateChecker.CheckAsync();
        if (_update is not null)
            CallDeferred(nameof(ShowUpdateBanner));
    }

    private void ShowUpdateBanner()
    {
        if (_update is null)
            return;
        _updateBanner.Text =
            $"[center][color=#9fe6a0]A new version ({_update.Version}) is available — "
            + $"[url={_update.Url}]download[/url][/color][/center]";
        _updateBanner.Visible = true;
    }

    // Manual refresh (button): cancel and restart the SSE stream so the snapshot is fresh.
    private void Reload()
    {
        _listStatus.Text = "Connecting…";
        _listStatus.Visible = true;
        foreach (var child in _list.GetChildren())
            child.QueueFree();
        _serverMap.Clear();
        StartSse();
    }

    // ---- SSE subscription --------------------------------------------------

    private void StartSse()
    {
        _sseCts?.Cancel();
        _sseCts?.Dispose();
        _sseCts = new CancellationTokenSource();
        _ = SseLoopAsync(_sseCts.Token);
    }

    private void StopSse()
    {
        _sseCts?.Cancel();
        _sseCts?.Dispose();
        _sseCts = null;
    }

    // Connects to /servers/events and streams SSE events with exponential-backoff reconnect.
    private async Task SseLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{_cm.LobbyBase}/servers/events?protocol={GameNetClient.ProtocolVersion}"
                );
                req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                _sseError = null;
                backoff = TimeSpan.FromSeconds(1); // successful connect resets backoff

                await ParseSseStreamAsync(reader, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _sseError = e.Message;
                _sseQueue.Enqueue(("error", ""));
                CallDeferred(nameof(DrainSseQueue));
            }

            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    // Reads lines from an open SSE stream and dispatches events to the main thread.
    private async Task ParseSseStreamAsync(StreamReader reader, CancellationToken ct)
    {
        string eventName = "";
        var dataLines = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
                return; // stream closed

            if (line.StartsWith(':'))
                continue; // SSE comment / keepalive

            if (line.StartsWith("event:"))
                eventName = line["event:".Length..].Trim();
            else if (line.StartsWith("data:"))
                dataLines.Append(line["data:".Length..].Trim());
            else if (line.Length == 0 && dataLines.Length > 0)
            {
                // Blank line = event boundary; dispatch to main thread.
                _sseQueue.Enqueue((eventName, dataLines.ToString()));
                CallDeferred(nameof(DrainSseQueue));
                eventName = "";
                dataLines.Clear();
            }
        }
    }

    // Runs on the Godot main thread (via CallDeferred). Drains the queue and re-renders if changed.
    private void DrainSseQueue()
    {
        bool changed = false;
        while (_sseQueue.TryDequeue(out var item))
        {
            changed |= ApplySseEvent(item.Event, item.Data);
        }
        if (changed)
            RenderServers();
    }

    // Applies one SSE event to _serverMap. Returns true when the list visibly changed.
    private bool ApplySseEvent(string eventName, string data)
    {
        if (eventName == "error")
            return true;
        try
        {
            switch (eventName)
            {
                case "snapshot":
                {
                    var list = JsonSerializer.Deserialize<List<ServerDto>>(data, JsonOpts) ?? new();
                    _serverMap.Clear();
                    foreach (var s in list)
                        _serverMap[s.SessionId] = s;
                    return true;
                }
                case "registered":
                case "updated":
                {
                    var s = JsonSerializer.Deserialize<ServerDto>(data, JsonOpts);
                    if (s is not null)
                    {
                        _serverMap[s.SessionId] = s;
                        return true;
                    }
                    break;
                }
                case "removed":
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("sessionId", out var sid))
                        return _serverMap.Remove(sid.GetString() ?? "");
                    break;
                }
            }
        }
        catch
        { /* malformed event — skip */
        }
        return false;
    }

    // ---- Render ------------------------------------------------------------

    private void RenderServers()
    {
        foreach (var child in _list.GetChildren())
            child.QueueFree();

        if (_sseError is not null)
        {
            _listStatus.Text = $"Lobby unreachable ({_cm.LobbyBase})";
            _listStatus.Visible = true;
            return;
        }

        var servers = _serverMap.Values.ToList();
        if (servers.Count == 0)
        {
            _listStatus.Text = "No public servers online";
            _listStatus.Visible = true;
            return;
        }

        _listStatus.Visible = false;
        foreach (var s in servers)
        {
            bool direct = !string.IsNullOrEmpty(s.PublicEndpoint);
            int max = s.MaxPlayers > 0 ? s.MaxPlayers : 32;
            string occupancy = $"({s.Players}/{max})";
            string state = string.IsNullOrEmpty(s.State) ? "" : $"   ·   {s.State}";
            string label = $"{s.Name}   ·   {occupancy}{state}";
            var btn = new ChamferButton
            {
                Text = label,
                Variant = ButtonVariant.Secondary,
                CustomMinimumSize = new Vector2(0, 40),
                Alignment = HorizontalAlignment.Left,
            };
            string sid = s.SessionId,
                name = s.Name,
                endpoint = s.PublicEndpoint ?? "";
            btn.Pressed += () =>
            {
                CommitName();
                if (direct)
                    _cm.ConnectTo(endpoint);
                else
                    _cm.ConnectToLobby(sid, name);
            };
            _list.AddChild(btn);
        }
    }

    private void Submit()
    {
        string text = _field.Text.Trim();
        if (text.Length == 0)
        {
            _error.Text = "Enter an address like  host:port";
            _error.Visible = true;
            return;
        }
        _error.Visible = false;
        CommitName();
        _cm.ConnectTo(text);
    }

    private void CommitName()
    {
        string name = UserPrefs.Clamp(_name.Text);
        UserPrefs.SetPilotName(name);
        _cm.SetPilotName(name);
    }

    private static Label Centered(string text, int size)
    {
        UiFonts.EnsureLoaded();
        var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontOverride("font", size >= 30 ? UiFonts.SairaBold : UiFonts.Saira);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", Dim);
        return l;
    }
}
