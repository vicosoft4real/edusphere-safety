using EduSphere.Safety.Application;
using EduSphere.Safety.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EduSphere.Safety.Worker;

/// <summary>
/// Leased escalation dispatch loop. Claims due escalation rows, rechecks the
/// incident state transactionally (acknowledged/resolved/missing rows are
/// deleted silently — no send), resolves recipients from the CURRENT default
/// policy, emails each, then settles (ledger + next level or delete) in one
/// transaction. Send failure appends <c>failed</c> and reschedules with
/// backoff; cancellation appends <c>unknown</c>. SMTP has no delivery
/// callbacks, so <c>delivered</c> is never recorded here. A crash between send
/// and settle can duplicate one email (at-least-once, documented); the ledger
/// dedup keeps a single accepted row per recipient. Honors shutdown.
/// </summary>
public sealed class EscalationDispatcher(
    IServiceScopeFactory scopes,
    IEmailSender email,
    OutboxOptions outbox,
    ILogger<EscalationDispatcher> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly IEmailSender _email = email ?? throw new ArgumentNullException(nameof(email));
    private readonly OutboxOptions _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly ILogger<EscalationDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
                _logger.LogError(ex, "Escalation dispatch cycle failed; retrying.");
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
        var provider = scope.ServiceProvider;
        var notifications = provider.GetRequiredService<INotificationStore>();
        var leaseUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _outbox.LeaseSeconds));
        var due = await notifications.ClaimDueAsync(Math.Max(1, _outbox.BatchSize), leaseUntil, ct);
        foreach (var claim in due)
        {
            if (ct.IsCancellationRequested)
                break;
            await FireAsync(provider, notifications, claim, ct);
        }
    }

    private async Task FireAsync(
        IServiceProvider provider, INotificationStore notifications, EscalationClaim claim, CancellationToken ct)
    {
        var decision = await notifications.PrepareFireAsync(claim.TenantId, claim.IncidentId, claim.Level, ct);
        if (decision == FireDecision.Cancelled)
        {
            _logger.LogInformation(
                "Escalation {Tenant}/{Incident} L{Level} cancelled: incident no longer New.",
                claim.TenantId, claim.IncidentId, claim.Level);
            return;
        }

        var config = provider.GetRequiredService<IConfigStore>();
        var policy = await config.GetPolicyAsync(claim.TenantId, NotificationPolicy.DefaultId, ct);
        var levels = policy is null ? [] : NotificationLevels.Parse(policy.LevelsJson);
        if (policy is null || claim.Level < 1 || claim.Level > levels.Count)
        {
            await notifications.CancelScheduleAsync(claim.TenantId, claim.IncidentId, claim.Level, ct);
            return;
        }

        var level = levels[claim.Level - 1];
        var policyRecipients = NotificationLevels.ParseRecipients(policy.RecipientsJson);
        var recipients = NotificationRouting.ResolveRecipients([], level.Recipients, policyRecipients);
        if (recipients.Count == 0)
        {
            await notifications.CancelScheduleAsync(claim.TenantId, claim.IncidentId, claim.Level, ct);
            return;
        }

        var incidents = provider.GetRequiredService<IIncidentStore>();
        var incident = await incidents.GetAsync(claim.TenantId, claim.IncidentId, ct);
        if (incident is null)
        {
            await notifications.CancelScheduleAsync(claim.TenantId, claim.IncidentId, claim.Level, ct);
            return;
        }

        var subject = $"[safety][escalation L{claim.Level}] {incident.EventType} at {incident.CampusId}";
        var sent = new List<DeliveryAttempt>();
        var failed = new List<DeliveryAttempt>();
        foreach (var to in recipients)
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                await _email.SendAsync(to, subject, EscalationBody(incident, claim), ct);
                sent.Add(Attempt(claim, to, "accepted", string.Empty));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                failed.Add(Attempt(claim, to, "unknown", "Cancelled during send."));
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Escalation {Tenant}/{Incident} L{Level} send to {To} failed.", claim.TenantId, claim.IncidentId, claim.Level, to);
                failed.Add(Attempt(claim, to, "failed", ex.Message));
            }
        }

        if (ct.IsCancellationRequested)
        {
            // Shutdown drain: the passed token is already cancelled, so the
            // ledger write runs detached (best effort); the lease reclaims
            // anything unrecorded and the next run redelivers.
            await notifications.DeferFireAsync(
                claim.TenantId, claim.IncidentId, claim.Level,
                DateTimeOffset.UtcNow.Add(OutboxDispatcher.Backoff(claim.Attempts)),
                [.. sent, .. failed], CancellationToken.None);
            return;
        }

        if (failed.Count > 0)
        {
            await notifications.DeferFireAsync(
                claim.TenantId, claim.IncidentId, claim.Level,
                DateTimeOffset.UtcNow.Add(OutboxDispatcher.Backoff(claim.Attempts)),
                [.. sent, .. failed], ct);
            return;
        }

        int? nextLevel = claim.Level < levels.Count ? claim.Level + 1 : null;
        DateTimeOffset? nextDue = nextLevel.HasValue
            ? DateTimeOffset.UtcNow.AddSeconds(levels[nextLevel.Value - 1].AfterSeconds)
            : null;
        await notifications.SettleFireAsync(
            claim.TenantId, claim.IncidentId, claim.Level, sent, nextLevel, nextDue, ct);
        _logger.LogInformation(
            "Escalation {Tenant}/{Incident} L{Level} sent to {Count} recipient(s).",
            claim.TenantId, claim.IncidentId, claim.Level, sent.Count);
    }

    private static DeliveryAttempt Attempt(EscalationClaim claim, string recipient, string state, string detail) =>
        new(claim.TenantId, claim.IncidentId, "email", claim.Level, recipient, state, detail);

    private static string EscalationBody(Incident incident, EscalationClaim claim) =>
        $"Tenant: {incident.TenantId}\n" +
        $"Incident: {incident.Id}\n" +
        $"Event: {incident.EventType} (camera {incident.CameraId}, campus {incident.CampusId})\n" +
        $"State: {incident.State} (still unacknowledged at escalation level {claim.Level})\n" +
        "Acknowledge or resolve it in the safety dashboard.";
}
