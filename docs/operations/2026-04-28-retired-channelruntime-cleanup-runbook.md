# Retired ChannelRuntime Startup Cleanup Runbook

This runbook covers the one-time startup cleanup for actors persisted with
runtime type names from the retired `Aevatar.GAgents.ChannelRuntime` assembly.

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
can rebuild them, activation fails and can abort pod startup.

`LegacyClrTypeName` remains a protobuf payload compatibility tool for renamed state
messages. It does not make a retired actor implementation type safe to activate.
The startup cleanup therefore targets persisted runtime actor type names, not every
legacy protobuf alias.

## Runtime Contract

`Aevatar.Mainnet.Host.Api` registers
`RetiredChannelRuntimeActorCleanupHostedService` before ChannelRuntime, Device, and
Scheduled projection startup services.

At startup the cleanup service:

1. Acquires a one-time event-store marker lease at
   `__maintenance:retired-channelruntime-cleanup:v1`.
2. Probes each known retired actor id through `IActorTypeProbe`.
3. Deletes only actors whose persisted runtime type still references the retired
   `Aevatar.GAgents.ChannelRuntime` implementation or materialization context.
4. Reads the retired user-agent catalog event stream before resetting it, extracts
   generated `skill-runner-*` / `workflow-agent-*` actor ids, and cleans those
   actors first when their runtime type is retired.
5. Removes projection scope relays from their root actor streams.
6. Deletes stale registration/catalog read-model documents for retired root actors
   on a best-effort basis. Projection store failures are logged and do not abort
   startup.
7. Destroys the runtime actor and resets its event stream.
8. Writes a completed marker so later pods skip the cleanup.

Current actors whose runtime type is already under the new namespaces are skipped.
If a previous pod already destroyed an actor but died before resetting its event
stream, the next pod continues the reset path when the actor type is unavailable
but the event stream still exists.

## Targets

- `channel-bot-registration-store`
- `device-registration-store`
- `agent-registry-store`
- generated actors referenced by the retired `agent-registry-store` stream:
  - `skill-runner-*`
  - `workflow-agent-*`
- `projection.durable.scope:channel-bot-registration:channel-bot-registration-store`
- `projection.durable.scope:device-registration:device-registration-store`
- `projection.durable.scope:agent-registry:agent-registry-store`

## Configuration

The cleanup is enabled by default in Mainnet host composition.

Configuration section:

```text
Aevatar:RetiredChannelRuntimeActorCleanup
```

Options:

- `Enabled`: default `true`
- `ResetEventStreams`: default `true`
- `CleanupReadModels`: default `true`
- `InProgressTimeoutSeconds`: default `300`
- `WaitPollMilliseconds`: default `1000`
- `ReadModelCleanupPageSize`: default `500`

Use `Enabled=false` only for emergency rollback while manually clearing the retired
actors. Leaving it disabled means the old activation failure can return on every pod
restart until the persisted data is cleaned.

## Expected Upgrade Behavior

- First pod to start owns the cleanup lease.
- Other pods wait while the marker is in progress.
- If the owning pod dies before completion, another pod can take over after
  `InProgressTimeoutSeconds`.
- Once completed, later deployments do not repeat the destructive cleanup.
- New projection startup then recreates the needed actors using the current runtime
  types and rebuild paths.

## Validation

Healthy startup logs should include either:

- `Retired ChannelRuntime actor cleanup completed.`
- `Retired ChannelRuntime actor cleanup already completed.`

During the first cleanup, `IActorTypeProbe` may activate a retired actor long
enough to read its persisted type name. Orleans can emit transient error logs like
`Unable to resolve agent type Aevatar.GAgents.ChannelRuntime.*` for those actors
before the cleanup removes them. Treat those as expected only when they are followed
by `Retired ChannelRuntime actor cleanup completed.`

The failure signatures below should disappear after the cleanup has completed:

- `Unable to resolve agent type Aevatar.GAgents.ChannelRuntime.*`
- `projection.durable.scope:* is not a ProjectionMaterializationScopeGAgent<...new namespace...>`
