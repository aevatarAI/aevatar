import { configurationApi } from './configurationApi';

describe('configurationApi', () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  it('checks configuration health through the local tool host capability', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      text: async () => 'ok',
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    const result = await configurationApi.getHealth();

    expect(fetchMock).toHaveBeenCalledWith('/api/configuration/health');
    expect(result).toBe('ok');
  });

  it('decodes source status with local runtime access visibility', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ok: true,
        mode: 'file',
        mongoConfigured: false,
        fileConfigured: true,
        localRuntimeAccess: true,
        paths: {
          root: '/tmp/.aevatar',
          secretsJson: '/tmp/.aevatar/secrets.json',
          configJson: '/tmp/.aevatar/config.json',
          connectorsJson: '/tmp/.aevatar/connectors.json',
          mcpJson: '/tmp/.aevatar/mcp.json',
          workflowsHome: '/tmp/.aevatar/workflows',
          workflowsRepo: '/repo/workflows',
          homeEnvValue: null,
          secretsPathEnvValue: null,
        },
        doctor: {
          paths: {
            root: '/tmp/.aevatar',
            secretsJson: '/tmp/.aevatar/secrets.json',
            configJson: '/tmp/.aevatar/config.json',
            connectorsJson: '/tmp/.aevatar/connectors.json',
            mcpJson: '/tmp/.aevatar/mcp.json',
            workflowsHome: '/tmp/.aevatar/workflows',
            workflowsRepo: '/repo/workflows',
            homeEnvValue: null,
            secretsPathEnvValue: null,
          },
          secrets: {
            path: '/tmp/.aevatar/secrets.json',
            exists: true,
            readable: true,
            writable: true,
            sizeBytes: 12,
            error: null,
          },
          config: {
            path: '/tmp/.aevatar/config.json',
            exists: true,
            readable: true,
            writable: true,
            sizeBytes: 24,
            error: null,
          },
          connectors: {
            path: '/tmp/.aevatar/connectors.json',
            exists: false,
            readable: true,
            writable: true,
            sizeBytes: null,
            error: null,
          },
          mcp: {
            path: '/tmp/.aevatar/mcp.json',
            exists: false,
            readable: true,
            writable: true,
            sizeBytes: null,
            error: null,
          },
          workflowsHome: {
            path: '/tmp/.aevatar/workflows',
            exists: true,
            readable: true,
            writable: true,
            sizeBytes: 0,
            error: null,
          },
          workflowsRepo: {
            path: '/repo/workflows',
            exists: true,
            readable: true,
            writable: false,
            sizeBytes: 0,
            error: null,
          },
        },
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(configurationApi.getSourceStatus()).resolves.toMatchObject({
      mode: 'file',
      localRuntimeAccess: true,
      fileConfigured: true,
    });
  });

  it('decodes workflow files from the configuration workspace', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ok: true,
        workflows: [
          {
            filename: 'draft.yaml',
            source: 'home',
            path: '/tmp/draft.yaml',
            sizeBytes: 42,
            lastModified: '2026-03-13T00:00:00Z',
          },
        ],
        source: 'all',
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(configurationApi.listWorkflows()).resolves.toEqual([
      {
        filename: 'draft.yaml',
        source: 'home',
        path: '/tmp/draft.yaml',
        sizeBytes: 42,
        lastModified: '2026-03-13T00:00:00Z',
      },
    ]);
  });

  it('loads connectors raw JSON from the configuration capability', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ok: true,
        json: '{\n  "connectors": []\n}',
        count: 0,
        exists: false,
        path: '/tmp/.aevatar/connectors.json',
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(configurationApi.getConnectorsRaw()).resolves.toEqual({
      json: '{\n  "connectors": []\n}',
      count: 0,
      exists: false,
      path: '/tmp/.aevatar/connectors.json',
    });
  });

  it('validates mcp raw JSON through the configuration capability', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ok: true,
        valid: true,
        message: 'valid mcp json',
        count: 2,
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      configurationApi.validateMcpRaw('{\n  "mcpServers": {}\n}'),
    ).resolves.toEqual({
      valid: true,
      message: 'valid mcp json',
      count: 2,
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/configuration/mcp/validate',
      expect.objectContaining({
        method: 'POST',
      }),
    );
  });

  it('lists structured MCP servers from the configuration capability', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ok: true,
        servers: [
          {
            name: 'local-tools',
            command: 'node',
            args: ['server.js'],
            env: { API_KEY: 'masked' },
            timeoutMs: 60000,
          },
        ],
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(configurationApi.listMcpServers()).resolves.toEqual([
      {
        name: 'local-tools',
        command: 'node',
        args: ['server.js'],
        env: { API_KEY: 'masked' },
        timeoutMs: 60000,
      },
    ]);
  });

  it('loads masked llm api key status for a provider instance', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ok: true,
        providerName: 'default',
        configured: true,
        masked: 'sk-****1234',
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(configurationApi.getLlmApiKey('default')).resolves.toEqual({
      providerName: 'default',
      configured: true,
      masked: 'sk-****1234',
    });
  });

  it('loads embeddings status from the configuration capability', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ok: true,
        embeddings: {
          enabled: true,
          providerType: 'deepseek',
          endpoint: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
          model: 'text-embedding-v3',
          configured: true,
          masked: 'sk-****1234',
        },
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(configurationApi.getEmbeddingsStatus()).resolves.toEqual({
      enabled: true,
      providerType: 'deepseek',
      endpoint: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
      model: 'text-embedding-v3',
      configured: true,
      masked: 'sk-****1234',
    });
  });

  it('loads skillsmp status from the configuration capability', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ok: true,
        configured: true,
        masked: 'sk-****5678',
        keyPath: 'SkillsMP:ApiKey',
        baseUrl: 'https://skillsmp.com',
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(configurationApi.getSkillsMpStatus()).resolves.toEqual({
      configured: true,
      masked: 'sk-****5678',
      keyPath: 'SkillsMP:ApiKey',
      baseUrl: 'https://skillsmp.com',
    });
  });

  it('generates secp256k1 key material through the configuration capability', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ok: true,
        backedUp: true,
        publicKeyHex: '04abcdef',
        status: {
          configured: true,
          privateKey: {
            configured: true,
            masked: 'abcd****wxyz',
            keyPath: 'Crypto:EcdsaSecp256k1:PrivateKeyHex',
            backupsPrefix: 'Crypto:EcdsaSecp256k1:Backups:',
            backupCount: 2,
          },
          publicKey: {
            configured: true,
            hex: '04abcdef',
          },
        },
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(configurationApi.generateSecp256k1()).resolves.toEqual({
      backedUp: true,
      publicKeyHex: '04abcdef',
      status: {
        configured: true,
        privateKey: {
          configured: true,
          masked: 'abcd****wxyz',
          keyPath: 'Crypto:EcdsaSecp256k1:PrivateKeyHex',
          backupsPrefix: 'Crypto:EcdsaSecp256k1:Backups:',
          backupCount: 2,
        },
        publicKey: {
          configured: true,
          hex: '04abcdef',
        },
      },
    });
  });
});
