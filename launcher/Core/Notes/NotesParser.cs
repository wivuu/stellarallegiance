using System.Text;

namespace StellarAllegiance.Launcher.Notes;

// A tiny, safe subset-of-Markdown parser for "PATCH NOTES" / "WHAT'S NEW" text pulled from GitHub
// release bodies embedded in the update feed. That text is UNTRUSTED (it comes from the network)
// and is only ever rendered later, so this type has exactly one job: turn it into a bounded list of
// NotesBlock without ever throwing. Two hard limits enforce "bounded": MaxInputChars caps the raw
// text before any parsing happens, MaxBlocks caps the parsed output regardless of input shape.
//
// This is intentionally NOT a general Markdown engine: no nested inlines (bold-inside-italic etc.),
// no reference-style links, no tables, no nested lists beyond a clamped depth. Every rule below is
// line-based and tolerant — malformed input degrades to plain text rather than erroring.
public static class NotesParser
{
    public const int MaxInputChars = 20_000;
    public const int MaxBlocks = 200;

    private const string HttpsScheme = "https://";
    private const string HttpScheme = "http://";
    private const string GitHubPrefix = "https://github.com/";

    public static IReadOnlyList<NotesBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        string text = markdown.Replace("\r\n", "\n").Replace("\r", "\n");

        if (text.Length > MaxInputChars)
        {
            text = text[..MaxInputChars];
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline > 0) // prefer cutting at a line boundary when the truncation window has one
                text = text[..lastNewline];
        }

        // Comments can span lines ("<!--\n…\n-->"), so strip them on the whole text before splitting
        // into lines rather than trying to track fence-like open/close state per line.
        text = RemoveHtmlComments(text);

        var blocks = new List<NotesBlock>();
        bool capped = false;

        // Single choke point for adding a block: once MaxBlocks is reached, append the "…" marker
        // exactly once and latch `capped` so every later call (and the line loop itself) becomes a
        // no-op. Keeps the cap logic in one place instead of scattered through every emit site.
        void Emit(NotesBlock block)
        {
            if (capped)
                return;
            blocks.Add(block);
            if (blocks.Count >= MaxBlocks)
            {
                blocks.Add(new NotesBlock(NotesBlockKind.Paragraph, 0, [new NotesInline(NotesInlineKind.Text, "…")]));
                capped = true;
            }
        }

        string? pendingParagraph = null;
        string? pendingBulletText = null;
        int pendingBulletLevel = 0;
        bool inFence = false;
        var codeLines = new List<string>();

        void FlushParagraph()
        {
            if (pendingParagraph is { } p)
            {
                var inlines = ParseInlines(p);
                if (inlines.Count > 0)
                    Emit(new NotesBlock(NotesBlockKind.Paragraph, 0, inlines));
                pendingParagraph = null;
            }
        }

        void FlushBullet()
        {
            if (pendingBulletText is { } b)
            {
                var inlines = ParseInlines(b);
                if (inlines.Count > 0)
                    Emit(new NotesBlock(NotesBlockKind.Bullet, pendingBulletLevel, inlines));
                pendingBulletText = null;
            }
        }

        // Paragraph and bullet accumulation are mutually exclusive (starting one always flushes the
        // other first), so flushing both here is just "flush whichever is actually pending".
        void FlushPending()
        {
            FlushParagraph();
            FlushBullet();
        }

        foreach (string rawLine in text.Split('\n'))
        {
            if (capped)
                break;

            if (inFence)
            {
                if (IsFenceMarker(rawLine))
                {
                    Emit(
                        new NotesBlock(
                            NotesBlockKind.Code,
                            0,
                            [new NotesInline(NotesInlineKind.Text, string.Join("\n", codeLines))]
                        )
                    );
                    inFence = false;
                }
                else
                {
                    codeLines.Add(rawLine); // raw: no tag-stripping, no entity decoding, no inline parsing
                }
                continue;
            }

            string line = CleanLine(rawLine);

            if (line.Trim().Length == 0)
            {
                FlushPending();
                continue;
            }

            if (IsFenceMarker(line))
            {
                FlushPending();
                inFence = true;
                codeLines.Clear();
                continue;
            }

            if (IsHorizontalRule(line))
            {
                FlushPending();
                Emit(new NotesBlock(NotesBlockKind.Rule, 0, []));
                continue;
            }

            if (TryParseHeading(line, out int headingLevel, out string headingText))
            {
                FlushPending();
                var inlines = ParseInlines(headingText);
                if (inlines.Count > 0)
                    Emit(new NotesBlock(NotesBlockKind.Heading, headingLevel, inlines));
                continue;
            }

            if (TryParseBullet(line, out int bulletLevel, out string bulletText))
            {
                FlushPending();
                pendingBulletText = bulletText;
                pendingBulletLevel = bulletLevel;
                continue;
            }

            // Plain text: continues whichever block is open, or opens a fresh paragraph. A bullet
            // only swallows a CONTINUATION line when that line is itself indented; an unindented
            // line ends the bullet and starts a new paragraph instead.
            bool indented = line.Length > 0 && (line[0] == ' ' || line[0] == '\t');
            string trimmedText = line.Trim();

            if (pendingBulletText != null)
            {
                if (indented)
                    pendingBulletText = pendingBulletText + " " + trimmedText;
                else
                {
                    FlushBullet();
                    pendingParagraph = trimmedText;
                }
            }
            else if (pendingParagraph != null)
                pendingParagraph = pendingParagraph + " " + trimmedText;
            else
                pendingParagraph = trimmedText;
        }

        if (!capped)
        {
            if (inFence) // unclosed fence: runs to the end of the input
                Emit(
                    new NotesBlock(
                        NotesBlockKind.Code,
                        0,
                        [new NotesInline(NotesInlineKind.Text, string.Join("\n", codeLines))]
                    )
                );
            FlushPending();
        }

        return blocks;
    }

    // Used for logs and tests: headings/paragraphs/code on their own line, bullets prefixed with
    // "• ", every block separated by "\n".
    public static string ToPlainText(IReadOnlyList<NotesBlock> blocks)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');

            NotesBlock block = blocks[i];
            if (block.Kind == NotesBlockKind.Bullet)
                sb.Append("• ");
            else if (block.Kind == NotesBlockKind.Rule)
                sb.Append("---");

            foreach (var inline in block.Inlines)
                sb.Append(inline.Text);
        }
        return sb.ToString();
    }

    private static bool IsFenceMarker(string line) => line.TrimStart(' ', '\t').StartsWith("```", StringComparison.Ordinal);

    private static bool IsHorizontalRule(string line)
    {
        string compact = line.Replace(" ", "").Replace("\t", "");
        if (compact.Length < 3)
            return false;
        char marker = compact[0];
        if (marker != '-' && marker != '*' && marker != '_')
            return false;
        foreach (char c in compact)
        {
            if (c != marker)
                return false;
        }
        return true;
    }

    private static bool TryParseHeading(string line, out int level, out string text)
    {
        string trimmed = line.TrimStart(' ', '\t');
        int hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
            hashes++;

        if (hashes == 0 || hashes >= trimmed.Length || trimmed[hashes] != ' ')
        {
            level = 0;
            text = "";
            return false;
        }

        level = Math.Clamp(hashes, 1, 3);
        string rest = trimmed[(hashes + 1)..].Trim();
        int end = rest.Length;
        while (end > 0 && rest[end - 1] == '#') // trailing "##" closer, e.g. "## Title ##"
            end--;
        text = rest[..end].TrimEnd();
        return true;
    }

    private static bool TryParseBullet(string line, out int level, out string text)
    {
        int indent = 0;
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            indent += line[i] == '\t' ? 2 : 1;
            i++;
        }

        level = 0;
        text = "";
        if (i >= line.Length)
            return false;

        char marker = line[i];
        int markerLen;
        if ((marker == '*' || marker == '-' || marker == '+') && i + 1 < line.Length && line[i + 1] == ' ')
        {
            markerLen = 2;
        }
        else if (char.IsAsciiDigit(marker))
        {
            int j = i;
            while (j < line.Length && char.IsAsciiDigit(line[j]))
                j++;
            if (j >= line.Length || line[j] != '.' || j + 1 >= line.Length || line[j + 1] != ' ')
                return false;
            markerLen = j - i + 2; // digits + '.' + ' '
        }
        else
        {
            return false;
        }

        level = Math.Clamp(indent / 2, 0, 2);
        text = line[(i + markerLen)..].Trim();
        return true;
    }

    // Strips HTML tags (keeping their inner text) and decodes the handful of entities GitHub bodies
    // actually use. Applied to every non-fenced line before block classification; fenced code lines
    // skip this entirely so markup inside a code sample stays byte-for-byte literal.
    private static string CleanLine(string line) => DecodeEntities(StripHtmlTags(line));

    private static string StripHtmlTags(string line)
    {
        if (!line.Contains('<'))
            return line;

        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            // Require a letter or '/' right after '<' so a bare "a < b" comparison is never mistaken
            // for a tag; comments ("<!--") are already gone by the time this runs.
            if (c == '<' && i + 1 < line.Length && (char.IsLetter(line[i + 1]) || line[i + 1] == '/'))
            {
                int close = line.IndexOf('>', i + 1);
                if (close >= 0)
                {
                    i = close + 1;
                    continue;
                }
                // No '>' anywhere in the rest of the line, so nothing later can close as a tag
                // either — stop looking instead of re-scanning the same tail from every remaining
                // '<' (which would be O(n^2) on input like "<a<a<a<a…" with no closing bracket).
                sb.Append(line, i, line.Length - i);
                break;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static readonly (string Entity, string Value)[] HtmlEntities =
    [
        ("&amp;", "&"),
        ("&lt;", "<"),
        ("&gt;", ">"),
        ("&quot;", "\""),
        ("&#39;", "'"),
        ("&nbsp;", " "),
    ];

    private static string DecodeEntities(string line)
    {
        if (!line.Contains('&'))
            return line;

        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == '&')
            {
                bool matched = false;
                foreach (var (entity, value) in HtmlEntities)
                {
                    if (i + entity.Length <= line.Length && string.CompareOrdinal(line, i, entity, 0, entity.Length) == 0)
                    {
                        sb.Append(value);
                        i += entity.Length;
                        matched = true;
                        break;
                    }
                }
                if (matched)
                    continue;
            }
            sb.Append(line[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsEscapable(char c) => c is '*' or '_' or '`' or '[' or '#';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    // Scans "[inner](dest)" starting at the '[' position, with no nested-bracket/paren handling —
    // the first ']' and first ')' win. That is enough for the flat, single-line text this parser
    // ever sees and keeps the scan simple and always-terminating.
    private static bool TryParseBracketParen(
        string s,
        int bracketStart,
        out int innerStart,
        out int innerEnd,
        out (int Start, int End) dest,
        out int after
    )
    {
        innerStart = bracketStart + 1;
        innerEnd = 0;
        dest = (0, 0);
        after = 0;

        int closeBracket = s.IndexOf(']', innerStart);
        if (closeBracket < 0 || closeBracket + 1 >= s.Length || s[closeBracket + 1] != '(')
            return false;

        int destStart = closeBracket + 2;
        int closeParen = s.IndexOf(')', destStart);
        if (closeParen < 0)
            return false;

        innerEnd = closeBracket;
        dest = (destStart, closeParen);
        after = closeParen + 1;
        return true;
    }

    private static bool MatchesUrlScheme(string s, int i, out int schemeLen)
    {
        if (i + HttpsScheme.Length <= s.Length && string.CompareOrdinal(s, i, HttpsScheme, 0, HttpsScheme.Length) == 0)
        {
            schemeLen = HttpsScheme.Length;
            return true;
        }
        if (i + HttpScheme.Length <= s.Length && string.CompareOrdinal(s, i, HttpScheme, 0, HttpScheme.Length) == 0)
        {
            schemeLen = HttpScheme.Length;
            return true;
        }
        schemeLen = 0;
        return false;
    }

    // https://github.com/<owner>/<repo>/(pull|issues)/<digits> exactly — no trailing slash or extra
    // path segments — shortens to "#<n>" when used as a BARE url (an explicit [text](url) link
    // keeps the author's own text).
    private static bool TryGitHubIssueOrPr(string url, out string number)
    {
        number = "";
        if (!url.StartsWith(GitHubPrefix, StringComparison.Ordinal))
            return false;

        string[] parts = url[GitHubPrefix.Length..].Split('/');
        if (parts.Length != 4 || parts[0].Length == 0 || parts[1].Length == 0)
            return false;
        if (parts[2] != "pull" && parts[2] != "issues")
            return false;
        if (parts[3].Length == 0)
            return false;

        foreach (char c in parts[3])
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }
        number = parts[3];
        return true;
    }

    // Left-to-right inline scan. No nesting beyond the rules below: content captured between a pair
    // of markers is used as literal text, never re-scanned. Every branch that fails to find its
    // closing marker falls back to consuming exactly one literal character and retrying — that is
    // what makes unmatched markers "stay literal" and guarantees the scan always terminates.
    private static List<NotesInline> ParseInlines(string s)
    {
        var raw = new List<NotesInline>();
        var text = new StringBuilder();
        int len = s.Length;

        // Without any ']' at all, no [text](url) or ![alt](url) can ever match — skip straight to
        // literal handling for '[' / '!' rather than re-scanning to the end from every one (avoids
        // an O(n^2) rescan on adversarial input like "[[[[[[[[…" with no closing bracket at all).
        bool hasCloseBracket = s.Contains(']');

        void FlushText()
        {
            if (text.Length > 0)
            {
                raw.Add(new NotesInline(NotesInlineKind.Text, text.ToString()));
                text.Clear();
            }
        }

        int i = 0;
        while (i < len)
        {
            char c = s[i];

            if (c == '\\' && i + 1 < len && IsEscapable(s[i + 1]))
            {
                text.Append(s[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int close = s.IndexOf('`', i + 1);
                if (close >= 0)
                {
                    FlushText();
                    raw.Add(new NotesInline(NotesInlineKind.Code, s[(i + 1)..close]));
                    i = close + 1;
                    continue;
                }
                text.Append(c);
                i++;
                continue;
            }

            if (c == '*' && i + 1 < len && s[i + 1] == '*')
            {
                int close = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close >= 0)
                {
                    FlushText();
                    raw.Add(new NotesInline(NotesInlineKind.Bold, s[(i + 2)..close]));
                    i = close + 2;
                    continue;
                }
                text.Append(c);
                i++;
                continue;
            }

            if (c == '_' && i + 1 < len && s[i + 1] == '_')
            {
                int close = s.IndexOf("__", i + 2, StringComparison.Ordinal);
                if (close >= 0)
                {
                    FlushText();
                    raw.Add(new NotesInline(NotesInlineKind.Bold, s[(i + 2)..close]));
                    i = close + 2;
                    continue;
                }
                text.Append(c);
                i++;
                continue;
            }

            if (c == '*')
            {
                int close = s.IndexOf('*', i + 1);
                if (close >= 0)
                {
                    FlushText();
                    raw.Add(new NotesInline(NotesInlineKind.Italic, s[(i + 1)..close]));
                    i = close + 1;
                    continue;
                }
                text.Append(c);
                i++;
                continue;
            }

            if (c == '_')
            {
                // Underscore only opens/closes at a word boundary, so "snake_case_name" stays
                // literal — CommonMark's intraword rule for '_' (which '*' deliberately skips).
                bool leftFlank = i == 0 || !IsWordChar(s[i - 1]);
                int close = -1;
                if (leftFlank)
                {
                    for (int j = i + 1; j < len; j++)
                    {
                        if (s[j] == '_' && (j + 1 >= len || !IsWordChar(s[j + 1])))
                        {
                            close = j;
                            break;
                        }
                    }
                }
                if (close >= 0)
                {
                    FlushText();
                    raw.Add(new NotesInline(NotesInlineKind.Italic, s[(i + 1)..close]));
                    i = close + 1;
                    continue;
                }
                text.Append(c);
                i++;
                continue;
            }

            if (c == '!' && hasCloseBracket && i + 1 < len && s[i + 1] == '[')
            {
                if (TryParseBracketParen(s, i + 1, out int altStart, out int altEnd, out _, out int after))
                {
                    FlushText();
                    raw.Add(new NotesInline(NotesInlineKind.Text, s[altStart..altEnd])); // alt text only; the url is never surfaced
                    i = after;
                    continue;
                }
                text.Append(c);
                i++;
                continue;
            }

            if (c == '[' && hasCloseBracket)
            {
                if (
                    TryParseBracketParen(
                        s,
                        i,
                        out int txtStart,
                        out int txtEnd,
                        out (int Start, int End) dest,
                        out int after
                    )
                )
                {
                    FlushText();
                    string linkText = s[txtStart..txtEnd];
                    string url = s[dest.Start..dest.End];
                    raw.Add(
                        url.StartsWith(HttpScheme, StringComparison.Ordinal)
                        || url.StartsWith(HttpsScheme, StringComparison.Ordinal)
                            ? new NotesInline(NotesInlineKind.Link, linkText, url)
                            : new NotesInline(NotesInlineKind.Text, linkText) // non-http(s) scheme: keep the text, drop the url
                    );
                    i = after;
                    continue;
                }
                text.Append(c);
                i++;
                continue;
            }

            if (c == 'h' && MatchesUrlScheme(s, i, out int schemeLen))
            {
                int end = i + schemeLen;
                while (end < len && !char.IsWhiteSpace(s[end]) && s[end] != '<' && s[end] != '>')
                    end++;
                while (end > i + schemeLen && ".,);".IndexOf(s[end - 1]) >= 0) // trailing punctuation is never part of the url
                    end--;

                if (end > i + schemeLen)
                {
                    FlushText();
                    string url = s[i..end];
                    raw.Add(
                        TryGitHubIssueOrPr(url, out string number)
                            ? new NotesInline(NotesInlineKind.Link, "#" + number, url)
                            : new NotesInline(NotesInlineKind.Link, url, url)
                    );
                    i = end;
                    continue;
                }
            }

            text.Append(c);
            i++;
        }

        FlushText();

        // Merge adjacent Text (which can cascade once an empty inline between two Text runs is
        // dropped, e.g. "a" + Text("") from an empty image alt + "b" collapses to just "ab") and
        // drop every empty inline regardless of kind.
        var result = new List<NotesInline>(raw.Count);
        foreach (var inline in raw)
        {
            if (inline.Text.Length == 0)
                continue;
            if (result.Count > 0 && result[^1].Kind == NotesInlineKind.Text && inline.Kind == NotesInlineKind.Text)
                result[^1] = result[^1] with { Text = result[^1].Text + inline.Text };
            else
                result.Add(inline);
        }
        return result;
    }

    private static string RemoveHtmlComments(string text)
    {
        if (!text.Contains("<!--", StringComparison.Ordinal))
            return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf("<!--", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }
            sb.Append(text, i, start - i);
            int end = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0)
                break; // unterminated comment: drop the remainder (bounded, never throws)
            i = end + 3;
        }
        return sb.ToString();
    }
}
