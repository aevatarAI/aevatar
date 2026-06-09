---
title: Workflow Run Resume From Step Design
status: accepted
owner: workflow
---

# Workflow Run Resume From Step Design

## Decision

Forking a workflow run is a command surface, not a private application service. HTTP adapters and automatic coordinators construct a typed `WorkflowForkRunCommand` and dispatch it through `ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>`.

The source run seed is read through `IWorkflowRunForkSeedQueryPort`, which is a read-model query contract. The fork path does not read actor state, replay the event store, or attach seed data to `WorkflowDefinitionBinding`.

## Seed Path

The only authoritative seed ingress for a new run is request-level:

```text
WorkflowForkRunCommand
  -> WorkflowChatRunRequest.ForkSeed
  -> WorkflowChatRequestEvent.fork_seed
  -> StartWorkflowEvent.fork_seed
  -> WorkflowExecutionKernel
```

Run binding remains definition/run binding only: definition actor id, workflow name, workflow YAML, inline workflow YAMLs, run id, and scope id. It must not carry fork seed variables.

## Runtime Semantics

`WorkflowExecutionKernel` applies `StartWorkflowEvent.fork_seed.variables` before normal start parameters, starts from `fork_seed.start_at_step_id` when present, and publishes a failed `WorkflowCompletedEvent` if that step is missing. This keeps topology validation inside the workflow core, where step identity is owned.

## Command Surface

`WorkflowForkRunCommand` carries source run id, start step id, optional inline YAML, variable overrides, optional input, command identity seed, correlation identity seed, typed scope id, and typed caller credential. The fork dispatch target resolver creates a run from the selected workflow definition and prepares a seeded `WorkflowChatRunRequest`; the command dispatch pipeline owns command context, envelope creation, inbox admission, receipt mapping, and cleanup on dispatch failure.

Caller credential handling is intentionally narrow. Fork dispatch can use the current typed caller credential from the request or continuation command, but it must not recover historical bearer tokens from public read models, metadata bags, query-time replay, or event-store side reads.

## Verification

Coverage must assert that the HTTP fork endpoint calls the typed command dispatch service, the chat request envelope carries `fork_seed`, the run actor forwards it onto `StartWorkflowEvent`, and core execution forks at the requested step.
