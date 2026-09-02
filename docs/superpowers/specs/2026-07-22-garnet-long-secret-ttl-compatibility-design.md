---
title: "Garnet Long Secret TTL Compatibility"
status: approved
owner: eanzhao
---

# Garnet Long Secret TTL Compatibility

## Problem

Studio Team automation provisions a dedicated NyxID Agent Key with a 90-day lifetime and stores the one-time key material in `ISecretVault`. Production verification reached a valid authorization plan, issued the credential, and then failed while storing it with:

```text
ERR value is not an integer or out of range
```

`GarnetBackedSecretVault` calculates a relative TTL from the exact credential expiration and the current millisecond timestamp. When that duration has a millisecond remainder, StackExchange.Redis encodes it as `PX <milliseconds>`. A 90-day duration is approximately 7.8 billion milliseconds, which exceeds the relative-millisecond integer range accepted by the production Garnet server. The materializer then correctly revokes the issued NyxID key, so no orphan key remains, but the automation cannot become active.

## Decision

Keep the existing 90-day credential policy and exact logical expiration. Normalize only long backend TTLs that exceed `Int32.MaxValue` milliseconds to a whole-second duration, rounded up, so StackExchange.Redis emits `EX <seconds>` instead of `PX <milliseconds>`.

The protobuf vault record remains the authority for the exact expiration timestamp. `ResolveAsync` continues to reject the secret at that exact timestamp, so rounding the backend cleanup TTL up by less than one second cannot extend credential usability.

The same compatibility rule applies to compare-and-set rotation. The Garnet Lua script preserves the shorter of the existing and requested TTLs in milliseconds, then uses:

- `PSETEX` when the effective TTL is within the supported millisecond range;
- `SET ... EX` with rounded-up seconds when it exceeds that range.

This keeps normal short-lived secret precision unchanged and prevents rotation from reintroducing the same long-TTL failure.

## Alternatives Rejected

### Shorten Agent Key Lifetime

Reducing the lifetime below the Garnet millisecond limit would hide a persistence defect, change product semantics, and leave other long-lived secrets vulnerable to the same failure.

### Remove Backend Expiration

Persisting without a Garnet TTL would retain encrypted expired records indefinitely. Logical expiration would remain secure, but storage cleanup would regress.

### Clamp To The Maximum Millisecond TTL

Clamping would delete a 90-day credential after roughly 24.8 days and break recurring schedules before the NyxID key expires.

## Ownership And Boundaries

The compatibility conversion belongs in the Garnet persistence implementation. Application, Studio, schedule actor, authorization plan, and NyxID adapter contracts remain unchanged.

The vault record continues to own exact expiration semantics. Garnet TTL remains a storage cleanup mechanism and must not become an alternate business expiration fact.

## Failure Semantics

- Zero or negative TTLs remain invalid where currently rejected.
- Short TTLs retain millisecond precision.
- Long TTL conversion uses checked integer arithmetic.
- Compare-and-set continues to avoid expanding a shorter existing backend TTL, except for the sub-second cleanup rounding already bounded by exact logical expiration.
- NyxID key issuance and cleanup behavior remains unchanged.

## Verification

Tests must prove:

- a 90-day vault write from a clock with a millisecond component is normalized to whole seconds;
- a short vault TTL remains unchanged;
- Garnet set-if-absent accepts a long TTL and reports a remaining TTL near 90 days when the integration environment is available;
- compare-and-set accepts and preserves a long TTL without expanding a shorter existing TTL;
- existing Garnet secret-vault tests pass;
- architecture and test-stability guards pass.

After deployment, production verification repeats the canonical Studio flow:

1. create and bind a fresh deterministic workflow member;
2. refresh the NyxID catalog and obtain a successful preflight plan;
3. create the automation and confirm a new constrained Agent Key ID appears;
4. wait until the automation read model is `active`;
5. execute `run-now` and confirm a new successful workflow run;
6. delete the automation and confirm the dedicated key is revoked;
7. retire and remove temporary verification resources where supported.

No raw Agent Key, bearer token, or vault ciphertext may be printed during verification.

## Non-Goals

- Changing the 90-day credential lifetime.
- Changing NyxID scope-plan or key-creation contracts.
- Backfilling authorization evidence for historical workflow revisions.
- Fixing the separate generic `scheduled_agent_creator` omission of `AuthorizationFact`; that requires its own mapper and fire-path test.
