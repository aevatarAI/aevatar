# Channel Runtime Editor

Channel details now provides an Edit action at
`/scopes/:scopeId/workflow-activity-vnext/channels/:registrationId/edit`.
Edit and Remove sit at the top right beside the breadcrumb, with a 12px gap.
On narrow screens, the action group wraps below the breadcrumb and stays right aligned.
The editor follows the approved [Figma frame](https://www.figma.com/design/FaVJx5IZeQX9jHcUi55ndB/?node-id=63-327)
and the existing Telegram connection form.

## Shared Components

- `ChannelSkillField` is shared by connection and editing, including its label,
  optional state, input, and field error presentation.
- Both pages use `ChannelServicePicker`, `useChannelServiceChoices`, the existing
  connection styles, shell, loading skeleton, and toast provider.
- The form contains only Skill name and Services, with no Advanced settings
  section or inputs for Skill version and Bot instructions.
- Channel names and bot tokens are not editable through this API. Tool sets,
  extra tool names, instructions, skill version, and credential source are
  retained from the loaded config.

## API Behavior

The API contract was checked against `origin/feature/integrate` at
`6c929a00db3d7636c91bc719478043a5fd331f7c`, specifically
`ChannelCallbackEndpoints.cs` and `ChannelRegistrationServiceSelection.cs`.

- GET and POST use `/api/channels/registrations/{registrationId}/runtime-config`.
  Detail identity and scope must match the route.
- Opening the editor requires a fresh successful GET, including when Query has
  cached data from an earlier visit. Subsequent readback never resets user input.
- POST sends the complete known runtime config because omitted fields are not
  patches. Strings edited by the user are trimmed as the backend parser expects.
- Clearing Skill name also clears its version. Removing service authorization
  removes selectors for the deselected service, preserving other selectors.
- Services use the same authenticated inventory and bearer grant filtering as
  creation. Saved services absent from current choices remain visible as
  unavailable until deliberately removed. They are never silently dropped.
- Existing `nyxid_default` registrations retain their mode until the user
  explicitly switches to individual selection. Zero individually selected
  services is sent as an explicit empty allowlist.
- After an acknowledged POST, the form makes one GET for accurate feedback and
  returns to channel details. The Save action remains busy across both requests
  and rejects duplicate submission. There is no separate confirmation step,
  Check again button, background polling, or automatic resubmission.
- A saved toast requires that GET to match the submitted configuration and have
  an authoritative state version newer than the initial GET. Otherwise an
  informational toast reports only that changes were submitted; a failed GET
  produces a warning that the latest configuration could not be loaded. Details
  reloads real data on entry and never substitutes submitted values as saved
  facts. The user may refresh details later to see the current configuration.
- A rejected POST keeps the edited values, reports the error through a toast,
  and unlocks the form for correction and another explicit Save.
- Backend errors for editable fields map to localized field messages; errors
  for other fields use the shared save-failure toast. Raw diagnostic messages
  are not retained. Unrecognized config modes fail closed; unexpected credential
  fields are discarded by the adapter.
- Cancel and shell navigation protect unsaved changes. Reload/close uses the
  native before-unload warning. Late responses after unmount cannot navigate or
  report success.

## Design Baseline

- Baseline directory: `docs/design-baselines/workflow-activity-vnext/`.
- Primary design: `aevatar-workflow-activity-vnext.excalidraw`.
- SHA-256: `30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`.
- Contract: `docs/superpowers/specs/2026-08-04-workflow-activity-vnext-design.md`.
- User paths: `docs/superpowers/specs/2026-08-04-workflow-activity-vnext-user-paths.md`.
- The approved Figma frame supplements the baseline for Channel editing.
- Authentication, callback, session, returnTo, and Umi localization are reused.
- Production data comes only from real APIs and API-acknowledged user actions.
  Fixtures remain test-only; no backend, mock, or credential fallback is added.

## Verification Scope

Focused integration cases cover exact identity, safe decoding, unsupported
config, detail-to-edit navigation, whole-config preservation, accepted readback,
explicit clearing, unavailable services, field errors, cached detail revalidation,
legacy authorization, delayed/failed readback navigation, duplicate-submit
protection, failed-save retry, unsaved navigation, and unmounts during POST/GET.
Existing creation, channel listing/details, API, navigation, route configuration,
and locale tests protect the reused surfaces.

The original editor was verified against the configured remote backend for
configuration prefill, service selection, and desktop and 390px layouts.
The save-flow revision uses the focused integration cases above; the local
preview compiles on port 5173, its API proxy responds, and the browser shows the
login page. Its OAuth callback uses the same origin. No live bot configuration
was changed during verification.

Local verification is restricted to affected Jest files, changed-file Biome,
the test stability guard, baseline integrity, and diff checks. Full frontend
tests, typecheck, and production build are delegated to GitHub CI; no native
affected TypeScript target is available.
