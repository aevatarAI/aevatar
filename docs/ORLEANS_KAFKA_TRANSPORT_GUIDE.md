# Orleans Kafka Transport Guide

## 1. Scope

This guide describes the optional Kafka transport adapter for the Foundation Orleans runtime.

Core runtime remains in:

1. `src/Aevatar.Foundation.Runtime.Implementations.Orleans`
2. `src/Aevatar.Foundation.Runtime.Hosting`

Kafka transport adapter is in:

1. `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Transport/MassTransit`

## 2. Default Behavior

Default runtime behavior is unchanged:

1. `ActorRuntime:Provider=InMemory` uses local runtime.
2. `ActorRuntime:Provider=Orleans` uses Foundation Orleans runtime with in-memory stream/provider defaults.

Kafka transport is only enabled when explicitly configured.

## 3. Client Transport

Register Orleans runtime from hosting layer, then set:

1. `ActorRuntime:Provider=Orleans`
2. `ActorRuntime:Transport=Kafka`
3. `ActorRuntime:KafkaBootstrapServers=localhost:9092`
4. `ActorRuntime:KafkaTopicName=aevatar-foundation-agent-events`

The hosting extension:

1. `src/Aevatar.Foundation.Runtime.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`

will invoke:

1. `AddAevatarFoundationRuntimeOrleansKafkaClientTransport(...)`

## 4. Silo Transport

For silo-side Kafka consumption/dispatch, use:

1. `AddAevatarFoundationRuntimeOrleansWithKafkaTransport(...)`

from:

1. `src/Aevatar.Foundation.Runtime.Implementations.Orleans/DependencyInjection/ServiceCollectionExtensions.cs`

This registers:

1. Kafka producer sender (`IOrleansTransportEventSender`)
2. MassTransit consumer (`OrleansTransportEventConsumer`)
3. Grain routing handler (`IOrleansTransportEventHandler`)
4. Runtime dispatch fallback to Kafka sender when sender is registered (`OrleansActor` / `OrleansAgentProxy` / `OrleansGrainEventPublisher`).

## 5. Key Components

1. `OrleansTransportEventMessage`: transport message contract.
2. `KafkaOrleansTransportEventSender`: Kafka producer wrapper.
3. `OrleansTransportEventConsumer`: inbound consumer.
4. `OrleansTransportEventHandler`: routes message bytes to `IRuntimeActorGrain.HandleEnvelopeAsync`.
5. `OrleansActor` and `OrleansGrainEventPublisher`: use `IOrleansTransportEventSender` when available, otherwise keep direct grain dispatch.

## 6. Validation Commands

1. `dotnet build aevatar.slnx --nologo --no-restore -m:1 -nodeReuse:false --tl:off`
2. `dotnet test aevatar.slnx --nologo --no-build --no-restore -m:1 -nodeReuse:false --tl:off`
3. `bash tools/ci/architecture_guards.sh`
