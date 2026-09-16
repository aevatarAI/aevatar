# Channels in the Console

Channels is a first-level destination in the Workflow Activity vNext sidebar.
It lets the signed-in owner view their connected bots and inspect a connection.

The three pages follow the latest simplified [Figma design](https://www.figma.com/design/FaVJx5IZeQX9jHcUi55ndB?node-id=7-226)
(list frame `7:2`, detail frame `6:131`, Telegram form `7:226`). The current design intentionally keeps
channel details read-only with Remove as the sole resource action. The earlier
runtime-configuration editor is outside this iteration of issue #3617.

Channel refresh is explicitly user driven. Do not add background polling or
refresh on focus/reconnection to these pages.

Manual Refresh keeps the connected table mounted and displays the shared
loading overlay over that table while the registration list, bot names, and
active status refreshes settle. Table actions are inert during the refresh;
the button blocks duplicate submissions. Completion or failure removes the
overlay and restores interaction, with the existing error toast and manual
retry behavior retained.

The page header and content are horizontally centered within the area beside
the navigation rail, using the same maximum width and responsive side padding.
The connection form and details share this alignment; the form keeps its
narrower maximum width. On small screens the containers fill the available
width while retaining the shell's side padding.

Refresh and Manage use outlined button styling with visible hover/focus states
and larger touch targets on mobile. Manage retains navigation-link semantics.
Details omit Scope. Bot ID and Agent key ID are blue underlined external links
with an external-link icon, opening the corresponding NyxID record in a new tab.
The website origin is the design's `https://nyx.chrono-ai.fun`, separate from
the configured NyxID API/OIDC authority. Bot links use `/channel-bots/{botId}`;
Agent key links use the verified NyxID `/keys/api-key/{agentKeyId}` route.
The Figma `/keys/xxx` placeholder refers to NyxID service details, so it is not
used for Agent keys. Each complete source ID is encoded as one URL segment;
missing IDs remain unlinked placeholders.

The compact identifier in each connected-channel row opens the shared Tooltip
with the exact full bot ID (or registration ID when no bot ID exists). Mouse
hover, keyboard focus and click/tap reveal the full value, which wraps within
the Tooltip instead of being shortened again. The control has a visible focus
ring.

## Routes and existing setup

- `/scopes/:scopeId/workflow-activity-vnext/channels`: platform directory and
  owner connection list.
- `/scopes/:scopeId/workflow-activity-vnext/channels/:registrationId`: exact
  connection details. Registration IDs are opaque and encoded as one segment.
- `/scopes/:scopeId/workflow-activity-vnext/channels/connect/telegram`: the
  single-page Telegram connection form. Connect Telegram navigates here.
  The channel directory shows Telegram and WhatsApp only. WhatsApp replaces
  the Lark/Feishu placeholders and is marked Soon without a connection action;
  Discord and Slack placeholders are omitted. Existing connected records still
  render the platform returned by the API.
  The older backend `/channels` onboarding page remains independently available.

`AEVATAR_CHANNEL_WEBHOOK_BASE_URL` optionally sets the public backend callback
base URL at build time. The default is
`https://aevatar-console-backend-api.aevatar.ai`. It must be HTTPS without
credentials, query, or fragment. An invalid value disables registration with
an inline deployment-configuration message. It is never inferred from the
separate Console frontend origin or shown as an editable form field.

## Telegram connection form

The form requires a masked bot token and shows independent optional Channel name
(「Channel 名字」 in Chinese) and Skill name inputs. They share a row on desktop
and stack on narrow screens. The service selection may be empty. The token
reveal control is keyboard accessible. Unsaved form navigation asks whether to discard; form data
is never persisted. The token is used only for Telegram's official name lookup
and the authenticated registration POST. It stays in component memory for a
manual retry after failure and clears after successful admission. No registration
mutation cache stores it.

Services use the current NyxID session's complete `GET /api/v1/user-services`
inventory and its exact bearer service grants. The account inventory includes
HTTP tools and LLM services; personal ownership alone does not establish that
the current Aevatar session can grant a service to a bot.

The existing session helper refreshes an expired session before selection.
The auth adapter reads `allow_all_services` and `allowed_service_ids` from that
access token. Explicit `true` permits all active, account-accessible UserServices;
explicit `false` permits only exact IDs in the list, including an empty grant.
Inactive services, disallowed organization memberships and unknown credential
sources remain excluded. Names, slugs, catalog IDs and credential IDs never
substitute for `UserService.id`. No service type is excluded: an authorized LLM
service such as Chrono Public remains selectable even without MCP operations.

These claims are used only for display filtering, not as client-side signature
verification or proof that registration will succeed. The inventory request is
pinned to the exact bearer used for filtering; NyxID authenticates it and the
registration backend enforces delegation limits. Unsupported/malformed claims,
a mismatched account subject, an expired JWT or a failed inventory read leave
the picker in a retryable error state. There is no allow-all fallback when
claims are absent. Token/parser errors are sanitized, and token/claim payloads
never enter the query cache. Only the filtered safe display fields are retained.

Do not substitute `/api/v1/mcp/config` for this authorization list: its documented
contract describes executable tool operations and omits LLM services and some
services with unavailable runtime routes. `/api/v1/llm/status` is also not an
exact current-bearer grant inventory, and account consent records may differ
from the current token's resource restrictions. The NyxID access-token producer
in `backend/src/crypto/jwt.rs` emits explicit service grants for OAuth clients;
`backend/src/mw/auth.rs` consumes the same fields for service authorization.

The inventory read omits browser cookies and bypasses HTTP caching. It runs on
entry and explicit recovery only; no polling, window-focus or reconnect refresh
is added. A bearer with only some account services authorized sees only that
subset, across HTTP and LLM service types.

Options use label, then catalog service name, then slug. Search matches label
or slug and preserves selections outside the current search. A Select all
checkbox selects or clears every available service in the current results,
with a partial-selection state. While searching, it is labeled Select all
results and preserves selections outside the search. Inactive or disallowed
services are excluded; no selectable results or a locked form disables it.
Each selection uses the exact `services[].id`; no default selection is made.
The registration POST always supplies `authorization_mode: explicit_service_allowlist` and
`service_ids`, including `[]` for no service access. It never substitutes a
slug, endpoint ID, API key ID, or omitted field. If a selected service becomes
unavailable after an explicit recovery read, it is removed from the selection.
Submission waits while access is being rechecked, preventing stale hidden IDs
from entering a retry. Load failure has inline retry; an empty authorized list
can connect with an explicit empty service allowlist even if the account owns
other services.

Registration uses existing `POST /api/channels/registrations` with
`platform`, `bot_token`, `label`, `default_skill_name`, `webhook_base_url`, and
the explicit selection above. A `202`/`status: accepted` response must contain
the registration ID. After admission, the form clears its token, retains only
the safe returned ID, and explicitly refreshes registrations once. A successful
fresh read must contain that exact ID, route scope and Telegram platform before
the success toast and navigation to the newly created channel's existing detail
page. The form does not return to the collection automatically or infer success
from cached rows after a failed read. Until confirmation, it preserves the
submitted choices and shows a pending state with Check again. This action only
repeats the GET, never the create POST; failed confirmation reads show a safe
error toast and remain retryable. No polling, window-focus refresh, or reconnect
refresh runs. Inbound activity continues to use its real status, independently
of registration creation.
Registration failures, including
504 and network errors, show a shared error toast and restore editable fields and Connect
Telegram. Inputs and still-authorized selections are preserved for an explicit manual
retry; no automatic registration retry runs. An in-flight request still blocks
duplicate clicks. An accepted submission cannot be sent again while navigation
is completing; late responses after unmount cannot show a toast or navigate.

### Channel name, Skill name and Telegram bot-name defaults

The registration adapter trims the two inputs independently: Channel name maps
to `label`, and Skill name maps to `default_skill_name`. If both are nonblank,
Telegram name lookup is skipped. Otherwise, the adapter calls Telegram's
official `getMe` endpoint once and uses the bot's trimmed `first_name`, never
its `username`, to resolve missing names.

A blank Channel name defaults to the bot name plus a hyphen and six random
digits (for example, `My Bot-482731`). The suffix is generated in the frontend
once per submission, in the range 100000–999999. A blank Skill name defaults
to the bot name without the suffix, restoring the original independent field
behavior. Custom names receive no suffix and never overwrite the other input.
The form locks during lookup/submission and preserves its input after failure;
a manual retry resolves the current token/name again.

This uses the same official API flow as NyxID's existing Telegram onboarding,
because the reviewed Aevatar/NyxID backend has no separate pre-registration
name endpoint. The plain fetch targets only `https://api.telegram.org`, with
credentials omitted, no cache, no referrer, redirects rejected and a 15-second
timeout. Telegram's protocol requires the token in the API request path; it
never enters browser navigation, logs, toast text, persistent state, or the
authenticated fetch helper. Raw fetch errors and response bodies are discarded.
A rejected token, unavailable lookup or absent bot name prevents registration,
shows a safe toast and leaves the form available for retry.

## Connected names, channels and skills

The connected table separates Channel name from Channel (platform). The current
Aevatar registration summary does not return the submitted label: provisioning
stores it on the NyxID bot. For a nonempty list with bot references, the page
therefore reads `GET /api/v1/channel-bots` once through existing NyxID auth/config,
without an organization selector. This endpoint returns the caller's active
personal bots as `{ bots, total }`. Only `id`, `platform` and `label` enter the
query cache. Registration `nyx_channel_bot_id` must exactly match `bots[].id`,
and platforms must agree; registration IDs, platform bot IDs, usernames and
skill names never substitute for that join or for the Channel name.

Names load independently of registrations and statuses. Missing/deactivated
bots, empty labels and failed reads show Name unavailable beside the existing
identifier; loading is shown explicitly. A name-read failure preserves the
connected rows and actions, shows a safe error toast and can be retried with
Refresh. Refresh reads the names once with the list/status reads. No per-row
name request, automatic retry, polling, focus or reconnect refresh is added.

Channel details use the same bot-identity query and exact join for their Channel
name heading. Missing or failed name reads keep the detail facts and actions
available with a local Reload channel name action. The title never substitutes
a compact bot ID, registration ID or Skill name for the label.

Channel details include Skill using the same name, optional version and Ornn
link as the connected list. Missing skills show Not set. Reading names/skills
does not request full runtime configuration or alter bot resources.

### Authorized services in channel details

The detail page displays the registration's saved `authorization_mode` and
`service_ids` from `GET /api/channels/registrations`. For an explicit allowlist,
only those exact UserService IDs are shown. One account inventory read resolves
their labels and slugs; the current session's selectable grants and service
activity do not hide saved channel authorizations or add other services.
Only safe ID, label and slug fields enter the service-name query cache.

Services appear as compact labels arranged horizontally, wrapping when the
available width is filled. Each service shows its resolved display name once;
the slug is not repeated on a second line. Long names wrap within their label.

Missing service names retain their saved IDs. A failed name lookup preserves
other details and the saved authorization list, shows a safe toast, and offers
a manual retry of the names alone. Explicit empty authorization shows no
services authorized; NyxID default authorization and unavailable or legacy
authorization details have distinct messages and do not query the inventory.
No automatic refresh or registration change is introduced by this display.

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
  load on entry and refresh only through the explicit Refresh action. Neither
  the list nor status queries poll, refresh on window focus, or refresh on
  network reconnection.
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
before reporting completion and returning to Channels. Removal triggers one
list read; if the row is still present, Check again performs one GET and never
repeats DELETE. No background polling runs while waiting.
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
