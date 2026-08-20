export const AI_OVERVIEW_ROUTE = '/ai';
export const AI_CHAT_ROUTE = '/ai/chat';
export const AI_AGENTS_ROUTE = '/ai/agents';
export const AI_MODELS_ROUTE = '/ai/models';
export const AI_ACTIVITY_ROUTE = '/ai/activity';
export const AI_ACTIVITY_RUNS_ROUTE = `${AI_ACTIVITY_ROUTE}/runs`;

function normalizeIdentity(value: string): string {
  return value.trim();
}

export function buildAIChatHref(conversationId?: string | null): string {
  const normalizedConversationId = normalizeIdentity(conversationId ?? '');
  if (!normalizedConversationId) {
    return AI_CHAT_ROUTE;
  }

  const query = new URLSearchParams({
    conversationId: normalizedConversationId,
  });
  return `${AI_CHAT_ROUTE}?${query.toString()}`;
}

/**
 * Run ids are issued by the workflow read model. They are opaque values and
 * must be encoded as one route segment rather than interpreted by the UI.
 */
export function buildAIActivityRunDetailHref(runId?: string | null): string {
  const normalizedRunId = normalizeIdentity(runId ?? '');
  if (!normalizedRunId) {
    return AI_ACTIVITY_ROUTE;
  }

  return `${AI_ACTIVITY_RUNS_ROUTE}/${encodeURIComponent(normalizedRunId)}`;
}

export type AIActivityRunDetailRoute = {
  runId: string;
};

/**
 * Parse a detail pathname without relying on a router's decoded params. A
 * malformed or multi-segment identity fails closed.
 */
export function parseAIActivityRunDetailPath(
  pathname: string,
): AIActivityRunDetailRoute | null {
  const normalizedPathname =
    pathname.split(/[?#]/, 1)[0]?.replace(/\/+$/, '') || '/';
  const prefix = `${AI_ACTIVITY_RUNS_ROUTE}/`;
  if (!normalizedPathname.startsWith(prefix)) {
    return null;
  }

  const encodedRunId = normalizedPathname.slice(prefix.length);
  if (!encodedRunId || encodedRunId.includes('/')) {
    return null;
  }

  try {
    const runId = decodeURIComponent(encodedRunId).trim();
    return runId ? { runId } : null;
  } catch {
    return null;
  }
}

export function buildLegacyChatRedirectHref(
  search = typeof window === 'undefined' ? '' : window.location.search,
  hash = typeof window === 'undefined' ? '' : window.location.hash,
): string {
  return `${AI_CHAT_ROUTE}${search}${hash}`;
}
