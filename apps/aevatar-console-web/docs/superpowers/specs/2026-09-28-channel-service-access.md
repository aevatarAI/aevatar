# Channel service selection

Bind and Edit list active services the user's NyxID account can use, regardless
of which services the user selected at login. The Services header has no Manage
service access action, and the channel form does not redirect into OAuth consent.
This supersedes the former login-grant filtering and consent-return draft flow.

## Source and authorization boundary

The authenticated `GET /api/v1/user-services` inventory owns service identity,
activity and account access. Human-session inventory is independent of OAuth
service selections. The console does not decode `allowed_service_ids` or
`allow_all_services` to constrain channel selection. Personal services and
organization services allowed by membership are selectable when active; NyxID
organization viewer entries (`credential_source.allowed=false`) remain unavailable.

Both Bind and Edit submit exact UserService IDs with
`authorization_mode=explicit_service_allowlist`. Saving uses the existing backend
registration authorization planner: it verifies active instances and ownership,
asks NyxID for the Agent Key scope plan, and creates or updates that independent
channel credential. This behavior was checked against `feature/integrate`;
no backend contract change is needed. Account access does not prove credential
health, and the server revalidates each submitted selection.

## Link hints and user path

Repeated `requiredServiceId` parameters on the canonical Bind or Edit URL remain
initial selection hints for exact UserService IDs:

```text
/scopes/:scopeId/channels/:registrationId/edit?requiredServiceId=:userServiceId
/scopes/:scopeId/channels/bind/:botId?skillId=:optionalSkillId&requiredServiceId=:userServiceId
```

The editor trims and deduplicates hints, accepts at most 20 nonempty values of at
most 128 characters, and ignores invalid values. On first entry to Bind or Edit,
the first successfully refreshed account inventory preselects active, available
exact matches alongside saved selections and built-in required services. Matches
retain their Requested label. A same-slug instance never substitutes for the
requested ID; unavailable, missing and account-denied entries are not selected.
Their existing availability notice remains, with unresolved IDs behind details.

Initialization runs once per entered channel form. Failed initial inventory loads
can retry before initialization. Later inventory refreshes, newly available
services, query-parameter changes and rerenders do not apply defaults again.
Users can deselect optional Requested services; manual changes made while cached
inventory is refreshing also take precedence over initialization. Opening another
bot or registration starts a new form with its own initial URL hints. Reopening
or fully reloading the page starts a new entry and applies the URL defaults again.

URL hints do not add mandatory requirements, authorize credentials or save a
channel. Bind bot or Save changes is still required. There are no instructions
to customize login consent.

Users search and select services, then explicitly click Bind bot or Save changes.
Skill service suggestions are withdrawn; selecting a Skill does not trigger
service discovery or display a recommendation panel.
Required built-in services retain their existing selection and validation rules.
A successful inventory refresh removes missing, inactive or account-denied
selections from the draft, selected count and next submission. Active services
outside login consent remain selected. Failed or pending refreshes preserve edits
and block saving. Reactivation makes services selectable without reselecting them.

There is no channel consent redirect or temporary session-storage draft. Unsaved
navigation retains its ordinary discard protection. Saving still completes only
when the submitted configuration is observed, not when a command is accepted.

## Verification and visual direction

Keep the compact Channels form, search, selected count, typography and design
tokens. Route integration tests cover Bind and Edit with empty login grants,
initial exact-instance selection, one-time defaults, manual deselection, refresh
and route isolation, explicit submission, Bind observation, inactive/deleted
cleanup, failed-refresh preservation and reactivation. Adapter tests cover account
inventory, organization availability, authentication rejection and token refresh.
Full frontend typecheck, suite and production build are delegated to GitHub CI.

Design baseline: `../../design-baselines/workflow-activity-vnext/`, primary
`aevatar-workflow-activity-vnext.excalidraw`, SHA-256
`30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`.
Contract: `2026-08-04-workflow-activity-vnext-design.md`.
User paths: `2026-08-04-workflow-activity-vnext-user-paths.md`.
Production data comes from real APIs and acknowledged user actions only.
