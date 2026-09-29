# Channel Skill service suggestions — withdrawn

Issue: https://github.com/aevatarAI/aevatar/issues/3678

The service recommendation feature introduced by [PR #3679](https://github.com/aevatarAI/aevatar/pull/3679)
is withdrawn from Bind and Edit at the user’s request on 2026-09-29.
The backend [PR #3683](https://github.com/aevatarAI/aevatar/pull/3683), targeting
`feature/integrate`, has not been merged or deployed with the frontend.

The console does not request `/api/skills/service-recommendations` or show the
Suggested for panel, Refresh suggestions action, discovery failure message,
or Manage connections in NyxID link. The recommendation component, adapter,
query key, styles and locale messages are removed.

Manual service selection remains governed by
[Channel service selection](2026-09-28-channel-service-access.md): active,
account-available NyxID services can be selected even if omitted at login.
Skill selection, required built-in services, explicit Bind/Save and observed
completion retain their existing behavior.

Any future reintroduction requires explicit approval and verification that the
backend capability is deployed. Backend PR approval or deployment alone must
not silently re-enable this frontend feature.

Existing Bind and Edit route tests verify that a selected Skill produces no
recommendation panel or request. Related channel and inventory tests protect
manual selection and save behavior. Full frontend suite, typecheck and build
remain delegated to GitHub CI.
