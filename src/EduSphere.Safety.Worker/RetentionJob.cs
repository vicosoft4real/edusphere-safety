using Dapper;
using EduSphere.Safety.Application;
using EduSphere.Safety.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EduSphere.Safety.Worker;

/// <summary>
/// Daily leased retention sweep. Deletes expired media (object bytes via
/// <see cref="IEvidenceStore.DeleteAsync"/> then the manifest row), sweeps
/// abandoned never-uploaded manifests, and purges old access-log rows.
/// Incident and incident-activity rows are NEVER touched: metadata outlives
/// media (the manifest <c>incident_id</c> FK is <c>ON DELETE SET NULL</c> in
/// the other direction). Single-flight across replicas via a Postgres
/// advisory lease; with no connection string configured (tests, single
/// node) the sweep runs unleashed and logs that fact.
/// </summary>
public sealed class RetentionJob(
    IServiceScopeFactory scopes,
    RetentionOptions retention,
    PostgresOptions postgres,
    ILogger<RetentionJob> logger) : BackgroundService
{
    private const long LeaseKey = 841726351927L;
    private const int SweepBatchLimit = 500;

    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly RetentionOptions _retention = retention ?? throw new ArgumentNullException(nameof(retention));
    private readonly PostgresOptions _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
    private readonly ILogger<RetentionJob> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention sweep failed; retrying tomorrow.");
            }
        }
    }

    public async Task<RetentionResult> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEvidenceStore>();
        if (!await TryAcquireLeaseAsync(ct))
        {
            _logger.LogWarning("Retention sweep skipped: lease held by another replica.");
            return new RetentionResult(0, 0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var clipDays = Math.Max(1, _retention.ClipDays);
        var snapshotDays = Math.Max(1, _retention.SnapshotDays);
        var auditDays = Math.Max(1, _retention.AuditDays);

        // Loop bounded passes so one run drains backlogs without unbounded memory.
        var expiredTotal = 0;
        while (true)
        {
            var expired = await store.ListExpiredManifestsAsync(
                now.AddDays(-clipDays), now.AddDays(-snapshotDays), ct);
            if (expired.Count == 0)
                break;
            foreach (var manifest in expired)
            {
                if (ct.IsCancellationRequested)
                    break;
                await store.DeleteAsync(manifest.ObjectKey, ct);
                await store.DeleteManifestAsync(manifest.TenantId, manifest.EdgeEventId, manifest.Kind, ct);
                expiredTotal++;
            }

            if (expired.Count < SweepBatchLimit)
                break;
        }

        var abandonedTotal = 0;
        while (true)
        {
            var abandoned = await store.ListAbandonedManifestsAsync(now, ct);
            if (abandoned.Count == 0)
                break;
            foreach (var manifest in abandoned)
            {
                if (ct.IsCancellationRequested)
                    break;
                await store.DeleteAsync(manifest.ObjectKey, ct);
                await store.DeleteManifestAsync(manifest.TenantId, manifest.EdgeEventId, manifest.Kind, ct);
                abandonedTotal++;
            }

            if (abandoned.Count < SweepBatchLimit)
                break;
        }

        var purged = await store.PurgeAccessLogAsync(now.AddDays(-auditDays), ct);
        _logger.LogInformation(
            "Retention sweep complete: {Expired} expired manifests, {Abandoned} abandoned manifests, {Purged} access-log rows.",
            expiredTotal, abandonedTotal, purged);
        return new RetentionResult(expiredTotal, abandonedTotal, purged);
    }

    private async Task<bool> TryAcquireLeaseAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_postgres.ConnectionString))
        {
            _logger.LogInformation("Retention sweep running without a lease (no Postgres connection configured).");
            return true;
        }

        try
        {
            await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
            await connection.OpenAsync(ct);
            return await connection.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT pg_try_advisory_lock(@key)",
                    new { key = LeaseKey },
                    cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention lease unavailable; skipping this run.");
            return false;
        }
    }
}
