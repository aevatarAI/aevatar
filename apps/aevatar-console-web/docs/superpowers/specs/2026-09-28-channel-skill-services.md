# Channel Skill service suggestions

Issue: https://github.com/aevatarAI/aevatar/issues/3678

## User behavior

Channel binding and editing automatically show suggested Services when a Skill
is selected. Each suggestion names its evidence and shows the account's personal
or organization service instances. Selecting an instance adds its exact
UserService ID to the existing form; the normal channel save commits the choice.
Discovery never adds grants, selects services, or removes existing selections.
Changing or clearing the Skill preserves manually selected services.

The region distinguishes unselected, selected, no connection, inactive,
unavailable account access, and missing current-session authorization. It does
not claim credential validity, which the inventory endpoint does not expose.
Links open NyxID service management and the existing Account service-access
review in new tabs, preserving unsaved form choices. Refresh suggestions reloads
both discovery and selectable service access after connection or authorization
changes. Pending and failed discovery stays local to this region; manual service
selection remains usable. An empty result does not imply that no services are
needed. Current platform-required services retain their existing behavior.

## Discovery contracts and limits

- Resolve the registration's Skill name through authenticated Ornn
  `GET /api/v1/skills/:idOrName`, then read `GET /api/v1/skills/:guid/json`.
  Verify matching names and read only the description, root `SKILL.md`, and
  optional `nyxidServiceSlug` association.
- Read NyxID `GET /api/v1/catalog?include_all=true` for service names, slugs and
  exact `recommended_skills` names. Read `GET /api/v1/user-services` for account
  instances. These are existing external contracts, also used by the backend
  Ornn client on `feature/integrate`; no backend changes are required.
- Evidence priority is the Skill's explicit service association, then an exact
  catalog recommendation, then whole service-name/slug mentions in the Skill
  description or root instructions. Names shorter than three characters are
  excluded from text inference. Nested skill references are not traversed.
- None of these sources declares an exhaustive mandatory dependency list.
  Suggestions therefore remain advisory; text mentions explicitly say they may
  be needed for some tasks. There is no LLM inference or fabricated required-
  service schema. Literal matching can miss aliases or identify incidental
  mentions; users retain the complete manual picker.
- Skill content is input data, never executed or rendered as instructions.
  Reasons show the evidence category without copying private instruction text.
  Malformed or mismatched responses produce a retryable discovery failure.
- Slugs associate recommendations with instances; only the existing authorized
  service-choice adapter determines selectable UserService IDs. Catalog IDs and
  Ornn association IDs never enter the channel authorization payload.
- Queries are scoped by console scope and selected Skill name, consume abort
  signals, and do not show the previous Skill's results while a new query runs.

## Design and validation

Use the existing compact Channels form, typography and color tokens. The inline
suggestion list supports wrapped names, narrow widths, keyboard actions, loading,
empty, error and retry states. English and Chinese messages use existing locales.
The Excalidraw baseline remains unchanged:
`30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`.

Route integration coverage exercises evidence, distinct same-slug personal and
organization instances, exact submitted IDs, unavailable access, manual fallback,
late responses, clearing and retry after new authorization. Adapter coverage
protects authenticated package/catalog requests and mismatched Skill identity.
Full frontend typecheck, suite and production build belong to GitHub CI under
the personal incremental-validation policy.
