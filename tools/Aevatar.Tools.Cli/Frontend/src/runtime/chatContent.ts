// Pattern for DSML-style function call blocks: < | DSML | function_calls>...</ | DSML | function_calls>
// The pipe may be ASCII | (U+007C) or full-width ｜ (U+FF5C) — some LLMs emit full-width in CJK context.
const PIPE = String.raw`[\|\uff5c]`;
const DSML_FUNCTION_CALLS_BLOCK_PATTERN =
  String.raw`<\s*` + PIPE + String.raw`\s*DSML\s*` + PIPE + String.raw`\s*function_calls\s*>[\s\S]*?<\/\s*` + PIPE + String.raw`\s*DSML\s*` + PIPE + String.raw`\s*function_calls\s*>`;
const DSML_FUNCTION_CALLS_OPEN_PATTERN =
  String.raw`<\s*` + PIPE + String.raw`\s*DSML\s*` + PIPE + String.raw`\s*function_calls\s*>`;
const DSML_FUNCTION_CALLS_CLOSE_PATTERN =
  String.raw`<\/\s*` + PIPE + String.raw`\s*DSML\s*` + PIPE + String.raw`\s*function_calls\s*>`;

// Pattern for standard XML function call blocks: <function_calls>...</function_calls>
// Allows optional whitespace before closing > to match backend tolerance
const XML_FUNCTION_CALLS_BLOCK_PATTERN =
  String.raw`<function_calls\s*>[\s\S]*?<\/function_calls\s*>`;
const XML_FUNCTION_CALLS_OPEN_PATTERN =
  String.raw`<function_calls\s*>`;
const XML_FUNCTION_CALLS_CLOSE_PATTERN =
  String.raw`<\/function_calls\s*>`;

const MARKDOWN_OR_BARE_LINK_PATTERN =
  /(\[([^\]]+)\]\(((?:https?:\/\/|www\.)[^\s)]+)\))|((?:https?:\/\/|www\.)[^\s<]+[^<.,:;"')\]\s])/gi;

export type InlineContentToken =
  | { kind: 'text'; text: string; bold: boolean }
  | { kind: 'code'; text: string }
  | { kind: 'link'; text: string; href: string; bold: boolean };

export type MarkdownBlock =
  | { kind: 'paragraph'; lines: string[] }
  | { kind: 'heading'; level: number; text: string }
  | { kind: 'blockquote'; lines: string[] }
  | { kind: 'unordered-list'; items: string[] }
  | { kind: 'ordered-list'; items: string[] }
  | { kind: 'code'; lang: string; code: string }
  | { kind: 'thematic-break' };

/** All block pattern pairs: [completeBlockRegex, openRegex, closeRegex] */
const FUNCTION_CALL_PATTERNS: [string, string, string][] = [
  [DSML_FUNCTION_CALLS_BLOCK_PATTERN, DSML_FUNCTION_CALLS_OPEN_PATTERN, DSML_FUNCTION_CALLS_CLOSE_PATTERN],
  [XML_FUNCTION_CALLS_BLOCK_PATTERN, XML_FUNCTION_CALLS_OPEN_PATTERN, XML_FUNCTION_CALLS_CLOSE_PATTERN],
];

export function sanitizeAssistantMessageContent(content: string): string {
  if (!content) {
    return '';
  }

  let sanitized = content;

  // Strip all complete function call blocks (DSML + XML formats)
  for (const [blockPattern] of FUNCTION_CALL_PATTERNS) {
    sanitized = sanitized.replace(new RegExp(blockPattern, 'gi'), '\n');
  }

  // Hide any dangling (unclosed) blocks while streaming
  const danglingBlockStart = findDanglingFunctionCallBlockStart(sanitized);
  if (danglingBlockStart >= 0) {
    sanitized = sanitized.slice(0, danglingBlockStart);
  }

  return sanitized
    .replace(/\n[ \t]+\n/g, '\n\n')
    .replace(/\n{3,}/g, '\n\n')
    .trimEnd();
}

function findDanglingFunctionCallBlockStart(content: string): number {
  let earliest = -1;

  for (const [, openPatternStr, closePatternStr] of FUNCTION_CALL_PATTERNS) {
    const result = findDanglingStart(content, openPatternStr, closePatternStr);
    if (result >= 0 && (earliest < 0 || result < earliest)) {
      earliest = result;
    }
  }

  return earliest;
}

function findDanglingStart(content: string, openPatternStr: string, closePatternStr: string): number {
  let searchIndex = 0;

  while (searchIndex < content.length) {
    const openPattern = new RegExp(openPatternStr, 'gi');
    openPattern.lastIndex = searchIndex;
    const openMatch = openPattern.exec(content);
    if (!openMatch) {
      return -1;
    }

    const closePattern = new RegExp(closePatternStr, 'gi');
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

    if (codePart.startsWith('`') && codePart.endsWith('`')) {
      tokens.push({ kind: 'code', text: codePart.slice(1, -1) });
      continue;
    }

    const boldParts = codePart.split(/(\*\*[^*]+\*\*)/g);
    for (const boldPart of boldParts) {
      if (!boldPart) {
        continue;
      }

      const isBold = boldPart.startsWith('**') && boldPart.endsWith('**');
      appendLinkifiedTokens(tokens, isBold ? boldPart.slice(2, -2) : boldPart, isBold);
    }
  }

  return tokens;
}

export function parseMarkdownBlocks(text: string): MarkdownBlock[] {
  if (!text) {
    return [];
  }

  const normalized = text.replace(/\r\n?/g, '\n');
  const lines = normalized.split('\n');
  const blocks: MarkdownBlock[] = [];
  let paragraphLines: string[] = [];

  const flushParagraph = () => {
    if (paragraphLines.length === 0) {
      return;
    }

    blocks.push({ kind: 'paragraph', lines: paragraphLines });
    paragraphLines = [];
  };

  for (let index = 0; index < lines.length; index++) {
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
      while (cursor < lines.length && !lines[cursor].trim().startsWith('```')) {
        codeLines.push(lines[cursor]);
        cursor++;
      }

      blocks.push({
        kind: 'code',
        lang: codeFence[1] || '',
        code: codeLines.join('\n'),
      });
      index = cursor < lines.length ? cursor : lines.length;
      continue;
    }

    const heading = trimmed.match(/^(#{1,6})\s+(.+)$/);
    if (heading) {
      flushParagraph();
      blocks.push({
        kind: 'heading',
        level: heading[1].length,
        text: heading[2].trim(),
      });
      continue;
    }

    if (/^([-*_])(?:\s*\1){2,}$/.test(trimmed)) {
      flushParagraph();
      blocks.push({ kind: 'thematic-break' });
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

        quoteLines.push(currentTrimmed.replace(/^>\s?/, ''));
        cursor++;
      }

      blocks.push({ kind: 'blockquote', lines: quoteLines });
      index = cursor - 1;
      continue;
    }

    if (/^\s*[-*+]\s+/.test(line)) {
      flushParagraph();
      const items: string[] = [];
      let cursor = index;
      while (cursor < lines.length && /^\s*[-*+]\s+/.test(lines[cursor])) {
        items.push(lines[cursor].replace(/^\s*[-*+]\s+/, '').trim());
        cursor++;
      }

      blocks.push({ kind: 'unordered-list', items });
      index = cursor - 1;
      continue;
    }

    if (/^\s*\d+\.\s+/.test(line)) {
      flushParagraph();
      const items: string[] = [];
      let cursor = index;
      while (cursor < lines.length && /^\s*\d+\.\s+/.test(lines[cursor])) {
        items.push(lines[cursor].replace(/^\s*\d+\.\s+/, '').trim());
        cursor++;
      }

      blocks.push({ kind: 'ordered-list', items });
      index = cursor - 1;
      continue;
    }

    paragraphLines.push(line);
  }

  flushParagraph();
  return blocks;
}

export function markdownToPlainText(text: string): string {
  if (!text) {
    return '';
  }

  return parseMarkdownBlocks(text)
    .map(block => markdownBlockToPlainText(block))
    .filter(Boolean)
    .join('\n\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

function appendLinkifiedTokens(tokens: InlineContentToken[], text: string, bold: boolean): void {
  let lastIndex = 0;
  for (const match of text.matchAll(MARKDOWN_OR_BARE_LINK_PATTERN)) {
    const matchIndex = match.index ?? 0;
    if (matchIndex > lastIndex) {
      tokens.push({ kind: 'text', text: text.slice(lastIndex, matchIndex), bold });
    }

    const markdownLabel = match[2];
    const markdownHref = match[3];
    const bareHref = match[4];
    const href = normalizeExternalHref(markdownHref || bareHref || '');
    const label = markdownLabel || bareHref || '';
    tokens.push({ kind: 'link', text: label, href, bold });

    lastIndex = matchIndex + match[0].length;
  }

  if (lastIndex < text.length) {
    tokens.push({ kind: 'text', text: text.slice(lastIndex), bold });
  }
}

function normalizeExternalHref(rawHref: string): string {
  if (!rawHref) {
    return '';
  }

  return rawHref.startsWith('www.')
    ? `https://${rawHref}`
    : rawHref;
}

function markdownBlockToPlainText(block: MarkdownBlock): string {
  switch (block.kind) {
    case 'code':
      return block.code.trim();
    case 'heading':
      return inlineContentToPlainText(block.text);
    case 'blockquote':
      return markdownLinesToPlainText(block.lines);
    case 'unordered-list':
      return block.items
        .map(item => `- ${inlineContentToPlainText(item)}`)
        .join('\n');
    case 'ordered-list':
      return block.items
        .map((item, index) => `${index + 1}. ${inlineContentToPlainText(item)}`)
        .join('\n');
    case 'thematic-break':
      return '---';
    case 'paragraph':
    default:
      return markdownLinesToPlainText(block.lines);
  }
}

function markdownLinesToPlainText(lines: string[]): string {
  return lines
    .map(line => inlineContentToPlainText(line))
    .join('\n');
}

function inlineContentToPlainText(text: string): string {
  return tokenizeInlineContent(text)
    .map(token => {
      if (token.kind === 'link') {
        return token.text === token.href
          ? token.href
          : `${token.text} (${token.href})`;
      }

      return token.text;
    })
    .join('');
}
