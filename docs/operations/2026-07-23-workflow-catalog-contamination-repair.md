# Workflow Catalog Contamination Repair Runbook

## Scope

This runbook defines the operator boundary for removing legacy scope-owned workflow documents that were materialized into the global `workflow-catalog-current-states` read model before the projection ownership fix.

The current projector rejects committed workflow state with a non-empty `ScopeId`. That prevents new contamination, but it does not delete documents already present in a document provider. This repository change does not add or run a production repair command.

## Non-Negotiable Boundaries

- Do not add replay, projection priming, mutation, deletion, or cleanup to `IWorkflowCatalogPort`, `WorkflowCatalogReadModelQueryPort`, Chat tools, or any other query/request path.
- Do not identify private documents from workflow name, document ID, `source`, `showInLibrary`, YAML contents, actor-ID text patterns, or route position. Those values are not ownership authority.
- Do not assume a catalog document with no `scope_id` is public. `WorkflowCatalogCurrentStateDocument` intentionally has no scope field.
- Derive every deletion candidate from the authoritative Definition actor's committed state or equivalent durable committed fact. A candidate is eligible only when that authority proves a non-empty owner `ScopeId` for the exact actor/document relationship.
- Keep `memberId`, `workflowId`, and `publishedServiceId` separate. None of them is a substitute for the catalog document ID or Definition actor ID.
- Run the migration as an explicitly approved background or operator-owned materialization job. Online traffic must remain read-only throughout the repair.

## Preconditions

Record all of the following before any mutation:

1. The deployed commit containing the `WorkflowCatalogCurrentStateProjector` non-empty-`ScopeId` rejection.
2. The document provider, environment, concrete index or collection, alias, and schema fingerprint.
3. A point-in-time backup or provider snapshot with a tested restore procedure.
4. The authoritative public template inventory produced by the reviewed startup/file import configuration.
5. A dry-run candidate manifest containing catalog document ID, Definition actor ID, committed state version, committed event ID, authoritative owner scope, source kind, and the evidence reference used to prove ownership.

The dry run must not write to either actor state or the catalog read model. A second reviewer must approve the candidate manifest and confirm that no authoritative scope-less public template is present.

## Dry-Run Inventory

Build the candidate manifest by joining provider documents to authoritative committed Definition facts using the exact Definition actor identity carried by the catalog document. For each row:

1. Read the catalog document without changing it.
2. Resolve the matching Definition actor's committed state through an approved repair/materialization source, outside the query call stack.
3. Record its committed `ScopeId`, `SourceKind`, state version, and supporting event ID.
4. Mark the row as a deletion candidate only when the committed owner `ScopeId` is non-empty.
5. Quarantine unresolved, missing, version-conflicting, or identity-ambiguous rows for manual review. Do not delete them.

Compare the manifest with the expected public template inventory and record counts grouped by owner scope and source kind. Store the manifest and checksums with the change record; do not include workflow YAML, role prompts, tokens, or credentials.

## Approved Mutation

Execute deletion only after backup and manifest approval:

- Delete by the exact catalog document IDs in the approved manifest.
- Use provider-native optimistic concurrency or equivalent version checks so a document changed after the dry run is rejected rather than deleted.
- Make the job idempotent: an already absent approved candidate is a successful no-op; a changed or unapproved document is a failure requiring review.
- Keep a per-document audit record with the manifest checksum, previous provider version, result, timestamp, and operator/change identifier.
- Stop immediately on version conflicts, unexpected document counts, backup failure, provider timeout, or any candidate that resolves to empty `ScopeId`.

Do not replay all Definition events into the same contaminated index as part of deletion. If a rebuild is required, use the governed current-state disaster-recovery materialization path into a clean target, validate it, then cut over according to the document-provider lifecycle runbook.

## Post-Migration Verification

After mutation, verify all of the following with read-only checks:

1. Every approved candidate is absent and every non-candidate document remains present at its expected version.
2. The public template tool returns only the reviewed public inventory and preserves `showInLibrary` behavior.
3. Console Chat's unqualified workflow inventory still reads the Studio member current-state model and returns only the caller's current scope Team-owned workflow members.
4. No query, tool, endpoint, or startup request performed replay, priming, or cleanup.
5. Application logs and projection health show no version conflicts or unexpected writes after the repair.

Retain the backup and audit manifest until the change owner signs off. Roll back by restoring the provider snapshot or reverting the clean-target cutover; do not compensate by writing actor state from read-model contents.

## Completion Evidence

The operator record must include the deployed commit, approved manifest checksum, backup reference, before/after counts, conflict count, deletion audit location, read-only verification results, and rollback decision. Until this evidence exists, catalog contamination repair is not complete.
