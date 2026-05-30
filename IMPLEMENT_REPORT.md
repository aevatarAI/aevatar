# IMPLEMENT_REPORT

## Scope

Phase 9 #1396 first-slice v1 release gate / regression guards only.

## Changes

- Added Lark outbound dispatcher regression coverage for new-message POST, `message_id` parsing, and `230002` fallback ownership.
- Added source regression guard ensuring Lark new-message POST stays behind `LarkOutboundDispatcher`.
- Added workflow trusted-control regression coverage for typed `ScopeId`, `ToolContext`, and `LlmControl` fields, with user-supplied trusted keys kept out of `Metadata`.
- Added workflow envelope typed-control regression coverage.
- Added workflow completion-source regression guard requiring LLM/Evaluate/Reflect modules to use `WorkflowRoleReplyRecordedEvent` rather than presentation frames.
- Added retired-token guards for `NyxRelayAgentBuilderFlow` in tests and `tools/ci/architecture_guards.sh`.
- Removed retired flow name from comments so the strict grep guard can enforce deletion.

## Verification

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --filter "FullyQualifiedName~LarkOutboundDispatcherTests|FullyQualifiedName~ChannelRuntimeSourceRegressionTests|FullyQualifiedName~AgentBuilderCardFlowTests"
# Passed: 43, Failed: 0

dotnet test test/Aevatar.AI.ToolProviders.AevatarInvocation.Tests/Aevatar.AI.ToolProviders.AevatarInvocation.Tests.csproj --nologo
# Passed: 36, Failed: 0

dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowApplicationRegistrationAndExecutionTests"
# Passed: 18, Failed: 0

dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowModuleCompletionSourceRegressionTests"
# Passed: 2, Failed: 0

bash tools/ci/test_stability_guards.sh
# Passed

bash tools/ci/architecture_guards.sh
# Passed

dotnet build aevatar.slnx --nologo 2>&1 | tail -3
#     0 个错误
# 已用时间 00:00:22.74

dotnet test aevatar.slnx --nologo --no-build 2>&1 | tail -30
# Tail showed all listed assemblies passed; final visible assembly: Aevatar.GAgentService.Integration.Tests, Passed: 303, Failed: 0.
```

## Marker

IMPLEMENT_DONE:issue1447-first:ok
⟦AI:AUTO-LOOP⟧
