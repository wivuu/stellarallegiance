using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using StellarAllegiance.Shared.Lobby;
using HttpClient = System.Net.Http.HttpClient;

namespace StellarAllegiance.Ui;

// The in-client account page (plan .PLAN/LobbyRankingService.md §1.5 / WP3.3): display name
// (edited through PATCH /api/me — the lobby's PlayerGrain owns it, never the local callsign
// pref), career line, linked logins read-only, "manage in browser" (the web /me page owns
// passkeys and provider links), sign out. Same modal scaffold as SignInDialog. Anonymous: only
// offers to sign in.
public partial class AccountDialog : Control
{
    public static bool Active { get; private set; }

    public static void Open(Node context)
    {
        if (Active)
            return;
        ModalHost.Ensure(context).AddChild(new AccountDialog());
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private VBoxContainer _col = null!;
    private Label _title = null!;
    private Label _career = null!;
    private LineEdit _nameEdit = null!;
    private Label _nameStatus = null!;
    private VBoxContainer _logins = null!;
    private ChamferButton _save = null!;
    private PlayerProfileDto? _profile;
    private bool _closing;

    public override void _EnterTree() => Active = true;

    public override void _ExitTree()
    {
        Active = false;
        if (AuthSession.Instance is { } auth)
            auth.StateChanged -= OnAuthChanged;
    }

    public override void _Ready()
    {
        BuildUi();
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuOpen);
        if (AuthSession.Instance is { } auth)
            auth.StateChanged += OnAuthChanged;
        _ = LoadProfileAsync();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- Layout -------------------------------------------------------------

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this);
        UiFonts.EnsureLoaded();

        var scrim = new ColorRect { Color = new Color(DesignTokens.Scrim, 0.82f), MouseFilter = MouseFilterEnum.Stop };
        scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        scrim.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true })
                Close();
        };
        AddChild(scrim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new BracketPanel { FillOverride = DesignTokens.PanelDeep, CustomMinimumSize = new Vector2(500, 0) };
        center.AddChild(panel);

        _col = new VBoxContainer();
        _col.AddThemeConstantOverride("separation", 14);
        panel.AddChild(_col);

        bool signedIn = AuthSession.Instance?.IsSignedIn ?? false;
        _col.AddChild(UiKit.MakeLabel("PUBLIC LOBBY", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _title = UiKit.MakeLabel(signedIn ? AuthSession.Instance!.DisplayName : "ACCOUNT", UiKit.TextStyle.Title);
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _col.AddChild(_title);

        if (!signedIn)
        {
            var body = UiKit.MakeLabel(
                "You're playing without an account. Sign in to keep your career stats and join Verified servers.",
                UiKit.TextStyle.Body,
                DesignTokens.Text2
            );
            body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            body.CustomMinimumSize = new Vector2(460, 0);
            _col.AddChild(body);
            var signIn = UiKit.MakeButton(
                "SIGN IN",
                () =>
                {
                    Close();
                    SignInDialog.Open(GetParent());
                },
                ButtonVariant.Primary
            );
            signIn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _col.AddChild(signIn);
            _col.AddChild(new DiamondDivider());
            var close = UiKit.MakeButton("CLOSE", Close, ButtonVariant.Ghost);
            close.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _col.AddChild(close);
            return;
        }

        _career = UiKit.MakeLabel("Loading your profile…", UiKit.TextStyle.Data, DesignTokens.Text2);
        _col.AddChild(_career);

        // Display name (lobby-owned; unique, 3–24, case-insensitive).
        _col.AddChild(UiKit.MakeLabel("DISPLAY NAME", UiKit.TextStyle.Label, DesignTokens.TextDim));
        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 10);
        _col.AddChild(nameRow);
        _nameEdit = new LineEdit
        {
            Text = AuthSession.Instance!.DisplayName,
            PlaceholderText = "3–24 characters",
            MaxLength = LobbyLimits.DisplayNameMax,
            CustomMinimumSize = new Vector2(0, 36),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _nameEdit.AddThemeFontOverride("font", UiFonts.Mono);
        _nameEdit.AddThemeFontSizeOverride("font_size", 15);
        _nameEdit.TextSubmitted += _ => SaveName();
        nameRow.AddChild(_nameEdit);
        _save = UiKit.MakeButton("SAVE", SaveName, ButtonVariant.Primary);
        nameRow.AddChild(_save);
        _nameStatus = UiKit.MakeLabel(
            "Shown on every listing, roster and ladder. Matches already played keep the old name.",
            UiKit.TextStyle.Data,
            DesignTokens.TextDim
        );
        _nameStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _col.AddChild(_nameStatus);

        _col.AddChild(UiKit.MakeLabel("LINKED LOGINS", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _logins = new VBoxContainer();
        _logins.AddThemeConstantOverride("separation", 4);
        _col.AddChild(_logins);
        _logins.AddChild(UiKit.MakeLabel("…", UiKit.TextStyle.Data, DesignTokens.TextDim));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _col.AddChild(row);
        var manage = UiKit.MakeButton(
            "MANAGE IN BROWSER",
            () => OS.ShellOpen($"{AuthSession.Instance!.LobbyBase}/me"),
            ButtonVariant.Secondary
        );
        manage.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(manage);
        row.AddChild(UiKit.MakeButton("SIGN OUT", SignOut, ButtonVariant.Danger));

        _col.AddChild(new DiamondDivider());
        var closeBtn = UiKit.MakeButton("CLOSE", Close, ButtonVariant.Ghost);
        closeBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _col.AddChild(closeBtn);
    }

    // ---- Data -----------------------------------------------------------------

    private async Task LoadProfileAsync()
    {
        var auth = AuthSession.Instance;
        if (auth is null || !auth.IsSignedIn)
            return;
        try
        {
            var token = await auth.GetAccessTokenAsync();
            if (token is null)
                return;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{auth.LobbyBase}/api/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                CallDeferred(nameof(ApplyProfileError), $"profile unavailable ({(int)resp.StatusCode})");
                return;
            }
            _profile = await resp.Content.ReadFromJsonAsync<PlayerProfileDto>(JsonOpts);
            CallDeferred(nameof(ApplyProfile));
        }
        catch (Exception e)
        {
            CallDeferred(nameof(ApplyProfileError), e.Message);
        }
    }

    private void ApplyProfile()
    {
        if (_profile is null || _closing)
            return;
        var p = _profile;
        _title.Text = p.DisplayName;
        _career.Text =
            $"{p.MatchesPlayed} RANKED MATCHES · {p.Wins}W {p.Losses}L · {p.Kills}K {p.Deaths}D {p.Ejects}EJ · {p.Points} PTS"
            + (p.IsAdmin ? " · ADMIN" : "");
        foreach (var child in _logins.GetChildren())
            child.QueueFree();
        if (p.Logins.Length == 0)
            _logins.AddChild(
                UiKit.MakeLabel(
                    "Passkey-only account — add a provider in the browser.",
                    UiKit.TextStyle.Data,
                    DesignTokens.TextDim
                )
            );
        foreach (var l in p.Logins)
            _logins.AddChild(
                UiKit.MakeLabel(
                    string.IsNullOrEmpty(l.DisplayName)
                        ? l.Provider.ToUpperInvariant()
                        : $"{l.Provider.ToUpperInvariant()} · {l.DisplayName}",
                    UiKit.TextStyle.Data,
                    DesignTokens.TextHi
                )
            );
    }

    private void ApplyProfileError(string message)
    {
        if (_closing)
            return;
        _career.Text = message;
    }

    private void SaveName()
    {
        var auth = AuthSession.Instance;
        if (auth is null || !auth.IsSignedIn)
            return;
        string name = _nameEdit.Text.Trim();
        if (name.Length < LobbyLimits.DisplayNameMin || name.Length > LobbyLimits.DisplayNameMax)
        {
            SetNameStatus(
                $"Display name must be {LobbyLimits.DisplayNameMin}–{LobbyLimits.DisplayNameMax} characters.",
                DesignTokens.Danger
            );
            return;
        }
        if (name == auth.DisplayName)
        {
            SetNameStatus("That's already your name.", DesignTokens.TextDim);
            return;
        }
        _save.Disabled = true;
        SetNameStatus("Saving…", DesignTokens.TextDim);
        _ = SaveNameAsync(auth, name);
    }

    private async Task SaveNameAsync(AuthSession auth, string name)
    {
        try
        {
            var token = await auth.GetAccessTokenAsync();
            if (token is null)
            {
                CallDeferred(nameof(ApplySaveResult), false, "Not signed in.");
                return;
            }
            using var req = new HttpRequestMessage(HttpMethod.Patch, $"{auth.LobbyBase}/api/me")
            {
                Content = JsonContent.Create(new UpdateProfileRequest(name)),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                _profile = await resp.Content.ReadFromJsonAsync<PlayerProfileDto>(JsonOpts);
                CallDeferred(nameof(ApplySaveResult), true, name);
                return;
            }
            string detail = resp.StatusCode switch
            {
                HttpStatusCode.Conflict => "That display name is taken.",
                HttpStatusCode.BadRequest =>
                    $"Display name must be {LobbyLimits.DisplayNameMin}–{LobbyLimits.DisplayNameMax} characters.",
                HttpStatusCode.Unauthorized => "Session expired — sign in again.",
                _ => $"Rename failed ({(int)resp.StatusCode}).",
            };
            CallDeferred(nameof(ApplySaveResult), false, detail);
        }
        catch (Exception e)
        {
            CallDeferred(nameof(ApplySaveResult), false, e.Message);
        }
    }

    private void ApplySaveResult(bool ok, string detail)
    {
        if (_closing)
            return;
        _save.Disabled = false;
        if (ok)
        {
            AuthSession.Instance?.UpdateDisplayName(detail);
            _title.Text = detail;
            SetNameStatus("Saved. It shows on your next join.", DesignTokens.Ok);
            ApplyProfile();
        }
        else
            SetNameStatus(detail, DesignTokens.Danger);
    }

    private void SetNameStatus(string text, Color color)
    {
        _nameStatus.Text = text;
        _nameStatus.AddThemeColorOverride("font_color", color);
    }

    private void SignOut()
    {
        _ = AuthSession.Instance?.SignOutAsync();
        Close();
    }

    private void OnAuthChanged()
    {
        // Signed out from elsewhere while open: nothing sensible left to show.
        if (AuthSession.Instance is { IsSignedIn: false })
            Close();
    }

    private void Close()
    {
        if (_closing)
            return;
        _closing = true;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuClose);
        QueueFree();
    }
}
