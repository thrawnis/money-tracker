using System.Text.Json;

namespace MoneyTracker.Services;

/// <summary>
/// Lenient readers for the small JSON arrays kept on ImportDraft (and posted
/// as form fields at commit time): malformed or missing input reads as empty.
/// </summary>
public static class ImportDraftJson
{
    public static List<int> ReadInts(string? json) => Read<int>(json);

    public static List<string> ReadStrings(string? json) => Read<string>(json);

    private static List<T> Read<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
