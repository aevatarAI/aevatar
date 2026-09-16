# Channel Runtime Editor

Channel details now provides an Edit action at
`/scopes/:scopeId/workflow-activity-vnext/channels/:registrationId/edit`.
Edit and Remove sit at the top right beside the breadcrumb, with a 12px gap.
On narrow screens, the action group wraps below the breadcrumb and stays right aligned.
The editor follows the approved [Figma frame](https://www.figma.com/design/FaVJx5IZeQX9jHcUi55ndB/?node-id=63-327)
and the existing Telegram connection form, with the September 16 request
to limit editing to Label and Skill name.

## Shared Components

- `ChannelSkillField` is shared by connection and editing, including its label,
  optional state, input, and field error presentation.
- Both pages reuse the existing connection styles, centered content, shell,
  loading skeleton, and toast provider.
- The edit form contains only Label and Skill name. It does not load the
  UserService inventory or display a Services selector. Creation retains its
  existing service selection; details retain their read-only authorization list.
- Bot tokens are not editable. Tool sets, extra tool names, instructions,
  service selectors, skill version, and credential source are retained from the
  loaded config (clearing Skill name also clears its version).

## API Behavior

The runtime-config contract was checked against `origin/feature/integrate` at
`6c929a00db3d7636c91bc719478043a5fd331f7c`, specifically `ChannelCallbackEndpoints.cs`. Label updates use NyxID's existing
`backend/src/handlers/channel_bots.rs` contract; no backend changes are required.

- GET and POST use `/api/channels/registrations/{registrationId}/runtime-config`.
  Detail identity and scope must match the route.
- Opening the editor requires fresh successful config, registration-list, and
  personal NyxID bot-list reads, even when Query has cached data. The exact owned
  registration's `nyx_channel_bot_id` and matching platform identify the bot.
  Missing/cross-scope identities block the editor; registration IDs never serve
  as bot IDs. Later readback never resets user input.
- Label uses the actual NyxID bot label, not the runtime-config `label` placeholder
  (currently the registration ID). Only a changed label triggers
  `PATCH /api/v1/channel-bots/{botId}` with `{ "label": "..." }`. NyxID requires a
  non-empty trimmed label of at most 128 UTF-8 bytes. The PATCH response must
  confirm the exact bot ID, platform, and label before the safe query cache is
  updated; credential fields and raw diagnostic bodies are discarded.
- A changed Skill name submits the complete known runtime config because omitted
  runtime fields are not patches. Only Skill name is trimmed; other fields are
  retained. The request omits `authorization_mode` and `service_ids`, allowing
  the backend to preserve its current service selection, including legacy
  defaults and explicit empty allowlists. Backend authorization checks still
  apply; there is no frontend authorization override.
- When both fields change, Label saves first, then Skill name. A rejected Label
  request leaves both inputs intact and skips the runtime update. If Label has
  saved but Skill fails, the editor explicitly reports partial success and a
  retry submits only the still-unsaved changes. The two APIs are not atomic.
- A Label-only save returns to details after the confirmed PATCH without
  submitting runtime config or performing an unnecessary config readback.
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
explicit clearing, label validation and exact bot identity, partial-save retry,
field errors, cached detail revalidation,
legacy authorization, delayed/failed readback navigation, duplicate-submit
protection, failed-save retry, unsaved navigation, and unmounts during POST/GET.
Existing creation, channel listing/details, API, navigation, route configuration,
and locale tests protect the reused surfaces.

The original editor was verified against the configured remote backend for
configuration prefill and desktop and 390px layouts. The names-only revision is
covered by API-boundary integration tests. No live bot configuration is changed
for verification.

Local verification is restricted to affected Jest files, changed-file Biome,
the test stability guard, baseline integrity, and diff checks. Full frontend
tests, typecheck, and production build are delegated to GitHub CI; no native
affected TypeScript target is available.
