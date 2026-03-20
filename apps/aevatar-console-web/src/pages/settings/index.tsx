import type {
  ProDescriptionsItemProps,
  ProFormInstance,
} from '@ant-design/pro-components';
import {
  PageContainer,
  ProCard,
  ProDescriptions,
  ProForm,
  ProFormDigit,
  ProFormSelect,
  ProFormText,
  ProList,
} from '@ant-design/pro-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { history } from '@umijs/max';
import {
  Alert,
  Avatar,
  Button,
  Col,
  Empty,
  Grid,
  Input,
  message,
  Row,
  Select,
  Space,
  Tabs,
  Tag,
  Typography,
} from 'antd';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { configurationApi } from '@/shared/api/configurationApi';
import { consoleApi } from '@/shared/api/consoleApi';
import type {
  ConfigurationEmbeddingsStatus,
  ConfigurationLlmApiKeyStatus,
  ConfigurationLlmProbeResult,
  ConfigurationMcpServer,
  ConfigurationPathStatus,
  ConfigurationSecp256k1Status,
  ConfigurationSecretValueStatus,
  ConfigurationSkillsMpStatus,
  ConfigurationWebSearchStatus,
  ConfigurationWorkflowFile,
} from '@/shared/api/models';
import { loadStoredAuthSession } from '@/shared/auth/session';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { buildObservabilityTargets } from '@/shared/observability/observabilityLinks';
import {
  type ActorGraphDirection,
  type ConsolePreferences,
  loadConsolePreferences,
  resetConsolePreferences,
  saveConsolePreferences,
  type StudioAppearanceTheme,
  type StudioColorMode,
} from '@/shared/preferences/consolePreferences';
import { buildWorkflowCatalogOptions } from '@/shared/workflows/catalogVisibility';
import {
  fillCardStyle,
  moduleCardProps,
  stretchColumnStyle,
} from '@/shared/ui/proComponents';

type SettingsSummaryRecord = {
  preferredWorkflow: string;
  graphDirection: ActorGraphDirection;
  grafanaStatus: 'configured' | 'missing';
  configStatus: 'ready' | 'unavailable';
  configMode: string;
  runtimeWorkflowFiles: number;
  primitiveCount: number;
  defaultProvider: string;
  observabilityTargetsConfigured: number;
};

type SettingsHintItem = {
  id: string;
  text: string;
};

type SettingsObservabilityItem = {
  id: string;
  label: string;
  description: string;
  status: 'configured' | 'missing';
  homeUrl: string;
  exploreUrl: string;
};

type SettingsProfileIdentityRecord = {
  provider: string;
  displayName: string;
  email: string;
  subject: string;
  emailVerified: 'Verified' | 'Unverified' | 'Unknown';
};

type SettingsProfileAccessRecord = {
  roles: string[];
  groups: string[];
  permissions: string[];
  scope: string;
  expiresAt: string;
};

type ConfigurationPathRecord = {
  id: string;
  label: string;
  status: ConfigurationPathStatus;
};

type WorkflowDraftSource = 'home' | 'repo';

const grafanaValueEnum = {
  configured: { text: 'Configured', status: 'Success' },
  missing: { text: 'Not configured', status: 'Default' },
} as const;

const configurationValueEnum = {
  ready: { text: 'Ready', status: 'Success' },
  unavailable: { text: 'Unavailable', status: 'Error' },
} as const;

const settingsSummaryColumns: ProDescriptionsItemProps<SettingsSummaryRecord>[] =
  [
    {
      title: 'Preferred workflow',
      dataIndex: 'preferredWorkflow',
      render: (_, record) => (
        <Tag color="processing">{record.preferredWorkflow}</Tag>
      ),
    },
    {
      title: 'Graph direction',
      dataIndex: 'graphDirection',
    },
    {
      title: 'Grafana',
      dataIndex: 'grafanaStatus',
      valueType: 'status' as any,
      valueEnum: grafanaValueEnum,
    },
    {
      title: 'Configuration API',
      dataIndex: 'configStatus',
      valueType: 'status' as any,
      valueEnum: configurationValueEnum,
    },
    {
      title: 'Configuration mode',
      dataIndex: 'configMode',
      render: (_, record) => record.configMode || 'unknown',
    },
    {
      title: 'Runtime workflow files',
      dataIndex: 'runtimeWorkflowFiles',
      valueType: 'digit',
    },
    {
      title: 'Primitive count',
      dataIndex: 'primitiveCount',
      valueType: 'digit',
    },
    {
      title: 'Default provider',
      dataIndex: 'defaultProvider',
      render: (_, record) => <Tag>{record.defaultProvider || 'default'}</Tag>,
    },
    {
      title: 'Configured targets',
      dataIndex: 'observabilityTargetsConfigured',
      valueType: 'digit',
    },
  ];

const settingsProfileIdentityColumns: ProDescriptionsItemProps<SettingsProfileIdentityRecord>[] =
  [
    {
      title: 'Provider',
      dataIndex: 'provider',
      render: (_, record) => <Tag color="processing">{record.provider}</Tag>,
    },
    {
      title: 'Display name',
      dataIndex: 'displayName',
    },
    {
      title: 'Email',
      dataIndex: 'email',
      render: (_, record) => record.email || 'n/a',
    },
    {
      title: 'Email status',
      dataIndex: 'emailVerified',
      render: (_, record) => (
        <Tag color={record.emailVerified === 'Verified' ? 'success' : 'default'}>
          {record.emailVerified}
        </Tag>
      ),
    },
    {
      title: 'Subject',
      dataIndex: 'subject',
      render: (_, record) => (
        <Typography.Text copyable>{record.subject}</Typography.Text>
      ),
    },
  ];

const settingsProfileAccessColumns: ProDescriptionsItemProps<SettingsProfileAccessRecord>[] =
  [
    {
      title: 'Roles',
      dataIndex: 'roles',
      render: (_, record) =>
        record.roles.length > 0 ? (
          <Space wrap size={[8, 8]}>
            {record.roles.map((role) => (
              <Tag key={role}>{role}</Tag>
            ))}
          </Space>
        ) : (
          <Typography.Text type="secondary">No roles</Typography.Text>
        ),
    },
    {
      title: 'Groups',
      dataIndex: 'groups',
      render: (_, record) =>
        record.groups.length > 0 ? (
          <Space wrap size={[8, 8]}>
            {record.groups.map((group) => (
              <Tag key={group}>{group}</Tag>
            ))}
          </Space>
        ) : (
          <Typography.Text type="secondary">No groups</Typography.Text>
        ),
    },
    {
      title: 'Permissions',
      dataIndex: 'permissions',
      render: (_, record) =>
        record.permissions.length > 0 ? (
          <Space wrap size={[8, 8]}>
            {record.permissions.map((permission) => (
              <Tag key={permission}>{permission}</Tag>
            ))}
          </Space>
        ) : (
          <Typography.Text type="secondary">No permissions</Typography.Text>
        ),
    },
    {
      title: 'Scope',
      dataIndex: 'scope',
      render: (_, record) =>
        record.scope ? (
          <Typography.Text code>{record.scope}</Typography.Text>
        ) : (
          <Typography.Text type="secondary">No scope</Typography.Text>
        ),
    },
    {
      title: 'Access token expires',
      dataIndex: 'expiresAt',
    },
  ];

const settingsHints: SettingsHintItem[] = [
  {
    id: 'hint-runtime',
    text: 'Runtime configuration is managed from this page and applies to the local tool host.',
  },
  {
    id: 'hint-workflow',
    text: 'Workflow files in the workspace are backed by local home/repo paths and can be edited directly from this page.',
  },
  {
    id: 'hint-secrets',
    text: 'Secrets and raw config writes are local-only management actions. Review JSON carefully before saving.',
  },
];

const studioAppearanceOptions: Array<{
  label: string;
  value: StudioAppearanceTheme;
}> = [
  { label: 'Blue', value: 'blue' },
  { label: 'Coral', value: 'coral' },
  { label: 'Forest', value: 'forest' },
];

const studioColorModeOptions: Array<{
  label: string;
  value: StudioColorMode;
}> = [
  { label: 'Light', value: 'light' },
  { label: 'Dark', value: 'dark' },
];

function workflowKey(
  item: Pick<ConfigurationWorkflowFile, 'filename' | 'source'>,
): string {
  return `${item.source}:${item.filename}`;
}

function normalizeWorkflowSource(source: string): WorkflowDraftSource {
  return source === 'repo' ? 'repo' : 'home';
}

function buildNewWorkflowTemplate(filename: string): string {
  const normalizedName = filename
    .replace(/\.(yaml|yml)$/i, '')
    .replace(/[^A-Za-z0-9_]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .toLowerCase();
  const workflowName = normalizedName || 'new_workflow';

  return `name: ${workflowName}\ndescription: Draft workflow\nsteps:\n  - id: start\n    type: assign\n    parameters:\n      target: status\n      value: ready\n`;
}

function formatMcpArgs(args: string[]): string {
  return args.join('\n');
}

function formatMcpEnv(env: Record<string, string>): string {
  return JSON.stringify(env, null, 2);
}

function parseMcpArgs(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map((entry) => entry.trim())
    .filter(Boolean);
}

function parseMcpEnv(value: string): Record<string, string> {
  const trimmed = value.trim();
  if (!trimmed) {
    return {};
  }

  const parsed = JSON.parse(trimmed) as unknown;
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error('MCP env must be a JSON object.');
  }

  return Object.fromEntries(
    Object.entries(parsed as Record<string, unknown>).map(([key, entry]) => {
      if (typeof entry !== 'string') {
        throw new Error(`MCP env value for "${key}" must be a string.`);
      }

      return [key, entry];
    }),
  );
}

function formatProbeSummary(result: ConfigurationLlmProbeResult): string {
  if (!result.ok) {
    return result.error || 'Probe failed.';
  }

  if (result.models && result.models.length > 0) {
    return `Discovered ${result.models.length} models.`;
  }

  if (result.modelsCount !== undefined) {
    return `Probe succeeded with ${result.modelsCount} models.`;
  }

  return 'Probe succeeded.';
}

const SettingsPage: React.FC = () => {
  const [messageApi, messageContextHolder] = message.useMessage();
  const queryClient = useQueryClient();
  const screens = Grid.useBreakpoint();
  const formRef = useRef<ProFormInstance<ConsolePreferences> | undefined>(
    undefined,
  );
  const authSession = useMemo(() => loadStoredAuthSession(), []);
  const [preferences, setPreferences] = useState<ConsolePreferences>(() =>
    loadConsolePreferences(),
  );
  const [selectedWorkflowKey, setSelectedWorkflowKey] = useState<string | null>(
    null,
  );
  const [selectedLlmInstanceName, setSelectedLlmInstanceName] = useState<
    string | null
  >(null);
  const [isNewLlmInstanceDraft, setIsNewLlmInstanceDraft] = useState(false);
  const [workflowFilename, setWorkflowFilename] = useState('');
  const [workflowSource, setWorkflowSource] =
    useState<WorkflowDraftSource>('home');
  const [workflowContent, setWorkflowContent] = useState('');
  const [llmInstanceNameDraft, setLlmInstanceNameDraft] = useState('');
  const [llmProviderTypeDraft, setLlmProviderTypeDraft] = useState('');
  const [llmModelDraft, setLlmModelDraft] = useState('');
  const [llmEndpointDraft, setLlmEndpointDraft] = useState('');
  const [llmApiKeyDraft, setLlmApiKeyDraft] = useState('');
  const [llmProbeResult, setLlmProbeResult] =
    useState<ConfigurationLlmProbeResult | null>(null);
  const [llmModelsResult, setLlmModelsResult] =
    useState<ConfigurationLlmProbeResult | null>(null);
  const [embeddingsEnabledDraft, setEmbeddingsEnabledDraft] = useState(true);
  const [embeddingsProviderTypeDraft, setEmbeddingsProviderTypeDraft] =
    useState('deepseek');
  const [embeddingsEndpointDraft, setEmbeddingsEndpointDraft] = useState(
    'https://dashscope.aliyuncs.com/compatible-mode/v1',
  );
  const [embeddingsModelDraft, setEmbeddingsModelDraft] =
    useState('text-embedding-v3');
  const [embeddingsApiKeyDraft, setEmbeddingsApiKeyDraft] = useState('');
  const [webSearchEnabledDraft, setWebSearchEnabledDraft] = useState(true);
  const [webSearchProviderDraft, setWebSearchProviderDraft] =
    useState('tavily');
  const [webSearchEndpointDraft, setWebSearchEndpointDraft] = useState('');
  const [webSearchTimeoutDraft, setWebSearchTimeoutDraft] = useState('15000');
  const [webSearchDepthDraft, setWebSearchDepthDraft] = useState('advanced');
  const [webSearchApiKeyDraft, setWebSearchApiKeyDraft] = useState('');
  const [skillsMpBaseUrlDraft, setSkillsMpBaseUrlDraft] = useState(
    'https://skillsmp.com',
  );
  const [skillsMpApiKeyDraft, setSkillsMpApiKeyDraft] = useState('');
  const [selectedMcpName, setSelectedMcpName] = useState<string | null>(null);
  const [isNewMcpDraft, setIsNewMcpDraft] = useState(false);
  const [mcpNameDraft, setMcpNameDraft] = useState('');
  const [mcpCommandDraft, setMcpCommandDraft] = useState('');
  const [mcpArgsDraft, setMcpArgsDraft] = useState('');
  const [mcpEnvDraft, setMcpEnvDraft] = useState('{}');
  const [mcpTimeoutDraft, setMcpTimeoutDraft] = useState('60000');
  const [configJsonDraft, setConfigJsonDraft] = useState('');
  const [connectorsJsonDraft, setConnectorsJsonDraft] = useState('');
  const [mcpJsonDraft, setMcpJsonDraft] = useState('');
  const [secretKeyDraft, setSecretKeyDraft] = useState('');
  const [secretValueDraft, setSecretValueDraft] = useState('');
  const [secretsJsonDraft, setSecretsJsonDraft] = useState('');
  const [pendingDefaultProvider, setPendingDefaultProvider] = useState('');

  const workflowCatalogQuery = useQuery({
    queryKey: ['settings-workflow-catalog'],
    queryFn: () => consoleApi.listWorkflowCatalog(),
  });
  const capabilitiesQuery = useQuery({
    queryKey: ['settings-capabilities'],
    queryFn: () => consoleApi.getCapabilities(),
  });
  const configurationHealthQuery = useQuery({
    queryKey: ['settings-configuration-health'],
    queryFn: () => configurationApi.getHealth(),
    retry: false,
  });
  const configurationSourceQuery = useQuery({
    queryKey: ['settings-configuration-source'],
    queryFn: () => configurationApi.getSourceStatus(),
    retry: false,
  });
  const hasLocalRuntimeAccess =
    configurationSourceQuery.data?.localRuntimeAccess ?? false;
  const configurationWorkflowsQuery = useQuery({
    queryKey: ['settings-configuration-workflows'],
    queryFn: () => configurationApi.listWorkflows('all'),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationLlmProvidersQuery = useQuery({
    queryKey: ['settings-configuration-llm-providers'],
    queryFn: () => configurationApi.listLlmProviders(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationLlmInstancesQuery = useQuery({
    queryKey: ['settings-configuration-llm-instances'],
    queryFn: () => configurationApi.listLlmInstances(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationLlmDefaultQuery = useQuery({
    queryKey: ['settings-configuration-llm-default'],
    queryFn: () => configurationApi.getLlmDefault(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationEmbeddingsQuery = useQuery<ConfigurationEmbeddingsStatus>({
    queryKey: ['settings-configuration-embeddings'],
    queryFn: () => configurationApi.getEmbeddingsStatus(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationWebSearchQuery = useQuery<ConfigurationWebSearchStatus>({
    queryKey: ['settings-configuration-websearch'],
    queryFn: () => configurationApi.getWebSearchStatus(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationSkillsMpQuery = useQuery<ConfigurationSkillsMpStatus>({
    queryKey: ['settings-configuration-skillsmp'],
    queryFn: () => configurationApi.getSkillsMpStatus(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationSecp256k1Query = useQuery<ConfigurationSecp256k1Status>({
    queryKey: ['settings-configuration-secp256k1'],
    queryFn: () => configurationApi.getSecp256k1Status(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationConfigRawQuery = useQuery({
    queryKey: ['settings-configuration-config-raw'],
    queryFn: () => configurationApi.getConfigRaw(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationConnectorsRawQuery = useQuery({
    queryKey: ['settings-configuration-connectors-raw'],
    queryFn: () => configurationApi.getConnectorsRaw(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationMcpServersQuery = useQuery({
    queryKey: ['settings-configuration-mcp-servers'],
    queryFn: () => configurationApi.listMcpServers(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationMcpRawQuery = useQuery({
    queryKey: ['settings-configuration-mcp-raw'],
    queryFn: () => configurationApi.getMcpRaw(),
    enabled: hasLocalRuntimeAccess,
    retry: false,
  });
  const configurationSecretsRawQuery = useQuery({
    queryKey: ['settings-configuration-secrets-raw'],
    queryFn: () => configurationApi.getSecretsRaw(),
    enabled: hasLocalRuntimeAccess,
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
      'settings-configuration-workflow-detail',
      selectedWorkflow?.filename,
      selectedWorkflow?.source,
    ],
    queryFn: () => {
      if (!selectedWorkflow) {
        throw new Error('No workflow is selected.');
      }

      return configurationApi.getWorkflow(
        selectedWorkflow.filename,
        normalizeWorkflowSource(selectedWorkflow.source),
      );
    },
    enabled: hasLocalRuntimeAccess && Boolean(selectedWorkflow),
    retry: false,
  });

  const configurationLlmApiKeyQuery = useQuery<ConfigurationLlmApiKeyStatus>({
    queryKey: ['settings-configuration-llm-api-key', selectedLlmInstance?.name],
    queryFn: () => {
      if (!selectedLlmInstance?.name) {
        throw new Error('No LLM instance is selected.');
      }

      return configurationApi.getLlmApiKey(selectedLlmInstance.name);
    },
    enabled: hasLocalRuntimeAccess && Boolean(selectedLlmInstance?.name),
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
    setLlmApiKeyDraft('');
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
      configurationEmbeddingsQuery.data.enabled ?? true,
    );
    setEmbeddingsProviderTypeDraft(
      configurationEmbeddingsQuery.data.providerType || 'deepseek',
    );
    setEmbeddingsEndpointDraft(
      configurationEmbeddingsQuery.data.endpoint ||
        'https://dashscope.aliyuncs.com/compatible-mode/v1',
    );
    setEmbeddingsModelDraft(
      configurationEmbeddingsQuery.data.model || 'text-embedding-v3',
    );
    setEmbeddingsApiKeyDraft('');
  }, [configurationEmbeddingsQuery.data]);

  useEffect(() => {
    if (!configurationWebSearchQuery.data) {
      return;
    }

    setWebSearchEnabledDraft(configurationWebSearchQuery.data.enabled ?? true);
    setWebSearchProviderDraft(
      configurationWebSearchQuery.data.provider || 'tavily',
    );
    setWebSearchEndpointDraft(configurationWebSearchQuery.data.endpoint || '');
    setWebSearchTimeoutDraft(
      configurationWebSearchQuery.data.timeoutMs != null
        ? String(configurationWebSearchQuery.data.timeoutMs)
        : '15000',
    );
    setWebSearchDepthDraft(
      configurationWebSearchQuery.data.searchDepth || 'advanced',
    );
    setWebSearchApiKeyDraft('');
  }, [configurationWebSearchQuery.data]);

  useEffect(() => {
    if (!configurationSkillsMpQuery.data) {
      return;
    }

    setSkillsMpBaseUrlDraft(
      configurationSkillsMpQuery.data.baseUrl || 'https://skillsmp.com',
    );
    setSkillsMpApiKeyDraft('');
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

  const workflowOptions = useMemo(
    () =>
      buildWorkflowCatalogOptions(
        workflowCatalogQuery.data ?? [],
        preferences.preferredWorkflow,
      ),
    [preferences.preferredWorkflow, workflowCatalogQuery.data],
  );

  const observabilityTargets = useMemo<SettingsObservabilityItem[]>(
    () =>
      buildObservabilityTargets(preferences, {
        workflow: preferences.preferredWorkflow,
        actorId: '',
        commandId: '',
        runId: '',
        stepId: '',
      }).map((target) => ({
        id: target.id,
        label: target.label,
        description: target.description,
        status: target.status,
        homeUrl: target.homeUrl,
        exploreUrl: target.exploreUrl,
      })),
    [preferences],
  );

  const settingsSummary = useMemo<SettingsSummaryRecord>(
    () => ({
      preferredWorkflow: preferences.preferredWorkflow,
      graphDirection: preferences.actorGraphDirection,
      grafanaStatus: preferences.grafanaBaseUrl ? 'configured' : 'missing',
      configStatus: configurationHealthQuery.isSuccess && hasLocalRuntimeAccess
        ? 'ready'
        : 'unavailable',
      configMode: hasLocalRuntimeAccess
        ? (configurationSourceQuery.data?.mode ?? '')
        : 'restricted',
      runtimeWorkflowFiles: hasLocalRuntimeAccess
        ? (configurationWorkflowsQuery.data?.length ?? 0)
        : 0,
      primitiveCount: capabilitiesQuery.data?.primitives.length ?? 0,
      defaultProvider: hasLocalRuntimeAccess
        ? (configurationLlmDefaultQuery.data ?? '')
        : '',
      observabilityTargetsConfigured: observabilityTargets.filter(
        (target) => target.status === 'configured',
      ).length,
    }),
    [
      capabilitiesQuery.data?.primitives.length,
      configurationHealthQuery.isSuccess,
      configurationLlmDefaultQuery.data,
      hasLocalRuntimeAccess,
      configurationSourceQuery.data?.mode,
      configurationWorkflowsQuery.data?.length,
      observabilityTargets,
      preferences,
    ],
  );

  const settingsUsageNotes = useMemo<SettingsHintItem[]>(
    () =>
      hasLocalRuntimeAccess
        ? settingsHints
        : [
            {
              id: 'hint-runtime-restricted',
              text: 'Local runtime configuration is hidden because this console is not connected through a loopback tool host.',
            },
          ],
    [hasLocalRuntimeAccess],
  );

  const profileIdentity = useMemo<SettingsProfileIdentityRecord | null>(() => {
    if (!authSession) {
      return null;
    }

    return {
      provider: 'NyxID',
      displayName:
        authSession.user.name || authSession.user.email || authSession.user.sub,
      email: authSession.user.email ?? '',
      subject: authSession.user.sub,
      emailVerified:
        authSession.user.email_verified === true
          ? 'Verified'
          : authSession.user.email_verified === false
            ? 'Unverified'
            : 'Unknown',
    };
  }, [authSession]);

  const profileAccess = useMemo<SettingsProfileAccessRecord | null>(() => {
    if (!authSession) {
      return null;
    }

    return {
      roles: authSession.user.roles ?? [],
      groups: authSession.user.groups ?? [],
      permissions: authSession.user.permissions ?? [],
      scope: authSession.tokens.scope ?? '',
      expiresAt: formatDateTime(authSession.tokens.expiresAt),
    };
  }, [authSession]);

  const configurationPathRecords = useMemo<ConfigurationPathRecord[]>(() => {
    const doctor = configurationSourceQuery.data?.doctor;
    if (!doctor) {
      return [];
    }

    return [
      { id: 'config', label: 'config.json', status: doctor.config },
      { id: 'secrets', label: 'secrets.json', status: doctor.secrets },
      {
        id: 'workflows-home',
        label: 'workflows (home)',
        status: doctor.workflowsHome,
      },
      {
        id: 'workflows-repo',
        label: 'workflows (repo)',
        status: doctor.workflowsRepo,
      },
      { id: 'connectors', label: 'connectors.json', status: doctor.connectors },
      { id: 'mcp', label: 'mcp.json', status: doctor.mcp },
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
          queryKey: ['settings-configuration-workflows'],
        }),
        queryClient.invalidateQueries({
          queryKey: [
            'settings-configuration-workflow-detail',
            saved.filename,
            saved.source,
          ],
        }),
      ]);
      setSelectedWorkflowKey(workflowKey(saved));
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to save workflow.',
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
      setWorkflowFilename('');
      setWorkflowContent('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-workflows'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-workflow-detail'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to delete workflow.',
      );
    },
  });

  const saveConfigRawMutation = useMutation({
    mutationFn: () => configurationApi.saveConfigRaw(configJsonDraft),
    onSuccess: async () => {
      messageApi.success('config.json saved.');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-config-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-websearch'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-skillsmp'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to save config.json.',
      );
    },
  });

  const saveConnectorsRawMutation = useMutation({
    mutationFn: () => configurationApi.saveConnectorsRaw(connectorsJsonDraft),
    onSuccess: async () => {
      messageApi.success('connectors.json saved.');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-connectors-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to save connectors.json.',
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
          : 'Failed to validate connectors.json.',
      );
    },
  });

  const saveMcpRawMutation = useMutation({
    mutationFn: () => configurationApi.saveMcpRaw(mcpJsonDraft),
    onSuccess: async () => {
      messageApi.success('mcp.json saved.');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-mcp-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to save mcp.json.',
      );
    },
  });

  const saveMcpServerMutation = useMutation({
    mutationFn: () => {
      const timeoutMs = Number.parseInt(mcpTimeoutDraft.trim(), 10);
      if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
        throw new Error('MCP timeout must be a positive integer.');
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
          queryKey: ['settings-configuration-mcp-servers'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-mcp-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
      setIsNewMcpDraft(false);
      setSelectedMcpName(server.name);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to save MCP server.',
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
        error instanceof Error ? error.message : 'Failed to validate mcp.json.',
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
      setMcpNameDraft('');
      setMcpCommandDraft('');
      setMcpArgsDraft('');
      setMcpEnvDraft('{}');
      setMcpTimeoutDraft('60000');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-mcp-servers'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-mcp-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to delete MCP server.',
      );
    },
  });

  const saveSecretsRawMutation = useMutation({
    mutationFn: () => configurationApi.saveSecretsRaw(secretsJsonDraft),
    onSuccess: async () => {
      messageApi.success('secrets.json saved.');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-api-key'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-embeddings'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-websearch'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-skillsmp'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secp256k1'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-providers'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-instances'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-default'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to save secrets.json.',
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
          queryKey: ['settings-configuration-llm-instances'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-providers'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-default'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-api-key', name],
        }),
      ]);
      setIsNewLlmInstanceDraft(false);
      setSelectedLlmInstanceName(name);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to save LLM instance.',
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
      setLlmInstanceNameDraft('');
      setLlmProviderTypeDraft('');
      setLlmModelDraft('');
      setLlmEndpointDraft('');
      setLlmApiKeyDraft('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-instances'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-providers'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-default'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-api-key', name],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to delete LLM instance.',
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
          queryKey: ['settings-configuration-llm-api-key', name],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-default'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-instances'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to update API key.',
      );
    },
  });

  const deleteLlmApiKeyMutation = useMutation({
    mutationFn: () =>
      configurationApi.deleteLlmApiKey(llmInstanceNameDraft.trim()),
    onSuccess: async () => {
      const name = llmInstanceNameDraft.trim();
      messageApi.success(`API key removed for ${name}.`);
      setLlmApiKeyDraft('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-api-key', name],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-default'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-instances'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to remove API key.',
      );
    },
  });

  const revealLlmApiKeyMutation = useMutation({
    mutationFn: () =>
      configurationApi.getLlmApiKey(llmInstanceNameDraft.trim(), {
        reveal: true,
      }),
    onSuccess: (result) => {
      setLlmApiKeyDraft(result.value ?? '');
      messageApi.success(`Loaded API key for ${result.providerName}.`);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to reveal API key.',
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
      messageApi.success('Embeddings configuration saved.');
      setEmbeddingsApiKeyDraft('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-embeddings'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to save embeddings configuration.',
      );
    },
  });

  const deleteEmbeddingsMutation = useMutation({
    mutationFn: () => configurationApi.deleteEmbeddings(),
    onSuccess: async () => {
      messageApi.success('Embeddings configuration deleted.');
      setEmbeddingsApiKeyDraft('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-embeddings'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to delete embeddings configuration.',
      );
    },
  });

  const revealEmbeddingsApiKeyMutation =
    useMutation<ConfigurationSecretValueStatus>({
      mutationFn: () => configurationApi.getEmbeddingsApiKey({ reveal: true }),
      onSuccess: (result) => {
        setEmbeddingsApiKeyDraft(result.value ?? '');
        messageApi.success('Loaded embeddings API key.');
      },
      onError: (error) => {
        messageApi.error(
          error instanceof Error
            ? error.message
            : 'Failed to reveal embeddings API key.',
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
        throw new Error('Web search timeout must be a positive integer.');
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
      messageApi.success('Web search configuration saved.');
      setWebSearchApiKeyDraft('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-websearch'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-config-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to save web search configuration.',
      );
    },
  });

  const deleteWebSearchMutation = useMutation({
    mutationFn: () => configurationApi.deleteWebSearch(),
    onSuccess: async () => {
      messageApi.success('Web search configuration deleted.');
      setWebSearchApiKeyDraft('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-websearch'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-config-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to delete web search configuration.',
      );
    },
  });

  const revealWebSearchApiKeyMutation =
    useMutation<ConfigurationSecretValueStatus>({
      mutationFn: () => configurationApi.getWebSearchApiKey({ reveal: true }),
      onSuccess: (result) => {
        setWebSearchApiKeyDraft(result.value ?? '');
        messageApi.success('Loaded web search API key.');
      },
      onError: (error) => {
        messageApi.error(
          error instanceof Error
            ? error.message
            : 'Failed to reveal web search API key.',
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
      messageApi.success('SkillsMP configuration saved.');
      setSkillsMpApiKeyDraft('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-skillsmp'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-config-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to save SkillsMP configuration.',
      );
    },
  });

  const deleteSkillsMpMutation = useMutation({
    mutationFn: () => configurationApi.deleteSkillsMp(),
    onSuccess: async () => {
      messageApi.success('SkillsMP configuration deleted.');
      setSkillsMpApiKeyDraft('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-skillsmp'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-config-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to delete SkillsMP configuration.',
      );
    },
  });

  const revealSkillsMpApiKeyMutation =
    useMutation<ConfigurationSecretValueStatus>({
      mutationFn: () => configurationApi.getSkillsMpApiKey({ reveal: true }),
      onSuccess: (result) => {
        setSkillsMpApiKeyDraft(result.value ?? '');
        messageApi.success('Loaded SkillsMP API key.');
      },
      onError: (error) => {
        messageApi.error(
          error instanceof Error
            ? error.message
            : 'Failed to reveal SkillsMP API key.',
        );
      },
    });

  const generateSecp256k1Mutation = useMutation({
    mutationFn: () => configurationApi.generateSecp256k1(),
    onSuccess: async (result) => {
      messageApi.success(
        result.backedUp
          ? 'Generated signer key and backed up the previous private key.'
          : 'Generated signer key.',
      );
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secp256k1'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-source'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to generate signer key.',
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
      messageApi[result.ok ? 'success' : 'warning'](formatProbeSummary(result));
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to probe provider.',
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
      messageApi[result.ok ? 'success' : 'warning'](formatProbeSummary(result));
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to fetch models.',
      );
    },
  });

  const setLlmDefaultMutation = useMutation({
    mutationFn: () => configurationApi.setLlmDefault(pendingDefaultProvider),
    onSuccess: async (providerName) => {
      messageApi.success(`Default provider set to ${providerName}.`);
      await queryClient.invalidateQueries({
        queryKey: ['settings-configuration-llm-default'],
      });
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error
          ? error.message
          : 'Failed to update default provider.',
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
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-api-key'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-embeddings'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-websearch'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-skillsmp'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secp256k1'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-providers'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-instances'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-default'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to save secret.',
      );
    },
  });

  const removeSecretMutation = useMutation({
    mutationFn: () => configurationApi.removeSecret(secretKeyDraft.trim()),
    onSuccess: async () => {
      messageApi.success(`Secret ${secretKeyDraft.trim()} removed.`);
      setSecretValueDraft('');
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secrets-raw'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-api-key'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-embeddings'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-websearch'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-skillsmp'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-secp256k1'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-providers'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-instances'],
        }),
        queryClient.invalidateQueries({
          queryKey: ['settings-configuration-llm-default'],
        }),
      ]);
    },
    onError: (error) => {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to remove secret.',
      );
    },
  });

  const handleSavePreferences = async (values: ConsolePreferences) => {
    const next = saveConsolePreferences(values);
    setPreferences(next);
    messageApi.success('Console preferences saved.');
    return true;
  };

  const handleResetPreferences = () => {
    const next = resetConsolePreferences();
    setPreferences(next);
    formRef.current?.setFieldsValue(next);
    messageApi.success('Console preferences reset to defaults.');
  };

  const handleNewWorkflowDraft = () => {
    const filename = 'new_workflow.yaml';
    setSelectedWorkflowKey(null);
    setWorkflowFilename(filename);
    setWorkflowSource('home');
    setWorkflowContent(buildNewWorkflowTemplate(filename));
  };

  const handleNewLlmInstanceDraft = () => {
    setIsNewLlmInstanceDraft(true);
    setSelectedLlmInstanceName(null);
    setLlmInstanceNameDraft('new-provider');
    setLlmProviderTypeDraft(configurationLlmProvidersQuery.data?.[0]?.id ?? '');
    setLlmModelDraft('');
    setLlmEndpointDraft('');
    setLlmApiKeyDraft('');
    setLlmProbeResult(null);
    setLlmModelsResult(null);
  };

  const handleNewMcpServerDraft = () => {
    setIsNewMcpDraft(true);
    setSelectedMcpName(null);
    setMcpNameDraft('new-mcp-server');
    setMcpCommandDraft('');
    setMcpArgsDraft('');
    setMcpEnvDraft('{}');
    setMcpTimeoutDraft('60000');
  };

  const handleDeleteWorkflow = () => {
    if (!workflowFilename) {
      messageApi.warning('Select a workflow file before deleting.');
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
      messageApi.warning('Select an LLM instance before deleting.');
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
      messageApi.warning('Select an MCP server before deleting.');
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
        'Delete embeddings configuration, including the stored API key?',
      )
    ) {
      return;
    }

    deleteEmbeddingsMutation.mutate();
  };

  const handleDeleteWebSearch = () => {
    if (
      !window.confirm(
        'Delete web search configuration, including the stored API key?',
      )
    ) {
      return;
    }

    deleteWebSearchMutation.mutate();
  };

  const handleDeleteSkillsMp = () => {
    if (
      !window.confirm(
        'Delete SkillsMP configuration, including the stored API key?',
      )
    ) {
      return;
    }

    deleteSkillsMpMutation.mutate();
  };

  const handleGenerateSecp256k1 = () => {
    if (
      !window.confirm(
        'Generate a new secp256k1 private key and save it locally? Existing material will be backed up automatically.',
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
    [configurationLlmInstancesQuery.data],
  );

  const llmProviderTypeOptions = useMemo(
    () =>
      (configurationLlmProvidersQuery.data ?? []).map((item) => ({
        label: `${item.displayName} · ${item.category}`,
        value: item.id,
      })),
    [configurationLlmProvidersQuery.data],
  );

  const workspaceTabs = [
    {
      key: 'system',
      label: 'System status',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          {configurationSourceQuery.isError ? (
            <Alert
              type="error"
              showIcon
              message="Configuration capability is unavailable."
              description={
                configurationSourceQuery.error instanceof Error
                  ? configurationSourceQuery.error.message
                  : 'Unable to load runtime configuration status.'
              }
            />
          ) : null}
          <ProDescriptions
            column={2}
            dataSource={{
              health: configurationHealthQuery.isSuccess ? 'ok' : 'unavailable',
              mode: configurationSourceQuery.data?.mode ?? 'unknown',
              root: configurationSourceQuery.data?.paths.root ?? '',
              workflowsHome:
                configurationSourceQuery.data?.paths.workflowsHome ?? '',
              workflowsRepo:
                configurationSourceQuery.data?.paths.workflowsRepo ?? '',
              secretsJson:
                configurationSourceQuery.data?.paths.secretsJson ?? '',
              configJson: configurationSourceQuery.data?.paths.configJson ?? '',
            }}
            columns={[
              { title: 'Health', dataIndex: 'health' },
              { title: 'Mode', dataIndex: 'mode' },
              { title: 'Root', dataIndex: 'root', copyable: true },
              {
                title: 'Workflows (home)',
                dataIndex: 'workflowsHome',
                copyable: true,
              },
              {
                title: 'Workflows (repo)',
                dataIndex: 'workflowsRepo',
                copyable: true,
              },
              { title: 'Secrets', dataIndex: 'secretsJson', copyable: true },
              { title: 'Config', dataIndex: 'configJson', copyable: true },
            ]}
          />
          <ProList<ConfigurationPathRecord>
            rowKey="id"
            search={false}
            split
            dataSource={configurationPathRecords}
            locale={{
              emptyText: (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="No configuration path status available."
                />
              ),
            }}
            metas={{
              title: {
                dataIndex: 'label',
                render: (_, record) => (
                  <Space wrap>
                    <Typography.Text strong>{record.label}</Typography.Text>
                    <Tag color={record.status.exists ? 'success' : 'default'}>
                      {record.status.exists ? 'exists' : 'missing'}
                    </Tag>
                    <Tag
                      color={record.status.writable ? 'processing' : 'default'}
                    >
                      {record.status.writable ? 'writable' : 'read-only'}
                    </Tag>
                  </Space>
                ),
              },
              description: {
                render: (_, record) => (
                  <Space
                    direction="vertical"
                    size={4}
                    style={{ width: '100%' }}
                  >
                    <Typography.Text code>{record.status.path}</Typography.Text>
                    {record.status.error ? (
                      <Typography.Text type="danger">
                        {record.status.error}
                      </Typography.Text>
                    ) : null}
                  </Space>
                ),
              },
              subTitle: {
                render: (_, record) => (
                  <Typography.Text type="secondary">
                    size {record.status.sizeBytes ?? 0} bytes
                  </Typography.Text>
                ),
              },
            }}
          />
        </Space>
      ),
    },
    {
      key: 'workflows',
      label: 'Workflow files',
      children: (
        <Row gutter={[16, 16]}>
          <Col xs={24} lg={10}>
            <ProCard
              title="Available files"
              ghost
              extra={
                <Button onClick={() => configurationWorkflowsQuery.refetch()}>
                  Refresh
                </Button>
              }
            >
              <ProList<ConfigurationWorkflowFile>
                rowKey={(record) => workflowKey(record)}
                search={false}
                split
                dataSource={configurationWorkflowsQuery.data ?? []}
                locale={{
                  emptyText: (
                    <Empty
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                      description="No workflow files discovered."
                    />
                  ),
                }}
                metas={{
                  title: {
                    dataIndex: 'filename',
                    render: (_, record) => (
                      <Space wrap>
                        <Button
                          type={
                            selectedWorkflow &&
                            workflowKey(record) ===
                              workflowKey(selectedWorkflow)
                              ? 'primary'
                              : 'link'
                          }
                          onClick={() =>
                            setSelectedWorkflowKey(workflowKey(record))
                          }
                        >
                          {record.filename}
                        </Button>
                        <Tag>{record.source}</Tag>
                      </Space>
                    ),
                  },
                  description: {
                    render: (_, record) => (
                      <Space
                        direction="vertical"
                        size={4}
                        style={{ width: '100%' }}
                      >
                        <Typography.Text type="secondary">
                          {formatDateTime(record.lastModified)}
                        </Typography.Text>
                        <Typography.Text code>{record.path}</Typography.Text>
                      </Space>
                    ),
                  },
                }}
              />
            </ProCard>
          </Col>
          <Col xs={24} lg={14}>
            <ProCard title="Editor" ghost>
              <Space direction="vertical" style={{ width: '100%' }} size={16}>
                <Space wrap style={{ width: '100%' }}>
                  <Input
                    style={{ minWidth: 240 }}
                    placeholder="workflow filename.yaml"
                    value={workflowFilename}
                    onChange={(event) =>
                      setWorkflowFilename(event.target.value)
                    }
                  />
                  <Select<WorkflowDraftSource>
                    style={{ width: 140 }}
                    value={workflowSource}
                    onChange={setWorkflowSource}
                    options={[
                      { label: 'home', value: 'home' },
                      { label: 'repo', value: 'repo' },
                    ]}
                  />
                  <Button onClick={handleNewWorkflowDraft}>New draft</Button>
                  <Button
                    onClick={() => workflowDetailQuery.refetch()}
                    disabled={!selectedWorkflow}
                  >
                    Reload
                  </Button>
                  <Button
                    type="primary"
                    loading={saveWorkflowMutation.isPending}
                    onClick={() => saveWorkflowMutation.mutate()}
                    disabled={
                      !workflowFilename.trim() || !workflowContent.trim()
                    }
                  >
                    Save file
                  </Button>
                  <Button
                    danger
                    loading={deleteWorkflowMutation.isPending}
                    onClick={handleDeleteWorkflow}
                    disabled={!workflowFilename.trim()}
                  >
                    Delete file
                  </Button>
                </Space>
                <Input.TextArea
                  rows={18}
                  value={workflowContent}
                  onChange={(event) => setWorkflowContent(event.target.value)}
                  placeholder="name: workflow_name"
                />
                <Typography.Text type="secondary">
                  Files are loaded from both home and repo roots. Save writes to
                  the selected target source.
                </Typography.Text>
              </Space>
            </ProCard>
          </Col>
        </Row>
      ),
    },
    {
      key: 'embeddings',
      label: 'Embeddings',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <Alert
            type="info"
            showIcon
            message="Global embeddings fallback"
            description="Manage the local fallback embedding provider and its API key."
          />
          <Space wrap>
            <Tag
              color={
                configurationEmbeddingsQuery.data?.configured
                  ? 'success'
                  : 'default'
              }
            >
              {configurationEmbeddingsQuery.data?.configured
                ? 'API key configured'
                : 'API key missing'}
            </Tag>
            <Tag>{embeddingsEnabledDraft ? 'enabled' : 'disabled'}</Tag>
            {configurationEmbeddingsQuery.data?.masked ? (
              <Tag>{configurationEmbeddingsQuery.data.masked}</Tag>
            ) : null}
            <Button onClick={() => configurationEmbeddingsQuery.refetch()}>
              Reload
            </Button>
          </Space>
          <Space wrap style={{ width: '100%' }}>
            <Select
              style={{ width: 160 }}
              value={embeddingsEnabledDraft ? 'enabled' : 'disabled'}
              onChange={(value) =>
                setEmbeddingsEnabledDraft(value === 'enabled')
              }
              options={[
                { label: 'Enabled', value: 'enabled' },
                { label: 'Disabled', value: 'disabled' },
              ]}
            />
            <Input
              style={{ minWidth: 220 }}
              placeholder="provider type"
              value={embeddingsProviderTypeDraft}
              onChange={(event) =>
                setEmbeddingsProviderTypeDraft(event.target.value)
              }
            />
            <Input
              style={{ minWidth: 320 }}
              placeholder="endpoint"
              value={embeddingsEndpointDraft}
              onChange={(event) =>
                setEmbeddingsEndpointDraft(event.target.value)
              }
            />
            <Input
              style={{ minWidth: 220 }}
              placeholder="model"
              value={embeddingsModelDraft}
              onChange={(event) => setEmbeddingsModelDraft(event.target.value)}
            />
          </Space>
          <Space wrap style={{ width: '100%' }}>
            <Input.Password
              style={{ minWidth: 360 }}
              placeholder="Embeddings API key"
              value={embeddingsApiKeyDraft}
              onChange={(event) => setEmbeddingsApiKeyDraft(event.target.value)}
            />
            <Button
              loading={revealEmbeddingsApiKeyMutation.isPending}
              disabled={!configurationEmbeddingsQuery.data?.configured}
              onClick={() => revealEmbeddingsApiKeyMutation.mutate()}
            >
              Reveal current
            </Button>
            <Button
              type="primary"
              loading={saveEmbeddingsMutation.isPending}
              disabled={
                embeddingsEnabledDraft &&
                (!embeddingsEndpointDraft.trim() ||
                  !embeddingsModelDraft.trim() ||
                  (!embeddingsApiKeyDraft.trim() &&
                    !configurationEmbeddingsQuery.data?.configured))
              }
              onClick={() => saveEmbeddingsMutation.mutate()}
            >
              Save embeddings
            </Button>
            <Button
              danger
              loading={deleteEmbeddingsMutation.isPending}
              disabled={
                !configurationEmbeddingsQuery.data?.configured &&
                !configurationEmbeddingsQuery.data?.providerType &&
                !configurationEmbeddingsQuery.data?.endpoint &&
                !configurationEmbeddingsQuery.data?.model
              }
              onClick={handleDeleteEmbeddings}
            >
              Delete embeddings
            </Button>
          </Space>
          <Typography.Text type="secondary">
            Stored keys live under{' '}
            <Typography.Text code>LLMProviders:Embeddings:*</Typography.Text>.
          </Typography.Text>
        </Space>
      ),
    },
    {
      key: 'websearch',
      label: 'Web Search',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <Alert
            type="info"
            showIcon
            message="Local web search tool configuration"
            description="Manage the web search provider and keep its API key in secrets."
          />
          <Space wrap>
            <Tag
              color={
                configurationWebSearchQuery.data?.configured
                  ? 'success'
                  : 'default'
              }
            >
              {configurationWebSearchQuery.data?.configured
                ? 'API key configured'
                : 'API key missing'}
            </Tag>
            <Tag>{webSearchEnabledDraft ? 'enabled' : 'disabled'}</Tag>
            {configurationWebSearchQuery.data?.masked ? (
              <Tag>{configurationWebSearchQuery.data.masked}</Tag>
            ) : null}
            <Button onClick={() => configurationWebSearchQuery.refetch()}>
              Reload
            </Button>
          </Space>
          <Space wrap style={{ width: '100%' }}>
            <Select
              style={{ width: 160 }}
              value={webSearchEnabledDraft ? 'enabled' : 'disabled'}
              onChange={(value) =>
                setWebSearchEnabledDraft(value === 'enabled')
              }
              options={[
                { label: 'Enabled', value: 'enabled' },
                { label: 'Disabled', value: 'disabled' },
              ]}
            />
            <Input
              style={{ minWidth: 220 }}
              placeholder="provider"
              value={webSearchProviderDraft}
              onChange={(event) =>
                setWebSearchProviderDraft(event.target.value)
              }
            />
            <Input
              style={{ minWidth: 320 }}
              placeholder="endpoint"
              value={webSearchEndpointDraft}
              onChange={(event) =>
                setWebSearchEndpointDraft(event.target.value)
              }
            />
            <Input
              style={{ width: 180 }}
              placeholder="timeout ms"
              value={webSearchTimeoutDraft}
              onChange={(event) => setWebSearchTimeoutDraft(event.target.value)}
            />
            <Input
              style={{ width: 180 }}
              placeholder="search depth"
              value={webSearchDepthDraft}
              onChange={(event) => setWebSearchDepthDraft(event.target.value)}
            />
          </Space>
          <Space wrap style={{ width: '100%' }}>
            <Input.Password
              style={{ minWidth: 360 }}
              placeholder="Web search API key"
              value={webSearchApiKeyDraft}
              onChange={(event) => setWebSearchApiKeyDraft(event.target.value)}
            />
            <Button
              loading={revealWebSearchApiKeyMutation.isPending}
              disabled={!configurationWebSearchQuery.data?.configured}
              onClick={() => revealWebSearchApiKeyMutation.mutate()}
            >
              Reveal current
            </Button>
            <Button
              type="primary"
              loading={saveWebSearchMutation.isPending}
              disabled={
                webSearchEnabledDraft &&
                (!webSearchProviderDraft.trim() ||
                  (!webSearchApiKeyDraft.trim() &&
                    !configurationWebSearchQuery.data?.configured))
              }
              onClick={() => saveWebSearchMutation.mutate()}
            >
              Save web search
            </Button>
            <Button
              danger
              loading={deleteWebSearchMutation.isPending}
              disabled={
                !configurationWebSearchQuery.data?.configured &&
                !configurationWebSearchQuery.data?.provider &&
                !configurationWebSearchQuery.data?.endpoint &&
                configurationWebSearchQuery.data?.timeoutMs == null &&
                !configurationWebSearchQuery.data?.searchDepth
              }
              onClick={handleDeleteWebSearch}
            >
              Delete web search
            </Button>
          </Space>
          <Typography.Text type="secondary">
            Non-secret fields are written to{' '}
            <Typography.Text code>config.json</Typography.Text>. The key stays
            in <Typography.Text code>secrets.json</Typography.Text>.
          </Typography.Text>
        </Space>
      ),
    },
    {
      key: 'skillsmp',
      label: 'SkillsMP',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <Alert
            type="info"
            showIcon
            message="Skills marketplace credentials"
            description="This mirrors the old SkillsMP panel and manages the API key plus optional base URL."
          />
          <Space wrap>
            <Tag
              color={
                configurationSkillsMpQuery.data?.configured
                  ? 'success'
                  : 'default'
              }
            >
              {configurationSkillsMpQuery.data?.configured
                ? 'API key configured'
                : 'API key missing'}
            </Tag>
            {configurationSkillsMpQuery.data?.masked ? (
              <Tag>{configurationSkillsMpQuery.data.masked}</Tag>
            ) : null}
            <Button onClick={() => configurationSkillsMpQuery.refetch()}>
              Reload
            </Button>
          </Space>
          <Space wrap style={{ width: '100%' }}>
            <Input
              style={{ minWidth: 360 }}
              placeholder="https://skillsmp.com"
              value={skillsMpBaseUrlDraft}
              onChange={(event) => setSkillsMpBaseUrlDraft(event.target.value)}
            />
            <Input.Password
              style={{ minWidth: 360 }}
              placeholder="SkillsMP API key"
              value={skillsMpApiKeyDraft}
              onChange={(event) => setSkillsMpApiKeyDraft(event.target.value)}
            />
          </Space>
          <Space wrap>
            <Button
              loading={revealSkillsMpApiKeyMutation.isPending}
              disabled={!configurationSkillsMpQuery.data?.configured}
              onClick={() => revealSkillsMpApiKeyMutation.mutate()}
            >
              Reveal current
            </Button>
            <Button
              type="primary"
              loading={saveSkillsMpMutation.isPending}
              disabled={
                !skillsMpApiKeyDraft.trim() &&
                !configurationSkillsMpQuery.data?.configured
              }
              onClick={() => saveSkillsMpMutation.mutate()}
            >
              Save SkillsMP
            </Button>
            <Button
              danger
              loading={deleteSkillsMpMutation.isPending}
              disabled={
                !configurationSkillsMpQuery.data?.configured &&
                !configurationSkillsMpQuery.data?.baseUrl
              }
              onClick={handleDeleteSkillsMp}
            >
              Delete SkillsMP
            </Button>
          </Space>
          <Typography.Text type="secondary">
            API key path:{' '}
            <Typography.Text code>
              {configurationSkillsMpQuery.data?.keyPath ?? 'SkillsMP:ApiKey'}
            </Typography.Text>
          </Typography.Text>
        </Space>
      ),
    },
    {
      key: 'signer-key',
      label: 'Signer key',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <Alert
            type="info"
            showIcon
            message="Local secp256k1 signer key"
            description="The public key is safe to copy. Generating a new private key automatically backs up the previous key material in secrets."
          />
          <Space wrap>
            <Tag
              color={
                configurationSecp256k1Query.data?.configured
                  ? 'success'
                  : 'default'
              }
            >
              {configurationSecp256k1Query.data?.configured
                ? 'configured'
                : 'not configured'}
            </Tag>
            <Tag>
              backups:{' '}
              {configurationSecp256k1Query.data?.privateKey.backupCount ?? 0}
            </Tag>
            <Button onClick={() => configurationSecp256k1Query.refetch()}>
              Reload
            </Button>
            <Button
              type="primary"
              loading={generateSecp256k1Mutation.isPending}
              onClick={handleGenerateSecp256k1}
            >
              Generate & save
            </Button>
          </Space>
          <Space direction="vertical" size={8} style={{ width: '100%' }}>
            <Typography.Text strong>Public key</Typography.Text>
            <Input.TextArea
              rows={4}
              readOnly
              value={configurationSecp256k1Query.data?.publicKey.hex ?? ''}
              placeholder="(not configured yet)"
            />
            {configurationSecp256k1Query.data?.publicKey.hex ? (
              <Typography.Text
                copyable={{
                  text: configurationSecp256k1Query.data.publicKey.hex,
                }}
              >
                Copy public key
              </Typography.Text>
            ) : null}
          </Space>
          <Space direction="vertical" size={8} style={{ width: '100%' }}>
            <Typography.Text strong>Private key status</Typography.Text>
            <Input
              readOnly
              value={configurationSecp256k1Query.data?.privateKey.masked ?? ''}
              placeholder="(not configured yet)"
            />
            <Typography.Text type="secondary">
              Stored at{' '}
              <Typography.Text code>
                {configurationSecp256k1Query.data?.privateKey.keyPath ??
                  'Crypto:EcdsaSecp256k1:PrivateKeyHex'}
              </Typography.Text>
            </Typography.Text>
          </Space>
        </Space>
      ),
    },
    {
      key: 'connectors',
      label: 'Connectors',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <Alert
            type="info"
            showIcon
            message="Raw connector definitions"
            description="Connector configuration is edited as raw JSON in this phase to avoid losing disabled entries through a filtered structured view."
          />
          <Space wrap>
            <Tag
              color={
                configurationConnectorsRawQuery.data?.exists
                  ? 'success'
                  : 'default'
              }
            >
              {configurationConnectorsRawQuery.data?.exists
                ? 'connectors.json present'
                : 'connectors.json missing'}
            </Tag>
            <Tag>
              entries: {configurationConnectorsRawQuery.data?.count ?? 0}
            </Tag>
            <Button onClick={() => configurationConnectorsRawQuery.refetch()}>
              Reload
            </Button>
            <Button
              loading={validateConnectorsRawMutation.isPending}
              onClick={() => validateConnectorsRawMutation.mutate()}
            >
              Validate
            </Button>
            <Button
              type="primary"
              loading={saveConnectorsRawMutation.isPending}
              onClick={() => saveConnectorsRawMutation.mutate()}
            >
              Save connectors
            </Button>
          </Space>
          <Input.TextArea
            rows={18}
            value={connectorsJsonDraft}
            onChange={(event) => setConnectorsJsonDraft(event.target.value)}
            placeholder={'{\n  "connectors": []\n}'}
          />
          {configurationConnectorsRawQuery.data?.path ? (
            <Typography.Text type="secondary">
              Path:{' '}
              <Typography.Text code>
                {configurationConnectorsRawQuery.data.path}
              </Typography.Text>
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      key: 'llm',
      label: 'LLM providers',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <Space wrap>
            <Tag color="processing">
              Default provider: {configurationLlmDefaultQuery.data ?? 'default'}
            </Tag>
            <Select
              style={{ minWidth: 260 }}
              value={pendingDefaultProvider || undefined}
              onChange={setPendingDefaultProvider}
              placeholder="Choose a configured provider"
              options={llmDefaultOptions}
            />
            <Button
              type="primary"
              loading={setLlmDefaultMutation.isPending}
              disabled={!pendingDefaultProvider}
              onClick={() => setLlmDefaultMutation.mutate()}
            >
              Set default
            </Button>
          </Space>
          <ProCard title="Instance maintenance" ghost>
            <Space direction="vertical" style={{ width: '100%' }} size={16}>
              <Space wrap style={{ width: '100%' }}>
                <Input
                  style={{ minWidth: 220 }}
                  placeholder="provider name"
                  value={llmInstanceNameDraft}
                  onChange={(event) =>
                    setLlmInstanceNameDraft(event.target.value)
                  }
                />
                <Select
                  style={{ minWidth: 240 }}
                  value={llmProviderTypeDraft || undefined}
                  onChange={setLlmProviderTypeDraft}
                  placeholder="Provider type"
                  options={llmProviderTypeOptions}
                />
                <Input
                  style={{ minWidth: 220 }}
                  placeholder="model"
                  value={llmModelDraft}
                  onChange={(event) => setLlmModelDraft(event.target.value)}
                />
                <Input
                  style={{ minWidth: 280 }}
                  placeholder="endpoint (optional)"
                  value={llmEndpointDraft}
                  onChange={(event) => setLlmEndpointDraft(event.target.value)}
                />
              </Space>
              <Space wrap>
                <Button onClick={handleNewLlmInstanceDraft}>
                  New instance
                </Button>
                <Button
                  type="primary"
                  loading={saveLlmInstanceMutation.isPending}
                  disabled={
                    !llmInstanceNameDraft.trim() ||
                    !llmProviderTypeDraft.trim() ||
                    !llmModelDraft.trim()
                  }
                  onClick={() => saveLlmInstanceMutation.mutate()}
                >
                  Save instance
                </Button>
                <Button
                  danger
                  loading={deleteLlmInstanceMutation.isPending}
                  disabled={
                    !llmInstanceNameDraft.trim() || isNewLlmInstanceDraft
                  }
                  onClick={handleDeleteLlmInstance}
                >
                  Delete instance
                </Button>
              </Space>
              <Space wrap style={{ width: '100%' }}>
                <Input.Password
                  style={{ minWidth: 320 }}
                  placeholder="API key"
                  value={llmApiKeyDraft}
                  onChange={(event) => setLlmApiKeyDraft(event.target.value)}
                />
                <Button
                  loading={revealLlmApiKeyMutation.isPending}
                  disabled={
                    !llmInstanceNameDraft.trim() || isNewLlmInstanceDraft
                  }
                  onClick={() => revealLlmApiKeyMutation.mutate()}
                >
                  Reveal current
                </Button>
                <Button
                  type="primary"
                  loading={setLlmApiKeyMutation.isPending}
                  disabled={
                    !llmInstanceNameDraft.trim() || !llmApiKeyDraft.trim()
                  }
                  onClick={() => setLlmApiKeyMutation.mutate()}
                >
                  Set API key
                </Button>
                <Button
                  danger
                  loading={deleteLlmApiKeyMutation.isPending}
                  disabled={
                    !llmInstanceNameDraft.trim() ||
                    !configurationLlmApiKeyQuery.data?.configured
                  }
                  onClick={() => deleteLlmApiKeyMutation.mutate()}
                >
                  Remove API key
                </Button>
              </Space>
              <Space wrap>
                <Tag
                  color={
                    configurationLlmApiKeyQuery.data?.configured
                      ? 'success'
                      : 'default'
                  }
                >
                  {configurationLlmApiKeyQuery.data?.configured
                    ? 'api key configured'
                    : 'api key missing'}
                </Tag>
                {configurationLlmApiKeyQuery.data?.masked ? (
                  <Tag>{configurationLlmApiKeyQuery.data.masked}</Tag>
                ) : null}
              </Space>
              <Space wrap>
                <Button
                  loading={probeLlmTestMutation.isPending}
                  disabled={
                    !llmProviderTypeDraft.trim() || !llmApiKeyDraft.trim()
                  }
                  onClick={() => probeLlmTestMutation.mutate()}
                >
                  Test connection
                </Button>
                <Button
                  loading={probeLlmModelsMutation.isPending}
                  disabled={
                    !llmProviderTypeDraft.trim() || !llmApiKeyDraft.trim()
                  }
                  onClick={() => probeLlmModelsMutation.mutate()}
                >
                  Fetch models
                </Button>
              </Space>
              {llmProbeResult ? (
                <Alert
                  type={llmProbeResult.ok ? 'success' : 'warning'}
                  showIcon
                  message="Connectivity probe"
                  description={
                    <Space direction="vertical" size={4}>
                      <Typography.Text>
                        {formatProbeSummary(llmProbeResult)}
                      </Typography.Text>
                      {llmProbeResult.sampleModels?.length ? (
                        <Typography.Text code>
                          {llmProbeResult.sampleModels.join(', ')}
                        </Typography.Text>
                      ) : null}
                    </Space>
                  }
                />
              ) : null}
              {llmModelsResult ? (
                <Alert
                  type={llmModelsResult.ok ? 'success' : 'warning'}
                  showIcon
                  message="Models probe"
                  description={
                    <Space direction="vertical" size={4}>
                      <Typography.Text>
                        {formatProbeSummary(llmModelsResult)}
                      </Typography.Text>
                      {llmModelsResult.models?.length ? (
                        <Typography.Text code>
                          {llmModelsResult.models.slice(0, 10).join(', ')}
                        </Typography.Text>
                      ) : null}
                    </Space>
                  }
                />
              ) : null}
            </Space>
          </ProCard>
          <ProCard title="Configured instances" ghost>
            <ProList
              rowKey="name"
              search={false}
              split
              dataSource={configurationLlmInstancesQuery.data ?? []}
              locale={{
                emptyText: (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="No configured instances."
                  />
                ),
              }}
              metas={{
                title: {
                  dataIndex: 'name',
                  render: (_, record) => (
                    <Space wrap>
                      <Button
                        type={
                          selectedLlmInstance?.name === record.name
                            ? 'primary'
                            : 'link'
                        }
                        onClick={() => {
                          setIsNewLlmInstanceDraft(false);
                          setSelectedLlmInstanceName(record.name);
                        }}
                      >
                        {record.name}
                      </Button>
                      <Tag>{record.providerDisplayName}</Tag>
                    </Space>
                  ),
                },
                description: {
                  render: (_, record) => (
                    <Space
                      direction="vertical"
                      size={4}
                      style={{ width: '100%' }}
                    >
                      <Typography.Text>Model: {record.model}</Typography.Text>
                      <Typography.Text code>{record.endpoint}</Typography.Text>
                    </Space>
                  ),
                },
              }}
            />
          </ProCard>
          <ProCard title="Provider types" ghost>
            <ProList
              rowKey="id"
              search={false}
              split
              dataSource={configurationLlmProvidersQuery.data ?? []}
              locale={{
                emptyText: (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="No provider types available."
                  />
                ),
              }}
              metas={{
                title: {
                  dataIndex: 'displayName',
                  render: (_, record) => (
                    <Space wrap>
                      <Typography.Text strong>
                        {record.displayName}
                      </Typography.Text>
                      <Tag>{record.category}</Tag>
                      {record.recommended ? (
                        <Tag color="success">recommended</Tag>
                      ) : null}
                    </Space>
                  ),
                },
                description: {
                  render: (_, record) => (
                    <Typography.Text>{record.description}</Typography.Text>
                  ),
                },
                subTitle: {
                  render: (_, record) => (
                    <Typography.Text type="secondary">
                      configured instances: {record.configuredInstancesCount}
                    </Typography.Text>
                  ),
                },
              }}
            />
          </ProCard>
        </Space>
      ),
    },
    {
      key: 'mcp',
      label: 'MCP',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <Alert
            type="info"
            showIcon
            message="Structured MCP server workspace"
            description="MCP servers can now be managed structurally. The raw JSON editor is still available below as an advanced fallback."
          />
          <Row gutter={[16, 16]}>
            <Col xs={24} lg={10}>
              <ProCard
                title="Registered servers"
                ghost
                extra={
                  <Button
                    onClick={() => configurationMcpServersQuery.refetch()}
                  >
                    Refresh
                  </Button>
                }
              >
                <ProList<ConfigurationMcpServer>
                  rowKey="name"
                  search={false}
                  split
                  dataSource={configurationMcpServersQuery.data ?? []}
                  locale={{
                    emptyText: (
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description="No MCP servers configured."
                      />
                    ),
                  }}
                  metas={{
                    title: {
                      dataIndex: 'name',
                      render: (_, record) => (
                        <Space wrap>
                          <Button
                            type={
                              selectedMcpServer?.name === record.name
                                ? 'primary'
                                : 'link'
                            }
                            onClick={() => {
                              setIsNewMcpDraft(false);
                              setSelectedMcpName(record.name);
                            }}
                          >
                            {record.name}
                          </Button>
                          <Tag>{record.timeoutMs} ms</Tag>
                        </Space>
                      ),
                    },
                    description: {
                      render: (_, record) => (
                        <Space
                          direction="vertical"
                          size={4}
                          style={{ width: '100%' }}
                        >
                          <Typography.Text code>
                            {record.command}
                          </Typography.Text>
                          <Typography.Text type="secondary">
                            args: {record.args.length} · env:{' '}
                            {Object.keys(record.env).length}
                          </Typography.Text>
                        </Space>
                      ),
                    },
                  }}
                />
              </ProCard>
            </Col>
            <Col xs={24} lg={14}>
              <ProCard title="Server editor" ghost>
                <Space direction="vertical" style={{ width: '100%' }} size={16}>
                  <Space wrap style={{ width: '100%' }}>
                    <Input
                      style={{ minWidth: 220 }}
                      placeholder="server name"
                      value={mcpNameDraft}
                      onChange={(event) => setMcpNameDraft(event.target.value)}
                    />
                    <Input
                      style={{ minWidth: 260 }}
                      placeholder="command"
                      value={mcpCommandDraft}
                      onChange={(event) =>
                        setMcpCommandDraft(event.target.value)
                      }
                    />
                    <Input
                      style={{ width: 160 }}
                      type="number"
                      min={1}
                      placeholder="timeout ms"
                      value={mcpTimeoutDraft}
                      onChange={(event) =>
                        setMcpTimeoutDraft(event.target.value)
                      }
                    />
                    <Button onClick={handleNewMcpServerDraft}>
                      New server
                    </Button>
                    <Button
                      type="primary"
                      loading={saveMcpServerMutation.isPending}
                      disabled={!mcpNameDraft.trim() || !mcpCommandDraft.trim()}
                      onClick={() => saveMcpServerMutation.mutate()}
                    >
                      Save server
                    </Button>
                    <Button
                      danger
                      loading={deleteMcpServerMutation.isPending}
                      disabled={!mcpNameDraft.trim() || isNewMcpDraft}
                      onClick={handleDeleteMcpServer}
                    >
                      Delete server
                    </Button>
                  </Space>
                  <Space
                    direction="vertical"
                    size={8}
                    style={{ width: '100%' }}
                  >
                    <Typography.Text strong>Args</Typography.Text>
                    <Input.TextArea
                      rows={6}
                      value={mcpArgsDraft}
                      onChange={(event) => setMcpArgsDraft(event.target.value)}
                      placeholder={'node\nserver.js\n--transport\nstdio'}
                    />
                    <Typography.Text type="secondary">
                      One argument per line.
                    </Typography.Text>
                  </Space>
                  <Space
                    direction="vertical"
                    size={8}
                    style={{ width: '100%' }}
                  >
                    <Typography.Text strong>Env</Typography.Text>
                    <Input.TextArea
                      rows={8}
                      value={mcpEnvDraft}
                      onChange={(event) => setMcpEnvDraft(event.target.value)}
                      placeholder={'{\n  "API_KEY": "value"\n}'}
                    />
                    <Typography.Text type="secondary">
                      Provide a JSON object with string values.
                    </Typography.Text>
                  </Space>
                </Space>
              </ProCard>
            </Col>
          </Row>
          <ProCard title="Advanced raw JSON" ghost>
            <Space direction="vertical" style={{ width: '100%' }} size={16}>
              <Space wrap>
                <Tag
                  color={
                    configurationMcpRawQuery.data?.exists
                      ? 'success'
                      : 'default'
                  }
                >
                  {configurationMcpRawQuery.data?.exists
                    ? 'mcp.json present'
                    : 'mcp.json missing'}
                </Tag>
                <Tag>servers: {configurationMcpRawQuery.data?.count ?? 0}</Tag>
                <Button onClick={() => configurationMcpRawQuery.refetch()}>
                  Reload raw
                </Button>
                <Button
                  loading={validateMcpRawMutation.isPending}
                  onClick={() => validateMcpRawMutation.mutate()}
                >
                  Validate raw
                </Button>
                <Button
                  type="primary"
                  loading={saveMcpRawMutation.isPending}
                  onClick={() => saveMcpRawMutation.mutate()}
                >
                  Save raw
                </Button>
              </Space>
              <Input.TextArea
                rows={14}
                value={mcpJsonDraft}
                onChange={(event) => setMcpJsonDraft(event.target.value)}
                placeholder={'{\n  "mcpServers": {}\n}'}
              />
              {configurationMcpRawQuery.data?.path ? (
                <Typography.Text type="secondary">
                  Path:{' '}
                  <Typography.Text code>
                    {configurationMcpRawQuery.data.path}
                  </Typography.Text>
                </Typography.Text>
              ) : null}
            </Space>
          </ProCard>
        </Space>
      ),
    },
    {
      key: 'secrets',
      label: 'Secrets',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <Alert
            type="warning"
            showIcon
            message="Sensitive local data"
            description="This editor writes raw secrets JSON on the current machine. Treat it as a local admin surface."
          />
          <ProCard title="Key-based operations" ghost>
            <Space direction="vertical" style={{ width: '100%' }} size={16}>
              <Space wrap style={{ width: '100%' }}>
                <Input
                  style={{ minWidth: 320 }}
                  placeholder="secret key"
                  value={secretKeyDraft}
                  onChange={(event) => setSecretKeyDraft(event.target.value)}
                />
                <Input.Password
                  style={{ minWidth: 320 }}
                  placeholder="secret value"
                  value={secretValueDraft}
                  onChange={(event) => setSecretValueDraft(event.target.value)}
                />
                <Button
                  type="primary"
                  loading={setSecretMutation.isPending}
                  disabled={!secretKeyDraft.trim() || !secretValueDraft}
                  onClick={() => setSecretMutation.mutate()}
                >
                  Set secret
                </Button>
                <Button
                  danger
                  loading={removeSecretMutation.isPending}
                  disabled={!secretKeyDraft.trim()}
                  onClick={() => removeSecretMutation.mutate()}
                >
                  Remove secret
                </Button>
              </Space>
              <Typography.Text type="secondary">
                Use key-based operations for small targeted changes. Raw JSON
                remains available below for bulk edits.
              </Typography.Text>
            </Space>
          </ProCard>
          <Space wrap>
            <Button onClick={() => configurationSecretsRawQuery.refetch()}>
              Reload
            </Button>
            <Button
              type="primary"
              loading={saveSecretsRawMutation.isPending}
              onClick={() => saveSecretsRawMutation.mutate()}
            >
              Save secrets
            </Button>
          </Space>
          <Input.TextArea
            rows={18}
            value={secretsJsonDraft}
            onChange={(event) => setSecretsJsonDraft(event.target.value)}
            placeholder={'{\n  "LLMProviders": {}\n}'}
          />
        </Space>
      ),
    },
    {
      key: 'config',
      label: 'Raw config',
      children: (
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <Typography.Text type="secondary">
            This editor writes the local runtime config JSON directly.
          </Typography.Text>
          <Space wrap>
            <Button onClick={() => configurationConfigRawQuery.refetch()}>
              Reload
            </Button>
            <Button
              type="primary"
              loading={saveConfigRawMutation.isPending}
              onClick={() => saveConfigRawMutation.mutate()}
            >
              Save config
            </Button>
          </Space>
          <Input.TextArea
            rows={18}
            value={configJsonDraft}
            onChange={(event) => setConfigJsonDraft(event.target.value)}
            placeholder={'{\n  "Workflow": {}\n}'}
          />
        </Space>
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
  const workspaceTabPlacement = screens.xl ? 'start' : 'top';
  const settingsSectionPlacement = screens.md ? 'top' : 'top';

  const runtimeWorkspaceContent = (
    <ProCard title="Runtime configuration workspace" ghost>
      <Tabs
        items={workspaceTabs}
        size="large"
        tabPlacement={workspaceTabPlacement}
        tabBarGutter={8}
      />
    </ProCard>
  );

  const profileContent = (
    <Row gutter={[16, 16]} align="stretch">
      <Col xs={24} xxl={10} style={stretchColumnStyle}>
        <ProCard title="Account profile" ghost style={fillCardStyle}>
          {authSession && profileIdentity ? (
            <Space direction="vertical" style={{ width: '100%' }} size={16}>
              <Space align="center" size={16}>
                <Avatar size={56} src={authSession.user.picture}>
                  {profileIdentity.displayName.slice(0, 1).toUpperCase()}
                </Avatar>
                <Space direction="vertical" size={2}>
                  <Typography.Title level={4} style={{ margin: 0 }}>
                    {profileIdentity.displayName}
                  </Typography.Title>
                  <Typography.Text type="secondary">
                    {profileIdentity.email || profileIdentity.subject}
                  </Typography.Text>
                </Space>
              </Space>
              <ProDescriptions<SettingsProfileIdentityRecord>
                column={1}
                dataSource={profileIdentity}
                columns={settingsProfileIdentityColumns}
              />
            </Space>
          ) : (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="No NyxID session is available."
            />
          )}
        </ProCard>
      </Col>
      <Col xs={24} xxl={14} style={stretchColumnStyle}>
        <ProCard title="Access profile" ghost style={fillCardStyle}>
          {profileAccess ? (
            <ProDescriptions<SettingsProfileAccessRecord>
              column={1}
              dataSource={profileAccess}
              columns={settingsProfileAccessColumns}
            />
          ) : (
            <Alert
              type="warning"
              showIcon
              message="Profile access data is unavailable."
              description="Sign in with NyxID to inspect roles, groups, permissions, and token metadata."
            />
          )}
        </ProCard>
      </Col>
    </Row>
  );

  const consolePreferencesContent = (
    <Row gutter={[16, 16]} align="stretch">
      <Col xs={24} xxl={15} style={stretchColumnStyle}>
        <ProCard title="Console preferences" ghost style={fillCardStyle}>
          <ProForm<ConsolePreferences>
            formRef={formRef}
            layout="vertical"
            initialValues={preferences}
            onFinish={handleSavePreferences}
            submitter={{
              render: (props) => (
                <Space wrap>
                  <Button type="primary" onClick={() => props.form?.submit?.()}>
                    Save preferences
                  </Button>
                  <Button onClick={handleResetPreferences}>
                    Reset defaults
                  </Button>
                  <Button
                    onClick={() =>
                      history.push(
                        `/runs?workflow=${encodeURIComponent(
                          formRef.current?.getFieldValue('preferredWorkflow') ??
                            preferences.preferredWorkflow,
                        )}`,
                      )
                    }
                  >
                    Open preferred workflow
                  </Button>
                </Space>
              ),
            }}
          >
            <Space direction="vertical" style={{ width: '100%' }} size={16}>
              <ProCard title="Studio appearance" ghost>
                <Row gutter={[16, 0]}>
                  <Col xs={24} md={12}>
                    <ProFormSelect<StudioAppearanceTheme>
                      name="studioAppearanceTheme"
                      label="Studio accent"
                      options={studioAppearanceOptions}
                      rules={[
                        {
                          required: true,
                          message: 'Studio accent is required.',
                        },
                      ]}
                    />
                  </Col>
                  <Col xs={24} md={12}>
                    <ProFormSelect<StudioColorMode>
                      name="studioColorMode"
                      label="Studio color mode"
                      options={studioColorModeOptions}
                      rules={[
                        {
                          required: true,
                          message: 'Studio color mode is required.',
                        },
                      ]}
                    />
                  </Col>
                </Row>
                <Typography.Text type="secondary">
                  Studio appearance is now a console-level preference instead of
                  a Studio runtime setting.
                </Typography.Text>
              </ProCard>
              <ProCard title="Workflow defaults" ghost>
                <Row gutter={[16, 0]}>
                  <Col xs={24}>
                    <ProFormSelect
                      name="preferredWorkflow"
                      label="Preferred workflow"
                      options={workflowOptions}
                      placeholder="Select a default workflow"
                      rules={[
                        {
                          required: true,
                          message: 'Preferred workflow is required.',
                        },
                      ]}
                      fieldProps={{
                        showSearch: true,
                        optionFilterProp: 'label',
                        notFoundContent: workflowCatalogQuery.isLoading ? (
                          <Typography.Text type="secondary">
                            Loading workflows...
                          </Typography.Text>
                        ) : (
                          <Empty
                            image={Empty.PRESENTED_IMAGE_SIMPLE}
                            description="No workflows available."
                          />
                        ),
                      }}
                    />
                  </Col>
                </Row>
              </ProCard>
              <ProCard title="Observability URLs" ghost>
                <Row gutter={[16, 0]}>
                  <Col xs={24} md={12}>
                    <ProFormText
                      name="grafanaBaseUrl"
                      label="Grafana base URL"
                      placeholder="https://grafana.example.com"
                    />
                  </Col>
                  <Col xs={24} md={12}>
                    <ProFormText
                      name="jaegerBaseUrl"
                      label="Jaeger base URL"
                      placeholder="https://jaeger.example.com"
                    />
                  </Col>
                  <Col xs={24}>
                    <ProFormText
                      name="lokiBaseUrl"
                      label="Loki base URL"
                      placeholder="https://loki.example.com"
                    />
                  </Col>
                </Row>
              </ProCard>
              <ProCard title="Actor explorer defaults" ghost>
                <Row gutter={[16, 0]}>
                  <Col xs={24} md={12}>
                    <ProFormDigit
                      name="actorTimelineTake"
                      label="Actor timeline take"
                      min={10}
                      max={500}
                      fieldProps={{ precision: 0 }}
                      rules={[
                        {
                          required: true,
                          message: 'Timeline take is required.',
                        },
                      ]}
                    />
                  </Col>
                  <Col xs={24} md={12}>
                    <ProFormDigit
                      name="actorGraphDepth"
                      label="Actor graph depth"
                      min={1}
                      max={8}
                      fieldProps={{ precision: 0 }}
                      rules={[
                        { required: true, message: 'Graph depth is required.' },
                      ]}
                    />
                  </Col>
                  <Col xs={24} md={12}>
                    <ProFormDigit
                      name="actorGraphTake"
                      label="Actor graph take"
                      min={10}
                      max={500}
                      fieldProps={{ precision: 0 }}
                      rules={[
                        { required: true, message: 'Graph take is required.' },
                      ]}
                    />
                  </Col>
                  <Col xs={24} md={12}>
                    <ProFormSelect<ActorGraphDirection>
                      name="actorGraphDirection"
                      label="Actor graph direction"
                      options={[
                        { label: 'Both', value: 'Both' },
                        { label: 'Outbound', value: 'Outbound' },
                        { label: 'Inbound', value: 'Inbound' },
                      ]}
                      rules={[
                        {
                          required: true,
                          message: 'Graph direction is required.',
                        },
                      ]}
                    />
                  </Col>
                </Row>
              </ProCard>
            </Space>
          </ProForm>
        </ProCard>
      </Col>
      <Col xs={24} xxl={9} style={stretchColumnStyle}>
        <Space direction="vertical" style={{ width: '100%' }} size={16}>
          <ProCard title="Observability endpoints" ghost style={fillCardStyle}>
            <ProList<SettingsObservabilityItem>
              rowKey="id"
              search={false}
              split
              dataSource={observabilityTargets}
              locale={{
                emptyText: (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="No observability targets configured."
                  />
                ),
              }}
              metas={{
                title: {
                  dataIndex: 'label',
                  render: (_, record) => (
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong>{record.label}</Typography.Text>
                      <Tag
                        color={
                          record.status === 'configured' ? 'success' : 'default'
                        }
                      >
                        {record.status}
                      </Tag>
                    </Space>
                  ),
                },
                description: {
                  dataIndex: 'description',
                },
                subTitle: {
                  render: (_, record) =>
                    record.homeUrl ? (
                      <Tag>{record.homeUrl}</Tag>
                    ) : (
                      <Tag>No URL configured</Tag>
                    ),
                },
                actions: {
                  render: (_, record) => [
                    <Button
                      key={`${record.id}-open`}
                      type="link"
                      disabled={record.status !== 'configured'}
                      href={record.homeUrl || undefined}
                      target="_blank"
                      rel="noreferrer"
                    >
                      Open
                    </Button>,
                    <Button
                      key={`${record.id}-explore`}
                      type="link"
                      disabled={record.status !== 'configured'}
                      href={record.exploreUrl || undefined}
                      target="_blank"
                      rel="noreferrer"
                    >
                      Explore
                    </Button>,
                  ],
                },
              }}
            />
          </ProCard>
          <ProCard title="Usage notes" ghost style={fillCardStyle}>
            <Space direction="vertical" style={{ width: '100%' }} size={12}>
              {settingsUsageNotes.map((item) => (
                <Typography.Text key={item.id}>{item.text}</Typography.Text>
              ))}
            </Space>
          </ProCard>
        </Space>
      </Col>
    </Row>
  );

  const settingsSectionTabs = [
    {
      key: 'profile',
      label: 'Profile',
      children: profileContent,
    },
    {
      key: 'preferences',
      label: 'Console preferences',
      children: consolePreferencesContent,
    },
    ...(hasLocalRuntimeAccess
      ? [
          {
            key: 'runtime',
            label: 'Runtime configuration',
            children: runtimeWorkspaceContent,
          },
        ]
      : []),
  ];

  return (
    <PageContainer
      title="Settings"
      content="Manage account profile, console preferences, and, when available, local runtime configuration from a single workspace."
    >
      {messageContextHolder}
      <Space direction="vertical" style={{ width: '100%' }} size={16}>
        <ProCard
          title="Environment summary"
          {...moduleCardProps}
          style={fillCardStyle}
        >
          <ProDescriptions<SettingsSummaryRecord>
            column={summaryColumnCount}
            dataSource={settingsSummary}
            columns={settingsSummaryColumns}
          />
        </ProCard>
        <ProCard
          title="Settings workspace"
          {...moduleCardProps}
          style={fillCardStyle}
        >
          <Tabs
            items={settingsSectionTabs}
            size="large"
            tabPlacement={settingsSectionPlacement}
            tabBarGutter={12}
          />
        </ProCard>
      </Space>
    </PageContainer>
  );
};

export default SettingsPage;
