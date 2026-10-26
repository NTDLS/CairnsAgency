using System.Text.Encodings.Web;
using System.Text.Json;

namespace VPT.Einkr.CairnsAgency.Notes;

/// <summary>A session's durable notes -- a faithful port of the desktop app's renderer/notes-store.js, restricted to
/// what an unattended agent does with them: load what the session already holds, let the MODEL replace its own
/// notes (notes_write), and write the result back in the SAME persisted format so the Electron app reads it
/// unchanged.
///
/// Two authors share one list in the app; Josh's notes are pinned and the model's replace-all never touches them, and a
/// note Josh deleted leaves a tombstone the model cannot resurrect. All of that is preserved here, byte for byte in
/// behavior -- the agency must never be the thing that eats a note Josh wrote. It has no way to add, edit or remove
/// a user note, by design.</summary>
public sealed class NotesStore
{
    public const int NotesMaxCount = 40;
    public const int NotesMaxChars = 6000;
    public const int UserNotesMaxCount = 40;
    public const int UserNotesMaxChars = 8000;
    public const int RemovedMaxCount = 40;
    public const int RemovedMaxChars = 4000;
    public const string UserNoteTag = "[user]";

    public sealed record Note(string Text, bool User);

    /// <summary>User notes first, then the model's.</summary>
    public List<Note> Items { get; private set; } = [];

    /// <summary>Text Josh removed, oldest first -- what notes_write must not bring back.</summary>
    public List<string> Removed { get; private set; } = [];

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Normalize(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.StartsWith(UserNoteTag, StringComparison.OrdinalIgnoreCase)) text = text[UserNoteTag.Length..].Trim();
        return text;
    }

    private static bool SameNote(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool IsEmpty => Items.Count == 0;
    public IEnumerable<Note> UserNotes => Items.Where(i => i.User);
    public IEnumerable<Note> ModelNotes => Items.Where(i => !i.User);

    private void Tombstone(string text)
    {
        var clean = text.Trim();
        if (clean.Length == 0 || Removed.Any(r => SameNote(r, clean))) return;
        Removed.Add(clean);
        while (Removed.Count > RemovedMaxCount || (Removed.Count > 1 && Removed.Sum(r => r.Length) > RemovedMaxChars))
            Removed.RemoveAt(0);
    }

    private bool IsRemoved(string text) => Removed.Any(r => SameNote(r, text));

    /// <summary>Serialized for persistence (ChatSession.SessionNotes): the model's notes as plain strings, a user note as
    /// { text, source: 'user' }, tombstones as { text, source: 'removed' } after the notes.</summary>
    public string ToJson()
    {
        var entries = new List<object>();
        foreach (var i in Items) entries.Add(i.User ? new Dictionary<string, string> { ["text"] = i.Text, ["source"] = "user" } : i.Text);
        foreach (var r in Removed) entries.Add(new Dictionary<string, string> { ["text"] = r, ["source"] = "removed" });
        return JsonSerializer.Serialize(entries, Compact);
    }

    /// <summary>The JSON to send with a turn, or null when there is nothing to send. Taken ONCE per turn so every round
    /// carries the same text (a changed system prompt each round would discard the prefix cache).</summary>
    public string? Snapshot() => IsEmpty && Removed.Count == 0 ? null : ToJson();

    /// <summary>Restore from the persisted JSON array. Tolerates null/garbage -- comes back empty.</summary>
    public void Load(string? json)
    {
        Items = [];
        Removed = [];
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var user = new List<Note>();
            var model = new List<Note>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                string? raw = entry.ValueKind switch
                {
                    JsonValueKind.String => entry.GetString(),
                    JsonValueKind.Object when entry.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String => t.GetString(),
                    _ => null
                };
                var text = Normalize(raw);
                if (text.Length == 0) continue;

                string? source = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() : null;
                if (source == "removed") { Tombstone(text); continue; }
                var isUser = source == "user";
                (isUser ? user : model).Add(new Note(text, isUser));
            }
            Items = [.. user, .. model];
        }
        catch (JsonException)
        {
            // Not JSON we wrote -- leave notes empty rather than crash.
        }
    }

    /// <summary>Handles a notes_write call from the model; returns the tool result the model sees. Replaces the MODEL's
    /// notes with what it sent; the user's are kept as they are.</summary>
    public string Write(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("notes", out var notesEl) || notesEl.ValueKind != JsonValueKind.Array)
            return "Error: 'notes' is required and must be an array of strings.";

        var userItems = UserNotes.ToList();
        var normalized = new List<string>();
        var refused = new List<string>();
        foreach (var rawEl in notesEl.EnumerateArray())
        {
            string? raw = rawEl.ValueKind switch
            {
                JsonValueKind.String => rawEl.GetString(),
                JsonValueKind.Object when rawEl.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String => t.GetString(),
                _ => null
            };
            var text = Normalize(raw);
            if (text.Length == 0) continue;
            if (userItems.Any(u => SameNote(u.Text, text))) continue;
            if (normalized.Any(n => SameNote(n, text))) continue;
            // Josh deleted (or reworded) this one; the model resends its whole list from its own context.
            if (IsRemoved(text)) { refused.Add(text); continue; }
            normalized.Add(text);
        }

        if (normalized.Count > NotesMaxCount)
            return $"Error: {normalized.Count} notes is too many -- keep at most {NotesMaxCount}. These are " +
                   "durable facts, not a log. Merge or drop the ones that no longer matter and send the shorter list.";

        var totalChars = normalized.Sum(n => n.Length);
        if (totalChars > NotesMaxChars)
            return $"Error: the notes total {totalChars} characters, over the {NotesMaxChars} limit -- they ride " +
                   "in your context every round, so keep them terse. Shorten or drop some and send the list again.";

        Items = [.. userItems, .. normalized.Select(t => new Note(t, false))];
        var result = Render();
        if (refused.Count == 0) return result;
        return $"{result}\n\nThese were NOT kept -- the user removed them because they were out of date or wrong, so " +
               $"do not record them again:\n{string.Join("\n", refused.Select(r => $"- \"{r}\""))}";
    }

    /// <summary>What the model gets back after a write.</summary>
    public string Render()
    {
        var removedLines = Removed.Count == 0
            ? new List<string>()
            : new List<string> { "", "Removed by the user (out of date or wrong) -- do not record these again:" }
                .Concat(Removed.Select(r => $"- {r}")).ToList();

        if (Items.Count == 0) return string.Join("\n", new[] { "Session notes: (none yet)." }.Concat(removedLines));

        var lines = Items.Select((n, i) => $"{i + 1}. {(n.User ? UserNoteTag + " " : "")}{n.Text}");
        return string.Join("\n", new[]
        {
            "SESSION NOTES (durable notes for this run -- kept across compaction; prune YOUR notes with notes_write. " +
            $"Notes marked {UserNoteTag} were written by the user: they stay whatever you send, so do not resend or rewrite them):"
        }.Concat(lines).Concat(removedLines));
    }
}
