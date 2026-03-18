# Projection ReadModel Protobuf 序列化统一重构方案

> **状态**：提案
> **日期**：2026-03-17
> **影响范围**：`Aevatar.CQRS.Projection.Stores.Abstractions`、所有 Projection Provider、所有 ReadModel 实现

---

## 1. 问题陈述

### 1.1 现状

当前投影系统存在**双轨序列化**问题：

| 组件 | 序列化方式 | 具体用法 |
|---|---|---|
| Elasticsearch Provider | `System.Text.Json` | `JsonSerializer.Serialize/Deserialize` 存取 ReadModel |
| Neo4j Provider | `System.Text.Json` | 属性字典 JSON 编码/解码 |
| InMemory Provider | 手写 `DeepClone()` | 要求所有 ReadModel 实现 `IProjectionReadModelCloneable<T>` |
| Scripting ReadModel | 自定义 `JsonConverter` (~434 行) | 桥接 Protobuf `IMessage` 与 `System.Text.Json` |
| Workflow ReadModel | Protobuf + partial class 桥接 | `Timestamp` ↔ `DateTimeOffset` 手动转换 |
| Platform ReadModel | 手写 POCO + 手写 `DeepClone()` | 无 proto 定义 |

**核心矛盾**：

1. **AGENTS.md 明确要求所有序列化统一 Protobuf**，但 Provider 层仍依赖 `System.Text.Json`。
2. 11 个 ReadModel 已经是 proto 生成的 `IMessage`，但要经过自定义 `JsonConverter` 二次转换才能存入 Elasticsearch/Neo4j——增加了约 **434 行桥接代码**（仅 Scripting）。
3. 7 个 Platform ReadModel 是手写 POCO，每个需要手写 `DeepClone()` 实现。
4. `IProjectionReadModelCloneable<T>` 接口增加了每个 ReadModel 的实现负担，而 `IMessage` 已内置 `Clone()` 方法。

### 1.2 目标

- 所有 ReadModel 统一为 Protobuf `IMessage`，序列化由 Protobuf 原生能力承担。
- Provider 层使用 `Google.Protobuf.JsonFormatter` / `JsonParser` 替代 `System.Text.Json`。
- 消除 `IProjectionReadModelCloneable<T>` 接口，使用 `IMessage.Clone()`。
- 删除所有自定义 `JsonConverter` 桥接代码。

---

## 2. 设计决策

### 2.1 `IProjectionReadModel` 接口演进

**决策**：在 `IProjectionReadModel` 上增加 `IMessage` 约束。

```csharp
// 变更前
public interface IProjectionReadModel
{
    string Id { get; }
    string ActorId { get; }
    long StateVersion { get; }
    string LastEventId { get; }
    DateTimeOffset UpdatedAt { get; }
}

// 变更后
public interface IProjectionReadModel : IMessage
{
    string Id { get; }
    string ActorId { get; }
    long StateVersion { get; }
    string LastEventId { get; }
    DateTimeOffset UpdatedAt { get; }
}
```

**理由**：
- 接口级约束最彻底，编译器强制所有实现必须是 proto 生成类。
- 避免在每个 Provider 泛型约束处重复 `where TReadModel : IMessage`。
- `Stores.Abstractions` 新增唯一依赖：`Google.Protobuf`（纯抽象包，无运行时副作用）。

**`DateTimeOffset UpdatedAt` 保留**：
- 接口层保持 `DateTimeOffset`，对上层消费者友好。
- Proto 定义层用 `google.protobuf.Timestamp updated_at_utc_value` 存储。
- Partial class 提供转换属性（已有 11 个 ReadModel 验证过此模式）。

### 2.2 消除 `IProjectionReadModelCloneable<T>`

**决策**：删除 `IProjectionReadModelCloneable<T>` 接口，统一使用 `IMessage.Clone()`。

```csharp
// InMemory Provider 克隆方法
// 变更前
private TReadModel Clone(TReadModel source)
{
    if (source is IProjectionReadModelCloneable<TReadModel> cloneable)
        return cloneable.DeepClone();
    throw new InvalidOperationException(...);
}

// 变更后
private TReadModel Clone(TReadModel source)
    => (TReadModel)((IMessage)source).Clone();
```

**理由**：
- `IMessage.Clone()` 是 Protobuf 原生深拷贝，基于字段逐一复制，语义正确且高效。
- 删除 18 个 ReadModel 类的手写 `DeepClone()` 实现。
- 消除 InMemory Provider 对 `IProjectionReadModelCloneable` 的运行时检查。

### 2.3 Provider 序列化替换

#### 2.3.1 Elasticsearch Provider

**变更前**（`ElasticsearchProjectionDocumentStore`）：

```csharp
private readonly JsonSerializerOptions _jsonOptions = new()
{
    PropertyNameCaseInsensitive = true,
};

// 写入
var json = JsonSerializer.Serialize(readModel, _jsonOptions);

// 读取
var result = JsonSerializer.Deserialize<TReadModel>(json, _jsonOptions);
```

**变更后**：

```csharp
private static readonly JsonFormatter Formatter = new(
    JsonFormatter.Settings.Default.WithFormatDefaultValues(true));

private static readonly JsonParser Parser = new(
    JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

// 写入
var json = Formatter.Format((IMessage)readModel);

// 读取（需要 MessageDescriptor）
var msg = (TReadModel)(object)Parser.Parse(json, descriptorForT);
```

**字段命名**：Protobuf `JsonFormatter` 默认输出 **camelCase**（如 `actorId`、`stateVersion`）。Elasticsearch 不区分大小写时无影响；如需保持 proto 原始命名（`actor_id`），可配置 `WithPreserveProtoFieldNames(true)`。

**关键变更点**：
- `ElasticsearchProjectionDocumentStore` 构造函数需要接收 `MessageDescriptor`（或通过泛型约束 `IMessage<TReadModel>` 获取 `TReadModel.Descriptor`）。
- 查询结果反序列化使用 `JsonParser.Parse()`。
- 删除防御性克隆的 JSON 往返（`Serialize → Deserialize`），改为 `IMessage.Clone()`。

#### 2.3.2 Neo4j Provider

Graph 节点/边的属性字典 `Dictionary<string, string>` 不是 `IProjectionReadModel`，**保持 `System.Text.Json` 不变**。

理由：
- 属性字典是简单 `Dictionary<string, string>`，不涉及 proto 消息。
- Neo4j Provider 当前不存储 `IProjectionReadModel` 文档，只存储 Graph 结构。
- 改动 ROI 低，风险高。

#### 2.3.3 InMemory Provider

- Document Store：用 `IMessage.Clone()` 替代 `IProjectionReadModelCloneable`（见 2.2）。
- Graph Store：属性字典的 JSON 克隆保持不变（同 Neo4j 理由）。

### 2.4 Platform POCO 迁移到 Proto

7 个 Platform ReadModel 需要新建 `.proto` 定义：

| ReadModel | 字段数 | 复杂度 |
|---|---|---|
| `ServiceCatalogReadModel` | 12 + 嵌套 `ServiceCatalogEndpointReadModel` | 中 |
| `ServiceRevisionCatalogReadModel` | 8 | 低 |
| `ServiceDeploymentCatalogReadModel` | 7 | 低 |
| `ServiceServingSetReadModel` | 9 | 低 |
| `ServiceRolloutReadModel` | 12 | 低 |
| `ServiceTrafficViewReadModel` | 9 | 低 |
| `ServiceConfigurationReadModel` | 9 | 低 |

**迁移模式**（沿用 Workflow/Scripting 已验证模式）：

1. 在 `src/platform/Aevatar.GAgentService.Projection/` 下新建 `service_projection_read_models.proto`。
2. Proto 字段使用 `google.protobuf.Timestamp` 代替 `DateTimeOffset`。
3. Partial class 实现 `IProjectionReadModel` 接口，提供 `DateTimeOffset UpdatedAt` 包装属性。
4. 删除原 POCO 类和手写 `DeepClone()` 实现。

### 2.5 删除自定义 JsonConverter

重构完成后，以下文件可以**整体删除**：

| 文件 | 行数 | 说明 |
|---|---|---|
| `src/Aevatar.Scripting.Projection/Serialization/ScriptProjectionReadModelJsonConverters.cs` | ~434 | 5 个自定义 JsonConverter + 支持类 |
| Partial class 上的 `[JsonConverter(...)]` 属性 | — | 每个 Scripting ReadModel 的注解 |
| Partial class 上的 `[JsonIgnore]` 属性 | — | Timestamp 包装属性上的屏蔽注解 |

**理由**：Protobuf `JsonFormatter` 原生处理所有 proto 类型（`Timestamp`、`Any`、`ByteString`、`RepeatedField`、`MapField`），不需要手动桥接。

---

## 3. 影响分析

### 3.1 变更文件清单

| 层级 | 项目 | 变更类型 | 预估影响 |
|---|---|---|---|
| **抽象层** | `CQRS.Projection.Stores.Abstractions` | 接口变更 + 新增 Protobuf 依赖 | `IProjectionReadModel` 加 `: IMessage`；删除 `IProjectionReadModelCloneable<T>` |
| **Elasticsearch** | `CQRS.Projection.Providers.Elasticsearch` | 序列化替换 | `JsonFormatter/JsonParser` 替代 `System.Text.Json`；新增 Protobuf 依赖 |
| **InMemory** | `CQRS.Projection.Providers.InMemory` | 克隆方式变更 | `IMessage.Clone()` 替代 `IProjectionReadModelCloneable` |
| **Neo4j** | `CQRS.Projection.Providers.Neo4j` | 无变更 | Graph 属性字典不受影响 |
| **Workflow** | `Workflow.Projection` | 清理 | 删除 `IProjectionReadModelCloneable` 实现、`[JsonIgnore]` 注解 |
| **Scripting** | `Scripting.Projection` | 大量删除 | 删除 ~434 行 JsonConverter；删除 `[JsonConverter]` 注解 |
| **Platform** | `GAgentService.Projection` / `Governance.Projection` | POCO → Proto | 新建 `.proto` 文件；删除 POCO 类和 `DeepClone()` |
| **测试** | 多个测试项目 | 适配 | 更新序列化断言；删除 JsonConverter 测试 |

### 3.2 依赖变更

```
Aevatar.CQRS.Projection.Stores.Abstractions
  新增: Google.Protobuf (PackageReference)

Aevatar.CQRS.Projection.Providers.Elasticsearch
  新增: Google.Protobuf (PackageReference)
  删除: 无（System.Text.Json 是框架内置，但不再使用）

Aevatar.CQRS.Projection.Providers.InMemory
  删除: 对 IProjectionReadModelCloneable 的运行时依赖
```

### 3.3 Elasticsearch 索引兼容性

**关键风险**：字段命名格式变更可能导致已有索引不兼容。

| 场景 | 影响 | 缓解措施 |
|---|---|---|
| 新索引 | 无影响 | `JsonFormatter` 输出 camelCase，Elasticsearch 自动映射 |
| 已有索引（生产） | 字段名可能不一致 | 迁移期使用 `WithPreserveProtoFieldNames(true)` 保持 snake_case |
| `DateTimeOffset` → `Timestamp` | 格式从 `System.Text.Json` 的 ISO 8601 变为 Protobuf 的 RFC 3339 | 两者兼容（ES 的 `date` 类型均可解析） |

**建议**：配置 `JsonFormatter.Settings.Default.WithPreserveProtoFieldNames(true).WithFormatDefaultValues(true)` 保持与现有索引的 snake_case 兼容。

### 3.4 `MessageDescriptor` 获取策略

`JsonParser.Parse()` 需要 `MessageDescriptor`。两种方案：

**方案 A：泛型约束提升为 `IMessage<TReadModel>`**

```csharp
public class ElasticsearchProjectionDocumentStore<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel, IMessage<TReadModel>, new()
```

优点：可通过 `new TReadModel().Descriptor` 获取 descriptor。
缺点：增加 `new()` 约束，所有 Provider 泛型约束需同步更新。

**方案 B：构造函数注入 `MessageDescriptor`**

```csharp
public ElasticsearchProjectionDocumentStore(
    ...,
    MessageDescriptor descriptor)
```

优点：不改泛型约束，更灵活。
缺点：DI 注册时需额外传入。

**推荐方案 A**：proto 生成类天然满足 `IMessage<T>` 和 `new()` 约束，编译器可完全校验。

---

## 4. 实施计划

### Phase 1：抽象层变更（破坏性，一次完成）

1. `IProjectionReadModel` 增加 `: IMessage` 约束。
2. `Stores.Abstractions` csproj 新增 `Google.Protobuf` 依赖。
3. 删除 `IProjectionReadModelCloneable<T>` 接口。
4. 编译验证：此时所有 ReadModel 实现都会编译失败（预期）。

### Phase 2：Platform POCO → Proto（消除编译错误）

5. 为 7 个 Platform ReadModel 创建 `.proto` 定义。
6. 创建 partial class 实现 `IProjectionReadModel` 接口。
7. 删除原 POCO 类和 `DeepClone()` 实现。
8. 编译验证：Platform 部分恢复。

### Phase 3：清理现有 Proto ReadModel（消除编译错误）

9. 删除所有 ReadModel 上的 `IProjectionReadModelCloneable<T>` 实现和 `DeepClone()` 方法。
10. 删除 `[JsonConverter]`、`[JsonIgnore]` 注解。
11. 编译验证：Scripting/Workflow 部分恢复。

### Phase 4：Provider 序列化替换

12. InMemory Document Store：`Clone()` 改为 `(TReadModel)((IMessage)source).Clone()`。
13. Elasticsearch Document Store：`System.Text.Json` 替换为 `JsonFormatter` / `JsonParser`。
14. 更新泛型约束（如果选方案 A）。
15. 更新 DI 注册代码。

### Phase 5：删除桥接代码

16. 删除 `ScriptProjectionReadModelJsonConverters.cs`（~434 行）。
17. 删除其他 `[JsonIgnore]` / `[JsonConverter]` 残留。
18. 清理不再使用的 `using System.Text.Json` 引用。

### Phase 6：测试适配

19. 更新 `ScriptReadModelDocumentJsonConverterTests.cs` → 改为 Protobuf JSON 序列化断言。
20. 更新 `ElasticsearchProjectionDocumentStoreBehaviorTests.cs` → 适配新序列化格式。
21. 更新 `ServiceReadModelCloneTests.cs` → 验证 `IMessage.Clone()` 行为。
22. 更新 `WorkflowProjectionReadModelCoverageTests.cs` → 删除 `DeepClone` 测试。
23. 运行完整测试套件：`dotnet test aevatar.slnx --nologo`。

### Phase 7：门禁验证

24. `bash tools/ci/architecture_guards.sh`
25. `bash tools/ci/solution_split_guards.sh`
26. `bash tools/ci/solution_split_test_guards.sh`
27. `bash tools/ci/projection_route_mapping_guard.sh`
28. `bash tools/ci/projection_state_version_guard.sh`

---

## 5. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| Elasticsearch 索引字段名不兼容 | 中 | 高 | 使用 `WithPreserveProtoFieldNames(true)` 保持 snake_case；或约定新索引名触发重建 |
| `IMessage` 约束导致第三方/外部 ReadModel 无法实现 | 低 | 中 | 当前无外部 ReadModel 消费者；未来如需扩展可引入 adapter |
| Protobuf JSON 对 `null` / 默认值的处理差异 | 中 | 中 | `WithFormatDefaultValues(true)` 确保空字段输出；测试覆盖边界情况 |
| `Any` 类型（Scripting ReadModel）的 JSON 格式差异 | 中 | 中 | Protobuf `JsonFormatter` 原生支持 `Any`（输出 `@type` + 内嵌字段），比手写转换器更规范 |
| 并行开发冲突（其他分支修改 ReadModel） | 中 | 低 | 在单独分支完成，PR 前 rebase |

---

## 6. 预期收益量化

| 指标 | 变更前 | 变更后 | 差异 |
|---|---|---|---|
| 自定义 JsonConverter 代码行数 | ~434 行（Scripting） | 0 | **-434 行** |
| 手写 DeepClone 实现数 | 18 个 | 0 | **-18 个** |
| IProjectionReadModelCloneable 接口 | 1 接口 + 18 实现 | 删除 | **-19 个类型引用** |
| 序列化方式数 | 3（STJ + 自定义 Converter + Proto） | 1（Proto） | **统一** |
| Stores.Abstractions 外部依赖 | 0 | 1（Google.Protobuf） | +1 |
| 新增 .proto 文件 | 0 | 1（Platform ReadModel） | +1 |

---

## 7. 验证标准

- [ ] `dotnet build aevatar.slnx --nologo` 编译通过。
- [ ] `dotnet test aevatar.slnx --nologo` 全量测试通过。
- [ ] `bash tools/ci/architecture_guards.sh` 门禁通过。
- [ ] `bash tools/ci/solution_split_test_guards.sh` 分片测试通过。
- [ ] Elasticsearch Provider E2E 测试通过（`bash tools/ci/projection_provider_e2e_smoke.sh`）。
- [ ] 无 `System.Text.Json` 残留在 Projection Provider 的 ReadModel 序列化路径中（Graph 属性字典除外）。
- [ ] 所有 ReadModel 实现类均为 proto 生成类 + partial class。
- [ ] `IProjectionReadModelCloneable<T>` 接口文件已删除。
- [ ] `ScriptProjectionReadModelJsonConverters.cs` 文件已删除。
