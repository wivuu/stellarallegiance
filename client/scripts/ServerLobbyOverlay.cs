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

// The "Server Lobby" — the first screen a player sees when the client is launched WITHOUT
// --host. A two-column server browser fed live by the public lobby's SSE stream:
//   left  — the game list (status dot, name, "hosted by" tag, pilot count)
//   right — detail for the selected server: status line + per-team pilot rosters
// Joining goes through the CONNECT TO SERVER action (or the direct-connect modal for a typed
// ip:port). Either hands off to ConnectionManager, which opens the single native connection:
// public-lobby servers join over WebRTC (NAT-friendly, keyed by SessionId) unless they
// advertise a probed PublicEndpoint for a direct WebSocket join.
public partial class ServerLobbyOverlay : Control
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Wire DTOs mirroring public-lobby/Contracts.cs (additive fields are nullable so servers
    // that predate them keep rendering with graceful fallbacks).
    private sealed record RosterDto(string Name, int Team, bool Ready, bool Flying);

    private sealed record ServerDto(
        string SessionId,
        string Name,
        string? PublicEndpoint,
        int Players,
        int MaxPlayers,
        string? State,
        string? HostedBy,
        List<RosterDto>? Roster,
        bool Protected = false
    );

    private ConnectionManager _cm = null!;
    private LineEdit _name = null!;
    private Label _online = null!;
    private VBoxContainer _list = null!;
    private Label _listStatus = null!;
    private VBoxContainer _detailBox = null!;
    private Label _hint = null!;

    // Direct-connect modal.
    private Control _modal = null!;
    private LineEdit _address = null!;
    private LineEdit _password = null!;
    private Label _modalError = null!;

    // "A newer build exists" nudge — hidden until the startup check (UpdateChecker) finds one.
    private RichTextLabel _updateBanner = null!;
    private UpdateChecker.UpdateInfo? _update;

    // Selection state. The rendered ServerDto instance is remembered so RenderServers can skip
    // rebuilding the detail panel when the selected entry didn't change (SSE events replace
    // instances, so a reference compare is exactly "did an update arrive for this server").
    private string? _selectedSessionId;
    private ServerDto? _renderedDetail;

    // SSE state — managed from StartSse/StopSse; events are queued cross-thread, drained on main thread.
    private CancellationTokenSource? _sseCts;
    private readonly Dictionary<string, ServerDto> _serverMap = [];
    private readonly ConcurrentQueue<(string Event, string Data)> _sseQueue = [];
    private string? _sseError;

    public void Init(ConnectionManager cm)
    {
        _cm = cm;

        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this);
        UiFonts.EnsureLoaded();

        // The server browser is the first screen a player sees — there's no live 3D space
        // behind it, so it sits on the animated Nebula backdrop rather than flat Void.
        var bg = new NebulaBackground();
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (string m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(m, 18);
        AddChild(margin);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        margin.AddChild(col);

        BuildTopBar(col);

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

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 14);
        col.AddChild(body);
        BuildGameList(body);
        BuildDetailPanel(body);

        _hint = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextDim);
        col.AddChild(_hint);

        BuildConnectModal();

        // Keep the header callsign field in sync when the settings dialog changes the saved
        // pilot name (unsubscribed in _ExitTree — UserPrefs.Changed is static).
        UserPrefs.Changed += OnPrefsChanged;

        // Subscribe to the lobby SSE stream for live server list updates.
        StartSse();

        // One-shot, fire-and-forget: ask GitHub whether a newer client release is out.
        _ = CheckUpdateAsync();

        RenderServers();
    }

    public override void _ExitTree()
    {
        UserPrefs.Changed -= OnPrefsChanged;
        StopSse();
        base._ExitTree();
    }

    // Refresh the callsign field from prefs — skipped while the player is typing in it (and
    // benign when the change came from our own gear handler: same text, no signal re-fires,
    // since _name has no TextChanged wiring).
    private void OnPrefsChanged()
    {
        if (!_name.HasFocus() && _name.Text != UserPrefs.PilotName)
            _name.Text = UserPrefs.PilotName;
    }

    // Esc: dismiss the direct-connect modal first; otherwise open the escape menu (CLOSE /
    // SETTINGS / QUIT) — only while this is the active screen (browsing, not connecting).
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
            return;
        if (_modal.Visible)
        {
            HideConnectModal();
            GetViewport().SetInputAsHandled();
        }
        else if (
            Visible
            && _cm.State == ConnectionManager.ConnState.AwaitingAddress
            && !EscapeMenu.Active
            && !SettingsDialog.Active
        )
        {
            EscapeMenu.Open(this, EscapeMenu.Context.Browser);
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- Layout ------------------------------------------------------------

    private void BuildTopBar(VBoxContainer col)
    {
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 14);
        col.AddChild(bar);

        // Brand mark: accent diamond + wide-tracked wordmark + the active-screen chip.
        var diamond = UiKit.MakeLabel("◆", UiKit.TextStyle.Body, DesignTokens.TeamAccent);
        bar.AddChild(diamond);
        var brand = new Label { Text = "STELLAR ALLEGIANCE" };
        brand.AddThemeFontOverride("font", UiFonts.WithGlyphSpacing(UiFonts.SairaBold, 3));
        brand.AddThemeFontSizeOverride("font_size", 16);
        brand.AddThemeColorOverride("font_color", DesignTokens.TextHi);
        bar.AddChild(brand);
        bar.AddChild(UiChips.AccentChip("LOBBY", 12, 3));

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bar.AddChild(spacer);

        bar.AddChild(UiKit.MakeLabel("PILOT ·", UiKit.TextStyle.Label, DesignTokens.Text2));
        _name = new LineEdit
        {
            PlaceholderText = "your callsign",
            Text = UserPrefs.PilotName,
            MaxLength = UserPrefs.MaxNameLength,
            CustomMinimumSize = new Vector2(180, 34),
        };
        _name.AddThemeFontOverride("font", UiFonts.Mono);
        _name.AddThemeFontSizeOverride("font_size", 15);
        bar.AddChild(_name);

        _online = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Ok);
        bar.AddChild(_online);

        var gear = UiKit.MakeButton("⚙", OpenSettings, ButtonVariant.Icon);
        gear.CustomMinimumSize = new Vector2(34, 34);
        gear.FocusMode = FocusModeEnum.None;
        bar.AddChild(gear);
    }

    // Settings gear — sync the typed callsign into prefs first (SetPilotName clamps) so the
    // dialog's PILOT tab shows what's on screen.
    private void OpenSettings()
    {
        UserPrefs.SetPilotName(_name.Text);
        SettingsDialog.Open(this);
    }

    private void BuildGameList(HBoxContainer body)
    {
        var panel = new HairlinePanel
        {
            Title = "GAMES",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.5f,
        };
        body.AddChild(panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        panel.AddChild(col);

        var header = new HBoxContainer();
        col.AddChild(header);
        var dotPad = new Control { CustomMinimumSize = new Vector2(26, 0) };
        header.AddChild(dotPad);
        var game = UiKit.MakeLabel("GAME", UiKit.TextStyle.Label, DesignTokens.TextDim);
        game.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(game);
        header.AddChild(UiKit.MakeLabel("PILOTS", UiKit.TextStyle.Label, DesignTokens.TextDim));

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        col.AddChild(scroll);
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 0);
        scroll.AddChild(_list);

        _listStatus = UiKit.MakeLabel("Connecting…", UiKit.TextStyle.Data, DesignTokens.TextDim);
        _listStatus.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_listStatus);

        var connectTo = UiKit.MakeButton("+ CONNECT TO…", ShowConnectModal, ButtonVariant.Ghost);
        connectTo.Alignment = HorizontalAlignment.Left;
        connectTo.CustomMinimumSize = new Vector2(0, 40);
        col.AddChild(connectTo);
    }

    private void BuildDetailPanel(HBoxContainer body)
    {
        var panel = new BracketPanel
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            // 0.706 vs the list's 1.5 ratio = 0.706/2.206 ≈ 32% of the body, 20% narrower than the
            // prior 1.0 ratio (40%). The knob is this ratio, not a fixed width.
            SizeFlagsStretchRatio = 0.706f,
        };
        body.AddChild(panel);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        panel.AddChild(scroll);
        _detailBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _detailBox.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_detailBox);
    }

    private void BuildConnectModal()
    {
        _modal = new Control { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _modal.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_modal);

        var dim = new ColorRect { Color = new Color(0.01f, 0.02f, 0.04f, 0.82f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        dim.MouseFilter = MouseFilterEnum.Stop;
        dim.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true })
                HideConnectModal();
        };
        _modal.AddChild(dim);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Ignore;
        _modal.AddChild(center);

        var panel = new BracketPanel { CustomMinimumSize = new Vector2(480, 0) };
        center.AddChild(panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        panel.AddChild(col);

        col.AddChild(UiKit.MakeLabel("DIRECT CONNECT", UiKit.TextStyle.Label, DesignTokens.TextDim));
        col.AddChild(UiKit.MakeLabel("CONNECT TO SERVER", UiKit.TextStyle.Title));

        col.AddChild(UiKit.MakeLabel("SERVER URL / HOSTNAME", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _address = new LineEdit
        {
            PlaceholderText = "ip-or-hostname:port   (e.g. localhost:8090)",
            Text = "localhost:8090",
            CustomMinimumSize = new Vector2(0, 42),
        };
        _address.AddThemeFontOverride("font", UiFonts.Mono);
        _address.AddThemeFontSizeOverride("font_size", 15);
        _address.TextSubmitted += _ => SubmitDirect();
        col.AddChild(_address);

        col.AddChild(UiKit.MakeLabel("PASSWORD (IF PROTECTED)", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _password = new LineEdit
        {
            PlaceholderText = "••••••••",
            Secret = true,
            CustomMinimumSize = new Vector2(0, 42),
        };
        _password.AddThemeFontOverride("font", UiFonts.Mono);
        _password.AddThemeFontSizeOverride("font_size", 15);
        _password.TextSubmitted += _ => SubmitDirect();
        col.AddChild(_password);

        _modalError = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.DangerText);
        _modalError.Visible = false;
        col.AddChild(_modalError);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        col.AddChild(buttons);
        var cancel = UiKit.MakeButton("CANCEL", HideConnectModal, ButtonVariant.Ghost);
        buttons.AddChild(cancel);
        var connect = UiKit.MakeButton("◆ CONNECT", SubmitDirect, ButtonVariant.Primary);
        connect.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        connect.CustomMinimumSize = new Vector2(0, 44);
        buttons.AddChild(connect);
    }

    private void ShowConnectModal()
    {
        _modalError.Visible = false;
        _modal.Visible = true;
        _address.GrabFocus();
    }

    private void HideConnectModal() => _modal.Visible = false;

    // ---- Update check --------------------------------------------------------

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

        var servers = _serverMap.Values.OrderByDescending(s => s.Players).ThenBy(s => s.Name).ToList();
        _online.Text = $"● {servers.Sum(s => s.Players)} ONLINE";

        // Keep the selection across re-renders; fall back to the top row when it vanished.
        if (_selectedSessionId is null || !_serverMap.ContainsKey(_selectedSessionId))
            _selectedSessionId = servers.Count > 0 ? servers[0].SessionId : null;

        if (_sseError is not null)
        {
            _listStatus.Text = $"LOBBY UNREACHABLE ({_cm.LobbyBase})";
            _listStatus.Visible = true;
        }
        else if (servers.Count == 0)
        {
            _listStatus.Text = "NO PUBLIC SERVERS ONLINE";
            _listStatus.Visible = true;
        }
        else
        {
            _listStatus.Visible = false;
        }

        foreach (var s in servers)
        {
            var row = new ServerRow();
            row.Configure(s, s.SessionId == _selectedSessionId);
            string sid = s.SessionId;
            row.Pressed += () =>
            {
                if (_selectedSessionId == sid)
                    return;
                _selectedSessionId = sid;
                RenderServers();
            };
            _list.AddChild(row);
        }

        RenderDetail();
    }

    private void RenderDetail()
    {
        ServerDto? sel = _selectedSessionId is not null ? _serverMap.GetValueOrDefault(_selectedSessionId) : null;
        if (ReferenceEquals(sel, _renderedDetail))
            return; // same instance = no update arrived for the selected server
        _renderedDetail = sel;

        foreach (var child in _detailBox.GetChildren())
            child.QueueFree();

        if (sel is null)
        {
            var empty = UiKit.MakeLabel("SELECT A GAME", UiKit.TextStyle.Data, DesignTokens.TextDim);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.SizeFlagsVertical = SizeFlags.ExpandFill;
            empty.VerticalAlignment = VerticalAlignment.Center;
            _detailBox.AddChild(empty);
            _hint.Text = "";
            return;
        }

        bool live = sel.State == "in-progress";
        int max = sel.MaxPlayers > 0 ? sel.MaxPlayers : 32;

        // Name + status pill.
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 10);
        _detailBox.AddChild(titleRow);
        var name = UiKit.MakeLabel(sel.Name, UiKit.TextStyle.Title);
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleRow.AddChild(name);
        if (sel.Protected)
        {
            var lockPill = new StatusPill { SizeFlagsVertical = SizeFlags.ShrinkCenter };
            titleRow.AddChild(lockPill);
            lockPill.Configure("⚿ PROTECTED", StatusPill.Kind.Warn);
        }
        var pill = new StatusPill { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        titleRow.AddChild(pill);
        (string pillText, StatusPill.Kind pillKind) = sel.State switch
        {
            "in-progress" => ("LIVE", StatusPill.Kind.Warn),
            "ended" => ("ENDED", StatusPill.Kind.Neutral),
            _ => ("OPEN", StatusPill.Kind.Ok),
        };
        pill.Configure(pillText, pillKind, pulse: live);

        // Mono subline: state · pilots · host attribution. Map name/sectors and the 2D
        // sector preview were removed from the server lobby — map info lives in the game lobby.
        var subParts = new List<string> { (sel.State ?? "lobby").ToUpperInvariant() };
        subParts.Add($"{sel.Players}/{max} PILOTS");
        if (!string.IsNullOrEmpty(sel.HostedBy))
            subParts.Add($"HOSTED BY {sel.HostedBy.ToUpperInvariant()}");
        var sub = UiKit.MakeLabel(string.Join(" · ", subParts), UiKit.TextStyle.Data, DesignTokens.Text2);
        sub.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _detailBox.AddChild(sub);

        // Team rosters — no join buttons here by design; joining picks a team in-game.
        var teams = new HBoxContainer();
        teams.AddThemeConstantOverride("separation", 12);
        _detailBox.AddChild(teams);
        teams.AddChild(BuildTeamPanel(0, sel, max));
        teams.AddChild(BuildTeamPanel(1, sel, max));

        var connect = UiKit.MakeButton("◆ CONNECT TO SERVER", () => Join(sel), ButtonVariant.Primary);
        connect.CustomMinimumSize = new Vector2(0, 46);
        _detailBox.AddChild(connect);

        _hint.Text = $"SELECTED · {sel.Name} — " + (live ? "match in progress" : "pick a team to deploy");
    }

    private Control BuildTeamPanel(int team, ServerDto sel, int maxPlayers)
    {
        Color tc = DesignTokens.Faction(team);
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(tc, 0.07f),
            BorderColor = new Color(tc, 0.30f),
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.SetContentMarginAll(12);
        panel.AddThemeStyleboxOverride("panel", sb);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        panel.AddChild(col);

        var members = sel.Roster?.Where(r => r.Team == team).ToList() ?? new List<RosterDto>();
        int capacity = Math.Max(maxPlayers / 2, members.Count);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        col.AddChild(header);
        header.AddChild(UiKit.MakeLabel("◆", UiKit.TextStyle.Data, tc));
        var teamName = UiKit.MakeLabel(team == 0 ? "BLUE" : "RED", UiKit.TextStyle.Label, DesignTokens.TextHi);
        teamName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(teamName);
        header.AddChild(UiKit.MakeLabel($"{members.Count}/{capacity}", UiKit.TextStyle.Data, tc));

        // Roster rows: ◆ = flying (has a ship), ▸ = in the lobby; [RDY] marks readied pilots.
        const int MaxRows = 8;
        foreach (var r in members.Take(MaxRows))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 7);
            col.AddChild(row);
            row.AddChild(UiKit.MakeLabel(r.Flying ? "◆" : "▸", UiKit.TextStyle.Data, r.Flying ? tc : DesignTokens.TextDim));
            var pilot = UiKit.MakeLabel(r.Name, UiKit.TextStyle.Body, DesignTokens.TextHi);
            pilot.AddThemeFontSizeOverride("font_size", 13);
            pilot.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            pilot.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(pilot);
            if (r.Ready)
                row.AddChild(UiKit.MakeLabel("RDY", UiKit.TextStyle.Data, DesignTokens.Ok));
        }
        if (members.Count > MaxRows)
            col.AddChild(UiKit.MakeLabel($"+{members.Count - MaxRows} more", UiKit.TextStyle.Data, DesignTokens.TextDim));

        // A few "open slot" rows so capacity reads at a glance (design shows italic placeholders).
        int open = Math.Max(0, capacity - members.Count);
        for (int i = 0; i < Math.Min(open, 3); i++)
            col.AddChild(UiKit.MakeLabel("+ open slot", UiKit.TextStyle.Data, DesignTokens.TextDim));
        if (open > 3)
            col.AddChild(UiKit.MakeLabel($"+{open - 3} more slots", UiKit.TextStyle.Data, DesignTokens.TextDim));

        return panel;
    }

    // ---- Joining -------------------------------------------------------------

    private void Join(ServerDto s)
    {
        CommitName();
        // A password-protected server prompts for the passphrase first; the modal seeds the secret and
        // then dials via the same transport branch below. Open servers dial straight through.
        if (s.Protected)
        {
            ServerPasswordModal.Open(this, s.Name, pw =>
            {
                _cm.SetJoinSecret(pw);
                Dial(s);
            });
            return;
        }
        Dial(s);
    }

    // Direct WebSocket to an advertised endpoint, else WebRTC through the lobby. The lobby name is kept
    // for the connecting modal's server well.
    private void Dial(ServerDto s)
    {
        if (!string.IsNullOrEmpty(s.PublicEndpoint))
            _cm.ConnectTo(s.PublicEndpoint, s.Name);
        else
            _cm.ConnectToLobby(s.SessionId, s.Name);
    }

    private void SubmitDirect()
    {
        string text = _address.Text.Trim();
        if (text.Length == 0)
        {
            _modalError.Text = "Enter an address like  host:port";
            _modalError.Visible = true;
            return;
        }
        _modalError.Visible = false;
        CommitName();
        // The shared-secret password rides the next Hello frame (same slot SIM_SECRET seeds).
        _cm.SetJoinSecret(_password.Text);
        HideConnectModal();
        _cm.ConnectTo(text);
    }

    private void CommitName()
    {
        string name = UserPrefs.Clamp(_name.Text);
        UserPrefs.SetPilotName(name);
        _cm.SetPilotName(name);
    }

    // ---- List row --------------------------------------------------------------

    // One game-list entry: status dot · name + "hosted by" tag · pilot count, with a hairline
    // bottom border and a cyan tint + accent inset edge when selected. Drawn custom (like
    // ChamferButton) so the row matches the design instead of a stock Button face.
    private sealed partial class ServerRow : Button
    {
        private string _name = "";
        private string _tag = "";
        private string _pilots = "";
        private bool _full;
        private bool _live;
        private bool _protected;
        private bool _selected;
        private float _pulse; // 0..2π phase for the live-dot fade

        public override void _Ready()
        {
            UiFonts.EnsureLoaded();
            CustomMinimumSize = new Vector2(0, 46);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            foreach (string s in new[] { "normal", "hover", "pressed", "focus", "disabled" })
                AddThemeStyleboxOverride(s, new StyleBoxEmpty());
            Pressed += () => SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
        }

        public void Configure(ServerDto s, bool selected)
        {
            int max = s.MaxPlayers > 0 ? s.MaxPlayers : 32;
            _name = s.Name;
            _tag = !string.IsNullOrEmpty(s.HostedBy) ? $"hosted by {s.HostedBy}" : (s.State ?? "");
            _pilots = $"{s.Players}/{max}";
            _full = s.Players >= max;
            _live = s.State == "in-progress";
            _protected = s.Protected;
            _selected = selected;
            QueueRedraw();
        }

        public override void _Process(double delta)
        {
            if (!_live || !IsVisibleInTree())
                return;
            _pulse = (_pulse + (float)delta * 4.5f) % Mathf.Tau;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var r = new Rect2(Vector2.Zero, Size);

            if (_selected)
            {
                DrawRect(r, new Color(DesignTokens.TeamAccent, 0.10f), filled: true);
                DrawRect(new Rect2(0, 0, 2, Size.Y), DesignTokens.TeamAccent, filled: true);
            }
            else if (IsHovered())
            {
                DrawRect(r, new Color(DesignTokens.TeamAccent, 0.05f), filled: true);
            }
            DrawLine(new Vector2(0, Size.Y - 0.5f), new Vector2(Size.X, Size.Y - 0.5f), DesignTokens.BorderLo, 1f);

            // Status dot: pulsing warn triangle when a match is live, steady ok dot when open.
            Font mono = UiFonts.Mono;
            if (_live)
            {
                float a = 0.55f + 0.45f * Mathf.Sin(_pulse);
                DrawString(mono, new Vector2(12, Size.Y / 2f + 4f), "▶", fontSize: 11, modulate: new Color(DesignTokens.Warn, a));
            }
            else
            {
                DrawString(mono, new Vector2(12, Size.Y / 2f + 4f), "●", fontSize: 11, modulate: DesignTokens.Ok);
            }

            // Right-aligned pilot count first, so the name knows how much room it has.
            Color countColor = _full ? DesignTokens.DangerText : DesignTokens.Data;
            Vector2 countSize = mono.GetStringSize(_pilots, HorizontalAlignment.Left, -1, DesignTokens.DataSize);
            DrawString(
                mono,
                new Vector2(Size.X - countSize.X - 14, Size.Y / 2f + 5f),
                _pilots,
                fontSize: DesignTokens.DataSize,
                modulate: countColor
            );

            float textX = 34;
            float lockW = _protected ? 18f : 0f;
            float maxW = Size.X - textX - countSize.X - 28 - lockW;
            string shownName = Truncate(UiFonts.SairaSemi, _name, DesignTokens.BodySize, maxW);
            DrawString(
                UiFonts.SairaSemi,
                new Vector2(textX, 20),
                shownName,
                fontSize: DesignTokens.BodySize,
                modulate: DesignTokens.TextHi
            );
            // Amber padlock right after the name marks a password-protected server.
            if (_protected)
            {
                float nameW = UiFonts.SairaSemi.GetStringSize(shownName, HorizontalAlignment.Left, -1, DesignTokens.BodySize).X;
                DrawPadlock(new Vector2(textX + nameW + 7f, 12f));
            }
            if (_tag.Length > 0)
                DrawString(
                    mono,
                    new Vector2(textX, 37),
                    Truncate(mono, _tag, 11, maxW),
                    fontSize: 11,
                    modulate: DesignTokens.TextDim
                );
        }

        // Small padlock (amber body + shackle arc), drawn as shapes so it renders regardless of the
        // UI font's glyph coverage. `at` is the top-left of the icon box.
        private void DrawPadlock(Vector2 at)
        {
            var c = DesignTokens.Warn;
            float w = 9f;
            float h = 7f;
            float bodyTop = at.Y + 4f;
            DrawArc(new Vector2(at.X + w / 2f, bodyTop), w * 0.32f, Mathf.Pi, Mathf.Tau, 12, c, 1.4f, antialiased: true);
            DrawRect(new Rect2(at.X, bodyTop, w, h), c, filled: true);
        }

        private static string Truncate(Font f, string text, int fontSize, float maxW)
        {
            if (maxW <= 0 || f.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X <= maxW)
                return text;
            for (int len = text.Length - 1; len > 0; len--)
            {
                string t = text[..len] + "…";
                if (f.GetStringSize(t, HorizontalAlignment.Left, -1, fontSize).X <= maxW)
                    return t;
            }
            return "…";
        }
    }
}
