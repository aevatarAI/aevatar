---
title: Elasticsearch exact-match field resolution reads live index mapping
status: Accepted
owner: eanzhao
---

# ADR-0025: Elasticsearch exact-match field resolution reads live index mapping

## Context

PR #665 ("Stabilize Elasticsearch projection index mappings", merged 2026-05-18;
design doc `docs/design/2026-05-15-elasticsearch-projection-index-mapping-blueprint.md`)
added `ElasticsearchProjectionDescriptorMappingSupport.AugmentMetadata`: for every
read-model string field whose name matches a stable-identifier shape (`*_id`,
`*_key`, `*_hash`, `*_status`, `*_kind`, `*_type`, ...), the provider injects a
`{"type":"keyword"}` entry into the in-memory `DocumentIndexMetadata.Mappings`.

`BuildExactMatchFieldPathResolver` consulted that augmented metadata to decide
whether an exact-match (`term`) filter targets a field directly or through its
`.keyword` sub-field. The contradiction:

- **Augmented metadata is the code's *intent*** — it says "this field should be
  a keyword."
- **An Elasticsearch index created before that intent shipped keeps its original
  mapping forever.** A string field on such an index carries the ES dynamic
  default — `text` with a `.keyword` multi-field — and `EnsureIndexAsync` never
  reconciles an already-existing index (it treats `resource_already_exists` as
  "done").

For any index created before 2026-05-18, the resolver therefore saw `keyword`
(intent) and emitted the bare field path, while the field was physically `text`
(truth). The `term` query hit the analyzed `text` field and returned **0 hits**
for identifier-shaped values — silently. This took down the Lark bot on
2026-05-20 (issue #743): the relay callback's scope resolver could not resolve
`apiKeyId → scopeId`, and every inbound relay callback returned 401.

The 2026-05-15 blueprint anticipated incompatibility (§5 hard-constraint #2: the
mapping helper works only from the proto contract + declared
`DocumentIndexMetadata`, never from runtime index state; #9: incompatible
contract changes require a manual clear/rebuild). That stance is defensible for
*index creation*, but it left the *read path* trusting intent over physical
truth, and the rebuild runbook was enforced by no gate.

## Decision

### D1 — The exact-match resolver reads the live index `_mapping`

`ElasticsearchProjectionDocumentStore.QueryAsync` resolves `keyword`/`text` field
paths from the **actual** Elasticsearch mapping of the target index, obtained via
`GET <index>/_mapping` (`ElasticsearchIndexLifecycleManager.GetActualFieldMappingsAsync`),
not from the code-side augmented `DocumentIndexMetadata`.

This narrows blueprint hard-constraint #2 for the read path only: exact-match
`term` resolution is now sourced from index truth. Index *creation* still works
purely from the proto contract + declared metadata — `AugmentMetadata` and
`EnsureIndexAsync` are unchanged.

### D2 — Reading mapping is not query-time repair

`GET _mapping` reads index schema metadata. It performs no mapping mutation, no
reindex, no document backfill, and no event replay. The query path stays free of
repair/priming side effects (CLAUDE.md "query path 禁止执行 mapping mutation /
repair"; blueprint §5 #3). The provider still does not do online index repair or
document-level dual-read.

### D3 — Probe failure falls back to declared metadata

When the `_mapping` probe cannot read physical truth (index absent, ES
unreachable, HTTP timeout, unparseable body), the resolver falls back to the
augmented `DocumentIndexMetadata` — the pre-#743 behaviour. A best-effort probe
must never turn a transient mapping-endpoint failure into a query failure.

### D4 — The probe result is cached per index for the store lifetime

`GetActualFieldMappingsAsync` caches a successful read per index name. Steady-state
cost is one extra `GET _mapping` per index per process. Mapping drift within a
process lifetime is not a concern for stable query fields — they exist in the
proto contract from the start; a process restart re-probes.

### D5 — Scope: query path only

This ADR fixes the exact-match *filter* resolution that caused #743. It does not
introduce alias indirection, schema fingerprinting, blue-green reindex migration,
or a real-Elasticsearch CI suite. Those (issue #743 phases P1–P3, P5) remain
tracked by #743 as a separate index-lifecycle effort; they are required neither to
recover the outage nor to make the query path drift-tolerant.

## Alternatives considered

- **Revert #665.** Rejected: descriptor-driven keyword mapping for new indices is
  correct and wanted. The missing piece is read-path drift tolerance, not the
  augmentation itself.
- **Heuristic patch to the resolver** (e.g. "always also try `.keyword`").
  Rejected — #743 non-goal #8. A blind second guess deepens implicit-convention
  debt; reading the index's real mapping is ground truth, not a heuristic.
- **Manual clear/rebuild runbook** (the blueprint's original stance). Rejected as
  the *primary* mechanism: it is enforced by no gate and already failed in
  production. Reading index truth makes the query path correct without an
  operator step.
- **The full index-lifecycle epic now** (alias + fingerprint + migration +
  Testcontainers). Deferred: too large for one PR onto the live deploy branch and
  unnecessary to recover the outage. Tracked by #743.

## Consequences

- Every projection index created before 2026-05-18 with dynamic string mappings
  now answers identifier-shaped exact-match queries correctly — the Lark
  registration lookup and every latent variant recover without an operator
  touching production.
- One additional cached `GET _mapping` round-trip per index per process.
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/README.md` "自动索引映射"
  is updated: the provider reads live mapping for read-side field resolution (it
  still does not repair or rebuild indices).
- The blueprint's "no runtime index state" constraint now has a recorded, scoped
  exception; future read-path work references this ADR instead of silently
  re-deciding.

## References

- Issue #743 — ES projection index lifecycle: schema-drift gap silently breaks
  by-field queries (Lark bot outage 2026-05-20).
- PR #665 — Stabilize Elasticsearch projection index mappings.
- `docs/design/2026-05-15-elasticsearch-projection-index-mapping-blueprint.md` —
  §5 hard-constraints #2/#3, §9 target architecture.
- CLAUDE.md — "权威状态 / ReadModel / Projection（强制）", "正确架构优先".
