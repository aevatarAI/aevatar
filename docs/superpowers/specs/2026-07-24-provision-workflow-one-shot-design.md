# Provision Workflow First-Class One-Shot Design

## Problem

Issue #2962 tracks a semantic mismatch in the C1 `/provision-workflow` path.
When the caller omits `cron` and leaves `RunImmediately=true`,
`StudioWorkflowProvisioningService` currently synthesizes a five-field cron
such as `31 10 19 6 *`.

That expression is not one-shot. It is an annual recurrence at a fixed
minute, hour, day, and month. If the first fire cannot invoke the newly bound
workflow service, the next natural cron occurrence is one year later. The
mapping also bypasses the first-class one-shot lifecycle already owned by
`ScheduledDispatchGAgent`.

## Goal

Map C1 execution intent onto the existing typed schedule model:

- no caller cron and `RunImmediately=true` becomes
  `ScheduledDispatchScheduleMode.OneShotAtUtc`;
- caller cron becomes `ScheduledDispatchScheduleMode.RecurringCron`;
- no caller cron and `RunImmediately=false` remains bind-only and creates no
  schedule.

The change must not alter scheduled credential selection, Agent Key handling,
member/workflow identity, binding acknowledgement semantics, or the
authoritative scheduled-dispatch actor.

## Semantic Decision

`RunImmediately` means "schedule one near-future fire" in the C1 provisioning
contract. It does not mean "encode a calendar date as a recurring cron" and it
does not mean "dispatch an inline request-path run-now command."

For a one-shot request, the service constructs:

- `ScheduleMode = ScheduledDispatchScheduleMode.OneShotAtUtc`;
- `OneShotFireAt = TimeProvider.GetUtcNow() +
  ProvisionWorkflowRequest.DefaultOneShotDelaySeconds`;
- `CronExpression = string.Empty`;
- `Timezone = ScheduledDispatchCalculator.DefaultTimezone`.

The UTC timestamp preserves seconds. It does not round to a cron minute because
the one-shot contract accepts an exact `DateTimeOffset`.

For a recurring request, the service preserves the normalized caller cron and
timezone and constructs:

- `ScheduleMode = ScheduledDispatchScheduleMode.RecurringCron`;
- `OneShotFireAt = null`.

## Application Flow

`StudioWorkflowProvisioningService` keeps the current sequence:

1. Admit external capabilities before mutation.
2. Resolve or create the deterministic Team member.
3. Dispatch the asynchronous member binding command.
4. If the request has executable schedule intent, ensure the deterministic
   schedule with the caller credential source.
5. Return the existing accepted receipt with member, binding-run, schedule,
   Studio, and Observatory identities.

Only step 4 changes. A small schedule-timing value object or tuple resolves the
four scheduling fields together so cron, timezone, mode, and one-shot time
cannot drift independently.

The scheduled-dispatch application and actor layers remain unchanged. They
already validate, persist, fire, complete, and retire first-class one-shot
schedules.

## Error Handling

One-shot validation continues to be owned by
`ScheduledDispatchApplicationService`. The C1 path supplies a future UTC
timestamp using the injected `TimeProvider`; it does not duplicate lower-level
validation.

Recurring cron validation also remains in the scheduled-dispatch application
service. The C1 path only normalizes the caller timezone as it does today.

This change does not poll binding readiness. A binding that is still pending
when the one-shot fires remains part of the broader asynchronous provisioning
lifecycle tracked by #2679. This issue removes the false annual retry
representation without claiming to complete that lifecycle.

## Compatibility

The public `/provision-workflow` request and response shapes do not change.
No protobuf field, endpoint, credential contract, schedule identifier, member
identifier, or workflow identifier changes.

Behavior changes only for `RunImmediately=true` requests without a caller
cron:

- before: fixed-date annual cron;
- after: typed one-shot schedule that terminates through the canonical
  scheduled-dispatch lifecycle.

Caller-supplied recurring schedules and bind-only requests retain their
existing behavior.

## Tests And Verification

Focused tests will prove:

- a deterministic clock produces the exact future `OneShotFireAt`;
- no-cron immediate provisioning uses `OneShotAtUtc`;
- its cron expression is empty and it no longer carries a fixed-date annual
  cron;
- caller cron provisioning remains `RecurringCron`, preserves timezone, and
  has no one-shot timestamp;
- `RunImmediately=false` without cron still creates no schedule.

Verification includes the focused Studio provisioning tests, the Studio test
project, `test_stability_guards.sh`, `workflow_binding_boundary_guard.sh`,
documentation lint, architecture guards, and an affected project or solution
build.
