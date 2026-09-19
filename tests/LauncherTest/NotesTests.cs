using StellarAllegiance.Launcher.Notes;

// NotesParser turns UNTRUSTED release-note markdown into a bounded block model for the Avalonia
// view to render. These checks lean heavily on the two formatting helpers below (FmtBlock/FmtBlocks)
// so a mismatch prints a readable one-line diff instead of a raw record dump — NotesBlock/NotesInline
// equality can't be used directly here since NotesBlock.Inlines is a plain List<> (reference, not
// value, equality).
static class NotesTests
{
    private static string FmtInline(NotesInline x) =>
        x.Kind switch
        {
            NotesInlineKind.Link => $"Link({x.Text}->{x.Url})",
            NotesInlineKind.Code => $"Code({x.Text})",
            NotesInlineKind.Bold => $"Bold({x.Text})",
            NotesInlineKind.Italic => $"Italic({x.Text})",
            _ => $"Text({x.Text})",
        };

    private static string FmtBlock(NotesBlock b) =>
        b.Inlines.Count == 0
            ? $"{b.Kind}[{b.Level}]"
            : $"{b.Kind}[{b.Level}]: {string.Join(" + ", b.Inlines.Select(FmtInline))}";

    private static string FmtBlocks(IReadOnlyList<NotesBlock> blocks) => string.Join(" || ", blocks.Select(FmtBlock));

    public static void Run()
    {
        T.Section("NotesParser: headings");
        T.Eq(
            "Heading[1]: Text(H1) || Heading[2]: Text(H2) || Heading[3]: Text(H3) || Heading[3]: Text(H4) || Heading[3]: Text(H6) || Heading[2]: Text(Trimmed)",
            FmtBlocks(
                NotesParser.Parse(string.Join("\n", "# H1", "## H2", "### H3", "#### H4", "###### H6", "## Trimmed ##"))
            ),
            "levels 1-3 pass through, deeper headings clamp to 3, trailing #'s are stripped"
        );
        T.Eq(
            "Paragraph[0]: Text(#NoSpace)",
            FmtBlocks(NotesParser.Parse("#NoSpace")),
            "a '#' with no following space is not a heading — it falls through to a literal paragraph"
        );

        T.Section("NotesParser: paragraphs");
        T.Eq(
            "Paragraph[0]: Text(Line one Line two Line three)",
            FmtBlocks(NotesParser.Parse(string.Join("\n", "Line one", "Line two", "Line three"))),
            "consecutive non-blank lines join into one paragraph with single spaces"
        );
        T.Eq(
            "Paragraph[0]: Text(Para A) || Paragraph[0]: Text(Para B)",
            FmtBlocks(NotesParser.Parse(string.Join("\n", "Para A", "", "Para B"))),
            "a blank line separates paragraphs"
        );
        T.Eq(
            FmtBlocks(NotesParser.Parse(string.Join("\n", "Para A", "", "Para B"))),
            FmtBlocks(NotesParser.Parse(string.Join("\r\n", "Para A", "", "Para B"))),
            "CRLF input parses identically to LF input"
        );

        T.Section("NotesParser: bullets");
        T.Eq(
            "Bullet[0]: Text(top) || Bullet[1]: Text(nested1) || Bullet[2]: Text(nested2) || Bullet[2]: Text(nested3) || Bullet[0]: Text(ordered one) || Bullet[0]: Text(ordered ten)",
            FmtBlocks(
                NotesParser.Parse(
                    string.Join(
                        "\n",
                        "* top",
                        "  * nested1",
                        "    * nested2",
                        "      * nested3",
                        "1. ordered one",
                        "10. ordered ten"
                    )
                )
            ),
            "nesting = leading spaces / 2 clamped 0-2; ordered items are bullets too, with the number stripped"
        );
        T.Eq(
            "Bullet[0]: Text(first item continued text) || Bullet[0]: Text(second item) || Paragraph[0]: Text(not continued)",
            FmtBlocks(
                NotesParser.Parse(string.Join("\n", "* first item", "  continued text", "* second item", "not continued"))
            ),
            "an indented non-bullet line continues the previous bullet; an unindented one ends it and starts a paragraph"
        );
        T.Eq(
            "Bullet[1]: Text(tab nested)",
            FmtBlocks(NotesParser.Parse("\t* tab nested")),
            "a leading tab counts as 2 spaces of indent"
        );

        T.Section("NotesParser: code fences");
        {
            string fenced = string.Join("\n", "```csharp", "**bold** <tag>html</tag> \\*esc\\*", "plain second line", "```");
            var codeBlocks = NotesParser.Parse(fenced);
            string expectedCode = string.Join("\n", "**bold** <tag>html</tag> \\*esc\\*", "plain second line");
            T.Eq(1, codeBlocks.Count, "a fenced block (with a language tag) parses to exactly one block");
            T.Check(codeBlocks.Count == 1 && codeBlocks[0].Kind == NotesBlockKind.Code, "...and it's a Code block");
            T.Eq(
                expectedCode,
                codeBlocks.Count == 1 && codeBlocks[0].Inlines.Count == 1 ? codeBlocks[0].Inlines[0].Text : "<missing>",
                "...bold/tag/backslash markup inside stays byte-for-byte literal, lines joined by \\n"
            );

            var unclosed = NotesParser.Parse(string.Join("\n", "```", "line one", "line two"));
            T.Eq(1, unclosed.Count, "an unclosed fence still yields one block");
            T.Check(unclosed.Count == 1 && unclosed[0].Kind == NotesBlockKind.Code, "...it's a Code block");
            T.Eq(
                "line one\nline two",
                unclosed.Count == 1 && unclosed[0].Inlines.Count == 1 ? unclosed[0].Inlines[0].Text : "<missing>",
                "...running to the end of the input"
            );
        }

        T.Section("NotesParser: rules");
        {
            var rules = NotesParser.Parse(string.Join("\n", "---", "***", "___", "- - -"));
            T.Eq(4, rules.Count, "---, ***, ___ and '- - -' each parse as one block");
            T.Check(
                rules.All(b => b.Kind == NotesBlockKind.Rule && b.Level == 0 && b.Inlines.Count == 0),
                "...all four are Rule blocks with no inlines"
            );
            T.Eq(
                "Paragraph[0]: Text(--)",
                FmtBlocks(NotesParser.Parse("--")),
                "only 2 dashes is short of the 3+ a rule needs, so it's literal paragraph text instead"
            );
        }

        T.Section("NotesParser: HTML comments");
        T.Eq(
            "Paragraph[0]: Text(Before) || Paragraph[0]: Text(After)",
            FmtBlocks(NotesParser.Parse(string.Join("\n", "Before", "<!-- hidden -->", "After"))),
            "a single-line HTML comment is removed entirely"
        );
        T.Eq(
            "Paragraph[0]: Text(Before) || Paragraph[0]: Text(After)",
            FmtBlocks(NotesParser.Parse(string.Join("\n", "Before", "<!--", "multi", "line", "comment", "-->", "After"))),
            "a multi-line HTML comment is removed entirely, wherever it spans"
        );

        T.Section("NotesParser: HTML tags and entities");
        T.Eq(
            "Paragraph[0]: Text(kept and also kept plus  done)",
            FmtBlocks(NotesParser.Parse("<b>kept</b> and <i>also kept</i> plus <img src=\"x.png\"/> done")),
            "tags (including a self-closing one) are stripped, their inner text is kept"
        );
        T.Eq(
            "Paragraph[0]: Text(Tom & Jerry <tag> \"quoted\" 'it's' fine here)",
            FmtBlocks(NotesParser.Parse("Tom &amp; Jerry &lt;tag&gt; &quot;quoted&quot; &#39;it&#39;s&#39; fine&nbsp;here")),
            "&amp; &lt; &gt; &quot; &#39; &nbsp; all decode, and a decoded '<' is never re-stripped as a tag"
        );

        T.Section("NotesParser: inline markup");
        T.Eq("Paragraph[0]: Bold(bold)", FmtBlocks(NotesParser.Parse("**bold**")), "**bold**");
        T.Eq("Paragraph[0]: Bold(bold2)", FmtBlocks(NotesParser.Parse("__bold2__")), "__bold2__");
        T.Eq("Paragraph[0]: Italic(italic)", FmtBlocks(NotesParser.Parse("*italic*")), "*italic*");
        T.Eq("Paragraph[0]: Italic(italic2)", FmtBlocks(NotesParser.Parse("_italic2_")), "_italic2_");
        T.Eq("Paragraph[0]: Code(code)", FmtBlocks(NotesParser.Parse("`code`")), "`code`");
        T.Eq(
            "Paragraph[0]: Link(text->https://example.com/page)",
            FmtBlocks(NotesParser.Parse("[text](https://example.com/page)")),
            "[text](https url) -> Link"
        );
        T.Eq(
            "Paragraph[0]: Text(snake_case_name)",
            FmtBlocks(NotesParser.Parse("snake_case_name")),
            "an underscore flanked by word characters on both sides is literal, not italic"
        );
        T.Eq(
            "Paragraph[0]: Text(a **b)",
            FmtBlocks(NotesParser.Parse("a **b")),
            "an unmatched ** stays literal instead of throwing away the markers"
        );
        T.Eq(
            "Paragraph[0]: Text(Click here)",
            FmtBlocks(NotesParser.Parse("[Click here](javascript:doStuff)")),
            "a link with a non-http(s) scheme keeps just the text"
        );
        T.Eq(
            "Paragraph[0]: Text(Screenshot)",
            FmtBlocks(NotesParser.Parse("![Screenshot](https://example.com/img.png)")),
            "an image keeps its alt text as plain Text, never a Link"
        );
        T.Eq(
            0,
            NotesParser.Parse("![](https://example.com/img.png)").Count,
            "an image with empty alt drops the whole (now-empty) paragraph"
        );
        T.Eq(
            "Paragraph[0]: Text(Visit ) + Link(https://example.com/page->https://example.com/page) + Text(, thanks)",
            FmtBlocks(NotesParser.Parse("Visit https://example.com/page, thanks")),
            "a bare URL's trailing comma is not part of the link"
        );
        T.Eq(
            "Paragraph[0]: Text(See ) + Link(#84->https://github.com/wivuu/stellarallegiance/pull/84) + Text( for details)",
            FmtBlocks(NotesParser.Parse("See https://github.com/wivuu/stellarallegiance/pull/84 for details")),
            "a bare GitHub PR url shortens its link text to #84"
        );
        T.Eq(
            "Paragraph[0]: Text(See ) + Link(#9->https://github.com/wivuu/stellarallegiance/issues/9) + Text( too)",
            FmtBlocks(NotesParser.Parse("See https://github.com/wivuu/stellarallegiance/issues/9 too")),
            "...same for a bare issues url"
        );
        T.Eq(
            "Paragraph[0]: Link(the turret PR->https://github.com/wivuu/stellarallegiance/pull/84)",
            FmtBlocks(NotesParser.Parse("[the turret PR](https://github.com/wivuu/stellarallegiance/pull/84)")),
            "...but an explicit [text](url) link keeps the author's own text, even for a PR url"
        );
        T.Eq(
            "Paragraph[0]: Text(*a* _b_ `c` [d #e)",
            FmtBlocks(NotesParser.Parse("\\*a\\* \\_b\\_ \\`c\\` \\[d \\#e")),
            "backslash escapes (\\* \\_ \\` \\[ \\#) all produce their literal character, never a marker"
        );
        T.Eq(
            "Paragraph[0]: Text(ab)",
            FmtBlocks(NotesParser.Parse("a![](http://x.com/i.png)b")),
            "an empty inline (dropped image alt) between two text runs still lets them merge into one Text"
        );
        T.Eq(
            "Paragraph[0]: Text(Fix ) + Bold(macOS) + Text( name)",
            FmtBlocks(NotesParser.Parse("Fix **macOS** name")),
            "bold in the middle of a sentence keeps the surrounding text as separate inlines"
        );

        T.Section("NotesParser: input bounds");
        T.Eq(0, NotesParser.Parse(null).Count, "null input -> empty list");
        T.Eq(0, NotesParser.Parse("").Count, "empty input -> empty list");
        T.Eq(0, NotesParser.Parse("   \n\t  \n  ").Count, "whitespace-only input -> empty list");

        {
            string huge = new string('a', 25_000);
            var hugeResult = NotesParser.Parse(huge);
            T.Eq(1, hugeResult.Count, "an over-length single-line input (no newline to prefer) still parses to one block");
            T.Eq(
                NotesParser.MaxInputChars,
                hugeResult.Count == 1 && hugeResult[0].Inlines.Count == 1 ? hugeResult[0].Inlines[0].Text.Length : -1,
                "...hard-truncated to exactly MaxInputChars"
            );
        }
        {
            string headPlusTail = new string('x', 19_990) + "\n" + new string('y', 50);
            var trResult = NotesParser.Parse(headPlusTail);
            string got =
                trResult.Count == 1 && trResult[0].Inlines.Count == 1 ? trResult[0].Inlines[0].Text : "<unexpected shape>";
            T.Eq(
                19_990,
                got.Length,
                "truncation prefers the last line boundary within MaxInputChars over a hard character cut"
            );
            T.Check(!got.Contains('y'), "...so the partial trailing line beyond that boundary is dropped, not kept");
        }
        {
            var many = new List<string>();
            for (int n = 0; n < 250; n++)
                many.Add($"* item {n}");
            var capped = NotesParser.Parse(string.Join("\n", many));
            T.Eq(
                NotesParser.MaxBlocks + 1,
                capped.Count,
                "250 bullets cap at MaxBlocks+1 blocks (200 real + the ellipsis marker)"
            );
            T.Check(
                capped.Take(NotesParser.MaxBlocks).All(b => b.Kind == NotesBlockKind.Bullet),
                "...the first MaxBlocks are all real bullets, in order"
            );
            T.Eq(NotesBlockKind.Paragraph, capped[^1].Kind, "...and the last block is the ellipsis paragraph");
            T.Eq("…", capped[^1].Inlines.Count == 1 ? capped[^1].Inlines[0].Text : "?", "...whose text is exactly \"…\"");
        }

        T.Section("NotesParser: realistic GitHub-generated body");
        {
            string body = string.Join(
                "\n",
                "<!-- Release notes generated using configuration in .github/release.yml -->",
                "## What's Changed",
                "* Implement crew-served turret mechanics by @onionhammer in https://github.com/wivuu/stellarallegiance/pull/84",
                "* Fix **macOS** app name in release workflow",
                "* Add offline interpolation test harness (`tests/InterpTest`)",
                "",
                "**Full Changelog**: https://github.com/wivuu/stellarallegiance/compare/v0.0.11...v0.0.12"
            );
            var real = NotesParser.Parse(body);

            T.Eq(5, real.Count, "heading + 3 bullets + trailing paragraph; the comment contributes no block");
            T.Check(
                real.Count > 0
                    && real[0].Kind == NotesBlockKind.Heading
                    && FmtBlock(real[0]) == "Heading[2]: Text(What's Changed)",
                "the heading is \"What's Changed\""
            );
            T.Eq(3, real.Count(b => b.Kind == NotesBlockKind.Bullet), "exactly three bullets");
            var prBullet = real.FirstOrDefault(b =>
                b.Kind == NotesBlockKind.Bullet && b.Inlines.Any(x => x.Kind == NotesInlineKind.Link && x.Text == "#84")
            );
            T.Check(
                prBullet != null && prBullet.Inlines.Any(x => x.Url == "https://github.com/wivuu/stellarallegiance/pull/84"),
                "the PR link is shortened to #84 (full url preserved on the Link)"
            );
            T.Check(
                real.Any(b => b.Inlines.Any(x => x.Kind == NotesInlineKind.Bold && x.Text == "macOS")),
                "the bold **macOS** markup survives as a Bold inline"
            );
            T.Check(
                !NotesParser.ToPlainText(real).Contains("configuration in"),
                "the HTML comment left no trace in the rendered output"
            );
        }

        T.Section("NotesParser: ToPlainText");
        T.Eq(
            "Title\n• one\n• two\npara text",
            NotesParser.ToPlainText(NotesParser.Parse(string.Join("\n", "# Title", "* one", "* two", "para text"))),
            "headings plain, bullets prefixed with •, blocks newline-separated"
        );

        T.Section("NotesParser: robustness (fuzz)");
        {
            // The exact markup alphabet called out in the task, plus letters/space/newline.
            const string alphabet = "*_`[]()<>#-!\\abcdefghijklmnopqrstuvwxyz  \n";
            var rng = new Random(20260919); // fixed seed: deterministic, reproducible failures
            bool ok = true;
            string? firstFailure = null;
            for (int n = 0; n < 2000; n++)
            {
                int length = rng.Next(0, 300);
                var chars = new char[length];
                for (int k = 0; k < length; k++)
                    chars[k] = alphabet[rng.Next(alphabet.Length)];
                string input = new string(chars);
                try
                {
                    var result = NotesParser.Parse(input);
                    if (result.Count > NotesParser.MaxBlocks + 1)
                    {
                        ok = false;
                        firstFailure = $"iteration {n}: {result.Count} blocks";
                        break;
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    firstFailure = $"iteration {n} threw {ex.GetType().Name}: {ex.Message}";
                    break;
                }
            }
            T.Check(
                ok,
                $"2000 fixed-seed pseudo-random markup-heavy strings never throw and never exceed MaxBlocks+1 blocks{(firstFailure is null ? "" : $" ({firstFailure})")}"
            );
        }
    }
}
