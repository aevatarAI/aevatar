import type { NyxIDAuthSession } from '@/shared/auth/session';

// Deliberately unsigned test credential. It can only be used with mocked HTTP.
export function createNyxIDServiceSession(
  claims: Record<string, unknown> = {},
): NyxIDAuthSession {
  const expiresAt = Date.now() + 3_600_000;
  const payload = Buffer.from(
    JSON.stringify({
      sub: 'user-test',
      token_type: 'access',
      exp: Math.floor(expiresAt / 1000),
      allow_all_services: false,
      allowed_service_ids: [
        'user-service-github',
        'user-service-personal',
        'user-service-slack',
        'user-service-model',
      ],
      ...claims,
    }),
  ).toString('base64url');
  return {
    tokens: {
      accessToken: `eyJhbGciOiJSUzI1NiJ9.${payload}.TEST_ONLY_SIGNATURE`,
      tokenType: 'Bearer',
      expiresIn: 3600,
      expiresAt,
    },
    user: { sub: 'user-test' },
  };
}
