# Task 3B Report: Explicit Request Preview and Confirmation

## Scope and Implementation

Implemented the Task 3B frontend flow only under
`apps/aevatar-console-web/`.

- Added typed preview and confirmation models, plus the Studio adapter request
  for `POST /api/scopes/{scopeId}/workflows:explicit-request-preview`.
- The decoder accepts only the sanitized preview contract and rejects missing,
  unknown, or malformed enum values and empty allowed execution modes.
- Both `bindMemberWorkflow` and `saveAndBindWorkflow` send only
  `callSiteId`, `requestContractDigest`, and `attestedRisk` confirmation
  fields, and omit the field entirely for an empty confirmation list.
- Existing-member publish and changed-published-member save-and-bind both take
  one captured serialized YAML value, preview that exact value, require a
  fresh confirmation, then publish the same value.
- The confirmation dialog renders the service ID, method/path, risk, approval,
  request-body policy, response policy, and allowed execution modes. A preview
  not available for interactive execution fails closed.
- Canceling clears the local publish error state and makes no publication call;
  preview errors remain on the existing error path and make no publication call.
- Added synchronized English and Chinese catalog entries.

## TDD Evidence

The implementation was developed with the following RED/GREEN evidence before
this finalization pass:

- Adapter RED: the focused adapter test failed because
  `studioApi.previewExplicitRequests` did not exist; after the typed adapter was
  added, it passed.
- Route RED: the fresh-confirmation test expected a preview request but saw
  zero calls; after the publish hook was wired, it passed.
- Cancel/reconfirm: cancel made no bind/save-and-bind call, left Publish usable,
  and a second publish fetched a fresh preview before confirmation. This also
  exposed and fixed stale `publishError` visibility after cancel.
- Preview failure: the existing error surface is shown and neither publication
  transport is called.
- Zero-item preview: RED exposed an empty `explicitRequestConfirmations` array
  being sent; the implementation now omits the field. This finalization added
  the observable assertion that `Modal.confirm` is not called for that path.

Finalization found one TypeScript test-fixture error: the confirmation array in
`api.test.ts` inferred `attestedRisk` as `string`. `pnpm ... tsc` reproduced
the error, then the fixture was annotated as
`StudioExplicitRequestConfirmation[]`; the focused adapter test and `tsc`
were rerun successfully.

## Test Selection

The adapter integration test protects request/response decoding and the two
transport payload boundaries. The route integration cases protect fresh
preview-before-publish, cancellation/reconfirmation, preview failure, and the
empty-preview publication path. The locale catalog test protects synchronized
catalog keys. These are the smallest affected tests under the frontend testing
policy; the complete frontend Jest suite was not run.

## Final Verification

| Command | Result |
| --- | --- |
| `pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/shared/studio/api.test.ts --testNamePattern 'previews sanitized explicit requests and forwards only their confirmations to workflow publication transports'` | PASS: 1 passed, 50 skipped. Rerun after the fixture type annotation also PASS: 1 passed, 50 skipped. |
| `pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/team-member-workflow-studio/index.test.tsx --testNamePattern 'requires a fresh explicit-request confirmation before publishing an existing workflow member|surfaces an explicit-request preview failure without publishing|saves and rebinds a changed published workflow member through save-and-bind'` | PASS: 3 passed, 81 skipped. The final run included the zero-item `Modal.confirm` assertion. |
| `pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/locales/catalog.test.ts --testNamePattern 'keeps the English and Chinese message catalogs structurally aligned'` | PASS: 1 passed, 6 skipped. |
| `pnpm --dir apps/aevatar-console-web tsc` | PASS: `tsc --noEmit` completed with no diagnostics after the fixture annotation. |
| `pnpm --dir apps/aevatar-console-web exec biome lint src/locales/en-US.ts src/locales/zh-CN.ts src/pages/team-member-workflow-studio/hooks/useTeamMemberWorkflowStudio.ts src/pages/team-member-workflow-studio/index.test.tsx src/shared/studio/api.test.ts src/shared/studio/api.ts src/shared/studio/models.ts` | Completed: checked 7 files, no fixes applied; reported one pre-existing warning for unused `requestAccepted` in `src/shared/studio/api.ts:726`. |
| `pnpm --dir apps/aevatar-console-web build` | UNKNOWN: both attempts reached `max build` -> `Compiling Webpack`, then the tool returned before a final exit code or success/failure line. OS process checks confirmed the build processes later exited, but no reliable exit status was available. Do not treat as PASS. Warnings shown were an invalid Node localstorage-file path and outdated Browserslist data. |
| `bash tools/ci/test_stability_guards.sh` | PASS: `Test stability guard passed (polling waits constrained by allowlist).` All subsequent guard messages passed. |
| `git diff --check` | PASS: `TASK3B_DIFF_CHECK_EXIT=0`. |

No complete frontend Jest suite, browser suite, or unrelated test group was run.

## Files Changed

- `apps/aevatar-console-web/src/shared/studio/models.ts`
- `apps/aevatar-console-web/src/shared/studio/api.ts`
- `apps/aevatar-console-web/src/shared/studio/api.test.ts`
- `apps/aevatar-console-web/src/pages/team-member-workflow-studio/hooks/useTeamMemberWorkflowStudio.ts`
- `apps/aevatar-console-web/src/pages/team-member-workflow-studio/index.test.tsx`
- `apps/aevatar-console-web/src/locales/en-US.ts`
- `apps/aevatar-console-web/src/locales/zh-CN.ts`

## Self-Review and Concerns

Static review found no identity mixing: the route member ID remains the member
API identity, workflow IDs remain draft identities, and published service IDs
are only passed through their explicit save-and-bind contract field. The
confirmation payload is derived only from the just-returned sanitized preview,
and the captured YAML is reused for the corresponding publication request.

No lockfile, package metadata, `dist`, `coverage`, or generated-file changes
are present. The remaining concern is build verification: it is explicitly
unknown because the execution environment did not return a usable final build
status. Biome's single warning is pre-existing and unrelated to this change.
