# PR Review 修复复评打分（Workflow 并发 Run 关联一致性）- 2026-02-26

## 1. 审计范围与输入

1. 审计对象：
   - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`
   - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs`
   - `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto`
   - `src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Modules/MakerRecursiveModule.cs`
   - `src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Modules/MakerVoteModule.cs`
2. 审计类型：对上一版 PR review 打分文件的修复复评（关注 F1/F2 问题关闭情况）。
3. 评分口径：`docs/audit-scorecard/README.md`（100 分制，6 维度）。

## 2. 客观验证结果

| 检查项 | 命令 | 结果 |
|---|---|---|
| Maker 扩展测试 | `dotnet test test/Aevatar.Workflow.Extensions.Maker.Tests/Aevatar.Workflow.Extensions.Maker.Tests.csproj --nologo` | 通过（14/14） |
| Workflow 定向集成测试 | `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowLoopModuleCoverageTests\|FullyQualifiedName~WorkflowAdditionalModulesCoverageTests" --nologo` | 通过（44/44） |
| 全量构建 | `dotnet build aevatar.slnx --nologo` | 通过（0 warning, 0 error） |
| 测试稳定性门禁 | `bash tools/ci/test_stability_guards.sh` | 通过 |
| 架构门禁 | `bash tools/ci/architecture_guards.sh` | 通过 |

## 3. 复评结论（摘要）

1. 上一版审计中的两条问题（F1/F2）均已关闭，运行态 run 关联链路已收敛为 run-scoped 语义。
2. 当前版本已满足合并前阻断条件，结论调整为：**建议合并**。

## 4. 总体评分（100 分制）

**总分：97 / 100（A+）**

| 维度 | 权重 | 得分 | 说明 |
|---|---:|---:|---|
| 分层与依赖反转 | 20 | 20 | 修复仅在 Workflow Core/Extensions 内收敛契约，未引入跨层反向依赖。 |
| CQRS 与统一投影链路 | 20 | 19 | completion/run 关联已统一；保留 1 分审慎扣分用于后续全链路回归持续观察。 |
| Projection 编排与状态约束 | 20 | 20 | 并发 run 下中间状态改为 `(runId, stepId)` 关联，避免串扰。 |
| 读写分离与会话语义 | 15 | 15 | `wait_signal` 挂起通知可携带 `run_id`，恢复链路可定向对账。 |
| 命名语义与冗余清理 | 10 | 10 | 命名与语义一致，未引入额外壳层。 |
| 可验证性（门禁/构建/测试） | 15 | 13 | 已完成定向与门禁验证；本次复评未执行 `dotnet test aevatar.slnx` 全量测试。 |

## 5. 问题关闭状态与证据

### F1（原 P1）并发 run 下 completion 无 run_id 造成丢路由/卡死

- 关闭状态：**Closed**
- 关闭证据：
  1. `WorkflowLoopModule` 明确拒绝无 `run_id` completion：  
     `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:121`
  2. `WorkflowLoopModule` run 解析改为强制规范化，不再单活跃 run 回退：  
     `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs:477`
  3. Maker completion 事件已补齐 `RunId`：
     - `src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Modules/MakerVoteModule.cs:96`
     - `src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Modules/MakerRecursiveModule.cs:235`
     - `src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Modules/MakerRecursiveModule.cs:260`
  4. Maker 中间态改为 run-scoped 键，避免跨 run 同 stepId 串扰：  
     `src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Modules/MakerRecursiveModule.cs:17`  
     `src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/Modules/MakerRecursiveModule.cs:333`
  5. 回归测试覆盖“缺失 run_id completion 必须忽略”与 run-scoped 隔离：
     - `test/Aevatar.Integration.Tests/WorkflowLoopModuleCoverageTests.cs:268`
     - `test/Aevatar.Workflow.Extensions.Maker.Tests/MakerRecursiveModuleCoverageTests.cs:77`

### F2（原 P2）`WaitingForSignalEvent` 不含 run_id，无法定向恢复

- 关闭状态：**Closed**
- 关闭证据：
  1. 协议新增 `WaitingForSignalEvent.run_id`：  
     `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto:13`
  2. `WaitSignalModule` 发布等待通知时已写入 `RunId`：  
     `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs:60`
  3. 回归测试已断言等待通知携带 run_id：  
     `test/Aevatar.Integration.Tests/WorkflowAdditionalModulesCoverageTests.cs:177`

## 6. 复评后合并门禁结论

1. F1 关闭：并发 run 场景 completion 关联已强制 run-scoped。
2. F2 关闭：挂起通知与恢复链路已具备 run_id 对账能力。
3. 定向回归 + 架构门禁 + 稳定性门禁通过。

当前结论：**通过复评，建议合并。**
