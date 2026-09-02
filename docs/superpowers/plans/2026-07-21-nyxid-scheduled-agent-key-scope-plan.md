---
title: "Implement NyxID Scheduled Agent Key Scope Plan Integration"
status: active
owner: eanzhao
---

# Implementation Plan

## 1. Lock The External Adapter Contract

- Add focused NyxID client tests for `GET /api/v1/user-services` and `POST /api/v1/api-keys/scope-plan`.
- Add typed response/error parsing that rejects malformed enums, declarations, identities, timestamps, duplicates, and inconsistent flattened sets.
- Add reusable NyxID API access DI registration and verify standalone scheduled and Studio composition.

## 2. Evolve Authorization Protobufs

- Replace route-topology node evidence with resource owner, typed node requirement, and node IDs.
- Embed node IDs in each service grant and delete the separate plan node-grant collection.
- Replace the nonexistent external revision with typed scope-plan contract/policy versions and provider evaluation time.
- Reserve removed protobuf numbers and names.
- Update proto round-trip and integrity tests first and observe their expected failures.

## 3. Materialize Published Catalog Evidence

- Rewrite refresh-port tests for inventory filtering, exact scope-plan request, personal owner validation, success observation, and sanitized failures.
- Implement inventory plus full eligible-set scope-plan refresh.
- Update catalog actor validation, state transitions, command mapping, projection, and query mapping.
- Preserve actor-owned committed state and the existing projection pipeline; do not add query-time HTTP or projection priming.

## 4. Canonicalize Local Authorization Plans

- Rewrite planner tests from order/multiplicity assertions to ordinal-sorted unique permission sets.
- Map exact per-service resource owner and node IDs from the current catalog replica.
- Keep local protobuf content and permission digests deterministic and independent from NyxID's mutation digest.

## 5. Enforce Targeted Mutation Precondition

- Add issuer tests that require a targeted scope-plan call before key creation.
- Compare response authority, versions, actor/owner, declarations, per-service grants, and flattened sets with the validated local plan.
- Create the key only with response allowlists, both allow-all flags false, and the targeted `scope_plan_digest`.
- Verify organization target mapping remains explicit without enabling organization catalog refresh.

## 6. Remove False Runtime Topology

- Delete role, edge, binding, priority, and redundant runtime node-grant fields.
- Keep per-service node IDs in the authorization fact and scheduled actor state.
- Update dispatch validation and affected Studio/scheduled tests.
- Search the changed authorization surface for stale topology terminology.

## 7. Document And Verify

- Update `docs/canon/scheduled-skill-runners.md` with the catalog/effect split and exact digest semantics.
- Run focused GAgentService, ChannelRuntime, Studio, NyxID tool-provider, and capability composition tests.
- Run `test_stability_guards.sh`, query/projection guards, proto lint, solution split guards, architecture guards, and docs lint.
- Run solution restore/build and broader tests as time permits.
- Request independent review, resolve Critical/Important findings, rebase or merge the latest remote target without force, commit, and push `HEAD:feature/integrate`.
