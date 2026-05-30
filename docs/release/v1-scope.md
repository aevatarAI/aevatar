---
title: v1 Scope Freeze Decision Table
status: active
owner: loning
---

# v1 Scope Freeze Decision Table

本文档冻结 2026-05-30 查询到的 `milestone:p0:v1-target` open issue 范围,作为 v1 first-slice 收口与 v2 延后判断依据。

## 数据来源

- 查询命令:`gh issue list --label "milestone:p0:v1-target" --state open --json number,title`
- 查询日期:2026-05-30
- 分类口径:
  - `v1 必合`:no-new-core first-slice,当前已进入 `phase:implementing` 或 `phase:pr-open`,或是 v1 release candidate 前必须完成的 guard / rollup / umbrella 收口项。
  - `v1 之后 v2 处理`:later-slice design-pending,需要 owner-change bootstrap、typed continuation、lifecycle contract、canon vocabulary 等较大设计或 maintainer 决策。
  - `drop`:已 obsolete、重复或不再承载独立 v1/v2 决策的 issue。

## 决策表

| Issue | 标题 | 分类 | Rationale |
|---|---|---|---|
| #1473 | Phase 9 #1388 later-slice(design-pending):v1 release invariant followups | v1 之后 v2 处理 | later-slice 且带 `refactor-design-needed` / `phase:blocked` / maintainer 决策标签,属于 v1 scope freeze 后的 invariant 深化,不进入 first-slice 必合。 |
| #1472 | Phase 9 #1388 first-slice: v1 scope freeze decision table(docs only,no-new-core) | v1 必合 | 本文档对应 issue,是 v1 scope freeze 的 first-slice 文档交付,无新增 core 设计。 |
| #1471 | Phase 9 #1389 later-slice(design-pending):typed ChatRun continuation / true complete-mode 语义 | v1 之后 v2 处理 | typed continuation 与 true complete-mode 会改变 ChatRun 完成语义,需要设计共识,不属于 no-new-core first-slice。 |
| #1470 | Phase 9 #1389 first-slice: 删除 InvokeTeam wait=complete frame folding(no-new-core) | v1 必合 | first-slice 且已 `phase:implementing`,目标是删除 misleading complete-mode folding,直接降低 v1 ACK / query 语义风险。 |
| #1469 | Phase 9 #1391 later-slice(design-pending):owner-change bootstrap(split / merge / re-key)工具链 | v1 之后 v2 处理 | owner-change bootstrap、split / merge / re-key 工具链属于 actor identity 与 readmodel 迁移大设计,当前 blocked on maintainer 决策。 |
| #1468 | Phase 9 #1391 first-slice: v1 non-goal + query/read bootstrap guard(no-new-core) | v1 必合 | first-slice 且已 `phase:implementing`,用于把复杂 bootstrap 明确为 v1 non-goal 并补 query/read guard。 |
| #1467 | Phase 9 #1390 later-slice(design-pending):durable payload credential boundary | v1 之后 v2 处理 | durable payload credential boundary 需要更完整的 typed credential / persistence 设计,当前 blocked on maintainer 决策。 |
| #1466 | Phase 9 #1390 first-slice: ChannelInboundEvent.registration_token reserve + scrub guard(no-new-core) | v1 必合 | first-slice 且已 `phase:implementing`,通过 reserve + scrub guard 收紧 v1 credential 暴露风险。 |
| #1464 | Phase 9 #1392 later-slice: lifecycle failure contract + topology port + canon design(maintainer 决策) | v1 之后 v2 处理 | lifecycle failure contract、topology port 与 canon design 是 runtime contract 级设计,明确需 maintainer 决策。 |
| #1461 | Phase 9 #1393 later-slice: accepted/queryable custom device event design(等 v1 后产品需求) | v1 之后 v2 处理 | 标题已声明等待 v1 后产品需求,custom device event accepted/queryable 语义不作为 v1 必合。 |
| #1458 | Phase 9 #1394 later-slice: workflow identity 长期边界 design(SessionId / CommandId / canon vocabulary) | v1 之后 v2 处理 | workflow identity 长期边界和 canon vocabulary 需要统一命名语义与迁移设计,属于 v2 设计项。 |
| #1455 | Phase 9 #1449 later-slice: 完整 release version contract(VERSION / CHANGELOG / console-web 对齐 / release scripts) | v1 之后 v2 处理 | 完整 version contract 涉及 VERSION、CHANGELOG、console-web 与 release scripts 的体系化收口,不阻塞 first-slice scope freeze。 |
| #1451 | Phase 9 #1395 later-slice: ScopeWorkflowSummary v1 命运 A/B/C 三选一(maintainer 决策) | v1 之后 v2 处理 | ScopeWorkflowSummary 的保留、降级或删除需要 maintainer 在 A/B/C 间决策,不能在 v1 first-slice 中擅自落定。 |
| #1447 | Phase 9 #1396 first-slice: v1 release gate / regression guards(Lark outbound + trusted facts + workflow completion) | v1 必合 | first-slice 且 PR 已开,覆盖 Lark outbound、trusted facts、workflow completion 等 v1 release gate 防回归。 |
| #1445 | [refactor-design] #1397 later slice: Console Web status presentation adapter + vocabulary 设计 | v1 之后 v2 处理 | Console Web status adapter 与 vocabulary 属于 presentation / vocabulary later-slice,当前 blocked on design。 |
| #1444 | [refactor-impl] #1397 first slice: Team workbench 状态诚实修正(完成 ≠ 稳定) | v1 必合 | first-slice 且 PR 已开,修正 Team workbench 把完成态误当稳定态的问题,直接服务 v1 查询诚实性。 |
| #1443 | [refactor-design] #1400 later slice: v1 release scope canon table + glossary + docs lint 设计 | v1 之后 v2 处理 | 该 issue 要决定是否新增 canon table、glossary 与 docs lint,命中 canon-vocabulary / docs-canon-change trigger,保持 v2 design-pending。 |
| #1399 | [v1] Guard baseline: release candidate 前运行 architecture/build/test/slow/docs 与专项 guards | v1 必合 | v1 release candidate 退出标准的一部分,需要在 scope freeze 与 rollup 收口后记录 guard baseline。 |
| #1398 | [v1] Rollup CI: 修复 #1167 coverage-quality + 清理 #1171/#1166/#1024 v1 影响 | v1 必合 | v1 退出标准要求 rollup 红项关闭、合并或明确非 v1,该 issue 是 release candidate 前必须收口的 CI 判定项。 |
| #1387 | [v1-umbrella] Aevatar v1 — 第一个版本目标伞 issue | v1 必合 | v1 umbrella 承载 release scope、退出标准、blocking issue/PR 与进度,必须保留到 v1 发布完成。 |

## 汇总

- `v1 必合`:9 个
- `v1 之后 v2 处理`:11 个
- `drop`:0 个

本次冻结未发现明确 obsolete 或重复的 open issue。后续若某个 later-slice 被 maintainer 明确判定为不再需要,应单独更新为 `drop` 并写明替代 issue 或关闭依据。

⟦AI:AUTO-LOOP⟧
