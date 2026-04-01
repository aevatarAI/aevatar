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
