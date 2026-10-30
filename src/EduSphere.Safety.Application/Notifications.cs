using System.Text.Json;

namespace EduSphere.Safety.Application;

public static class NotificationPolicy
{
    /// <summary>
    /// Convention: the tenant policy consulted for escalation scheduling.
    /// A tenant without this policy id gets level-0 notification only.
    /// </summary>
    public const string DefaultId = "default";
}

public sealed record EscalationLevel(int AfterSeconds, IReadOnlyList<string> Recipients);

public static class NotificationLevels
{
    /// <summary>
    /// Parses a policy levels document: a JSON array of
    /// <c>{"afterSeconds": N, "recipients"? [...]}</c>. Lenient by design:
    /// malformed content yields zero levels (no escalation, never an error —
    /// shape validation already ran at policy-write time).
    /// </summary>
    public static IReadOnlyList<EscalationLevel> Parse(string levelsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(levelsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var levels = new List<EscalationLevel>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("afterSeconds", out var after)
                    || after.ValueKind != JsonValueKind.Number
                    || !after.TryGetInt32(out var seconds)
                    || seconds < 0)
                    return [];
                var recipients = new List<string>();
                if (entry.TryGetProperty("recipients", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in list.EnumerateArray())
                    {
                        if (r.ValueKind == JsonValueKind.String)
                            recipients.Add(r.GetString() ?? string.Empty);
                    }
                }

                levels.Add(new EscalationLevel(seconds, recipients));
            }

            return levels;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> ParseRecipients(string recipientsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(recipientsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public static class NotificationRouting
{
    /// <summary>
    /// Pure recipient resolution: rule recipients ∪ level recipients ∪ policy
    /// recipients, trimmed, de-duplicated (ordinal), empties dropped. Order is
    /// deterministic (rule, then level, then policy) for testable ledgers.
    /// Rule recipients are empty under the v1 bundle (rules carry no
    /// recipients field); the parameter keeps the contract forward-compatible.
    /// </summary>
    public static IReadOnlyList<string> ResolveRecipients(
        IEnumerable<string>? ruleRecipients,
        IEnumerable<string>? levelRecipients,
        IEnumerable<string>? policyRecipients)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<string>();
        foreach (var source in new[] { ruleRecipients, levelRecipients, policyRecipients })
        {
            if (source is null)
                continue;
            foreach (var candidate in source)
            {
                var address = candidate?.Trim() ?? string.Empty;
                if (address.Length > 0 && seen.Add(address))
                    resolved.Add(address);
            }
        }

        return resolved;
    }
}
