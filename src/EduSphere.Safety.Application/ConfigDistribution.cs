using System.Text.Json;
using EduSphere.Safety.Shared;
using FluentValidation;

namespace EduSphere.Safety.Application;

public static class BundleShape
{
    public const string ExpectedFormat = "edusphere-rule-bundle/1";

    /// <summary>
    /// Cloud-side shape validation for a bundle document: parseable JSON with
    /// the expected format tag, bundleVersion ≥ 1, a non-empty rules array,
    /// non-empty ruleIds, and every rule scheduleId resolving to a declared
    /// schedule (unique ids). Full semantic evaluation stays edge-side, where
    /// unknown members are ignored per the forward-compat contract; the edge
    /// refuses bad bundles fail-closed on receipt.
    /// </summary>
    public static string? Validate(string document)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(document);
        }
        catch (JsonException)
        {
            return "Bundle is not valid JSON.";
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("format", out var format) || format.GetString() != ExpectedFormat)
                return $"Bundle format must be '{ExpectedFormat}'.";
            if (!root.TryGetProperty("bundleVersion", out var version) || version.GetInt32() < 1)
                return "bundleVersion must be >= 1.";
            if (!root.TryGetProperty("schedules", out var schedules) || schedules.ValueKind != JsonValueKind.Array)
                return "Bundle must declare a schedules array.";
            var scheduleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in schedules.EnumerateArray())
            {
                if (!s.TryGetProperty("scheduleId", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
                    return "Every schedule needs a non-empty scheduleId.";
                if (!scheduleIds.Add(id.GetString()!))
                    return $"Duplicate scheduleId '{id.GetString()}'.";
            }

            if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
                return "Bundle must declare a rules array.";
            var count = 0;
            foreach (var r in rules.EnumerateArray())
            {
                count++;
                if (!r.TryGetProperty("ruleId", out var ruleId) || string.IsNullOrWhiteSpace(ruleId.GetString()))
                    return "Every rule needs a non-empty ruleId.";
                if (!r.TryGetProperty("version", out var rv) || rv.GetInt32() < 1)
                    return $"Rule '{ruleId.GetString()}' version must be >= 1.";
                if (!r.TryGetProperty("scheduleId", out var rs) || !scheduleIds.Contains(rs.GetString() ?? string.Empty))
                    return $"Rule '{ruleId.GetString()}' references an unknown scheduleId.";
            }

            if (count == 0)
                return "Bundle must declare at least one rule.";
        }

        return null;
    }
}

public sealed record PublishBundleCommand(string TenantId, string Document, string CreatedBy) : ICommand<int>;

public sealed class PublishBundleHandler(IConfigStore store) : ICommandHandler<PublishBundleCommand, int>
{
    public async Task<Result<int>> HandleAsync(PublishBundleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.TenantId) || string.IsNullOrWhiteSpace(command.Document))
            return Result<int>.Fail("validation.failed", "TenantId and Document are required.");
        var problem = BundleShape.Validate(command.Document);
        if (problem is not null)
            return Result<int>.Fail("validation.failed", problem);
        var version = await store.PublishBundleAsync(command.TenantId, command.Document, command.CreatedBy, ct);
        return Result<int>.Ok(version);
    }
}

public sealed record SetPolicyCommand(
    string TenantId, string Id, string LevelsJson, string RecipientsJson, string CreatedBy) : ICommand<bool>;

public sealed class SetPolicyHandler(IConfigStore store) : ICommandHandler<SetPolicyCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(SetPolicyCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.TenantId) || string.IsNullOrWhiteSpace(command.Id))
            return Result<bool>.Fail("validation.failed", "TenantId and Id are required.");
        var problem = ValidateLevels(command.LevelsJson, command.RecipientsJson);
        if (problem is not null)
            return Result<bool>.Fail("validation.failed", problem);
        await store.SetPolicyAsync(
            new PolicyRecord(
                command.TenantId, command.Id, command.LevelsJson, command.RecipientsJson,
                command.CreatedBy, DateTimeOffset.UtcNow),
            ct);
        return Result<bool>.Ok(true);
    }

    private static string? ValidateLevels(string levelsJson, string recipientsJson)
    {
        try
        {
            using var levels = JsonDocument.Parse(levelsJson);
            using var recipients = JsonDocument.Parse(recipientsJson);
            if (levels.RootElement.ValueKind != JsonValueKind.Array || levels.RootElement.GetArrayLength() == 0)
                return "Policy levels must be a non-empty array.";
            foreach (var level in levels.RootElement.EnumerateArray())
            {
                if (!level.TryGetProperty("afterSeconds", out var after) || after.GetInt32() < 0)
                    return "Every escalation level needs afterSeconds >= 0.";
            }

            if (recipients.RootElement.ValueKind != JsonValueKind.Array)
                return "Policy recipients must be an array.";
        }
        catch (JsonException)
        {
            return "Policy levels/recipients must be valid JSON.";
        }

        return null;
    }
}
