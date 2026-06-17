---
title: "Approval Quota Ledger"
status: active
owner: architecture
---

# Approval Quota Ledger

Approval workflows sometimes need to check and consume an allowance before or after a human decision. The allowance itself is not an Aevatar fact. It belongs to an external quota backend or to the native account system of the channel that already owns the allowance.

This document defines the Aevatar-side canon for that boundary. The machine-readable profile is [approval-quota-ledger.openapi.yaml](../contracts/approval-quota-ledger.openapi.yaml). Aevatar may call a registered service through NyxID proxy or expose its marked operations as connected-service tools, but Aevatar must not create a parallel quota ledger.

## 1. Ownership

The authoritative source for each quota fact is one of two surfaces:

| Route | Authority | When to use |
|---|---|---|
| A. External `QuotaLedger` backend | A dedicated downstream service registered in NyxID | The target workflow needs a reusable approval allowance ledger and no channel-native system already owns the quota. |
| B. Channel-native ledger | The channel or SaaS platform that already stores and consumes the allowance | The platform exposes read/write APIs with sufficient scopes and a real subject probe proves the target subject can be resolved. |

NyxID is the credential, proxy, approval, audit, and service-discovery channel. It is not the quota authority. Aevatar is the workflow and orchestration caller. It is not the quota authority.

## 2. Route A: `QuotaLedger` connected-service profile

Route A uses a downstream service that implements the OpenAPI profile in `docs/contracts/approval-quota-ledger.openapi.yaml` and is registered as a NyxID connected service.

Required operations:

| Operation | Purpose | Mutation |
|---|---|---|
| `GET /balances` | Read current available quota for a subject. | No |
| `POST /balances/reserve` | Hold quota before a side effect or approval branch completes. | Yes |
| `POST /balances/deduct` | Finalize a reservation after approval succeeds. | Yes |
| `POST /balances/release` | Release a reservation after rejection, cancellation, expiry, or pre-deduct failure. | Yes |

Required semantic fields are explicit: `subject_id`, `quota_type`, `unit`, `available`, `amount`, `idempotency_key`, `reservation_id`, `ledger_transaction_id`, `effective_at`, and `expires_at`. Do not replace these with a generic bag.

A route A service is not considered usable until all of these facts are recorded in the issue or PR that wires the workflow:

1. NyxID service slug or user-service id, with secrets redacted.
2. OpenAPI spec URL or checked-in equivalent.
3. Subject mapping rule, such as channel user id to employee id.
4. Successful read probe against a real non-production subject.
5. Successful reserve probe and repeated idempotency probe.
6. Successful deduct or release probe in a non-production allowance bucket.

## 3. Route B: Channel-Native Ledger

Route B is preferred when the downstream channel already owns the allowance and exposes enough API surface to support the workflow without duplicating facts.

Before choosing route B, record:

1. The channel API endpoints used for read and mutation.
2. Required OAuth scopes, service permissions, or app grants.
3. The subject mapping from Aevatar caller or channel sender to the channel's quota subject.
4. A read probe for a real non-production subject.
5. A side-effect probe or documented sandbox constraint that proves how the channel consumes or releases quota.

If the channel API automatically consumes quota as part of the approved business action, Aevatar must not also call route A `deduct`. That would create a double-spend path. If the channel only reads quota but does not reserve or consume it, route B is insufficient for workflows that require a hold before approval.

## 4. Workflow Orchestration

No workflow should be changed to call quota endpoints until the selected route has real service registration and probe evidence. Once evidence exists, the expected route A workflow shape is:

1. Read `GET /balances` for the target `subject_id`, `quota_type`, and `unit`.
2. Reserve the requested `amount` with a stable `idempotency_key`.
3. Run the human approval step.
4. On approval success, call `deduct` with a new stable `idempotency_key`.
5. On rejection, cancellation, expiry, or pre-deduct failure, call `release` with a new stable `idempotency_key`.

Idempotency keys are part of the external ledger contract. Aevatar can derive them from stable run, step, and action identifiers, then pass them through the existing NyxID proxy call. Repeated reserve, deduct, or release calls for the same logical action must return the same external result or an explicit conflict.

## 5. Aevatar Boundaries

The following are not allowed:

1. An Aevatar-owned quota balance actor, readmodel, or database table for these allowance facts.
2. A process-local service catalog keyed by service slug, subject id, workflow run id, or session id.
3. Query-time replay, projection priming, or a workflow-side balance reconstruction path.
4. Direct calls to a downstream quota backend that bypass NyxID proxy when NyxID is the configured credential channel.
5. A dependency on new NyxID endpoints, schema fields, or repository changes.

If a future product requirement needs Aevatar to own cross-channel quota aggregation, that is a different capability. It needs an actor-owned domain model and a separate design decision before implementation.

## 6. Probe Evidence Template

Use this template in the implementation issue or PR. Do not paste tokens, raw secrets, or personally sensitive balance values.

| Field | Value |
|---|---|
| Selected route | `A: QuotaLedger` or `B: channel-native` |
| NyxID service slug / channel service | `<redacted slug or service id>` |
| Subject mapping | `<source identifier> -> <external subject id shape>` |
| Quota type and unit | `<quota_type>` / `<unit>` |
| Read probe command | `<redacted command or HTTP request>` |
| Read probe assertion | `subject resolved`, `available present`, `effective_at present` |
| Reserve or channel mutation probe | `<redacted command or HTTP request>` |
| Idempotency assertion | `repeat returned same reservation or transaction` |
| Deduct/release assertion | `deduct finalized` or `release restored hold` |
| Approval policy | `NyxID approval config or channel-native approval note` |

Keep the evidence with the change that wires the real workflow. This canon file remains the stable contract and decision guide; it is not a ledger of individual probe runs.
