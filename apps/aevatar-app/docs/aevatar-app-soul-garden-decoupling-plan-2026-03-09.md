# Soul Garden 解耦方案 — `apps/aevatar-app` 通用化改造

> 日期：2026-03-09
> 目标：将 Soul Garden 代码级强关联部分改造为通用 App 框架，Soul Garden 特有内容降级为可替换的「内容包」

---

## 〇、前置分析：Soul Garden 关联度全景扫描

### 0.1 总体结论

`apps/aevatar-app` 整体就是 Soul Garden 的后端实现。generate API 确实是与 Soul Garden 关联最强的核心业务入口，但 Soul Garden 相关代码远不止 generate。

### 0.2 Generate API — Soul Garden 的核心生成链路

四个 generate 端点全部为 Soul Garden 的"植物养成"玩法设计：

| 端点 | Soul Garden 概念 | 功能 |
|------|-----------------|------|
| `POST /api/generate/manifestation` | 愿望/植物 | 根据 `userGoal` 生成 mantra、plantName、plantDescription |
| `POST /api/generate/affirmation` | 肯定语 | 根据 mantra、plantName、userGoal 生成每日肯定语 |
| `POST /api/generate/plant-image` | 植物图片 | 按 stage（seed/sprout/growing/blooming）生成植物图片 |
| `POST /api/generate/speech` | TTS 语音 | 朗读肯定语，生成语音文件 |

涉及的代码文件：

- `Endpoints/GenerateEndpoints.cs` — 端点定义
- `Services/GenerationAppService.cs` / `IGenerationAppService.cs` — 业务编排（含 fallback）
- `Services/AIGenerationAppService.cs` / `IAIGenerationAppService.cs` — 调用 garden_* 工作流
- `Services/Prompts.cs` — ManifestationSystem、Affirmation、PlantImage、Speech 等 prompt
- `Services/FallbackContent.cs` — AI 失败时的植物/肯定语占位内容
- `Filters/GenerateGuardFilter.cs` — plant-image 生成并发控制
- `Concurrency/ImageConcurrencyCoordinator.cs` / 接口 — 生成/上传共享并发池

### 0.3 除 generate 之外的 Soul Garden 相关代码

#### 工作流与连接器（Soul Garden 专属）

| 文件 | 作用 |
|------|------|
| `workflows/garden_content.yaml` | 植物内容生成工作流 |
| `workflows/garden_affirmation.yaml` | 肯定语生成工作流 |
| `workflows/garden_image.yaml` | 植物图片生成工作流 |
| `workflows/garden_speech.yaml` | TTS 语音合成工作流 |
| `connectors/garden.connectors.json` | gemini_imagen、gemini_tts 配置 |
| `config/fallbacks.json` | 植物占位内容（Seed of Potential、Bloom of Hope 等） |

#### 图片上传

- `Endpoints/UploadEndpoints.cs` — `POST /api/upload/plant-image`
- `Services/ImageStorageAppService.cs` — key 格式 `{userId}/{manifestationId}_{stage}_{timestamp}.png`

#### 配置

`appsettings.json` 中明确标识：

- `App:Id: "soul-garden"`、`App:DisplayName: "Soul Garden"`
- `Storage:BucketName: "soulgarden-prod"`
- `Firebase:ProjectId: "soul-21951"`
- `Quota` 限额（MaxSavedEntities: 10 棵植物、MaxEntitiesPerWeek: 3、MaxOperationsPerDay: 3 次浇水）

### 0.4 深度验证：哪些模块实际是通用的？

#### SyncEntityGAgent 及 Helpers

**没有强关联，是通用实体同步框架。** 整个 GAgent 处理的是 `EntityType`、`ClientId`、`Revision`、`Refs`、`Inputs`、`Output`、`State`、`Source` 等完全通用的字段，没有出现 `manifestation`、`affirmation`、`plant`、`stage` 等 Soul Garden 术语。

唯一的代码级泄漏在 `AnonymizeEntity` 方法中硬编码了 `"userGoal"` 字段名——这是 Soul Garden 的业务语义泄漏到了通用层。其余代码完全是通用同步逻辑。

#### SyncRequestValidator / EntityValidator

**没有任何关联，完全通用。** 校验的是 `syncId`、`clientRevision`、`entities`、`clientId`、`entityType`、`revision`、`refs`、`source`(ai|bank|user|edited) 等通用字段。没有校验 `entityType` 的具体值（如 `manifestation`），也没有校验 `stage`。

#### SyncEntityEntry / SyncEntityReducers

**SyncEntityEntry：完全通用的 POCO**，字段是 `ClientId`、`EntityType`、`Revision`、`Refs`、`Inputs`(JsonElement)、`Output`、`State` 等，没有任何 Soul Garden 语义。

**SyncEntityReducers：几乎全部通用**，唯一的泄漏同样在 `AccountDeletedEventSyncEntityReducer` 中硬编码了 `"userGoal"`，与 GAgent 端对称。

#### Firebase Auth / Trial Auth

**代码层面完全通用**，看不出任何 Soul Garden 关联。`FirebaseAuthHandler`、`TrialAuthHandler`、`AppAuthService` 处理的都是标准 JWT/OpenIdConnect 验证逻辑。唯一的关联在配置层面——`appsettings.json` 中 `Firebase:ProjectId: "soul-21951"` 指向 Soul Garden 的 Firebase 项目。这是部署配置绑定，不是代码耦合，换一套 `appsettings.json` 就可以服务其他 app。

### 0.5 关联度总结

| 模块 | 与 Soul Garden 强关联？ | 实际情况 |
|------|----------------------|----------|
| SyncEntityGAgent | **否** | 通用同步框架，仅 `userGoal` 硬编码是泄漏 |
| SyncRequestValidator / EntityValidator | **否** | 完全通用的结构校验 |
| SyncEntityEntry / SyncEntityReducers | **否** | 通用投影，仅 `userGoal` 匿名化是泄漏 |
| Firebase Auth / Trial Auth | **否** | 通用认证框架，仅 appsettings 配置指向 Soul Garden |
| **Generate API + Prompts + Fallback + Workflows** | **是** | Soul Garden 核心业务，manifestation/affirmation/plant/mantra 全部在这里 |
| **ImageStorageAppService** | **是** | key 中包含 `manifestationId` + `stage` |

真正与 Soul Garden **代码级强关联**的只有 Generate 链路（`GenerateEndpoints` → `GenerationAppService` → `AIGenerationAppService` → `Prompts` → `FallbackContent` → `garden_*.yaml` 工作流 → `garden.connectors.json`）以及 `ImageStorageAppService`。

其余模块本质上是**通用 app 框架**，只是被 Soul Garden 使用。代码级别唯一的泄漏点是两处 `"userGoal"` 硬编码（GAgent 和 Reducer 的匿名化逻辑）。

---

## 一、现状诊断：强关联清单

| # | 位置 | 耦合类型 | 严重度 |
|---|------|----------|--------|
| L1 | `Prompts.cs` — ManifestationSystem / Affirmation / PlantImage / Speech | 硬编码 prompt 模板 | **高** |
| L2 | `GenerateEndpoints.cs` — 端点路径 + 请求/响应模型 | 硬编码 `manifestation`/`affirmation`/`plant-image`/`speech` 路径与 DTO | **高** |
| L3 | `AIGenerationAppService.cs` — 工作流名称 | 硬编码 `garden_content`/`garden_affirmation` | **高** |
| L4 | `AIGenerationAppService.cs` — 连接器名称 | 硬编码 `gemini_imagen`/`gemini_tts` | **高** |
| L5 | `AIGenerationAppService.cs` — JSON 解析 | 硬编码 `mantra`/`plantName`/`plantDescription` 字段 | **高** |
| L6 | `FallbackContent.cs` — 默认内容 | 硬编码植物名、mantra、affirmation | **中** |
| L7 | `IFallbackContent.cs` — 接口语义 | 使用 `ManifestationFallback`/`PlantName` 等 Soul Garden 术语 | **中** |
| L8 | `GenerateEndpoints.cs` — 校验规则 | 硬编码 `stage: seed\|sprout\|growing\|blooming`、`trigger` 枚举 | **中** |
| L9 | `ImageStorageAppService.cs` — key 格式 | `{userId}/{manifestationId}_{stage}_{timestamp}.png` | **中** |
| L10 | `UploadEndpoints.cs` — 路径 + 校验 | `/api/upload/plant-image`、stage 硬编码 | **中** |
| L11 | `SyncEntityGAgent.Helpers.cs:210` | AnonymizeEntity 硬编码 `userGoal` 字段名 | **低** |
| L12 | `SyncEntityReducers.cs:153` | AccountDeletedEvent reducer 硬编码 `userGoal` | **低** |
| L13 | `appsettings.json` | `soul-garden`/`soulgarden-prod`/`soul-21951` 等配置值 | 配置级（不改代码） |

---

## 二、设计目标

1. **框架与内容分离**：`aevatar-app` 成为通用的「AI 内容生成 App 框架」，Soul Garden 仅是一种内容配置
2. **最小化侵入**：不重写整体架构，只把 Soul Garden 语义从代码提升到配置
3. **扩展对称性**：新增一种 App（如 Daily Journal）应无需修改框架代码，只需新增配置包
4. **保持可验证**：每步改动 build/test 通过

---

## 三、分层方案

### 第 1 层：Prompt 模板外部化（解决 L1）

**现状**：`Prompts.cs` 硬编码所有 prompt

**方案**：将 prompt 模板移入配置文件，运行时加载

```
config/prompts.json
```

```json
{
  "contentGeneration": {
    "systemPrompt": "You are a wise spiritual guide...",
    "userPromptTemplate": "User's goal: {userGoal}"
  },
  "affirmation": {
    "promptTemplate": "Generate a short... \"{mantra}\" ... \"{userGoal}\" ... \"{plantName}\"..."
  },
  "image": {
    "stages": {
      "seed": "A cute, single magical seed of a {name} floating...",
      "sprout": "A tiny, adorable sprout of a {name}...",
      "growing": "A happy, growing magical plant ({name})...",
      "blooming": "A magnificent, fully bloomed {name}..."
    },
    "defaultStyle": "SOLID MAGENTA #FF00FF BACKGROUND..."
  },
  "speech": {
    "promptTemplate": "Speak this affirmation in a soothing... \"{text}\""
  }
}
```

**代码改动**：

- `Prompts.cs` → `PromptTemplateService`，从 `IOptions<PromptOptions>` 读取模板，运行时替换变量
- 删除所有硬编码 prompt 常量

```csharp
public sealed class PromptOptions
{
    public ContentGenerationPrompt ContentGeneration { get; set; } = new();
    public AffirmationPrompt Affirmation { get; set; } = new();
    public ImagePrompt Image { get; set; } = new();
    public SpeechPrompt Speech { get; set; } = new();
}

public sealed class ContentGenerationPrompt
{
    public string SystemPrompt { get; set; } = "";
    public string UserPromptTemplate { get; set; } = "User's goal: {userGoal}";
}

public sealed class ImagePrompt
{
    public Dictionary<string, string> StageTemplates { get; set; } = new();
    public string DefaultStyle { get; set; } = "";
}
```

### 第 2 层：工作流名称与连接器可配置化（解决 L3、L4）

**现状**：`AIGenerationAppService` 硬编码 `"garden_content"`、`"garden_affirmation"`、`"gemini_imagen"`、`"gemini_tts"`

**方案**：通过 `IOptions<GenerationWorkflowOptions>` 注入

```csharp
public sealed class GenerationWorkflowOptions
{
    public string ContentWorkflow { get; set; } = "garden_content";
    public string AffirmationWorkflow { get; set; } = "garden_affirmation";
    public string ImageConnector { get; set; } = "gemini_imagen";
    public string ImageModelPath { get; set; } = "/v1beta/models/gemini-2.5-flash-image:generateContent";
    public string SpeechConnector { get; set; } = "gemini_tts";
    public string SpeechModelPath { get; set; } = "/v1beta/models/gemini-2.5-flash-preview-tts:generateContent";
}
```

**配置（appsettings.json）**：

```json
{
  "App": {
    "GenerationWorkflow": {
      "ContentWorkflow": "garden_content",
      "AffirmationWorkflow": "garden_affirmation",
      "ImageConnector": "gemini_imagen",
      "SpeechConnector": "gemini_tts"
    }
  }
}
```

**代码改动**：

- `AIGenerationAppService` 构造注入 `IOptions<GenerationWorkflowOptions>`
- 替换所有 `"garden_content"` → `_options.Value.ContentWorkflow` 等

### 第 3 层：端点与 DTO 语义通用化（解决 L2、L5、L8、L10）

**现状**：端点路径用 `manifestation`/`plant-image`，DTO 用 `ManifestationRequest`、`PlantImageRequest`，校验硬编码 stage 枚举

**方案 A（推荐 — 渐进重命名）**：保持 4 个端点结构不变，只做语义中性重命名

| 现有名称 | 通用化名称 | 说明 |
|----------|-----------|------|
| `/api/generate/manifestation` | `/api/generate/content` | 主题内容生成 |
| `ManifestationRequest` | `ContentRequest` | |
| `ManifestationResult` | `ContentResult` | |
| `mantra` | `title` (或保留业务字段由 JSON schema 定义) | |
| `plantName` | `name` | |
| `plantDescription` | `description` | |
| `/api/generate/plant-image` | `/api/generate/image` | |
| `PlantImageRequest` | `ImageRequest` | |
| `/api/upload/plant-image` | `/api/upload/image` | |
| `UploadPlantImageRequest` | `UploadImageRequest` | |
| `stage: seed\|sprout\|growing\|blooming` | `stage` 从配置读取合法值 | |
| `trigger: daily_interaction\|evolution\|watering\|manual` | `trigger` 从配置读取合法值 | |

**校验通用化**：stage/trigger 合法值不再硬编码，从配置读取

```csharp
public sealed class ContentTypeOptions
{
    public List<string> ValidStages { get; set; } = ["seed", "sprout", "growing", "blooming"];
    public List<string> ValidTriggers { get; set; } = ["daily_interaction", "evolution", "watering", "manual"];
}
```

端点中：

```csharp
// 替换前
if (stage is not ("seed" or "sprout" or "growing" or "blooming"))
    return Results.BadRequest(new { error = "stage must be seed|sprout|growing|blooming" });

// 替换后
if (!contentTypeOptions.Value.ValidStages.Contains(stage))
    return Results.BadRequest(new { error = $"stage must be one of: {string.Join("|", contentTypeOptions.Value.ValidStages)}" });
```

**方案 B（更激进 — 动态端点）**：从配置自动注册 N 种生成类型的端点。复杂度高，暂不推荐。

### 第 4 层：响应解析策略化（解决 L5）

**现状**：`ParseManifestationJson` 硬编码 `mantra`/`plantName`/`plantDescription` 字段名

**方案**：将输出字段映射定义到配置

```csharp
public sealed class ContentOutputMapping
{
    public List<string> RequiredFields { get; set; } = ["mantra", "plantName", "plantDescription"];
}
```

解析方法改为从配置读取字段名、校验必填项，返回 `Dictionary<string, string>` 而非固定 record：

```csharp
// 替换前
public sealed record ManifestationResult(string Mantra, string PlantName, string PlantDescription);

// 替换后
public sealed record ContentResult(IReadOnlyDictionary<string, string> Fields)
{
    public string? GetField(string name) =>
        Fields.TryGetValue(name, out var value) ? value : null;
}
```

或更简单的保留 record、但字段名通用化：

```csharp
public sealed record ContentResult(string Title, string Name, string Description);
```

### 第 5 层：Fallback 内容外部化（解决 L6、L7）

**现状**：`FallbackContent.cs` 硬编码默认植物/肯定语，接口用 Soul Garden 术语

**方案**：

1. 术语重命名：`ManifestationFallback` → `ContentFallback`、`PlantName` → `Name`、`Mantra` → `Title`
2. 默认值全部移入 `config/fallbacks.json`（已存在），代码中的默认值列表删除
3. 接口重命名：

```csharp
// 替换前
public interface IFallbackContent
{
    IReadOnlyList<ManifestationFallback> Plants { get; }
    ManifestationFallback GetManifestationFallback();
    ManifestationFallback GetManifestationFixedFallback(string userGoal);
}

// 替换后
public interface IFallbackContentProvider
{
    IReadOnlyList<ContentFallback> Items { get; }
    ContentFallback GetRandomFallback();
    ContentFallback GetFixedFallback(string userInput);
    string GetAffirmationFallback();
    string PlaceholderImage { get; }
}
```

### 第 6 层：图片存储 key 通用化（解决 L9）

**现状**：`GenerateKey` 格式为 `{userId}/{manifestationId}_{stage}_{timestamp}.png`

**方案**：将 `manifestationId` 参数名改为 `entityId`，保持 key 格式不变但语义通用

```csharp
// 替换前
public static string GenerateKey(string userId, string manifestationId, string stage) =>
    $"{userId}/{manifestationId}_{stage}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";

// 替换后
public static string GenerateKey(string userId, string entityId, string variant) =>
    $"{userId}/{entityId}_{variant}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";
```

接口同步更新：

```csharp
// 替换前
Task<UploadResult> UploadAsync(string userId, string manifestationId, string stage, ...);

// 替换后
Task<UploadResult> UploadAsync(string userId, string entityId, string variant, ...);
```

### 第 7 层：匿名化字段可配置化（解决 L11、L12）

**现状**：`AnonymizeEntity` 和 `AccountDeletedEventSyncEntityReducer` 硬编码 `"userGoal"`

**方案**：通过配置定义匿名化时需要保留/清除的字段

```csharp
public sealed class AnonymizationOptions
{
    public Dictionary<string, string> PreservedFieldDefaults { get; set; } = new()
    {
        ["userGoal"] = "[deleted]"
    };
}
```

代码改动：

```csharp
// SyncEntityGAgent.Helpers.cs — 替换前
if (entity.Inputs is not null)
{
    entity.Inputs.Fields.Clear();
    entity.Inputs.Fields["userGoal"] = Value.ForString("[deleted]");
}

// 替换后 — 通过选项注入
if (entity.Inputs is not null)
{
    entity.Inputs.Fields.Clear();
    foreach (var (key, defaultValue) in _anonymizationOptions.PreservedFieldDefaults)
        entity.Inputs.Fields[key] = Value.ForString(defaultValue);
}
```

Reducer 端同理，从 `IOptions<AnonymizationOptions>` 读取。

---

## 四、配置包结构

改造完成后，Soul Garden 的所有特有内容集中在配置包中：

```
apps/aevatar-app/
├── config/
│   ├── prompts.json           # L1: prompt 模板
│   ├── fallbacks.json         # L6: fallback 内容
│   ├── content-types.json     # L8: stage/trigger 合法值、输出字段映射
│   └── anonymization.json     # L11/L12: 匿名化字段规则
├── connectors/
│   └── garden.connectors.json # L4: connector 配置（已是外部配置）
├── workflows/
│   ├── garden_content.yaml    # L3: 工作流定义（已是外部配置）
│   ├── garden_affirmation.yaml
│   ├── garden_image.yaml
│   └── garden_speech.yaml
└── appsettings.json           # L2/L3/L4: 工作流名/connector名/端点映射
```

新增一种 App（如 Daily Journal），只需：

1. 复制配置包目录，修改 prompts/fallbacks/content-types
2. 创建对应工作流 YAML
3. 修改 appsettings.json 指向新配置
4. **零代码修改**

---

## 五、实施路径（推荐分步执行）

| 阶段 | 范围 | 文件数 | 风险 | 优先级 |
|------|------|--------|------|--------|
| Phase 1 | L11 + L12: `userGoal` 硬编码消除 | 2 文件 | 低 | **P0** |
| Phase 2 | L3 + L4: 工作流/连接器名可配置 | 2 文件 | 低 | **P0** |
| Phase 3 | L1: Prompt 模板外部化 | 2 文件 | 低 | **P1** |
| Phase 4 | L9: ImageStorage key 参数名通用化 | 3 文件 | 低 | **P1** |
| Phase 5 | L2 + L5 + L8 + L10: 端点/DTO/校验通用化 | 5 文件 | 中 | **P2** |
| Phase 6 | L6 + L7: Fallback 术语重命名 | 4 文件 | 中 | **P2** |

### Phase 1 详细步骤（userGoal 硬编码消除）

1. 新增 `AnonymizationOptions` 类（放在 `Services/` 或 `Contracts/`）
2. 修改 `SyncEntityGAgent.Helpers.cs:200-215`，注入选项
3. 修改 `SyncEntityReducers.cs:148-157`，注入选项
4. 在 DI 注册中绑定 `AnonymizationOptions` 到配置节
5. 在 `appsettings.json` 或 `config/anonymization.json` 中配置默认值
6. 补充测试覆盖匿名化逻辑
7. 执行 `dotnet build && dotnet test`

### Phase 2 详细步骤（工作流/连接器名可配置）

1. 新增 `GenerationWorkflowOptions` 类
2. 修改 `AIGenerationAppService.cs`：
   - 构造注入 `IOptions<GenerationWorkflowOptions>`
   - 替换 `"garden_content"` → `_options.ContentWorkflow`
   - 替换 `"garden_affirmation"` → `_options.AffirmationWorkflow`
   - 替换 `"gemini_imagen"` → `_options.ImageConnector`
   - 替换 `"gemini_tts"` → `_options.SpeechConnector`
   - 替换模型路径硬编码
3. 在 DI 注册中绑定配置
4. 在 `appsettings.json` 添加 `GenerationWorkflow` 配置节
5. 已有测试中 mock 选项
6. 执行 `dotnet build && dotnet test`

---

## 六、不改动的部分（已通用）

以下模块经源码验证为**通用代码**，本次不需要改动：

| 模块 | 理由 |
|------|------|
| SyncEntityGAgent 核心逻辑 | 通用实体同步，无 Soul Garden 术语 |
| SyncRequestValidator / EntityValidator | 通用结构校验 |
| SyncEntityEntry | 通用 POCO |
| SyncEntityReducers 核心逻辑 | 通用投影映射 |
| Firebase Auth / Trial Auth | 通用认证框架 |
| ImageConcurrencyCoordinator | 通用并发控制 |
| GenerateGuardFilter | 通用并发 filter |

---

## 七、风险与注意事项

1. **客户端兼容性**：端点路径和响应字段重命名（Phase 5）会破坏客户端 API 契约，需要与前端同步或提供兼容层
2. **渐进式迁移**：可以先保留旧端点路径（alias），新路径并行上线，逐步迁移
3. **测试覆盖**：每个 Phase 完成后必须确保现有测试全部通过
4. **配置默认值**：通用化后所有配置项必须有合理默认值，确保零配置也能启动（用 Soul Garden 默认值）
