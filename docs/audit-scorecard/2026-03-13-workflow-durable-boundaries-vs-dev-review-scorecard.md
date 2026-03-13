# 2026-03-13 `fix/workflow-durable-boundaries-20260310` 相对 `dev` 审计评分卡

## 1. 审计范围

- 审计对象：当前分支 `fix/workflow-durable-boundaries-20260310` 的当前工作区，相对 `dev` 的增量。
- `merge-base`：`2dc2e3a341816391ba576848d3325630a147ef69`
- 提交级差异规模：`1074 files changed, 109172 insertions(+), 17285 deletions(-)`
- 当前工作区额外未提交重点变更：14 个 `Workflow/Application/Host API/Test` 文件。
- 审计方式：风险优先人工审查，不对 1000+ 文件做逐行穷举；重点下钻 `resume/signal` 控制命令、accepted-only detached dispatch、projection cleanup、durable completion 查询与 Host 错误语义。

## 2. 已执行验证

1. `bash tools/ci/architecture_guards.sh`
   - 结果：通过
2. `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --filter "DetachedCommandDispatchService|WorkflowRunControl" --nologo`
   - 结果：通过，`58/58`
3. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --filter "HandleResume|HandleSignal|ReleaseActorProjectionAsync_ShouldStopReceivingNewEvents" --nologo`
   - 结果：通过，`13/13`

结论：本次 worktree 里的局部修复已经被定向测试和架构门禁覆盖，但这些验证还没有覆盖“宿主重启后的 detached cleanup 恢复”以及“durable query 持续失败时的退出路径”。

## 3. 总评分

**总分：79 / 100，等级：B**

| 维度 | 分数 | 说明 |
|---|---:|---|
| 分层与依赖反转 | 16 / 20 | `Host -> Application -> Runtime/Projection Port` 的分层总体清晰，`resume/signal` 命令边界也比之前更收敛。 |
| CQRS / 统一 projection 主链 | 15 / 20 | 修复了“durable completion 之前提前 release projection”的错误语义。 |
| Durable boundary / cleanup ownership | 9 / 20 | detached cleanup 仍然由宿主进程内后台任务承接，不满足“accepted 之后仍可 durable 收尾”的要求。 |
| 读写分离与 ACK 语义 | 14 / 15 | `resume/signal` 的关键参数校验已下沉到 Application 层，Host 错误码映射也同步收敛。 |
| 可验证性 | 15 / 15 | 相关 guards 和定向测试都能跑通，修复点有测试锁定。 |
| 命名与文档同步 | 10 / 10 | 新增错误语义、README 说明与测试命名基本一致。 |

## 4. 主要 Findings

### [P1] detached cleanup 只靠宿主进程内 `Task.Run` 承接，宿主重启后会丢失 durable 收尾

证据：

1. [src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunDetachedDispatchService.cs](/Users/chronoai/Code/aevatar/src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunDetachedDispatchService.cs#L53) 在 accepted 后直接 fire-and-forget 一个 `Task.Run(...)`。
2. 这个后台 lambda 是当前 detached 路径里唯一会在 durable completion 后调用 [src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunCommandTarget.cs](/Users/chronoai/Code/aevatar/src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunCommandTarget.cs#L89) `ReleaseAsync(destroyCreatedActors: true)` 的地方。
3. DI 里只注册了一个普通 singleton 服务 [src/workflow/Aevatar.Workflow.Application/DependencyInjection/ServiceCollectionExtensions.cs](/Users/chronoai/Code/aevatar/src/workflow/Aevatar.Workflow.Application/DependencyInjection/ServiceCollectionExtensions.cs#L75)，而应用层/Host 层没有对应的 `IHostedService`、恢复扫描器或 actor-owned cleanup owner 来接管这条收尾链路。

影响：

- API 一旦先返回 accepted，然后宿主进程回收、重启或实例漂移，这个后台任务就直接丢失。
- 之后即使 run 已经 durable terminal，projection lease 与 `CreatedActorIds` 也没有新的权威执行者继续释放/销毁。
- 这和仓库里“跨请求/跨节点事实必须有 durable owner、不能依赖进程内偶然状态”的架构要求直接冲突。

建议：

- 把 detached cleanup 的 ownership 转成 actor-owned 或 durable state owned。
- 至少要把“等待 durable completion -> release projection -> destroy created actors”改成可恢复的 durable internal command / event / reaper，而不是宿主进程内匿名任务。

### [P2] durable query 的永久故障会被吞成 `Incomplete`，后台监控将无限轮询且永不 cleanup

证据：

1. [src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunDurableCompletionResolver.cs](/Users/chronoai/Code/aevatar/src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunDurableCompletionResolver.cs#L28) 对所有非取消异常统一 `catch`，然后返回 `Incomplete`。
2. [src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunDetachedDispatchService.cs](/Users/chronoai/Code/aevatar/src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunDetachedDispatchService.cs#L109) 对 non-terminal 结果会无限轮询；当前只有“单次 resolve 超时”日志，没有“持续不可观测时的终止或转移”。
3. 现有新增测试只覆盖 `timeout -> retry -> terminal`，没有覆盖 projection query 持续抛错或永久不可读的路径。

影响：

- 一旦 projection query port 配置错误、读模型长期不可读，或 resolver 持续抛出非取消异常，这条 detached monitor 会永久存活。
- 结果不是“诚实失败”，而是静默地无限重试，projection 和 created actors 也就一直不清理。
- 在高并发 accepted-only 路径上，这会累积为常驻后台任务和资源泄漏。

建议：

- 把“暂时未完成”和“无法再可靠观测 durable completion”拆成不同语义。
- 对永久故障增加上限、告警和 durable compensation handoff，而不是永远返回 `Incomplete`。

## 5. 正向变化

1. `resume/signal` 的关键参数校验已经下沉到 Application 命令解析层，不再只靠 Host 做输入守门。
2. Host 对 `InvalidStepId` / `InvalidSignalName` 的错误码映射已补齐，Application 与 HTTP 契约不再分叉。
3. detached 路径已经修复“在 durable terminal 之前提前 release projection”的错误行为，这一点比前一版明显更正确。

## 6. 审计结论

这批 worktree 修复解决了两个此前最明显的契约问题，所以 `resume/signal` 命令边界和 detached projection release 语义都比前一版更好；相关定向测试也通过了。

但从仓库自身的“durable boundary / actor-owned state / 不依赖进程内偶然状态”要求来看，当前 detached cleanup 仍然没有真正 durable owner。尤其 `Task.Run` 承接 accepted 之后的清理链路这一点，使得该分支还不能拿到 A 档评分，也不建议把“已具备 durable 收尾语义”作为当前状态对外宣称。要把这个分支评到可放心合并的水平，至少还需要补上 detached cleanup 的可恢复承接机制，或者把语义明确降级为 best-effort 并同步收窄文档表述。
