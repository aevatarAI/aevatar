# ${PROBLEM_TITLE_ZH}

## 1. 问题是什么

${PROBLEM_STATEMENT_ZH}

## 2. 具体例子

下面是真实问题代码。标 `problem` 的行触发违反。

```csharp
${PROBLEM_EXAMPLE_CODE}
```

**文件**：`${PROBLEM_EXAMPLE_FILE_PATH}`

## 3. 为什么需要设计决定

${WHY_NEEDS_DESIGN_ZH}

## 4. 需要你回答

加 `auto-loop-resume` 标签前请回答。Implement codex 会原样读取你的最新评论作为设计输入。

- [ ] 模式选择：${DESIGN_QUESTION_PATTERN_ZH}
- [ ] Proto 影响：如需新增 typed field，列出 message 名和 field number；无 proto 改动请明确说明。
- [ ] 向后兼容：现有持久态如何处理？例如 reserved 字段号、type alias、migration 或可接受重置。
- [ ] Scope 拆分：单 cluster 还是拆 N 个 PR？拆则给 cluster id 草案。
- [ ] 测试面：除 `verification_hints` 外，必须测试哪些行为？
- [ ] 越界禁地：implement codex 不应碰什么？

## 5. Auto-loop 行为

- Controller 在此 issue 是仅剩工作时约每 1 小时轮询一次。
- 首次新评论触发 operator 通知；后续评论不重复推送。
- 加 `auto-loop-resume` 后，controller 把最新评论拼到新 implement prompt 前面并 dispatch。
- 不加该标签直接关闭 issue，controller 视为设计拒绝。

## 6. Reference: full cluster spec

<details>
<summary>展开 cluster YAML / evidence / audit fix boundary</summary>

### Cluster spec

${CLUSTER_YAML}

### Evidence

${CLUSTER_EVIDENCE}

### Fix boundary

${CLUSTER_FIX_BOUNDARY}

</details>

cc: @loning
