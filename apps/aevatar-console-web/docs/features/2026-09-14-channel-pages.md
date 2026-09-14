# Channels in the Console

Channels is a first-level destination in the Workflow Activity vNext sidebar.
It lets the signed-in owner view their connected bots and inspect a connection.

The two pages follow the latest simplified [Figma design](https://www.figma.com/design/FaVJx5IZeQX9jHcUi55ndB?node-id=6-2)
(list frame `6:2`, detail frame `6:131`). The current design intentionally keeps
channel details read-only with Remove as the sole resource action. The earlier
runtime-configuration editor is outside this iteration of issue #3617.

## Routes and existing setup

- `/scopes/:scopeId/workflow-activity-vnext/channels`: platform directory and
  owner connection list.
- `/scopes/:scopeId/workflow-activity-vnext/channels/:registrationId`: exact
  connection details. Registration IDs are opaque and encoded as one segment.
- Connect Lark and Connect Telegram open the existing backend `/channels`
  onboarding page. That page owns credentials, registration, platform setup,
  reply model, and verification; it currently has no platform-specific deep
  link. Feishu, Discord, and Slack remain visibly unavailable.

`AEVATAR_CHANNELS_ONBOARDING_URL` optionally sets the existing setup page URL
at build time. The default is the deployed
`https://aevatar-console-backend-api.aevatar.ai/channels`. Configure this value
for other deployments; an invalid value disables setup links. No credential is
added to the URL and the existing onboarding authentication flow is preserved.

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
