using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
// Godot ships its own HttpClient; the lobby list fetch uses the BCL one.
using HttpClient = System.Net.Http.HttpClient;

// The first screen a player sees when the client is launched WITHOUT --host. Two ways to join:
//   1. Pick from the PUBLIC LOBBY list (ServerShare at ConnectionManager.LobbyBase) — these join
//      over WebRTC (works for NAT'd player-run servers), keyed by the server's SessionId.
//   2. Type an ip-or-hostname:port for a DIRECT WebSocket join (LAN / dev / port-forwarded).
// Either hands off to ConnectionManager, which opens the single native connection.
public partial class ServerInputOverlay : Control
{
    private static readonly Color Dim = new(0.85f, 0.9f, 1f);
    private static readonly Color Bad = new(1f, 0.45f, 0.4f);
    private static readonly Color Faint = new(0.6f, 0.66f, 0.78f);

    // Shared by the (background) list fetch; web JSON defaults are case-insensitive so this binds
    // ServerShare's camelCase entries.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private sealed record ServerDto(string SessionId, string Name, string? PublicEndpoint);

    private ConnectionManager _cm = null!;
    private LineEdit _field = null!;
    private Label _error = null!;
    private VBoxContainer _list = null!;
    private Label _listStatus = null!;

    // Cross-thread handoff from the fetch task to RenderServers (called on the main thread).
    private List<ServerDto>? _fetched;
    private string? _fetchError;

    public void Init(ConnectionManager cm)
    {
        _cm = cm;

        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new ColorRect { Color = new Color(0.02f, 0.03f, 0.06f, 0.95f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        col.AddThemeConstantOverride("separation", 12);
        center.AddChild(col);

        col.AddChild(Centered("STELLAR ALLEGIANCE", 38));

        // ---- Public server list ----
        var header = new HBoxContainer();
        col.AddChild(header);
        var listTitle = new Label { Text = "Public servers", HorizontalAlignment = HorizontalAlignment.Left };
        listTitle.AddThemeFontSizeOverride("font_size", 18);
        listTitle.AddThemeColorOverride("font_color", Dim);
        listTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(listTitle);
        var refresh = new Button { Text = "Refresh" };
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

        _field = new LineEdit
        {
            PlaceholderText = "ip-or-hostname:port   (e.g. localhost:8090)",
            Text = "localhost:8090",
            CustomMinimumSize = new Vector2(0, 40),
        };
        _field.AddThemeFontSizeOverride("font_size", 18);
        _field.TextSubmitted += _ => Submit();
        col.AddChild(_field);

        _error = Centered("", 16);
        _error.AddThemeColorOverride("font_color", Bad);
        _error.Visible = false;
        col.AddChild(_error);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        col.AddChild(row);
        var connect = new Button { Text = "Connect", CustomMinimumSize = new Vector2(200, 44) };
        connect.Pressed += Submit;
        row.AddChild(connect);

        Reload();
    }

    // Kick off a background fetch of the public-server list, then render on the main thread.
    private void Reload()
    {
        _listStatus.Text = "Loading servers…";
        _listStatus.Visible = true;
        foreach (var child in _list.GetChildren()) child.QueueFree();
        _ = FetchAsync();
    }

    private async Task FetchAsync()
    {
        try
        {
            var json = await Http.GetStringAsync($"{_cm.LobbyBase}/servers");
            _fetched = JsonSerializer.Deserialize<List<ServerDto>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            _fetchError = null;
        }
        catch (Exception e)
        {
            _fetched = null;
            _fetchError = e.Message;
        }
        CallDeferred(nameof(RenderServers));
    }

    private void RenderServers()
    {
        foreach (var child in _list.GetChildren()) child.QueueFree();

        if (_fetchError is not null)
        {
            _listStatus.Text = $"Lobby unreachable ({_cm.LobbyBase})";
            _listStatus.Visible = true;
            return;
        }

        var servers = _fetched ?? new();
        if (servers.Count == 0)
        {
            _listStatus.Text = "No public servers online";
            _listStatus.Visible = true;
            return;
        }

        _listStatus.Visible = false;
        foreach (var s in servers)
        {
            // The lobby probed each server: a PublicEndpoint means it's directly joinable over
            // WebSocket; otherwise it's behind a NAT and we join over WebRTC (relayed SDP + STUN).
            bool direct = !string.IsNullOrEmpty(s.PublicEndpoint);
            string label = direct ? $"{s.Name}   ·   {s.PublicEndpoint}" : $"{s.Name}   ·   relayed";
            var btn = new Button
            {
                Text = label,
                CustomMinimumSize = new Vector2(0, 40),
                Alignment = HorizontalAlignment.Left,
            };
            btn.AddThemeFontSizeOverride("font_size", 17);
            // Capture per-iteration values for the handler.
            string sid = s.SessionId, name = s.Name, endpoint = s.PublicEndpoint ?? "";
            btn.Pressed += () =>
            {
                if (direct) _cm.ConnectTo(endpoint);          // straight WebSocket to the server
                else _cm.ConnectToLobby(sid, name);            // WebRTC via the lobby
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
        _cm.ConnectTo(text);
    }

    private static Label Centered(string text, int size)
    {
        var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", Dim);
        return l;
    }
}
