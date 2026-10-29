# EduSphere Safety Plan 1 — Cloud Vertical-Slice Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `EduSphere.Safety` solution skeleton with tenant-scoped incident creation, idempotent ingest, outbox, and email dispatch, covered by unit + Postgres integration tests.

**Architecture:** Modular monolith: Shared (Result, mediator) → Core (incident state machine) → Application (commands, validators) → Infrastructure (Dapper repos, migration 001, outbox) → Api (endpoint modules, OIDC stub, SignalR) → Worker (leased outbox dispatch + SMTP email).

**Tech Stack:** .NET 10, Dapper, Npgsql, FluentValidation, xUnit, Testcontainers.PostgreSql, SMTP pickup-dir email, Serilog.

**Later plans (not this file):** Plan 2 edge pipeline (FFmpeg/ONNX/tracker/rules/simulator), Plan 3 Next.js workflows, Plan 4 hardening/deploy/Chrome validation.

---

## File structure

- Create: `EduSphere.Safety.slnx` (solution), `Directory.Packages.props`, `src/EduSphere.Safety.Shared/Result.cs`, `src/EduSphere.Safety.Shared/Mediator.cs`, `src/EduSphere.Safety.Shared/ServiceRegistrar.cs`
- Create: `src/EduSphere.Safety.Core/Incident.cs` (state machine New→Acknowledged→Investigating→Resolved/Dismissed)
- Create: `src/EduSphere.Safety.Application/CreateIncident.cs`, `src/EduSphere.Safety.Application/TransitionIncident.cs` (+ validators)
- Create: `src/EduSphere.Safety.Infrastructure/Migrations/001_foundation.sql`, `src/EduSphere.Safety.Infrastructure/IncidentRepository.cs`, `src/EduSphere.Safety.Infrastructure/Outbox.cs`
- Create: `src/EduSphere.Safety.Api/Program.cs`, `src/EduSphere.Safety.Api/IncidentsEndpoints.cs`, `src/EduSphere.Safety.Worker/OutboxDispatcher.cs`, `src/EduSphere.Safety.Worker/SmtpEmailSender.cs`
- Test: `tests/EduSphere.Safety.Tests/IncidentTests.cs`, `tests/EduSphere.Safety.Tests/IncidentRepositoryTests.cs` (Testcontainers)

---

### Task 1: Solution skeleton + Shared kernel

**Files:**
- Create: `EduSphere.Safety.slnx`, `Directory.Packages.props`
- Create: `src/EduSphere.Safety.Shared/Result.cs`
- Create: `src/EduSphere.Safety.Shared/Mediator.cs`
- Create: `src/EduSphere.Safety.Shared/ServiceRegistrar.cs`

- [ ] **Step 1: Create solution and Shared project**

Run: `dotnet new slnx -n EduSphere.Safety && dotnet new classlib -n EduSphere.Safety.Shared -o src/EduSphere.Safety.Shared -f net10.0 && dotnet sln EduSphere.Safety.slnx add src/EduSphere.Safety.Shared`
Expected: solution + project created, `dotnet build` PASS.

- [ ] **Step 2: Write `Result<T>` + mediator + registrar**

```csharp
// src/EduSphere.Safety.Shared/Result.cs
namespace EduSphere.Safety.Shared;
public sealed record Error(string Code, string Message);
public sealed class Result<T>
{
    public T? Value { get; }
    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    private Result(T? value, Error? error) { Value = value; Error = error; }
    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(string code, string message) => new(default, new Error(code, message));
}
```

```csharp
// src/EduSphere.Safety.Shared/Mediator.cs
namespace EduSphere.Safety.Shared;
public interface ICommand<TResponse> { }
public interface ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken ct);
}
public interface ISender
{
    Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken ct);
}
public sealed class Sender(IServiceProvider services) : ISender
{
    public async Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken ct)
    {
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResponse));
        dynamic handler = services.GetService(handlerType)
            ?? throw new InvalidOperationException($"No handler for {command.GetType().Name}");
        return await handler.HandleAsync((dynamic)command, ct);
    }
}
```

```csharp
// src/EduSphere.Safety.Shared/ServiceRegistrar.cs
namespace EduSphere.Safety.Shared;
public interface IServiceRegistrar { void Register(IServiceCollection services); }
```

- [ ] **Step 3: Build**

Run: `dotnet build EduSphere.Safety.slnx`
Expected: PASS with zero warnings/errors.

---

### Task 2: Core incident state machine (TDD)

**Files:**
- Create: `src/EduSphere.Safety.Core/Incident.cs`
- Test: `tests/EduSphere.Safety.Tests/IncidentTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EduSphere.Safety.Tests/IncidentTests.cs
using EduSphere.Safety.Core;
using Xunit;
public sealed class IncidentTests
{
    [Fact]
    public void New_incident_can_be_acknowledged()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        var result = incident.TransitionTo(IncidentState.Acknowledged, "staff-1");
        Assert.True(result.IsSuccess);
        Assert.Equal(IncidentState.Acknowledged, incident.State);
    }

    [Fact]
    public void New_incident_cannot_skip_to_resolved()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        var result = incident.TransitionTo(IncidentState.Resolved, "staff-1");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Dismiss_requires_reason()
    {
        var incident = Incident.Create("t1", "campus-a", "ZoneIntrusion", "cam-1");
        incident.TransitionTo(IncidentState.Acknowledged, "staff-1");
        incident.TransitionTo(IncidentState.Investigating, "staff-1");
        Assert.False(incident.TransitionTo(IncidentState.Dismissed, "staff-1", reason: null).IsSuccess);
        Assert.True(incident.TransitionTo(IncidentState.Dismissed, "staff-1", reason: "false alarm").IsSuccess);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EduSphere.Safety.Tests --filter IncidentTests`
Expected: FAIL (Core project/type missing).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EduSphere.Safety.Core/Incident.cs
using EduSphere.Safety.Shared;
namespace EduSphere.Safety.Core;
public enum IncidentState { New, Acknowledged, Investigating, Resolved, Dismissed }
public sealed class Incident
{
    public Guid Id { get; } = Guid.NewGuid();
    public string TenantId { get; }
    public string CampusId { get; }
    public string EventType { get; }
    public string CameraId { get; }
    public IncidentState State { get; private set; } = IncidentState.New;
    public int Version { get; private set; }
    private Incident(string tenantId, string campusId, string eventType, string cameraId)
    { TenantId = tenantId; CampusId = campusId; EventType = eventType; CameraId = cameraId; }
    public static Incident Create(string tenantId, string campusId, string eventType, string cameraId)
        => new(tenantId, campusId, eventType, cameraId);
    private static readonly Dictionary<IncidentState, IncidentState[]> Allowed = new()
    {
        [IncidentState.New] = [IncidentState.Acknowledged],
        [IncidentState.Acknowledged] = [IncidentState.Investigating, IncidentState.Dismissed],
        [IncidentState.Investigating] = [IncidentState.Resolved, IncidentState.Dismissed],
        [IncidentState.Resolved] = [],
        [IncidentState.Dismissed] = [],
    };
    public Result<Incident> TransitionTo(IncidentState next, string actorId, string? reason = null)
    {
        if (!Allowed[State].Contains(next))
            return Result<Incident>.Fail("incident.invalid_transition", $"Cannot move from {State} to {next}.");
        if (next == IncidentState.Dismissed && string.IsNullOrWhiteSpace(reason))
            return Result<Incident>.Fail("incident.dismiss_reason_required", "Dismissal requires a reason.");
        State = next;
        Version++;
        return Result<Incident>.Ok(this);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EduSphere.Safety.Tests --filter IncidentTests`
Expected: 3/3 PASS.

---

### Task 3: Application commands — idempotent create + ETag transition

**Files:**
- Create: `src/EduSphere.Safety.Application/CreateIncident.cs`
- Create: `src/EduSphere.Safety.Application/TransitionIncident.cs`
- Test: extend `tests/EduSphere.Safety.Tests/IncidentTests.cs` with handler tests using an in-memory `IIncidentStore` fake defined in the test file.

- [ ] **Step 1: Write failing handler tests** (fake store keyed by `(tenantId, edgeEventId)` returning existing incident on duplicate create; transition handler rejects stale `If-Match` version with `incident.concurrency_conflict`). Also add a `Sender` dispatch test (happy-path + missing-handler `InvalidOperationException`). Conventions (from Task 1 quality review): register `Sender` as Scoped/Transient (handlers use scoped services — never Singleton from root provider); thrown handler exceptions mean 500 by convention, only expected domain failures become `Result.Fail`.

- [ ] **Step 2: Run to verify they fail** (`dotnet test --filter IncidentHandlerTests`, expect FAIL).

- [ ] **Step 3: Implement** `CreateIncidentCommand(edgeEventId, tenantId, campusId, eventType, cameraId, detectedAtUtc, ruleVersion, modelVersion)` → returns existing incident id when the same `(tenantId, edgeEventId)` was already ingested; `TransitionIncidentCommand(incidentId, tenantId, expectedVersion, next, actorId, reason)` → handler loads tenant-scoped incident (`GetAsync`; null → `incident.not_found`), checks `Version == expectedVersion` (`incident.concurrency_conflict`), calls `Incident.TransitionTo` (domain `Fail` keeps its code — NEVER mapped to conflict), then `SaveAsync` with version predicate (false → `incident.concurrency_conflict`, lost race). Concurrency contract (decided Task 2 review, refined Task 3 verification): optimistic concurrency lives in the STORE layer (`UPDATE ... WHERE tenant_id AND id AND version = expected`); Core's `Version` is the counter, no `expectedVersion` param on `TransitionTo`. Store interface is load-then-save (`GetAsync` + `SaveAsync(incident, expectedVersion, activity, outbox)`), NOT a mutate-callback: a callback returning bool cannot distinguish mutation failure from version conflict. Actor/reason/timestamp recording goes to `incident_activity` (Task 4); Application validates `actorId` non-empty but Core keeps its signature. Validators: `edgeEventId`/`tenantId`/`campusId`/`eventType`/`cameraId` required, `detectedAtUtc` not default and not more than 5 min in the future, dismiss requires non-whitespace reason. Each command writes its outbox row in the same unit of work via `IUnitOfWork`. Extend unit tests: terminal-state transitions fail, `Acknowledged→Resolved` fails, whitespace reason fails, `Version` increments, error codes asserted (`incident.invalid_transition`, `incident.dismiss_reason_required`, `incident.concurrency_conflict`). Transition map stays as spec'd (no New→Dismissed fast path, no reopen) unless the user asks. Test-double rules: the fake store MUST return snapshot copies (like a real repo materializes fresh instances), never live references; add post-failed-save assertions (state/version unchanged, no activity/outbox appended). Precedence pinned: version check runs before domain validation, so stale-version + illegal move → 409/conflict, current-version + illegal move → 422/invalid_transition (cover the combined case in tests). `incident.updated` outbox payload must include the resulting (post-increment) version. Deferred (tech debt, not this task): injected `IValidator<T>` instead of `new` per call, `TimeProvider` instead of `DateTimeOffset.UtcNow` in handlers.

- [ ] **Step 4: Run tests** (`dotnet test --filter IncidentHandlerTests`, expect PASS) plus full `dotnet test` green.

---

### Task 4: Postgres migration 001 + Dapper repository (Testcontainers)

**Files:**
- Create: `src/EduSphere.Safety.Infrastructure/Migrations/001_foundation.sql`
- Create: `src/EduSphere.Safety.Infrastructure/IncidentRepository.cs`
- Test: `tests/EduSphere.Safety.Tests/IncidentRepositoryTests.cs`

- [ ] **Step 1: Write failing integration tests**: insert + load round-trip; cross-tenant test (tenant B cannot load tenant A's incident id → not found); duplicate `(tenant_id, edge_event_id)` insert is rejected by unique constraint; outbox row exists after create.

- [ ] **Step 2: Run to verify they fail** (need Docker for Testcontainers; if Docker is unavailable, record as blocked and use `dotnet test --filter Category!=Integration`).

- [ ] **Step 3: Implement migration 001**: tables `tenants`, `tenant_members`, `incidents` (all with `tenant_id`, composite PK/FK `(tenant_id, id)`, `incidents` also has `edge_event_id` nullable + `version` int), `incident_activity` (incident ref, actor, reason, occurred_at), `outbox` (`id`, `tenant_id`, `type`, `payload JSONB`, `created_at`, `locked_until`, `attempts`); unique index on `incidents(tenant_id, edge_event_id)`; `IncidentRepository` with `tenant_id` predicate on every query; Migration 001 also ships minimal `devices` (id/key_hash/revoked) and `cameras` tables for Task 5 device auth, plus `schema_migrations` bookkeeping. `SaveAsync` uses a single-statement conditional `UPDATE ... SET state, version WHERE tenant_id AND id AND version = expected` (0 rows → false; write the supplied already-incremented version, never increment unconditionally; keep `tenant_id` in the WHERE even though the caller loaded the entity); activity + outbox inserts happen in the SAME transaction and ONLY when the predicate matched; `SET LOCAL app.tenant` per transaction.

- [ ] **Step 4: Run integration tests**, expect PASS; run `dotnet test` full suite green.

---

### Task 5: API ingest + incident endpoints + Worker email dispatch

**Files:**
- Create: `src/EduSphere.Safety.Api/Program.cs`, `src/EduSphere.Safety.Api/IncidentsEndpoints.cs`
- Create: `src/EduSphere.Safety.Worker/OutboxDispatcher.cs`, `src/EduSphere.Safety.Worker/SmtpEmailSender.cs`

- [ ] **Step 1: Write failing API tests** (use `WebApplicationFactory`: POST `/v1/ingest/events` twice with same `edgeEventId` → one incident, second returns same id; PATCH incident with stale `If-Match` → 409; tenant B token cannot read tenant A incident → 404).

- [ ] **Step 2: Run to verify they fail**, expect 404/compile failures.

- [ ] **Step 3: Implement**: `POST /v1/ingest/events` (device auth: `X-Device-Key` + key id, checked against `devices` table; revoked → 401) and `GET/PATCH /v1/incidents` + `GET /v1/incidents/{id}` (OIDC dev-stub JWT: real JWT validation against local stub issuer/audience, tenant from `tid` claim + `tenant_members`; never a header). Add a shared `AddSharedKernel()` extension (registers `ISender`→`Sender` as Transient) used by both Api and Worker. Before the commit step, add a standard .NET `.gitignore` (must cover `bin/`, `obj/`) so `git add src` never captures build artifacts. `ETag: "<version>"` on GET, `If-Match` required on PATCH. Error mapping: `incident.not_found` → 404, `incident.concurrency_conflict` → 409, `validation.failed` → 400, domain codes (`invalid_transition`, `dismiss_reason_required`) → 422. Idempotent duplicate create returns 200 with the existing id (not 201). CONCURRENCY: concurrent duplicate ingests must never 500 — `AddAsync` returns the effective id; on unique-violation (23505) it re-reads and returns the existing id WITHOUT writing a second outbox row. STARTUP: Worker refuses to start when `Notification:To` is empty or the email config (pickup dir or SMTP host) is invalid, and the dev signing key must be ≥32 bytes. `ListAsync` orders by `(created_at, id)`. Deferred to later plans (do NOT build now): recipient/escalation policy engine (pilot uses `Notification:To`), cursor pagination, SignalR `access_token` query-string auth + narrow CORS policy for the web app, per-device credential scoping at scale, TimeProvider/IValidator injection. SignalR hub `safety` pushes `incident.created/updated` to tenant-campus groups. Worker leases due outbox rows (`locked_until`), sends email via SMTP to pickup directory locally (`Email:PickupDirectory`), marks delivered/failed with attempts + backoff; dead-letter after 10 attempts surfaces in `GET /v1/ops/dead-letter`.

- [ ] **Step 4: Run API tests + full suite**, expect green. Manual check: `dotnet run --project src/EduSphere.Safety.Api`, POST a staged event, confirm pickup-dir email file appears and SignalR event fires.

- [ ] **Step 5: Commit Plan 1**

```bash
git add src tests docs/superpowers/plans/2026-10-29-edusphere-safety-plan1-foundation.md
git commit -m "feat: plan1 cloud vertical-slice foundation"
```

## Plan 1 acceptance

Staged `edgeEventId` ingested twice → single incident; ack via ETag PATCH; email file in pickup dir; cross-tenant read → 404; `dotnet test` green; no secrets in repo or logs.

## Sandbox execution notes (this dev sandbox only — never commit these as repo config)

- Socket `bind()` is blocked entirely (TCP + Unix), so: default NuGet restore's out-of-proc MSBuild walk fails silently, and `vstest` cannot execute (`dotnet test` builds, then aborts at test-run).
- Restore: `NUGET_PACKAGES=/Users/user/.nuget/packages dotnet restore <proj> /p:NuGetAudit=false /p:RestoreUseStaticGraphEvaluation=true` (warm cache; pins must exist in `~/.nuget/packages`).
- Build: prefix `MSBUILDDISABLENODEREUSE=1`, add `-m:1`, keep `/p:NuGetAudit=false`.
- Run consoles: build with `/p:UseAppHost=false`, execute via `dotnet exec ./bin/Debug/net10.0/<name>.dll` (apphost creation is blocked).
- `dotnet test` execution here is impossible; verify by (a) compiling the test assembly, (b) an in-process behavioral probe under `/tmp` mirroring the committed tests. Full `dotnet test` runs on a connected machine/CI.
- `TreatWarningsAsErrors`, `Nullable`, and NuGet audit stay ON in repo files; the `/p:` flags above are command-line only.
