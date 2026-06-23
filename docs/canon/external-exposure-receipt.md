---
title: "External Exposure Receipt"
status: active
owner: eanzhao
---

# External Exposure Receipt

`ExternalExposure` is the actor-owned receipt for publishing an Aevatar service to NyxID. It is not a caller-supplied slug pointer and not a query-time reconstruction. The only authoritative owner is `ServiceDefinitionGAgent`; projection only materializes the actor's committed current state into service catalog readmodels.

## Canonical State

`ServiceDefinitionGAgent` owns the full receipt state:

- `exposure_desired`: typed opt-in intent for this service.
- `status`: `Pending`, `Registering`, `Registered`, `Failed`, or `Retired`.
- `nyxid_service_id` and `nyxid_slug`: NyxID-returned registration receipt values.
- `desired_spec_hash` and `registered_spec_hash`: reconcile drift facts.
- `attempt`, `next_attempt_at`, and `last_error`: retry visibility facts.
- `credential_kid`: identifier for the Aevatar-issued scope credential stored in NyxID.

Create/update service commands may carry only `exposure_desired` as caller intent. Caller-supplied `nyxid_slug`, `nyxid_service_id`, `registered_at`, error, attempt, or credential values are not accepted as facts.

## Bind Intent

Scope binding exposes a typed tri-state `ExposureDesired` request field.

- omitted/null leaves the existing exposure intent unchanged.
- `true` records canonical opt-in intent on the service definition.
- explicit `false` dispatches the existing retire command for an existing service. `ServiceDefinitionGAgent` commits the resulting `Retired` receipt even when no NyxID service id has been returned yet.

Binding does not build OpenAPI URLs, hashes, or registration payloads. Activation committed hooks and explicit bind opt-in share the same external exposure intent service, which computes those Infrastructure facts before dispatching the existing reconcile command.

## Retry Exhaustion

Retry facts are injected as a Core value object: `ServiceExternalExposureRetrySettings`.

Host configuration may set max attempts, base delay, and max delay. It does not provide a retry policy port. `ServiceDefinitionGAgent` computes retry delay and owns exhaustion:

- retryable failures before max attempt persist `FAILED` with `next_attempt_at`.
- max-attempt exhaustion persists `FAILED`, `last_error=retry_exhausted:*`, `attempt=max`, and `next_attempt_at=null`.
- exhausted attempts do not schedule another durable self retry.
- explicit reconcile may restart at attempt 1.

## Read Model Contract

The service catalog projector must materialize exposure records for `Pending`, `Registering`, and `Failed` states even when `nyxid_slug` and `registered_at` are empty. These states are observable facts, not incomplete rows.

Query DTOs expose `externalExposure.sourceStateVersion`. The value is copied from the service catalog readmodel root `StateVersion`, which is sourced from the authoritative service-definition actor committed version. Query paths must not recompute it, increment it locally, or replay events to derive it.

## Related Decision

- [ADR-0035: 已发布 workflow 服务自动注册到 NyxID](../adr/0035-auto-register-published-service-to-nyxid.md)
