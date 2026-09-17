import type { NyxIDAuthSession } from '@/shared/auth/session';
import type { StudioAuthSession } from '@/shared/studio/models';
import { resolveWorkflowActivityAccount } from './resolveWorkflowActivityAccount';

function createStoredSession(sub = 'user-abigail'): NyxIDAuthSession {
  return {
    tokens: {
      accessToken: 'token',
      expiresAt: Date.now() + 60_000,
      expiresIn: 60,
      tokenType: 'Bearer',
    },
    user: {
      email: 'abigail@example.test',
      email_verified: true,
      groups: ['platform'],
      name: 'Abigail Deng',
      picture: 'https://example.test/abigail.png',
      roles: ['operator'],
      sub,
    },
  };
}

function createBackendSession(
  overrides: Partial<StudioAuthSession> = {},
): StudioAuthSession {
  return {
    authenticated: true,
    enabled: true,
    providerDisplayName: 'NyxID',
    session: {
      authenticated: true,
      expiresAtUtc: '2099-08-05T10:00:00Z',
      scopeId: 'scope-alpha',
    },
    ...overrides,
  };
}

describe('resolveWorkflowActivityAccount', () => {
  it('keeps backend profile facts authoritative when local storage has the same subject', () => {
    const result = resolveWorkflowActivityAccount(
      createBackendSession({ subject: 'user-abigail', profile: null }),
      createStoredSession(),
    );

    expect(result.principal).toEqual({
      authenticated: true,
      displayName: '',
      picture: null,
    });
    expect(result.auth?.profile).toBeNull();
    expect(result.auth?.name).toBeUndefined();
    expect(result.auth?.email).toBeUndefined();
    expect(result.auth?.picture).toBeUndefined();
  });

  it('does not reuse a stored profile for a different backend subject', () => {
    const result = resolveWorkflowActivityAccount(
      createBackendSession({ subject: 'user-calvin', profile: null }),
      createStoredSession(),
    );

    expect(result.principal).toEqual({
      authenticated: true,
      displayName: '',
      picture: null,
    });
    expect(result.auth?.profile).toBeNull();
  });

  it('keeps a restorable browser principal when backend account state is unauthenticated', () => {
    const result = resolveWorkflowActivityAccount(
      createBackendSession({
        authenticated: false,
        subject: 'user-abigail',
        session: { authenticated: false },
      }),
      createStoredSession(),
    );

    expect(result.auth?.authenticated).toBe(false);
    expect(result.principal).toEqual({
      authenticated: true,
      displayName: 'Abigail Deng',
      picture: 'https://example.test/abigail.png',
    });
  });

  it('uses the stored profile while backend account facts are unavailable', () => {
    const result = resolveWorkflowActivityAccount(
      undefined,
      createStoredSession(),
    );

    expect(result.auth).toBeUndefined();
    expect(result.principal).toEqual({
      authenticated: true,
      displayName: 'Abigail Deng',
      picture: 'https://example.test/abigail.png',
    });
  });
});
