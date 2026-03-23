import {
  PageContainer,
  ProCard,
  ProDescriptions,
} from "@ant-design/pro-components";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { history } from "@umijs/max";
import { Alert, Grid, message, Space, Tabs } from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { configurationApi } from "@/shared/api/configurationApi";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import type {
  ConfigurationEmbeddingsStatus,
  ConfigurationLlmApiKeyStatus,
  ConfigurationLlmProbeResult,
  ConfigurationSecp256k1Status,
  ConfigurationSecretValueStatus,
  ConfigurationSkillsMpStatus,
  ConfigurationWebSearchStatus,
} from "@/shared/models/platform/configuration";
import { fillCardStyle, moduleCardProps } from "@/shared/ui/proComponents";
import {
  ConnectorsSection,
  EmbeddingsSection,
  LlmProvidersSection,
  McpSection,
  RawConfigSection,
  SecretsSection,
  SignerKeySection,
  SkillsMpSection,
  SystemStatusSection,
  WebSearchSection,
  WorkflowFilesSection,
} from "./runtimeSettingsWorkspaceSections";
import {
  type ConfigurationPathRecord,
  type SettingsSummaryRecord,
  type WorkflowDraftSource,
  buildNewWorkflowTemplate,
  formatMcpArgs,
  formatMcpEnv,
  formatProbeSummary,
  normalizeWorkflowSource,
  parseMcpArgs,
  parseMcpEnv,
  settingsSummaryColumns,
  workflowKey,
} from "./runtimeSettingsShared";

const RuntimeSettingsPage: React.FC = () => {
  const [messageApi, messageContextHolder] = message.useMessage();
  const queryClient = useQueryClient();
  const screens = Grid.useBreakpoint();
  const [selectedWorkflowKey, setSelectedWorkflowKey] = useState<string | null>(
    null
  );
  const [selectedLlmInstanceName, setSelectedLlmInstanceName] = useState<
    string | null
  >(null);
  const [isNewLlmInstanceDraft, setIsNewLlmInstanceDraft] = useState(false);
  const [workflowFilename, setWorkflowFilename] = useState("");
  const [workflowSource, setWorkflowSource] =
    useState<WorkflowDraftSource>("home");
  const [workflowContent, setWorkflowContent] = useState("");
  const [llmInstanceNameDraft, setLlmInstanceNameDraft] = useState("");
  const [llmProviderTypeDraft, setLlmProviderTypeDraft] = useState("");
  const [llmModelDraft, setLlmModelDraft] = useState("");
  const [llmEndpointDraft, setLlmEndpointDraft] = useState("");
  const [llmApiKeyDraft, setLlmApiKeyDraft] = useState("");
  const [llmProbeResult, setLlmProbeResult] =
    useState<ConfigurationLlmProbeResult | null>(null);
  const [llmModelsResult, setLlmModelsResult] =
    useState<ConfigurationLlmProbeResult | null>(null);
  const [embeddingsEnabledDraft, setEmbeddingsEnabledDraft] = useState(true);
  const [embeddingsProviderTypeDraft, setEmbeddingsProviderTypeDraft] =
    useState("deepseek");
  const [embeddingsEndpointDraft, setEmbeddingsEndpointDraft] = useState(
    "https://dashscope.aliyuncs.com/compatible-mode/v1"
  );
  const [embeddingsModelDraft, setEmbeddingsModelDraft] =
    useState("text-embedding-v3");
  const [embeddingsApiKeyDraft, setEmbeddingsApiKeyDraft] = useState("");
  const [webSearchEnabledDraft, setWebSearchEnabledDraft] = useState(true);
  const [webSearchProviderDraft, setWebSearchProviderDraft] =
    useState("tavily");
  const [webSearchEndpointDraft, setWebSearchEndpointDraft] = useState("");
  const [webSearchTimeoutDraft, setWebSearchTimeoutDraft] = useState("15000");
  const [webSearchDepthDraft, setWebSearchDepthDraft] = useState("advanced");
  const [webSearchApiKeyDraft, setWebSearchApiKeyDraft] = useState("");
  const [skillsMpBaseUrlDraft, setSkillsMpBaseUrlDraft] = useState(
    "https://skillsmp.com"
  );
  const [skillsMpApiKeyDraft, setSkillsMpApiKeyDraft] = useState("");
  const [selectedMcpName, setSelectedMcpName] = useState<string | null>(null);
  const [isNewMcpDraft, setIsNewMcpDraft] = useState(false);
  const [mcpNameDraft, setMcpNameDraft] = useState("");
  const [mcpCommandDraft, setMcpCommandDraft] = useState("");
  const [mcpArgsDraft, setMcpArgsDraft] = useState("");
  const [mcpEnvDraft, setMcpEnvDraft] = useState("{}");
  const [mcpTimeoutDraft, setMcpTimeoutDraft] = useState("60000");
  const [configJsonDraft, setConfigJsonDraft] = useState("");
  const [connectorsJsonDraft, setConnectorsJsonDraft] = useState("");
  const [mcpJsonDraft, setMcpJsonDraft] = useState("");
  const [secretKeyDraft, setSecretKeyDraft] = useState("");
  const [secretValueDraft, setSecretValueDraft] = useState("");
  const [secretsJsonDraft, setSecretsJsonDraft] = useState("");
  const [pendingDefaultProvider, setPendingDefaultProvider] = useState("");
  const runtimeSummaryEnabled = true;

  const capabilitiesQuery = useQuery({
    queryKey: ["settings-capabilities"],
    queryFn: () => runtimeQueryApi.getCapabilities(),
    enabled: runtimeSummaryEnabled,
  });
  const configurationHealthQuery = useQuery({
    queryKey: ["settings-configuration-health"],
    queryFn: () => configurationApi.getHealth(),
    enabled: runtimeSummaryEnabled,
    retry: false,
  });
  const configurationSourceQuery = useQuery({
    queryKey: ["settings-configuration-source"],
    queryFn: () => configurationApi.getSourceStatus(),
    enabled: runtimeSummaryEnabled,
    retry: false,
  });
  const hasLocalRuntimeAccess =
    configurationSourceQuery.data?.localRuntimeAccess ?? false;
  const runtimeWorkspaceEnabled = hasLocalRuntimeAccess;
  const configurationWorkflowsQuery = useQuery({
    queryKey: ["settings-configuration-workflows"],
    queryFn: () => configurationApi.listWorkflows("all"),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationLlmProvidersQuery = useQuery({
    queryKey: ["settings-configuration-llm-providers"],
    queryFn: () => configurationApi.listLlmProviders(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationLlmInstancesQuery = useQuery({
    queryKey: ["settings-configuration-llm-instances"],
    queryFn: () => configurationApi.listLlmInstances(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationLlmDefaultQuery = useQuery({
    queryKey: ["settings-configuration-llm-default"],
    queryFn: () => configurationApi.getLlmDefault(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationEmbeddingsQuery = useQuery<ConfigurationEmbeddingsStatus>({
    queryKey: ["settings-configuration-embeddings"],
    queryFn: () => configurationApi.getEmbeddingsStatus(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationWebSearchQuery = useQuery<ConfigurationWebSearchStatus>({
    queryKey: ["settings-configuration-websearch"],
    queryFn: () => configurationApi.getWebSearchStatus(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationSkillsMpQuery = useQuery<ConfigurationSkillsMpStatus>({
    queryKey: ["settings-configuration-skillsmp"],
    queryFn: () => configurationApi.getSkillsMpStatus(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationSecp256k1Query = useQuery<ConfigurationSecp256k1Status>({
    queryKey: ["settings-configuration-secp256k1"],
    queryFn: () => configurationApi.getSecp256k1Status(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationConfigRawQuery = useQuery({
    queryKey: ["settings-configuration-config-raw"],
    queryFn: () => configurationApi.getConfigRaw(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationConnectorsRawQuery = useQuery({
    queryKey: ["settings-configuration-connectors-raw"],
    queryFn: () => configurationApi.getConnectorsRaw(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationMcpServersQuery = useQuery({
    queryKey: ["settings-configuration-mcp-servers"],
    queryFn: () => configurationApi.listMcpServers(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationMcpRawQuery = useQuery({
    queryKey: ["settings-configuration-mcp-raw"],
    queryFn: () => configurationApi.getMcpRaw(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });
  const configurationSecretsRawQuery = useQuery({
    queryKey: ["settings-configuration-secrets-raw"],
    queryFn: () => configurationApi.getSecretsRaw(),
    enabled: runtimeWorkspaceEnabled,
    retry: false,
  });

  const selectedWorkflow = useMemo(() => {
    const items = configurationWorkflowsQuery.data ?? [];
    if (!selectedWorkflowKey) {
      return items[0] ?? null;
    }

    return (
      items.find((item) => workflowKey(item) === selectedWorkflowKey) ??
      items[0] ??
      null
    );
  }, [configurationWorkflowsQuery.data, selectedWorkflowKey]);

  const selectedLlmInstance = useMemo(() => {
    const items = configurationLlmInstancesQuery.data ?? [];
    if (isNewLlmInstanceDraft) {
      return null;
    }

    if (!selectedLlmInstanceName) {
      return items[0] ?? null;
    }

    return (
      items.find((item) => item.name === selectedLlmInstanceName) ??
      items[0] ??
      null
    );
  }, [
    configurationLlmInstancesQuery.data,
    isNewLlmInstanceDraft,
    selectedLlmInstanceName,
  ]);

  const selectedMcpServer = useMemo(() => {
    const items = configurationMcpServersQuery.data ?? [];
    if (isNewMcpDraft) {
      return null;
    }

    if (!selectedMcpName) {
      return items[0] ?? null;
    }

    return (
      items.find((item) => item.name === selectedMcpName) ?? items[0] ?? null
    );
  }, [configurationMcpServersQuery.data, isNewMcpDraft, selectedMcpName]);

  const workflowDetailQuery = useQuery({
    queryKey: [
      "settings-configuration-workflow-detail",
      selectedWorkflow?.filename,
      selectedWorkflow?.source,
    ],
    queryFn: () => {
      if (!selectedWorkflow) {
        throw new Error("No workflow is selected.");
      }

      return configurationApi.getWorkflow(
        selectedWorkflow.filename,
        normalizeWorkflowSource(selectedWorkflow.source)
      );
    },
    enabled: runtimeWorkspaceEnabled && Boolean(selectedWorkflow),
    retry: false,
  });

  const configurationLlmApiKeyQuery = useQuery<ConfigurationLlmApiKeyStatus>({
    queryKey: ["settings-configuration-llm-api-key", selectedLlmInstance?.name],
    queryFn: () => {
      if (!selectedLlmInstance?.name) {
        throw new Error("No LLM instance is selected.");
      }

      return configurationApi.getLlmApiKey(selectedLlmInstance.name);
    },
    enabled: runtimeWorkspaceEnabled && Boolean(selectedLlmInstance?.name),
    retry: false,
  });

  useEffect(() => {
    const items = configurationWorkflowsQuery.data ?? [];
    if (items.length === 0) {
      return;
    }

    if (!selectedWorkflowKey) {
      setSelectedWorkflowKey(workflowKey(items[0]));
      return;
    }

    if (!items.some((item) => workflowKey(item) === selectedWorkflowKey)) {
      setSelectedWorkflowKey(workflowKey(items[0]));
    }
  }, [configurationWorkflowsQuery.data, selectedWorkflowKey]);

  useEffect(() => {
    const items = configurationLlmInstancesQuery.data ?? [];
    if (items.length === 0) {
      return;
    }

    if (isNewLlmInstanceDraft) {
      return;
    }

    if (!selectedLlmInstanceName) {
      setSelectedLlmInstanceName(items[0].name);
      return;
    }

    if (!items.some((item) => item.name === selectedLlmInstanceName)) {
      setSelectedLlmInstanceName(items[0].name);
    }
  }, [
    configurationLlmInstancesQuery.data,
    isNewLlmInstanceDraft,
    selectedLlmInstanceName,
  ]);

  useEffect(() => {
    const items = configurationMcpServersQuery.data ?? [];
    if (items.length === 0) {
      return;
    }

    if (isNewMcpDraft) {
      return;
    }

    if (!selectedMcpName) {
      setSelectedMcpName(items[0].name);
      return;
    }

    if (!items.some((item) => item.name === selectedMcpName)) {
      setSelectedMcpName(items[0].name);
    }
  }, [configurationMcpServersQuery.data, isNewMcpDraft, selectedMcpName]);

  useEffect(() => {
    if (!workflowDetailQuery.data) {
      return;
    }

    setWorkflowFilename(workflowDetailQuery.data.filename);
    setWorkflowSource(normalizeWorkflowSource(workflowDetailQuery.data.source));
    setWorkflowContent(workflowDetailQuery.data.content);
  }, [workflowDetailQuery.data]);

  useEffect(() => {
    if (!selectedLlmInstance) {
      return;
    }

    setLlmInstanceNameDraft(selectedLlmInstance.name);
    setLlmProviderTypeDraft(selectedLlmInstance.providerType);
    setLlmModelDraft(selectedLlmInstance.model);
    setLlmEndpointDraft(selectedLlmInstance.endpoint);
    setLlmApiKeyDraft("");
    setLlmProbeResult(null);
    setLlmModelsResult(null);
  }, [selectedLlmInstance]);

  useEffect(() => {
    if (configurationConfigRawQuery.data) {
      setConfigJsonDraft(configurationConfigRawQuery.data.json);
    }
  }, [configurationConfigRawQuery.data]);

  useEffect(() => {
    if (!configurationEmbeddingsQuery.data) {
      return;
    }

    setEmbeddingsEnabledDraft(
      configurationEmbeddingsQuery.data.enabled ?? true
    );
    setEmbeddingsProviderTypeDraft(
      configurationEmbeddingsQuery.data.providerType || "deepseek"
    );
    setEmbeddingsEndpointDraft(
      configurationEmbeddingsQuery.data.endpoint ||
        "https://dashscope.aliyuncs.com/compatible-mode/v1"
    );
    setEmbeddingsModelDraft(
      configurationEmbeddingsQuery.data.model || "text-embedding-v3"
    );
    setEmbeddingsApiKeyDraft("");
  }, [configurationEmbeddingsQuery.data]);

  useEffect(() => {
    if (!configurationWebSearchQuery.data) {
      return;
    }

    setWebSearchEnabledDraft(configurationWebSearchQuery.data.enabled ?? true);
    setWebSearchProviderDraft(
      configurationWebSearchQuery.data.provider || "tavily"
    );
    setWebSearchEndpointDraft(configurationWebSearchQuery.data.endpoint || "");
    setWebSearchTimeoutDraft(
      configurationWebSearchQuery.data.timeoutMs != null
        ? String(configurationWebSearchQuery.data.timeoutMs)
        : "15000"
    );
    setWebSearchDepthDraft(
      configurationWebSearchQuery.data.searchDepth || "advanced"
    );
    setWebSearchApiKeyDraft("");
  }, [configurationWebSearchQuery.data]);

  useEffect(() => {
    if (!configurationSkillsMpQuery.data) {
      return;
    }

    setSkillsMpBaseUrlDraft(
      configurationSkillsMpQuery.data.baseUrl || "https://skillsmp.com"
    );
    setSkillsMpApiKeyDraft("");
  }, [configurationSkillsMpQuery.data]);

  useEffect(() => {
    if (!selectedMcpServer) {
      return;
    }

    setMcpNameDraft(selectedMcpServer.name);
    setMcpCommandDraft(selectedMcpServer.command);
    setMcpArgsDraft(formatMcpArgs(selectedMcpServer.args));
    setMcpEnvDraft(formatMcpEnv(selectedMcpServer.env));
    setMcpTimeoutDraft(String(selectedMcpServer.timeoutMs));
  }, [selectedMcpServer]);

  useEffect(() => {
    if (configurationConnectorsRawQuery.data) {
      setConnectorsJsonDraft(configurationConnectorsRawQuery.data.json);
    }
  }, [configurationConnectorsRawQuery.data]);

  useEffect(() => {
    if (configurationMcpRawQuery.data) {
      setMcpJsonDraft(configurationMcpRawQuery.data.json);
    }
  }, [configurationMcpRawQuery.data]);

  useEffect(() => {
    if (configurationSecretsRawQuery.data) {
      setSecretsJsonDraft(configurationSecretsRawQuery.data.json);
    }
  }, [configurationSecretsRawQuery.data]);

  useEffect(() => {
    if (configurationLlmDefaultQuery.data) {
      setPendingDefaultProvider(configurationLlmDefaultQuery.data);
    }
  }, [configurationLlmDefaultQuery.data]);

  const settingsSummary = useMemo<SettingsSummaryRecord>(
    () => ({
      configStatus:
        configurationHealthQuery.isSuccess && hasLocalRuntimeAccess
          ? "ready"
          : "unavailable",
      configMode: hasLocalRuntimeAccess
        ? configurationSourceQuery.data?.mode ?? ""
        : "restricted",
      runtimeWorkflowFiles: hasLocalRuntimeAccess
        ? configurationWorkflowsQuery.data?.length ?? 0
        : 0,
      primitiveCount: capabilitiesQuery.data?.primitives.length ?? 0,
      defaultProvider: hasLocalRuntimeAccess
        ? configurationLlmDefaultQuery.data ?? ""
        : "",
    }),
    [
      capabilitiesQuery.data?.primitives.length,
      configurationHealthQuery.isSuccess,
      configurationLlmDefaultQuery.data,
      hasLocalRuntimeAccess,
      configurationSourceQuery.data?.mode,
      configurationWorkflowsQuery.data?.length,
    ]
  );

  const configurationPathRecords = useMemo<ConfigurationPathRecord[]>(() => {
    const doctor = configurationSourceQuery.data?.doctor;
    if (!doctor) {
      return [];
    }

    return [
      { id: "config", label: "config.json", status: doctor.config },
      { id: "secrets", label: "secrets.json", status: doctor.secrets },
      {
        id: "workflows-home",
        label: "workflows (home)",
        status: doctor.workflowsHome,
      },
      {
        id: "workflows-repo",
        label: "workflows (repo)",
        status: doctor.workflowsRepo,
      },
      { id: "connectors", label: "connectors.json", status: doctor.connectors },
      { id: "mcp", label: "mcp.json", status: doctor.mcp },
    ];
  }, [configurationSourceQuery.data]);

  const saveWorkflowMutation = useMutation({
    mutationFn: () =>
      configurationApi.saveWorkflow({
        filename: workflowFilename,
        content: workflowContent,
        source: workflowSource,
      }),
    onSuccess: async (saved) => {
      messageApi.success(`Saved ${saved.filename} to ${saved.source}.`);
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-workflows"],
        }),
        queryClient.invalidateQueries({
          queryKey: [
            "settings-configuration-workflow-detail",
            saved.filename,
            saved.source,
          ],
        }),
      ]);
      setSelectedWorkflowKey(workflowKey(saved));
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to save workflow."
      );
    },
  });

  const deleteWorkflowMutation = useMutation({
    mutationFn: async () => {
      await configurationApi.deleteWorkflow({
        filename: workflowFilename,
        source: workflowSource,
      });
    },
    onSuccess: async () => {
      messageApi.success(`Deleted ${workflowFilename}.`);
      setSelectedWorkflowKey(null);
      setWorkflowFilename("");
      setWorkflowContent("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-workflows"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-workflow-detail"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to delete workflow."
      );
    },
  });

  const saveConfigRawMutation = useMutation({
    mutationFn: () => configurationApi.saveConfigRaw(configJsonDraft),
    onSuccess: async () => {
      messageApi.success("config.json saved.");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-config-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-websearch"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-skillsmp"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to save config.json."
      );
    },
  });

  const saveConnectorsRawMutation = useMutation({
    mutationFn: () => configurationApi.saveConnectorsRaw(connectorsJsonDraft),
    onSuccess: async () => {
      messageApi.success("connectors.json saved.");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-connectors-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to save connectors.json."
      );
    },
  });

  const validateConnectorsRawMutation = useMutation({
    mutationFn: () =>
      configurationApi.validateConnectorsRaw(connectorsJsonDraft),
    onSuccess: (result) => {
      messageApi.success(`connectors.json is valid (${result.count} entries).`);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to validate connectors.json."
      );
    },
  });

  const saveMcpRawMutation = useMutation({
    mutationFn: () => configurationApi.saveMcpRaw(mcpJsonDraft),
    onSuccess: async () => {
      messageApi.success("mcp.json saved.");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-mcp-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to save mcp.json."
      );
    },
  });

  const saveMcpServerMutation = useMutation({
    mutationFn: () => {
      const timeoutMs = Number.parseInt(mcpTimeoutDraft.trim(), 10);
      if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
        throw new Error("MCP timeout must be a positive integer.");
      }

      return configurationApi.saveMcpServer({
        name: mcpNameDraft.trim(),
        command: mcpCommandDraft.trim(),
        args: parseMcpArgs(mcpArgsDraft),
        env: parseMcpEnv(mcpEnvDraft),
        timeoutMs,
      });
    },
    onSuccess: async (server) => {
      messageApi.success(`Saved MCP server ${server.name}.`);
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-mcp-servers"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-mcp-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
      setIsNewMcpDraft(false);
      setSelectedMcpName(server.name);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to save MCP server."
      );
    },
  });

  const validateMcpRawMutation = useMutation({
    mutationFn: () => configurationApi.validateMcpRaw(mcpJsonDraft),
    onSuccess: (result) => {
      messageApi.success(`mcp.json is valid (${result.count} servers).`);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to validate mcp.json."
      );
    },
  });

  const deleteMcpServerMutation = useMutation({
    mutationFn: async () => {
      await configurationApi.deleteMcpServer(mcpNameDraft.trim());
    },
    onSuccess: async () => {
      messageApi.success(`Deleted MCP server ${mcpNameDraft.trim()}.`);
      setIsNewMcpDraft(false);
      setSelectedMcpName(null);
      setMcpNameDraft("");
      setMcpCommandDraft("");
      setMcpArgsDraft("");
      setMcpEnvDraft("{}");
      setMcpTimeoutDraft("60000");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-mcp-servers"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-mcp-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to delete MCP server."
      );
    },
  });

  const saveSecretsRawMutation = useMutation({
    mutationFn: () => configurationApi.saveSecretsRaw(secretsJsonDraft),
    onSuccess: async () => {
      messageApi.success("secrets.json saved.");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-api-key"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-embeddings"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-websearch"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-skillsmp"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secp256k1"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-providers"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-instances"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-default"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to save secrets.json."
      );
    },
  });

  const saveLlmInstanceMutation = useMutation({
    mutationFn: () =>
      configurationApi.saveLlmInstance({
        providerName: llmInstanceNameDraft.trim(),
        providerType: llmProviderTypeDraft.trim(),
        model: llmModelDraft.trim(),
        endpoint: llmEndpointDraft.trim() || undefined,
        apiKey: llmApiKeyDraft.trim() || undefined,
      }),
    onSuccess: async () => {
      const name = llmInstanceNameDraft.trim();
      messageApi.success(`Saved LLM instance ${name}.`);
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-instances"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-providers"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-default"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-api-key", name],
        }),
      ]);
      setIsNewLlmInstanceDraft(false);
      setSelectedLlmInstanceName(name);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to save LLM instance."
      );
    },
  });

  const deleteLlmInstanceMutation = useMutation({
    mutationFn: () =>
      configurationApi.deleteLlmInstance(llmInstanceNameDraft.trim()),
    onSuccess: async () => {
      const name = llmInstanceNameDraft.trim();
      messageApi.success(`Deleted LLM instance ${name}.`);
      setIsNewLlmInstanceDraft(false);
      setSelectedLlmInstanceName(null);
      setLlmInstanceNameDraft("");
      setLlmProviderTypeDraft("");
      setLlmModelDraft("");
      setLlmEndpointDraft("");
      setLlmApiKeyDraft("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-instances"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-providers"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-default"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-api-key", name],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to delete LLM instance."
      );
    },
  });

  const setLlmApiKeyMutation = useMutation({
    mutationFn: () =>
      configurationApi.setLlmApiKey({
        providerName: llmInstanceNameDraft.trim(),
        apiKey: llmApiKeyDraft.trim(),
      }),
    onSuccess: async () => {
      const name = llmInstanceNameDraft.trim();
      messageApi.success(`API key updated for ${name}.`);
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-api-key", name],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-default"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-instances"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to update API key."
      );
    },
  });

  const deleteLlmApiKeyMutation = useMutation({
    mutationFn: () =>
      configurationApi.deleteLlmApiKey(llmInstanceNameDraft.trim()),
    onSuccess: async () => {
      const name = llmInstanceNameDraft.trim();
      messageApi.success(`API key removed for ${name}.`);
      setLlmApiKeyDraft("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-api-key", name],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-default"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-instances"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to remove API key."
      );
    },
  });

  const revealLlmApiKeyMutation = useMutation({
    mutationFn: () =>
      configurationApi.getLlmApiKey(llmInstanceNameDraft.trim(), {
        reveal: true,
      }),
    onSuccess: (result) => {
      setLlmApiKeyDraft(result.value ?? "");
      messageApi.success(`Loaded API key for ${result.providerName}.`);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to reveal API key."
      );
    },
  });

  const saveEmbeddingsMutation = useMutation({
    mutationFn: () =>
      configurationApi.saveEmbeddings({
        enabled: embeddingsEnabledDraft,
        providerType: embeddingsProviderTypeDraft.trim(),
        endpoint: embeddingsEndpointDraft.trim(),
        model: embeddingsModelDraft.trim(),
        apiKey: embeddingsApiKeyDraft.trim() || undefined,
      }),
    onSuccess: async () => {
      messageApi.success("Embeddings configuration saved.");
      setEmbeddingsApiKeyDraft("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-embeddings"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to save embeddings configuration."
      );
    },
  });

  const deleteEmbeddingsMutation = useMutation({
    mutationFn: () => configurationApi.deleteEmbeddings(),
    onSuccess: async () => {
      messageApi.success("Embeddings configuration deleted.");
      setEmbeddingsApiKeyDraft("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-embeddings"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to delete embeddings configuration."
      );
    },
  });

  const revealEmbeddingsApiKeyMutation =
    useMutation<ConfigurationSecretValueStatus>({
      mutationFn: () => configurationApi.getEmbeddingsApiKey({ reveal: true }),
      onSuccess: (result) => {
        setEmbeddingsApiKeyDraft(result.value ?? "");
        messageApi.success("Loaded embeddings API key.");
      },
      onError: (error) => {
        messageApi.error(
          error instanceof Error
            ? error.message
            : "Failed to reveal embeddings API key."
        );
      },
    });

  const saveWebSearchMutation = useMutation({
    mutationFn: () => {
      const timeoutMs = webSearchTimeoutDraft.trim()
        ? Number.parseInt(webSearchTimeoutDraft.trim(), 10)
        : undefined;
      if (
        webSearchTimeoutDraft.trim() &&
        (!Number.isFinite(timeoutMs) || Number(timeoutMs) <= 0)
      ) {
        throw new Error("Web search timeout must be a positive integer.");
      }

      return configurationApi.saveWebSearch({
        enabled: webSearchEnabledDraft,
        provider: webSearchProviderDraft.trim(),
        endpoint: webSearchEndpointDraft.trim() || undefined,
        timeoutMs,
        searchDepth: webSearchDepthDraft.trim() || undefined,
        apiKey: webSearchApiKeyDraft.trim() || undefined,
      });
    },
    onSuccess: async () => {
      messageApi.success("Web search configuration saved.");
      setWebSearchApiKeyDraft("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-websearch"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-config-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to save web search configuration."
      );
    },
  });

  const deleteWebSearchMutation = useMutation({
    mutationFn: () => configurationApi.deleteWebSearch(),
    onSuccess: async () => {
      messageApi.success("Web search configuration deleted.");
      setWebSearchApiKeyDraft("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-websearch"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-config-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to delete web search configuration."
      );
    },
  });

  const revealWebSearchApiKeyMutation =
    useMutation<ConfigurationSecretValueStatus>({
      mutationFn: () => configurationApi.getWebSearchApiKey({ reveal: true }),
      onSuccess: (result) => {
        setWebSearchApiKeyDraft(result.value ?? "");
        messageApi.success("Loaded web search API key.");
      },
      onError: (error) => {
        messageApi.error(
          error instanceof Error
            ? error.message
            : "Failed to reveal web search API key."
        );
      },
    });

  const saveSkillsMpMutation = useMutation({
    mutationFn: () =>
      configurationApi.saveSkillsMp({
        apiKey: skillsMpApiKeyDraft.trim() || undefined,
        baseUrl: skillsMpBaseUrlDraft.trim() || undefined,
      }),
    onSuccess: async () => {
      messageApi.success("SkillsMP configuration saved.");
      setSkillsMpApiKeyDraft("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-skillsmp"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-config-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to save SkillsMP configuration."
      );
    },
  });

  const deleteSkillsMpMutation = useMutation({
    mutationFn: () => configurationApi.deleteSkillsMp(),
    onSuccess: async () => {
      messageApi.success("SkillsMP configuration deleted.");
      setSkillsMpApiKeyDraft("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-skillsmp"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-config-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to delete SkillsMP configuration."
      );
    },
  });

  const revealSkillsMpApiKeyMutation =
    useMutation<ConfigurationSecretValueStatus>({
      mutationFn: () => configurationApi.getSkillsMpApiKey({ reveal: true }),
      onSuccess: (result) => {
        setSkillsMpApiKeyDraft(result.value ?? "");
        messageApi.success("Loaded SkillsMP API key.");
      },
      onError: (error) => {
        messageApi.error(
          error instanceof Error
            ? error.message
            : "Failed to reveal SkillsMP API key."
        );
      },
    });

  const generateSecp256k1Mutation = useMutation({
    mutationFn: () => configurationApi.generateSecp256k1(),
    onSuccess: async (result) => {
      messageApi.success(
        result.backedUp
          ? "Generated signer key and backed up the previous private key."
          : "Generated signer key."
      );
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secp256k1"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-source"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to generate signer key."
      );
    },
  });

  const probeLlmTestMutation = useMutation({
    mutationFn: () =>
      configurationApi.probeLlmTest({
        providerType: llmProviderTypeDraft.trim(),
        endpoint: llmEndpointDraft.trim() || undefined,
        apiKey: llmApiKeyDraft.trim(),
      }),
    onSuccess: (result) => {
      setLlmProbeResult(result);
      messageApi[result.ok ? "success" : "warning"](formatProbeSummary(result));
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to probe provider."
      );
    },
  });

  const probeLlmModelsMutation = useMutation({
    mutationFn: () =>
      configurationApi.probeLlmModels({
        providerType: llmProviderTypeDraft.trim(),
        endpoint: llmEndpointDraft.trim() || undefined,
        apiKey: llmApiKeyDraft.trim(),
      }),
    onSuccess: (result) => {
      setLlmModelsResult(result);
      messageApi[result.ok ? "success" : "warning"](formatProbeSummary(result));
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to fetch models."
      );
    },
  });

  const setLlmDefaultMutation = useMutation({
    mutationFn: () => configurationApi.setLlmDefault(pendingDefaultProvider),
    onSuccess: async (providerName) => {
      messageApi.success(`Default provider set to ${providerName}.`);
      await queryClient.invalidateQueries({
        queryKey: ["settings-configuration-llm-default"],
      });
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : "Failed to update default provider."
      );
    },
  });

  const setSecretMutation = useMutation({
    mutationFn: () =>
      configurationApi.setSecret({
        key: secretKeyDraft.trim(),
        value: secretValueDraft,
      }),
    onSuccess: async () => {
      messageApi.success(`Secret ${secretKeyDraft.trim()} saved.`);
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-api-key"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-embeddings"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-websearch"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-skillsmp"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secp256k1"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-providers"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-instances"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-default"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to save secret."
      );
    },
  });

  const removeSecretMutation = useMutation({
    mutationFn: () => configurationApi.removeSecret(secretKeyDraft.trim()),
    onSuccess: async () => {
      messageApi.success(`Secret ${secretKeyDraft.trim()} removed.`);
      setSecretValueDraft("");
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secrets-raw"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-api-key"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-embeddings"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-websearch"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-skillsmp"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-secp256k1"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-providers"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-instances"],
        }),
        queryClient.invalidateQueries({
          queryKey: ["settings-configuration-llm-default"],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : "Failed to remove secret."
      );
    },
  });

  const handleNewWorkflowDraft = () => {
    const filename = "new_workflow.yaml";
    setSelectedWorkflowKey(null);
    setWorkflowFilename(filename);
    setWorkflowSource("home");
    setWorkflowContent(buildNewWorkflowTemplate(filename));
  };

  const handleNewLlmInstanceDraft = () => {
    setIsNewLlmInstanceDraft(true);
    setSelectedLlmInstanceName(null);
    setLlmInstanceNameDraft("new-provider");
    setLlmProviderTypeDraft(configurationLlmProvidersQuery.data?.[0]?.id ?? "");
    setLlmModelDraft("");
    setLlmEndpointDraft("");
    setLlmApiKeyDraft("");
    setLlmProbeResult(null);
    setLlmModelsResult(null);
  };

  const handleNewMcpServerDraft = () => {
    setIsNewMcpDraft(true);
    setSelectedMcpName(null);
    setMcpNameDraft("new-mcp-server");
    setMcpCommandDraft("");
    setMcpArgsDraft("");
    setMcpEnvDraft("{}");
    setMcpTimeoutDraft("60000");
  };

  const handleDeleteWorkflow = () => {
    if (!workflowFilename) {
      messageApi.warning("Select a workflow file before deleting.");
      return;
    }

    if (!window.confirm(`Delete ${workflowFilename} from ${workflowSource}?`)) {
      return;
    }

    deleteWorkflowMutation.mutate();
  };

  const handleDeleteLlmInstance = () => {
    const name = llmInstanceNameDraft.trim();
    if (!name) {
      messageApi.warning("Select an LLM instance before deleting.");
      return;
    }

    if (!window.confirm(`Delete LLM instance ${name}?`)) {
      return;
    }

    deleteLlmInstanceMutation.mutate();
  };

  const handleDeleteMcpServer = () => {
    const name = mcpNameDraft.trim();
    if (!name) {
      messageApi.warning("Select an MCP server before deleting.");
      return;
    }

    if (!window.confirm(`Delete MCP server ${name}?`)) {
      return;
    }

    deleteMcpServerMutation.mutate();
  };

  const handleDeleteEmbeddings = () => {
    if (
      !window.confirm(
        "Delete embeddings configuration, including the stored API key?"
      )
    ) {
      return;
    }

    deleteEmbeddingsMutation.mutate();
  };

  const handleDeleteWebSearch = () => {
    if (
      !window.confirm(
        "Delete web search configuration, including the stored API key?"
      )
    ) {
      return;
    }

    deleteWebSearchMutation.mutate();
  };

  const handleDeleteSkillsMp = () => {
    if (
      !window.confirm(
        "Delete SkillsMP configuration, including the stored API key?"
      )
    ) {
      return;
    }

    deleteSkillsMpMutation.mutate();
  };

  const handleGenerateSecp256k1 = () => {
    if (
      !window.confirm(
        "Generate a new secp256k1 private key and save it locally? Existing material will be backed up automatically."
      )
    ) {
      return;
    }

    generateSecp256k1Mutation.mutate();
  };

  const llmDefaultOptions = useMemo(
    () =>
      (configurationLlmInstancesQuery.data ?? []).map((item) => ({
        label: `${item.name} · ${item.providerDisplayName}`,
        value: item.name,
      })),
    [configurationLlmInstancesQuery.data]
  );

  const llmProviderTypeOptions = useMemo(
    () =>
      (configurationLlmProvidersQuery.data ?? []).map((item) => ({
        label: `${item.displayName} · ${item.category}`,
        value: item.id,
      })),
    [configurationLlmProvidersQuery.data]
  );

  const workspaceTabs = [
    {
      key: "system",
      label: "System status",
      children: (
        <SystemStatusSection
          configurationSourceError={configurationSourceQuery.error}
          configurationSourceStatus={configurationSourceQuery.data}
          configurationSourceUnavailable={configurationSourceQuery.isError}
          configurationHealthReady={configurationHealthQuery.isSuccess}
          configurationPathRecords={configurationPathRecords}
        />
      ),
    },
    {
      key: "workflows",
      label: "Workflow files",
      children: (
        <WorkflowFilesSection
          workflows={configurationWorkflowsQuery.data ?? []}
          selectedWorkflowId={
            selectedWorkflow ? workflowKey(selectedWorkflow) : null
          }
          workflowFilename={workflowFilename}
          workflowSource={workflowSource}
          workflowContent={workflowContent}
          canReloadSelectedWorkflow={Boolean(selectedWorkflow)}
          isSavingWorkflow={saveWorkflowMutation.isPending}
          isDeletingWorkflow={deleteWorkflowMutation.isPending}
          onRefresh={() => {
            void configurationWorkflowsQuery.refetch();
          }}
          onSelectWorkflow={setSelectedWorkflowKey}
          onWorkflowFilenameChange={setWorkflowFilename}
          onWorkflowSourceChange={setWorkflowSource}
          onWorkflowContentChange={setWorkflowContent}
          onNewDraft={handleNewWorkflowDraft}
          onReloadSelectedWorkflow={() => {
            void workflowDetailQuery.refetch();
          }}
          onSaveWorkflow={() => saveWorkflowMutation.mutate()}
          onDeleteWorkflow={handleDeleteWorkflow}
        />
      ),
    },
    {
      key: "embeddings",
      label: "Embeddings",
      children: (
        <EmbeddingsSection
          status={configurationEmbeddingsQuery.data}
          enabledDraft={embeddingsEnabledDraft}
          providerTypeDraft={embeddingsProviderTypeDraft}
          endpointDraft={embeddingsEndpointDraft}
          modelDraft={embeddingsModelDraft}
          apiKeyDraft={embeddingsApiKeyDraft}
          isRevealingApiKey={revealEmbeddingsApiKeyMutation.isPending}
          isSaving={saveEmbeddingsMutation.isPending}
          isDeleting={deleteEmbeddingsMutation.isPending}
          onReload={() => {
            void configurationEmbeddingsQuery.refetch();
          }}
          onEnabledChange={setEmbeddingsEnabledDraft}
          onProviderTypeChange={setEmbeddingsProviderTypeDraft}
          onEndpointChange={setEmbeddingsEndpointDraft}
          onModelChange={setEmbeddingsModelDraft}
          onApiKeyChange={setEmbeddingsApiKeyDraft}
          onRevealApiKey={() => revealEmbeddingsApiKeyMutation.mutate()}
          onSave={() => saveEmbeddingsMutation.mutate()}
          onDelete={handleDeleteEmbeddings}
        />
      ),
    },
    {
      key: "websearch",
      label: "Web Search",
      children: (
        <WebSearchSection
          status={configurationWebSearchQuery.data}
          enabledDraft={webSearchEnabledDraft}
          providerDraft={webSearchProviderDraft}
          endpointDraft={webSearchEndpointDraft}
          timeoutDraft={webSearchTimeoutDraft}
          searchDepthDraft={webSearchDepthDraft}
          apiKeyDraft={webSearchApiKeyDraft}
          isRevealingApiKey={revealWebSearchApiKeyMutation.isPending}
          isSaving={saveWebSearchMutation.isPending}
          isDeleting={deleteWebSearchMutation.isPending}
          onReload={() => {
            void configurationWebSearchQuery.refetch();
          }}
          onEnabledChange={setWebSearchEnabledDraft}
          onProviderChange={setWebSearchProviderDraft}
          onEndpointChange={setWebSearchEndpointDraft}
          onTimeoutChange={setWebSearchTimeoutDraft}
          onSearchDepthChange={setWebSearchDepthDraft}
          onApiKeyChange={setWebSearchApiKeyDraft}
          onRevealApiKey={() => revealWebSearchApiKeyMutation.mutate()}
          onSave={() => saveWebSearchMutation.mutate()}
          onDelete={handleDeleteWebSearch}
        />
      ),
    },
    {
      key: "skillsmp",
      label: "SkillsMP",
      children: (
        <SkillsMpSection
          status={configurationSkillsMpQuery.data}
          baseUrlDraft={skillsMpBaseUrlDraft}
          apiKeyDraft={skillsMpApiKeyDraft}
          isRevealingApiKey={revealSkillsMpApiKeyMutation.isPending}
          isSaving={saveSkillsMpMutation.isPending}
          isDeleting={deleteSkillsMpMutation.isPending}
          onReload={() => {
            void configurationSkillsMpQuery.refetch();
          }}
          onBaseUrlChange={setSkillsMpBaseUrlDraft}
          onApiKeyChange={setSkillsMpApiKeyDraft}
          onRevealApiKey={() => revealSkillsMpApiKeyMutation.mutate()}
          onSave={() => saveSkillsMpMutation.mutate()}
          onDelete={handleDeleteSkillsMp}
        />
      ),
    },
    {
      key: "signer-key",
      label: "Signer key",
      children: (
        <SignerKeySection
          status={configurationSecp256k1Query.data}
          isGenerating={generateSecp256k1Mutation.isPending}
          onReload={() => {
            void configurationSecp256k1Query.refetch();
          }}
          onGenerate={handleGenerateSecp256k1}
        />
      ),
    },
    {
      key: "connectors",
      label: "Connectors",
      children: (
        <ConnectorsSection
          rawDocument={configurationConnectorsRawQuery.data}
          connectorsJsonDraft={connectorsJsonDraft}
          isValidating={validateConnectorsRawMutation.isPending}
          isSaving={saveConnectorsRawMutation.isPending}
          onReload={() => {
            void configurationConnectorsRawQuery.refetch();
          }}
          onValidate={() => validateConnectorsRawMutation.mutate()}
          onSave={() => saveConnectorsRawMutation.mutate()}
          onConnectorsJsonChange={setConnectorsJsonDraft}
        />
      ),
    },
    {
      key: "llm",
      label: "LLM providers",
      children: (
        <LlmProvidersSection
          defaultProvider={configurationLlmDefaultQuery.data ?? ""}
          pendingDefaultProvider={pendingDefaultProvider}
          defaultOptions={llmDefaultOptions}
          providerTypeOptions={llmProviderTypeOptions}
          llmInstances={configurationLlmInstancesQuery.data ?? []}
          llmProviderTypes={configurationLlmProvidersQuery.data ?? []}
          llmApiKeyStatus={configurationLlmApiKeyQuery.data}
          selectedLlmInstanceName={selectedLlmInstanceName}
          isNewLlmInstanceDraft={isNewLlmInstanceDraft}
          llmInstanceNameDraft={llmInstanceNameDraft}
          llmProviderTypeDraft={llmProviderTypeDraft}
          llmModelDraft={llmModelDraft}
          llmEndpointDraft={llmEndpointDraft}
          llmApiKeyDraft={llmApiKeyDraft}
          llmProbeResult={llmProbeResult}
          llmModelsResult={llmModelsResult}
          isSettingDefaultProvider={setLlmDefaultMutation.isPending}
          isSavingInstance={saveLlmInstanceMutation.isPending}
          isDeletingInstance={deleteLlmInstanceMutation.isPending}
          isRevealingApiKey={revealLlmApiKeyMutation.isPending}
          isSettingApiKey={setLlmApiKeyMutation.isPending}
          isDeletingApiKey={deleteLlmApiKeyMutation.isPending}
          isTestingConnection={probeLlmTestMutation.isPending}
          isFetchingModels={probeLlmModelsMutation.isPending}
          onPendingDefaultProviderChange={setPendingDefaultProvider}
          onSetDefaultProvider={() => setLlmDefaultMutation.mutate()}
          onNewInstance={handleNewLlmInstanceDraft}
          onLlmInstanceNameChange={setLlmInstanceNameDraft}
          onLlmProviderTypeChange={setLlmProviderTypeDraft}
          onLlmModelChange={setLlmModelDraft}
          onLlmEndpointChange={setLlmEndpointDraft}
          onLlmApiKeyChange={setLlmApiKeyDraft}
          onSaveInstance={() => saveLlmInstanceMutation.mutate()}
          onDeleteInstance={handleDeleteLlmInstance}
          onRevealApiKey={() => revealLlmApiKeyMutation.mutate()}
          onSetApiKey={() => setLlmApiKeyMutation.mutate()}
          onDeleteApiKey={() => deleteLlmApiKeyMutation.mutate()}
          onTestConnection={() => probeLlmTestMutation.mutate()}
          onFetchModels={() => probeLlmModelsMutation.mutate()}
          onSelectInstance={(name) => {
            setIsNewLlmInstanceDraft(false);
            setSelectedLlmInstanceName(name);
          }}
        />
      ),
    },
    {
      key: "mcp",
      label: "MCP",
      children: (
        <McpSection
          servers={configurationMcpServersQuery.data ?? []}
          rawDocument={configurationMcpRawQuery.data}
          selectedMcpServerName={selectedMcpName}
          isNewMcpDraft={isNewMcpDraft}
          mcpNameDraft={mcpNameDraft}
          mcpCommandDraft={mcpCommandDraft}
          mcpArgsDraft={mcpArgsDraft}
          mcpEnvDraft={mcpEnvDraft}
          mcpTimeoutDraft={mcpTimeoutDraft}
          mcpJsonDraft={mcpJsonDraft}
          isSavingServer={saveMcpServerMutation.isPending}
          isDeletingServer={deleteMcpServerMutation.isPending}
          isValidatingRaw={validateMcpRawMutation.isPending}
          isSavingRaw={saveMcpRawMutation.isPending}
          onReloadServers={() => {
            void configurationMcpServersQuery.refetch();
          }}
          onSelectServer={(name) => {
            setIsNewMcpDraft(false);
            setSelectedMcpName(name);
          }}
          onMcpNameChange={setMcpNameDraft}
          onMcpCommandChange={setMcpCommandDraft}
          onMcpArgsChange={setMcpArgsDraft}
          onMcpEnvChange={setMcpEnvDraft}
          onMcpTimeoutChange={setMcpTimeoutDraft}
          onMcpJsonChange={setMcpJsonDraft}
          onNewServer={handleNewMcpServerDraft}
          onSaveServer={() => saveMcpServerMutation.mutate()}
          onDeleteServer={handleDeleteMcpServer}
          onReloadRaw={() => {
            void configurationMcpRawQuery.refetch();
          }}
          onValidateRaw={() => validateMcpRawMutation.mutate()}
          onSaveRaw={() => saveMcpRawMutation.mutate()}
        />
      ),
    },
    {
      key: "secrets",
      label: "Secrets",
      children: (
        <SecretsSection
          secretsJsonDraft={secretsJsonDraft}
          secretKeyDraft={secretKeyDraft}
          secretValueDraft={secretValueDraft}
          isSavingSecret={setSecretMutation.isPending}
          isRemovingSecret={removeSecretMutation.isPending}
          isSavingRaw={saveSecretsRawMutation.isPending}
          onReload={() => {
            void configurationSecretsRawQuery.refetch();
          }}
          onSecretsJsonChange={setSecretsJsonDraft}
          onSecretKeyChange={setSecretKeyDraft}
          onSecretValueChange={setSecretValueDraft}
          onSetSecret={() => setSecretMutation.mutate()}
          onRemoveSecret={() => removeSecretMutation.mutate()}
          onSaveRaw={() => saveSecretsRawMutation.mutate()}
        />
      ),
    },
    {
      key: "config",
      label: "Raw config",
      children: (
        <RawConfigSection
          configJsonDraft={configJsonDraft}
          isSaving={saveConfigRawMutation.isPending}
          onReload={() => {
            void configurationConfigRawQuery.refetch();
          }}
          onConfigJsonChange={setConfigJsonDraft}
          onSave={() => saveConfigRawMutation.mutate()}
        />
      ),
    },
  ];

  const summaryColumnCount = screens.xxl
    ? 4
    : screens.lg
    ? 3
    : screens.md
    ? 2
    : 1;
  const workspaceTabPlacement = screens.xl ? "start" : "top";
  const runtimeWorkspaceContent = (
    <ProCard title="Runtime configuration workspace" ghost>
      {hasLocalRuntimeAccess ? (
        <Tabs
          items={workspaceTabs}
          size="large"
          tabPlacement={workspaceTabPlacement}
          tabBarGutter={8}
        />
      ) : (
        <Alert
          type="warning"
          showIcon
          title="Local runtime access is unavailable."
          description="Attach the console to a loopback or local tool host to manage configuration files, workflows, providers, MCP servers, and secrets from this workspace."
        />
      )}
    </ProCard>
  );

  return (
    <PageContainer
      title="Runtime Settings"
      content="Manage local runtime workflows, providers, connectors, MCP servers, secrets, and raw configuration documents from one dedicated workspace."
      onBack={() => history.push("/settings/console")}
    >
      {messageContextHolder}
      <Space direction="vertical" style={{ width: "100%" }} size={16}>
        <ProCard
          title="Runtime environment summary"
          {...moduleCardProps}
          style={fillCardStyle}
        >
          <ProDescriptions<SettingsSummaryRecord>
            column={summaryColumnCount}
            dataSource={settingsSummary}
            columns={settingsSummaryColumns}
          />
        </ProCard>
        {runtimeWorkspaceContent}
      </Space>
    </PageContainer>
  );
};

export default RuntimeSettingsPage;
