# Connector 配置与执行逻辑

这份文档基于当前代码实现梳理两件事：

1. Connector 现在怎么配置、怎么被系统加载；
2. Agent/Workflow 在运行时如何使用 Connector。

---

## 1. 总览：Connector 在系统里的位置

- `IConnector` / `IConnectorRegistry` 是统一外部调用契约，定义在 `Aevatar.Foundation.Abstractions`。
- Connector 的定义是中心化配置（`~/.aevatar/connectors.json`）。
- Workflow 里通过 `type: connector_call` + `parameters.connector` 使用命名 Connector。
- 角色（role）里的 `connectors` 是授权白名单，不是连接定义本身。

简化链路：

1. 启动时读取 `connectors.json`；
2. 按 `type` 用 Builder 构造具体 Connector（`http/cli/mcp`）；
3. 注册到 `IConnectorRegistry`；
4. 运行 `connector_call` 步骤时按名称解析并调用。

---

## 2. 配置方式（当前实现）

## 2.1 配置文件位置

- 默认：`~/.aevatar/connectors.json`
- 可通过环境变量 `AEVATAR_HOME` 改变根目录（最终路径为 `${AEVATAR_HOME}/connectors.json`）。

`AevatarConfigLoader` 会把 `connectors.json` 加入配置源；但 Connector 实例注册是启动阶段执行，不是自动热更新注册（见下文 2.4）。

## 2.2 支持的 JSON 形状

`AevatarConnectorConfig.LoadConnectors()` 支持三种写法：

1. 数组

```json
{ "connectors": [ { "name": "...", "type": "...", ... } ] }
```

2. 对象（key 作为 name）

```json
{ "connectors": { "my_connector": { "type": "...", ... } } }
```

3. `definitions` 包裹

```json
{ "connectors": { "definitions": [ ... ] } }
```

解析特性：

- key 大小写不敏感；
- `enabled=false` 条目会被过滤；
- 缺失 `name` 或 `type` 的条目会被过滤；
- `timeoutMs` 会被 clamp 到 `100..300000`；
- `retry` 会被 clamp 到 `0..5`。

## 2.3 字段模型

公共字段（`ConnectorConfigEntry`）：

- `name`：Connector 名称（workflow 用它引用）
- `type`：`http` / `cli` / `mcp`
- `enabled`：默认 `true`
- `timeoutMs`：默认 `30000`
- `retry`：默认 `0`

类型字段：

- `http`：
  - `baseUrl`、`allowedMethods`、`allowedPaths`、`allowedInputKeys`、`defaultHeaders`
- `cli`：
  - `command`、`fixedArguments`、`allowedOperations`、`allowedInputKeys`、`workingDirectory`、`environment`
- `mcp`：
  - `serverName`、`command`、`arguments`、`environment`、`defaultTool`、`allowedTools`、`allowedInputKeys`

## 2.4 启动加载与注册

加载注册链路：

1. Host 启动时 `ConnectorBootstrapHostedService.StartAsync()` 执行；
2. 调用 `ConnectorRegistration.RegisterConnectors(...)`；
3. 从 `AevatarConnectorConfig.LoadConnectors()` 读取配置；
4. 根据 `type` 找到 `IConnectorBuilder`；
5. 成功构建后注册到 `IConnectorRegistry`（默认实现 `InMemoryConnectorRegistry`）。

Builder 当前行为：

- `HttpConnectorBuilder`：`http.baseUrl` 必填；
- `CliConnectorBuilder`：`cli.command` 必填，且不能包含 `://`；
- `MCPConnectorBuilder`：`mcp.command` 必填。

注意：

- `http`、`cli` builder 在 `AddAevatarBootstrap()` 默认注册；
- `mcp` builder 只有在 `AddAevatarAIFeatures(..., options => options.EnableMCPTools = true)` 时注册。

---

## 3. Workflow/Agent 如何使用 Connector

## 3.1 Workflow YAML 角色授权

在 workflow `roles` 中：

```yaml
roles:
  - id: coordinator
    connectors:
      - maker_post_processor
```

`WorkflowParser` 会把该字段解析到 `RoleDefinition.Connectors`。

`WorkflowLoopModule` 在派发 `StepRequestEvent` 时：

- 如果步骤有 `target_role/role` 且该 role 的 `connectors` 非空；
- 会注入 `allowed_connectors=...` 到步骤参数中。

这一步只负责“传授权信息”，不直接执行 connector。

## 3.2 connector_call 执行主链路

`ConnectorCallModule` 处理 `StepRequestEvent`（`step_type == connector_call`）：

1. 读取参数：
   - `connector`（或 `connector_name`）必填
   - `operation`（或 `action`）
   - `retry`、`timeout_ms`、`optional`、`on_missing`、`on_error`
2. 从 `IConnectorRegistry` 解析 connector 名称；
3. 若有 `allowed_connectors`，校验当前 connector 是否在白名单；
4. 构造 `ConnectorRequest` 并调用 `IConnector.ExecuteAsync()`；
5. 根据结果发布 `StepCompletedEvent`。

Ergonomic 别名（解析期归一化到 `connector_call`）：

- `http_get` / `http_post` / `http_put` / `http_delete`
  - 归一化后仍是 `connector_call`
  - 若未显式指定 `method`，会自动补 `GET/POST/PUT/DELETE`
- `mcp_call`
  - 归一化后仍是 `connector_call`
  - 仅写 `tool` 且未写 `operation/action` 时，会自动补 `operation=<tool>`
- `cli_call`
  - 归一化后仍是 `connector_call`
  - 不改变执行语义（仍通过命名 connector 调用）

容错语义：

- 找不到 connector：
  - `optional=true` 或 `on_missing=skip` -> 步骤成功并沿用输入；
  - 否则失败。
- 执行失败：
  - `on_error=continue` -> 步骤成功并沿用输入；
  - 否则失败。
- 重试次数：`attempts = retry + 1`（`retry` 上限 5）。

运行注解会写入 `StepCompletedEvent.Annotations`，例如：

- 通用：`connector.name/type/operation/attempts/timeout_ms/duration_ms`
- skip：`connector.skipped`, `connector.skip_reason`
- continue：`connector.continued_on_error`, `connector.error`
- 具体实现附加字段：`connector.http.*` / `connector.cli.*` / `connector.mcp.*`

## 3.3 三类 Connector 的执行逻辑

### HTTP Connector

- 方法默认 `POST`，可由参数 `method` 覆盖；
- 路径优先 `operation`，其次参数 `path`；
- 必须通过 `allowedMethods/allowedPaths` 白名单；
- 强制校验目标 URL 不能逃逸 `baseUrl` 的 scheme/host/port；
- 可用 `allowedInputKeys` 校验 payload JSON key；
- 返回 HTTP 状态和耗时元数据。

### CLI Connector

- 命令由配置固定，`operation` 作为附加参数（需通过 `allowedOperations`）；
- `payload` 通过 stdin 传入；
- 可用 `allowedInputKeys` 校验 payload JSON key；
- 返回 exit code、耗时等元数据。

### MCP Connector

- 首次调用时连接 MCP Server 并发现工具；
- 工具名解析优先级：
  1) `operation`
  2) 参数 `tool`
  3) `defaultTool`
- 可通过 `allowedTools` 做工具白名单；
- 可通过 `allowedInputKeys` 做 payload key 白名单；
- 返回 server/tool/耗时元数据。

---

## 4. “Agent 如何使用”的关键说明

这里有两个容易混淆的路径：

1. `connector_call` 路径（本文件主线）
   - 执行者是 Workflow 内的 `ConnectorCallModule`；
   - Role 的 `connectors` 仅用于授权校验；
   - 不是由 `RoleGAgent` 直接发起 connector 调用。

2. `tool_call` 路径（Agent 工具系统）
   - 来自 `IAgentToolSource`（例如 MCP Tools、Skills）；
   - 由 `ToolCallModule` 解析工具并执行；
   - 与 `connectors.json` 是两套机制。

当前代码现状（很重要）：

- 独立 `role/agent yaml` 中即使写了 `connectors`，`RoleGAgentFactory.RoleYamlConfig` 目前没有该字段，`InitializeRoleAgentEvent` 也没有对应字段；
- 所以“角色 connector 授权”当前真正生效路径是 **workflow roles + connector_call**。

---

## 5. 最小可用示例

`~/.aevatar/connectors.json`：

```json
{
  "connectors": [
    {
      "name": "maker_post_processor",
      "type": "cli",
      "timeoutMs": 8000,
      "cli": {
        "command": "cat",
        "fixedArguments": [],
        "allowedOperations": [],
        "allowedInputKeys": []
      }
    }
  ]
}
```

workflow YAML：

```yaml
name: connector_demo
roles:
  - id: coordinator
    name: Coordinator
    connectors:
      - maker_post_processor
steps:
  - id: post
    type: connector_call
    role: coordinator
    parameters:
      connector: maker_post_processor
      timeout_ms: "8000"
      retry: "1"
      on_missing: "skip"
      on_error: "continue"
```

---

## 6. 当前实现的几个注意点

- `ConnectorConfigEntry.retry` 已解析但当前没有传入具体 Connector 执行逻辑；实际重试由步骤参数 `retry` 驱动。
- `connectors.json` 配置变更不会自动重建 registry；通常需要重启宿主使新定义生效。
- 角色白名单校验只在 `allowed_connectors` 存在时触发：
  - 即步骤指定了角色且该角色配置了非空 `connectors`。

---

## 7. 从零接入操作清单

适用场景：项目里第一次接入 Connector，目标是稳定跑通 `connector_call`。

1) 确认宿主会加载 Connector

- 需要具备：
  - `IConnectorRegistry`（通常由 `AddAevatarWorkflow()` 注册）；
  - 至少一个 `IConnectorBuilder`（`http/cli` 或 `mcp`）。
- 推荐路径：
  - 使用 `AddAevatarDefaultHost()`（内含 `ConnectorBootstrapHostedService`）；
  - 或者在自定义宿主里手动调用 `ConnectorRegistration.RegisterConnectors(...)`。

2) 准备 `~/.aevatar/connectors.json`

- 至少包含一个 `name` + `type` 完整条目；
- 先用 `cli` 类型做最小验证，外部依赖最少。

示例（最小 CLI）：

```json
{
  "connectors": [
    {
      "name": "demo_cli_dotnet",
      "type": "cli",
      "enabled": true,
      "timeoutMs": 5000,
      "cli": {
        "command": "dotnet",
        "fixedArguments": ["--version"],
        "allowedOperations": [],
        "allowedInputKeys": []
      }
    }
  ]
}
```

3) 在 workflow role 上声明授权白名单

```yaml
roles:
  - id: operator
    name: Operator
    connectors:
      - demo_cli_dotnet
```

4) 在 steps 中调用 `connector_call`

```yaml
steps:
  - id: call_connector
    type: connector_call
    role: operator
    parameters:
      connector: demo_cli_dotnet
      timeout_ms: "5000"
      retry: "1"
      on_missing: "fail"
      on_error: "fail"
```

5) 启动并验证

- 观察启动日志是否出现已加载 connector 名称；
- 执行 workflow，确认 `connector_call` 步骤成功；
- 检查步骤元数据是否包含：
  - `connector.name`
  - `connector.type`
  - `connector.duration_ms`
  - （CLI 场景可选）`connector.cli.exit_code`

6) 常见问题排查

- `connector 'xxx' not found`：通常是配置文件路径不对、`enabled=false`、`type` 无 builder。
- `not allowed for this role`：`roles[].connectors` 未包含该 connector 名称。
- `operation is not allowed`：CLI `allowedOperations` 限制命中。
- `http path/method is not allowed`：HTTP allowlist 限制命中。
- `payload schema violation`：`allowedInputKeys` 与 payload JSON 不匹配。

---

## 8. 5 分钟快跑（零外部依赖）

目标：不用任何外部 API，验证 connector 主链路可用。

1. 写入上面的 `demo_cli_dotnet` 配置到 `~/.aevatar/connectors.json`
2. 准备一个最小 workflow（role + connector_call）
3. 启动宿主（确保执行了 connector 注册）
4. 发送一次运行请求
5. 看到 `dotnet --version` 输出 + `connector.*` 元数据即通过

通过标准：

- `connector_call` 步骤 `Success=true`
- 输出包含 .NET 版本号
- 元数据里有 `connector.name=demo_cli_dotnet`
