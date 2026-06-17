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
run `_reindex`, or swap aliases.

## Lifecycle Entry Points

The lifecycle ensure runs from:

- write-side first touch before an upsert reaches Elasticsearch documents
- the provider-local hosted startup initializer for static read-model aliases
- dynamic index scopes only when a write resolves the concrete scoped alias

All entry points call the same lifecycle manager. The startup initializer skips
dynamic index-scope read models because their concrete aliases are data-driven.

## Reconciliation

The lifecycle manager handles four states:

1. Alias points at the expected physical index: no-op.
2. Alias points at exactly one old fingerprint physical index: create the
   expected physical index, copy old to new with `_reindex`, require no
   per-document failures and no timeout, then atomically remove old / add new in
   one `_aliases` request.
3. A legacy bare index exists with the alias name: create the expected physical
   index, copy bare to physical with `_reindex`, then atomically add the alias
   and remove the bare index in one `_aliases` request.
4. Nothing exists: create the expected physical index with the alias declared in
   the create-index payload.

After a failed lifecycle ensure, read and write traffic must continue failing
closed until the underlying Elasticsearch state is repaired or the migration is
retried successfully.

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

For a clean single-backing old fingerprint alias, restart or retry a writer so
the provider lifecycle can create the expected physical index, reindex, and swap
the alias.

For fail-closed cases, prefer one explicit recovery path:

- restore the missing source index and retry lifecycle
- remove the ambiguous extra alias backing after confirming the intended source
- delete the incomplete expected physical index and retry lifecycle
- rebuild the read model through projection replay into a clean expected alias
- perform an operator-owned export/import and then atomically repoint the alias

Do not add query-time fallback, dual reads, live mapping reads, or request-path
repair to work around a drifted alias.
