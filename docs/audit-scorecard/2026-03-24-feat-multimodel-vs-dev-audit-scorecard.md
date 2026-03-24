# 2026-03-24 `feat/multimodel` vs `dev` 审计评分卡

## 1. 审计范围

- 分支：`feat/multimodel`
- 基线：`dev`
- 审计口径：`git diff dev...`，按当前工作树审计，包含未提交改动
- 变更规模：`87 files changed, 2540 insertions(+), 541 deletions(-)`
- 审计重点：多模态 LLM 请求链路、workflow chat capability API、console-web 登录与导航、相关测试与文档

---

## 2. 总评

| 总分 | 等级 | 结论 |
|------|------|------|
| **80 / 100** | **B-** | 多模态建模方向正确，Proto 强类型化、workflow chat 端点覆盖和 AI payload 序列化都有明显进展；但当前分支仍有一个阻断级前端可用性问题，以及两个会让多模态请求“看起来成功、实际降级”的接口问题，不建议在修复 P1 前直接合并。 |

---

## 3. 关键发现

### P1. `console-web` 新的自定义导航绕过了 `base/publicPath`，子路径部署会直接跳错页

- **严重性**：高
- **证据**：
  - `apps/aevatar-console-web/config/config.ts` 已新增 `AEVATAR_CONSOLE_PUBLIC_PATH` 并设置 `base/publicPath`
  - `apps/aevatar-console-web/src/shared/navigation/history.ts:12-27` 直接把目标地址写成 `pathname + search + hash`
  - `apps/aevatar-console-web/src/app.tsx:34-47, 137-180` 仍然大量写死 `/login`、`/overview`、`/settings`
  - `apps/aevatar-console-web/src/pages/auth/callback/index.tsx:15-17, 30, 51-52`
  - `apps/aevatar-console-web/src/shared/auth/session.ts:146-157`
- **问题**：
  - 这次分支显式引入了非根路径部署能力，但新的导航实现仍把所有站内跳转当成站点根路径。
  - 一旦 console 部署在 `/console/`、`/apps/console/` 之类的子路径下，登录回跳、未登录重定向、logout、菜单跳转都会离开当前 base，直接落到站点根目录。
- **影响**：
  - `/console/auth/callback` 登录完成后会跳去 `/overview`，不是 `/console/overview`
  - 未登录保护会跳到 `/login`，不是 `/console/login`
  - 这会直接导致 404、回跳丢失或进入错误应用
- **建议**：
  - 不要自己拼根路径，统一走 Umi router/base 能力，或引入一个“带 base 前缀”的 route builder
  - 至少把 login/logout/callback/default route/sanitizeReturnTo 统一改为 base-aware

### P2. 非法或超限的多模态输入分片会被静默丢弃，带文本 prompt 的请求仍然会成功下发

- **严重性**：中高
- **证据**：
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatRunRequestNormalizer.cs:132-160`
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatRunRequestNormalizer.cs:163-168`
- **问题**：
  - `NormalizeInputParts` 对不支持的 `type`、超长 `data_base64`、空分片一律 `continue`
  - 只有“没有 prompt 且所有 inputParts 都被丢光”时，才返回 `PromptRequired`
  - 也就是说，只要用户同时传了文本 prompt，请求就会继续执行，即使附件已经全部被吞掉
- **影响**：
  - 用户会以为图片/PDF/音频已经参与推理，但实际下游只收到文本 prompt
  - 这对“看图总结”“读 PDF 审阅”“语音转写”类场景是隐蔽的正确性问题
- **建议**：
  - 对被拒绝的 input part 返回显式校验错误，而不是静默跳过
  - 至少要区分 `unsupported type`、`missing payload`、`payload too large`

### P3. multimodal-only 非法请求返回 `PROMPT_REQUIRED`，错误语义与实际原因不符

- **严重性**：中
- **证据**：
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatRunRequestNormalizer.cs:44-46`
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatRunRequestNormalizer.cs:163-168`
  - 当前测试已把这个行为固化：`test/Aevatar.Workflow.Host.Api.Tests/ChatEndpointsInternalTests.cs` 与 `test/Aevatar.Workflow.Host.Api.Tests/WorkflowCapabilityEndpointsCoverageTests.cs`
- **问题**：
  - 当客户端发送了 `inputParts`，但这些分片都不合法时，接口返回的是“缺少 prompt”
  - 这不是事实错误本身，属于错误码建模不诚实
- **影响**：
  - SDK / UI 侧无法告诉用户“你的文件类型不支持”或“附件太大”
  - 未来如果要做稳定的前端提示、重试策略或埋点分析，会把输入格式问题误记成 prompt 缺失
- **建议**：
  - 新增显式错误，如 `InvalidInputPart` / `InputPartTooLarge`
  - 保留 `PromptRequired` 只用于“确实既没有 prompt，也没有有效 input part”

---

## 4. 评分拆解

| 维度 | 权重 | 得分 | 说明 |
|------|-----:|-----:|------|
| 分层与强类型语义 | 20 | 18 | `ChatContentPart` / `ContentPartKind` / `MediaContentEvent` 的方向是对的，边界内模型比之前更清晰。 |
| 多模态链路完整性 | 20 | 15 | 请求、Proto、provider、AGUI 事件都已打通，但输入校验仍会静默降级。 |
| 前端运行正确性 | 20 | 10 | publicPath 支持引入后，导航和 auth 回跳没有同步做 base-aware，影响真实部署。 |
| CQRS / Actor / 会话语义 | 15 | 13 | 没看到明显越层或进程内事实态回退，session replay 也补了 input/output parts。 |
| 测试与可验证性 | 15 | 14 | 新增的 AI / workflow chat 测试不少，目标子集全部通过；但 publicPath 场景与 mixed prompt + invalid attachment 场景仍无覆盖。 |
| 文档与运维适配 | 10 | 10 | 文档、README、启动脚本、`ListenUrlResolver` 都在补齐部署体验。 |

---

## 5. 主要加分项

1. **多模态语义进入强类型 Proto 主链路**
   - `ai_messages.proto` 新增 `ChatContentPartKind`、`ChatContentPart`、`MediaContentEvent`
   - `RoleChatSessionStartedEvent` / `RoleChatSessionCompletedEvent` / `RoleChatSessionState` 都纳入 `input_parts` / `output_parts`

2. **workflow chat API 已具备 multimodal 输入能力**
   - `ChatInput`、`WorkflowChatRunRequest`、`WorkflowChatRequestEnvelopeFactory`、`AevatarWorkflowClient` 已同步支持 `InputParts`

3. **AI payload 的 JSON 序列化能力补齐**
   - `ChatJsonPayloads` 已把 `AiMessagesReflection.Descriptor` 加入 type registry
   - `MediaContentEvent` 这类 custom payload 能正确输出到 capability/API 层

4. **测试覆盖确实有增量**
   - `ContentPartProtoMapperTests`
   - `LLMProviderCapabilitiesTests`
   - `ChatEndpointsInternalTests`
   - `WorkflowCapabilityEndpointsCoverageTests`

---

## 6. 验证结果

已执行：

1. `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --filter "FullyQualifiedName~ContentPartProtoMapperTests|FullyQualifiedName~LLMProviderCapabilitiesTests|FullyQualifiedName~AIAbstractionsProtoCoverageTests" --nologo`
   - 结果：**PASS**
   - 通过：`43`

2. `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --filter "FullyQualifiedName~ChatEndpointsInternalTests|FullyQualifiedName~WorkflowCapabilityEndpointsCoverageTests|FullyQualifiedName~ChatJsonPayloadsTests|FullyQualifiedName~ChatWebSocketCoordinatorAndProtocolTests" --nologo`
   - 结果：**PASS**
   - 通过：`63`

观察到的非阻断项：

- 还原与构建过程中存在大量 `NU1507`
- 相关项目中仍有若干 `CA1502 / CA1506` 复杂度与耦合告警，但本次关键发现不依赖这些告警成立

---

## 7. 建议修复顺序

### P1（合并前建议修复）

1. 让 console-web 的所有内部跳转、auth 回跳、logout、默认保护路由都变成 base-aware
2. 为 publicPath/base-path 场景补一组最小前端测试

### P2（紧随其后）

1. 把 `inputParts` 的拒绝原因建模为显式错误，而不是静默丢弃
2. 覆盖“有 prompt + 非法附件”的场景，确保请求不会悄悄降级成纯文本

### P3（后续迭代）

1. 为 capability / SDK / WebSocket 三条入口统一错误语义
2. 考虑把前端/API 边界上的 `type` 字符串进一步收敛成更稳定的契约表示，减少运行时兜底分支
