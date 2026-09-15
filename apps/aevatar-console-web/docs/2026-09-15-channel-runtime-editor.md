# Channel Runtime Editor

Channel details now provides an Edit action at
`/scopes/:scopeId/workflow-activity-vnext/channels/:registrationId/edit`.
The editor follows the approved [Figma frame](https://www.figma.com/design/FaVJx5IZeQX9jHcUi55ndB/?node-id=63-327)
and the existing Telegram connection form.

## Shared Components

- `ChannelSkillField` is shared by connection and editing, including its label,
  optional state, input, and field error presentation.
- Both pages use `ChannelServicePicker`, `useChannelServiceChoices`, the existing
  connection styles, shell, loading skeleton, and toast provider.
- The primary form contains Skill name and Services. Advanced settings is
  collapsed initially and contains Skill version and Bot instructions.
- Channel names and bot tokens are not editable through this API. Tool sets,
  extra tool names, and credential source are retained from the loaded config.

## API Behavior

The API contract was checked against `origin/feature/integrate` at
`dc4862789e0f70b7dd27e7d785f63f91c308118b`, specifically
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
- `202 Accepted` does not produce a saved toast. Readback must match the submitted
  configuration and have an authoritative state version newer than the initial
  GET. Delayed or failed confirmation preserves the request and offers Check
  again, which issues only GET. There is no background polling.
- Backend field errors map to localized fields without retaining raw diagnostic
  messages. Unrecognized config modes fail closed; unexpected credential fields
  are discarded by the adapter.
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

Eight new integration cases cover exact identity, safe decoding, unsupported
config, detail-to-edit navigation, whole-config preservation, accepted readback,
explicit clearing, unavailable services, field errors, cached detail revalidation,
legacy authorization, confirmation retry, unsaved navigation, and unmounts.
Existing creation, channel listing/details, API, navigation, route configuration,
and locale tests protect the reused surfaces.

Browser verification uses the configured remote backend: actual configuration
prefill, service selection, collapsed/expanded advanced fields, and desktop and
390px mobile layouts. It does not submit a configuration change to the live bot.
The local preview runs on port 5193 with its OAuth callback on the same origin.

Local verification is restricted to affected Jest files, changed-file Biome,
the test stability guard, baseline integrity, and diff checks. Full frontend
tests, typecheck, and production build are delegated to GitHub CI; no native
affected TypeScript target is available.
