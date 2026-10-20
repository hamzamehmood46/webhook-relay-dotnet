using System.Text.Json;

namespace Relay.Engine;

/// <summary>Reading values out of a payload, and deciding whether a route wants an event.</summary>
public static class Matching
{
    /// <summary>
    /// Event types match exactly, by prefix (<c>invoice.*</c> matches <c>invoice.paid</c> but not <c>invoices.paid</c>
    /// and not <c>invoice</c> itself), or everything (<c>*</c>). Matching ignores case.
    /// </summary>
    public static bool TypeMatches(IEnumerable<string> patterns, string type)
    {
        foreach (var pattern in patterns)
        {
            if (pattern == "*")
            {
                return true;
            }

            if (pattern.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = pattern[..^1];
                if (type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && type.Length > prefix.Length)
                {
                    return true;
                }
            }
            else if (string.Equals(pattern, type, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ConditionMatches(PayloadCondition? condition, JsonElement payload)
    {
        if (condition is null)
        {
            return true;
        }

        var value = ReadPath(payload, condition.Path);
        return value is not null && string.Equals(value, condition.Value, StringComparison.Ordinal);
    }

    /// <summary>Reads a scalar at a dotted path such as <c>data.object.id</c>. Returns null if anything on the way is missing or the value is not a scalar.</summary>
    public static string? ReadPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>A path is either a header (<c>header:X-GitHub-Event</c>) or a dotted path into the JSON body.</summary>
    public static string? Extract(string path, IReadOnlyDictionary<string, string> headers, JsonElement body)
    {
        const string prefix = "header:";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return headers.TryGetValue(path[prefix.Length..].Trim(), out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        }

        var text = ReadPath(body, path);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
