# Channel Skill service suggestions

Issue: https://github.com/aevatarAI/aevatar/issues/3678

## Backend-owned discovery

The console calls `GET /api/skills/service-recommendations?skillName=...` on
Mainnet. The backend owns Skill resolution, service discovery and evidence.
See the [backend contract](https://github.com/aevatarAI/aevatar/blob/3da66b8813c03a7b1d87616889dd3ff343dcde1a/docs/contracts/skill-service-recommendations.md)
for the response, source contracts, layering and limitations.

The backend is delivered in [PR #3683](https://github.com/aevatarAI/aevatar/pull/3683)
against `feature/integrate`. This console change is delivered separately in
[PR #3679](https://github.com/aevatarAI/aevatar/pull/3679) against
`feat/2026-08-04_workflow-activity-vnext`. The backend endpoint must be merged
and deployed for recommendations to become available.

The browser does not fetch `SKILL.md`, load the catalog for inference, or match
service names against Skill content. Its API adapter validates the selected
Skill identity, evidence enums and exact UserService instance IDs. A missing or
unsupported backend result produces a retryable discovery state, with the
manual picker still usable; there is no local inference fallback.

## User behavior

Binding and editing show suggestions after a Skill is selected. Reasons name the
backend's evidence category without exposing private instructions. Account
instances show personal/organization source, activity and availability.
Active services available to the NyxID account determine which exact IDs are
selectable, including services omitted at login. Account-level organization
restrictions still apply. Login service grants do not constrain a channel
Agent Key selection. Both Bind and Edit use this same inventory boundary; see
[Channel service selection](2026-09-28-channel-service-access.md).

Selecting an instance adds its exact UserService ID to the form; normal save
commits it. Discovery never selects, authorizes or removes services. Changing or
clearing the Skill preserves manual choices. Queries are keyed by scope and
Skill name and consume abort signals, so late results do not replace the current
Skill's recommendations.

Missing connections and inactive services offer NyxID connection management in a
new tab. Channel forms no longer show an OAuth service-access review action or
consent instructions. Refresh reloads backend discovery and account inventory.
Pending and failed discovery stay within the suggestion region.

The current backend sources do not declare mandatory dependencies. Suggestions
remain advisory; text mentions explicitly say they may be needed for some tasks.
An empty result does not prove no services are required. Existing platform-
required services keep their own behavior. Inventory is not credential-validity
evidence.

## Design and verification

Retain the compact Channels form, design tokens, wrapped names, keyboard actions
and English/Chinese locales. Excalidraw assets remain unchanged:
`30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`.
Use real authenticated backend responses; mocks exist only in tests.

Route integration tests cover rendering backend suggestions, exact instance
selection and save, access gaps, switching/clearing, stale responses and recovery.
API tests cover request encoding and malformed/mismatched results.
Full frontend typecheck, suite and production build are delegated to GitHub CI.
