---
title: RoleGAgent provider streaming normalization measurement
status: complete
owner: codex
issue: 3146
source_commit: 80887623f7e8b418e3fb90024ff9bce12ee46670
---

# RoleGAgent provider streaming normalization measurement

## Decision

The provider comparison does not change the existing **no-go** decision on
altering `RoleGAgent` commit boundaries. MEAI and NyxID normalize their
supported text, reasoning, tool, usage, and terminal streaming shapes into the
same typed committed event semantics. Tornado currently exposes only text,
usage, and terminal facts in this adapter path. That is a provider request
mapping gap, not evidence that durable progress should be batched or discarded.

The provider gap should be handled separately from durability work: reconcile
Tornado's declared tool-call capability with its request mapping before relying
on Tornado tool streaming. Do not weaken the canonical CQRS/AGUI observation
path to compensate for it.

Raw samples and provenance are in
[`raw/2026-08-02-role-provider-normalization.json`](raw/2026-08-02-role-provider-normalization.json).
The adjacent `.sha256` sidecar pins the exact checked-in artifact.

## Method

Each sample traverses the repository provider, `RoleGAgent`, a real
`LocalActor` mailbox, `IActorDispatchPort`, the event store, and committed event
publication. Deterministic MEAI and loopback OpenAI-compatible fixtures remove
external model and network variance while retaining the production provider
parsers and request mapping.

Configuration and provenance:

- source commit `80887623f7e8b418e3fb90024ff9bce12ee46670`;
- config SHA-256
  `49d8081c54f0dc270c27dfa3e6de0932f327fa4beab41b175a53fcaaac4c4e1d`;
- source SHA-256 values:
  `MEAILLMProvider.cs=ce780d6259b8516c4450359e08c0fab6ea876996e38c09e550132cc818695b04`,
  `NyxIdLLMProvider.cs=ee9c516fa125911c05ba4346b08038930158b8d9fcda37350ba0eb54b52f7211`,
  `TornadoLLMProvider.cs=586ddb931f6589559f26fabdf029ba630f62c3607d250b896bffdc4fb32cb137`,
  `RoleGAgent.cs=790afed3332bb427e63aac08846956e8e7b8d32798c778a8ea2753b11e3bc298`,
  and
  `ProviderNormalizationMeasurement.cs=5daddbf458e672dadecf46268b97255a31f8a2ac31197dbe89f3fe8feae61050`;
- raw SHA-256
  `c66d36b81d5e5f3e3d59b908796649f64bd3dd6601420ec6537e5f88c0aadb49`;
- macOS 26.3 arm64 and .NET 10.0.3;
- two warmups and twelve measured samples per provider;
- snapshot interval 1,000 with compaction disabled, so the comparison isolates
  provider normalization and per-progress commit behavior;
- loopback protocol fixtures only, with no external credentials or services.

Correctness uses committed typed events, not provider SDK chunk counts. Every
sample requires a strictly monotonic progress sequence, exactly one
`RoleChatSessionCompletedEvent`, append-ledger equality with durable readback,
and durable event-ID equality with committed publications. Supported shapes
also require their typed text, reasoning, media, tool-start, tool-completion,
usage, and terminal evidence.

Percentiles use nearest rank. With twelve samples, p95 and p99 are both the
maximum observed sample and are not production tail estimates.

## Coverage

| Provider | Text | Reasoning | Media | Tool start/completion | Usage | Terminal |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| MEAI | pass | pass | pass | pass | pass | pass |
| NyxID | pass | pass, raw patch | unsupported | pass | pass | pass |
| Tornado | pass | unsupported | unsupported | unsupported | pass | pass |

MEAI and NyxID produced the same non-empty final tool-arguments SHA-256 in all
samples. NyxID does not surface streaming output media through its
OpenAI-compatible route/SDK path.

Tornado's limitation is stronger than an unsupported response part:
`TornadoLLMProvider.MapRequest` does not map `LLMRequest.Tools`. LlmTornado's
OpenAI streaming parser only emits the tool accumulator when its request tools
collection is non-empty. The recorded upstream request consequently has
`toolsAdvertised=false`, so this run cannot honestly claim tool coverage even
though `Capabilities.SupportsToolCalls` is currently true.

## Results

Values are `p50/p95/p99` from twelve samples.

| Provider | Appends | Committed events | Protobuf bytes | Store I/O ms |
| --- | ---: | ---: | ---: | ---: |
| MEAI | 12/12/12 | 12/12/12 | 7,251/7,251/7,251 | 0.0317/0.0390/0.0390 |
| NyxID | 12/12/12 | 12/12/12 | 7,132/7,132/7,132 | 0.0369/0.0440/0.0440 |
| Tornado | 5/5/5 | 5/5/5 | 1,888/1,888/1,888 | 0.0153/2.4104/2.4104 |

| Provider | First token ms | Completion ms | Process CPU ms | Allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| MEAI | 0.1872/5.8517/5.8517 | 1.0571/6.7827/6.7827 | 2.608/8.729/8.729 | 295,584/314,632/314,632 |
| NyxID | 0.7903/4.8071/4.8071 | 2.3581/6.5548/6.5548 | 4.979/10.260/10.260 | 415,280/423,576/423,576 |
| Tornado | 0.6767/0.8777/0.8777 | 0.9400/3.7509/3.7509 | 2.154/5.607/5.607 | 153,392/160,352/160,352 |

The event and byte differences reflect the shapes each production adapter can
actually surface, so the latency and allocation rows are diagnostic rather
than provider rankings. In particular, Tornado's lower event count omits
reasoning, media, and tool facts and cannot be interpreted as an efficiency
win.

## Request facts

Only the following low-cardinality tuples are present:

| Provider | Path | Stream | Usage opt-in | Auth | User-Agent | Tools advertised |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| MEAI | `in-process-ichatclient` | yes | yes | no | no | yes |
| NyxID | `/api/v1/llm/gateway/v1/chat/completions` | yes | yes | yes | no | yes |
| Tornado | `/v1/chat/completions` | yes | yes | yes | yes | no |

Allowed labels are limited to provider and these request-shape fields. Actor,
session, command, and tool-call IDs are not metric labels.

## Architecture conclusion

Provider normalization preserves the required architecture where the adapters
surface equivalent facts: provider-specific frames become typed progress,
then follow one `Command -> committed Event -> projection/observation` path.
The measurement introduces neither a transient AGUI side channel nor a query
fallback.

Combined with the write-amplification and recovery sweep, the data still does
not establish a production capacity, latency, or storage violation. Append
transaction count is the synthetic cost center, while the required recovery
boundaries from #3138 and publication/compaction fences from #3139 remain more
important than an unproven reduction. Close #3146 as measured with no commit
boundary change. Treat Tornado tool request mapping as a separate provider
contract issue.

## Limitations

- Fixtures run in-process or over loopback, not against external model
  endpoints or production network topology.
- CPU and allocation are process-level diagnostics without matched controls in
  this provider-only mode.
- The provider shapes intentionally differ where production adapters lack
  support; they are semantic coverage checks, not equal-work performance
  benchmarks.
- Twelve samples establish deterministic reproducibility, not production p95
  or p99 latency.

## Reproduction

Run:

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-provider-normalization.sh
```

The script runs the Release harness and writes the raw JSON plus its SHA-256
sidecar. The complete fail-closed `jq` assertion is documented in
`tools/measurements/Aevatar.RoleStreamingWriteAmplification/README.md`.
