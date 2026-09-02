# Aevatar, NyxID, Ornn, And Lark Interaction Notes

> Date: 2026-06-15.
> Status: Historical snapshot with current retirement note.

This file used to describe two live end-to-end paths: direct Lark chat and a legacy scheduled runner path. The scheduled runner path has since been retired. Current scheduled workflow/team automation must use `ScheduledDispatchGAgent` plus workflow/team service invocation, not `SkillRunnerGAgent`.

## Current Direct Chat Path

Direct chat still follows the NyxID relay and NyxidChat path:

1. Lark sends the user message to NyxID.
2. NyxID normalizes and forwards the relay callback to aevatar.
3. aevatar dispatches the inbound activity into the conversation actor path.
4. NyxidChat creates a per-turn run and streams LLM/tool output through the same reply chain.
5. Generic skill invocation stays on `UseSkillTool`, `SkillsAgentToolSource`, `IRemoteSkillFetcher`, `ChatRuntime`, and the normal AI/tool-provider execution pipeline.

Slash-skill recovery is not part of the retired scheduled runner model. For Lark and web, unknown slash commands from a bound sender can fall through to LLM reply generation with an `AgentSkillRecoveryContext`; the subsequent run may plan `use_skill(<slash-command-name>, args)`.

## Current Scheduled Workflow Path

Scheduled workflow/team automation uses the unified scheduled dispatch path:

1. Scheduled workflow creation records a catalog entry and creates a schedule through scheduled dispatch.
2. `ScheduledDispatchGAgent` owns schedule facts and ticks.
3. One-shot and recurring schedules invoke workflow/team service contracts.
4. Run, disable, enable, and delete management actions use scheduled dispatch lifecycle contracts.
5. Queries read current-state readmodels; they do not replay events or prime projection from the query path.

External webhook admission for workflow/team automation belongs to workflow/team-owned ingress, replay, and dispatch contracts. It must not be routed through legacy runner delivery state.

## Retired Scheduled Runner Model

The following names refer only to historical code paths and old persisted state:

- `SkillRunnerGAgent`
- `ISkillRunnerCommandPort`
- `ISkillRunnerCronSchedulePort`
- `ISkillRunnerExecutionQueryPort`
- `InitializeSkillRunnerCommand`
- `TriggerSkillRunnerExecutionCommand`
- `AdmitSkillRunnerExternalTriggerCommand`
- `ScheduledDispatchScheduleKind.SkillRunner`

Do not use those names as a current implementation guide. Historical actor kind/type tokens such as `channel-runtime.skill-runner` and `skill_runner` may remain in retired-actor cleanup tests and cleanup specifications so old state can be removed safely.
