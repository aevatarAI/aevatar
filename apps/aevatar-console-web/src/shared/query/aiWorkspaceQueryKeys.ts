import { loadStoredAuthSession } from '@/shared/auth/session';

export type AIWorkspaceSessionAuthority = {
  principalId: string;
  sessionExpiresAt: number;
};

export type AIWorkspaceScopeAuthority = AIWorkspaceSessionAuthority & {
  scopeId: string;
};

export function readAIWorkspaceSessionAuthority(): AIWorkspaceSessionAuthority {
  const session = loadStoredAuthSession();
  return {
    principalId: session?.user.sub.trim() || 'unauthenticated',
    sessionExpiresAt: session?.tokens.expiresAt ?? 0,
  };
}

export const aiWorkspaceQueryKeys = {
  root: ['ai-workspace'] as const,
  context(authority: AIWorkspaceSessionAuthority) {
    return [
      'ai-workspace',
      'context',
      authority.principalId,
      authority.sessionExpiresAt,
    ] as const;
  },
  overview(authority: AIWorkspaceScopeAuthority) {
    return [
      'ai-workspace',
      'overview',
      authority.principalId,
      authority.sessionExpiresAt,
      authority.scopeId,
    ] as const;
  },
  agents(
    authority: AIWorkspaceScopeAuthority,
    input: {
      ownedCursor?: string;
      systemCursor?: string;
      take?: number;
    },
  ) {
    return [
      'ai-workspace',
      'agents',
      authority.principalId,
      authority.sessionExpiresAt,
      authority.scopeId,
      input.ownedCursor ?? '',
      input.systemCursor ?? '',
      input.take ?? null,
    ] as const;
  },
  models(authority: AIWorkspaceScopeAuthority) {
    return [
      'ai-workspace',
      'models',
      authority.principalId,
      authority.sessionExpiresAt,
      authority.scopeId,
    ] as const;
  },
  conversations(authority: AIWorkspaceScopeAuthority) {
    return [
      'chat-conversations',
      authority.principalId,
      authority.sessionExpiresAt,
      authority.scopeId,
    ] as const;
  },
  activityConversations(
    authority: AIWorkspaceScopeAuthority,
    input: { take?: number } = {},
  ) {
    return [
      'ai-workspace',
      'activity',
      'conversations',
      authority.principalId,
      authority.sessionExpiresAt,
      authority.scopeId,
      input.take ?? null,
    ] as const;
  },
  activityRuns(
    authority: AIWorkspaceScopeAuthority,
    input: {
      status?: string;
      origins?: readonly string[];
      workflowId?: string;
      q?: string;
      from?: string;
      to?: string;
      take?: number;
      includeTotalCount?: boolean;
    } = {},
  ) {
    return [
      'ai-workspace',
      'activity',
      'runs',
      authority.principalId,
      authority.sessionExpiresAt,
      authority.scopeId,
      input.status ?? '',
      [...(input.origins ?? [])],
      input.workflowId ?? '',
      input.q ?? '',
      input.from ?? '',
      input.to ?? '',
      input.take ?? null,
      input.includeTotalCount ?? false,
    ] as const;
  },
  activityRunDetail(authority: AIWorkspaceScopeAuthority, runId: string) {
    return [
      'ai-workspace',
      'activity',
      'run-detail',
      authority.principalId,
      authority.sessionExpiresAt,
      authority.scopeId,
      runId,
    ] as const;
  },
};
