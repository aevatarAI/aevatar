import type { StudioAuthSession } from '@/shared/studio/models';
import { buildAccountIdentity } from './accountIdentity';

const NOW = Date.parse('2026-08-06T08:00:00Z');

function createAuthSession(
  overrides: Partial<StudioAuthSession> = {},
): StudioAuthSession {
  return {
    enabled: true,
    authenticated: true,
    providerDisplayName: 'NyxID',
    profile: {
      subject: 'user-alpha',
      name: 'Ada Operator',
      email: 'ada@example.test',
      emailVerified: true,
      picture: null,
      roles: ['operator'],
      groups: ['platform'],
    },
    session: {
      authenticated: true,
      providerDisplayName: 'NyxID',
      scopeId: 'scope-alpha',
      scopeSource: 'nyxid-session',
      expiresAtUtc: '2026-08-08T08:00:00Z',
    },
    ...overrides,
  };
}

describe('account identity presentation', () => {
  it.each([
    {
      label: 'active',
      auth: createAuthSession(),
      expected: 'active',
    },
    {
      label: 'expiring soon',
      auth: createAuthSession({
        session: {
          authenticated: true,
          expiresAtUtc: '2026-08-06T12:00:00Z',
        },
      }),
      expected: 'expiring_soon',
    },
    {
      label: 'expired',
      auth: createAuthSession({
        authenticated: false,
        session: {
          authenticated: false,
          expiresAtUtc: '2026-08-06T07:59:00Z',
        },
      }),
      expected: 'expired',
    },
    {
      label: 'invalid',
      auth: createAuthSession({
        authenticated: true,
        session: {
          authenticated: false,
          expiresAtUtc: '2026-08-08T08:00:00Z',
        },
      }),
      expected: 'invalid',
    },
  ])('classifies $label without contradicting authentication facts', ({
    auth,
    expected,
  }) => {
    expect(buildAccountIdentity(auth, NOW, 'en-US').sessionState).toBe(
      expected,
    );
  });

  it('formats expiry in local time with a timezone and relative time', () => {
    const presentation = buildAccountIdentity(
      createAuthSession({
        session: {
          authenticated: true,
          expiresAtUtc: '2026-08-06T12:00:00Z',
        },
      }),
      NOW,
      'en-US',
    );

    if (presentation.expiry.kind !== 'value') {
      throw new Error('Expected a formatted expiry value.');
    }
    expect(presentation.expiry.value).toContain('in 4 hours');
    expect(presentation.expiry.value).toMatch(/UTC|GMT/);
  });

  it('does not guess that missing profile data was hidden by policy', () => {
    const missing = buildAccountIdentity(
      createAuthSession({
        profile: {
          subject: 'user-alpha',
          name: 'Ada Operator',
          email: null,
          emailVerified: null,
          picture: null,
          roles: [],
          groups: [],
        },
      }),
      NOW,
      'en-US',
    );
    const missingProfile = buildAccountIdentity(
      createAuthSession({
        name: 'Ada Operator',
        profile: null,
      }),
      NOW,
      'en-US',
    );

    expect(missing.email.kind).toBe('not_provided');
    expect(missing.emailVerified).toBeNull();
    expect(missingProfile.email.kind).toBe('not_provided');
  });
});
