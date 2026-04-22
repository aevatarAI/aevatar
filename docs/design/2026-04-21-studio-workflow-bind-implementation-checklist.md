## Studio Workflow Bind Implementation Checklist

### 文档目标

把 [2026-04-21-studio-workflow-bind-information-architecture.md](./2026-04-21-studio-workflow-bind-information-architecture.md) 变成可直接开工的前端实施清单。

这份 checklist 的核心原则是：

1. 不重做整条 Studio 链路，只收口 `Bind`。
2. 不废弃现有 `ScopeServiceRuntimeWorkbench`，但不再直接把它当成 Studio 的 Bind 主页面。
3. 优先提取可复用能力，再重新组合成 `StudioMemberBindPanel`。
4. 先收口产品语义，再做视觉 polish。

---

### 1. 当前基线

当前 `Bind` 在 Studio 中由下面这段挂接：

1. [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx)
2. [ScopeServiceRuntimeWorkbench.tsx](../../apps/aevatar-console-web/src/pages/scopes/components/ScopeServiceRuntimeWorkbench.tsx)

当前可复用的能力包括：

1. published service 选择
2. endpoint 选择
3. service bindings 查询
4. service revisions 查询
5. recent runs 查询
6. binding create / edit / retire

当前可复用的调用能力包括：

1. [runtimeRunsApi.streamChat](../../apps/aevatar-console-web/src/shared/api/runtimeRunsApi.ts)
2. [runtimeRunsApi.invokeEndpoint](../../apps/aevatar-console-web/src/shared/api/runtimeRunsApi.ts)
3. [StudioMemberInvokePanel.tsx](../../apps/aevatar-console-web/src/pages/studio/components/StudioMemberInvokePanel.tsx)

当前可复用的 NyxID 预绑定能力包括：

1. [studioApi.bindScopeGAgent](../../apps/aevatar-console-web/src/shared/studio/api.ts)
2. [createNyxIdChatBindingInput](../../apps/aevatar-console-web/src/shared/runs/scopeConsole.ts)

---

### 2. 目标交付

本轮要交付的是一个新的 `Bind` 页面组合，而不是继续往 `ScopeServiceRuntimeWorkbench` 上堆内容。

建议交付结构：

1. `StudioMemberBindPanel`
2. `BindInvokeContractCard`
3. `BindParametersForm`
4. `BindSnippetTabs`
5. `BindSmokeTestRail`
6. `BindExistingBindingsSection`
7. `BindRevisionsSection`

---

### 3. 文件落位建议

建议新增目录：

`apps/aevatar-console-web/src/pages/studio/components/bind/`

建议新增文件：

1. `StudioMemberBindPanel.tsx`
2. `BindInvokeContractCard.tsx`
3. `BindParametersForm.tsx`
4. `BindSnippetTabs.tsx`
5. `BindSmokeTestRail.tsx`
6. `BindExistingBindingsSection.tsx`
7. `BindRevisionsSection.tsx`
8. `bindModels.ts`
9. `bindSnippets.ts`
10. `bindContract.ts`

建议新增测试：

1. `StudioMemberBindPanel.test.tsx`
2. `bindSnippets.test.ts`
3. `bindContract.test.ts`

---

### 4. 分阶段实施

## Phase 0: 收口 Bind View Model

目标：

先把页面真正的一等对象定义出来，避免组件层继续直接消费零散查询结果。

任务：

- [ ] 定义 `StudioBindContract`
- [ ] 定义 `StudioBindParametersView`
- [ ] 定义 `StudioBindSmokeTestState`
- [ ] 定义 `StudioBindSnippetInput`
- [ ] 写 `buildStudioBindContract(...)`

`StudioBindContract` 至少包含：

1. `serviceId`
2. `serviceDisplayName`
3. `serviceKey`
4. `endpointId`
5. `endpointDisplayName`
6. `invokeUrl`
7. `method`
8. `authScheme`
9. `scopeLabel`
10. `environment`
11. `revisionId`
12. `deploymentStatus`
13. `streaming`

数据来源：

1. `scopeBindingQuery`
2. `publishedScopeServices`
3. `bindingSelectionRef`
4. revisions query

验收：

1. `Bind` 页面上的核心卡片不再直接读散装字段。
2. snippet 生成和 smoke-test 都只依赖 `StudioBindContract`。

## Phase 1: 引入新的 Bind 主组件

目标：

停止在 Studio 中直接渲染 `ScopeServiceRuntimeWorkbench` 作为 Bind 主页面。

任务：

- [ ] 在 [studio/index.tsx](../../apps/aevatar-console-web/src/pages/studio/index.tsx) 新接入 `StudioMemberBindPanel`
- [ ] 保留 `ScopeServiceRuntimeWorkbench` 给 `/scopes/invoke` 等现有 runtime 页面继续使用
- [ ] 给 `StudioMemberBindPanel` 传入：
  - `scopeId`
  - `scopeBinding`
  - `services`
  - `initialServiceId`
  - `initialEndpointId`
  - `onSelectionChange`
  - `onContinueToInvoke`

验收：

1. `Studio -> Bind` 不再直接显示 `Published Services / Runtime Posture` 大卡片布局。
2. Studio Bind 与 Scope Runtime Workbench 正式脱钩。

## Phase 2: 落 Invoke Contract Card

目标：

让 Bind 首屏第一块就变成“怎么调”。

任务：

- [ ] 落 `BindInvokeContractCard`
- [ ] 展示：
  - `POST`
  - invoke URL
  - copy
  - `bound/live/draft` 标签
  - auth 标签
  - scope 标签
  - revision 标签
  - streaming 标签
- [ ] 加 `Need a NyxID token?` 辅助说明

实现注意：

1. URL 允许先用前端派生值占位，但必须在文案上诚实。
2. 任何尚无后端来源的字段都要带 provenance，而不是伪装成已持久化配置。

验收：

1. 用户进入 Bind 首屏，第一眼能看到当前 invoke URL。
2. 用户不需要先理解 runtime posture 才知道怎么调。

## Phase 3: 落 Binding Parameters Form

目标：

把当前 contract 的结构化信息讲清楚。

任务：

- [ ] 落 `BindParametersForm`
- [ ] 支持字段：
  - `Scope`
  - `Environment`
  - `Revision`
  - `Rate limit`
  - `Allowed origins`
  - `Streaming`
- [ ] `Revision` 变化时联动更新 contract 与 snippets
- [ ] `Endpoint` 变化时联动更新 contract 与 smoke-test placeholder

实现注意：

1. 当前 scope 在 Studio 中通常是只读。
2. 如果某些字段暂无正式写入 API，则优先只读展示。
3. 不允许前端伪装“已经保存”的配置。

验收：

1. `Binding Parameters` 与 `Invoke URL` 明显属于同一契约区域。
2. 切 revision / endpoint 能立即反映到首屏。

## Phase 4: 落 Snippet Tabs

目标：

把 contract 直接转成可复制代码。

任务：

- [ ] 新建 `bindSnippets.ts`
- [ ] 实现：
  - `buildCurlSnippet`
  - `buildFetchSnippet`
  - `buildSdkSnippet`
- [ ] 新建 `BindSnippetTabs`
- [ ] 支持 `copy`
- [ ] snippet 内容联动：
  - URL
  - revision
  - auth
  - stream protocol
  - sample body

可复用来源：

1. 原型 `/Users/xiezixin/Downloads/aevatar-console/project/js/bind.jsx`

验收：

1. 任何 contract 变化都会同步刷新 snippet。
2. 用户可以直接复制 cURL / Fetch / SDK 片段。

## Phase 5: 落 Smoke-test Rail

目标：

在 Bind 里提供轻量验证，但不复制完整 Invoke。

任务：

- [ ] 新建 `BindSmokeTestRail`
- [ ] 支持输入：
  - bearer token
  - prompt 或 body
- [ ] 支持 `Send test request`
- [ ] 显示：
  - status
  - latency
  - response summary
  - error summary
- [ ] 成功后允许一键 `进入调用`

调用策略建议：

1. 非 chat endpoint：
   直接复用 `runtimeRunsApi.invokeEndpoint(...)`
2. chat endpoint：
   第一版可以只支持最小 chat smoke-test
   或明确回退到 `进入调用`

重要约束：

1. 不在 Bind 内复制完整 transcript 面板。
2. 不在 Bind 内复制 request history。
3. 不在 Bind 内复制 events 浏览器。

验收：

1. Bind 首屏可以完成一次轻量 smoke-test。
2. 用户仍然清楚完整调用应该去 `Invoke`。

## Phase 6: 迁移 Existing Bindings / Revisions

目标：

保留治理与历史能力，但降为 Bind 下半区。

任务：

- [ ] 从 `ScopeServiceRuntimeWorkbench` 提取 `bindingCards`
- [ ] 落 `BindExistingBindingsSection`
- [ ] 保留：
  - `Edit binding`
  - `Retire`
- [ ] 从 `ScopeServiceRuntimeWorkbench` 提取 revision list
- [ ] 落 `BindRevisionsSection`
- [ ] 将 `Runs` 改为次级 deep link，而不是首屏主区块

验收：

1. 用户不会在首屏先被治理信息淹没。
2. 绑定治理和 revision 历史仍然可达。

## Phase 7: Studio 连续体验打通

目标：

把 `Build -> Bind -> Invoke` 做成真正连续的一条链。

任务：

- [ ] `Build -> Continue to Bind` 保留当前 member 上下文
- [ ] `Bind -> 进入调用` 传递：
  - selected service id
  - selected endpoint id
  - 当前 contract 相关状态
- [ ] `Invoke` 打开后默认落在刚才选择的 service/endpoint
- [ ] 成功 smoke-test 后可以显式提示：
  - `继续在 Invoke 查看完整事件流`

验收：

1. 用户从 Build 进入 Bind，不会丢 member 上下文。
2. 用户从 Bind 进入 Invoke，不需要重新选 service/endpoint。

---

### 5. 测试清单

- [ ] `StudioMemberBindPanel.test.tsx`
  验证首屏顺序为：
  `Invoke URL -> Binding parameters -> Snippets -> Smoke-test`
- [ ] `bindContract.test.ts`
  验证 contract 生成逻辑
- [ ] `bindSnippets.test.ts`
  验证 cURL / Fetch / SDK 输出
- [ ] `index.test.tsx`
  验证 Studio 的 Bind surface 已切换成新组件
- [ ] 保留 `ScopeServiceRuntimeWorkbench` 原有测试，确保 `/scopes/invoke` 不被破坏

---

### 6. 非目标

- [ ] 不在本轮重做 `Invoke`
- [ ] 不在本轮重做 `Observe`
- [ ] 不为 `Bind` 新造一套后端 binding schema
- [ ] 不把 scope runtime 页面删除

---

### 7. 完成标准

当下面这条链路可走通时，本期才算完成：

1. 在 `Build` 中完成 workflow draft 并点击 `Continue to Bind`
2. 在 `Bind` 首屏看到 invoke contract
3. 在 `Bind` 中确认 binding parameters
4. 在 `Bind` 中复制 snippet 或完成 smoke-test
5. 点击 `进入调用`
6. 在 `Invoke` 中直接看到刚才那套 service / endpoint 上下文

如果 Bind 仍然主要表现成“runtime inspection 面板”，而不是“invoke contract workbench”，则视为本期未完成。
