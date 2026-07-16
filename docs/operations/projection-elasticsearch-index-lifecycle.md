# Elasticsearch Projection Index Lifecycle Runbook

This runbook covers the Elasticsearch document projection provider lifecycle for
stable read/write aliases and schema-fingerprint physical indexes.

## Contract

Each read model alias points at a physical index named:

```text
{alias}-v{fingerprint}
```

The fingerprint is computed from the provider-normalized, protobuf descriptor
augmented mapping contract. It is the provider lifecycle truth source for index
schema drift. Live Elasticsearch mappings are not a second truth source.

`GetAsync`, `QueryAsync`, and `CheckIndexConsistencyAsync` are read-side paths:
they may diagnose alias fingerprint drift, but they must not create indexes,
run `_reindex`, or swap aliases. `GetAsync` and `QueryAsync` fail closed on
fingerprint drift before reading stale documents; the consistency probe reports
drift without mutation. `UpsertAsync` calls `EnsureIndexAsync` before document
writes; on fingerprint drift it also fails closed and does not reindex or swap
aliases.

## Lifecycle Entry Points

The lifecycle has two mutation entry points:

- write-side first touch (`UpsertAsync -> EnsureIndexAsync`) before an upsert
  reaches Elasticsearch documents
- the provider-local hosted startup initializer, which invokes
  `IProjectionIndexReconcileTarget.ReconcileIndexAsync` for static read-model
  aliases

The write-side ensure path creates greenfield physical indexes and wraps legacy
bare indexes, but it fails closed on single-backing fingerprint drift and
multi-backing drift. Clean old-fingerprint migration is only the static startup
reconcile contract.

The startup initializer skips dynamic index-scope read models because their
concrete aliases are data-driven. Dynamic scopes remain write-side first-touch
only for greenfield or legacy bare lifecycle; they do not get startup clean
drift migration.

Hosts that compose startup readers must register the static index reconcile
initializer before those readers. Mainnet starts hosted services sequentially
in registration order, so this ordering is part of the lifecycle contract: a
startup migration or bootstrap query must not observe a drifted alias before
the provider-local reconcile has had a chance to migrate it.

## Write-Side Ensure

`EnsureIndexAsync` handles four states:

1. Alias points at the expected physical index: no-op.
2. Alias points at exactly one old fingerprint physical index: throw
   `ProjectionIndexSchemaDriftException`; do not create the expected physical,
   run `_reindex`, swap aliases, or write the document.
3. A legacy bare index exists with the alias name: create the expected physical
   index, copy bare to physical with `_reindex`, then atomically add the alias
   and remove the bare index in one `_aliases` request.
4. Nothing exists: create the expected physical index with the alias declared in
   the create-index payload.

After a failed ensure, read and write traffic must continue failing closed until
the underlying Elasticsearch state is repaired or static startup reconcile
succeeds.

## Static Startup Reconcile

`IProjectionIndexReconcileTarget.ReconcileIndexAsync` is the controlled
provider-local startup migration path for static aliases. It handles these
states:

1. Alias points at the expected physical index: no-op.
2. Alias points at exactly one old fingerprint physical index: create the
   expected physical index when missing, copy old to new with `_reindex`,
   require no per-document failures and no timeout, then atomically remove old /
   add new in one `_aliases` request. If the expected physical already exists,
   reconcile only repoints the alias and does not reindex again.
3. Alias has multiple backing physical indexes: throw
   `ProjectionIndexSchemaDriftException`; do not create the expected physical,
   run `_reindex`, swap aliases, or touch documents.
4. A legacy bare index exists with the alias name: wrap it into the expected
   physical with `_reindex` and an atomic `_aliases` request.
5. Nothing exists: create the expected physical index with the alias declared in
   the create-index payload.

## Fail-Closed Cases

Do not swap aliases or continue document writes when any of these are observed:

- alias has multiple backing physical indexes
- source index is missing
- Elasticsearch rejects the new mapping as incompatible
- `_reindex` returns per-document failures
- `_reindex` times out
- the alias swap request fails
- an operator observes partial copy or ambiguous source data

These cases need explicit operator repair, projection replay, or a later clean
lifecycle retry. Query/read paths must not perform the repair.

## Operational Checks

To inspect alias state:

```bash
curl -sS "$ELASTICSEARCH_ENDPOINT/_alias/<alias>"
```

Healthy static aliases have exactly one object key in that response, and the key
starts with `<alias>-v`.

To inspect a provider-side diagnosis without mutation, use the registered
`IProjectionIndexConsistencyProbe<TReadModel>` or the application diagnostics
surface that wraps it. A drifted probe result is not a repair attempt.

## Recovery Guidance

For a clean single-backing old fingerprint alias, restart the host or otherwise
run the provider-local static reconcile so it can create the expected physical
index, reindex, and swap the alias. Retrying a writer is not a clean drift
migration path; write-side ensure will fail closed on fingerprint drift.

For fail-closed cases, prefer one explicit recovery path:

- restore the missing source index and retry lifecycle
- remove the ambiguous extra alias backing after confirming the intended source
- delete the incomplete expected physical index and retry lifecycle
- rebuild the read model through projection replay into a clean expected alias
- perform an operator-owned export/import and then atomically repoint the alias

Do not add query-time fallback, dual reads, live mapping reads, or request-path
repair to work around a drifted alias.
