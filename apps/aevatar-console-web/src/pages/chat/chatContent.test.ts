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

  it("parses GFM tables without splitting escaped or code-span pipes", () => {
    expect(
      parseMarkdownBlocks(`| Name | \`member|id\` | Workflow |
| :--- | :---: | ---: |
| Observatory \\| beta | \`m-alpha\` | \`wf-alpha-with-a-long-identifier\` |`),
    ).toEqual([
      {
        kind: "table",
        headers: ["Name", "`member|id`", "Workflow"],
        alignments: ["left", "center", "right"],
        rows: [
          [
            "Observatory | beta",
            "`m-alpha`",
            "`wf-alpha-with-a-long-identifier`",
          ],
        ],
      },
    ]);
  });

  it("keeps malformed table syntax as paragraph content", () => {
    expect(
      parseMarkdownBlocks(`| Name | Member | Workflow |
| --- | --- |
| Observatory | m-alpha | wf-alpha |`),
    ).toEqual([
      {
        kind: "paragraph",
        lines: [
          "| Name | Member | Workflow |",
          "| --- | --- |",
          "| Observatory | m-alpha | wf-alpha |",
        ],
      },
    ]);
  });

  it("rejects table delimiters with internal whitespace", () => {
    expect(
      parseMarkdownBlocks(`| Name | Member |
| - - - | --- |
| Observatory | m-alpha |`),
    ).toEqual([
      {
        kind: "paragraph",
        lines: [
          "| Name | Member |",
          "| - - - | --- |",
          "| Observatory | m-alpha |",
        ],
      },
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
