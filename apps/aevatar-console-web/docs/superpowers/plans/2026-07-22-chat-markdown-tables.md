# Chat Markdown Tables Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render valid GFM tables in Chat as accessible, responsive HTML tables while keeping the repository's existing typed Markdown pipeline authoritative.

**Architecture:** Extend `parseMarkdownBlocks` with a typed table block and a row scanner that distinguishes structural pipes from escaped and code-span pipes. Render that block explicitly in Chat, Studio run output, and Explorer so every existing consumer honors the shared parser contract without a second Markdown implementation or a new dependency.

**Tech Stack:** React 19, TypeScript, Jest 29, Testing Library, pnpm, Ant Design/Umi frontend.

---

### Task 1: Parse GFM Tables Into A Typed Block

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/chat/chatContent.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatContent.ts`

- [x] **Step 1: Write the failing parser test**

Append this test inside `describe("chatContent", ...)`:

```typescript
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
```

- [x] **Step 2: Run the parser test and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --selectProjects jsdom \
  --runTestsByPath src/pages/chat/chatContent.test.ts \
  --runInBand
```

Expected: FAIL because the actual value is a `paragraph` block containing raw table lines.

- [x] **Step 3: Add the typed table contract and scanner**

Add these exported types and the union member near `MarkdownBlock`:

```typescript
export type MarkdownTableAlignment = "left" | "center" | "right" | null;

export type MarkdownTableBlock = {
  kind: "table";
  headers: string[];
  alignments: MarkdownTableAlignment[];
  rows: string[][];
};

export type MarkdownBlock =
  | { kind: "paragraph"; lines: string[] }
  | { kind: "heading"; level: number; text: string }
  | { kind: "blockquote"; lines: string[] }
  | { kind: "unordered-list"; items: string[] }
  | { kind: "ordered-list"; items: string[] }
  | { kind: "code"; lang: string; code: string }
  | { kind: "thematic-break" }
  | MarkdownTableBlock;
```

Add these parser helpers above `parseMarkdownBlocks`:

```typescript
const MARKDOWN_TABLE_DELIMITER_CELL_PATTERN = /^:?-{3,}:?$/;

function splitMarkdownTableRow(line: string): string[] | null {
  const source = line.trim();
  const cells: string[] = [];
  let cell = "";
  let codeFenceLength = 0;
  let structuralPipeCount = 0;
  let lastStructuralPipeIndex = -1;

  for (let index = 0; index < source.length; index += 1) {
    const character = source[index];

    if (character === "\\" && source[index + 1] === "|") {
      cell += "|";
      index += 1;
      continue;
    }

    if (character === "`") {
      let runLength = 1;
      while (source[index + runLength] === "`") {
        runLength += 1;
      }

      cell += "`".repeat(runLength);
      if (codeFenceLength === 0) {
        codeFenceLength = runLength;
      } else if (codeFenceLength === runLength) {
        codeFenceLength = 0;
      }
      index += runLength - 1;
      continue;
    }

    if (character === "|" && codeFenceLength === 0) {
      structuralPipeCount += 1;
      lastStructuralPipeIndex = index;
      cells.push(cell.trim());
      cell = "";
      continue;
    }

    cell += character;
  }

  if (structuralPipeCount === 0) {
    return null;
  }

  cells.push(cell.trim());
  if (source.startsWith("|")) {
    cells.shift();
  }
  if (lastStructuralPipeIndex === source.length - 1) {
    cells.pop();
  }

  return cells;
}

function parseMarkdownTableAlignments(
  line: string,
): MarkdownTableAlignment[] | null {
  const cells = splitMarkdownTableRow(line);
  if (!cells || cells.length === 0) {
    return null;
  }

  const alignments: MarkdownTableAlignment[] = [];
  for (const cell of cells) {
    if (!MARKDOWN_TABLE_DELIMITER_CELL_PATTERN.test(cell)) {
      return null;
    }

    const left = cell.startsWith(":");
    const right = cell.endsWith(":");
    alignments.push(left && right ? "center" : right ? "right" : left ? "left" : null);
  }

  return alignments;
}
```

After code-fence parsing and before heading parsing in `parseMarkdownBlocks`, add:

```typescript
const alignments =
  index + 1 < lines.length
    ? parseMarkdownTableAlignments(lines[index + 1])
    : null;
const headers = alignments ? splitMarkdownTableRow(line) : null;
if (headers && alignments && headers.length === alignments.length) {
  flushParagraph();
  const rows: string[][] = [];
  let cursor = index + 2;

  while (cursor < lines.length) {
    const cells = splitMarkdownTableRow(lines[cursor]);
    if (!cells) {
      break;
    }

    rows.push(headers.map((_, cellIndex) => cells[cellIndex] ?? ""));
    cursor += 1;
  }

  blocks.push({ kind: "table", headers, alignments, rows });
  index = cursor - 1;
  continue;
}
```

- [x] **Step 4: Run the parser test and verify GREEN**

Run the command from Step 2.

Actual: PASS with 6 tests in `chatContent.test.ts`, including the review-driven
internal-whitespace delimiter regression.

- [x] **Step 5: Inspect the parser diff and keep it uncommitted**

```bash
git diff --check -- \
  apps/aevatar-console-web/src/pages/chat/chatContent.ts \
  apps/aevatar-console-web/src/pages/chat/chatContent.test.ts
```

Expected: no whitespace errors. Keep this change uncommitted because the new
union member requires the Chat, Studio, and Explorer consumers to be updated
before the source commit can pass `tsc`.

### Task 2: Render Responsive Semantic Tables In Chat

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/chat/chatPresentation.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatPresentation.tsx`

- [x] **Step 1: Write the failing Chat presentation test**

Change imports to include `within`, `ChatMessageBubble`, and `ChatMessage`, then append:

```typescript
import { fireEvent, render, screen, within } from '@testing-library/react';
import { ChatInput, ChatMessageBubble } from './chatPresentation';
import type { ChatMessage } from './chatTypes';

it('renders GFM tables with accessible headers and responsive containment', () => {
  const message: ChatMessage = {
    id: 'message-table',
    role: 'assistant',
    content: `| 名称 | member_id | workflow_id |
| --- | :---: | ---: |
| Observatory | \`m-alpha\` | \`wf-alpha-with-a-long-identifier\` |`,
    timestamp: 1,
    status: 'complete',
  };

  render(<ChatMessageBubble message={message} />);

  const region = screen.getByRole('region', { name: 'Message table' });
  expect(region).toHaveStyle({ maxWidth: '100%', overflowX: 'auto' });

  const table = within(region).getByRole('table');
  const headers = within(table).getAllByRole('columnheader');
  expect(headers).toHaveLength(3);
  expect(headers[0]).toHaveAttribute('scope', 'col');
  expect(headers[2]).toHaveStyle({ textAlign: 'right' });

  const workflowId = within(table).getByText('wf-alpha-with-a-long-identifier');
  expect(workflowId.tagName).toBe('CODE');
  expect(workflowId.closest('td')).toHaveStyle({ overflowWrap: 'anywhere' });
});
```

- [x] **Step 2: Run the presentation test and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --selectProjects jsdom \
  --runTestsByPath src/pages/chat/chatPresentation.test.tsx \
  --runInBand
```

Expected: FAIL because no accessible `region` or `table` exists.

- [x] **Step 3: Add stable table styles and render the typed block**

Add module-level `React.CSSProperties` constants near the Markdown renderer:

```typescript
const markdownTableRegionStyle: React.CSSProperties = {
  border: "1px solid #e5e7eb",
  borderRadius: 8,
  margin: "8px 0 12px",
  maxWidth: "100%",
  overflowX: "auto",
};

const markdownTableStyle: React.CSSProperties = {
  borderCollapse: "collapse",
  fontSize: 13,
  minWidth: "100%",
  width: "max-content",
};

const markdownTableHeaderCellStyle: React.CSSProperties = {
  background: "#f8fafc",
  borderBottom: "1px solid #d1d5db",
  color: "#475569",
  fontWeight: 700,
  padding: "9px 11px",
  textAlign: "left",
  whiteSpace: "nowrap",
};

const markdownTableCellStyle: React.CSSProperties = {
  borderTop: "1px solid #eef2f7",
  maxWidth: 320,
  overflowWrap: "anywhere",
  padding: "9px 11px",
  verticalAlign: "top",
  wordBreak: "normal",
};
```

Add this `case` to `renderMarkdownBlock` before `case "code"`:

```tsx
case "table":
  return (
    <div
      aria-label={t("pages.chat.chatpresentation.message.table", "Message table")}
      key={`block-${blockIndex}`}
      role="region"
      style={markdownTableRegionStyle}
      tabIndex={0}
    >
      <table style={markdownTableStyle}>
        <thead>
          <tr>
            {block.headers.map((header, cellIndex) => (
              <th
                key={`table-${blockIndex}-header-${cellIndex}`}
                scope="col"
                style={{
                  ...markdownTableHeaderCellStyle,
                  textAlign: block.alignments[cellIndex] ?? "left",
                }}
              >
                {renderInlineTokens(
                  tokenizeInlineContent(header),
                  `table-${blockIndex}-header-${cellIndex}`,
                )}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {block.rows.map((row, rowIndex) => (
            <tr key={`table-${blockIndex}-row-${rowIndex}`}>
              {block.headers.map((_, cellIndex) => (
                <td
                  key={`table-${blockIndex}-row-${rowIndex}-${cellIndex}`}
                  style={{
                    ...markdownTableCellStyle,
                    textAlign: block.alignments[cellIndex] ?? "left",
                  }}
                >
                  {renderInlineTokens(
                    tokenizeInlineContent(row[cellIndex] ?? ""),
                    `table-${blockIndex}-row-${rowIndex}-${cellIndex}`,
                  )}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
```

- [x] **Step 4: Run the presentation test and verify GREEN**

Run the command from Step 2.

Expected: PASS with 5 tests in `chatPresentation.test.tsx`.

- [x] **Step 5: Inspect the Chat renderer diff and keep it uncommitted**

```bash
git diff --check -- \
  apps/aevatar-console-web/src/pages/chat/chatPresentation.tsx \
  apps/aevatar-console-web/src/pages/chat/chatPresentation.test.tsx
```

Expected: no whitespace errors. Keep this change with Task 1 until all shared
parser consumers compile.

### Task 3: Keep Shared Parser Consumers Exhaustive

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/studio/components/StudioMemberCurrentRunPanel.tsx`
- Modify: `apps/aevatar-console-web/src/pages/studio/explorer/ExplorerContentView.tsx`

- [x] **Step 1: Run TypeScript and verify the typed-contract failures**

Run:

```bash
pnpm --dir apps/aevatar-console-web tsc
```

Expected: FAIL where Studio and Explorer default branches read `block.lines` from the new table member.

- [x] **Step 2: Convert Studio's workaround to the typed table block**

Add `case 'table': return renderMarkdownTable(block, index);` before the code case. Delete `splitMarkdownTableRow` and `isMarkdownTableSeparator`. Change the existing table renderer to:

```tsx
function renderMarkdownTable(
  block: Extract<MarkdownBlock, { kind: 'table' }>,
  index: number,
) {
  return (
    <div key={`table-${index}`} style={markdownTableWrapperStyle}>
      <table style={markdownTableStyle}>
        <thead>
          <tr>
            {block.headers.map((cell, cellIndex) => (
              <th
                key={cellIndex}
                scope="col"
                style={{
                  ...markdownTableHeaderCellStyle,
                  textAlign: block.alignments[cellIndex] ?? 'left',
                }}
              >
                {renderInlineContent(cell, `table-${index}-head-${cellIndex}`)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {block.rows.map((row, rowIndex) => (
            <tr key={rowIndex}>
              {block.headers.map((_, cellIndex) => (
                <td
                  key={cellIndex}
                  style={{
                    ...markdownTableCellStyle,
                    textAlign: block.alignments[cellIndex] ?? 'left',
                  }}
                >
                  {renderInlineContent(
                    row[cellIndex] ?? '',
                    `table-${index}-${rowIndex}-${cellIndex}`,
                  )}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
```

Replace `renderRunOutputContent`'s paragraph sniff with:

```tsx
return (
  <div style={renderedOutputStyle}>
    {blocks.map((block, index) => renderMarkdownBlock(block, index))}
  </div>
);
```

- [x] **Step 3: Add an explicit Explorer table case**

Before Explorer's code case, render the typed block directly:

```tsx
case "table":
  return (
    <div
      key={key}
      style={{
        border: "1px solid var(--ant-color-border-secondary)",
        borderRadius: 8,
        marginBottom: 12,
        maxWidth: "100%",
        overflowX: "auto",
      }}
    >
      <table style={{ borderCollapse: "collapse", minWidth: "100%", width: "max-content" }}>
        <thead>
          <tr>
            {block.headers.map((header, cellIndex) => (
              <th
                key={`${key}-header-${cellIndex}`}
                scope="col"
                style={{
                  background: "var(--ant-color-fill-quaternary)",
                  borderBottom: "1px solid var(--ant-color-border-secondary)",
                  padding: "8px 10px",
                  textAlign: block.alignments[cellIndex] ?? "left",
                  whiteSpace: "nowrap",
                }}
              >
                {renderInlineContent(header)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {block.rows.map((row, rowIndex) => (
            <tr key={`${key}-row-${rowIndex}`}>
              {block.headers.map((_, cellIndex) => (
                <td
                  key={`${key}-row-${rowIndex}-${cellIndex}`}
                  style={{
                    borderTop: "1px solid var(--ant-color-border-secondary)",
                    overflowWrap: "anywhere",
                    padding: "8px 10px",
                    textAlign: block.alignments[cellIndex] ?? "left",
                    verticalAlign: "top",
                  }}
                >
                  {renderInlineContent(row[cellIndex] ?? "")}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
```

- [x] **Step 4: Run TypeScript and focused Chat tests**

Run:

```bash
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web exec jest \
  --selectProjects jsdom \
  --runTestsByPath \
  src/pages/chat/chatContent.test.ts \
  src/pages/chat/chatPresentation.test.tsx \
  --runInBand
```

Actual: TypeScript passes; 2 test suites and 11 tests pass.

- [x] **Step 5: Commit the complete buildable implementation**

```bash
git add \
  apps/aevatar-console-web/src/pages/chat/chatContent.ts \
  apps/aevatar-console-web/src/pages/chat/chatContent.test.ts \
  apps/aevatar-console-web/src/pages/chat/chatPresentation.tsx \
  apps/aevatar-console-web/src/pages/chat/chatPresentation.test.tsx \
  apps/aevatar-console-web/src/pages/studio/components/StudioMemberCurrentRunPanel.tsx \
  apps/aevatar-console-web/src/pages/studio/explorer/ExplorerContentView.tsx
git -c user.name=AbigailDeng \
  -c user.email=108705114+AbigailDeng@users.noreply.github.com \
  commit -m "Render Chat Markdown tables"
```

### Task 4: Verify The Complete Frontend Change

**Files:**
- Inspect: all files changed from `origin/dev`

- [x] **Step 1: Run focused regression tests**

Run the exact Jest command from Task 3 Step 4.

Actual: 2 suites and 11 tests pass with no warnings caused by this change.

- [x] **Step 2: Run mandatory frontend validation**

Run each command independently:

```bash
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web test:ui -- --runInBand
pnpm --dir apps/aevatar-console-web build
bash tools/ci/test_stability_guards.sh
```

Actual: TypeScript, test stability, production build, and the second full UI
run all exited 0. The first UI run exposed the missing catalog entry; after the
catalog fix, 114 suites and 1028 tests passed.

- [x] **Step 3: Run diff and architecture checks**

```bash
git diff --check origin/dev...HEAD
git diff --stat origin/dev...HEAD
git status --short
```

Expected: no whitespace errors, no changes outside `apps/aevatar-console-web/**`, and a clean worktree.

- [x] **Step 4: Validate desktop and mobile containment locally**

Start the frontend on an allowed port:

```bash
pnpm --dir apps/aevatar-console-web start:dev --port 5173
```

Open the local Chat route in the in-app browser at desktop and mobile widths. Verify the table is nonblank, headers and cells align, the message width does not expand, horizontal scrolling is available when required, inline code remains legible, and no adjacent content overlaps. If authentication or backend state prevents a table-bearing Chat message, document that limitation and use the DOM-focused Jest evidence as the authoritative rendering verification.

Actual: the local route was blocked by the NyxID configuration gate before Chat
rendered. The DOM-focused Jest test is the authoritative responsive/semantic
evidence for this environment.

- [x] **Step 5: Commit any verification-only corrections**

If verification required a source correction, repeat its focused red-green test and commit only the verified correction with an imperative message. If no correction was needed, do not create an empty commit.

### Task 5: Create The PR And Enable Auto-Merge

**Files:**
- No additional repository files.

- [x] **Step 1: Confirm AbigailDeng authentication and branch state**

```bash
gh auth status
git branch --show-current
git status --short
```

Expected: `AbigailDeng` is active, branch is `fix/2026-07-22_chat-markdown-tables`, and status is clean.

Actual: all three conditions were confirmed before the GitHub writes.

- [x] **Step 2: Apply FKST issue labels**

```bash
gh issue edit 2885 --repo aevatarAI/aevatar \
  --add-label "fkst-dev:enabled" \
  --add-label "fkst-class:standard"
```

Expected: issue #2885 has both FKST labels.

Actual: issue #2885 has `fkst-dev:enabled` and `fkst-class:standard`.

- [x] **Step 3: Push the issue branch**

```bash
git push --set-upstream origin fix/2026-07-22_chat-markdown-tables
```

Expected: the branch is published under the AbigailDeng GitHub credentials.

Actual: `origin/fix/2026-07-22_chat-markdown-tables` was created by the active
AbigailDeng credentials.

- [x] **Step 4: Open a ready PR against dev**

Create `/tmp/aevatar-pr-2885.md` with `apply_patch` using this exact body:

```markdown
## Problem and solution

Chat treated valid GFM table source as paragraph text because the custom typed Markdown parser had no table block. This change adds a typed table contract, parses delimiters/alignment/escaped pipes, and renders semantic responsive tables with scoped headers.

## Impacted paths

- `apps/aevatar-console-web/src/pages/chat/**`
- `apps/aevatar-console-web/src/locales/projectMessages.en-US.ts`
- `apps/aevatar-console-web/src/locales/projectMessages.zh-CN.ts`
- `apps/aevatar-console-web/src/pages/studio/components/StudioMemberCurrentRunPanel.tsx`
- `apps/aevatar-console-web/src/pages/studio/explorer/ExplorerContentView.tsx`
- `apps/aevatar-console-web/docs/superpowers/**`

## Verification

- `pnpm --dir apps/aevatar-console-web tsc`
- `pnpm --dir apps/aevatar-console-web test:ui -- --runInBand`
- `pnpm --dir apps/aevatar-console-web build`
- `bash tools/ci/test_stability_guards.sh`

## Documentation

- `apps/aevatar-console-web/docs/superpowers/specs/2026-07-22-chat-markdown-tables-design.md`
- `apps/aevatar-console-web/docs/superpowers/plans/2026-07-22-chat-markdown-tables.md`

Closes #2885
```

Then create the PR:

```bash
gh pr create \
  --repo aevatarAI/aevatar \
  --base dev \
  --head fix/2026-07-22_chat-markdown-tables \
  --title "fix(console): render Chat Markdown tables" \
  --body-file /tmp/aevatar-pr-2885.md
```

Expected: a non-draft PR URL.

Actual: https://github.com/aevatarAI/aevatar/pull/2919 is open and ready against
`dev`.

- [x] **Step 5: Enable squash auto-merge as explicitly authorized**

```bash
PR_NUMBER=$(gh pr view --repo aevatarAI/aevatar --json number --jq .number)
gh pr merge "$PR_NUMBER" --repo aevatarAI/aevatar --auto --squash
```

Expected: GitHub reports auto-merge enabled, or reports that branch protection already permits an immediate merge. Do not bypass required checks or administrator protections.

Actual: GitHub records an enabled squash auto-merge request by AbigailDeng;
required CI checks continue normally.

- [x] **Step 6: Report final state**

Record the issue URL, PR URL and number, commit list, all verification results, and auto-merge status. Keep the local development server running and provide its URL when the route remains usable for the user.
