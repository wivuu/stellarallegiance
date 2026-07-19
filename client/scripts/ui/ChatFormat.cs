using System;
using Godot;
using StellarAllegiance.Net;

namespace StellarAllegiance.Ui;

// Shared BBCode rendering for a single ChatLine, factored out of Chat.FormatLine (in-flight chat
// overlay) and Lobby.RebuildComms (Game Lobby comms panel) — both built the same shape by hand:
// dim timestamp; muted-diamond system line; gold "★ CMDR {name} ▸ {text}" for a scope-2 commander
// directive (byte-identical string in both callers); a scope-1 tag; team-coloured name.
//
// The two callers aren't quite identical though, so those differences stay as parameters instead
// of being papered over:
//   - how a "system" line is detected (Chat carries an explicit flag; Lobby infers it from an
//     empty Name)
//   - what colour a FromTeam resolves to (Lobby also handles the NOAT pseudo-team; Chat doesn't)
//   - what the scope-1 tag reads for a given team ("team" vs Lobby's "team"/"noat")
//   - whether the message text itself gets an explicit colour wrap (Lobby wraps it in TextHi;
//     Chat relies on its RichTextLabel's default_color already being TextHi)
public static class ChatFormat
{
    public static string Escape(string? s) => string.IsNullOrEmpty(s) ? "" : s.Replace("[", "[lb]");

    public static string ToBbcode(
        ChatLine line,
        string time,
        bool isSystem,
        Func<int, Color> nameColorForTeam,
        Func<int, string> teamTagLabel,
        Color? messageColor = null
    )
    {
        string dimHex = DesignTokens.TextDim.ToHtml(false);
        string muteHex = DesignTokens.Text2.ToHtml(false);
        string goldHex = DesignTokens.CmdrGold.ToHtml(false);

        string stamp = $"[color=#{dimHex}]{time}[/color]";

        // System lines (slash-command output / locally-generated notes): a muted diamond-prefixed
        // note, no team-name coloring.
        if (isSystem)
            return $"{stamp} [color=#{muteHex}]◆ {Escape(line.Text)}[/color]";

        // Scope 2 = commander order directive (v34): the whole line gold so an order reads as an
        // order, not chatter. Name = the issuing commander.
        if (line.Scope == 2)
            return $"{stamp} [color=#{goldHex}]★ CMDR {Escape(line.Name)} ▸ {Escape(line.Text)}[/color]";

        Color nameColor = nameColorForTeam(line.FromTeam);
        string tag = line.Scope == 1 ? $"[color=#{muteHex}]\\[{teamTagLabel(line.FromTeam)}][/color] " : "";
        string name = $"[color=#{nameColor.ToHtml(false)}]{Escape(line.Name)}[/color]";
        string text = messageColor is Color c
            ? $"[color=#{c.ToHtml(false)}]{Escape(line.Text)}[/color]"
            : Escape(line.Text);
        return $"{stamp} {tag}{name}: {text}";
    }
}
