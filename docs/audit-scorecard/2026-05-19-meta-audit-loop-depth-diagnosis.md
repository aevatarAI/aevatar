---
title: Meta-audit of codex-refactor-loop depth (14-iter retrospective)
status: active
owner: codex-refactor-loop
issue: 678
---

# Meta Audit Depth Diagnosis

## 1. 真实诊断：为什么 audit 会浅出

1. **Phase 1 没有审计覆盖率契约，只要求启动一个 codex 并消费输出。**  
   skill 的 Phase 1 只规定复制 prompt、替换 iteration、用 1800s timeout 跑一个 codex（`.claude/skills/codex-refactor-loop/SKILL.md:70-82`），完成后 controller 只读取输出并填充 cluster（`:89-94`）。没有要求 audit 输出“打开了哪些文件、跑了哪些 analyzer、每条 CLAUDE 规则覆盖了多少命中”。因此 `total_clusters: 0` 只表示该 codex 没列 cluster，不表示规则空间已覆盖。

2. **初始模板鼓励从历史审计和少量 grep 抽样起步，而不是全仓规则扫描。**  
   模板要求先读 `docs/audit-scorecard/`，再对每个候选用 `rg`/`grep` “抽样 2~3 个真实命中”（`.claude/skills/codex-refactor-loop/prompts/audit.md:35-40`）。iter1 prompt 也写“优先复用历史审计”与“抽样验证 2~3 个真实命中”（`.refactor-loop/prompts/audit-iter-1.md:35-40`）。这会把 audit 变成候选确认器，而不是违反发现器。

3. **目标是输出少量可并行 cluster，不是枚举所有违规。**  
   iter1 要求识别 `3~6` 个独立 cluster，且单 cluster ≤30 文件（`.refactor-loop/prompts/audit-iter-1.md:13-20`）；iter2 变成 `0-3` 且“宁可输出 0-1 个高价值”（`.refactor-loop/prompts/audit-iter-2.md:11`, `:42`）；iter8 变成 `1-2` 且“少而精”（`.refactor-loop/prompts/audit-iter-8.md:31-38`）；iter14 只接受 ≤5 文件（`.refactor-loop/prompts/audit-iter-14.md:9-15`）。这会系统性排除跨模块、需要先设计再修的深层违规。

4. **后期 prompt 明确锚定 `0`。**  
   iter8 写“优先选择 `total_clusters: 0`”（`.refactor-loop/prompts/audit-iter-8.md:38`）；iter9 写“强烈优先 `total_clusters: 0`”（`.refactor-loop/prompts/audit-iter-9.md:46`）；iter10 写“优先 0”（`.refactor-loop/prompts/audit-iter-10.md:53`）；iter11 写“强烈优先 0”（`.refactor-loop/prompts/audit-iter-11.md:50`）；iter13 写“优先输出 0”（`.refactor-loop/prompts/audit-iter-13.md:24-27`）；iter14 写“强烈优先 0”（`.refactor-loop/prompts/audit-iter-14.md:25-28`）。这不是中性审计提示，会把边界案例推向 reject。

5. **“不得重复已修模式”把尾部违规伪装成不值得做。**  
   iter3 明确发现 StreamingProxy 仍有 `actor.HandleEventAsync` 直调，以及 `UserAgentCatalogCommandPort` 仍有 command-path polling，但因“同模式尾部 / leverage low”输出 0（`.refactor-loop/runs/audit-iter-3.md:10-16`, `:24`）。iter4 随后承认这些就是 cluster，并且“No fresh codex audit needed”（`.refactor-loop/runs/audit-iter-4.md:6-10`）。这证明 iter3 的 0 不是“无违规”，只是被门槛过滤掉。

6. **iter4 没有 materialized audit prompt，说明流程允许 controller 手工补 cluster。**  
   `runs/audit-iter-4.md` 存在并包含 2 个 cluster（`.refactor-loop/runs/audit-iter-4.md:1-5`），但 `.refactor-loop/prompts/audit-iter-4.md` 不存在。iter4 输出也说明它只是 controller-level formalization（`:6-10`）。这破坏了“每轮 audit 都由同一审计 prompt 产生”的可追溯性。

7. **后期 audit 逐步转向非核心维度，核心 CLAUDE 规则没有被重新系统扫。**  
   iter10 “deliberately avoided architectural / dispatch / projection / docs-drift families”（`.refactor-loop/runs/audit-iter-10.md:9-12`）；iter11 只扫 performance/complexity/fanout/DI（`.refactor-loop/runs/audit-iter-11.md:6-11`）；iter12 是 security-only（`.refactor-loop/runs/audit-iter-12.md:7-10`）；iter13 只扫 build warning/test factory/logging/DI（`.refactor-loop/runs/audit-iter-13.md:3-16`）；iter14 只接 must-fix bug 或 critical security（`.refactor-loop/runs/audit-iter-14.md:10-16`）。所以 iter9/14 的 0 不能说明架构违规已穷尽。

8. **audit 把 guard 通过当成语义覆盖，但 guard 只覆盖窄字符串模式。**  
   iter8 把 `architecture_guards.sh` 通过和 cluster-016 guard active 作为验证（`.refactor-loop/runs/audit-iter-8.md:11-16`）；iter9 同样列出 guard passed（`.refactor-loop/runs/audit-iter-9.md:14-21`），并据此 reject cluster-016 family tails（`:38-41`）。但 CLAUDE/AGENTS 的很多约束是语义性的：回调是否直接推进运行态、JSON 是否成为内部事实存储、session 状态是否 actor 化。这些不是简单 `actor.HandleEventAsync` 或 `localhost:5000` guard 能证明的。

9. **audit 对命中解释过宽，容易把真实残留归为“allowed boundary/runtime/test”。**  
   iter9 的 JSON scan 结论是 JSON 集中在 HTTP/CLI/provider adapters、projection provider payloads、tool arguments、demo output、user config/local secrets，因此无 protobuf violation（`.refactor-loop/runs/audit-iter-9.md:50-56`）。但当前 `Studio.Infrastructure.Storage` 仍用 JSON 读写 role/connector catalog 与 draft，这不是外部协议临时转换。iter8/9 对 `ChatAsync` 也以“不是当前实时入口”拒绝（`.refactor-loop/runs/audit-iter-8.md:58`, `.refactor-loop/runs/audit-iter-9.md:74-79`），但核心非流式 `ChatRuntime.ChatAsync` 和 Studio generator 非流式表面仍存在。

## 2. 反证：仍然存在的 CLAUDE.md 违反

### 2.1 VoicePresence remote session bridge 在 Host/Resolver 层用 lock + 进程内 state 管理会话

- **违反条款**: CLAUDE 要求投影/会话/订阅等运行态由 Actor 或分布式状态承载，禁止中间层进程内注册表/字典持事实状态（`CLAUDE.md:6`）；Actor/模块运行态只能在事件处理主线程修改，禁止用 `lock` 作为并发补丁（`CLAUDE.md:105`）；中间层不得维护 session 上下文/事实状态的进程内集合或状态（`CLAUDE.md:115-120`）。
- **证据**:
  - `src/Aevatar.Foundation.VoicePresence/Hosting/RemoteActorVoicePresenceSessionResolver.cs:90-99` 定义 host-side `RemoteActorVoicePresenceSessionBridge`，包含 `_gate = new()` 与 `_state`.
  - `src/Aevatar.Foundation.VoicePresence/Hosting/RemoteActorVoicePresenceSessionResolver.cs:127-140` 为每次 attach 生成 `sessionId`、创建 subscription/transport state，并在 `lock (_gate)` 内写入 `_state`.
  - `src/Aevatar.Foundation.VoicePresence/Hosting/RemoteActorVoicePresenceSessionResolver.cs:145-149` 在写入本地 state 后启动 relay 并 dispatch open request。
- **为什么前面 audit 应该捕捉但漏了**: iter14 专门扫 race/data race，并提到 “voice providers” 与 background loops，但以“未证明 concrete data race”拒绝（`.refactor-loop/runs/audit-iter-14.md:25-39`）。这漏掉了架构规则本身：不是必须先证明 corrupted state 才违规；中间层把 session attachment/subscription 作为 `_state` 并用 lock 维护，已经不符合 actor 化 session 运行态。
- **估计修复 scope**: 4-7 文件。需要把 remote voice session attachment 建模为 actor-owned lease/session event，host 只持显式 disposable lease 句柄；更新 `RemoteActorVoicePresenceSessionResolver`、`VoicePresenceSession`、相关 tests 和可能的 docs。

### 2.2 OpenAIRealtimeProvider 后台接收循环直接维护响应 epoch 字典

- **违反条款**: CLAUDE 要求模块运行态只能在事件处理主线程修改（`CLAUDE.md:105`），回调/后台循环只发信号，不直接读写运行态或推进业务（`CLAUDE.md:106`）；AGENTS 同样要求 Actor/模块运行态只在事件处理主线程修改（`AGENTS.md:94-95`）。
- **证据**:
  - `src/Aevatar.Foundation.VoicePresence.OpenAI/OpenAIRealtimeProvider.cs:25` 定义 `_responseEpochs` 字典。
  - `src/Aevatar.Foundation.VoicePresence.OpenAI/OpenAIRealtimeProvider.cs:162-174` 的 `RunReceiveLoopAsync` 后台循环从 provider session 收事件并调用 `MapSessionEvent`.
  - `src/Aevatar.Foundation.VoicePresence.OpenAI/OpenAIRealtimeProvider.cs:227-231` 在 mapping response-created event 时调用 `ResolveResponseEpoch`.
  - `src/Aevatar.Foundation.VoicePresence.OpenAI/OpenAIRealtimeProvider.cs:390-407` 读写 `_responseEpochs` 并递增 `_nextResponseId`.
- **为什么前面 audit 应该捕捉但漏了**: iter10 扫 `Task.Run`/timers/background loops，但只接受 HTTP client lifetime，其他 background loops 被归为 logged/cancellation patterns（`.refactor-loop/runs/audit-iter-10.md:78-83`）。iter14 又把 voice providers 的 background loops 拒绝为未证明 race（`.refactor-loop/runs/audit-iter-14.md:34-37`）。这两次都把“有没有已复现数据竞争”当门槛，漏掉了 CLAUDE 对模块运行态线程归属的硬约束。
- **估计修复 scope**: 3-5 文件。把 provider event mapping 变成无状态映射，epoch 分配通过 actor/module event handler 内的状态推进，或把 epoch 映射封装为 actor-owned/protobuf state；更新 OpenAI realtime provider tests。

### 2.3 AI Core 仍保留非流式 ChatAsync 主链并调用 provider.ChatAsync

- **违反条款**: AGENTS 明确要求 AI 对话主链必须使用 `ChatStreamAsync` 作为唯一权威执行入口，禁止 `ChatAsync` 作为正式主链，且不得用于 CLI/AGUI/Scope/NyxID/Workflow Chat 等实时入口（`AGENTS.md:97`）。CLAUDE 中对应原则是对话主链流式化和运行态统一发布（`CLAUDE.md:104-112`）。
- **证据**:
  - `src/Aevatar.AI.Core/Chat/ChatRuntime.cs:73-92` 暴露 `ChatRuntime.ChatAsync` overloads。
  - `src/Aevatar.AI.Core/Chat/ChatRuntime.cs:105-117` 非流式路径构造 history、provider，并调用 `_toolLoop.ExecuteAsync`.
  - `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs:289-302` `InvokeLlmAsync` 把 `IsStreaming = false` 并调用 `provider.ChatAsync`.
  - Studio generator 仍保留非流式表面：`src/Aevatar.Studio.Hosting/Endpoints/ScriptGenerateGAgent.cs:34-39` 与 `src/Aevatar.Studio.Hosting/Endpoints/WorkflowGenerateGAgent.cs:34-39` 直接委托 `ChatAsync`.
- **为什么前面 audit 应该捕捉但漏了**: iter3 专门扫描 `ChatAsync` 实时路径并把剩余调用解释为 provider/low-level/classifier 等（`.refactor-loop/runs/audit-iter-3.md:19`）；iter8/9 再次 reject，理由是当前 Studio endpoints 调 `GenerateWithReasoningAsync`，`GenerateAsync` helpers 不是 current realtime entrypoint（`.refactor-loop/runs/audit-iter-8.md:58`, `.refactor-loop/runs/audit-iter-9.md:74-79`）。但 AGENTS 的规则不是“当前 endpoint 是否调用”，而是“ChatStreamAsync 是唯一权威执行入口”；保留核心非流式主链和 user-facing generator 非流式表面会继续给新入口复用。
- **估计修复 scope**: 6-10 文件。删除或降级 `ChatRuntime.ChatAsync` 到明确 offline-only adapter；让 `ToolCallLoop` 非流式执行复用 stream 并聚合；删除 Studio generator `GenerateAsync` 死表面或改为 stream 聚合；更新 tests。

### 2.4 Studio 内部 catalog/draft 事实存储仍使用 JSON serializer

- **违反条款**: AGENTS 要求所有状态、领域事件、命令、回调载荷、快照、缓存载荷、跨 Actor/跨节点内部传输对象统一 Protobuf（`AGENTS.md:114`），禁止在模块持久态或其他事实存储中使用 JSON/XML/自定义字符串格式（`AGENTS.md:115`）；外部 JSON 只能在 Host/Adapter 边界临时转换，进入应用/领域/运行时后恢复 Protobuf（`AGENTS.md:116`）。
- **证据**:
  - `src/Aevatar.Studio.Infrastructure/Storage/ConnectorCatalogJsonSerializer.cs:7-12` 定义内部 `ConnectorCatalogJsonSerializer`.
  - `src/Aevatar.Studio.Infrastructure/Storage/ConnectorCatalogJsonSerializer.cs:14-35` 用 `JsonDocument.ParseAsync` 与 `JsonSerializer.SerializeAsync` 读写 connector catalog。
  - `src/Aevatar.Studio.Infrastructure/Storage/ConnectorCatalogJsonSerializer.cs:37-68` 同样读写 connector draft。
  - `src/Aevatar.Studio.Infrastructure/Storage/RoleCatalogJsonSerializer.cs:7-12` 定义 `RoleCatalogJsonSerializer`，`:14-35` 读写 role catalog，`:37-68` 读写 role draft。
- **为什么前面 audit 应该捕捉但漏了**: iter9 的 JSON scan 结论把 JSON usage 概括为 adapter/boundary/user config/local secrets，并认为无 protobuf violation（`.refactor-loop/runs/audit-iter-9.md:50-56`）。Studio catalog/draft 是仓库内部的 role/connector 定义事实，不是第三方协议临时转换；它在 Infrastructure storage 中长期落盘，正是应该被 JSON/protobuf 规则捕捉的类别。
- **估计修复 scope**: 5-9 文件。新增 proto 契约与 generated 类型，替换 role/connector catalog serializer；保留 import/export 边界 JSON/YAML 时在 adapter 层转换；更新 storage/import tests。

## 3. 改进方案：audit prompt + Phase 1 流程

1. **增加 mandatory coverage manifest，0 cluster 时必须通过。**  
   Phase 1 prompt 要求 audit 输出 `coverage_manifest`，至少包含：
   - 每个 CLAUDE/AGENTS 强制章节一个 `rule_id`；
   - 每个 `rule_id` 至少 1 个 executed command；
   - 每个 `rule_id` 至少 3 个打开过的非测试生产文件，或写明 `candidate_count=0` 的 grep 命令；
   - `total_opened_files >= 60`，其中 `src/ >= 30`、`agents/ >= 15`、`workflow/ >= 10`、`tools/ci >= 3`；
   - `0` 输出没有 manifest 时 controller 不接受，重新派发 audit。

2. **把“候选发现”和“cluster 选择”拆成两个文件。**  
   audit codex 必须先写 `.refactor-loop/runs/audit-iter-N-candidates.ndjson`，每行一个可 grep 候选，字段包含 `rule_id/path/line/evidence/reject_or_accept/reject_reason/prior_cluster_overlap`. 要求：
   - `candidate_count >= 25`，除非至少 8 个 analyzer 命令全部 0 命中；
   - 即使最终 `total_clusters: 0`，也必须列出 rejected candidates；
   - controller 只从 accepted candidates 形成 1-6 clusters，但不得把 candidate 文件省略。

3. **加入固定 analyzer pack，并让 controller 校验命令证据。**  
   Phase 1 在 prompt 中强制执行并粘贴摘要：
   - `rg -n "ChatAsync\\(|\\.ChatAsync\\(" src agents tools -g '*.cs'`
   - `rg -n "JsonSerializer|JsonDocument|JsonNode|Newtonsoft|ToJson|FromJson" src agents tools -g '*.cs'`
   - `rg -n "Dictionary<|ConcurrentDictionary<|HashSet<|Queue<|\\block\\s*\\(" src agents tools -g '*.cs'`
   - `rg -n "Task\\.Run|Timer|ContinueWith|CancellationTokenSource|Channel\\.Create" src agents tools -g '*.cs'`
   - `rg -n "Ensure.*Projection|IEventStore|ReplayAsync|GetEventsAsync|Rebuild|Backfill" src agents tools -g '*.cs'`
   - `rg -n "actorId.*StartsWith|StartsWith\\([^\\n]*actor|TypeUrl\\.Contains|\\.HandleEventAsync\\(|SubscribeAsync<EventEnvelope>" src agents tools -g '*.cs'`
   Controller 校验 audit 输出中包含这些命令名和命中数；缺失则不接受 `AUDIT_DONE`.

4. **修改 `prefer 0` 语言为双门槛。**  
   后期 prompt 不再写“强烈优先 0”。改成机械规则：`0` 只有在 `coverage_manifest` 完整、candidate 文件存在、每个 rejected candidate 都有 CLAUDE clause + grep evidence + reject reason，且至少一个 second-pass 命令复查 highest-risk categories 后才允许。否则输出 `AUDIT_INCOMPLETE` 而不是 `AUDIT_DONE:none:0`.

5. **禁止“同模式已修”直接 reject，必须先验证 guard 覆盖。**  
   如果 reject reason 是 “covered by existing CI guard” 或 “same family as prior cluster”，audit 必须提供：
   - guard 文件路径和具体行号；
   - 当前候选路径是否在 guard scan include set 内；
   - 一个临时 probe 描述，证明同类 reintroduction 会失败；
   - 若 guard 只覆盖字符串、不覆盖语义，则候选不得因此 reject，只能降级为 guard-gap candidate。

## 4. 诚实自评：Codex 在 audit 任务上的系统性偏差

1. **我会被 prompt 的停止语言锚定。**  
   当 prompt 写“优先 0 / loop 已饱和 / 健康信号”时，我倾向于把边界案例解释为 reject reason，而不是继续寻找反例。这在 iter8-14 的语言里特别明显。

2. **我容易把 `rg` 命中分类当成理解。**  
   例如看到 `ChatAsync` 在 provider/interface/test 中大量出现后，我会快速建立“这些是 allowed”的模式，然后把少数核心残留也归入同类。真正需要的是打开调用链文件，而不是只读命中列表。

3. **我偏好可实施的小 cluster，会低估大设计违规。**  
   codex-refactor-loop 的 downstream 是 implementer cluster，所以我会自然筛掉“需要先定协议 / actor 化 / proto 迁移”的问题。这对自动 refactor 有用，但对审计完整性有害。

4. **我会把 guard pass 误当成语义证明。**  
   CI guard 很适合防回归，但大多是字符串或路径规则。作为 auditor，我容易写“guard passed, no issue remains”，而没有证明 guard 覆盖了 CLAUDE 的语义边界。

5. **我倾向于宣告 done，因为结构化输出要求给出结论。**  
   `AUDIT_DONE:none:0` 是一个很干净的 marker；在无人值守 loop 中，这种 marker 会奖励确定性和闭环，而不是奖励保留不确定性。更好的 marker 应该允许 `AUDIT_INCOMPLETE`。

6. **我会把“当前入口没调用”误解为“无风险”。**  
   Studio generator `GenerateAsync -> ChatAsync` 目前可能不是 SSE 当前路径，但保留这种表面会让后续代码复用非流式主链。审计不应只看今天的路由，还应看公开/内部 API 是否允许轻易绕过强制规则。

7. **我对架构文字的执行常低于对 bug/security 的执行。**  
   如果没有复现崩溃、泄漏或安全漏洞，我会倾向于说“不 review-blocking”。但这个仓库的 CLAUDE/AGENTS 把架构规则本身定义为强制，不能用 bug bar 替代 architecture bar。

META_AUDIT_DONE:4:5
