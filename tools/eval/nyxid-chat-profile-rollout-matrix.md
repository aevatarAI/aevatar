# NyxID Chat Profile Rollout Matrix

The executable 64-case source is `NyxIdChatProfileRolloutEvaluationTests`. This
document is its review index, not a second runner or a second Profile pipeline.

## Authority Under Evaluation

Every case begins with one sealed `AgentProfileExecutionBinding` whose content
originates from Profile Actor committed state through the protected execution
read model. Host release/admission provenance is separate inside the binding.
The turn materializer consumes only that binding, committed turn authority,
route-owned local tool capabilities, and caller visibility. The executable
surface has no Profile query, Actor/event-store read, projection priming, Ornn,
HTTP, or remote skill-fetch dependency.

Create-time companion tests establish the preceding bind:

- resolve typed `system/nyxid-chat` once through the namespace read model;
- use the returned opaque Profile id for one execution snapshot read;
- validate revision, digest, replica agreement, exact closure, runtime bounds,
  route admission, and Host release pins;
- allow unprofiled Actor creation only for `NotSelected`; and
- reject `ProfileUnavailable` and `AdmissionMismatch` before Actor creation.

Conversation binding and replay tests then establish that one complete binding
is committed immutably and later turns/replay perform zero Profile queries and
zero Ornn reads. These are supporting authority checks, not extra matrix cases.

## Exact 64-Case Cross-Product

| Dimension | Values | Required distinction |
|---|---|---|
| Activation | SHADOW, ENFORCED | SHADOW observes but keeps recovery authority; ENFORCED may use a selected sealed body. |
| Selection | exact alias, classifier match, true no-match, classifier failure | Routed match beats default; only true no-match may use a default; classifier failure remains fail-closed. |
| Caller-visible tool surface | full, recovery-only | Caller visibility can only remove capabilities. |
| Route state | clean, same-name object collision | A collision removes the name and degrades to restricted-empty. |
| Binding form | direct, deterministic Protobuf serialize/parse | Serialized replay preserves source/admission provenance, sealed content, policies, and digest. |

`2 x 4 x 2 x 2 x 2 = 64` distinct typed cases.

## Matrix And Companion Invariants

- Profile instructions remain present in the Profile prompt in SHADOW and
  ENFORCED. Canonical `ALWAYS` procedures are covered by focused companion tests:
  they enter every Profile prompt in authoritative order, never route, and never
  widen tools.
- A clean alias or classifier match becomes selected only in ENFORCED. SHADOW
  may retain candidate observation but has no selected prompt layer and uses
  recovery authority.
- True no-match has no routed selected body in this cross-product. Focused
  default-member tests prove `DEFAULT_FOR_UNMATCHED_TURN` applies only to true
  no-match or zero routed candidates; classifier failure, timeout, collision,
  and unknown intent never select it.
- Effective tools are bounded by route ownership, caller visibility, Host
  admission, and the Profile maximum. Recovery applies the recovery policy; an
  ENFORCED selected branch may admit recovery plus selected task policy only
  within every prior ceiling.
- SHADOW and degraded ENFORCED retain recovery tools only. A clean, fully
  visible ENFORCED selection may retain recovery plus task tools. A collision
  yields restricted-empty rather than a name-based capability substitution.
- Direct and serialized bindings produce the same authority, prompt selection,
  and effective tools. Tampering fails closed before local registry discovery.
- No case performs runtime exact Ornn reads, sources Profile content from Host
  rollout data, invokes `protoc`, or reconstructs sealed content from a reference.
- Raw Profile instructions remain within 32,768 UTF-8 bytes. The complete
  materialized Profile prompt bound is exactly 65,536 UTF-8 bytes including
  canonical `ALWAYS` wrappers and separators.

## Promotion Thresholds

Promotion consumes one typed `AgentProfileEvaluationReport` and requires:

| Gate | Threshold |
|---|---|
| Offline invariants | 64/64 |
| Expected-match selection accuracy | At least 95% |
| Expected-match no-match rate | At most 5% |
| Classifier timeout/error rate | At most 1% |
| Safety counters | Zero unsafe admission, approval bypass, replay acceptance, secret telemetry, and SHADOW execution side effects |
| SHADOW latency | Classifier and total added pre-turn p95 at most 600 ms |
| ENFORCED latency | Total pre-turn p95 at most 2100 ms |
| First output | p95 regression at most 10% |
| Product quality | Completion drop at most 5 percentage points; unnecessary tool-round increase at most 5% |
| Online evidence | At least 24 continuous hours and 200 eligible turns per stage |

Insufficient evidence extends observation and never relaxes a threshold.
