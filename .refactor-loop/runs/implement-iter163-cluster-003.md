# implement-iter163-cluster-003

## Summary

cluster-003-workflow-suspension-legacy-metadata 已实施。

Old pattern: `WorkflowSuspendedEvent` 已有 typed `VariableName` / `Secure` / `RedactedOutput` 字段，但 AGUI 与 projection 仍从 `Metadata` 的 reserved legacy keys 读取 fallback。

New pattern: AGUI 与 projection 只消费 typed suspension fields；`Metadata` 仅作为开放扩展数据，`variable` / `secure` / `input_mode` / `redacted_output` reserved legacy keys 被过滤且不参与控制语义。

## Changes

- `WorkflowHumanInteractionProjector` 删除 `Metadata` fallback，只从 typed suspension fields 构造 human interaction annotations。
- `EventEnvelopeToWorkflowRunEventMapper` 同步删除 AGUI custom payload 的 `Metadata` fallback，并收窄共享 helper 为 typed string normalization + reserved-key filter。
- `WorkflowExecutionArtifactMaterializationSupport` 删除 workflow suspended timeline metadata fallback，只从 typed fields 写入 reserved semantic entries。
- `SecureInputModule` 更新 self-doc，明确生产 typed suspension fields，`Metadata` 不再承载 reserved secure input 语义。
- Host API 相关测试改为 negative coverage：legacy reserved keys 存在时被忽略，只保留 open extension metadata。

## Verification

- `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowHumanInteractionProjectorTests|FullyQualifiedName~WorkflowExecutionProjectionProjectorTests|FullyQualifiedName~EventEnvelopeToAGUIEventMapperTests"`：通过，45 passed。
- `dotnet build aevatar.slnx --nologo`：通过，0 errors，既有 warnings。
- `dotnet test aevatar.slnx --nologo --no-build`：通过，0 failures；存在仓库既有条件 skip，未新增 skip。
- `bash tools/ci/architecture_guards.sh`：通过。
- `bash tools/ci/test_stability_guards.sh`：通过。

⟦AI:AUTO-LOOP⟧
