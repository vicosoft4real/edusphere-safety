namespace EduSphere.Safety.Worker;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int IntervalSeconds { get; set; } = 5;

    public int BatchSize { get; set; } = 25;

    // Seconds before a leased-but-unfinished row becomes claimable again.
    public int LeaseSeconds { get; set; } = 60;
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    // When set, mail is written to this directory (System.Net.Mail
    // SpecifiedPickupDirectory) instead of an SMTP host — local dev default.
    public string PickupDirectory { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 25;

    public string From { get; set; } = "safety@localhost";
}

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Days an uploaded clip is kept. Default 30.</summary>
    public int ClipDays { get; set; } = 30;

    /// <summary>Days an uploaded snapshot is kept. Default 90.</summary>
    public int SnapshotDays { get; set; } = 90;

    /// <summary>Days evidence access-log rows are kept. Default 365.</summary>
    public int AuditDays { get; set; } = 365;
}

public sealed record RetentionResult(int ClipsDeleted, int AbandonedDeleted, int AccessLogPurged);

public sealed class NotificationOptions
{
    public const string SectionName = "Notification";

    // Pilot slice: static recipient list from config. A recipient-policy
    // engine (per-campus routing, preferences, escalation) is a later plan.
    public List<string> To { get; set; } = [];
}

/// <summary>
/// Fail-fast startup validation for the Worker. An empty recipient list
/// would silently DELETE outbox rows without emailing anyone, so the Worker
/// must refuse to start instead.
/// </summary>
public static class WorkerStartupValidator
{
    public static void Validate(NotificationOptions notifications, EmailOptions email)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(email);
        if (notifications.To is null
            || notifications.To.Count == 0
            || notifications.To.All(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "Worker startup failed: Notification:To must contain at least one recipient address.");
        var hasPickupDirectory = !string.IsNullOrWhiteSpace(email.PickupDirectory);
        var hasHost = !string.IsNullOrWhiteSpace(email.Host);
        if (hasPickupDirectory == hasHost)
            throw new InvalidOperationException(
                "Worker startup failed: set exactly one of Email:PickupDirectory or Email:Host.");
    }
}
