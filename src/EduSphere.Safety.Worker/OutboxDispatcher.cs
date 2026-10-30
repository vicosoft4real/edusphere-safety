using System.Text.Json;
using EduSphere.Safety.Application;

namespace EduSphere.Safety.Worker;

/// <summary>
/// Leased outbox dispatch loop. Claims due rows, emails each to the pilot
/// <c>Notification:To</c> list, DELETEs on success; on failure parks rows that
/// reached 10 attempts for 24h (still visible to the dead-letter endpoint),
/// otherwise reschedules with exponential backoff. Honors shutdown.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IEmailSender email,
    OutboxOptions outbox,
    NotificationOptions notifications,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    public const int DeadLetterAttempts = 10;

    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly IEmailSender _email = email ?? throw new ArgumentNullException(nameof(email));
    private readonly OutboxOptions _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly NotificationOptions _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    private readonly ILogger<OutboxDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _outbox.IntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox dispatch cycle failed; retrying.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task DispatchOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxDispatcherStore>();
        var ledger = scope.ServiceProvider.GetRequiredService<INotificationStore>();
        var leaseUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _outbox.LeaseSeconds));
        var due = await store.ClaimDueAsync(Math.Max(1, _outbox.BatchSize), leaseUntil, ct);
        foreach (var row in due)
        {
            if (ct.IsCancellationRequested)
                break;
            await DeliverAsync(store, ledger, row, ct);
        }
    }

    private async Task DeliverAsync(
        IOutboxDispatcherStore store, INotificationStore ledger, OutboxClaim row, CancellationToken ct)
    {
        var ledgerRef = IncidentRef(row);
        var sent = new List<DeliveryAttempt>();
        try
        {
            var attempted = 0;
            foreach (var to in _notifications.To)
            {
                if (ct.IsCancellationRequested)
                    break;
                await _email.SendAsync(to, $"[safety] {row.Type}", row.Payload, ct);
                attempted++;
                if (ledgerRef is not null)
                    sent.Add(new DeliveryAttempt(ledgerRef.Value.Tenant, ledgerRef.Value.Incident, "email", 0, to, "accepted", string.Empty));
            }

            if (ct.IsCancellationRequested)
            {
                if (ledgerRef is not null)
                    await RecordBestEffortAsync(ledger, sent.Concat(Rest(ledgerRef.Value, sent, "unknown", "Cancelled during send.")));
                return;
            }

            if (attempted == 0)
            {
                // Belt-and-braces: validation should have prevented startup with
                // zero recipients, but NEVER delete a row nobody was emailed.
                _logger.LogWarning("Outbox {OutboxId} ({Type}) had zero recipients attempted; rescheduling.", row.Id, row.Type);
                await store.RescheduleAsync(row.Id, DateTimeOffset.UtcNow.Add(Backoff(row.Attempts)), ct);
                return;
            }

            if (ledgerRef is not null)
                await RecordBestEffortAsync(ledger, sent);
            await store.DeleteAsync(row.Id, ct);
            _logger.LogInformation("Outbox {OutboxId} ({Type}) delivered.", row.Id, row.Type);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Outbox {OutboxId} delivery failed (attempt {Attempts}).", row.Id, row.Attempts);
            if (ledgerRef is not null)
                await RecordBestEffortAsync(ledger, sent.Concat(Rest(ledgerRef.Value, sent, "failed", ex.Message)));
            // attempts was already incremented by the atomic claim.
            var lockedUntil = row.Attempts >= DeadLetterAttempts
                ? DateTimeOffset.UtcNow.AddHours(24)
                : DateTimeOffset.UtcNow.Add(Backoff(row.Attempts));
            await store.RescheduleAsync(row.Id, lockedUntil, ct);
        }
    }

    private List<DeliveryAttempt> Rest(
        (string Tenant, Guid Incident) ledgerRef, List<DeliveryAttempt> sent, string state, string detail) =>
        _notifications.To
            .Where(to => sent.All(s => s.Recipient != to))
            .Select(to => new DeliveryAttempt(ledgerRef.Tenant, ledgerRef.Incident, "email", 0, to, state, detail))
            .ToList();

    private static (string Tenant, Guid Incident)? IncidentRef(OutboxClaim row)
    {
        // Level-0 ledger covers incident.created only; every other outbox type
        // keeps today's behavior with no ledger rows. Unparsable payloads
        // degrade to no-ledger rather than failing delivery.
        if (!string.Equals(row.Type, "incident.created", StringComparison.Ordinal))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(row.Payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tenantId", out var tenant)
                || !root.TryGetProperty("incidentId", out var id)
                || tenant.ValueKind != JsonValueKind.String
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tenant.GetString())
                || !Guid.TryParse(id.GetString(), out var incident))
                return null;
            return (tenant.GetString()!, incident);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task RecordBestEffortAsync(INotificationStore ledger, IEnumerable<DeliveryAttempt> attempts)
    {
        // The ledger must never block or fail delivery: alerts flow even when
        // audit is down. Detached token so a racing shutdown cannot cancel the
        // write after a successful send.
        try
        {
            await ledger.RecordAttemptsAsync(attempts.ToList(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Level-0 delivery ledger append failed; delivery outcome stands.");
        }
    }

    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Max(0, attempts - 1))));
}
