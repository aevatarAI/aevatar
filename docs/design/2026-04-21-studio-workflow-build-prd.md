## Studio Workflow Build PRD

### 背景

`Studio = Team Member Workbench` 已经成立，但当前 `Build -> Workflow` 还偏“可展示”，没有完全收敛成用户可以走通的 authoring workbench。  
Standalone `Aevatar Service Workbench` 里的 `workflow` 模式，本质上承载的是一个完整的工作流构建内循环：

`定义 DAG -> 编辑当前 step -> 切 YAML 精修 -> Save draft -> Dry-run -> 确认可用 -> Continue to Bind`

当前 Studio 里的 Workflow Build 需要严格回到这条主链。

### 产品目标

当用户选择一个 `team member` 且将其实现方式设为 `Workflow` 时，Studio 必须允许用户在同一块 Build workbench 内完成以下事情：

1. 看到当前 member 的 DAG。
2. 新增 step、调整 step 关系、调整布局。
3. 编辑当前选中 step 的核心定义。
4. 在画布视图和 YAML 视图之间切换。
5. 保存当前 workflow draft。
6. 直接对当前 draft 做 dry-run。
7. 在 dry-run 结果不符合预期时，继续回到 DAG / step detail / YAML 修改。
8. 当 draft 可用时，继续进入 `Bind`。

### 用户

- 团队编排者：负责把一个 member 设计成多 step 的可执行行为。
- 调试者：需要快速定位当前 step 配置、输入输出、链路关系是否有问题。
- 发布者：在 Build 内确认 draft 可用后，再进入 Bind 发布。

### 核心用户链条

#### 1. 选中 member

用户先在左侧 `Team members` 中选中一个 member，明确当前要改的是谁。

#### 2. 进入 Build 并选择 Workflow

用户在 `Construction Mode` 中选择 `Workflow`，明确当前 member 用 DAG 编排来实现。

#### 3. 构建 workflow 结构

用户在 `DAG Canvas` 中完成结构定义：

- 看到当前 workflow 节点图。
- 新增 step。
- 选中 step。
- 连线 step 之间的执行关系。
- 拖拽节点，调整布局。
- 一键 auto-layout。

#### 4. 编辑当前 step

用户选中某个 step 后，在 `Step Detail` 中完成当前 step 的定义：

- 编辑 step id。
- 编辑 step type。
- 编辑 target role。
- 编辑 parameters。
- 编辑 next / branches。
- 删除当前 step。

这里的目标不是“只读说明”，而是让用户真正改当前 step。

#### 5. 切换到 YAML

用户在需要精修结构时，可以从画布工作流切到 `YAML` 视图：

- 直接看到当前 workflow draft YAML。
- 直接编辑 YAML。
- 再切回 step detail / canvas 继续工作。

YAML 不是独立页面，只是 Workflow Build 内部的一种精修视图。

#### 6. Save draft

用户在 Build 内保存当前 workflow draft：

- Save 会把当前 YAML 与布局一起保存。
- 保存成功后，用户仍停留在 Build。
- 失败时给出清晰错误。

#### 7. Dry-run 当前 draft

用户在右侧 `Dry-run` 面板直接验证当前 draft：

- 输入 sample input。
- Load fixture。
- Run 当前 workflow draft。
- 看 output / transcript / runtime context。

Dry-run 必须发生在 Build 内，不要求用户先去 Bind。

#### 8. 决定是否进入 Bind

当用户确认 workflow draft 可以工作后，点击 `Continue to Bind` 进入下一个生命周期阶段。

### 页面结构

在 `Build + Workflow` 下，页面内容应固定为以下结构：

#### 顶部

- `Construction Mode`
- `Workflow / Script / GAgent`

#### 主工作台

- 左中主区域：`DAG Canvas`
- 主区域下方：`Step Detail` 或 `Workflow YAML`
- 右侧固定区域：`Dry-run`

#### 底部动作

- `Save draft`
- `Continue to Bind`

### 交互要求

#### Workflow toolbar

在 `DAG Canvas` 上方提供紧凑工具栏：

- `Add step`
- `step type selector`
- `Auto-layout`
- `YAML / Step Detail toggle`

#### Canvas

- 点击节点会切换当前 step。
- 连接节点时会更新 workflow 关系。
- 拖拽节点时会更新 layout。

#### Step detail

- 修改内容后，由用户显式点击 `Apply changes` 生效。
- JSON 类字段若格式不合法，必须阻止写入并给出错误提示。

#### Save

- 只要当前 draft 合法且具备最小保存条件，就允许保存。

#### Dry-run

- 仅在当前 YAML、scope、input 满足条件时允许运行。
- dry-run 输出必须留在当前 Build 页面内。

### 不在本期范围

- Team 级入口管理。
- Script / GAgent 的细化 PRD。
- Observe 阶段的重构。
- 新的 workflow 目录管理器。

### 验收标准

本期完成后，用户必须可以在 `Workflow` Build 中走完这一条链：

1. 打开某个 member 的 Workflow Build。
2. 新增一个 step。
3. 编辑该 step 的核心字段。
4. 在 YAML 与 Step Detail 之间切换。
5. 保存 draft。
6. 输入 dry-run 样例并执行。
7. 看到 dry-run 输出。
8. 点击 `Continue to Bind` 进入 Bind。

若上述链条任一步走不通，则视为本期未完成。
