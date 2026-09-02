import { readResponseError, readResponseErrorDetails } from './error';

describe('readResponseError', () => {
  it('includes ASP.NET validation problem details', async () => {
    await expect(
      readResponseError({
        status: 400,
        statusText: 'Bad Request',
        text: async () =>
          JSON.stringify({
            title: 'One or more validation errors occurred.',
            status: 400,
            errors: {
              '$.serviceInvocation.payload.value': [
                'The JSON value could not be converted to Google.Protobuf.ByteString.',
              ],
            },
          }),
      }),
    ).resolves.toBe(
      'One or more validation errors occurred.: $.serviceInvocation.payload.value: The JSON value could not be converted to Google.Protobuf.ByteString.',
    );
  });

  it('reads machine error codes from the backend error field', async () => {
    await expect(
      readResponseErrorDetails({
        status: 502,
        statusText: 'Bad Gateway',
        text: async () =>
          JSON.stringify({
            error: 'issued_binding_invalid',
            detail: 'The issued binding could not be adopted.',
          }),
      }),
    ).resolves.toMatchObject({
      code: 'issued_binding_invalid',
      message: 'issued_binding_invalid',
      status: 502,
    });
  });

  it('preserves typed correlation and retry guidance from an API problem', async () => {
    await expect(
      readResponseErrorDetails({
        status: 429,
        statusText: 'Too Many Requests',
        text: async () =>
          JSON.stringify({
            code: 'RATE_LIMITED',
            correlationId: 'corr-alpha',
            message: 'The request quota has been reached.',
            retryAfterSeconds: 17,
          }),
      }),
    ).resolves.toEqual({
      code: 'RATE_LIMITED',
      correlationId: 'corr-alpha',
      message: 'The request quota has been reached.',
      retryAfterSeconds: 17,
      status: 429,
    });
  });

  it('reads the standard Retry-After header when the body omits a countdown', async () => {
    await expect(
      readResponseErrorDetails({
        headers: {
          get: (name: string) =>
            name.toLowerCase() === 'retry-after' ? '23' : null,
        },
        status: 429,
        statusText: 'Too Many Requests',
        text: async () =>
          JSON.stringify({
            code: 'RATE_LIMITED',
            message: 'The request quota has been reached.',
          }),
      }),
    ).resolves.toMatchObject({ retryAfterSeconds: 23 });
  });
});
