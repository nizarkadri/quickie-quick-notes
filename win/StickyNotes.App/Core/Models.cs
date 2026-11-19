using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StickyNotes.App.Core;

// ---------------------------------------------------------------------------
// Managed mirror of the Rust DTOs (blueprint §6). JSON property names match
// the Rust serde snake_case output EXACTLY via [JsonPropertyName].
// ---------------------------------------------------------------------------

public sealed record Note
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("group_id")]
    public long? GroupId { get; init; }

    [JsonPropertyName("body")]
    public string Body { get; init; } = "";

    /// <summary>Derived: first non-empty line trimmed to &lt;=28 chars, fallback "New note".</summary>
    [JsonPropertyName("title_preview")]
    public string TitlePreview { get; init; } = "";

    [JsonPropertyName("is_pinned")]
    public bool IsPinned { get; init; }

    [JsonPropertyName("pinned_at_ms")]
    public long? PinnedAtMs { get; init; }

    [JsonPropertyName("created_at_ms")]
    public long CreatedAtMs { get; init; }

    [JsonPropertyName("updated_at_ms")]
    public long UpdatedAtMs { get; init; }

    [JsonPropertyName("last_opened_at_ms")]
    public long LastOpenedAtMs { get; init; }
}

public sealed record NoteSummary
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("group_id")]
    public long? GroupId { get; init; }

    [JsonPropertyName("title_preview")]
    public string TitlePreview { get; init; } = "";

    /// <summary>Used by the UI to decide whether delete needs confirmation.</summary>
    [JsonPropertyName("is_empty")]
    public bool IsEmpty { get; init; }

    [JsonPropertyName("is_pinned")]
    public bool IsPinned { get; init; }

    [JsonPropertyName("pinned_at_ms")]
    public long? PinnedAtMs { get; init; }

    [JsonPropertyName("last_opened_at_ms")]
    public long LastOpenedAtMs { get; init; }
}

public sealed record GroupSummary
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("note_count")]
    public int NoteCount { get; init; }

    [JsonPropertyName("is_pinned")]
    public bool IsPinned { get; init; }

    [JsonPropertyName("pinned_at_ms")]
    public long? PinnedAtMs { get; init; }

    [JsonPropertyName("created_at_ms")]
    public long CreatedAtMs { get; init; }
}

/// <summary>
/// Rust serde: <c>#[serde(tag = "kind", rename_all = "snake_case")]</c> enum with
/// <c>Group(GroupSummary)</c> / <c>Note(NoteSummary)</c> — the payload fields are
/// FLAT next to <c>"kind"</c>, e.g. <c>{"kind":"group","id":1,"name":"Work",...}</c>.
/// </summary>
[JsonConverter(typeof(PinnedItemConverter))]
public sealed class PinnedItem
{
    public const string GroupKind = "group";
    public const string NoteKind = "note";

    private PinnedItem(string kind, GroupSummary? group, NoteSummary? note)
    {
        Kind = kind;
        Group = group;
        Note = note;
    }

    /// <summary>"group" or "note".</summary>
    public string Kind { get; }

    /// <summary>Non-null when Kind == "group".</summary>
    public GroupSummary? Group { get; }

    /// <summary>Non-null when Kind == "note".</summary>
    public NoteSummary? Note { get; }

    public static PinnedItem FromGroup(GroupSummary group) => new(GroupKind, group, null);

    public static PinnedItem FromNote(NoteSummary note) => new(NoteKind, null, note);
}

internal static class PinnedItemJson
{
    /// <summary>Dedicated, permissive options for the flattened pinned-item payload (ignores "kind").</summary>
    internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };
}

internal sealed class PinnedItemConverter : JsonConverter<PinnedItem>
{
    public override PinnedItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("kind", out JsonElement kindEl))
        {
            throw new JsonException("pinned_items entry is missing the \"kind\" discriminator.");
        }

        string kind = kindEl.GetString() ?? throw new JsonException("pinned_items \"kind\" is not a string.");
        // F4: deserialize the flattened payload with a DEDICATED options instance so a future
        // switch to JsonUnmappedMemberHandling.Disallow on the shared options can't break chips.
        return kind switch
        {
            PinnedItem.GroupKind => PinnedItem.FromGroup(
                JsonSerializer.Deserialize<GroupSummary>(root.GetRawText(), PinnedItemJson.Options)
                ?? throw new JsonException("pinned_items group payload failed to deserialize.")),
            PinnedItem.NoteKind => PinnedItem.FromNote(
                JsonSerializer.Deserialize<NoteSummary>(root.GetRawText(), PinnedItemJson.Options)
                ?? throw new JsonException("pinned_items note payload failed to deserialize.")),
            _ => throw new JsonException($"Unknown pinned_items kind \"{kind}\"."),
        };
    }

    public override void Write(Utf8JsonWriter writer, PinnedItem value, JsonSerializerOptions options)
    {
        // Client never serializes PinnedItem; still emit the Rust enum shape faithfully.
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        JsonElement payload = JsonSerializer.SerializeToElement(
            value.Kind == PinnedItem.GroupKind ? (object)value.Group! : value.Note!,
            options);
        foreach (JsonProperty prop in payload.EnumerateObject())
        {
            prop.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

public sealed record BarSnapshot
{
    /// <summary>Interleaved by pinned_at_ms ASC (newest on the right).</summary>
    [JsonPropertyName("pinned_items")]
    public IReadOnlyList<PinnedItem> PinnedItems { get; init; } = new List<PinnedItem>();

    /// <summary>Max 5, is_pinned = false, last_opened_at_ms DESC.</summary>
    [JsonPropertyName("recent_notes")]
    public IReadOnlyList<NoteSummary> RecentNotes { get; init; } = new List<NoteSummary>();

    /// <summary>Ordered by created_at_ms ASC (powers `Groups ▾` and the QuickNote picker).</summary>
    [JsonPropertyName("all_groups")]
    public IReadOnlyList<GroupSummary> AllGroups { get; init; } = new List<GroupSummary>();
}

/// <summary>
/// Serializes to the Rust <c>QuickCaptureTarget</c> serde representation:
/// <c>{"mode":"standalone"}</c>, <c>{"mode":"group","group_id":N}</c>,
/// <c>{"mode":"append","note_id":N}</c>.
/// </summary>
public sealed class QuickCaptureTarget
{
    public const string StandaloneMode = "standalone";
    public const string GroupMode = "group";
    public const string AppendMode = "append";

    private QuickCaptureTarget(string mode, long? groupId, long? noteId)
    {
        Mode = mode;
        GroupId = groupId;
        NoteId = noteId;
    }

    public string Mode { get; }
    public long? GroupId { get; }
    public long? NoteId { get; }

    public static QuickCaptureTarget Standalone() => new(StandaloneMode, null, null);

    public static QuickCaptureTarget Group(long groupId) => new(GroupMode, groupId, null);

    public static QuickCaptureTarget Append(long noteId) => new(AppendMode, null, noteId);

    /// <summary>Exact serde <c>tag = "mode"</c> JSON; written by hand to guarantee no extra fields.</summary>
    internal string ToJson() => Mode switch
    {
        StandaloneMode => "{\"mode\":\"standalone\"}",
        GroupMode => string.Create(CultureInfo.InvariantCulture, $"{{\"mode\":\"group\",\"group_id\":{GroupId}}}"),
        AppendMode => string.Create(CultureInfo.InvariantCulture, $"{{\"mode\":\"append\",\"note_id\":{NoteId}}}"),
        _ => throw new InvalidOperationException($"Unknown quick-capture mode \"{Mode}\"."),
    };
}

public sealed class StickyCoreException : Exception
{
    public StickyCoreException(int status, string message) : base(message) => Status = status;

    /// <summary>0 = OK, 1 = NotFound, 2 = Validation, 3 = Sqlite, 4 = UnsupportedSchema, 5 = Internal/panic.</summary>
    public int Status { get; }
}

public interface IStickyCoreClient : IDisposable
{
    BarSnapshot GetBarSnapshot();
    IReadOnlyList<NoteSummary> ListGroupNotes(long groupId);
    IReadOnlyList<NoteSummary> ListAppendTargets(int limit = 30);
    Note GetNote(long noteId, bool markOpened);
    Note CreateNote(long? groupId, string body = "");
    NoteSummary UpdateNoteBody(long noteId, string body);
    void DeleteNote(long noteId);
    NoteSummary SetNotePinned(long noteId, bool pinned);
    Note SetNoteGroup(long noteId, long? groupId);
    GroupSummary CreateGroup(string name, bool pinned = true);
    GroupSummary RenameGroup(long groupId, string name);
    void DeleteGroup(long groupId);
    GroupSummary SetGroupPinned(long groupId, bool pinned);
    Note QuickCapture(string text, QuickCaptureTarget target);
}
