# Agent Profile Final Review Remediation Design

**Status:** Approved remediation for the Phase 1 whole-branch review

## Scope

This remediation closes the five Important findings returned by the whole-branch
review of `d2090689f..c195cedb6d4e6228c8a554668867918d958b0f20`. It does not add a
runtime Profile consumer, change Chat/WebSocket/NyxID/member/channel behavior, or
introduce a second Profile authority or query path.

## 1. Signed Application Ingress

The trust boundary is the Application-owned `IAgentProfileActorPort`, not a
caller-authored envelope publisher string. `CreateAgentProfileCommand`,
`UpdateAgentProfileDraftCommand`, `UpsertAgentProfileSkillBindingCommand`,
`RemoveAgentProfileSkillBindingCommand`, and `PublishAgentProfileCommand` carry a
typed `AgentProfileIngressProof`. The proof contains a key id, target Actor id,
exact command TypeUrl, canonical command digest, and signature.

The canonical command digest is SHA-256 over deterministic Protobuf bytes from a
clone whose proof field is cleared. The signature is RSA-PSS with SHA-256 over a
typed, domain-separated Protobuf material containing the target Actor id, command
TypeUrl, and canonical command digest. The proof therefore cannot authorize a
different target, command family, identity, expected version, draft, binding, or
snapshot. Capturing a proof permits only an exact replay, which remains subject to
Actor idempotency.

`AgentProfileActorPort` signs immediately before dispatch. Namespace/Profile
handlers verify before operation parsing, deduplication, or persistence through
`IAgentProfileIngressProofVerifier`. A missing verifier, missing proof, unknown or
revoked key, malformed digest/signature, or signature mismatch fails closed with
`PROFILE_INGRESS_PROOF_INVALID` and commits no event. Initialization,
continuations, and published-summary observation remain Actor-to-Actor protocol
messages and do not accept an Application proof.

Infrastructure owns the signer and key parsing. Core depends only on the verifier
abstraction. Host binds `Aevatar:AgentProfiles:IngressProof` with one current
PKCS#8 private key and a key-id-indexed set of SubjectPublicKeyInfo validation
keys. Previous public keys may remain during rotation; removed keys are rejected.
No private key, proof, or signature enters an event, Actor state, projection,
audit record, read model, API response, metric label, or log field.

## 2. Discovery Visibility

Human references are globally addressable but not globally visible. After caller
and entry normalization, a user Profile is visible only when
`caller.ScopeId == entry.OwningScopeId` using ordinal equality. A valid
`system/*` Profile remains globally discoverable. An inaccessible user Profile
returns not found and the execution read model is not queried.

## 3. Reconciliation Retry Identity

System reconciliation operation ids for update, binding removal/upsert, and
publish include the authority state version observed in the management read
model. A version race may commit `DRAFT_VERSION_CONFLICT`; while the read model is
still stale, the same observed version replays that result. Once projection
exposes the newer authority version, reconciliation derives a new operation id
and can converge. The create identity remains stable because it has no Profile
authority version.

## 4. Bounded Actor Idempotency Window

Exact replay is explicitly bounded by retained Actor state rather than promised
forever. A Profile retains its single initialization recovery record plus the 256
most recent mutation/publish operation records. The Namespace retains the 1,024
most recent terminal create/summary operation records and additionally pins
provisioning records whose Profile entry is still `PROVISIONING` or `FAILED` so
the continuation protocol remains recoverable. Compaction happens inside state
event application, preserves insertion order, and never uses process-local
state.

Inside the retained window, exact replay and payload-drift conflict behavior are
unchanged. After eviction, an operation id is outside the idempotency guarantee
and is evaluated as a new command against current identity, uniqueness, and
expected-version invariants. This count-bounded contract keeps lookup, cloning,
snapshot size, and committed `state_root` amplification bounded without adding a
cross-provider event-store transaction or a second authority.

## 5. Draft Versus Publish Validity

Multiple `DEFAULT_FOR_UNMATCHED_TURN` bindings are a publish-only invalid state,
not a structural draft error. Initialization, full draft update, and binding
upsert may commit a structurally valid draft containing more than one default.
`validate` and `publish` both report `MULTIPLE_DEFAULT_SKILLS`; the Profile Actor
keeps the same defense before accepting a sealed publish command. This makes the
management workflow capable of representing and repairing an incomplete draft.

## Verification

Tests must prove proof tamper resistance and key rotation, no-event rejection,
same-scope/system discovery, version-race convergence through real Actor and read
model state, bounded/pinned operation retention across replay, and draft versus
publish-only validation. The Agent Profile boundary guard must reject removal of
the proof check or retention policy. Relevant architecture/projection guards,
focused tests, full build, and full test suite are rerun after the fix.

The existing Minor telemetry finding is recorded for later work: readiness
telemetry is wired, while per-operation/outcome telemetry still lacks an ingress
context that distinguishes HTTP, agent tool, and system reconciliation without
leaking transport semantics into the Profile authority.
