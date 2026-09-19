using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using StellarAllegiance.Launcher.Controls;
using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Theme;
using StellarAllegiance.Ui;
using TextStyle = StellarAllegiance.Launcher.Theme.TextStyle;

namespace StellarAllegiance.Launcher.Views;

// `--launcher-showcase` — every ported component in every variant, on one page. This is the launcher's
// side of the visual contract: compare it with the game's UiShowcase (F9 in-game, or `--ui-showcase`)
// whenever a token or a component changes. Add new launcher controls here.
public sealed class ShowcaseWindow : Window
{
    public ShowcaseWindow()
    {
        Title = "Stellar Allegiance — launcher showcase";
        Width = 1100;
        Height = 760;
        Background = Sa.Void;
        UseLayoutRounding = true;
        FontFamily = Sa.Saira;

        var page = new StackPanel { Spacing = 18, Margin = new Thickness(26) };
        page.Children.Add(Sa.Text("LAUNCHER COMPONENT SHOWCASE", TextStyle.Display));
        page.Children.Add(new DiamondDivider());

        page.Children.Add(
            Section(
                "TYPE SCALE",
                Column(
                    Sa.Text("Display 34 — Saira Bold", TextStyle.Display),
                    Sa.Text("Hero 26 — Saira Bold", TextStyle.Hero),
                    Sa.Text("Title 22 — Saira Bold", TextStyle.Title),
                    Sa.Text("Body 15 — Saira Regular", TextStyle.Body),
                    Sa.Text("DATA 14 — JETBRAINS MONO 0123456789 -> != ", TextStyle.Data),
                    Sa.Text("LABEL 13 — SAIRA SEMIBOLD + SPACING", TextStyle.Label),
                    Sa.Text("Caption 11 — Saira Regular", TextStyle.Caption),
                    Sa.Text("Micro 9 — Saira Regular", TextStyle.Micro)
                )
            )
        );

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var variant in Enum.GetValues<ButtonVariant>())
        {
            bool icon = variant == ButtonVariant.Icon;
            buttons.Children.Add(
                Pad(
                    new ChamferButton
                    {
                        Variant = variant,
                        Label = icon ? "" : variant.ToString().ToUpperInvariant(),
                        Icon =
                            icon ? SaIconKind.Gear
                            : variant == ButtonVariant.Primary ? SaIconKind.Diamond
                            : SaIconKind.None,
                    }
                )
            );
            buttons.Children.Add(
                Pad(
                    new ChamferButton
                    {
                        Variant = variant,
                        Label = icon ? "" : "DISABLED",
                        Icon = icon ? SaIconKind.Cross : SaIconKind.None,
                        IsEnabled = false,
                    }
                )
            );
        }
        page.Children.Add(Section("CHAMFER BUTTONS", buttons));

        var pills = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var tone in Enum.GetValues<Tone>())
        {
            var pill = new StatusPill();
            pill.Configure(
                tone.ToString().ToUpperInvariant(),
                Sa.ToneColor(tone),
                tone == Tone.Ok ? SaIconKind.Dot : SaIconKind.Diamond,
                pulse: false
            );
            pills.Children.Add(Pad(pill));
        }
        page.Children.Add(Section("STATUS PILLS", pills));

        var icons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var kind in Enum.GetValues<SaIconKind>())
            if (kind != SaIconKind.None)
                icons.Children.Add(
                    Pad(
                        new SaIcon
                        {
                            Kind = kind,
                            Width = 18,
                            Height = 18,
                            Foreground = Sa.Accent,
                        }
                    )
                );
        page.Children.Add(Section("ICONS (vector — Saira has no symbol glyphs)", icons));

        var bars = new StackPanel
        {
            Spacing = 10,
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (
            var (mode, percent, color) in new[]
            {
                (BarMode.Fill, 62, DesignTokens.TeamAccent),
                (BarMode.Full, 100, DesignTokens.Ok),
                (BarMode.Sweep, 0, DesignTokens.TeamAccent),
                (BarMode.Full, 100, DesignTokens.Danger),
            }
        )
        {
            var bar = new ProgressSweepBar();
            bar.Configure(mode, percent, color);
            bars.Children.Add(bar);
        }
        page.Children.Add(Section("PROGRESS SWEEP BAR", bars));

        var alerts = new StackPanel
        {
            Spacing = 8,
            Width = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var tone in new[] { Tone.Danger, Tone.Warn, Tone.Ok })
        {
            var alert = new AlertBox();
            alert.Configure(
                $"{tone.ToString().ToUpperInvariant()} ALERT TITLE",
                "Sentence-case body text in mono, wrapped when it runs long enough to need it.",
                Sa.ToneColor(tone),
                tone == Tone.Danger
            );
            alerts.Children.Add(alert);
        }
        page.Children.Add(Section("ALERT BOX", alerts));

        var surfaces = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        surfaces.Children.Add(
            new BracketPanel
            {
                Width = 240,
                Height = 110,
                Child = Sa.Text("BracketPanel", TextStyle.Body),
            }
        );
        surfaces.Children.Add(
            new BracketPanel
            {
                Width = 240,
                Height = 110,
                Accent = Sa.Danger,
                Child = Sa.Text("…accent follows state", TextStyle.Body),
            }
        );
        surfaces.Children.Add(
            new HairlinePanel
            {
                Width = 280,
                Height = 110,
                Title = "HAIRLINE PANEL",
                Child = Sa.Text("slanted tab header", TextStyle.Body),
            }
        );
        page.Children.Add(Section("SURFACES", surfaces));

        Content = new Panel
        {
            Children =
            {
                new Backdrop(),
                new ScrollViewer { Content = page },
            },
        };
    }

    private static Control Section(string title, Control content) =>
        new StackPanel { Spacing = 8, Children = { Sa.Text(title, TextStyle.Label, DesignTokens.Data), content } };

    private static StackPanel Column(params Control[] children)
    {
        var column = new StackPanel { Spacing = 4 };
        column.Children.AddRange(children);
        return column;
    }

    private static Control Pad(Control control)
    {
        control.Margin = new Thickness(0, 0, 12, 10);
        return control;
    }
}
