using EduSphere.Safety.Application;
using EduSphere.Safety.Core;
using EduSphere.Safety.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

// Requires Docker (Testcontainers). Cannot run in the restricted dev sandbox
// (socket bind is blocked); runs on connected CI. Category=Integration.
[Trait("Category", "Integration")]
public sealed class IncidentRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").Build();
    private IncidentRepository _store = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _store = new IncidentRepository(new PostgresOptions { ConnectionString = _postgres.GetConnectionString() });
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        var migration = await File.ReadAllTextAsync(FindMigrationFile());
        await using var command = new NpgsqlCommand(migration, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task RoundTrip_InsertsAndLoadsIncident()
    {
        var tenant = await CreateTenantAsync();
        var edgeEventId = $"edge-{Guid.NewGuid():N}";
        var incident = Incident.Create(tenant, "campus-1", "ZoneIntrusion", "cam-1", edgeEventId);

        await _store.AddAsync(incident, Outbox("incident.created", tenant), CancellationToken.None);

        var loaded = await _store.GetAsync(tenant, incident.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(incident.Id, loaded.Id);
        Assert.Equal(tenant, loaded.TenantId);
        Assert.Equal("campus-1", loaded.CampusId);
        Assert.Equal("ZoneIntrusion", loaded.EventType);
        Assert.Equal("cam-1", loaded.CameraId);
        Assert.Equal(edgeEventId, loaded.EdgeEventId);
        Assert.Equal(IncidentState.New, loaded.State);
        Assert.Equal(0, loaded.Version);

        var byEdge = await _store.FindByEdgeEventAsync(tenant, edgeEventId, CancellationToken.None);
        Assert.NotNull(byEdge);
        Assert.Equal(incident.Id, byEdge.Id);
    }

    [Fact]
    public async Task CrossTenant_IncidentIsInvisible()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var edgeEventId = $"edge-{Guid.NewGuid():N}";
        var incident = Incident.Create(tenantA, "campus-1", "ZoneIntrusion", "cam-1", edgeEventId);
        await _store.AddAsync(incident, Outbox("incident.created", tenantA), CancellationToken.None);

        Assert.Null(await _store.GetAsync(tenantB, incident.Id, CancellationToken.None));
        Assert.Null(await _store.FindByEdgeEventAsync(tenantB, edgeEventId, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateEdgeEvent_ConflictPathReturnsExistingIdWithSingleOutboxRow()
    {
        var tenant = await CreateTenantAsync();
        var edgeEventId = $"edge-{Guid.NewGuid():N}";
        var first = Incident.Create(tenant, "campus-1", "ZoneIntrusion", "cam-1", edgeEventId);
        var firstId = await _store.AddAsync(first, Outbox("incident.created", tenant), CancellationToken.None);

        var duplicate = Incident.Create(tenant, "campus-1", "ZoneIntrusion", "cam-1", edgeEventId);
        var effectiveId = await _store.AddAsync(duplicate, Outbox("incident.created", tenant), CancellationToken.None);

        Assert.Equal(firstId, effectiveId);
        Assert.Equal(first.Id, effectiveId);
        Assert.Single(await QueryOutboxAsync(tenant));
    }

    [Fact]
    public async Task ParallelDoubleAdd_ReturnsSameIdWithSingleOutboxRow()
    {
        var tenant = await CreateTenantAsync();
        var edgeEventId = $"edge-{Guid.NewGuid():N}";
        var first = Incident.Create(tenant, "campus-1", "ZoneIntrusion", "cam-1", edgeEventId);
        var rival = Incident.Create(tenant, "campus-1", "ZoneIntrusion", "cam-1", edgeEventId);

        var ids = await Task.WhenAll(
            _store.AddAsync(first, Outbox("incident.created", tenant), CancellationToken.None),
            _store.AddAsync(rival, Outbox("incident.created", tenant), CancellationToken.None));

        Assert.Equal(ids[0], ids[1]);
        Assert.Single(await QueryOutboxAsync(tenant));
    }

    [Fact]
    public async Task Create_WritesOutboxRow()
    {
        var tenant = await CreateTenantAsync();
        var incident = Incident.Create(tenant, "campus-1", "ZoneIntrusion", "cam-1", $"edge-{Guid.NewGuid():N}");
        var payload = $"{{\"source\":\"test\",\"incidentId\":\"{incident.Id}\"}}";
        await _store.AddAsync(incident, new OutboxEntry(tenant, "incident.created", payload), CancellationToken.None);

        var rows = await QueryOutboxAsync(tenant);
        var created = Assert.Single(rows);
        Assert.Equal("incident.created", created.Type);
        Assert.Contains(incident.Id.ToString(), created.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAsync_RealisticCreatedPayload_PersistsIngestMetadata()
    {
        var tenant = await CreateTenantAsync();
        var incident = Incident.Create(tenant, "campus-1", "ZoneIntrusion", "cam-1", $"edge-{Guid.NewGuid():N}");
        var detectedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        const string ruleVersion = "rule-2026.10";
        const string modelVersion = "model-v3.2.1";
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            incidentId = incident.Id,
            edgeEventId = incident.EdgeEventId,
            tenantId = tenant,
            campusId = "campus-1",
            eventType = "ZoneIntrusion",
            cameraId = "cam-1",
            detectedAtUtc = detectedAt,
            ruleVersion,
            modelVersion,
        });

        await _store.AddAsync(incident, new OutboxEntry(tenant, "incident.created", payload), CancellationToken.None);

        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT detected_at, rule_version, model_version FROM incidents WHERE tenant_id = $1 AND id = $2",
            connection);
        command.Parameters.AddWithValue(tenant);
        command.Parameters.AddWithValue(incident.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(detectedAt, reader.GetFieldValue<DateTimeOffset>(0));
        Assert.Equal(ruleVersion, reader.GetString(1));
        Assert.Equal(modelVersion, reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task SaveAsync_LostRace_ReturnsFalseAndWritesNothing()
    {
        var tenant = await CreateTenantAsync();
        var incident = Incident.Create(tenant, "campus-1", "ZoneIntrusion", "cam-1", $"edge-{Guid.NewGuid():N}");
        await _store.AddAsync(incident, Outbox("incident.created", tenant), CancellationToken.None);

        var loaded = await _store.GetAsync(tenant, incident.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.True(loaded.TransitionTo(IncidentState.Acknowledged, "staff-1").IsSuccess);

        await BumpVersionDirectlyAsync(tenant, incident.Id, version: 5);

        var outboxBefore = await QueryOutboxAsync(tenant);
        var saved = await _store.SaveAsync(
            loaded,
            expectedVersion: 0,
            new ActivityEntry("staff-1", null, DateTimeOffset.UtcNow),
            Outbox("incident.updated", tenant),
            CancellationToken.None);

        Assert.False(saved);
        Assert.Equal(outboxBefore.Count, (await QueryOutboxAsync(tenant)).Count);
        Assert.Equal(0, await CountActivityAsync(tenant, incident.Id));
    }

    [Fact]
    public async Task SaveAsync_Success_RecordsActivityAndOutbox()
    {
        var tenant = await CreateTenantAsync();
        var incident = Incident.Create(tenant, "campus-1", "ZoneIntrusion", "cam-1", $"edge-{Guid.NewGuid():N}");
        await _store.AddAsync(incident, Outbox("incident.created", tenant), CancellationToken.None);

        var loaded = await _store.GetAsync(tenant, incident.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.True(loaded.TransitionTo(IncidentState.Acknowledged, "staff-1").IsSuccess);

        var saved = await _store.SaveAsync(
            loaded,
            expectedVersion: 0,
            new ActivityEntry("staff-1", null, DateTimeOffset.UtcNow),
            Outbox("incident.updated", tenant),
            CancellationToken.None);

        Assert.True(saved);
        var reloaded = await _store.GetAsync(tenant, incident.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(IncidentState.Acknowledged, reloaded.State);
        Assert.Equal(1, reloaded.Version);

        await using var connection = await OpenConnectionAsync();
        await using var activityCommand = new NpgsqlCommand(
            "SELECT actor_id, reason FROM incident_activity WHERE tenant_id = $1 AND incident_id = $2",
            connection);
        activityCommand.Parameters.AddWithValue(tenant);
        activityCommand.Parameters.AddWithValue(incident.Id);
        await using var reader = await activityCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("staff-1", reader.GetString(0));
        Assert.True(await reader.IsDBNullAsync(1));
        Assert.False(await reader.ReadAsync());

        var types = (await QueryOutboxAsync(tenant)).Select(row => row.Type).ToList();
        Assert.Contains("incident.created", types);
        Assert.Contains("incident.updated", types);
    }

    private async Task<string> CreateTenantAsync()
    {
        var tenant = $"t-{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO tenants (id, name) VALUES ($1, $2)", connection);
        command.Parameters.AddWithValue(tenant);
        command.Parameters.AddWithValue(tenant);
        await command.ExecuteNonQueryAsync();
        return tenant;
    }

    private async Task BumpVersionDirectlyAsync(string tenant, Guid incidentId, int version)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE incidents SET version = $1 WHERE tenant_id = $2 AND id = $3", connection);
        command.Parameters.AddWithValue(version);
        command.Parameters.AddWithValue(tenant);
        command.Parameters.AddWithValue(incidentId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<int> CountActivityAsync(string tenant, Guid incidentId)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM incident_activity WHERE tenant_id = $1 AND incident_id = $2", connection);
        command.Parameters.AddWithValue(tenant);
        command.Parameters.AddWithValue(incidentId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    private async Task<IReadOnlyList<(string Type, string Payload)>> QueryOutboxAsync(string tenant)
    {
        var rows = new List<(string Type, string Payload)>();
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT type, payload FROM outbox WHERE tenant_id = $1 ORDER BY created_at", connection);
        command.Parameters.AddWithValue(tenant);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private static OutboxEntry Outbox(string type, string tenant) =>
        new(tenant, type, "{\"source\":\"test\"}");

    private static string FindMigrationFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "EduSphere.Safety.Infrastructure", "Migrations", "001_foundation.sql");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate 001_foundation.sql above the test assembly.");
    }
}
