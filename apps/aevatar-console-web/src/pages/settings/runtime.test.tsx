import { screen, waitFor } from "@testing-library/react";
import React from "react";
import { configurationApi } from "@/shared/api/configurationApi";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import { persistAuthSession } from "@/shared/auth/session";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import RuntimeSettingsPage from "./runtime";

jest.mock("@/shared/api/runtimeQueryApi", () => ({
  runtimeQueryApi: {
    getCapabilities: jest.fn(async () => ({
      schemaVersion: "capabilities.v1",
      generatedAtUtc: "2026-03-13T00:00:00Z",
      primitives: [{ name: "llm_call" }],
      connectors: [],
      workflows: [],
    })),
  },
}));

jest.mock("@/shared/api/configurationApi", () => ({
  configurationApi: {
    getHealth: jest.fn(async () => "ok"),
    getSourceStatus: jest.fn(async () => ({
      mode: "file",
      mongoConfigured: false,
      fileConfigured: true,
      localRuntimeAccess: true,
      paths: {
        root: "/tmp/.aevatar",
        secretsJson: "/tmp/.aevatar/secrets.json",
        configJson: "/tmp/.aevatar/config.json",
        connectorsJson: "/tmp/.aevatar/connectors.json",
        mcpJson: "/tmp/.aevatar/mcp.json",
        workflowsHome: "/tmp/.aevatar/workflows",
        workflowsRepo: "/repo/workflows",
        homeEnvValue: null,
        secretsPathEnvValue: null,
      },
      doctor: {
        paths: {
          root: "/tmp/.aevatar",
          secretsJson: "/tmp/.aevatar/secrets.json",
          configJson: "/tmp/.aevatar/config.json",
          connectorsJson: "/tmp/.aevatar/connectors.json",
          mcpJson: "/tmp/.aevatar/mcp.json",
          workflowsHome: "/tmp/.aevatar/workflows",
          workflowsRepo: "/repo/workflows",
          homeEnvValue: null,
          secretsPathEnvValue: null,
        },
        secrets: {
          path: "/tmp/.aevatar/secrets.json",
          exists: true,
          readable: true,
          writable: true,
          sizeBytes: 12,
          error: null,
        },
        config: {
          path: "/tmp/.aevatar/config.json",
          exists: true,
          readable: true,
          writable: true,
          sizeBytes: 24,
          error: null,
        },
        connectors: {
          path: "/tmp/.aevatar/connectors.json",
          exists: false,
          readable: true,
          writable: true,
          sizeBytes: null,
          error: null,
        },
        mcp: {
          path: "/tmp/.aevatar/mcp.json",
          exists: false,
          readable: true,
          writable: true,
          sizeBytes: null,
          error: null,
        },
        workflowsHome: {
          path: "/tmp/.aevatar/workflows",
          exists: true,
          readable: true,
          writable: true,
          sizeBytes: 0,
          error: null,
        },
        workflowsRepo: {
          path: "/repo/workflows",
          exists: true,
          readable: true,
          writable: false,
          sizeBytes: 0,
          error: null,
        },
      },
    })),
    listWorkflows: jest.fn(async () => [
      {
        filename: "incident_triage.yaml",
        source: "home",
        path: "/tmp/.aevatar/workflows/incident_triage.yaml",
        sizeBytes: 128,
        lastModified: "2026-03-13T00:00:00Z",
      },
    ]),
    getWorkflow: jest.fn(async () => ({
      filename: "incident_triage.yaml",
      source: "home",
      path: "/tmp/.aevatar/workflows/incident_triage.yaml",
      sizeBytes: 128,
      lastModified: "2026-03-13T00:00:00Z",
      content: "name: incident_triage\nsteps: []\n",
    })),
    listLlmProviders: jest.fn(async () => [
      {
        id: "openai",
        displayName: "OpenAI",
        category: "general",
        description: "OpenAI-compatible provider",
        recommended: true,
        configuredInstancesCount: 1,
      },
    ]),
    listLlmInstances: jest.fn(async () => [
      {
        name: "default",
        providerType: "openai",
        providerDisplayName: "OpenAI",
        model: "gpt-test",
        endpoint: "https://api.example.com",
      },
    ]),
    getLlmApiKey: jest.fn(async () => ({
      providerName: "default",
      configured: true,
      masked: "sk-****1234",
    })),
    setLlmApiKey: jest.fn(),
    deleteLlmApiKey: jest.fn(),
    saveLlmInstance: jest.fn(),
    deleteLlmInstance: jest.fn(),
    probeLlmTest: jest.fn(),
    probeLlmModels: jest.fn(),
    getLlmDefault: jest.fn(async () => "default"),
    getEmbeddingsStatus: jest.fn(async () => ({
      enabled: true,
      providerType: "deepseek",
      endpoint: "https://dashscope.aliyuncs.com/compatible-mode/v1",
      model: "text-embedding-v3",
      configured: true,
      masked: "sk-****1234",
    })),
    getEmbeddingsApiKey: jest.fn(async () => ({
      configured: true,
      masked: "sk-****1234",
      keyPath: "LLMProviders:Embeddings:ApiKey",
    })),
    saveEmbeddings: jest.fn(),
    deleteEmbeddings: jest.fn(),
    getWebSearchStatus: jest.fn(async () => ({
      enabled: true,
      effectiveEnabled: true,
      provider: "tavily",
      endpoint: "https://api.tavily.com",
      timeoutMs: 15000,
      searchDepth: "advanced",
      configured: true,
      masked: "tv-****1234",
      available: true,
    })),
    getWebSearchApiKey: jest.fn(async () => ({
      configured: true,
      masked: "tv-****1234",
      keyPath: "Aevatar:Tools:WebSearch:ApiKey",
    })),
    saveWebSearch: jest.fn(),
    deleteWebSearch: jest.fn(),
    getSkillsMpStatus: jest.fn(async () => ({
      configured: true,
      masked: "sm-****1234",
      keyPath: "SkillsMP:ApiKey",
      baseUrl: "https://skillsmp.com",
    })),
    getSkillsMpApiKey: jest.fn(async () => ({
      configured: true,
      masked: "sm-****1234",
      keyPath: "SkillsMP:ApiKey",
    })),
    saveSkillsMp: jest.fn(),
    deleteSkillsMp: jest.fn(),
    getSecp256k1Status: jest.fn(async () => ({
      configured: true,
      privateKey: {
        configured: true,
        masked: "priv****mask",
        keyPath: "Crypto:EcdsaSecp256k1:PrivateKeyHex",
        backupsPrefix: "Crypto:EcdsaSecp256k1:Backups:",
        backupCount: 1,
      },
      publicKey: {
        configured: true,
        hex: "04abcdef",
      },
    })),
    generateSecp256k1: jest.fn(),
    getConfigRaw: jest.fn(async () => ({
      json: '{\n  "Workflow": {}\n}',
      keyCount: 1,
      exists: true,
    })),
    getConnectorsRaw: jest.fn(async () => ({
      json: '{\n  "connectors": []\n}',
      count: 0,
      exists: false,
      path: "/tmp/.aevatar/connectors.json",
    })),
    validateConnectorsRaw: jest.fn(),
    saveConnectorsRaw: jest.fn(),
    listMcpServers: jest.fn(async () => [
      {
        name: "local-tools",
        command: "node",
        args: ["server.js"],
        env: { API_KEY: "masked" },
        timeoutMs: 60000,
      },
    ]),
    saveMcpServer: jest.fn(),
    deleteMcpServer: jest.fn(),
    getMcpRaw: jest.fn(async () => ({
      json: '{\n  "mcpServers": {}\n}',
      count: 0,
      exists: false,
      path: "/tmp/.aevatar/mcp.json",
    })),
    validateMcpRaw: jest.fn(),
    saveMcpRaw: jest.fn(),
    getSecretsRaw: jest.fn(async () => ({
      json: '{\n  "LLMProviders": {}\n}',
      keyCount: 1,
    })),
    saveWorkflow: jest.fn(),
    deleteWorkflow: jest.fn(),
    saveConfigRaw: jest.fn(),
    saveSecretsRaw: jest.fn(),
    setSecret: jest.fn(),
    removeSecret: jest.fn(),
    setLlmDefault: jest.fn(),
  },
}));

describe("RuntimeSettingsPage", () => {
  beforeEach(() => {
    window.localStorage.clear();
    jest.clearAllMocks();
    persistAuthSession({
      tokens: {
        accessToken: "token-1",
        tokenType: "Bearer",
        expiresIn: 3600,
        expiresAt: Date.now() + 3600_000,
        scope: "openid profile email",
      },
      user: {
        sub: "nyxid-user-1",
        email: "potter@example.com",
        email_verified: true,
        name: "Potter Sun",
        roles: ["admin"],
        groups: ["console"],
        permissions: ["settings:write"],
      },
    });
  });

  it("renders the runtime configuration workspace backed by the configuration capability", async () => {
    renderWithQueryClient(React.createElement(RuntimeSettingsPage));

    await waitFor(() => {
      expect(runtimeQueryApi.getCapabilities).toHaveBeenCalled();
      expect(configurationApi.getHealth).toHaveBeenCalled();
      expect(configurationApi.getSourceStatus).toHaveBeenCalled();
      expect(configurationApi.listWorkflows).toHaveBeenCalled();
      expect(configurationApi.getWorkflow).toHaveBeenCalled();
      expect(configurationApi.getLlmDefault).toHaveBeenCalled();
      expect(configurationApi.getLlmApiKey).toHaveBeenCalledWith("default");
      expect(configurationApi.getEmbeddingsStatus).toHaveBeenCalled();
      expect(configurationApi.getWebSearchStatus).toHaveBeenCalled();
      expect(configurationApi.getSkillsMpStatus).toHaveBeenCalled();
      expect(configurationApi.getSecp256k1Status).toHaveBeenCalled();
      expect(configurationApi.getConfigRaw).toHaveBeenCalled();
      expect(configurationApi.getConnectorsRaw).toHaveBeenCalled();
      expect(configurationApi.listMcpServers).toHaveBeenCalled();
      expect(configurationApi.getMcpRaw).toHaveBeenCalled();
      expect(configurationApi.getSecretsRaw).toHaveBeenCalled();
    });

    expect(screen.queryByRole("tab", { name: "Profile" })).toBeNull();
    expect(screen.queryByRole("tab", { name: "Console settings" })).toBeNull();
    expect(screen.getByText("Runtime environment summary")).toBeTruthy();
    expect(screen.getByText("Runtime configuration workspace")).toBeTruthy();
  });

  it("shows an unavailable warning when local runtime access is missing", async () => {
    jest.mocked(configurationApi.getSourceStatus).mockResolvedValueOnce({
      mode: "restricted",
      mongoConfigured: false,
      fileConfigured: false,
      localRuntimeAccess: false,
      paths: {
        root: "",
        secretsJson: "",
        configJson: "",
        connectorsJson: "",
        mcpJson: "",
        workflowsHome: "",
        workflowsRepo: "",
        homeEnvValue: null,
        secretsPathEnvValue: null,
      },
      doctor: {
        paths: {
          root: "",
          secretsJson: "",
          configJson: "",
          connectorsJson: "",
          mcpJson: "",
          workflowsHome: "",
          workflowsRepo: "",
          homeEnvValue: null,
          secretsPathEnvValue: null,
        },
        secrets: {
          path: "",
          exists: false,
          readable: false,
          writable: false,
          sizeBytes: null,
          error: "Local runtime access is required.",
        },
        config: {
          path: "",
          exists: false,
          readable: false,
          writable: false,
          sizeBytes: null,
          error: "Local runtime access is required.",
        },
        connectors: {
          path: "",
          exists: false,
          readable: false,
          writable: false,
          sizeBytes: null,
          error: "Local runtime access is required.",
        },
        mcp: {
          path: "",
          exists: false,
          readable: false,
          writable: false,
          sizeBytes: null,
          error: "Local runtime access is required.",
        },
        workflowsHome: {
          path: "",
          exists: false,
          readable: false,
          writable: false,
          sizeBytes: null,
          error: "Local runtime access is required.",
        },
        workflowsRepo: {
          path: "",
          exists: false,
          readable: false,
          writable: false,
          sizeBytes: null,
          error: "Local runtime access is required.",
        },
      },
    });

    renderWithQueryClient(React.createElement(RuntimeSettingsPage));

    await waitFor(() => {
      expect(configurationApi.getSourceStatus).toHaveBeenCalled();
      expect(configurationApi.getHealth).toHaveBeenCalled();
    });

    expect(
      await screen.findByText("Local runtime access is unavailable.")
    ).toBeTruthy();
    expect(configurationApi.listWorkflows).not.toHaveBeenCalled();
    expect(configurationApi.getWorkflow).not.toHaveBeenCalled();
    expect(configurationApi.getLlmDefault).not.toHaveBeenCalled();
    expect(configurationApi.getLlmApiKey).not.toHaveBeenCalled();
    expect(configurationApi.getEmbeddingsStatus).not.toHaveBeenCalled();
    expect(configurationApi.getWebSearchStatus).not.toHaveBeenCalled();
    expect(configurationApi.getSkillsMpStatus).not.toHaveBeenCalled();
    expect(configurationApi.getSecp256k1Status).not.toHaveBeenCalled();
    expect(configurationApi.getConfigRaw).not.toHaveBeenCalled();
    expect(configurationApi.getConnectorsRaw).not.toHaveBeenCalled();
    expect(configurationApi.listMcpServers).not.toHaveBeenCalled();
    expect(configurationApi.getMcpRaw).not.toHaveBeenCalled();
    expect(configurationApi.getSecretsRaw).not.toHaveBeenCalled();
  });
});
