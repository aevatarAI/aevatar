import { describe, expect, it } from 'vitest';

import { parseMarkdownBlocks, sanitizeAssistantMessageContent, tokenizeInlineContent } from './chatContent';

describe('sanitizeAssistantMessageContent', () => {
  it('removes completed DSML function call blocks from assistant text', () => {
    const content = `好的！我看到你想连接 Lark。\n\n<| DSML | function_calls>
<| DSML | invoke name="nyxid_providers">
<| DSML | parameter name="input" string="true">action=get_credentials</| DSML | parameter>
</| DSML | invoke>
</| DSML | function_calls>\n\n我继续帮你检查配置。`;

    const sanitized = sanitizeAssistantMessageContent(content);

    expect(sanitized).toBe('好的！我看到你想连接 Lark。\n\n我继续帮你检查配置。');
  });

  it('hides dangling DSML blocks while the stream is still incomplete', () => {
    const content = `让我先检查一下：\n\n<| DSML | function_calls>
<| DSML | invoke name="nyxid_providers">`;

    const sanitized = sanitizeAssistantMessageContent(content);

    expect(sanitized).toBe('让我先检查一下：');
  });

  it('removes standard XML function_calls blocks', () => {
    const content = `让我调用工具：\n\n<function_calls>\n<invoke name="search">\n<parameter name="query">hello</parameter>\n</invoke>\n</function_calls>\n\n结果如下。`;

    const sanitized = sanitizeAssistantMessageContent(content);

    expect(sanitized).toBe('让我调用工具：\n\n结果如下。');
  });

  it('hides dangling XML function_calls blocks while streaming', () => {
    const content = `正在检查：\n\n<function_calls>\n<invoke name="search">`;

    const sanitized = sanitizeAssistantMessageContent(content);

    expect(sanitized).toBe('正在检查：');
  });

  it('removes XML function_calls blocks with whitespace before closing bracket', () => {
    const content = `调用工具：\n\n<function_calls >\n<invoke name="search" >\n<parameter name="query" >hello</parameter >\n</invoke >\n</function_calls >\n\n完成。`;

    const sanitized = sanitizeAssistantMessageContent(content);

    expect(sanitized).toBe('调用工具：\n\n完成。');
  });

  it('removes DSML blocks with full-width Unicode pipes (U+FF5C)', () => {
    const content = `检查配置：\n\n<\uff5cDSML\uff5cfunction_calls>\n<\uff5cDSML\uff5cinvoke name="nyxid_approvals">\n<\uff5cDSML\uff5cparameter name="input" string="true">action=configs</\uff5cDSML\uff5cparameter>\n</\uff5cDSML\uff5cinvoke>\n</\uff5cDSML\uff5cfunction_calls>\n\n完成。`;

    const sanitized = sanitizeAssistantMessageContent(content);

    expect(sanitized).toBe('检查配置：\n\n完成。');
  });

  it('handles mixed DSML and XML blocks in one message', () => {
    const content = `第一步\n\n<| DSML | function_calls>\n<| DSML | invoke name="a">\n</| DSML | invoke>\n</| DSML | function_calls>\n\n第二步\n\n<function_calls>\n<invoke name="b">\n</invoke>\n</function_calls>\n\n完成。`;

    const sanitized = sanitizeAssistantMessageContent(content);

    expect(sanitized).toBe('第一步\n\n第二步\n\n完成。');
  });

  it('tokenizes bare urls and markdown links into clickable link tokens', () => {
    const tokens = tokenizeInlineContent('参考 https://aevatar.ai 和 [文档](www.example.com/docs) 即可。');

    expect(tokens).toEqual([
      { kind: 'text', text: '参考 ', bold: false },
      { kind: 'link', text: 'https://aevatar.ai', href: 'https://aevatar.ai', bold: false },
      { kind: 'text', text: ' 和 ', bold: false },
      { kind: 'link', text: '文档', href: 'https://www.example.com/docs', bold: false },
      { kind: 'text', text: ' 即可。', bold: false },
    ]);
  });

  it('parses common markdown blocks for richer chat rendering', () => {
    const blocks = parseMarkdownBlocks(`# 标题

普通段落
第二行

- 第一项
- 第二项

1. 步骤一
2. 步骤二

> 引用
> 第二行

---

\`\`\`json
{"ok":true}
\`\`\``);

    expect(blocks).toEqual([
      { kind: 'heading', level: 1, text: '标题' },
      { kind: 'paragraph', lines: ['普通段落', '第二行'] },
      { kind: 'unordered-list', items: ['第一项', '第二项'] },
      { kind: 'ordered-list', items: ['步骤一', '步骤二'] },
      { kind: 'blockquote', lines: ['引用', '第二行'] },
      { kind: 'thematic-break' },
      { kind: 'code', lang: 'json', code: '{"ok":true}' },
    ]);
  });
});
