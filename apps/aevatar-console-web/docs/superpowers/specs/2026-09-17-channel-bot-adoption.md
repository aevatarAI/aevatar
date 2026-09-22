# Channel Bot Adoption

## Approved Design

The approved Channels design is the
[Channels - Add and bind Figma page](https://www.figma.com/design/FaVJx5IZeQX9jHcUi55ndB/?node-id=124-642).
The existing Workflow Activity vNext shell, authentication, localization and
real-API-only rules still apply. This supplement supersedes the previous
Telegram token creation flow.

- The top section is a static illustrated guide: add a bot in NyxID, return
  and refresh/bind, then view the bound bot in the table. All three steps have
  equal visual emphasis in every state. It is never a progress indicator.
- The primary external action opens https://nyx.chrono-ai.fun/channel-bots.
- Inventory contains both bound and unbound bots. Only an available unbound
  bot exposes Bind; only a bound registration exposes Manage.
- Bind uses the existing Skill and Services form controls. It contains no
  platform credentials or editable channel name. Edit retains its existing
  NyxID Label action and shares the Skill and Services controls.
- Bind and Edit preselect and lock services whose exact slugs are `ornn-api`
  and `chrono-llm-public`. Individual and bulk deselection retain both; other
  services remain optional. Resolve their real UserService IDs from the current
  authorized, active NyxID inventory, never from labels or slug-as-ID fallbacks.
  Missing required access blocks submission and offers retry after correcting
  NyxID access. Save uses an explicit allowlist containing both required IDs and
  the optional selections. Editing an older NyxID-default configuration explains
  that saving replaces defaults with these selected services; the UI does not
  infer which services were included in those defaults.
- Skill creation opens https://ornn.chrono-ai.fun/skills/new/generate in a
  new tab. Its tooltip explains the return-and-refresh action. The refresh icon
  sits at the right of the popup's Available in Ornn heading. Refresh preserves
  the selected skill and keeps the dropdown open.
- Match Figma's DM Sans typography, continuous white surface, restrained blue
  actions, 60px guide icons and neutral connecting arrows. At a 1440px viewport,
  the inventory content is 1080px wide; the table has a 46px header and 88px
  rows, horizontal separators and no enclosing card border.
- The binding form identifies the bot by label and platform and shows Not
  bound. The Skill popup contains a search input, skill names and descriptions,
  and the external creation footer. Keyboard users can move from search into
  the options with Arrow Down and dismiss the popup with Escape.

## API Contract

### Default Skill In Bind Links

`/scopes/:scopeId/channels/bind/:botId?skill=booking-capacity` opens the bind
form with that Skill name selected. `skill` is the exact name accepted by
`skill_name`, not an Ornn GUID. Link producers should encode the name with
`URLSearchParams` so spaces, `+`, `&` and non-ASCII names survive the URL.
The first `skill` parameter is URL-decoded once and trimmed; missing, blank
or over-128-character values leave the optional selector empty.

The URL supplies initial user input only. It does not assert that a skill is
available or authorized, add a catalogue record, grant services or submit a
binding. A name can be prefilled before the paginated skill catalogue loads.
Users can replace or clear it; query refreshes and same-bot query changes do
not overwrite their edits. Entering another bot's bind form starts fresh.
Existing registration edit pages ignore this parameter and keep their saved
skill. Login continues preserving the safe return URL and its query.

### Endpoints

[Backend issue #3652](https://github.com/aevatarAI/aevatar/issues/3652)
defines the required deployment contract:

| Action | Endpoint | Public inputs |
| --- | --- | --- |
| Inventory | GET /api/channels/registrations | None; caller-owned inventory |
| Bind | POST /api/channels/registrations | nyx_channel_bot_id, skill_name, authorization_mode, service_ids |
| Detail | GET /api/channels/registrations/{registrationId} | Opaque registration identity |
| Edit | POST /api/channels/registrations/{registrationId} | skill_name, authorization_mode, service_ids |

The inventory key is the NyxID bot ID. A local registration ID may be null for
unbound bots. Neither API input nor response decoding requires a scope key;
ownership is server resolved. Query caches remain isolated by the current
route scope. A missing or malformed inventory is an error, never an empty list.
Detail and edit take configuration and version evidence from the exact detail,
then match its registration ID, bot ID and platform to inventory for the current
NyxID Label and inbound status. The detail's placeholder label is never used as
the editable bot name.

A successful write receipt is admission only. Bind automatically observes the
inventory for the receipt's registration ID and the original bot ID with a
bound state and matching configuration. Once confirmed, it opens the existing
Manage/detail page for that observed registration ID, showing the bound bot
and its configuration. Acceptance alone does not navigate, and the NyxID bot
ID is never substituted for the registration ID. Edit observes the exact detail
until a newer authoritative state version contains the requested configuration.
Observation is bounded to 30 seconds. Delay or read failure preserves the
receipt and offers a return to Channels without repeating the write. An
uncertain transport result also prevents a duplicate write from this form.

Skill search uses the current NyxID session through
`GET /api/v1/proxy/s/ornn-api/api/v1/skill-search` on the configured NyxID
authority, with `scope=private&mode=keyword&q=...&limit=50`.
Private scope includes owned and shared skills. The selector supports server
search and opaque cursor pagination. No frontend API key or separate token
store is introduced. Ornn's route schema confirms that `cursor` and `limit`
are supported.

## Deployment And Verification

The #3652 contract was merged into `feature/integrate` by
[backend PR #3653](https://github.com/aevatarAI/aevatar/pull/3653).
During live verification on 2026-09-17, the remote deployment initially returned
HTTP 200 with the old inventory shape, which the new decoder rejected. The UI
now distinguishes an unavailable binding contract from a generic load failure.
It never infers a binding state from legacy fields or treats failed decoding as
an empty inventory.

The remote deployment subsequently exposed the new exact-detail GET/POST
routes and the new inventory shape. Verification against that real deployment
displayed nine bots (eight bound and one unbound), loaded NyxID services and
Ornn skills, and confirmed server search, keyboard selection and retention
after refreshing skills. Desktop (1440px) and mobile (375px) screenshots were
inspected in the existing Chrome session. No production bot or service grant
was mutated during this verification; write confirmation and uncertain-write
behavior are covered by focused tests.

A subsequent user-triggered Bind returned `400 insecure_webhook_base_url`.
The captured request contained exactly the documented bot ID, skill name,
authorization mode and service IDs. In the referenced backend, registration
checks the resolved callback address before calling the adoption facade.
The UI must attribute this rejection to server configuration and preserve the
user's choices instead of suggesting that the skill or grants are incorrect.

The deployed host must explicitly configure its public HTTPS callback origin
when TLS terminates before the application, for example:

```text
Aevatar__NyxIdRelay__WebhookBaseUrl=https://<public-channel-api-host>
```

`MainnetHostBuilderExtensions` reads `NyxIdRelay` first and overlays
`Aevatar:NyxIdRelay`; operators must check that effective configuration.
The host appends `/api/webhooks/nyxid-relay` to the base. Do not send this value
in adoption JSON or substitute the browser's localhost origin. Correcting the
UI error alone does not resolve the deployment configuration or establish a
successful live binding. The backend deployment blocker is tracked in
[issue #3656](https://github.com/aevatarAI/aevatar/issues/3656), assigned to
`louis4li`.

Focused tests cover inventory/null identities, request shapes and safe
decoding, private Ornn proxy authorization/pagination, static guidance,
binding/availability actions, refresh retention, exact detail/removal,
admission observation, newer edit state and duplicate-write prevention.
The removed token and runtime-config tests describe retired contracts.
Full frontend tests, typecheck and production build belong to GitHub CI.
