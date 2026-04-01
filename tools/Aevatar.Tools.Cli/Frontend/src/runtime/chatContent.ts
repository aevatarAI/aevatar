const DSML_FUNCTION_CALLS_BLOCK_PATTERN =
  String.raw`<\s*\|\s*DSML\s*\|\s*function_calls\s*>[\s\S]*?<\/\s*\|\s*DSML\s*\|\s*function_calls\s*>`;
const DSML_FUNCTION_CALLS_OPEN_PATTERN =
  String.raw`<\s*\|\s*DSML\s*\|\s*function_calls\s*>`;
const DSML_FUNCTION_CALLS_CLOSE_PATTERN =
  String.raw`<\/\s*\|\s*DSML\s*\|\s*function_calls\s*>`;

export function sanitizeAssistantMessageContent(content: string): string {
  if (!content) {
    return '';
  }

  let sanitized = content.replace(new RegExp(DSML_FUNCTION_CALLS_BLOCK_PATTERN, 'gi'), '\n');
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
  let searchIndex = 0;

  while (searchIndex < content.length) {
    const openPattern = new RegExp(DSML_FUNCTION_CALLS_OPEN_PATTERN, 'gi');
    openPattern.lastIndex = searchIndex;
    const openMatch = openPattern.exec(content);
    if (!openMatch) {
      return -1;
    }

    const closePattern = new RegExp(DSML_FUNCTION_CALLS_CLOSE_PATTERN, 'gi');
    closePattern.lastIndex = openMatch.index + openMatch[0].length;
    const closeMatch = closePattern.exec(content);
    if (!closeMatch) {
      return openMatch.index;
    }

    searchIndex = closeMatch.index + closeMatch[0].length;
  }

  return -1;
}
