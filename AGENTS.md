# Repository Guidelines

## 顶级架构要求（最高优先级）
- 严格分层：`Domain / Application / Infrastructure / Host`，`API` 仅做宿主与组合，不承载核心业务编排。
- 统一投影链路：CQRS 与 AGUI 走同一套 Projection Pipeline，统一入口、一对多分发，避免双轨实现。
- 明确读写分离：`Command -> Event`，`Query -> ReadModel`；异步完成通过事件通知与推送，不在会话内临时拼装流程。
- 严格依赖反转：上层依赖抽象，禁止跨层反向依赖和对具体实现的直接耦合。
- 命名语义优先：项目名、命名空间、目录一致；缩写全大写（如 `LLM/CQRS/AGUI`）；集合语义使用复数。
- 不保留无效层：空转发、重复抽象、无业务价值代码直接删除。
- 变更必须可验证：架构调整需同步文档，且 `build/test` 通过。

## 项目结构与模块组织
- `src/`：生产代码，按能力与分层组织（`Aevatar.Foundation.*`、`Aevatar.Workflow.Core`、`Aevatar.AI.*`、`Aevatar.CQRS.Projection.Abstractions/Core/WorkflowExecution`、`Aevatar.Host.*`）。
- `test/`：与 `src/` 对应的测试项目（单元、集成、API）。
- `docs/`：架构与设计文档；`workflows/`：YAML 工作流定义。
- `tools/`：开发工具；`demos/`：示例与演示程序。

## 构建、测试与本地运行
- `dotnet restore aevatar.slnx --nologo`：还原依赖。
- `dotnet build aevatar.slnx --nologo`：编译全部项目。
- `dotnet test aevatar.slnx --nologo`：运行全量测试。
- `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --collect:"XPlat Code Coverage"`：单项目覆盖率。
- `dotnet run --project src/workflow/Aevatar.Workflow.Host.Api`：启动 Workflow API（`/api/chat`、`/api/ws/chat`）。

## 编码风格与命名规范
- 遵循 `.editorconfig`：UTF-8、LF、4 空格缩进、去除行尾空白。
- 保持 `项目名 = 命名空间 = 目录语义`，推荐模式：`Aevatar.<Layer>.<Feature>`。
- 先抽象后实现；优先接口注入；避免跨层直接调用。
- 公开 API 与领域对象命名要表达业务意图，避免含糊词。
- 把不需要的直接删除, 无需考虑兼容性

## 测试与质量门禁
- 测试栈：xUnit、FluentAssertions、`coverlet.collector`。
- 测试文件命名：`*Tests.cs`，单文件聚焦一个行为域。
- 行为变更必须补测试；重构不得降低关键路径覆盖率。
- CI 守卫：禁止 `GetAwaiter().GetResult()`；禁止 `TypeUrl.Contains(...)` 字符串路由；禁止 `Aevatar.Workflow.Core` 依赖 `Aevatar.AI.Core`。

## 提交与 PR 规范
- 提交信息使用祈使句并聚焦单一目的（如：`Refactor projection pipeline`）。
- PR 必须包含：问题与方案、影响路径、验证命令与结果、相关文档更新。
- 若涉及架构调整，需同时更新 `docs/` 架构文档与示意图。

## 文档

- mermaid 默认指令（所有图统一加在代码块首行）：
  `%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%`
- mermaid 的标签用引号包起来, 如 A2[“RoleGAgent”], 不要 A2[RoleGAgent]. 
