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
- Skill creation opens https://ornn.chrono-ai.fun/skills/new/generate in a
  new tab. Its tooltip explains the return-and-refresh action. Refresh is
  always visible beside the selector and preserves the current selection.
- Match Figma's DM Sans typography, continuous white surface, restrained blue
  actions, 60px guide icons and neutral connecting arrows. At a 1440px viewport,
  the inventory content is 1080px wide; the table has a 46px header and 88px
  rows, horizontal separators and no enclosing card border.
- The binding form identifies the bot by label and platform and shows Not
  bound. The Skill popup contains a search input, skill names and descriptions,
  and the external creation footer. Keyboard users can move from search into
  the options with Arrow Down and dismiss the popup with Escape.

## API Contract

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
bound state and matching configuration. Edit observes the exact detail until a
newer authoritative state version contains the requested configuration.
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

Focused tests cover inventory/null identities, request shapes and safe
decoding, private Ornn proxy authorization/pagination, static guidance,
binding/availability actions, refresh retention, exact detail/removal,
admission observation, newer edit state and duplicate-write prevention.
The removed token and runtime-config tests describe retired contracts.
Full frontend tests, typecheck and production build belong to GitHub CI.
