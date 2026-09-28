# Channel edit service access recovery

Channel service selection and the user's NyxID authorization are separate
decisions. The editor shows the services the current session can select;
Manage service access opens the existing full NyxID consent flow. It never
promises a consent page limited to the services named in a link.

## Link contract

Use repeated `requiredServiceId` query parameters on the canonical edit URL:

```text
/scopes/:scopeId/channels/:registrationId/edit?requiredServiceId=:userServiceId&requiredServiceId=:anotherUserServiceId
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

1. The user opens the edit link. Services lists the missing requested access
   above the existing searchable channel selection.
2. Manage service access saves the non-secret, unsaved label, skill name and
   selected IDs in tab-scoped session storage before leaving. A storage or
   redirect failure keeps the editor open with a retryable error.
3. The existing `NyxIDAuthClient` starts `serviceAccessReview` with the complete
   edit path, query and fragment as `returnTo`. It uses the existing PKCE,
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
6. The user selects services and saves explicitly. Save still follows the
   channel's existing accepted-to-observed confirmation. Access review itself
   never saves the channel. Selected services that are no longer available
   must be reauthorized or deselected before saving.

The temporary draft is keyed by account subject, scope and registration, has
a one-hour expiry, and is cleared after restoration, completed save or explicit
discard. It contains no credentials and cannot establish authorization or a
saved channel fact. Browser history restoration resets pending review state
and refreshes service access.

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
requirement to resolve revoked selections before saving. Full frontend
typecheck, suite and production build are delegated to GitHub CI.

Design baseline:
`../../design-baselines/workflow-activity-vnext/`, primary
`aevatar-workflow-activity-vnext.excalidraw`, SHA-256
`30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`.
Contract: `2026-08-04-workflow-activity-vnext-design.md`.
User paths: `2026-08-04-workflow-activity-vnext-user-paths.md`.
Existing auth/session/returnTo and Umi localization remain authoritative.
Production data comes from real APIs and acknowledged user actions only.
