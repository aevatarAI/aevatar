# 2026-03-27 `feature/app-services` vs `dev` Audit Scorecard

## 审计范围

- 基线分支：`dev`
- 审计对象：当前工作树相对本地 `dev` 的完整差异，包含本地未提交修改
- 范围说明：
  - `git diff --stat dev` 当前为 `1180 files changed, 169773 insertions(+), 45014 deletions(-)`，已经超出单次逐文件穷举审查的合理范围
  - 本次评分沿用 `feature/planes` 既有基线，重点深审 `feature/planes..当前工作树` 的 `scope-first / app-services / runs console` 增量，并对当前工作树已修改文件做针对性复核

## 审计结论

当前分支相对昨天的审计基线已经明显进步：

1. `scope` 鉴权已经从 fail-open 改成 fail-closed，并补上了缺失 claim / claim 不匹配的集成测试。
2. scripts scope binding 的显式 revision 重放不再无条件命中“revision already exists”。
3. `apps/aevatar-console-web` 当前工作树的 `tsc`、后端单测/集测和架构 guard 都通过，分支已恢复到“可验证”状态。

但当前工作树仍有 4 个需要在合入前处理的实质问题：

1. Runs 控制台把普通 endpoint invoke 的同步 accepted receipt 误报成 `RUN_FINISHED`，把“已接收”说成“已完成”。
2. Runs 控制台允许自定义 `payloadTypeUrl` 且未提供 `payloadBase64`，但仍会塞入 `StringValue` 的 protobuf bytes，导致 `typeUrl` 与 payload 实际编码不一致。
3. scripts scope binding 现在对“revision 已存在”的复用判断过宽，只看 `revisionId + implementationKind + status`，没有校验底层脚本工件是否一致。
4. Scripts `Test Run` 仍绕过 `scope-first` 主链，继续打到旧 `/api/app/scripts/draft-run`。

## 主要问题

### High 1. 普通 endpoint invoke 被 UI 误报为”已完成”

- 证据：
  - `apps/aevatar-console-web/src/pages/runs/index.tsx:351-395`：`invokeEndpoint(...)` 在 351 行调用，返回后直接在 376 行派发 `RUN_STARTED`、391 行派发 `RUN_FINISHED`。同一函数在 ~406 行调用 `messageApi.success(“Endpoint ... accepted with run ...”)` — 提示文本本身诚实地写了 “accepted”，但 AGUI 事件却已经是 `RUN_FINISHED`，两者自相矛盾。
  - `src/platform/Aevatar.GAgentService.Abstractions/Protos/service_endpoint.proto:49-58` 明确把后端返回契约命名为 `ServiceInvocationAcceptedReceipt`，字段只有 `request_id / service_key / deployment_id / target_actor_id / endpoint_id / command_id / correlation_id`，无 committed 或 completed 语义。
  - `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ServiceEndpoints.cs:193-217` 和 scope 版本 invoke endpoint 都返回 `Results.Accepted(...)`，语义只是”已接受投递”。
  - `src/platform/Aevatar.GAgentService.Infrastructure/Dispatch/DefaultServiceInvocationDispatcher.cs:129-145` 返回的 receipt 同上，没有 committed 或 completed 语义。
- 影响：
  - 非 `chat` endpoint 的调用会在 UI 上被立即标成完成，Recent Runs 也会把它当成 finished 记录下来。
  - 一旦后端只是 accepted 后异步执行、或者执行失败，前端没有任何 honest way 区分“已接收”和“已完成”。
- 建议：
  - 把普通 invoke 的本地状态改成 `accepted` 或 `queued`，不要直接派发 `RUN_FINISHED`。
  - 如果要显示“完成”，必须接入后续 observation/read model，而不是用 accepted receipt 伪装完成态。

### High 2. 自定义 payload type URL 在未提供 base64 时会发送错误编码

- 证据：
  - `apps/aevatar-console-web/src/shared/api/runtimeRunsApi.ts:222-240`：`inferPayloadTypeUrl(endpointId, request.payloadTypeUrl)` 在 222 行推断类型 URL，231 行开始决定 `payloadBase64`：若为 `AppScriptCommand` 则用 `encodeAppScriptCommandBase64`，**否则无条件 fallback 到 `encodeStringValueBase64(normalizedPrompt)`**，同时把原始自定义 `payloadTypeUrl` 发给后端，形成”type URL 是用户指定类型 + bytes 是 StringValue 编码”的组合。
  - `apps/aevatar-console-web/src/pages/runs/components/RunsLaunchRail.tsx:571-580`：`payloadTypeUrl` 字段的 `extra` 提示（574 行）写的是 *”When payload base64 is empty, the workbench derives protobuf bytes from the payload text.”*，未说明此时编码格式固定为 `StringValue`，对填写非 StringValue 类型 URL 的用户形成误导。
- 影响：
  - 对任何非 `google.protobuf.StringValue`、非 `AppScriptCommand` 的 endpoint，只要用户没手填 `payloadBase64`，前端就会发送错误 wire payload。
  - 后端只能看到 `typeUrl` 正确，但内部 bytes 并不是该类型，最终要么解包失败，要么进入错误分支，属于 silent footgun。
- 建议：
  - 仅对 `StringValue` 和 `AppScriptCommand` 允许自动编码。
  - 一旦 `payloadTypeUrl` 是其他类型，必须强制要求 `payloadBase64`，或者提供真正的类型感知编码器。

### Medium 3. scripts revision 复用判断过宽，可能静默激活旧工件

- 证据：
  - `src/platform/Aevatar.GAgentService.Application/Bindings/ScopeBindingCommandApplicationService.cs:105-136` 现在只要发现同名 `revisionId` 已存在、实现类型是 `Scripting` 且未 retired，就直接跳过 `CreateRevisionAsync`。
  - 这段逻辑没有拿现存 revision 和当前请求的脚本 identity / source hash / definition actor 做等价校验。
  - `src/platform/Aevatar.GAgentService.Abstractions/Queries/ServiceRevisionCatalogSnapshot.cs:9-18`：`ServiceRevisionSnapshot` 只暴露 `RevisionId / ImplementationKind / Status / ArtifactHash / Endpoints / CreatedAt / ...`，没有脚本来源 identity（`ScriptId / DefinitionActorId / SourceHash`），说明当前快照契约无法支撑”同 revision = 同脚本工件”的显式判断。
- 影响：
  - 一旦 API 调用方传入碰撞的显式 `revisionId`，后端会静默复用旧 revision，并继续执行 `Prepare -> Publish -> Activate`。
  - `UpsertAsync(...)` 最终返回的 `ScopeBindingUpsertResult.Script` 仍来自本次请求的 script summary，可能把“旧 revision 仍在跑”伪装成“新脚本已绑定”。
- 建议：
  - 在缺乏等价性校验数据之前，对显式 `revisionId` 的碰撞默认 fail-closed。
  - 如果要保留幂等重放，必须同时校验底层脚本身份或稳定工件指纹完全一致。

### Medium 4. Scripts `Test Run` 仍然绕过 `scope-first` 主链

- 证据：
  - `apps/aevatar-console-web/src/shared/studio/scriptsApi.ts:249-263` 仍把 Test Run 发到 `/api/app/scripts/draft-run`。
  - `apps/aevatar-console-web/src/modules/studio/scripts/ScriptsWorkbenchPage.tsx:1650-1658` 的运行动作仍调用这个旧 endpoint。
  - `apps/aevatar-console-web/src/modules/studio/scripts/ScriptsWorkbenchPage.tsx:2849-2860` 的 UI 文案也明确暴露了旧路径。
- 影响：
  - workflows draft-run 已经走 `/api/scopes/{scopeId}/draft-run`，scripts Test Run 仍走旧 Studio host 入口，前端用户面继续保留两套运行主链。
  - 这会让 scripts 的测试运行、正式 binding、统一 scope 服务调用之间继续存在边界不一致。
- 建议：
  - 把 scripts Test Run 收敛到 `scope-first` 主链，至少保证测试运行与发布后调用共享同一套 scope/service 语义。

## 客观验证

| 命令 | 结果 |
|---|---|
| `bash tools/ci/architecture_guards.sh` | 通过 |
| `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo` | 通过，`260/260` |
| `dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo` | 通过，`119/119` |
| `pnpm --dir apps/aevatar-console-web exec tsc --noEmit` | 通过 |
| `pnpm --dir apps/aevatar-console-web test -- --runInBand src/shared/runs/draftRunSession.test.ts src/shared/runs/protobufPayload.test.ts src/shared/api/runtimeRunsApi.test.ts src/shared/studio/scriptsApi.test.ts` | 通过，`14/14` |
| `pnpm --dir apps/aevatar-console-web test -- --runInBand src/shared/api/runtimeRunsApi.test.ts src/shared/studio/api.test.ts src/modules/studio/scripts/ScriptsWorkbenchPage.test.tsx src/pages/runs/index.test.tsx` | 通过，`22/22` |

说明：

1. 本次是“相对 `dev` 的定向审计 + 当前工作树复核”，不是全量 `dotnet test aevatar.slnx --nologo`。
2. 当前工作树已经不存在上一版评分里提到的 `scope` fail-open 和前端 `tsc` 红灯问题。

## 总分

**87 / 100（A-）**

## 六维评分

| 维度 | 分数 | 说明 |
|---|---:|---|
| 分层与依赖反转 | 18/20 | `AppPlatform` 删除后主链更清晰，scope-first 入口也更聚焦 |
| CQRS 与统一投影链路 | 16/20 | 主体方向正确，但 scripts Test Run 仍保留旧 host 入口 |
| Projection 编排与状态约束 | 18/20 | 当前增量没有回退到中间层事实态字典，actor/projection 边界保持稳定 |
| 读写分离与会话语义 | 12/15 | 普通 invoke 的 accepted/finished 语义被前端混淆，scripts revision 幂等边界也过宽 |
| 命名语义与冗余清理 | 9/10 | 命名和收敛方向整体健康，冗余层继续减少 |
| 可验证性（门禁/构建/测试） | 14/15 | guards、后端测试、前端 `tsc` 和定向 Jest 都通过，但尚未做全量 `slnx` 级回归 |

## 分模块评分

| 模块 | 分数 | 结论 |
|---|---:|---|
| `GAgentService` | 84 | 主链收敛明显，但 scripts revision 复用语义还不够严格 |
| `Console Web / Studio` | 86 | 可验证性恢复，scripts 绑定与 GAgent 入口增强明显，但 Test Run 仍未收敛到 scope-first |
| `Runs Console` | 82 | endpoint-aware invoke 能力已经落地，但 accepted/finished 语义和 custom payload 编码仍需修正 |
| `Docs + Guards` | 91 | 文档披露更诚实，架构 guard 也能稳定通过 |

## 加分项

1. `ScopeEndpointAccess` 已经 fail-closed，并有集成测试覆盖“无 claim / claim 不匹配”。
2. `ScopeBindingCommandApplicationService` 对 scripting 显式 revision 的重复提交不再无条件炸掉，正常幂等路径已恢复。
3. Runs console 新增 endpoint-aware compose、payload helper、draft session helper，并有直接单测覆盖。
4. 当前工作树的后端测试、前端类型检查和架构门禁都通过，分支重新满足“变更可验证”的基本要求。

## 合入前修复建议

### P1

1. 把普通 endpoint invoke 的 UI 状态从“已完成”改成“已 accepted/queued”，不要再用 accepted receipt 伪装 finished。
2. 限制 custom `payloadTypeUrl` 的自动编码范围；对未知类型强制要求 `payloadBase64`。
3. 收紧 scripts revision 复用判断，在无法证明“同 revision = 同工件”之前默认拒绝碰撞。

### P2

1. 把 Scripts `Test Run` 迁到 `scope-first` 主链，去掉旧 `/api/app/scripts/draft-run` 用户面依赖。

## 非扣分观察项

1. 本次没有把 `Local / InMemory` 现状当作扣分项，遵循仓库评分规范中的基线口径。
2. 本次定向审计没有重跑全量 `dotnet build aevatar.slnx --nologo` 与 `dotnet test aevatar.slnx --nologo`；若合入前还要提分，建议补做一次全量回归。
