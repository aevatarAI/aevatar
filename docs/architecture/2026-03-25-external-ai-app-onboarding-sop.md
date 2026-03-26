# 外部 AI App 接入 Aevatar SOP（2026-03-25）

本文给的是 **当前代码现状下可执行** 的外部 AI app 接入 SOP。

它基于下面这些现实约束：

- `Aevatar` 已经作为长期运行的 `mainnet host`
- 认证接入走 `NyxID`
- `GAgentService` 是统一 capability kernel
- `AppPlatform` 目前是 **Phase 1 bootstrap control plane**
- 自定义 GAgent 只支持 **trusted type**，即预编译进宿主部署物，不支持在线上传源码/二进制动态装载

所以当前外部 app 接入，不是“完全自助式 SaaS 发布”，而是：

1. app 团队准备 workflow / script / trusted agent
2. 平台把这些能力发布成 `service`
3. 平台配置 `AppPlatform`
4. 重新部署 `mainnet host`

本文中的 HTTP 示例默认以 `http://localhost:5100` 作为宿主地址。

---

## 1. 适用范围

这份 SOP 适用于三类外部 app 能力：

- `workflow` 型 app
- `script` 型 app
- `trusted static gagent` 型 app

也适用于三者组合的 app。

不适用于：

- 第三方在线上传 `.cs` / `.dll` / `.zip` 后由平台动态编译或热装载
- 把 `Studio` 当成生产发布系统
- 把 `AppPlatform` 当成已经 actor 化、自助可写的 control plane

---

## 2. 角色分工

### 2.1 外部 app 团队

负责：

- 定义 app 的产品语义
- 准备 workflow yaml / script source / trusted agent 代码
- 给出 app 的 `app_id / route / entry service / service topology`
- 提供 smoke test case

### 2.2 Aevatar 平台团队

负责：

- 在 `mainnet host` 中接入 trusted agent 程序集
- 通过 `GAgentService` 发布 service / revision / serving
- 配置 governance
- 配置 `AppPlatform`
- 部署与验证

---

## 3. 接入前必填信息

每个外部 app 接入前，先冻结下面这张表。

| 项目 | 示例 | 说明 |
|------|------|------|
| `owner_scope_id` | `scope-dev` | app 所属 NyxID scope / org |
| `tenant_id` | `scope-dev` | 当前建议与 `owner_scope_id` 对齐 |
| `app_id` | `copilot` | app 稳定标识 |
| `namespace` | `prod` | 环境或发布通道 |
| `route_path` | `/copilot` | 对外入口路径 |
| `entry_service_id` | `chat-gateway` | 外部流量打到的 service |
| `entry_endpoint_id` | `chat` | service 暴露的 endpoint |
| `implementation_kind` | `workflow / scripting / static` | service 实现类型 |

建议统一命名：

```text
tenant_id  = owner_scope_id
app_id     = 外部 AI app 稳定名
namespace  = prod / staging / dev
service_id = chat-gateway / retrieval-script / knowledge-agent
```

示例：

```text
scope-dev:copilot:prod:chat-gateway
scope-dev:copilot:prod:retrieval-script
scope-dev:copilot:prod:knowledge-agent
```

---

## 4. 选接入模式

先决定外部 app 的主实现落在哪一类能力上。

### 4.1 Workflow 模式

适合：

- 编排驱动
- 多 step 对话
- connector / tool / role 组合
- 适合用 yaml 描述流程

### 4.2 Script 模式

适合：

- 逻辑相对集中
- 演化频繁
- 需要脚本化定义行为或 read model

### 4.3 Trusted Static GAgent 模式

适合：

- 需要自定义 `.NET` actor 行为
- 需要继承 `AIGAgentBase` 或 `GAgentBase`
- 需要比 workflow / script 更强的运行时控制

**关键现实**：

- trusted agent 不是上传到平台
- trusted agent 必须编进宿主部署物
- 然后通过 static revision 的 `ActorTypeName` 被 `GAgentService` 激活

---

## 5. SOP 主流程

## Step 1. 冻结 app topology

先明确：

- 哪个 service 是对外入口
- 哪些 service 是内部 companion
- 哪些 service 只做 internal capability
- 是否需要 governance binding

最小建议拓扑：

```text
entry service
  -> workflow service
  -> scripting service
  -> trusted agent service
```

如果 app 很简单，也可以只有一个 `entry service`。

---

## Step 2. 准备实现资产

### 2A. workflow 资产

外部团队交付：

- `workflow yaml`
- 如有子 workflow，则交付 inline yamls 或引用关系

当前 scope workflow 写入口在：

- [ScopeWorkflowEndpoints.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeWorkflowEndpoints.cs)

对应 API：

```text
PUT /api/scopes/{scopeId}/workflows/{workflowId}
GET /api/scopes/{scopeId}/workflows
GET /api/scopes/{scopeId}/workflows/{workflowId}
```

最小请求体：

```json
{
  "workflowYaml": "name: chat-gateway\nversion: v1\n...",
  "workflowName": "chat-gateway",
  "displayName": "Chat Gateway"
}
```

### 2B. script 资产

外部团队交付：

- script source
- 可选 `revision_id`

当前 scope script 写入口在：

- [ScopeScriptEndpoints.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeScriptEndpoints.cs)

对应 API：

```text
PUT /api/scopes/{scopeId}/scripts/{scriptId}
GET /api/scopes/{scopeId}/scripts
GET /api/scopes/{scopeId}/scripts/{scriptId}
GET /api/scopes/{scopeId}/scripts/{scriptId}/catalog
POST /api/scopes/{scopeId}/scripts/{scriptId}/evolutions/proposals
```

最小请求体：

```json
{
  "sourceText": "// script source here",
  "revisionId": "r1"
}
```

### 2C. trusted static gagent 资产

外部团队交付：

- 业务工程代码
- `AIGAgentBase` 或 `GAgentBase` 子类
- 稳定 `ActorTypeName`
- 对外 endpoint 契约

当前要求：

- trusted agent 代码进入宿主编译链
- 宿主重新构建、重新部署

建议工程路径：

```text
src/apps/<app>.TrustedAgents
```

这是业务工程，不是平台内核工程。

---

## Step 3. 把能力发布为 GAgentService service

这一步开始，外部 app 能力要进入统一 service 语义。

主入口在：

- [ServiceEndpoints.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.GAgentService.Hosting/Endpoints/ServiceEndpoints.cs)
- [ServiceServingEndpoints.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.GAgentService.Hosting/Endpoints/ServiceServingEndpoints.cs)

### 3.1 创建 service definition

API：

```text
POST /api/services
```

最小请求：

```json
{
  "tenantId": "scope-dev",
  "appId": "copilot",
  "namespace": "prod",
  "serviceId": "chat-gateway",
  "displayName": "Chat Gateway",
  "endpoints": [
    {
      "endpointId": "chat",
      "displayName": "Chat",
      "kind": "chat",
      "requestTypeUrl": "type.googleapis.com/example.ChatRequest",
      "responseTypeUrl": "type.googleapis.com/example.ChatResponse",
      "description": "Public chat endpoint"
    }
  ],
  "policyIds": ["public-chat-policy"]
}
```

### 3.2 创建 revision

API：

```text
POST /api/services/{serviceId}/revisions
```

#### workflow revision 示例

```json
{
  "tenantId": "scope-dev",
  "appId": "copilot",
  "namespace": "prod",
  "revisionId": "r1",
  "implementationKind": "workflow",
  "workflow": {
    "workflowName": "chat-gateway",
    "workflowYaml": "name: chat-gateway\nversion: v1\n..."
  }
}
```

#### scripting revision 示例

```json
{
  "tenantId": "scope-dev",
  "appId": "copilot",
  "namespace": "prod",
  "revisionId": "r1",
  "implementationKind": "scripting",
  "scripting": {
    "scriptId": "retrieval-script",
    "revision": "r1",
    "definitionActorId": "script-def:scope-dev:retrieval-script"
  }
}
```

#### trusted static revision 示例

```json
{
  "tenantId": "scope-dev",
  "appId": "copilot",
  "namespace": "prod",
  "revisionId": "r1",
  "implementationKind": "static",
  "static": {
    "actorTypeName": "Copilot.TrustedAgents.ChatGatewayAgent",
    "preferredActorId": "copilot-chat-gateway",
    "endpoints": [
      {
        "endpointId": "chat",
        "displayName": "Chat",
        "kind": "chat",
        "requestTypeUrl": "type.googleapis.com/example.ChatRequest",
        "responseTypeUrl": "type.googleapis.com/example.ChatResponse",
        "description": "Public chat endpoint"
      }
    ]
  }
}
```

### 3.3 准备并发布 revision

API：

```text
POST /api/services/{serviceId}/revisions/{revisionId}:prepare
POST /api/services/{serviceId}/revisions/{revisionId}:publish
POST /api/services/{serviceId}:activate
POST /api/services/{serviceId}:default-serving
```

如果只走单 revision，最小顺序是：

1. `create revision`
2. `prepare`
3. `publish`
4. `activate`
5. `set default serving revision`

---

## Step 4. 配置 serving

如果需要显式 serving target 或 rollout，使用：

```text
POST /api/services/{serviceId}:deploy
POST /api/services/{serviceId}:serving-targets
GET  /api/services/{serviceId}/serving
POST /api/services/{serviceId}/rollouts
GET  /api/services/{serviceId}/rollouts
GET  /api/services/{serviceId}/traffic
```

当前外部 app 首次接入，建议先用最简单策略：

- 只保留一个 active revision
- serving 100% 指向该 revision
- 不在首版接入阶段引入复杂 rollout

---

## Step 5. 配置 governance

app 内部 service 调用关系、公开暴露面、调用权限，统一走 governance。

入口在：

- [ServiceBindingEndpoints.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.GAgentService.Governance.Hosting/Endpoints/ServiceBindingEndpoints.cs)
- [ServiceEndpointCatalogEndpoints.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.GAgentService.Governance.Hosting/Endpoints/ServiceEndpointCatalogEndpoints.cs)
- [ServicePolicyEndpoints.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.GAgentService.Governance.Hosting/Endpoints/ServicePolicyEndpoints.cs)

### 5.1 配置 endpoint catalog

API：

```text
POST /api/services/{serviceId}/endpoint-catalog
PUT  /api/services/{serviceId}/endpoint-catalog
GET  /api/services/{serviceId}/endpoint-catalog
```

最小示例：

```json
{
  "tenantId": "scope-dev",
  "appId": "copilot",
  "namespace": "prod",
  "endpoints": [
    {
      "endpointId": "chat",
      "displayName": "Chat",
      "kind": "chat",
      "requestTypeUrl": "type.googleapis.com/example.ChatRequest",
      "responseTypeUrl": "type.googleapis.com/example.ChatResponse",
      "description": "Public chat endpoint",
      "exposureKind": "public",
      "policyIds": ["public-chat-policy"]
    }
  ]
}
```

### 5.2 配置 policy

API：

```text
POST /api/services/{serviceId}/policies
PUT  /api/services/{serviceId}/policies/{policyId}
GET  /api/services/{serviceId}/policies
GET  /api/services/{serviceId}:activation-capability
```

最小示例：

```json
{
  "tenantId": "scope-dev",
  "appId": "copilot",
  "namespace": "prod",
  "policyId": "public-chat-policy",
  "displayName": "Public Chat Policy",
  "activationRequiredBindingIds": [],
  "invokeAllowedCallerServiceKeys": [],
  "invokeRequiresActiveDeployment": true
}
```

### 5.3 配置 binding

如果 entry service 需要调用 companion service / connector / secret，继续配置 binding。

API：

```text
POST /api/services/{serviceId}/bindings
PUT  /api/services/{serviceId}/bindings/{bindingId}
GET  /api/services/{serviceId}/bindings
```

service binding 示例：

```json
{
  "tenantId": "scope-dev",
  "appId": "copilot",
  "namespace": "prod",
  "bindingId": "retrieval",
  "displayName": "Retrieval Service",
  "bindingKind": "service",
  "service": {
    "tenantId": "scope-dev",
    "appId": "copilot",
    "namespace": "prod",
    "serviceId": "retrieval-script",
    "endpointId": "run"
  },
  "policyIds": []
}
```

---

## Step 6. 配置 AppPlatform

这一步是 **当前 Phase 1 最关键的现实差异**。

当前 `AppPlatform` 还不是 write-side actor/control plane，而是：

- 宿主内配置驱动
- 对外只暴露 query / resolve 能力
- 所以 app / release / route 不是通过 API 写入
- 而是通过宿主配置声明

代码入口：

- [AppPlatformOptions.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.AppPlatform.Infrastructure/Configuration/AppPlatformOptions.cs)
- [ConfiguredAppRegistryReader.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.AppPlatform.Infrastructure/Readers/ConfiguredAppRegistryReader.cs)
- [AppPlatformEndpoints.cs](/Users/chronoai/Code/aevatar/src/platform/Aevatar.AppPlatform.Hosting/Endpoints/AppPlatformEndpoints.cs)

### 6.1 配置样例

把下面内容加入 `mainnet host` 配置，例如 `appsettings.json`：

```json
{
  "AppPlatform": {
    "Apps": [
      {
        "AppId": "copilot",
        "OwnerScopeId": "scope-dev",
        "DisplayName": "Copilot",
        "Description": "External AI copilot app",
        "Visibility": "public",
        "DefaultReleaseId": "prod-2026-03-25",
        "Routes": [
          {
            "RoutePath": "/copilot",
            "ReleaseId": "prod-2026-03-25",
            "EntryId": "default-chat"
          }
        ],
        "Releases": [
          {
            "ReleaseId": "prod-2026-03-25",
            "DisplayName": "Production",
            "Status": "published",
            "Services": [
              {
                "TenantId": "scope-dev",
                "AppId": "copilot",
                "Namespace": "prod",
                "ServiceId": "chat-gateway",
                "RevisionId": "r1",
                "ImplementationKind": "workflow",
                "Role": "entry"
              }
            ],
            "Entries": [
              {
                "EntryId": "default-chat",
                "ServiceId": "chat-gateway",
                "EndpointId": "chat"
              }
            ]
          }
        ]
      }
    ]
  }
}
```

### 6.2 配置规则

- `Routes[*].ReleaseId` 必须指向存在的 release
- `Routes[*].EntryId` 必须指向该 release 内存在的 entry
- `Entries[*].ServiceId` 必须指向该 release 内存在的 service
- entry 对应的 service 必须标记 `Role = entry`

---

## Step 7. 部署 mainnet host

如果 app 包含 trusted static gagent，这一步必须重新构建宿主。

最小要求：

1. trusted agent 程序集进入宿主编译链
2. `AppPlatform` 配置进入宿主配置
3. `mainnet host` 重新部署

当前宿主入口：

- [Program.cs](/Users/chronoai/Code/aevatar/src/Aevatar.Mainnet.Host.Api/Program.cs)

`AppPlatform` capability 已经在主网里接上：

```csharp
builder.AddAppPlatformCapability();
builder.AddGAgentServiceCapabilityBundle();
builder.AddStudioCapability();
```

---

## Step 8. 接入后验证

### 8.1 验证 AppPlatform route resolve

API：

```text
GET /api/apps/resolve?routePath=/copilot
```

预期：

- 能返回 `app`
- 能返回 `release`
- 能返回 `entry`
- `entry.service_ref` 指向正确的 service

### 8.2 验证 app 查询

```text
GET /api/apps
GET /api/apps/{appId}
GET /api/apps/{appId}/releases
GET /api/apps/{appId}/routes
```

### 8.3 验证 service 生命周期

```text
GET /api/services?tenantId=scope-dev&appId=copilot&namespace=prod
GET /api/services/chat-gateway?tenantId=scope-dev&appId=copilot&namespace=prod
GET /api/services/chat-gateway/revisions?tenantId=scope-dev&appId=copilot&namespace=prod
GET /api/services/chat-gateway/serving?tenantId=scope-dev&appId=copilot&namespace=prod
```

### 8.4 验证 governance

```text
GET /api/services/chat-gateway/endpoint-catalog?tenantId=scope-dev&appId=copilot&namespace=prod
GET /api/services/chat-gateway/policies?tenantId=scope-dev&appId=copilot&namespace=prod
GET /api/services/chat-gateway/bindings?tenantId=scope-dev&appId=copilot&namespace=prod
```

### 8.5 验证业务入口 invoke

API：

```text
POST /api/services/{serviceId}/invoke/{endpointId}
```

最小示例：

```json
{
  "tenantId": "scope-dev",
  "appId": "copilot",
  "namespace": "prod",
  "commandId": "cmd-001",
  "correlationId": "corr-001",
  "payloadTypeUrl": "type.googleapis.com/example.ChatRequest",
  "payloadBase64": "<base64-encoded-protobuf-payload>"
}
```

---

## 10. 当前阶段必须诚实告知外部团队的限制

当前要明确告诉接入方：

1. 这不是完全自助式接入
   当前 `AppPlatform` 没有开放写 API，app/release/route 仍由平台侧配置。

2. trusted agent 不是“上传插件”
   trusted static gagent 必须进宿主编译链，然后由平台部署。

3. Studio 不是生产发布面
   `Studio` 当前主要是 authoring / BFF，不是正式的 app release control plane。

4. app route resolve 已经有了，但还是 bootstrap 版本
   当前 query / resolve 可用，后续会演进成 actor/projection 权威实现。

---

## 11. 推荐的首版接入策略

如果要降低首次接入复杂度，推荐按下面顺序：

1. 先只接一个 entry service
2. 首版优先 workflow 或 static gagent 二选一
3. governance 只配最小 public endpoint + 必需 binding
4. AppPlatform 只配一个 route、一个 release
5. 先上线 `prod` 单 revision，不在首版引入 rollout

---

## 12. 平台团队交付清单

外部 AI app 接入完成时，平台团队至少交付：

- app 基础信息表
- service identity 清单
- governance 配置清单
- `AppPlatform` 配置片段
- 部署版本号
- smoke test 结果
- 回滚方式

---

## 13. 一句话版本

当前外部 AI app 接入 Aevatar 的真实 SOP 是：

**先把 app 能力做成 workflow / script / trusted static gagent，再通过 `GAgentService` 发布成 service，配好 governance，最后把 app/release/route 写进 `AppPlatform` 配置并重新部署主网宿主。**
