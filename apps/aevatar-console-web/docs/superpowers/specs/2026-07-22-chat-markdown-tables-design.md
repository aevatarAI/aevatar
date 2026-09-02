# Chat Markdown Table Rendering Design

## Status

Approved for implementation on 2026-07-22.

## Problem

Chat assistant messages render GFM table source as paragraph text. The custom
Markdown parser has no table block, so the presentation layer receives only
lines and emits line breaks. Long identifiers and inline code then wrap without
any row or column structure.

GitHub issue: `aevatarAI/aevatar#2885`.

## Goals

- Parse valid GFM table headers, delimiter rows, optional column alignment, and
  body rows into one typed Markdown block.
- Render semantic Chat tables with identifiable column headers.
- Keep tables contained on desktop and narrow screens without overlapping
  adjacent message content.
- Preserve inline code, bold text, and links inside cells.
- Keep malformed or incomplete table syntax as ordinary paragraph content.
- Reuse the existing parser and inline tokenization path without adding a
  Markdown runtime dependency.

## Non-Goals

- Implement every GFM extension.
- Replace the existing Markdown renderer.
- Change Chat navigation, message layout, or backend contracts.
- Introduce repository-wide Markdown styling.

## Root Cause

`parseMarkdownBlocks` returns headings, paragraphs, blockquotes, lists, code,
and thematic breaks, but no table variant. A table is therefore accumulated as
one paragraph. `ChatMessageBubble` calls the paragraph renderer, which preserves
the source pipes and inserts `<br>` elements between lines.

Studio currently compensates in its renderer by inspecting paragraph lines and
calling `split('|')`. That workaround is not an authoritative parser and cannot
distinguish delimiters from escaped pipes or pipes inside inline code.

## Design

### Typed Parser Contract

Add a table member to `MarkdownBlock` containing:

- `headers`: header cell source strings;
- `alignments`: `left`, `center`, `right`, or `null` per column;
- `rows`: body cell source strings.

The parser recognizes a table only when a candidate header is immediately
followed by a valid GFM delimiter row and both rows have the same non-zero
column count. Each delimiter cell must contain at least three hyphens, with
optional leading and trailing colons.

The row splitter scans a line once. It ignores escaped pipes and pipes inside
inline code spans, removes only optional outer pipes, and unescapes escaped
pipes in returned cell text. Body rows are normalized to the header width:
missing cells become empty strings and additional cells are ignored, matching
the table's declared column contract.

Code fences remain higher priority than table recognition. An incomplete table
during streaming stays a paragraph until the delimiter row arrives, after
which the normal React rerender produces the table.

### Presentation

Chat renders the block as:

```text
scroll region
  table
    thead > tr > th scope="col"
    tbody > tr > td
```

Every cell reuses the existing inline tokenizer and renderer, so inline code,
links, and bold text retain current behavior. Column alignment is applied to
both header and body cells.

The scroll region is capped at the message width, uses horizontal overflow,
and is keyboard focusable with an accessible label. The table fills available
width but may grow wider when content requires it. Cells allow safe wrapping
for long identifiers; inline code can wrap without enlarging the message
container. Styling follows the existing quiet Chat palette and compact type
scale.

### Existing Parser Consumers

The parser is also used by Studio run output and Explorer content. Both must
handle the typed table block so the shared contract remains exhaustive:

- Studio removes its paragraph-sniffing table workaround and renders the typed
  table with its current table visual treatment.
- Explorer renders the typed table using its existing theme variables.

These updates preserve Studio behavior and prevent a new table block from
falling through to paragraph-only code.

## Error Handling And Safety

- Invalid delimiter rows do not create tables.
- Header/delimiter width mismatches do not create tables.
- Missing body cells render as empty cells.
- Raw HTML remains plain text because this change does not introduce an HTML
  parser or `dangerouslySetInnerHTML`.
- Link behavior remains governed by the existing inline token renderer.

## Testing

Follow red-green-refactor:

1. Add parser tests that expect a typed table with alignment, inline code, and
   escaped pipe handling; confirm they fail because the parser returns a
   paragraph.
2. Add a Chat presentation test that expects table, columnheader, cell, inline
   code, and responsive scroll-region semantics; confirm it fails because no
   table is rendered.
3. Implement the smallest parser and renderer changes to make those tests pass.
4. Run the focused tests, `tsc`, the full `test:ui` suite, production build, and
   the repository test-stability guard.
5. Inspect desktop and mobile Chat rendering in a local browser when a runnable
   authenticated or mocked Chat route is available.

## Alternatives Rejected

- Chat-only paragraph sniffing: smaller diff, but duplicates Studio's weak
  workaround and leaves table semantics outside the parser.
- `react-markdown` plus `remark-gfm`: standards-based, but adds bundle and lock
  file scope, creates a second rendering path, and is unnecessary for the
  requested table extension.
