import type {
  ConfigurationCollectionRawDocument,
  ConfigurationEmbeddingsStatus,
  ConfigurationLlmApiKeyStatus,
  ConfigurationLlmProbeResult,
  ConfigurationLlmInstance,
  ConfigurationLlmProviderType,
  ConfigurationMcpServer,
  ConfigurationRawDocument,
  ConfigurationSecretValueStatus,
  ConfigurationSecp256k1GenerateResult,
  ConfigurationSecp256k1Status,
  ConfigurationSkillsMpStatus,
  ConfigurationSourceStatus,
  ConfigurationValidationResult,
  ConfigurationWebSearchStatus,
  ConfigurationWorkflowFile,
  ConfigurationWorkflowFileDetail,
} from "@/shared/models/platform/configuration";
import type { Decoder } from "./decodeUtils";
import {
  decodeConfigurationEmbeddingsStatusResponse,
  decodeConfigurationCollectionRawDocumentResponse,
  decodeConfigurationLlmApiKeyStatusResponse,
  decodeConfigurationLlmDefaultResponse,
  decodeConfigurationLlmInstancesResponse,
  decodeConfigurationLlmProbeResultResponse,
  decodeConfigurationSecretValueStatusResponse,
  decodeConfigurationSecp256k1GenerateResponse,
  decodeConfigurationSecp256k1StatusResponse,
  decodeConfigurationSkillsMpStatusResponse,
  decodeConfigurationMcpServerMutationResponse,
  decodeConfigurationMcpServersResponse,
  decodeConfigurationLlmProviderTypesResponse,
  decodeConfigurationRawDocumentResponse,
  decodeConfigurationSourceStatusResponse,
  decodeConfigurationValidationResultResponse,
  decodeConfigurationWebSearchStatusResponse,
  decodeConfigurationWorkflowFileDetailResponse,
  decodeConfigurationWorkflowFileMutationResponse,
  decodeConfigurationWorkflowFilesResponse,
} from "./configurationDecoders";
import { authFetch } from "@/shared/auth/fetch";

const JSON_HEADERS = {
  "Content-Type": "application/json",
  Accept: "application/json",
};

const CONFIGURATION_API_PREFIX = "/api/configuration";

type WorkflowSource = "home" | "repo" | "all";

function compactObject<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(
    Object.entries(value).filter(([, entry]) => entry !== undefined)
  ) as T;
}

function trimOptional(value?: string): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

async function readError(response: Response): Promise<string> {
  const text = await response.text();
  if (!text) {
    return `HTTP ${response.status}`;
  }

  try {
    const payload = JSON.parse(text) as {
      message?: string;
      error?: string;
      code?: string;
    };
    return payload.message || payload.error || payload.code || text;
  } catch {
    return text;
  }
}

async function requestJson<T>(
  input: string,
  decoder: Decoder<T>,
  init?: RequestInit
): Promise<T> {
  const response = await authFetch(input, init);
  if (!response.ok) {
    throw new Error(await readError(response));
  }

  return decoder(await response.json());
}

async function requestText(input: string): Promise<string> {
  const response = await authFetch(input);
  if (!response.ok) {
    throw new Error(await readError(response));
  }

  return response.text();
}

function withSource(path: string, source?: WorkflowSource): string {
  if (!source) {
    return path;
  }

  const params = new URLSearchParams({ source });
  return `${path}?${params.toString()}`;
}

export const configurationApi = {
  async getHealth(): Promise<"ok"> {
    const payload = await requestText(`${CONFIGURATION_API_PREFIX}/health`);
    if (payload.trim() !== "ok") {
      throw new Error(
        `Unexpected configuration health payload: ${payload || "<empty>"}`
      );
    }

    return "ok";
  },

  getSourceStatus(): Promise<ConfigurationSourceStatus> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/source`,
      decodeConfigurationSourceStatusResponse
    );
  },

  listWorkflows(
    source: WorkflowSource = "all"
  ): Promise<ConfigurationWorkflowFile[]> {
    return requestJson(
      withSource(`${CONFIGURATION_API_PREFIX}/workflows`, source),
      decodeConfigurationWorkflowFilesResponse
    );
  },

  getWorkflow(
    filename: string,
    source: WorkflowSource = "all"
  ): Promise<ConfigurationWorkflowFileDetail> {
    return requestJson(
      withSource(
        `${CONFIGURATION_API_PREFIX}/workflows/${encodeURIComponent(filename)}`,
        source
      ),
      decodeConfigurationWorkflowFileDetailResponse
    );
  },

  saveWorkflow(input: {
    filename: string;
    content: string;
    source: Exclude<WorkflowSource, "all">;
  }): Promise<ConfigurationWorkflowFile> {
    return requestJson(
      withSource(
        `${CONFIGURATION_API_PREFIX}/workflows/${encodeURIComponent(
          input.filename
        )}`,
        input.source
      ),
      decodeConfigurationWorkflowFileMutationResponse,
      {
        method: "PUT",
        headers: JSON_HEADERS,
        body: JSON.stringify({
          content: input.content,
        }),
      }
    );
  },

  async deleteWorkflow(input: {
    filename: string;
    source: Exclude<WorkflowSource, "all">;
  }): Promise<void> {
    const response = await authFetch(
      withSource(
        `${CONFIGURATION_API_PREFIX}/workflows/${encodeURIComponent(
          input.filename
        )}`,
        input.source
      ),
      {
        method: "DELETE",
        headers: JSON_HEADERS,
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  getConfigRaw(): Promise<ConfigurationRawDocument> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/config/raw`,
      decodeConfigurationRawDocumentResponse
    );
  },

  async saveConfigRaw(json: string): Promise<void> {
    const response = await authFetch(`${CONFIGURATION_API_PREFIX}/config/raw`, {
      method: "PUT",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        json,
      }),
    });
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  getConnectorsRaw(): Promise<ConfigurationCollectionRawDocument> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/connectors/raw`,
      decodeConfigurationCollectionRawDocumentResponse
    );
  },

  async saveConnectorsRaw(json: string): Promise<void> {
    const response = await authFetch(
      `${CONFIGURATION_API_PREFIX}/connectors/raw`,
      {
        method: "PUT",
        headers: JSON_HEADERS,
        body: JSON.stringify({
          json,
        }),
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  validateConnectorsRaw(json: string): Promise<ConfigurationValidationResult> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/connectors/validate`,
      decodeConfigurationValidationResultResponse,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify({
          json,
        }),
      }
    );
  },

  listMcpServers(): Promise<ConfigurationMcpServer[]> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/mcp`,
      decodeConfigurationMcpServersResponse
    );
  },

  saveMcpServer(input: {
    name: string;
    command: string;
    args: string[];
    env: Record<string, string>;
    timeoutMs: number;
  }): Promise<ConfigurationMcpServer> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/mcp/${encodeURIComponent(input.name)}`,
      decodeConfigurationMcpServerMutationResponse,
      {
        method: "PUT",
        headers: JSON_HEADERS,
        body: JSON.stringify({
          command: input.command,
          args: input.args,
          env: input.env,
          timeoutMs: input.timeoutMs,
        }),
      }
    );
  },

  async deleteMcpServer(name: string): Promise<void> {
    const response = await authFetch(
      `${CONFIGURATION_API_PREFIX}/mcp/${encodeURIComponent(name)}`,
      {
        method: "DELETE",
        headers: JSON_HEADERS,
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  getMcpRaw(): Promise<ConfigurationCollectionRawDocument> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/mcp/raw`,
      decodeConfigurationCollectionRawDocumentResponse
    );
  },

  async saveMcpRaw(json: string): Promise<void> {
    const response = await authFetch(`${CONFIGURATION_API_PREFIX}/mcp/raw`, {
      method: "PUT",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        json,
      }),
    });
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  validateMcpRaw(json: string): Promise<ConfigurationValidationResult> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/mcp/validate`,
      decodeConfigurationValidationResultResponse,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify({
          json,
        }),
      }
    );
  },

  getEmbeddingsStatus(): Promise<ConfigurationEmbeddingsStatus> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/embeddings`,
      decodeConfigurationEmbeddingsStatusResponse
    );
  },

  getEmbeddingsApiKey(
    input: { reveal?: boolean } = {}
  ): Promise<ConfigurationSecretValueStatus> {
    const params = new URLSearchParams();
    if (input.reveal === true) {
      params.set("reveal", "true");
    }

    const suffix = params.size > 0 ? `?${params.toString()}` : "";
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/embeddings/api-key${suffix}`,
      decodeConfigurationSecretValueStatusResponse
    );
  },

  async saveEmbeddings(input: {
    enabled?: boolean;
    providerType?: string;
    endpoint?: string;
    model?: string;
    apiKey?: string;
  }): Promise<void> {
    const response = await authFetch(`${CONFIGURATION_API_PREFIX}/embeddings`, {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify(compactObject(input)),
    });
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  async deleteEmbeddings(): Promise<void> {
    const response = await authFetch(`${CONFIGURATION_API_PREFIX}/embeddings`, {
      method: "DELETE",
      headers: JSON_HEADERS,
    });
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  getWebSearchStatus(): Promise<ConfigurationWebSearchStatus> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/websearch`,
      decodeConfigurationWebSearchStatusResponse
    );
  },

  getWebSearchApiKey(
    input: { reveal?: boolean } = {}
  ): Promise<ConfigurationSecretValueStatus> {
    const params = new URLSearchParams();
    if (input.reveal === true) {
      params.set("reveal", "true");
    }

    const suffix = params.size > 0 ? `?${params.toString()}` : "";
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/websearch/api-key${suffix}`,
      decodeConfigurationSecretValueStatusResponse
    );
  },

  async saveWebSearch(input: {
    enabled?: boolean;
    provider?: string;
    endpoint?: string;
    timeoutMs?: number;
    searchDepth?: string;
    apiKey?: string;
  }): Promise<void> {
    const response = await authFetch(`${CONFIGURATION_API_PREFIX}/websearch`, {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify(compactObject(input)),
    });
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  async deleteWebSearch(): Promise<void> {
    const response = await authFetch(`${CONFIGURATION_API_PREFIX}/websearch`, {
      method: "DELETE",
      headers: JSON_HEADERS,
    });
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  getSkillsMpStatus(): Promise<ConfigurationSkillsMpStatus> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/skillsmp/status`,
      decodeConfigurationSkillsMpStatusResponse
    );
  },

  getSkillsMpApiKey(
    input: { reveal?: boolean } = {}
  ): Promise<ConfigurationSecretValueStatus> {
    const params = new URLSearchParams();
    if (input.reveal === true) {
      params.set("reveal", "true");
    }

    const suffix = params.size > 0 ? `?${params.toString()}` : "";
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/skillsmp/api-key${suffix}`,
      decodeConfigurationSecretValueStatusResponse
    );
  },

  async saveSkillsMp(input: {
    apiKey?: string;
    baseUrl?: string;
  }): Promise<void> {
    const response = await authFetch(`${CONFIGURATION_API_PREFIX}/skillsmp`, {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify(compactObject(input)),
    });
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  async deleteSkillsMp(): Promise<void> {
    const response = await authFetch(`${CONFIGURATION_API_PREFIX}/skillsmp`, {
      method: "DELETE",
      headers: JSON_HEADERS,
    });
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  getSecp256k1Status(): Promise<ConfigurationSecp256k1Status> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/crypto/secp256k1/status`,
      decodeConfigurationSecp256k1StatusResponse
    );
  },

  generateSecp256k1(): Promise<ConfigurationSecp256k1GenerateResult> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/crypto/secp256k1/generate`,
      decodeConfigurationSecp256k1GenerateResponse,
      {
        method: "POST",
        headers: JSON_HEADERS,
      }
    );
  },

  getSecretsRaw(): Promise<ConfigurationRawDocument> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/secrets/raw`,
      decodeConfigurationRawDocumentResponse
    );
  },

  async saveSecretsRaw(json: string): Promise<void> {
    const response = await authFetch(
      `${CONFIGURATION_API_PREFIX}/secrets/raw`,
      {
        method: "PUT",
        headers: JSON_HEADERS,
        body: JSON.stringify({
          json,
        }),
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  listLlmProviders(): Promise<ConfigurationLlmProviderType[]> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/llm/providers`,
      decodeConfigurationLlmProviderTypesResponse
    );
  },

  listLlmInstances(): Promise<ConfigurationLlmInstance[]> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/llm/instances`,
      decodeConfigurationLlmInstancesResponse
    );
  },

  getLlmDefault(): Promise<string> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/llm/default`,
      decodeConfigurationLlmDefaultResponse
    );
  },

  setLlmDefault(providerName: string): Promise<string> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/llm/default`,
      decodeConfigurationLlmDefaultResponse,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            providerName: trimOptional(providerName),
          })
        ),
      }
    );
  },

  getLlmApiKey(
    providerName: string,
    input: { reveal?: boolean } = {}
  ): Promise<ConfigurationLlmApiKeyStatus> {
    const params = new URLSearchParams();
    if (input.reveal === true) {
      params.set("reveal", "true");
    }

    const suffix = params.size > 0 ? `?${params.toString()}` : "";
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/llm/api-key/${encodeURIComponent(
        providerName
      )}${suffix}`,
      decodeConfigurationLlmApiKeyStatusResponse
    );
  },

  async setLlmApiKey(input: {
    providerName: string;
    apiKey: string;
  }): Promise<void> {
    const response = await authFetch(
      `${CONFIGURATION_API_PREFIX}/llm/api-key`,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify({
          providerName: input.providerName,
          apiKey: input.apiKey,
        }),
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  async deleteLlmApiKey(providerName: string): Promise<void> {
    const response = await authFetch(
      `${CONFIGURATION_API_PREFIX}/llm/api-key/${encodeURIComponent(
        providerName
      )}`,
      {
        method: "DELETE",
        headers: JSON_HEADERS,
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  async saveLlmInstance(input: {
    providerName: string;
    providerType: string;
    model: string;
    endpoint?: string;
    apiKey?: string;
    copyApiKeyFrom?: string;
    forceCopyApiKeyFrom?: boolean;
  }): Promise<void> {
    const response = await authFetch(
      `${CONFIGURATION_API_PREFIX}/llm/instance`,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify(compactObject(input)),
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  async deleteLlmInstance(providerName: string): Promise<void> {
    const response = await authFetch(
      `${CONFIGURATION_API_PREFIX}/llm/instance/${encodeURIComponent(
        providerName
      )}`,
      {
        method: "DELETE",
        headers: JSON_HEADERS,
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  probeLlmTest(input: {
    providerType: string;
    endpoint?: string;
    apiKey: string;
  }): Promise<ConfigurationLlmProbeResult> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/llm/probe/test`,
      decodeConfigurationLlmProbeResultResponse,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify(compactObject(input)),
      }
    );
  },

  probeLlmModels(input: {
    providerType: string;
    endpoint?: string;
    apiKey: string;
  }): Promise<ConfigurationLlmProbeResult> {
    return requestJson(
      `${CONFIGURATION_API_PREFIX}/llm/probe/models`,
      decodeConfigurationLlmProbeResultResponse,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify(compactObject(input)),
      }
    );
  },

  async setSecret(input: { key: string; value: string }): Promise<void> {
    const response = await authFetch(
      `${CONFIGURATION_API_PREFIX}/secrets/set`,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify(input),
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },

  async removeSecret(key: string): Promise<void> {
    const response = await authFetch(
      `${CONFIGURATION_API_PREFIX}/secrets/remove`,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify({ key }),
      }
    );
    if (!response.ok) {
      throw new Error(await readError(response));
    }
  },
};
