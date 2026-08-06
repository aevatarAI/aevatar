# Standardized Failure Toast Design

## Context

Workflow Activity vNext routes typed backend failures through the shared
`ConsoleToast` API. The API currently delegates to Ant Design `message`, which
renders a wide top-center surface without a visible dismiss control. Actionable
failure content can contain a title, guidance, and recovery actions, so the
result obscures page context and does not follow the console's notification
layout.

The failure presentation already owns typed category, intent, recovery action,
tracking ID, and deduplication data. This change must preserve those semantics
and adjust only toast presentation and timing.

## Selected Approach

Keep `ConsoleToast` as the single application-level API, but render it through
Ant Design `notification` instead of `message`. Notification supplies the
required top-right placement, visible close control, hover-paused timer,
stacking, and accessible dismissal behavior without introducing a second toast
system or custom timer registry.

The public `toast.error/info/success/warning` methods and the existing `key`,
`duration`, and `onClose` options remain stable. Existing callers do not manage
notification lifecycle or depend on Ant Design internals.

## Visual Design

The notification uses the compact information hierarchy selected from the
approved visual comparison and the top-right placement selected from the
alternative layout:

- a semantic status icon appears at the leading edge;
- the primary message is the first, strongest line;
- guidance and optional retry timing use secondary text;
- recovery and tracking-ID actions remain in a compact action row;
- the close icon occupies a separate top-right control and never competes with
  recovery actions;
- the desktop surface is approximately 360 pixels wide;
- the mobile surface is constrained to the viewport with 16-pixel side margins;
- spacing, radius, border, shadow, and colors use existing Aevatar and Ant
  Design tokens rather than page-local hard-coded styling.

## Behavior

Workflow Activity failure notifications close automatically after eight
seconds. Hovering pauses the timer, and the close control dismisses the
notification immediately. Ant Design notification owns both behaviors.

All typed run-failure categories use the same eight-second duration, including
warning- and information-toned failures. Ordinary success and informational
toasts outside this failure flow retain their shorter shared defaults.

Existing notification keys continue to deduplicate repeated failures. Existing
actions such as `Reload latest`, `Retry`, `Sign in again`, and `Copy tracking
ID` retain their current callbacks and identity semantics.

## Component Boundaries

`ConsoleToast` owns the Ant Design notification adapter, placement, dismiss
behavior, intent mapping, and shared visual class. It does not classify business
failures.

`runFailurePresentation` continues to classify typed API and committed-run
evidence, produce user-safe copy, and expose recovery actions. It changes only
the duration policy from category-specific persistent or short-lived values to
the approved eight-second failure duration.

Workflow Activity pages continue to call the shared toast API with a stable key
and the existing `RunFailureToastContent`. They do not add local portals,
timers, or close state.

## Accessibility

The notification keeps Ant Design's accessible status announcement and close
button. The close control has an accessible name and supports keyboard
activation. Recovery actions remain real buttons with visible labels and native
focus behavior. Information is not communicated by color alone because each
notification combines intent, icon, text, and labeled actions.

## Verification

Test-driven implementation will add focused tests before production changes.
The shared toast adapter tests will prove that notification receives the
top-right placement, visible close control, hover pause, semantic intent,
duration, key, and close callback. Failure-presentation tests will prove that
all typed failure categories use eight seconds while action and tracking-ID
behavior remains intact.

Local verification is limited to dependency-related Jest tests, explicit new or
changed test files, the frontend test-stability guard, and changed-file Biome
checks. Full frontend test, typecheck, and production build remain delegated to
GitHub CI by the personal frontend workflow policy.

## Delivery

The change is delivered as a focused follow-up pull request from
`fix/2026-08-06_standardize-failure-toast` into
`feat/2026-08-04_workflow-activity-vnext`. The pull request includes the exact
focused validation commands and does not include unrelated frontend changes.
