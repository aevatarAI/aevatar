---
title: "Mainnet Production Clustering Uses Shared Garnet Membership"
status: accepted
owner: eanzhao
---

# ADR-0032: Mainnet Production Clustering Uses Shared Garnet Membership

## Context

Production (`aevatar-console-backend`, single replica, rolling deploy) ran the
`Distributed` profile with `Orleans:ClusteringMode=Localhost` while grain state
(`RedisGrainStorage`) and the reminder table (`UseRedisReminderService`) lived
in a shared Garnet instance under a stable `ServiceId`.

`UseLocalhostClustering` makes each silo a complete single-member cluster.
During every rolling deploy the old and new pod therefore each owned the entire
reminder consistent-hash ring over the *same* shared reminder table: both fired
every due reminder, both activated `RuntimeCallbackSchedulerGrain`, and both
wrote the same grain-state keys. The result was ~90 seconds of
`InconsistentStateException: Version conflict (WriteStateAsync)` etag
ping-pong, `Could not deliver reminder tick`, and deactivation storms until the
old pod died — delaying and duplicating durable callback fires (scheduled
dispatches, retry timers, one-shot reminders) on every deploy.

ADR 0002 already flagged this direction: production clustering should move to a
persistent membership provider instead of depending on a primary silo or
localhost membership.

## Decision

- Add `Orleans:ClusteringMode=Garnet` to the mainnet host: Orleans Redis
  clustering (`Microsoft.Orleans.Clustering.Redis`) over the same Garnet
  connection string used for grain state and reminders
  (`ActorRuntime:OrleansGarnetConnectionString`).
- The `Distributed` profile defaults to `Garnet` clustering. Membership,
  reminder table, and grain state now share one authoritative store and one
  stable `ClusterId`/`ServiceId`, so silos that overlap during a rolling deploy
  join one cluster, partition the reminder ring, and keep a single activation
  per grain.
- When `Orleans:SiloHost` is unset, the silo advertises the first non-loopback
  interface address (the pod IP inside Kubernetes) and, with
  `Orleans:ListenOnAnyHostAddress=true`, listens on all interfaces. No
  deployment-manifest change is required.
- `Localhost` remains the code default for single-process development;
  `Development` remains for fixed-primary multi-node smoke testing. Neither is
  valid for multi-replica or rolling-deploy production topologies.

## Alternatives Rejected

- **Per-deployment `ServiceId`**: `ServiceId` namespaces the reminder table and
  the grain-state keys (e.g.
  `aevatar-runtime-callback-scheduler/{grainId}/{serviceId}`). A new
  `ServiceId` per deploy orphans every durable reminder and scheduler state on
  every release — durable callbacks must survive deploys, so this is not
  acceptable.
- **Per-deployment `ClusterId` with stable `ServiceId`**: membership is scoped
  by `ClusterId`, but reminder data is scoped by `ServiceId`. Two one-silo
  clusters would each own the full ring over the same reminder table — the
  exact double-fire being fixed, made permanent instead of transient.
- **`Development` clustering in production**: membership lives in the primary
  silo's memory. In a single-replica rolling deploy the primary is the pod
  being replaced; membership cannot survive it.
- **`Recreate` deployment strategy alone**: removes the overlap window but
  takes a full availability gap on every deploy and leaves the architecture
  incorrect (any accidental second silo still split-brains). With shared
  membership, `RollingUpdate` becomes the correct zero-downtime path; the
  deploy strategy stays an ops choice, not a correctness requirement.

## Consequences

- Pods must reach each other on the silo port (11111) inside the namespace
  during the deploy overlap; verify no NetworkPolicy blocks pod-to-pod silo
  traffic before rollout.
- Graceful shutdown (SIGTERM → Orleans graceful stop) hands the ring to the
  surviving silo immediately; an ungraceful kill is recovered by membership
  probes and death votes within the liveness defaults.
- Garnet must serve the Orleans Redis providers' command surface (including
  Lua, already exercised by `RedisGrainStorage` etag writes in production;
  `docker-compose.mainnet-cluster.yml` runs Garnet with `--lua true`).
- Dead-silo entries accumulate in the membership hash and are cleaned up by
  Orleans defunct-silo cleanup; no per-deploy key churn is introduced.
