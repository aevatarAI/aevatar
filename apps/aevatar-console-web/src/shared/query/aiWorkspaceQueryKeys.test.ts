import { aiWorkspaceQueryKeys } from './aiWorkspaceQueryKeys';

describe('aiWorkspaceQueryKeys', () => {
  it('isolates cached workspace data by principal, session, and scope', () => {
    const alpha = {
      principalId: 'user-alpha',
      sessionExpiresAt: 1_700_003_600_000,
      scopeId: 'scope-alpha',
    };
    const otherPrincipal = {
      ...alpha,
      principalId: 'user-beta',
    };
    const refreshedSession = {
      ...alpha,
      sessionExpiresAt: alpha.sessionExpiresAt + 3_600_000,
    };
    const otherScope = {
      ...alpha,
      scopeId: 'scope-beta',
    };

    expect(aiWorkspaceQueryKeys.context(alpha)).not.toEqual(
      aiWorkspaceQueryKeys.context(otherPrincipal),
    );
    expect(aiWorkspaceQueryKeys.context(alpha)).not.toEqual(
      aiWorkspaceQueryKeys.context(refreshedSession),
    );

    for (const buildKey of [
      aiWorkspaceQueryKeys.overview,
      aiWorkspaceQueryKeys.models,
      aiWorkspaceQueryKeys.conversations,
    ]) {
      expect(buildKey(alpha)).not.toEqual(buildKey(otherPrincipal));
      expect(buildKey(alpha)).not.toEqual(buildKey(refreshedSession));
      expect(buildKey(alpha)).not.toEqual(buildKey(otherScope));
    }

    const agentsKey = aiWorkspaceQueryKeys.agents(alpha, { take: 24 });
    expect(agentsKey).not.toEqual(
      aiWorkspaceQueryKeys.agents(otherPrincipal, { take: 24 }),
    );
    expect(agentsKey).not.toEqual(
      aiWorkspaceQueryKeys.agents(refreshedSession, { take: 24 }),
    );
    expect(agentsKey).not.toEqual(
      aiWorkspaceQueryKeys.agents(otherScope, { take: 24 }),
    );
    expect(agentsKey).not.toEqual(
      aiWorkspaceQueryKeys.agents(alpha, {
        ownedCursor: 'owned-next',
        take: 24,
      }),
    );
    expect(agentsKey).not.toEqual(
      aiWorkspaceQueryKeys.agents(alpha, {
        systemCursor: 'system-next',
        take: 24,
      }),
    );
    expect(agentsKey).not.toEqual(
      aiWorkspaceQueryKeys.agents(alpha, { take: 48 }),
    );
  });
});
