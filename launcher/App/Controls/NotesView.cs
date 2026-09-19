using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Notes;
using StellarAllegiance.Launcher.Theme;
using StellarAllegiance.Launcher.Update;
using StellarAllegiance.Ui;
using TextStyle = StellarAllegiance.Launcher.Theme.TextStyle;

namespace StellarAllegiance.Launcher.Controls;

// Renders release notes (GitHub release bodies carried in the Velopack feed — UNTRUSTED text) from the
// small block model NotesParser produces. Built in code, inline by inline: no markdown library, no HTML,
// and links are styled text only — nothing from the feed is ever clickable. The one outbound link in the
// launcher is a button that opens a URL the launcher builds itself.
public sealed class NotesView : StackPanel
{
    public NotesView() => Spacing = 6;

    public void Show(IReadOnlyList<ReleaseNote> notes)
    {
        Children.Clear();
        if (notes.Count == 0)
        {
            Children.Add(Sa.Text(LauncherCopy.NoNotes, TextStyle.Label, DesignTokens.TextDim));
            return;
        }

        bool many = notes.Count > 1;
        foreach (var note in notes)
        {
            if (many)
                Children.Add(Sa.Text($"v{note.Version}", TextStyle.Data, DesignTokens.TeamAccent));
            foreach (var block in NotesParser.Parse(note.Markdown))
                Children.Add(Render(block));
            if (many)
                Children.Add(new DiamondDivider { Margin = new Thickness(0, 4) });
        }
    }

    private static Control Render(NotesBlock block)
    {
        switch (block.Kind)
        {
            case NotesBlockKind.Rule:
                return new DiamondDivider();

            case NotesBlockKind.Code:
            {
                var code = Sa.Text(
                    block.Inlines.Count > 0 ? block.Inlines[0].Text : "",
                    TextStyle.Data,
                    size: DesignTokens.CaptionSize + 1
                );
                code.TextWrapping = TextWrapping.Wrap;
                return new Border
                {
                    Background = Sa.Well,
                    BorderBrush = Sa.BorderLo,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10),
                    Child = code,
                };
            }

            case NotesBlockKind.Heading:
            {
                var heading = Sa.Text(
                    "",
                    block.Level <= 1 ? TextStyle.Title : TextStyle.Label,
                    block.Level <= 1 ? null : DesignTokens.Data
                );
                heading.Margin = new Thickness(0, 6, 0, 0);
                Fill(heading, block, upper: block.Level > 1);
                return heading;
            }

            case NotesBlockKind.Bullet:
            {
                var text = Sa.Text("", TextStyle.Body, DesignTokens.Text2, size: DesignTokens.DataSize);
                Fill(text, block, upper: false);
                var mark = new SaIcon
                {
                    Kind = SaIconKind.Diamond,
                    Width = 6,
                    Height = 6,
                    Foreground = Sa.Accent,
                    Margin = new Thickness(0, 7, 9, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                };
                var row = new DockPanel { Margin = new Thickness(block.Level * 16, 0, 0, 0) };
                DockPanel.SetDock(mark, Dock.Left);
                row.Children.Add(mark);
                row.Children.Add(text);
                return row;
            }

            default:
            {
                var paragraph = Sa.Text("", TextStyle.Body, DesignTokens.Text2, size: DesignTokens.DataSize);
                Fill(paragraph, block, upper: false);
                return paragraph;
            }
        }
    }

    private static void Fill(TextBlock target, NotesBlock block, bool upper)
    {
        target.TextWrapping = TextWrapping.Wrap;
        target.Inlines ??= [];
        foreach (var inline in block.Inlines)
        {
            string text = upper ? inline.Text.ToUpperInvariant() : inline.Text;
            var run = new Run(text);
            switch (inline.Kind)
            {
                case NotesInlineKind.Bold:
                    run.FontWeight = FontWeight.SemiBold;
                    run.Foreground = Sa.TextHi;
                    break;
                case NotesInlineKind.Italic:
                    run.FontStyle = FontStyle.Italic;
                    break;
                case NotesInlineKind.Code:
                    run.FontFamily = Sa.Mono;
                    run.Foreground = Sa.Data;
                    break;
                case NotesInlineKind.Link:
                    run.Foreground = Sa.Data; // styled, deliberately NOT clickable (untrusted feed text)
                    break;
            }
            target.Inlines.Add(run);
        }
    }
}
