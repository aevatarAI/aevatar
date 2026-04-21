---
title: "Channel Phase 0 Persistent Provider Validation Result"
status: accepted
owner: eanzhao
---

# ADR-0010: Channel Phase 0 Persistent Provider Validation Result

## Context

Issue `#255` defined Phase 0 as a prerequisite for the Channel RFC. One acceptance item required a documented result for the persistent-provider validation harness before Channel runtime work could rely on durable inbox semantics.

The repository now contains a provider redelivery harness in [PersistentStreamProviderRedeliveryValidationTests](../../test/Aevatar.Foundation.Runtime.Hosting.Tests/PersistentStreamProviderRedeliveryValidationTests.cs), but the in-repo Orleans stream backends remain:

- `InMemory` for local development and deterministic tests
- `KafkaProvider` for the durable backend that exists in this repository today

There is still no EventHubs transport/provider implementation in the repo to validate directly.

## Decision

Phase 0 records the following validation result:

- `KafkaProvider` is the only durable backend validated in-repo today.
- EventHubs is not treated as implicitly equivalent to Kafka.
- Until an EventHubs backend exists in this repository and passes the same throw-vs-return redelivery harness, Channel runtime work must keep Kafka as the durable-provider fallback.

This is an explicit fallback outcome, not an EventHubs pass.

## Evidence

- PR `#267` added the redelivery harness and then narrowed its scope honestly to Kafka-only.
- The harness verifies both required semantics for the currently supported durable backend:
  - `OnNextAsync` returns normally -> no redelivery
  - `OnNextAsync` throws with propagated failure -> message is redelivered

## Consequences

- Issue `#255` can treat the provider-validation acceptance item as documented with a fallback stance.
- Future EventHubs work must extend the harness for EventHubs explicitly and capture a fresh result before claiming EventHubs as a durable inbox provider.
- Channel Phase 1 implementations must not assume EventHubs checkpoint semantics from vendor docs or from Kafka behavior.
