export const AI_OVERVIEW_ROUTE = '/ai';
export const AI_CHAT_ROUTE = '/ai/chat';
export const AI_AGENTS_ROUTE = '/ai/agents';
export const AI_MODELS_ROUTE = '/ai/models';

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

export function buildLegacyChatRedirectHref(
  search = typeof window === 'undefined' ? '' : window.location.search,
  hash = typeof window === 'undefined' ? '' : window.location.hash,
): string {
  return `${AI_CHAT_ROUTE}${search}${hash}`;
}
