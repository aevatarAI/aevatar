# PR Review Scorecard: `fix/workflow-durable-boundaries-20260310` vs `dev`

## 1. 审计范围与本轮结论

- 审计对象：`fix/workflow-durable-boundaries-20260310` 相对 `dev` 的分支差异。
- 本文档覆盖 2026-03-10 的复审结果，替代此前同主题评分。
- 结论更新：上一轮列出的 3 项扣分项已全部闭环，当前分支可以进入合并阶段。

## 2. 本轮已闭环的问题

### 2.1 Durable compensation outbox 已回到 protobuf-only 契约

- `projection_dispatch_compensation_messages.proto` 的 `read_model_json` 已替换为 `google.protobuf.Any read_model`。
- `WorkflowProjectionDurableOutboxCompensator` 不再把 `WorkflowExecutionReport` 序列化成 JSON 字符串，而是通过显式 snapshot mapper 打包为 protobuf `Any`。
- `WorkflowProjectionDispatchCompensationOutboxGAgent` 重放时改为按 protobuf 契约解包，缺失或不兼容载荷只走 retry 分支，不再依赖 JSON shape。

### 2.2 Workflow session codec 已去掉 JSON round-trip

- `projection_session_event_transport.proto` 的 `payload` 已从字符串改成 `google.protobuf.Any`。
- `WorkflowRunEventSessionCodec` 已改为 protobuf envelope 编解码，不再把 `object?` 载荷往返成 `JsonElement`。
- 新增 `workflow_projection_transport.proto`、event envelope mapper、value codec、report snapshot mapper，把 replay 契约显式化并纳入类型系统。

### 2.3 交互式 cleanup failure 不再覆盖已完成结果

- `WorkflowRunInteractionService` 现在把执行结果和 cleanup 异常分离建模。
- 当业务执行已经成功或主异常已经确定时，`ReleaseAsync` 失败只记录日志，不再把请求整体改判为失败。
- 已补 regression test，覆盖“run 已成功但 cleanup 抛错”的场景。

## 3. 客观验证结果

| 命令 | 结果 | 备注 |
|---|---|---|
| `bash tools/ci/architecture_guards.sh` | Passed | 含 workflow binding、run-id、runtime callback 等守卫 |
| `bash tools/ci/projection_route_mapping_guard.sh` | Passed | reducer 路由静态门禁通过 |
| `bash tools/ci/test_stability_guards.sh` | Passed | 轮询等待门禁通过 |
| `bash tools/ci/solution_split_guards.sh` | Passed | Foundation/AI/CQRS/Workflow/Hosting/Distributed 分片构建通过 |
| `bash tools/ci/solution_split_test_guards.sh` | Passed | Foundation/AI/CQRS/Workflow/Hosting/Distributed 分片测试通过 |
| `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo --filter "ProjectionSessionEventHubTests|ProjectionOwnershipProtoCoverageTests"` | Passed | `8` passed |
| `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter WorkflowApplicationLayerTests` | Passed | `6` passed |
| `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter "WorkflowRunEventSessionCodecCoverageTests|WorkflowProjectionDispatchCompensationOutboxGAgentTests|ActorProjectionDispatchCompensationOutboxCoverageTests"` | Passed | `28` passed |
| `dotnet build aevatar.slnx --nologo` | Not run | 本轮以官方 split guard + targeted tests 作为验证主证据 |
| `dotnet test aevatar.slnx --nologo` | Not run | 同上 |

补充说明：

- 所有已执行命令仅出现既有 `NU1507` 多包源 warning，未出现新增编译或测试失败。
- `solution_split_test_guards.sh` 中分布式/外部依赖测试仍有预期 `SKIP`，未见新增失败。
- 为完成验证，顺手修复了 `WorkflowExecutionKernel.cs` 中一个已存在的局部变量遮蔽编译错误；该修复不属于本轮扣分项，但否则无法完成测试闭环。

## 4. 整体评分

**总分：96 / 100（A）**

| 维度 | 权重 | 得分 | 结论 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 19 | Workflow/CQRS 边界保持清晰，新增 transport mapper 仍留在正确层级 |
| CQRS 与统一投影链路 | 20 | 19 | session/outbox 已统一回到 protobuf 主链路，主干一致性明显提升 |
| Projection 编排与状态约束 | 20 | 19 | durable state 不再泄露 JSON，actor-owned replay 语义更稳定 |
| 读写分离与会话语义 | 15 | 15 | 成功结果与 cleanup failure 已分离，交互语义诚实 |
| 命名语义与冗余清理 | 10 | 9 | 命名总体一致，transport/snapshot 概念表达明确 |
| 可验证性 | 15 | 15 | 官方 guard、split build/test、补充回归测试均已闭环 |

## 5. 当前非阻断观察项

1. `WorkflowExecutionReportSnapshotMapper` 和 `WorkflowRunSessionEventEnvelopeMapper` 属于显式映射代码，后续字段演进时要继续靠测试守住契约。
2. 本轮未额外执行全量 `dotnet build/test aevatar.slnx --nologo`，但官方 split build/test 与针对性回归已经覆盖到本次改动面。

## 6. 最终结论

这条分支相对 `dev` 的主方向依旧是正向的，而上一轮阻断合并的三项问题已经全部收口：内部 durable/session 载荷重新统一为 protobuf，replay 不再发生 `JsonElement` 漂移，交互式 run 也不再被 cleanup failure 反向打失败。

当前结论：**建议合并。**
