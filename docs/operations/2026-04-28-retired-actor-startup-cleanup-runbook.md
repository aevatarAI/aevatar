# Retired Actor Startup Cleanup Runbook

This runbook covers the spec-driven startup cleanup that destroys actors whose
persisted runtime types reference assemblies/types that no longer exist. The
cleanup runs on every pod startup and is intrinsically idempotent — when nothing
matches a registered retired-actor spec the run is a no-op.

## Problem

Older deployments persisted runtime actor identities such as:

- `Aevatar.GAgents.ChannelRuntime.ChannelBotRegistrationGAgent`
- `Aevatar.GAgents.ChannelRuntime.UserAgentCatalogGAgent`
- `Aevatar.GAgents.ChannelRuntime.SkillRunnerGAgent`
- `Aevatar.GAgents.ChannelRuntime.WorkflowAgentGAgent`
- `Aevatar.CQRS.Projection.Core.Orchestration.ProjectionMaterializationScopeGAgent<T>` where `T` is a retired `Aevatar.GAgents.ChannelRuntime.*MaterializationContext`

After the split into `Aevatar.GAgents.Channel.Runtime`, `Aevatar.GAgents.Device`,
and `Aevatar.GAgents.Scheduled`, those actor implementation types no longer exist.
When Orleans activates the persisted actors during startup or rebuild paths,
activation fails and can abort pod startup. The same pattern appears whenever a
runtime CLR type is renamed or moved across assemblies.

`LegacyClrTypeName` remains a protobuf payload compatibility tool for renamed
state messages; it does not make a retired actor implementation type safe to
activate. The startup cleanup therefore targets persisted runtime actor type
names, not every legacy protobuf alias.

## Architecture

`RetiredActorCleanupHostedService` (in `Aevatar.Foundation.Runtime.Hosting`) is
registered by `Mainnet.Host.Api` via `services.AddRetiredActorCleanup()`. It is a
best-effort, restart-idempotent maintenance pass, not a cross-pod completion
barrier for per-module projection startup services. It iterates every
`IRetiredActorSpec` registered in the container.

Each retired module ships its own `IRetiredActorSpec` implementation alongside its
DI extension (`AddChannelRuntime`, `AddDeviceRegistration`, `AddScheduledAgents`).
A spec declares:

- `SpecId` — stable identifier used for logs and operational diagnostics.
- `Targets` — well-known retired actor ids and the CLR type name tokens that
  identify them as retired.
- `DiscoverDynamicTargetsAsync` — optional. The Scheduled spec uses this to read
  the typed `UserAgentCatalogDocument` read model and surface generated
  `skill-runner` / `workflow_agent` entries that need cleaning before the catalog
  itself is destroyed. Discovery is gated on the catalog runtime type still
  looking retired so warm clusters do not pay the catalog read-model query on
  every startup. Actor ids from the read model are treated as opaque addresses;
  the decision that a row is a generated user agent comes from
  `UserAgentCatalogDocument.AgentType`.
- `DeleteReadModelsForActorAsync` — optional. Each module deletes its own typed
  `IProjectionDocumentReader` / `IProjectionWriteDispatcher` documents (no
  cross-module document knowledge).

For each spec the service:

1. Triggers a background-service cleanup pass during host startup; module startup
   is not blocked on completion.
2. Streams targets from `DiscoverDynamicTargetsAsync` first, then iterates
   `Targets`.
3. For each target: probes the runtime type via `IActorTypeProbe`, then
   revalidates the same target immediately before destructive work. When the
   final check still matches a retired token, it removes upstream relays from
   `SourceStreamId`, removes outgoing relays best-effort, deletes module-owned
   read models best-effort, destroys the actor, and resets the event stream.

There is no "completed forever" marker. The cleanup runs every startup; targets
already cleaned by a previous pod are detected as either "no runtime type and no
event stream" (skip) or "no runtime type but stream still present" (continue
reset path). Targets recreated with a current runtime type between discovery and
cleanup are skipped by the per-target revalidation fence.

## Active Specs

| SpecId            | Module                            | Targets |
|-------------------|-----------------------------------|---------|
| `channel-runtime` | `Aevatar.GAgents.Channel.Runtime` | `channel-bot-registration-store`, `projection.durable.scope:channel-bot-registration:channel-bot-registration-store` |
| `device`          | `Aevatar.GAgents.Device`          | `device-registration-store`, `projection.durable.scope:device-registration:device-registration-store` |
| `scheduled`       | `Aevatar.GAgents.Scheduled`       | `agent-registry-store`, `projection.durable.scope:agent-registry:agent-registry-store` + dynamic generated user agents discovered from typed catalog read-model rows |

## Configuration

The cleanup is enabled by default in Mainnet host composition. Configuration
section:

```text
Aevatar:RetiredActorCleanup
```

Options:

- `Enabled`: default `true`
- `ResetEventStreams`: default `true`
- `CleanupReadModels`: default `true`

Use `Enabled=false` only for emergency rollback while manually clearing the
retired actors. Leaving it disabled means the old activation failure can return
on every pod restart until the persisted data is cleaned.

## Adding a New Retired-Actor Spec

When a new runtime CLR type is retired (rename, move, delete):

1. Add a class implementing `IRetiredActorSpec` (or extending `RetiredActorSpec`)
   in the module that owns the replacement. Declare the retired type tokens and
   the well-known actor ids that previously persisted them.
2. Override `DeleteReadModelsForActorAsync` if the module owns documents whose
   `ActorId` field references the retired actors.
3. Register the spec via `services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetiredActorSpec, …>())`
   in the module's DI extension.

The next deployment automatically runs the new spec on every pod startup until
the targets are fully cleaned (and remains a no-op afterwards). No changes to
`Mainnet.Host.Api` are needed.

## Expected Upgrade Behavior

- Every pod may trigger the startup cleanup pass.
- Duplicate cleanup passes converge through per-target revalidation and
  idempotent actor / stream / read-model cleanup.
- New projection startup recreates the needed actors using the current runtime
  types and rebuild paths. Startup cleanup is best-effort background work; it is
  not a startup-wide readiness barrier.

## Validation

Healthy startup logs should include one entry per spec:

- `Retired actor cleanup pass finished for spec channel-runtime.`
- `Retired actor cleanup pass finished for spec device.`
- `Retired actor cleanup pass finished for spec scheduled.`

During the first cleanup of a newly-retired type, `IActorTypeProbe` may activate
the retired actor long enough to read its persisted type name. Orleans can emit
transient error logs like `Unable to resolve agent type …` for those actors
before the cleanup removes them. Treat those as expected only when they are
followed by `Retired actor cleanup pass finished for spec …`.

The failure signatures below should disappear once the relevant retired actor
targets have been cleaned by one of the idempotent startup passes:

- `Unable to resolve agent type Aevatar.GAgents.ChannelRuntime.*`
- `projection.durable.scope:* is not a ProjectionMaterializationScopeGAgent<...new namespace...>`
