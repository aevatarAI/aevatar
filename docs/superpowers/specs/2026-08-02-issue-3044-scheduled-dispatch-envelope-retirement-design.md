# Issue 3044 Scheduled Dispatch Envelope Retirement Design

## Context

Before this change, `POST /api/schedules` and
`PUT /api/schedules/{scheduleId}` accepted
an `envelope` target containing a caller-selected actor ID and arbitrary
`EventEnvelope` payload. The target preparation path rewrites routing but
preserves both authority-bearing inputs. An authenticated caller can therefore
persist control or domain messages for an actor outside the caller's scope and
trigger them on a cron schedule or with `:run-now`.

Production callers do not require raw-envelope scheduling. Workflow schedules,
Studio member automations, and generic public schedules already use typed
service invocation targets. The raw target remains only in legacy state and
tests.

## Decision

Retire raw-envelope scheduling instead of adding a payload allowlist. Public
scheduling accepts only a typed `serviceInvocation` target that is resolved
through the service catalog and revision catalog. The HTTP boundary requires
the resolved target tenant to equal the authenticated request scope before
calling the Application service. The public schedule target input does not
accept a caller-supplied `actorId`, raw `EventEnvelope`, or internal authority
marker.

The retirement is enforced at three independent boundaries:

1. **HTTP contract:** remove the `envelope` request property and its request
   type. Unmapped-member rejection makes an external envelope payload a stable
   `400 Bad Request`. Cross-scope service targets are rejected before mutation.
2. **Application contract:** reject new envelope configurations even when an
   internal caller constructs `ScheduledDispatchConfiguration` directly. Hide
   legacy envelope rows from get/list and reject their lifecycle mutations as
   not found.
3. **Actor runtime:** require a typed Protobuf `TrustedInternal` authority on
   every new envelope configuration command. On activation, an existing
   envelope schedule without that authority is durably disabled and its
   callbacks are purged. Manual fire also fails before any target dispatch.
   This protects persisted schedules created before the HTTP contract changes
   while preserving the explicit low-level actor-only protocol used by runtime
   tests. `TrustedInternal` is not surfaced by Hosting or Application and does
   not authorize a public or administrator raw-envelope API.

At the Hosting/Application boundary, legacy Protobuf/state fields and enum
values are retired and read only: they remain available only to recognize and
hide legacy rows. Core retains the legacy representation for retirement and
fail-closed activation, and retains the typed Protobuf `TrustedInternal`
actor-only write protocol. The authority marker is not surfaced through
Hosting or Application, and no administrator/raw-envelope API is introduced.

## Alternatives

### Remove Only the HTTP Field

This is the smallest source change, but existing malicious schedules could
still fire automatically or through lifecycle endpoints. It does not isolate
the raw capability and is rejected.

### Delete Envelope State From Protobuf

This removes the representation completely, but historical actor event replay
would lose the target or fail activation before it can be disabled. That turns
a security retirement into an unsafe state migration. Keeping read-only legacy
recognition is safer and more explicit.

### Payload Allowlist and Actor Ownership Lookup

An allowlist would retain caller control over actor addresses and create a
second authorization system beside typed service invocation. Every future
message type would reopen the injection surface. The repository already has
the correct typed, server-resolved path, so a second public path is
unnecessary.

## Data Flow

For accepted public writes:

```text
HTTP serviceInvocation request
  -> catalog/revision resolution
  -> authenticated scope versus target tenant check
  -> Application service-invocation-only admission
  -> ScheduledDispatch actor configuration
  -> typed service invocation adapter at fire time
```

For rejected raw writes:

```text
HTTP envelope member -> JSON unmapped-member rejection -> 400, no mutation
direct envelope configuration -> Application rejection, no actor dispatch
internal actor envelope without TrustedInternal -> Core rejection
```

For historical raw schedules:

```text
projection row -> hidden from public get/list/lifecycle
actor activation without TrustedInternal -> disable event + callback purge
manual fire -> fail before FireStarted or target dispatch
```

## Authority Rules

- `scopeId`, `memberId`, `workflowId`, `publishedServiceId`, and `actorId` remain
  distinct identities in tests and code.
- A caller with `scope-alpha` cannot create or update a target whose
  `ServiceIdentity.TenantId` is `scope-beta`.
- Studio member automation keeps its exact
  `scope-alpha/team-alpha/m-alpha` owner checks and server-derived service
  target.
- A caller can never provide `actor-cross-owner` or a workflow control event as
  a public schedule target.

## Error Semantics

- A JSON `envelope` member is an invalid public request and maps to the existing
  `400` request-validation response.
- A cross-scope typed service target uses the existing scope access guard and
  returns the same non-leaking access-denied result as other scoped resources.
- A legacy envelope schedule is not visible or mutable through public schedule
  lifecycle APIs.
- Actor-level manual fire of a legacy envelope schedule throws a stable
  retirement error without dispatching an envelope.

## Verification

Tests must prove:

- raw HTTP envelope payloads are rejected, including a workflow stop/control
  payload addressed to a distinct actor ID;
- cross-scope typed service targets are rejected and same-scope targets remain
  accepted;
- direct Application envelope create/update is rejected before actor dispatch;
- legacy envelope rows are hidden from get/list and lifecycle mutation;
- new actor envelope configuration without typed internal authority is
  rejected, while the explicit trusted-internal protocol remains functional;
- activation disables a persisted unauthorized envelope schedule and manual
  fire never dispatches it;
- existing service invocation, workflow schedule, and Studio member automation
  tests remain green.

Required validation includes focused Scheduled Dispatch tests, affected project
test suites, architecture and stability guards, repository build/test, docs
lint, independent review, and GitHub CI.
