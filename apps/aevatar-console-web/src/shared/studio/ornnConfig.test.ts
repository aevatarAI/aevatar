import { getOrnnRuntimeConfig } from './ornnConfig';

describe('Ornn runtime config', () => {
  const originalEnv = { ...process.env };

  beforeEach(() => {
    process.env = {
      ...originalEnv,
    };
    delete process.env.ORNN_BASE_URL;
    window.history.replaceState({}, '', '/studio/settings');
  });

  afterEach(() => {
    process.env = originalEnv;
  });

  it('falls back to the default Ornn base URL', () => {
    expect(getOrnnRuntimeConfig()).toEqual({
      baseUrl: 'https://ornn.chrono-ai.fun',
      configurationError: undefined,
    });
  });

  it('normalizes local hostnames without an explicit scheme', () => {
    process.env.ORNN_BASE_URL = 'localhost:3010';

    expect(getOrnnRuntimeConfig()).toEqual({
      baseUrl: 'http://localhost:3010',
      configurationError: undefined,
    });
  });

  it('accepts quoted env values', () => {
    process.env.ORNN_BASE_URL = '"https://ornn.example.com/"';

    expect(getOrnnRuntimeConfig()).toEqual({
      baseUrl: 'https://ornn.example.com',
      configurationError: undefined,
    });
  });

  it('supports root-relative Ornn URLs', () => {
    process.env.ORNN_BASE_URL = '/ornn';

    expect(getOrnnRuntimeConfig()).toEqual({
      baseUrl: `${window.location.origin}/ornn`,
      configurationError: undefined,
    });
  });

  it('reports invalid Ornn URLs', () => {
    process.env.ORNN_BASE_URL = '://bad-url';

    expect(getOrnnRuntimeConfig()).toEqual({
      baseUrl: '',
      configurationError:
        'ORNN_BASE_URL must be a valid http(s) URL or a root-relative path such as /ornn.',
    });
  });
});

