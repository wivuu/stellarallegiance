using System.Text;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Ui;

// Lobby / pre-match / post-match overlay. Shown whenever the local player isn't in a live
// match:
//   • Lobby phase  — team picker + ready-up.
//   • Ended phase  — the winner banner; the server returns everyone to the lobby shortly.
//
// Pure overlay: it reads the server's lobby roster (GameNetClient.LobbyPlayers) + match phase
// (WorldRenderer.Phase) and sends team/ready actions over the wire. The match start/stop and
// balance rules are enforced server-side; this just drives them. Created by the Hud.
public partial class Lobby : Control
{
    // Team identity stays the faction colours (NOT the cyan structural accent).
    private static readonly Color Team0 = DesignTokens.Faction0;
    private static readonly Color Team1 = DesignTokens.Faction1;
    private static new readonly Color Ready = DesignTokens.Ok;

    private ConnectionManager _cm = null!;
    private WorldRenderer _world = null!;
    private GameNetClient _net = null!;

    private Label _title = null!;
    private Label _winner = null!;
    private RichTextLabel _roster = null!;
    private Label _status = null!;
    private HBoxContainer _teamRow = null!;
    private ChamferButton _joinBlue = null!;
    private ChamferButton _joinRed = null!;
    private ChamferButton _ready = null!;
    private ChamferButton _leave = null!;

    public void Init(ConnectionManager cm, WorldRenderer world)
    {
        _cm = cm;
        _world = world;
        _net = GetNode<GameNetClient>("../../GameNetClient");

        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this); // cascades the design theme to the roster/sliders/etc. below

        var bg = new ColorRect { Color = new Color(DesignTokens.Void, 0.78f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        var panel = new BracketPanel();
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        panel.GrowHorizontal = GrowDirection.End;
        panel.GrowVertical = GrowDirection.End;
        panel.OffsetLeft = 20;
        panel.OffsetTop = 20;
        AddChild(panel);

        var col = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
        col.AddThemeConstantOverride("separation", 12);
        panel.AddChild(col);

        _winner = Centered(UiKit.TextStyle.Display, "");
        _winner.Visible = false;
        col.AddChild(_winner);

        _title = Centered(UiKit.TextStyle.Title, "LOBBY");
        col.AddChild(_title);

        _status = Centered(UiKit.TextStyle.Body, "Pick a side and ready up.");
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(_status);

        col.AddChild(new DiamondDivider());

        _roster = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(0, 90),
        };
        _roster.AddThemeFontSizeOverride("normal_font_size", 16);
        col.AddChild(_roster);

        col.AddChild(new DiamondDivider());

        _teamRow = new HBoxContainer();
        _teamRow.AddThemeConstantOverride("separation", 8);
        _teamRow.Alignment = BoxContainer.AlignmentMode.Center;
        col.AddChild(_teamRow);
        // Team-join buttons carry their faction colour; Ready is the primary accent action.
        _joinBlue = MakeButton("Join BLUE", () => _net.SetTeam(0), ButtonVariant.Primary, Team0);
        _joinRed = MakeButton("Join RED", () => _net.SetTeam(1), ButtonVariant.Primary, Team1);
        _ready = MakeButton("Ready", ToggleReady, ButtonVariant.Primary);
        _teamRow.AddChild(_joinBlue);
        _teamRow.AddChild(_joinRed);
        _teamRow.AddChild(_ready);

        col.AddChild(new DiamondDivider());

        // Audio settings: one slider per bus, persisted + applied live via UserPrefs.
        col.AddChild(Centered(UiKit.TextStyle.Label, "AUDIO"));
        foreach (var bus in UserPrefs.AudioBuses)
        {
            string b = bus;
            col.AddChild(UiKit.MakeSliderRow(b, 0, 1, 0.05, UserPrefs.GetBusVolume(b), v => UserPrefs.SetBusVolume(b, (float)v)));
        }

        col.AddChild(new DiamondDivider());

        // Always-available exit back to the server-address screen.
        var leaveRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        col.AddChild(leaveRow);
        _leave = MakeButton("Leave Server", () => _cm.Leave(), ButtonVariant.Ghost);
        leaveRow.AddChild(_leave);
    }

    private LobbyPlayer? Me()
    {
        foreach (var p in _net.LobbyPlayers)
            if (p.Id == _net.LocalClientId)
                return p;
        return null;
    }

    private void ToggleReady()
    {
        if (Me() is LobbyPlayer me)
            _net.SetReady(!me.Ready);
    }

    private static Label Centered(UiKit.TextStyle style, string text)
    {
        var l = UiKit.MakeLabel(text, style);
        l.HorizontalAlignment = HorizontalAlignment.Center;
        return l;
    }

    // ChamferButton bakes in the UI click sound, so we only wire the action here.
    private static ChamferButton MakeButton(string text, System.Action onPressed, ButtonVariant variant = ButtonVariant.Secondary, Color? accent = null)
    {
        var b = new ChamferButton { Text = text, Variant = variant, AccentOverride = accent, CustomMinimumSize = new Vector2(130, 36) };
        b.Pressed += onPressed;
        return b;
    }

    public override void _Process(double delta)
    {
        // Only drive the lobby once we're truly connected; the ConnectLinkModal owns the
        // screen until then.
        if (_cm.State != ConnectionManager.ConnState.Connected)
        {
            Visible = false;
            return;
        }

        // A live match owns the screen — the Hud's spawn menu / flight HUD takes over.
        if (_world.Phase == MatchPhase.Active)
        {
            Visible = false;
            return;
        }

        Visible = true;
        UpdateUi();
    }

    private void UpdateUi()
    {
        bool ended = _world.Phase == MatchPhase.Ended;
        LobbyPlayer? me = Me();

        if (ended)
        {
            byte w = _world.Winner ?? 0;
            _winner.Text = $"TEAM {(w == 0 ? "BLUE" : "RED")} WINS";
            _winner.AddThemeColorOverride("font_color", w == 0 ? Team0 : Team1);
            _winner.Visible = true;
            _title.Text = "MATCH OVER";
        }
        else
        {
            _winner.Visible = false;
            _title.Text = "LOBBY";
        }

        // Roster + balance counts.
        int n0 = 0,
            n1 = 0;
        var sb = new StringBuilder();
        foreach (var p in _net.LobbyPlayers)
        {
            if (p.Team == 0)
                n0++;
            else
                n1++;
            bool isMe = p.Id == _net.LocalClientId;
            string sideHex = (p.Team == 0 ? Team0 : Team1).ToHtml(false);
            string side = p.Team == 0 ? "BLUE" : "RED";
            string tick = p.Ready ? $"  [color=#{Ready.ToHtml(false)}]READY[/color]" : "";
            string who = string.IsNullOrEmpty(p.Name) ? $"Pilot{p.Id}" : p.Name;
            sb.Append($"[color=#{sideHex}]{(isMe ? "» " : "  ")}{who}[/color]  —  {side}{tick}\n");
        }
        _roster.Text = sb.Length == 0 ? "[i]waiting for pilots…[/i]" : sb.ToString();

        // Status line.
        if (ended)
            _status.Text = "Next match starting shortly…";
        else if (me is LobbyPlayer m && m.Ready)
            _status.Text = "Ready — waiting for the other pilots…";
        else
            _status.Text = "Pick a side and ready up. (AI drones fill out the opposition.)";

        // Controls: only meaningful pre-match.
        bool lobbyPhase = !ended;
        _teamRow.Visible = lobbyPhase;
        if (lobbyPhase && me is LobbyPlayer mp)
        {
            _joinBlue.Text = $"Join BLUE ({n0})";
            _joinRed.Text = $"Join RED ({n1})";
            _joinBlue.Disabled = mp.Team == 0;
            _joinRed.Disabled = mp.Team == 1;
            _ready.Text = mp.Ready ? "Unready" : "Ready";
        }
    }
}
