import {
  parseMarkdownBlocks,
  sanitizeAssistantMessageContent,
  tokenizeInlineContent,
} from "./chatContent";

describe("chatContent", () => {
  it("strips complete and dangling tool call blocks from assistant content", () => {
    const content = `Before

<function_calls>
<invoke name="search">
<parameter name="query">hello</parameter>
</invoke>
</function_calls>

Middle

<| DSML | function_calls>
<| DSML | invoke name="dangerous_tool">`;

    expect(sanitizeAssistantMessageContent(content)).toBe("Before\n\nMiddle");
  });

  it("parses headings, lists, and code fences into markdown blocks", () => {
    expect(
      parseMarkdownBlocks(`# Title

- first
- second

\`\`\`ts
const value = 1;
\`\`\``),
    ).toEqual([
      { kind: "heading", level: 1, text: "Title" },
      { kind: "unordered-list", items: ["first", "second"] },
      { kind: "code", lang: "ts", code: "const value = 1;" },
    ]);
  });

  it("tokenizes bold text, code spans, and links", () => {
    expect(
      tokenizeInlineContent("Visit **[Docs](https://example.com)** and use `cmd`."),
    ).toEqual([
      { kind: "text", text: "Visit ", bold: false },
      {
        kind: "link",
        text: "Docs",
        href: "https://example.com",
        bold: true,
      },
      { kind: "text", text: " and use ", bold: false },
      { kind: "code", text: "cmd" },
      { kind: "text", text: ".", bold: false },
    ]);
  });
});
