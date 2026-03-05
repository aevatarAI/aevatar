# Workflow Web Playground Prompt Cookbook

下面是可直接复制到 `Playground -> Auto` 的提示词。目标是让模型更稳定地产出**结构完整、分支清晰、可交互迭代**的复杂 workflow YAML。

## 使用建议

- 先用一个场景提示词生成第一版。
- 如果你不满意，在审批阶段点击“继续优化”，追加你的约束。
- 提示词里尽量写清楚：角色数量、步骤数量、必须包含的 step 类型、失败分支与重试策略。
- 推荐加一句：只输出一个 `yaml` 代码块，不要解释。

## 通用复杂工作流模板（先收藏）

```text
你是资深 Workflow Architect。请将我的需求转成一个复杂但可运行的 workflow YAML。

硬性要求：
1) 只输出一个 ```yaml 代码块，不要解释
2) 至少 6 个 roles，至少 18 个 steps
3) 必须包含：assign、llm_call、parallel、map_reduce、evaluate、reflect、conditional、cache、checkpoint、human_approval、delay、emit
4) 至少 2 处并行分支、至少 3 处条件分支
5) 至少 1 个“拒绝 -> 优化 -> 再审批”的闭环
6) role 里只写 id/name/system_prompt，不写 provider/model
7) step id 必须唯一，branches/next 指向的 step 必须存在
8) 必须设计失败分支（fallback）和超时处理，不允许直接中断

业务需求：
<把你的业务描述写在这里>
```

## 场景提示词（可直接粘贴）

### 1) 新品发布前 48 小时决策引擎

```text
请设计一个 workflow：输入是新品介绍文案，输出是 go/no-go 决策包。
要求：
- roles 至少 6 个（planner/researcher/risk_reviewer/compliance_reviewer/copywriter/release_manager）
- steps 至少 20 个
- 必须包含 parallel + map_reduce + evaluate + human_approval
- 产出变量：risk_report、launch_plan、go_no_go_decision
- 审批拒绝后进入优化分支，再回到审批
- 最终输出结构化总结（风险、方案、结论）
只输出一个 ```yaml 代码块，不要解释。
```

### 2) 线上故障应急指挥（Incident Commander）

```text
请生成一个“线上故障响应”workflow。
输入：告警文本和初始日志片段。
要求：
- roles：commander/sre/backend/frontend/security/comms 至少 6 个
- 包含：告警分级、并行排查、根因假设投票、恢复方案审批、复盘草稿
- 必须有 wait_signal（等待“已恢复”外部信号）和 human_approval（发布公告前审批）
- 至少 3 条失败回退路径（例如方案失败后切换备用方案）
- 最终输出：incident_summary、rca_hypothesis、recovery_plan、postmortem_draft
只输出一个 ```yaml 代码块。
```

### 3) 多角色内容工厂（短视频脚本 -> 多平台版本）

```text
请设计一个内容生产 workflow：输入是一个主题，输出多平台内容包（抖音/小红书/公众号）。
要求：
- roles：strategist/script_writer/editor/fact_checker/style_reviewer/publisher
- 至少 18 步
- 包含 parallel（多平台并行改写）+ evaluate（质量评分）+ reflect（低分重写）
- human_approval 作为发布前闸门
- 每个平台都要有标题、正文、CTA 和风险提示
- 最终输出变量：content_pack、quality_scores、publish_checklist
只输出 yaml 代码块。
```

### 4) 需求评审与技术方案收敛

```text
生成一个“产品需求 -> 技术方案 -> 评审定稿”的 workflow。
输入：PRD 文本。
约束：
- roles：pm/architect/backend/frontend/qa/security/reviewer
- 先并行产出方案，再 map_reduce 合并，再 evaluate 打分
- 打分不达标时进入 reflect 循环优化（最多 2 轮）
- human_approval 通过后才输出 final_architecture
- 至少 2 个 conditional 分支（复杂度高/低、风险高/低）
- 输出：architecture_options、tradeoff_matrix、final_architecture、test_plan
只输出一个 yaml 代码块。
```

### 5) 销售线索评分与自动跟进策略

```text
请创建一个销售线索编排 workflow。
输入：线索资料（行业、规模、预算、行为记录）。
要求：
- roles：lead_analyst/segmenter/scorer/message_writer/ae_manager/compliance_checker
- 包含：规则打分 + LLM 解释 + 分群路由 + 跟进文案生成 + 审批
- 必须包含 cache（避免重复评估）与 checkpoint（关键阶段留痕）
- 评分低/中/高走不同路径，至少 3 路分支
- 输出：lead_score、segment_label、followup_plan、outreach_copy
只输出 yaml。
```

### 6) 数据分析报告流水线（从原始文本到高管摘要）

```text
设计一个“原始数据描述文本 -> 分析报告 -> 高管摘要”的 workflow。
要求：
- roles：data_analyst/domain_expert/insight_editor/risk_reviewer/executive_writer
- 使用 map_reduce 处理多段输入，再并行生成多个分析视角
- evaluate + reflect 保证摘要可读性和一致性
- human_approval 作为最终发布门禁
- 输出：analysis_report、executive_summary、risk_notes、action_items
- 至少 16 步
只输出一个 ```yaml 代码块。
```

### 7) 合规审计与整改闭环

```text
请生成一个“合规审计整改”workflow。
输入：审计发现列表。
要求：
- roles：auditor/control_owner/legal/reviewer/remediation_owner
- 对每个发现项 foreach 处理：判级 -> 整改建议 -> 风险复核
- 高风险项必须进入 human_approval
- 包含 delay（整改观察期）+ wait_signal（等待整改完成信号）
- 输出：audit_matrix、remediation_plan、approval_log、closure_report
只输出 yaml 代码块。
```

### 8) 客服工单分诊与升级处理

```text
生成一个客服工单 workflow。
输入：用户问题描述。
要求：
- roles：triage_bot/product_expert/tech_support/billing_specialist/qa_reviewer/escalation_manager
- 至少 15 个 steps
- 先分类，再按类别并行召回知识并生成答复
- 置信度不足时走升级分支并请求人工审批
- 必须有 emit 步骤输出关键事件（如 escalated/resolved）
- 输出：ticket_category、draft_reply、resolution_path、escalation_reason
只输出 yaml。
```

### 9) 多语言本地化发布编排

```text
请设计一个本地化 workflow：输入中文原稿，输出 EN/JP/ES 三语版本。
要求：
- roles：translator_en/translator_jp/translator_es/style_reviewer/brand_reviewer/release_editor
- parallel 并行翻译，map_reduce 汇总 QA 问题
- evaluate 检查术语一致性，低分进入 reflect 重写
- 最终 human_approval 后才输出 final_localized_pack
- 输出：localized_drafts、terminology_issues、final_localized_pack
只输出一个 yaml 代码块。
```

### 10) 招聘面试评估编排

```text
生成一个“候选人面试评估”workflow。
输入：候选人简历 + 面试记录。
要求：
- roles：screening_reviewer/tech_interviewer/system_design_interviewer/culture_interviewer/hiring_manager
- 并行给出多维度评分，map_reduce 汇总结论
- evaluate 判断是否达标，边界情况进入 human_approval
- 输出必须包括：scorecard、risk_flags、hire_recommendation、debrief_notes
- 至少 14 步，至少 2 个条件分支
只输出 yaml。
```

### 11) 子工作流编排（workflow_call）

```text
请设计一个主 workflow + 多个子 workflow 的方案：
- 主流程负责路由与汇总
- 子流程 A 负责信息提取
- 子流程 B 负责评估打分
- 子流程 C 负责报告生成

要求：
- 使用 workflow_call（或 sub_workflow 别名）调用子流程
- 每个子流程都有明确输入输出
- 主流程至少 12 步，含审批与失败回退
- 最终输出 aggregated_report 与 decision
只输出一个 yaml 代码块。
```

### 12) 强约束版（专门用于“要复杂图”）

```text
请生成一个“复杂可视化导向”的 workflow YAML：
- roles >= 8
- steps >= 24
- parallel >= 3
- conditional/switch 分支节点 >= 4
- 至少 1 个 foreach 和 1 个 map_reduce
- 至少 1 个 human_approval + 1 个 wait_signal
- 至少 1 个 checkpoint + 1 个 cache
- 每个关键步骤写清楚 parameters（不要留空）
- 输出需要包含：summary、decision、next_actions、risk_items
只输出 yaml 代码块，不要解释。
```

---

如果你想快速得到“更像你当前业务”的工作流，可以先发一句：

```text
先问我 3 个澄清问题再生成 YAML。
```

## 自然语言随手版（更口语、直接说想做什么）

这组提示词不强调“必须 N 个角色/N 个步骤”，适合灵感探索和快速试玩：

- 先随便写一句话，然后改成诗，再把诗改成一个故事。
- 先写一个朋友圈文案，再改成小红书风格，最后改成公众号开头。
- 给我一句产品口号，扩展成 30 秒口播稿，再写成短视频分镜。
- 先把这段话压缩成 3 句话，再扩成一段有画面感的叙述。
- 把这段抱怨改成建设性反馈，再整理成可执行行动清单。
- 先生成一个科普解释，再改成小学生能懂的版本，再改成专业版。
- 把这个想法先写成电梯陈述，再写成 PRD 摘要，再写成发布公告。
- 先列出 5 个标题，再选 1 个扩成正文，再加一个结尾 CTA。
- 把这段技术说明先翻译成白话文，再整理成 FAQ，再生成客服回复模板。
- 先写一个悬念开头，再续写成短故事，最后改写成诗意结尾。
- 先给一个严肃版本，再给一个幽默版本，再给一个克制中性的版本。
- 把这个需求先拆成步骤计划，再生成执行清单，再给出风险提示。
- 先给一个会议纪要摘要，再提炼待办，再生成周报版本。
- 先写一个播客提纲，再改成演讲提纲，再改成直播串词。
- 先把一句话写得更有情绪，再改成更理性客观，再融合成平衡版。

如果你想“稍微复杂一点但不想太正式”，可以在末尾加一句：

```text
尽量设计成多步骤 workflow，让我可以在审批阶段继续优化。
```
