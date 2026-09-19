using System.Text;

namespace StellarAllegiance.Launcher.Lobby;

// One dispatched Server-Sent-Event: the `event:` name (defaulted to "message" when the stream never
// sent one for this block) and the `data:` lines joined with "\n".
public readonly record struct SseEvent(string Event, string Data);

// Pure, allocation-light push parser for the subset of the SSE wire format public-lobby's
// `/servers/live` stream uses (public-lobby/PublicLobby.cs WriteSseEvent/WriteSseComment). Feed one
// line at a time (no trailing newline) and get back a dispatched event, or null while a block is
// still accumulating. No I/O, no threading, no allocation beyond the data buffer itself — the HTTP
// read loop (LobbyStatusClient) owns the network side entirely.
public sealed class SseLineParser
{
    private const string DefaultEventName = "message";

    private string _eventName = DefaultEventName;
    private readonly StringBuilder _data = new();
    private bool _hasData;

    // Feed exactly one line (as split on "\n" — a trailing "\r" from a CRLF stream is stripped here,
    // so callers don't need to pre-clean). Returns the dispatched event when this line was the blank
    // line ending a block that actually saw a `data:` field; null otherwise (including a blank line
    // that dispatches nothing because no data was accumulated — per spec that still clears state for
    // the next block, it just has nothing to report).
    public SseEvent? Feed(string line)
    {
        if (line.EndsWith('\r'))
            line = line[..^1];

        if (line.Length == 0)
        {
            SseEvent? dispatched = _hasData ? new SseEvent(_eventName, _data.ToString()) : null;
            Reset();
            return dispatched;
        }

        if (line[0] == ':')
            return null; // comment line, ignored

        int colon = line.IndexOf(':');
        string field;
        string value;
        if (colon < 0)
        {
            field = line;
            value = "";
        }
        else
        {
            field = line[..colon];
            value = line[(colon + 1)..];
            if (value.StartsWith(' ')) // exactly one optional leading space, never more
                value = value[1..];
        }

        switch (field)
        {
            case "event":
                _eventName = value;
                break;
            case "data":
                if (_hasData)
                    _data.Append('\n');
                _data.Append(value);
                _hasData = true;
                break;
            default:
                // id:/retry:/anything unrecognised — not part of what the status strip needs.
                break;
        }
        return null;
    }

    // Drops any in-progress (undispatched) block. Called between reconnect attempts so a line that
    // never got its terminating blank line on the old connection can't bleed into the new one.
    public void Reset()
    {
        _eventName = DefaultEventName;
        _data.Clear();
        _hasData = false;
    }

    // Accepts an arbitrary text chunk (a network read does not respect line boundaries) and yields
    // every event dispatched within it, buffering a trailing partial line for the next call.
    public IEnumerable<SseEvent> FeedChunk(string chunk)
    {
        string text = _pending.Length == 0 ? chunk : _pending + chunk;
        var events = new List<SseEvent>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            var dispatched = Feed(text[start..i]);
            if (dispatched is { } evt)
                events.Add(evt);
            start = i + 1;
        }
        _pending = text[start..];
        return events;
    }

    private string _pending = "";
}
