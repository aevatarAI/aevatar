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
When Orleans activates the persisted actors before the new projection startup path
can rebuild them, activation fails and can abort pod startup. The same pattern
appears whenever a runtime CLR type is renamed or moved across assemblies.

`LegacyClrTypeName` remains a protobuf payload compatibility tool for renamed
state messages; it does not make a retired actor implementation type safe to
activate. The startup cleanup therefore targets persisted runtime actor type
names, not every legacy protobuf alias.

## Architecture

`RetiredActorCleanupHostedService` (in `Aevatar.Foundation.Runtime.Hosting`) is
registered by `Mainnet.Host.Api` via `services.AddRetiredActorCleanup()`, ahead of
the per-module projection startup services. It iterates every
`IRetiredActorSpec` registered in the container.

Each retired module ships its own `IRetiredActorSpec` implementation alongside its
DI extension (`AddChannelRuntime`, `AddDeviceRegistration`, `AddScheduledAgents`).
A spec declares:

- `SpecId` — stable identifier used as the marker stream namespace.
- `Targets` — well-known retired actor ids and the CLR type name tokens that
  identify them as retired.
- `DiscoverDynamicTargetsAsync` — optional. The Scheduled spec uses this to read
  the user-agent catalog and surface generated `skill-runner-*` /
  `workflow-agent-*` actor ids that need cleaning before the catalog itself is
  destroyed. Discovery is gated on the catalog runtime type still looking
  retired (or the catalog state being cleared but its event stream still having
  events) so warm clusters do not pay the catalog walk on every startup.
  When the gate fires, ids are read from the `UserAgentCatalogDocument` read
  model first (survives event-stream snapshot+compaction), then merged with
  any catalog upsert events not yet projected.
- `DeleteReadModelsForActorAsync` — optional. Each module deletes its own typed
  `IProjectionDocumentReader` / `IProjectionWriteDispatcher` documents (no
  cross-module document knowledge).

For each spec the service:

1. Acquires a per-spec lease at `__maintenance:retired-actor-cleanup:{specId}`
   (waits if another pod holds an in-progress lease, takes over after
   `InProgressTimeoutSeconds`).
2. Streams targets from `DiscoverDynamicTargetsAsync` first, then iterates
   `Targets`.
3. For each target: probes the runtime type via `IActorTypeProbe`. When it
   matches a retired token, removes upstream relays from `SourceStreamId`,
   removes outgoing relays best-effort, deletes module-owned read models
   best-effort, destroys the actor, and resets the event stream.
4. Releases the lease.

There is no "completed forever" marker. The cleanup runs every startup; targets
already cleaned by a previous pod are detected as either "no runtime type and no
event stream" (skip) or "no runtime type but stream still present" (continue
reset path).

## Active Specs

| SpecId            | Module                            | Targets |
|-------------------|-----------------------------------|---------|
| `channel-runtime` | `Aevatar.GAgents.Channel.Runtime` | `channel-bot-registration-store`, `projection.durable.scope:channel-bot-registration:channel-bot-registration-store` |
| `device`          | `Aevatar.GAgents.Device`          | `device-registration-store`, `projection.durable.scope:device-registration:device-registration-store` |
| `scheduled`       | `Aevatar.GAgents.Scheduled`       | `agent-registry-store`, `projection.durable.scope:agent-registry:agent-registry-store` + dynamic `skill-runner-*` / `workflow-agent-*` discovered from the catalog stream |

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
- `InProgressTimeoutSeconds`: default `300`
- `WaitPollMilliseconds`: default `1000`

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

- First pod to start in a deployment wave acquires each spec's lease.
- Other pods wait while a spec's marker is in progress.
- If the owning pod dies before completion, another pod takes over after
  `InProgressTimeoutSeconds`.
- New projection startup recreates the needed actors using the current runtime
  types and rebuild paths.

## Validation

Healthy startup logs should include one entry per spec:

- `Retired actor cleanup completed for spec channel-runtime.`
- `Retired actor cleanup completed for spec device.`
- `Retired actor cleanup completed for spec scheduled.`

During the first cleanup of a newly-retired type, `IActorTypeProbe` may activate
the retired actor long enough to read its persisted type name. Orleans can emit
transient error logs like `Unable to resolve agent type …` for those actors
before the cleanup removes them. Treat those as expected only when they are
followed by `Retired actor cleanup completed for spec …`.

The failure signatures below should disappear after the cleanup has completed:

- `Unable to resolve agent type Aevatar.GAgents.ChannelRuntime.*`
- `projection.durable.scope:* is not a ProjectionMaterializationScopeGAgent<...new namespace...>`
