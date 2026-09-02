# Provision Workflow First-Class One-Shot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make C1 `/provision-workflow` map no-cron `RunImmediately=true` requests to the existing typed `OneShotAtUtc` scheduled-dispatch contract instead of a fixed-date annual cron.

**Architecture:** Keep `StudioWorkflowProvisioningService` as the C1 application orchestrator and `ScheduledDispatchGAgent` as the single schedule authority. Resolve cron, timezone, schedule mode, and one-shot fire time as one private value, then pass that value into the existing deterministic `EnsureAsync` path. Do not add binding polling, a request-path run-now call, or a second scheduler.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, existing ScheduledDispatch application contracts.

## Global Constraints

- Preserve the asynchronous accepted binding contract; do not call `GetBindingRunAsync` in the request path.
- Preserve scheduled credential and Agent Key selection unchanged.
- Preserve deterministic member, workflow, and schedule identities.
- Use `ScheduledDispatchScheduleMode.OneShotAtUtc` only when no caller cron is present and `RunImmediately=true`.
- Use `ScheduledDispatchScheduleMode.RecurringCron` for every caller-supplied cron.
- Keep `RunImmediately=false` with no cron as bind-only/no-schedule.
- Do not add `Task.Delay`, polling, process-local state, or a new projection/read-model path.

---

### Task 1: Map C1 immediate execution to typed one-shot scheduling

**Files:**
- Modify: `test/Aevatar.Studio.Tests/StudioWorkflowProvisioningServiceTests.cs:341-435`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioWorkflowProvisioningService.cs:187-200`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioWorkflowProvisioningService.cs:269-385`

**Interfaces:**
- Consumes: `ScheduledDispatchConfiguration`, `ScheduledDispatchScheduleMode`, `TimeProvider`, `ProvisionWorkflowRequest.DefaultOneShotDelaySeconds`.
- Produces: a private `ProvisionScheduleTiming` value containing `CronExpression`, `Timezone`, `ScheduleMode`, and `OneShotFireAt`; no public API changes.

- [ ] **Step 1: Replace the annual-cron expectation with a failing typed one-shot test**

Update the existing no-cron test to:

```csharp
[Fact]
public async Task ProvisionAsync_DefaultsToFirstClassOneShot_WhenNoCronSupplied()
{
    var member = NewMemberService();
    var schedule = new RecordingScheduleService { ScheduleId = ScheduleId };
    var sut = NewService(member, schedule, out var time);
    time.SetUtcNow(new DateTimeOffset(2026, 6, 19, 10, 30, 15, TimeSpan.Zero));

    await sut.ProvisionAsync(
        ScopeId,
        Caller,
        new ProvisionWorkflowRequest(DisplayName: "Monitor", WorkflowYaml: "name: monitor", Prompt: "go")
        {
            TeamId = TeamId,
        });

    schedule.Configuration!.ScheduleMode.Should().Be(ScheduledDispatchScheduleMode.OneShotAtUtc);
    schedule.Configuration.OneShotFireAt.Should()
        .Be(new DateTimeOffset(2026, 6, 19, 10, 30, 45, TimeSpan.Zero));
    schedule.Configuration.CronExpression.Should().BeEmpty();
    schedule.Configuration.Timezone.Should().Be(ScheduledDispatchCalculator.DefaultTimezone);
}
```

Extend the recurring assertions:

```csharp
schedule.Configuration!.ScheduleMode.Should().Be(ScheduledDispatchScheduleMode.RecurringCron);
schedule.Configuration.OneShotFireAt.Should().BeNull();
schedule.Configuration.CronExpression.Should().Be("*/15 * * * *");
schedule.Configuration.Timezone.Should().Be("Asia/Shanghai");
```

Also add the same recurring-mode and null-one-shot assertions to
`ProvisionAsync_RunImmediatelyFalseWithCron_StillCreatesRecurringSchedule`.

- [ ] **Step 2: Run the focused test and verify the expected red failure**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~StudioWorkflowProvisioningServiceTests.ProvisionAsync_DefaultsToFirstClassOneShot_WhenNoCronSupplied'
```

Expected: FAIL because the current configuration has
`ScheduleMode=RecurringCron` and a non-empty fixed-date cron.

- [ ] **Step 3: Resolve all schedule timing fields as one private value**

Replace `ResolveCron` with:

```csharp
private ProvisionScheduleTiming ResolveScheduleTiming(ProvisionWorkflowRequest request)
{
    var callerCron = NormalizeOptional(request.Cron);
    if (callerCron != null)
    {
        return new ProvisionScheduleTiming(
            callerCron,
            ScheduledDispatchCalculator.NormalizeTimezone(request.Timezone),
            ScheduledDispatchScheduleMode.RecurringCron,
            null);
    }

    return new ProvisionScheduleTiming(
        string.Empty,
        ScheduledDispatchCalculator.DefaultTimezone,
        ScheduledDispatchScheduleMode.OneShotAtUtc,
        _timeProvider
            .GetUtcNow()
            .AddSeconds(ProvisionWorkflowRequest.DefaultOneShotDelaySeconds)
            .ToUniversalTime());
}

private readonly record struct ProvisionScheduleTiming(
    string CronExpression,
    string Timezone,
    ScheduledDispatchScheduleMode ScheduleMode,
    DateTimeOffset? OneShotFireAt);
```

Update the XML comment to describe typed recurring versus one-shot resolution
and remove the annual-cron explanation.

- [ ] **Step 4: Thread the timing value through deterministic schedule ensure**

At the provisioning call site:

```csharp
var timing = ResolveScheduleTiming(request);
var auth = BuildScheduleAuth(subjectRef);
scheduleId = await EnsureProvisionScheduleAsync(
    normalizedScopeId,
    publishedServiceId,
    request.Prompt ?? string.Empty,
    auth,
    timing,
    ct);
```

Change `EnsureProvisionScheduleAsync` and `BuildScheduleConfiguration` to accept
`ProvisionScheduleTiming timing`, then construct:

```csharp
CronExpression: timing.CronExpression,
Timezone: timing.Timezone,
Enabled: true,
Headers: new Dictionary<string, string>(StringComparer.Ordinal),
ScheduleKind: ScheduledDispatchScheduleKind.Workflow,
ScheduleMode: timing.ScheduleMode,
OneShotFireAt: timing.OneShotFireAt);
```

Do not change auth, target, payload, schedule id generation, or accepted receipt
logic.

- [ ] **Step 5: Run the focused red-green test**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~StudioWorkflowProvisioningServiceTests.ProvisionAsync_DefaultsToFirstClassOneShot_WhenNoCronSupplied'
```

Expected: PASS.

- [ ] **Step 6: Run all provisioning service tests**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~StudioWorkflowProvisioningServiceTests'
```

Expected: 26 tests pass with zero failures.

- [ ] **Step 7: Run required guards and documentation checks**

Run:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/docs/lint.sh
bash tools/ci/architecture_guards.sh
```

Expected: every command exits 0.

- [ ] **Step 8: Build the affected Studio project**

Run:

```bash
dotnet build src/Aevatar.Studio.Application/Aevatar.Studio.Application.csproj --nologo
```

Expected: build exits 0. Existing repository warnings may remain, but there
must be no new compiler error.

- [ ] **Step 9: Commit the implementation**

Run:

```bash
git add \
  src/Aevatar.Studio.Application/Studio/Services/StudioWorkflowProvisioningService.cs \
  test/Aevatar.Studio.Tests/StudioWorkflowProvisioningServiceTests.cs \
  docs/superpowers/plans/2026-07-24-provision-workflow-one-shot.md
git commit -m "Use typed one-shot workflow provisioning"
```

Expected: one focused implementation commit referencing issue #2962 in the
subsequent GitHub comment or PR/push handoff.
