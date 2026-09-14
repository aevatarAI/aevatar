# Channels in the Console

Channels is a first-level destination in the Workflow Activity vNext sidebar.
It lets the signed-in owner view their connected bots and inspect a connection.

The three pages follow the latest simplified [Figma design](https://www.figma.com/design/FaVJx5IZeQX9jHcUi55ndB?node-id=7-226)
(list frame `7:2`, detail frame `6:131`, Telegram form `7:226`). The current design intentionally keeps
channel details read-only with Remove as the sole resource action. The earlier
runtime-configuration editor is outside this iteration of issue #3617.

## Routes and existing setup

- `/scopes/:scopeId/workflow-activity-vnext/channels`: platform directory and
  owner connection list.
- `/scopes/:scopeId/workflow-activity-vnext/channels/:registrationId`: exact
  connection details. Registration IDs are opaque and encoded as one segment.
- `/scopes/:scopeId/workflow-activity-vnext/channels/connect/telegram`: the
  single-page Telegram connection form. Connect Telegram navigates here.
  Lark, Feishu, Discord, and Slack are marked Soon in the current design.
  The older backend `/channels` onboarding page remains independently available.

`AEVATAR_CHANNEL_WEBHOOK_BASE_URL` optionally sets the public backend callback
base URL at build time. The default is
`https://aevatar-console-backend-api.aevatar.ai`. It must be HTTPS without
credentials, query, or fragment. An invalid value disables registration with
an inline deployment-configuration message. It is never inferred from the
separate Console frontend origin or shown as an editable form field.

## Telegram connection form

The form accepts a masked bot token, Label, Skill name, and an explicit service
selection. The token reveal control is keyboard accessible. Unsaved form
navigation asks whether to discard; form data is never persisted. Only the
authenticated registration POST carries the token, and successful admission
or an uncertain result clears it. No registration mutation cache stores it.

Services come from the current NyxID session's
`GET /api/v1/user-services`, resolved against the existing NyxID authority.
Its `{services: [...]}` response differs from the Aevatar runtime-config
inventory: it uses `is_active` and `credential_source.type` (`personal` or
`org`, with an `allowed` flag on the organization variant). Unknown variants,
inactive services, and disallowed organization services cannot be selected.
Only safe display fields enter the query cache.

Options use label, then catalog service name, then slug. Search matches label
or slug and preserves selections outside the current search. Each selection
uses the exact `services[].id`; no default selection is made. The registration
POST always supplies `authorization_mode: explicit_service_allowlist` and
`service_ids`, including `[]` for no service access. It never substitutes a
slug, endpoint ID, API key ID, or omitted field. If a selected service becomes
unavailable, submission is blocked until it is deselected. Load failure has
inline retry, while a genuine empty inventory can connect without service access.

Registration uses existing `POST /api/channels/registrations` with
`platform`, `bot_token`, `label`, `default_skill_name`, `webhook_base_url`, and
the explicit selection above. A `202`/`status: accepted` response must contain
the registration ID. The page waits for that exact Telegram registration in
the owner list before showing success and opening details; inbound activity
continues to use its real status. Observation is bounded to 20 seconds and
Check again performs GET only. Network/server failures with uncertain effects
require checking Channels before another setup, preventing automatic duplicates.

### Outstanding bot-name contract

The design asks for pre-registration token recognition and independent bot-name
fallbacks for empty Label and Skill name. The reviewed `feature/integrate`
backend and #3617/#3620 do not expose a non-mutating token/name lookup route.
The deployed registration behavior also does not supply these fallbacks: it
generates a generic label and forwards the skill name unchanged. NyxID's
existing create/verify operations mutate registrations and cannot serve as a
read-only preview.

Until that server contract exists, the form explicitly reports that automatic
lookup is unavailable and requires both names. It does not claim a bot has
been recognized or submit a sample name. This is the remaining difference from
the requested design; the frontend does not add a backend route or call Telegram
directly with a token in its request URL. A future lookup must return the bot's
`first_name`, preserve user edits, reject stale token results, and fall back
independently for each name (a custom label must never supply the skill name).

## API and ownership

Contracts were checked against `origin/feature/integrate` and the backend
API comment in [issue #3617](https://github.com/aevatarAI/aevatar/issues/3617).

- `GET /api/channels/registrations` supplies the current owner's summaries.
  The frontend never requests the administrative `scope=all` view and excludes
  records explicitly marked as foreign. The route scope partitions query
  state and navigation; authentication remains the server's owner authority.
- Each visible connection reads
  `GET /api/channels/registrations/{registrationId}/status` independently.
  Pending, active, error, and unknown states remain distinct. A status failure
  does not hide the connection or claim it is active. Visible status queries
  refresh every 30 seconds and on window focus.
- Skill uses `default_skill.name` and optional `version` from PR #3620.
  The deployed name-only `default_skill_name` contract is also supported.
  An absent name displays Not set; an absent version is omitted. No skill
  examples are embedded in runtime code. Ornn links use its published
  `/skills/:idOrName` route and the existing `ORNN_BASE_URL` configuration.
- Detail uses the exact owned summary and its status. It does not request or
  cache the full runtime configuration because this design has no editor.
  Unknown or inaccessible registrations share one unavailable state.
- The API adapter selects safe fields before returning to the query cache.
  Webhook URLs, credentials, runtime instructions, arbitrary extra fields, and
  raw error/cleanup bodies are not retained. Agent key ID is an identifier,
  never the raw key.

## Removal and recovery

Remove first confirms the specific bot. DELETE uses the registration ID,
never the bot ID, scope ID, or key ID. A failed operation keeps the detail and
confirmation available for retry. No real connection is removed during QA.

A `status: deleted` response starts list reconciliation. The UI keeps Remove
disabled and waits for the registration to disappear from a fresh owner list
before reporting completion and returning to Channels. Automatic observation
is bounded to 20 seconds; Check again repeats only GET and never DELETE.
Cleanup warnings produce a safe NyxID follow-up message without displaying
raw diagnostic values.

## Design baseline and validation

The existing shell continues to follow:

- `docs/design-baselines/workflow-activity-vnext/aevatar-workflow-activity-vnext.excalidraw`
- SHA-256: `30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`
- `docs/superpowers/specs/2026-08-04-workflow-activity-vnext-design.md`
- `docs/superpowers/specs/2026-08-04-workflow-activity-vnext-user-paths.md`

The supplied simplified Figma frames extend that shell with Channels. Existing
login, callback, session, returnTo, and Umi localization remain shared. All
production records and statuses come from real APIs; no mock fallback exists.

Focused coverage exercises API field selection and encoding, deployed/new
skill contracts, unknown status, loading/error/empty recovery, query isolation,
first-level navigation, detail ownership, confirmation cancellation, delayed
removal, failure retry, and safe cleanup warnings. Mobile table rows retain
labels and Manage; long IDs wrap or shorten for display while full identifiers
remain in detail. Full frontend tests, typecheck, and production build belong
to GitHub CI under the personal incremental validation policy.
