const PIPE = String.raw`[\|\uff5c]`;
const DSML_FUNCTION_CALLS_BLOCK_PATTERN =
  String.raw`<\s*` +
  PIPE +
  String.raw`\s*DSML\s*` +
  PIPE +
  String.raw`\s*function_calls\s*>[\s\S]*?<\/\s*` +
  PIPE +
  String.raw`\s*DSML\s*` +
  PIPE +
  String.raw`\s*function_calls\s*>`;
const DSML_FUNCTION_CALLS_OPEN_PATTERN =
  String.raw`<\s*` +
  PIPE +
  String.raw`\s*DSML\s*` +
  PIPE +
  String.raw`\s*function_calls\s*>`;
const DSML_FUNCTION_CALLS_CLOSE_PATTERN =
  String.raw`<\/\s*` +
  PIPE +
  String.raw`\s*DSML\s*` +
  PIPE +
  String.raw`\s*function_calls\s*>`;

const XML_FUNCTION_CALLS_BLOCK_PATTERN =
  String.raw`<function_calls\s*>[\s\S]*?<\/function_calls\s*>`;
const XML_FUNCTION_CALLS_OPEN_PATTERN = String.raw`<function_calls\s*>`;
const XML_FUNCTION_CALLS_CLOSE_PATTERN = String.raw`<\/function_calls\s*>`;

const MARKDOWN_OR_BARE_LINK_PATTERN =
  /(\[([^\]]+)\]\(((?:https?:\/\/|www\.)[^\s)]+)\))|((?:https?:\/\/|www\.)[^\s<]+[^<.,:;"')\]\s])/gi;

export type InlineContentToken =
  | { kind: "text"; text: string; bold: boolean }
  | { kind: "code"; text: string }
  | { kind: "link"; text: string; href: string; bold: boolean };

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

const FUNCTION_CALL_PATTERNS: [string, string, string][] = [
  [
    DSML_FUNCTION_CALLS_BLOCK_PATTERN,
    DSML_FUNCTION_CALLS_OPEN_PATTERN,
    DSML_FUNCTION_CALLS_CLOSE_PATTERN,
  ],
  [
    XML_FUNCTION_CALLS_BLOCK_PATTERN,
    XML_FUNCTION_CALLS_OPEN_PATTERN,
    XML_FUNCTION_CALLS_CLOSE_PATTERN,
  ],
];

export function sanitizeAssistantMessageContent(content: string): string {
  if (!content) {
    return "";
  }

  let sanitized = content;
  for (const [blockPattern] of FUNCTION_CALL_PATTERNS) {
    sanitized = sanitized.replace(new RegExp(blockPattern, "gi"), "\n");
  }

  const danglingBlockStart = findDanglingFunctionCallBlockStart(sanitized);
  if (danglingBlockStart >= 0) {
    sanitized = sanitized.slice(0, danglingBlockStart);
  }

  return sanitized
    .replace(/\n[ \t]+\n/g, "\n\n")
    .replace(/\n{3,}/g, "\n\n")
    .trimEnd();
}

function findDanglingFunctionCallBlockStart(content: string): number {
  let earliest = -1;

  for (const [, openPattern, closePattern] of FUNCTION_CALL_PATTERNS) {
    const matchIndex = findDanglingStart(content, openPattern, closePattern);
    if (matchIndex >= 0 && (earliest < 0 || matchIndex < earliest)) {
      earliest = matchIndex;
    }
  }

  return earliest;
}

function findDanglingStart(
  content: string,
  openPatternStr: string,
  closePatternStr: string,
): number {
  let searchIndex = 0;

  while (searchIndex < content.length) {
    const openPattern = new RegExp(openPatternStr, "gi");
    openPattern.lastIndex = searchIndex;
    const openMatch = openPattern.exec(content);
    if (!openMatch) {
      return -1;
    }

    const closePattern = new RegExp(closePatternStr, "gi");
    closePattern.lastIndex = openMatch.index + openMatch[0].length;
    const closeMatch = closePattern.exec(content);
    if (!closeMatch) {
      return openMatch.index;
    }

    searchIndex = closeMatch.index + closeMatch[0].length;
  }

  return -1;
}

export function tokenizeInlineContent(text: string): InlineContentToken[] {
  if (!text) {
    return [];
  }

  const tokens: InlineContentToken[] = [];
  const codeParts = text.split(/(`[^`]+`)/g);
  for (const codePart of codeParts) {
    if (!codePart) {
      continue;
    }

    if (codePart.startsWith("`") && codePart.endsWith("`")) {
      tokens.push({ kind: "code", text: codePart.slice(1, -1) });
      continue;
    }

    const boldParts = codePart.split(/(\*\*[^*]+\*\*)/g);
    for (const boldPart of boldParts) {
      if (!boldPart) {
        continue;
      }

      const isBold = boldPart.startsWith("**") && boldPart.endsWith("**");
      appendLinkifiedTokens(
        tokens,
        isBold ? boldPart.slice(2, -2) : boldPart,
        isBold,
      );
    }
  }

  return tokens;
}

function appendLinkifiedTokens(
  tokens: InlineContentToken[],
  text: string,
  bold: boolean,
): void {
  let searchIndex = 0;
  MARKDOWN_OR_BARE_LINK_PATTERN.lastIndex = 0;

  for (let match = MARKDOWN_OR_BARE_LINK_PATTERN.exec(text); match; match = MARKDOWN_OR_BARE_LINK_PATTERN.exec(text)) {
    const matchText = match[0] ?? "";
    const matchIndex = match.index ?? 0;
    if (matchIndex > searchIndex) {
      tokens.push({
        bold,
        kind: "text",
        text: text.slice(searchIndex, matchIndex),
      });
    }

    const markdownLabel = match[2];
    const markdownHref = match[3];
    const bareHref = match[4];
    const href = (markdownHref || bareHref || "").trim();
    const label = (markdownLabel || bareHref || matchText).trim();

    tokens.push({
      bold,
      href: href.startsWith("http") ? href : `https://${href}`,
      kind: "link",
      text: label,
    });

    searchIndex = matchIndex + matchText.length;
  }

  if (searchIndex < text.length) {
    tokens.push({
      bold,
      kind: "text",
      text: text.slice(searchIndex),
    });
  }
}

const MARKDOWN_TABLE_DELIMITER_CELL_PATTERN = /^:?-{3,}:?$/;

function splitMarkdownTableRow(line: string): string[] | null {
  const source = line.trim();
  const cells: string[] = [];
  let cell = "";
  let codeSpanDelimiterLength = 0;
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
      if (codeSpanDelimiterLength === 0) {
        codeSpanDelimiterLength = runLength;
      } else if (codeSpanDelimiterLength === runLength) {
        codeSpanDelimiterLength = 0;
      }
      index += runLength - 1;
      continue;
    }

    if (character === "|" && codeSpanDelimiterLength === 0) {
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
    alignments.push(
      left && right ? "center" : right ? "right" : left ? "left" : null,
    );
  }

  return alignments;
}

export function parseMarkdownBlocks(text: string): MarkdownBlock[] {
  if (!text) {
    return [];
  }

  const normalized = text.replace(/\r\n?/g, "\n");
  const lines = normalized.split("\n");
  const blocks: MarkdownBlock[] = [];
  let paragraphLines: string[] = [];

  const flushParagraph = () => {
    if (paragraphLines.length === 0) {
      return;
    }

    blocks.push({ kind: "paragraph", lines: paragraphLines });
    paragraphLines = [];
  };

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const trimmed = line.trim();

    if (!trimmed) {
      flushParagraph();
      continue;
    }

    const codeFence = trimmed.match(/^```([^\s`]*)\s*$/);
    if (codeFence) {
      flushParagraph();
      const codeLines: string[] = [];
      let cursor = index + 1;
      while (cursor < lines.length && !lines[cursor].trim().startsWith("```")) {
        codeLines.push(lines[cursor]);
        cursor += 1;
      }

      blocks.push({
        kind: "code",
        lang: codeFence[1] || "",
        code: codeLines.join("\n"),
      });
      index = cursor < lines.length ? cursor : lines.length;
      continue;
    }

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

    const heading = trimmed.match(/^(#{1,6})\s+(.+)$/);
    if (heading) {
      flushParagraph();
      blocks.push({
        kind: "heading",
        level: heading[1].length,
        text: heading[2].trim(),
      });
      continue;
    }

    if (/^([-*_])(?:\s*\1){2,}$/.test(trimmed)) {
      flushParagraph();
      blocks.push({ kind: "thematic-break" });
      continue;
    }

    if (/^>\s?/.test(trimmed)) {
      flushParagraph();
      const quoteLines: string[] = [];
      let cursor = index;
      while (cursor < lines.length) {
        const currentTrimmed = lines[cursor].trim();
        if (!/^>\s?/.test(currentTrimmed)) {
          break;
        }

        quoteLines.push(currentTrimmed.replace(/^>\s?/, ""));
        cursor += 1;
      }

      blocks.push({ kind: "blockquote", lines: quoteLines });
      index = cursor - 1;
      continue;
    }

    if (/^\s*[-*+]\s+/.test(line)) {
      flushParagraph();
      const items: string[] = [];
      let cursor = index;
      while (cursor < lines.length && /^\s*[-*+]\s+/.test(lines[cursor])) {
        items.push(lines[cursor].replace(/^\s*[-*+]\s+/, "").trim());
        cursor += 1;
      }

      blocks.push({ kind: "unordered-list", items });
      index = cursor - 1;
      continue;
    }

    if (/^\s*\d+\.\s+/.test(line)) {
      flushParagraph();
      const items: string[] = [];
      let cursor = index;
      while (cursor < lines.length && /^\s*\d+\.\s+/.test(lines[cursor])) {
        items.push(lines[cursor].replace(/^\s*\d+\.\s+/, "").trim());
        cursor += 1;
      }

      blocks.push({ kind: "ordered-list", items });
      index = cursor - 1;
      continue;
    }

    paragraphLines.push(line);
  }

  flushParagraph();
  return blocks;
}
