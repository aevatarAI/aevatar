import { authFetch } from '@/shared/auth/fetch';
import { requestJson } from './client';

jest.mock('@/shared/auth/fetch', () => ({
  authFetch: jest.fn(),
}));

const mockAuthFetch = authFetch as jest.Mock;

describe('requestJson', () => {
  afterEach(() => {
    jest.clearAllMocks();
  });

  it('preserves an HTTP authorization status for callers that need honest recovery UI', async () => {
    mockAuthFetch.mockResolvedValue({
      ok: false,
      status: 403,
      statusText: 'Forbidden',
      text: async () =>
        JSON.stringify({
          code: 'SCOPE_ACCESS_DENIED',
          message: 'Forbidden',
        }),
    });

    await expect(
      requestJson('/api/scopes/scope-other/services', (value) => value),
    ).rejects.toMatchObject({
      code: 'SCOPE_ACCESS_DENIED',
      message: 'Forbidden',
      status: 403,
    });
  });
});
