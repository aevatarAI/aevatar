# Channel Workflow Result Delivery In-Place Repair Design

## Problem

Some existing Lark channel registrations can receive messages and load their
bound Ornn skill, but cannot start a workflow-backed Team entry. These
registrations have a NyxID channel bot, conversation route, and agent API key,
but their authoritative `ChannelBotRegistrationEntry` has no usable
`workflow_result_delivery_credential`.

The workflow invocation dispatcher correctly fails closed before dispatch. A
background workflow result needs a durable agent credential after the inbound
reply token expires. Starting the workflow without that credential would create
a run whose terminal result could not be delivered to the originating chat.

Re-registering currently repairs the missing credential by creating a new
NyxID channel bot and webhook URL. That forces the owner to edit the Lark
developer console again. The registration UI also omits workflow delivery
capability from its response and list models, so a degraded registration is
presented as fully registered.

## Semantic Decision

Workflow result delivery is a capability of an existing channel registration,
not a separate Lark bot and not a property of the loaded skill. Repair must
preserve the registration's Lark-facing identity while rotating only the
NyxID agent credential and rebinding its existing internal route.

`ChannelBotRegistrationGAgent` remains the authoritative owner of the active
NyxID agent API key id, workflow delivery credential handle, and repair
progress. NyxID owns the external API key and conversation route. `ISecretVault`
owns raw credential material. The registration read model and `/channels` UI
only expose the actor's committed capability state.

## Goals

- Repair an owner-scoped Lark registration without changing its
  `nyx_channel_bot_id`, webhook URL, Lark app id, permissions, or event
  subscriptions.
- Use NyxID's existing API-key rotation and conversation-route update APIs.
- Keep the one-time `full_key` out of Protobuf state, events, read models,
  HTTP responses, and logs.
- Make interrupted repairs resumable from actor-owned typed state.
- Expose an honest workflow result delivery status and a focused repair action
  in `/channels`.
- Prevent a channel configuration failure from triggering an irrelevant Ornn
  replacement-skill search.

## Non-Goals

- Automatically migrate every historical registration in a background batch.
- Change the Lark developer-console configuration.
- Use the short-lived inbound reply token for workflow terminal delivery.
- Weaken the pre-dispatch `channel_workflow_delivery_unavailable` gate.
- Add another workflow execution or observation path.
- Add a general-purpose channel credential repair framework.

## Selected Approach

Use an owner-initiated in-place rotation repair. A brief relay interruption is
acceptable while NyxID deactivates the old key and the existing route is
updated to the rotated key id.

Alternatives were rejected as follows:

- A shadow-key migration avoids interruption but must duplicate all key scope
  fields and per-service bindings. NyxID rotation already preserves those facts,
  so the additional migration machinery is not justified for this repair.
- Bypassing the delivery gate or waiting for workflow completion inside the
  original chat turn depends on an expiring credential and can silently lose
  terminal results.
- Full channel re-provisioning changes the Lark-facing webhook and does not meet
  the requirement.

## Contracts And State

Add explicit Protobuf contracts under the channel registration boundary. Exact
field numbers are assigned without reusing reserved fields.

### Repair Status

`ChannelWorkflowResultDeliveryRepairStatus` has these values:

- `UNSPECIFIED`
- `REQUESTED`
- `CREDENTIAL_PREPARED`
- `FAILED`

Completion is represented by the active registration fields plus the committed
completion event; no permanent `COMPLETED` workflow state is needed. The read
model derives the public capability status from active fields and any pending
repair state.

`ChannelWorkflowResultDeliveryRepairPhase` and
`ChannelWorkflowResultDeliveryRepairFailureReason` are enums. Phases cover
request admission, key rotation, vault storage, route rebinding, and actor
completion. Failure reasons cover the stable decisions needed for retry and UI
guidance, including unauthorized owner, unsupported platform, stale active key,
rotation failure, vault storage failure, route update failure, completion
failure, and ambiguous rotated-key recovery. Exception text remains diagnostic
logging and never drives control flow.

### Actor State

`ChannelBotRegistrationEntry` gains an optional typed
`workflow_result_delivery_repair` sub-message containing:

- `request_id`
- `status`
- `expected_api_key_id`
- `expected_conversation_route_id`
- `rotated_api_key_id`
- `prepared_secret_reference`
- typed `failure_phase`
- typed `failure_reason`
- `requested_by_subject_id`
- `requested_at_unix_ms`
- `updated_at_unix_ms`

It never contains a bearer token or `full_key`.

### Commands And Events

The registration actor accepts narrow commands:

- `ChannelBotWorkflowResultDeliveryRepairRequestCommand`
- `ChannelBotWorkflowResultDeliveryRepairPrepareCommand`
- `ChannelBotWorkflowResultDeliveryRepairCompleteCommand`
- `ChannelBotWorkflowResultDeliveryRepairFailCommand`

It commits corresponding requested, prepared, completed, and failed domain
events. Every phase validates `registration_id`, `request_id`, the expected
active API key id, and the prior repair phase. Duplicate commands with identical
facts are idempotent. Conflicting or stale commands commit a typed rejected
outcome and never overwrite newer registration state.

The completed transition changes only:

- `nyx_agent_api_key_id`
- `workflow_result_delivery_credential`
- `workflow_result_delivery_repair` (cleared)

It preserves registration id, channel bot id, conversation route id, webhook
URL, scope, provider slug, default skill, creation time, inbound activation
time, and tombstone fields.

## Application Flow

Add a typed owner-facing application service and expose it through:

```text
POST /api/channels/registrations/{registrationId}/workflow-result-delivery/repair
```

The Host endpoint performs authentication and request/response adaptation only.
The application service owns the repair orchestration through narrow NyxID,
vault, registration-command, registration-query, and committed-outcome
observation ports.

The normal flow is:

1. Read the registration read model and require an active Lark registration
   owned by the caller's scope.
2. If the typed credential is already usable and no repair is pending, return
   `already_enabled` without rotating anything.
3. Dispatch the repair request command with the current API key id and route id.
4. Observe the committed requested outcome through a bounded projection session.
5. Rotate the current NyxID API key. Rotation returns a new key id and one-time
   `full_key` while preserving scopes, callback URL, expiry, rate limits, and
   agent service bindings.
6. Immediately store `full_key` in `ISecretVault` using purpose
   `channel.workflow-result-delivery-agent-key`, owner scope equal to the
   registration scope, and subject equal to the rotated API key id.
7. Dispatch and observe the prepared command containing only the rotated key id
   and cloned `SecretReference`.
8. Update the existing NyxID conversation route with the rotated key id and
   `default_agent=true`.
9. Dispatch and observe the complete command. The actor atomically promotes the
   prepared key id and credential handle into the active registration.
10. Return `repaired` only after the committed completion outcome is observed.

The endpoint returns the repair request id, committed status, registration id,
and the new non-secret API key id. It never returns the raw key or secret
reference.

## Interruption And Retry Semantics

The repair is forward-only after NyxID rotation because the old key becomes
inactive immediately.

- Cancellation before rotation leaves the registration unchanged.
- Once rotation succeeds, the critical prepare sequence uses a detached bounded
  completion token so caller cancellation does not discard the one-time key.
- Vault writes receive bounded retries. If they still fail, the service records
  the rotated key id and a typed vault-storage failure outcome before
  returning whenever the process remains alive.
- A retry from a vault-storage failure rotates the recorded active replacement
  key again to obtain a new one-time value, then stores and prepares that newer
  key. It never retries rotation against the now-inactive original key.
- A retry with `CREDENTIAL_PREPARED` skips rotation and vault storage, repeats
  the idempotent route update, and attempts completion.
- A route or completion failure retains the prepared key id and secret reference
  even while public status is `repair_failed`; retry resumes from the prepared
  phase rather than rotating again.
- A retry after route update but before actor completion repeats the route update
  and completion command with the same request id.
- If the process terminates after rotation but before the rotated id is recorded,
  the retry discovers the unique owner-scoped active relay key whose exact
  deterministic name belongs to the registration and whose creation time is
  not older than the repair request. Because its one-time value is unavailable,
  it rotates that key once more and continues. Zero or multiple candidates
  return the typed ambiguous-recovery reason; the implementation must not guess.
- A failed repair remains visible as actor-owned state. It is not reconstructed
  from logs, a process-local dictionary, or query-time event replay.

No test or implementation path uses polling delays. Completion observation uses
the existing projection-session event pattern.

## Authorization And Secret Handling

- The caller must own the registration scope. Platform-admin visibility alone
  does not grant authority to rotate another owner's NyxID key.
- The NyxID bearer remains method-local and is never copied into a command,
  event, actor state, read model, or log field.
- The `full_key` is passed directly from the rotation response parser to
  `ISecretVault.PutAsync` and then discarded.
- Logs contain repair id, registration id, phase, and non-secret resource ids.
  They never contain request bodies, bearer values, `full_key`, or vault refs.
- The endpoint is included in the existing channel registration audit boundary.

## Read Model And UI

The registration query response exposes one typed product status:

- `enabled`
- `repair_required`
- `repairing`
- `repair_failed`

Optional non-secret typed failure phase and reason values are returned only for
the failed state. The raw `SecretReference` is never serialized to the browser.

The `/channels` manage surface shows this status alongside the channel's active
status. For `repair_required` and `repair_failed`, it offers a command button
named `Repair workflow replies`. During `repairing`, the action is disabled and
the committed phase is shown. Success explicitly says that no Lark-side changes
are required.

New registration responses also carry the status. A bot that can chat but lacks
workflow delivery is shown as partially configured rather than as an
unqualified registration success.

## Skill Recovery Semantics

`channel_workflow_delivery_unavailable` is a configuration-required tool
outcome, not evidence that the loaded skill is wrong. The typed tool-failure
classification marks it as non-recoverable by skill discovery. The recovery
planner must not invoke `ornn_search_skills` for this outcome and the final reply
must point to the channel repair action.

This classification is based on the typed error code/outcome. It must not add
another English or Chinese phrase match.

## Verification

Implementation follows TDD and adds focused coverage for:

- registration actor request/prepare/complete/fail transitions;
- duplicate and stale repair commands;
- preservation of every unrelated registration field;
- owner-scope authorization and non-Lark rejection;
- already-enabled idempotency;
- rotate, vault, route-update, and actor-completion call ordering;
- retry from `CREDENTIAL_PREPARED` without another rotation;
- recovery of the unique post-rotation key and rejection of ambiguous matches;
- absence of raw key material in state, events, JSON, and logs;
- unchanged channel bot id, route id, and webhook URL;
- registration list and `/channels` status/action behavior;
- repaired channel context dispatching a workflow Team entry successfully;
- `channel_workflow_delivery_unavailable` not triggering Ornn skill recovery.

Required checks include targeted channel runtime, invocation, and AI tests,
`bash tools/ci/test_stability_guards.sh`,
`bash tools/ci/architecture_guards.sh`, and the relevant solution build/test
slice. The canonical workflow delivery documentation is updated to replace the
current re-registration-only migration rule with this in-place repair contract.
