import { describe, expect, it } from 'vitest';

import { sanitizeAssistantMessageContent } from './chatContent';

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
});
