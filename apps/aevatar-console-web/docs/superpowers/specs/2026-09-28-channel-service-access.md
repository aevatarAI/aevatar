# Channel configuration service access recovery

Channel service selection and the user's NyxID authorization are separate
decisions. The editor shows the services the current session can select;
Manage service access opens the existing full NyxID consent flow. It never
promises a consent page limited to the services named in a link.

## Link contract

Use repeated `requiredServiceId` query parameters on the canonical edit or Bind URL:

```text
/scopes/:scopeId/channels/:registrationId/edit?requiredServiceId=:userServiceId&requiredServiceId=:anotherUserServiceId
/scopes/:scopeId/channels/bind/:botId?skillId=:optionalSkillId&requiredServiceId=:userServiceId&requiredServiceId=:anotherUserServiceId
```

Each value is the exact NyxID **UserService ID**, obtained from an authoritative
service reference. A catalog ID, display name, slug, channel registration ID,
or Aevatar published-service ID is not interchangeable with this identity.
The editor trims and deduplicates hints, accepts at most 20 nonempty values of
at most 128 characters each, and ignores invalid values. Hints are not grants,
do not select a service automatically, and do not become channel requirements.

Names and slugs come from the authenticated NyxID user-service inventory.
Effective availability requires an active service, account access, and the
current bearer grant for that exact ID (or an explicit all-services grant).
Another service with the same slug cannot satisfy the hint. Missing inventory
entries are shown as unresolved services with the requested ID behind a
details disclosure; unavailable services are not presented as selectable.

## User path

1. The user opens the edit or Bind link. Services lists the missing requested access
   above the existing searchable channel selection.
2. Manage service access saves the non-secret, unsaved label, skill name and
   selected IDs in tab-scoped session storage before leaving. A storage or
   redirect failure keeps the editor open with a retryable error.
3. The existing `NyxIDAuthClient` starts `serviceAccessReview` with the complete
   configuration path, query and fragment as `returnTo`. It uses the existing PKCE,
   callback and backend finalization flow. It supplies no targeted `resource`
   or `preselect_service_ids` parameters.
4. NyxID currently displays the full consent page. The editor directs users to
   **Customize** under **Service access**, retain services they still need,
   select the listed services, and choose **Allow**. Viewing that page does not
   imply that any permission was granted.
5. Returning reloads the actual service inventory/grants and restores the
   editor draft. New available services are marked Requested but remain
   unselected until the user chooses them. Partial or cancelled authorization
   leaves the remaining access needs visible. The callback's return action is
   labeled Back to previous page because it may return to this editor.
   Restored edits receive one short inline reminder to review selections and
   save. Do not show a generic permission-check success heading or panel;
   only unresolved access needs warrant a separate notice and service list.
6. The user selects services and clicks **Save changes** or **Bind bot** explicitly.
   Both actions follow the channel's existing accepted-to-observed confirmation.
   Access review itself never saves or binds the channel. Selected services that are no longer available
   must be reauthorized or deselected before saving.

Both routes reuse `ChannelConfigurationPage`, `ChannelServicePicker` and
`ChannelServiceAccessNotice`, including loading, retry and revoked-selection
behavior. Bind needs no saved channel registration to review access.

The temporary draft is keyed by account subject, scope and a typed target:
`bind + botId` for an unbound bot, or `edit + registrationId` for a saved channel.
These identities never substitute for each other, even if raw ID strings coincide.
The draft has a one-hour expiry and is cleared after restoration, completed save
or binding, or explicit discard. It contains no credentials and cannot establish
authorization or a saved channel fact. Browser history restoration resets pending
review state and refreshes service access.

On Bind, a restored skill choice takes precedence over the link's `skillId`
default, including an explicitly cleared choice. Authorization return preserves
the complete link but does not reapply the default or automatically select newly
authorized services. Binding still targets the original bot ID and completes only
after the returned registration ID and bot ID are observed with the submitted
configuration.

## Current NyxID review limitation

The ordinary `prompt=consent` page can initialize from the app's default
services instead of the user's latest saved consent. On 2026-09-29, the live
review page showed the original six services even though the latest Authorized
Apps entry contained the two additionally granted UserService IDs. Both extra
services were available but unchecked under Customize. Merely opening the
review did not remove the saved grant.

The user must include every service they intend to retain before submitting
that ordinary review. Its consent decision replaces the selected service
boundary; the channel's draft selections do not initialize NyxID's picker.
Do not send the consent page's server-generated `preselect_service_ids` as
an invented `/oauth/authorize` contract, or substitute slug-based `resource`
parameters: resource requests can narrow issued authority and cannot reliably
represent distinct same-slug UserServices.

[NyxID PR #1683](https://github.com/ChronoAIProject/NyxID/pull/1683) introduces
explicit incremental consent with `service_access_mode=incremental` and exact
repeated `requested_service_ids`. It was open during this investigation.
After the backend and consent UI deploy, Aevatar must integrate that contract
and verify repeated consent preserves the accumulated grant. This full-review
fallback does not claim that capability.

## Verification and visual direction

Keep the existing compact white work surface, AlibabaSans typography, blue
actions and token-based amber access notice. Missing services are a short list
with readable names and slugs; the existing search and checkboxes remain the
channel-selection controls. Actions and rows wrap at mobile widths.

Route integration tests exercise exact-ID hints, duplicate slugs, partial
authorization, explicit selection/save, draft restoration, cancellation,
redirect/storage failure and account isolation. Existing adapter and callback
tests protect grant validation and the shared return flow. Browser history
restoration coverage verifies fresh grants, temporary draft cleanup and the
requirement to resolve revoked selections before saving. Bind route coverage
also verifies the complete return URL, explicit binding after refreshed grants,
accepted-versus-observed completion, restored skill overrides and clearing, and
isolation across bots and edit registrations. Full frontend
typecheck, suite and production build are delegated to GitHub CI.

Design baseline:
`../../design-baselines/workflow-activity-vnext/`, primary
`aevatar-workflow-activity-vnext.excalidraw`, SHA-256
`30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`.
Contract: `2026-08-04-workflow-activity-vnext-design.md`.
User paths: `2026-08-04-workflow-activity-vnext-user-paths.md`.
Existing auth/session/returnTo and Umi localization remain authoritative.
Production data comes from real APIs and acknowledged user actions only.
