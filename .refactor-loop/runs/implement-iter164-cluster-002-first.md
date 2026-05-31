## 🤖 implement done

### TL;DR
- 这是什么: #1226 first slice 实施完成,三类 LLM workflow module 不再把 `TextMessageEndEvent` / `ChatResponseEvent` 当完成信号。
- 现在到哪一步 / 结论是什么: 完成信号已切到既有 `WorkflowRoleReplyRecordedEvent`,并补齐 committed role reply 到 workflow continuation 的测试。
- 需要 maintainer 做什么 OR controller 下一步: controller 可进入 review/landing 流程;本地已 `git add -A`,未 commit、未 push。

---

### 详细说明

本次改动把 `LLMCallModule`、`EvaluateModule`、`ReflectModule` 的 completion 输入从 presentation stream frame 收敛为 `WorkflowRoleReplyRecordedEvent`。`CanHandle` 已移除 `TextMessageEndEvent` / `ChatResponseEvent` descriptor 分支,handler 只按 `SessionId` 对账 actor-owned committed reply fact 后发布 `StepCompletedEvent`。

为保证真实链路可推进,`WorkflowRunGAgent` 在持久化 `WorkflowRoleReplyRecordedEvent` 后 self-publish 该事实,让 workflow module 在 actor turn 内继续消费;runtime relay 增加 child -> parent 的 `CommittedStateEventPublished` observation binding,只转发 committed observation,不引入 query/reply 或 presentation-frame completion 回退。

### 修改范围

- `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs`: 删除 presentation frame completion handler,新增 `WorkflowRoleReplyRecordedEvent` completion handler。
- `src/workflow/Aevatar.Workflow.Core/Modules/EvaluateModule.cs`: completion 输入改为 `WorkflowRoleReplyRecordedEvent`,score 从 committed reply content 解析。
- `src/workflow/Aevatar.Workflow.Core/Modules/ReflectModule.cs`: critique/improve completion 输入改为 `WorkflowRoleReplyRecordedEvent`。
- `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs`: committed role reply fact 持久化后 self-publish,用于 workflow continuation。
- `src/Aevatar.Foundation.Abstractions/Streaming/StreamForwardingRules.cs`: 新增 committed observation relay binding helper。
- `src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorRuntime.cs`: link/unlink/destroy 时维护 child -> parent committed observation relay。
- `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs`: Orleans runtime 同步维护 child -> parent committed observation relay。
- `test/Aevatar.Integration.Tests/WorkflowCoreModulesCoverageTests.cs`: LLM role reply 正向完成、presentation frame 负向不完成。
- `test/Aevatar.Integration.Tests/WorkflowAdditionalModulesCoverageTests.cs`: evaluate/reflect role reply 正向路径及 presentation frame 负向路径。
- `test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs`: committed role reply fact 可继续推进 LLM step completion。
- `test/Aevatar.Workflow.Core.Tests/Modules/RuntimeCallbackEventizationTests.cs`: runtime callback 路径改用 committed workflow role reply fact。

### Verification

- `dotnet build aevatar.slnx --nologo` — passed,46 warnings,0 errors。
- `dotnet test aevatar.slnx --nologo --no-build` — passed,0 failed,0 skipped。
- `bash tools/ci/architecture_guards.sh` — passed。
- `bash tools/ci/test_stability_guards.sh` — passed。
- `git diff --check` — passed。

### SCOPE_EXTEND Records

- `SCOPE_EXTEND: src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs` committed role reply must self-publish so modules can consume actor-owned completion fact.
- `SCOPE_EXTEND: src/Aevatar.Foundation.Abstractions/Streaming/StreamForwardingRules.cs` committed role reply observation needs typed relay rule rather than presentation-frame fallback.
- `SCOPE_EXTEND: src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorRuntime.cs` local runtime link/unlink must maintain committed observation relay for child role facts.
- `SCOPE_EXTEND: src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs` Orleans runtime link/unlink must maintain the same committed observation relay semantics.
- `SCOPE_EXTEND: test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs` integration coverage required for committed role reply continuation through `WorkflowRunGAgent`.
- `SCOPE_EXTEND: test/Aevatar.Workflow.Core.Tests/Modules/RuntimeCallbackEventizationTests.cs` callback eventization test must use the new committed completion signal.
- `SCOPE_EXTEND: .refactor-loop/runs/implement-iter164-cluster-002-first.md` record implementation and verification required by task.

---

<details>
<summary>📎 完整 codex 原始输出(存档备查)</summary>

First slice implemented for #1226.

Old pattern removed:
- `LLMCallModule` / `EvaluateModule` / `ReflectModule` no longer handle `TextMessageEndEvent` or `ChatResponseEvent` as completion input.
- Negative tests assert presentation frames do not complete pending steps.

New principle applied:
- `WorkflowRoleReplyRecordedEvent` is the only module completion signal for role LLM replies.
- Workflow-owned committed role reply facts are persisted and then self-published for actor-local continuation.
- Runtime hierarchy relay only forwards committed observation facts from role child to workflow parent.

Verification completed:
- Full solution build passed.
- Full solution test passed with `--no-build`.
- Architecture guards passed.
- Test stability guards passed.

No commit, push, branch, checkout, PR create, or PR merge command was run.

</details>

⟦AI:AUTO-LOOP⟧
