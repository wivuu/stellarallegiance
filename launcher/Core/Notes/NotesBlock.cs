namespace StellarAllegiance.Launcher.Notes;

// Render-ready block model produced by NotesParser. Deliberately flat and DTO-like: the Avalonia
// view walks these to build TextBlock inlines in code, so it needs no markdown knowledge and this
// model needs no UI knowledge.

public enum NotesBlockKind
{
    Heading,
    Paragraph,
    Bullet,
    Code,
    Rule,
}

public enum NotesInlineKind
{
    Text,
    Bold,
    Italic,
    Code,
    Link,
}

// Url is only ever set for Link; every other kind ignores it.
public sealed record NotesInline(NotesInlineKind Kind, string Text, string? Url = null);

// Level means: heading depth 1-3 (a deeper "####…" clamps to 3), bullet nesting depth 0-2 (deeper
// indentation clamps to 2), 0 for everything else. A Code block carries exactly one Text inline
// holding the raw fenced lines joined by "\n" — never inline-parsed, see NotesParser. Rule carries
// no inlines at all.
public sealed record NotesBlock(NotesBlockKind Kind, int Level, IReadOnlyList<NotesInline> Inlines);
