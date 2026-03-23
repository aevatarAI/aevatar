import { ProCard, ProDescriptions, ProList } from "@ant-design/pro-components";
import {
  Alert,
  Button,
  Col,
  Empty,
  Input,
  Row,
  Select,
  Space,
  Tag,
  Typography,
} from "antd";
import React from "react";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type {
  ConfigurationEmbeddingsStatus,
  ConfigurationLlmApiKeyStatus,
  ConfigurationLlmInstance,
  ConfigurationLlmProbeResult,
  ConfigurationLlmProviderType,
  ConfigurationMcpServer,
  ConfigurationRawDocument,
  ConfigurationSecp256k1Status,
  ConfigurationSkillsMpStatus,
  ConfigurationSourceStatus,
  ConfigurationValidationResult,
  ConfigurationWebSearchStatus,
  ConfigurationWorkflowFile,
} from "@/shared/models/platform/configuration";
import type {
  ConfigurationPathRecord,
  WorkflowDraftSource,
} from "./runtimeSettingsShared";
import { formatProbeSummary, workflowKey } from "./runtimeSettingsShared";

type ReloadHandler = () => void;
type ActionHandler = () => void;
type ChangeHandler<T> = (value: T) => void;

const fixedListCardStyle = {
  height: 520,
} as const;

const fixedListCardBodyStyle = {
  display: "flex",
  flexDirection: "column",
  minHeight: 0,
  overflow: "hidden",
} as const;

const fixedListViewportStyle = {
  flex: 1,
  minHeight: 0,
  maxHeight: "none",
  overflowX: "hidden",
  overflowY: "auto",
  paddingRight: 4,
} as const;

export type SystemStatusSectionProps = {
  configurationSourceError?: unknown;
  configurationSourceStatus?: ConfigurationSourceStatus;
  configurationSourceUnavailable: boolean;
  configurationHealthReady: boolean;
  configurationPathRecords: ConfigurationPathRecord[];
};

export const SystemStatusSection: React.FC<SystemStatusSectionProps> = ({
  configurationSourceError,
  configurationSourceStatus,
  configurationSourceUnavailable,
  configurationHealthReady,
  configurationPathRecords,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
    {configurationSourceUnavailable ? (
      <Alert
        type="error"
        showIcon
        message="Configuration capability is unavailable."
        description={
          configurationSourceError instanceof Error
            ? configurationSourceError.message
            : "Unable to load runtime configuration status."
        }
      />
    ) : null}
    <ProDescriptions
      column={2}
      dataSource={{
        health: configurationHealthReady ? "ok" : "unavailable",
        mode: configurationSourceStatus?.mode ?? "unknown",
        root: configurationSourceStatus?.paths.root ?? "",
        workflowsHome: configurationSourceStatus?.paths.workflowsHome ?? "",
        workflowsRepo: configurationSourceStatus?.paths.workflowsRepo ?? "",
        secretsJson: configurationSourceStatus?.paths.secretsJson ?? "",
        configJson: configurationSourceStatus?.paths.configJson ?? "",
      }}
      columns={[
        { title: "Health", dataIndex: "health" },
        { title: "Mode", dataIndex: "mode" },
        { title: "Root", dataIndex: "root", copyable: true },
        {
          title: "Workflows (home)",
          dataIndex: "workflowsHome",
          copyable: true,
        },
        {
          title: "Workflows (repo)",
          dataIndex: "workflowsRepo",
          copyable: true,
        },
        { title: "Secrets", dataIndex: "secretsJson", copyable: true },
        { title: "Config", dataIndex: "configJson", copyable: true },
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
          dataIndex: "label",
          render: (_, record) => (
            <Space wrap>
              <Typography.Text strong>{record.label}</Typography.Text>
              <Tag color={record.status.exists ? "success" : "default"}>
                {record.status.exists ? "exists" : "missing"}
              </Tag>
              <Tag color={record.status.writable ? "processing" : "default"}>
                {record.status.writable ? "writable" : "read-only"}
              </Tag>
            </Space>
          ),
        },
        description: {
          render: (_, record) => (
            <Space direction="vertical" size={4} style={{ width: "100%" }}>
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
);

export type WorkflowFilesSectionProps = {
  workflows: ConfigurationWorkflowFile[];
  selectedWorkflowId: string | null;
  workflowFilename: string;
  workflowSource: WorkflowDraftSource;
  workflowContent: string;
  canReloadSelectedWorkflow: boolean;
  isSavingWorkflow: boolean;
  isDeletingWorkflow: boolean;
  onRefresh: ReloadHandler;
  onSelectWorkflow: ChangeHandler<string>;
  onWorkflowFilenameChange: ChangeHandler<string>;
  onWorkflowSourceChange: ChangeHandler<WorkflowDraftSource>;
  onWorkflowContentChange: ChangeHandler<string>;
  onNewDraft: ActionHandler;
  onReloadSelectedWorkflow: ActionHandler;
  onSaveWorkflow: ActionHandler;
  onDeleteWorkflow: ActionHandler;
};

export const WorkflowFilesSection: React.FC<WorkflowFilesSectionProps> = ({
  workflows,
  selectedWorkflowId,
  workflowFilename,
  workflowSource,
  workflowContent,
  canReloadSelectedWorkflow,
  isSavingWorkflow,
  isDeletingWorkflow,
  onRefresh,
  onSelectWorkflow,
  onWorkflowFilenameChange,
  onWorkflowSourceChange,
  onWorkflowContentChange,
  onNewDraft,
  onReloadSelectedWorkflow,
  onSaveWorkflow,
  onDeleteWorkflow,
}) => (
  <Row gutter={[16, 16]}>
    <Col xs={24} lg={10}>
      <ProCard
        title="Available files"
        ghost
        extra={<Button onClick={onRefresh}>Refresh</Button>}
        style={fixedListCardStyle}
        bodyStyle={fixedListCardBodyStyle}
      >
        <div style={fixedListViewportStyle}>
          <ProList<ConfigurationWorkflowFile>
            rowKey={(record) => workflowKey(record)}
            search={false}
            split
            dataSource={workflows}
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
                dataIndex: "filename",
                render: (_, record) => (
                  <Space wrap>
                    <Button
                      type={
                        selectedWorkflowId &&
                        workflowKey(record) === selectedWorkflowId
                          ? "primary"
                          : "link"
                      }
                      onClick={() => onSelectWorkflow(workflowKey(record))}
                    >
                      {record.filename}
                    </Button>
                    <Tag>{record.source}</Tag>
                  </Space>
                ),
              },
              description: {
                render: (_, record) => (
                  <Space direction="vertical" size={4} style={{ width: "100%" }}>
                    <Typography.Text type="secondary">
                      {formatDateTime(record.lastModified)}
                    </Typography.Text>
                    <Typography.Text code>{record.path}</Typography.Text>
                  </Space>
                ),
              },
            }}
          />
        </div>
      </ProCard>
    </Col>
    <Col xs={24} lg={14}>
      <ProCard title="Editor" ghost>
        <Space direction="vertical" style={{ width: "100%" }} size={16}>
          <Space wrap style={{ width: "100%" }}>
            <Input
              style={{ minWidth: 240 }}
              placeholder="workflow filename.yaml"
              value={workflowFilename}
              onChange={(event) => onWorkflowFilenameChange(event.target.value)}
            />
            <Select<WorkflowDraftSource>
              style={{ width: 140 }}
              value={workflowSource}
              onChange={onWorkflowSourceChange}
              options={[
                { label: "home", value: "home" },
                { label: "repo", value: "repo" },
              ]}
            />
            <Button onClick={onNewDraft}>New draft</Button>
            <Button
              onClick={onReloadSelectedWorkflow}
              disabled={!canReloadSelectedWorkflow}
            >
              Reload
            </Button>
            <Button
              type="primary"
              loading={isSavingWorkflow}
              onClick={onSaveWorkflow}
              disabled={!workflowFilename.trim() || !workflowContent.trim()}
            >
              Save file
            </Button>
            <Button
              danger
              loading={isDeletingWorkflow}
              onClick={onDeleteWorkflow}
              disabled={!workflowFilename.trim()}
            >
              Delete file
            </Button>
          </Space>
          <Input.TextArea
            rows={18}
            value={workflowContent}
            onChange={(event) => onWorkflowContentChange(event.target.value)}
            placeholder="name: workflow_name"
          />
          <Typography.Text type="secondary">
            Files are loaded from both home and repo roots. Save writes to the
            selected target source.
          </Typography.Text>
        </Space>
      </ProCard>
    </Col>
  </Row>
);

export type EmbeddingsSectionProps = {
  status?: ConfigurationEmbeddingsStatus;
  enabledDraft: boolean;
  providerTypeDraft: string;
  endpointDraft: string;
  modelDraft: string;
  apiKeyDraft: string;
  isRevealingApiKey: boolean;
  isSaving: boolean;
  isDeleting: boolean;
  onReload: ReloadHandler;
  onEnabledChange: ChangeHandler<boolean>;
  onProviderTypeChange: ChangeHandler<string>;
  onEndpointChange: ChangeHandler<string>;
  onModelChange: ChangeHandler<string>;
  onApiKeyChange: ChangeHandler<string>;
  onRevealApiKey: ActionHandler;
  onSave: ActionHandler;
  onDelete: ActionHandler;
};

export const EmbeddingsSection: React.FC<EmbeddingsSectionProps> = ({
  status,
  enabledDraft,
  providerTypeDraft,
  endpointDraft,
  modelDraft,
  apiKeyDraft,
  isRevealingApiKey,
  isSaving,
  isDeleting,
  onReload,
  onEnabledChange,
  onProviderTypeChange,
  onEndpointChange,
  onModelChange,
  onApiKeyChange,
  onRevealApiKey,
  onSave,
  onDelete,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
    <Alert
      type="info"
      showIcon
      message="Global embeddings fallback"
      description="Manage the local fallback embedding provider and its API key."
    />
    <Space wrap>
      <Tag color={status?.configured ? "success" : "default"}>
        {status?.configured ? "API key configured" : "API key missing"}
      </Tag>
      <Tag>{enabledDraft ? "enabled" : "disabled"}</Tag>
      {status?.masked ? <Tag>{status.masked}</Tag> : null}
      <Button onClick={onReload}>Reload</Button>
    </Space>
    <Space wrap style={{ width: "100%" }}>
      <Select
        style={{ width: 160 }}
        value={enabledDraft ? "enabled" : "disabled"}
        onChange={(value) => onEnabledChange(value === "enabled")}
        options={[
          { label: "Enabled", value: "enabled" },
          { label: "Disabled", value: "disabled" },
        ]}
      />
      <Input
        style={{ minWidth: 220 }}
        placeholder="provider type"
        value={providerTypeDraft}
        onChange={(event) => onProviderTypeChange(event.target.value)}
      />
      <Input
        style={{ minWidth: 320 }}
        placeholder="endpoint"
        value={endpointDraft}
        onChange={(event) => onEndpointChange(event.target.value)}
      />
      <Input
        style={{ minWidth: 220 }}
        placeholder="model"
        value={modelDraft}
        onChange={(event) => onModelChange(event.target.value)}
      />
    </Space>
    <Space wrap style={{ width: "100%" }}>
      <Input.Password
        style={{ minWidth: 360 }}
        placeholder="Embeddings API key"
        value={apiKeyDraft}
        onChange={(event) => onApiKeyChange(event.target.value)}
      />
      <Button
        loading={isRevealingApiKey}
        disabled={!status?.configured}
        onClick={onRevealApiKey}
      >
        Reveal current
      </Button>
      <Button
        type="primary"
        loading={isSaving}
        disabled={
          enabledDraft &&
          (!endpointDraft.trim() ||
            !modelDraft.trim() ||
            (!apiKeyDraft.trim() && !status?.configured))
        }
        onClick={onSave}
      >
        Save embeddings
      </Button>
      <Button
        danger
        loading={isDeleting}
        disabled={
          !status?.configured &&
          !status?.providerType &&
          !status?.endpoint &&
          !status?.model
        }
        onClick={onDelete}
      >
        Delete embeddings
      </Button>
    </Space>
    <Typography.Text type="secondary">
      Stored keys live under{" "}
      <Typography.Text code>LLMProviders:Embeddings:*</Typography.Text>.
    </Typography.Text>
  </Space>
);

export type WebSearchSectionProps = {
  status?: ConfigurationWebSearchStatus;
  enabledDraft: boolean;
  providerDraft: string;
  endpointDraft: string;
  timeoutDraft: string;
  searchDepthDraft: string;
  apiKeyDraft: string;
  isRevealingApiKey: boolean;
  isSaving: boolean;
  isDeleting: boolean;
  onReload: ReloadHandler;
  onEnabledChange: ChangeHandler<boolean>;
  onProviderChange: ChangeHandler<string>;
  onEndpointChange: ChangeHandler<string>;
  onTimeoutChange: ChangeHandler<string>;
  onSearchDepthChange: ChangeHandler<string>;
  onApiKeyChange: ChangeHandler<string>;
  onRevealApiKey: ActionHandler;
  onSave: ActionHandler;
  onDelete: ActionHandler;
};

export const WebSearchSection: React.FC<WebSearchSectionProps> = ({
  status,
  enabledDraft,
  providerDraft,
  endpointDraft,
  timeoutDraft,
  searchDepthDraft,
  apiKeyDraft,
  isRevealingApiKey,
  isSaving,
  isDeleting,
  onReload,
  onEnabledChange,
  onProviderChange,
  onEndpointChange,
  onTimeoutChange,
  onSearchDepthChange,
  onApiKeyChange,
  onRevealApiKey,
  onSave,
  onDelete,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
    <Alert
      type="info"
      showIcon
      message="Local web search tool configuration"
      description="Manage the web search provider and keep its API key in secrets."
    />
    <Space wrap>
      <Tag color={status?.configured ? "success" : "default"}>
        {status?.configured ? "API key configured" : "API key missing"}
      </Tag>
      <Tag>{enabledDraft ? "enabled" : "disabled"}</Tag>
      {status?.masked ? <Tag>{status.masked}</Tag> : null}
      <Button onClick={onReload}>Reload</Button>
    </Space>
    <Space wrap style={{ width: "100%" }}>
      <Select
        style={{ width: 160 }}
        value={enabledDraft ? "enabled" : "disabled"}
        onChange={(value) => onEnabledChange(value === "enabled")}
        options={[
          { label: "Enabled", value: "enabled" },
          { label: "Disabled", value: "disabled" },
        ]}
      />
      <Input
        style={{ minWidth: 220 }}
        placeholder="provider"
        value={providerDraft}
        onChange={(event) => onProviderChange(event.target.value)}
      />
      <Input
        style={{ minWidth: 320 }}
        placeholder="endpoint"
        value={endpointDraft}
        onChange={(event) => onEndpointChange(event.target.value)}
      />
      <Input
        style={{ width: 180 }}
        placeholder="timeout ms"
        value={timeoutDraft}
        onChange={(event) => onTimeoutChange(event.target.value)}
      />
      <Input
        style={{ width: 180 }}
        placeholder="search depth"
        value={searchDepthDraft}
        onChange={(event) => onSearchDepthChange(event.target.value)}
      />
    </Space>
    <Space wrap style={{ width: "100%" }}>
      <Input.Password
        style={{ minWidth: 360 }}
        placeholder="Web search API key"
        value={apiKeyDraft}
        onChange={(event) => onApiKeyChange(event.target.value)}
      />
      <Button
        loading={isRevealingApiKey}
        disabled={!status?.configured}
        onClick={onRevealApiKey}
      >
        Reveal current
      </Button>
      <Button
        type="primary"
        loading={isSaving}
        disabled={
          enabledDraft &&
          (!providerDraft.trim() ||
            (!apiKeyDraft.trim() && !status?.configured))
        }
        onClick={onSave}
      >
        Save web search
      </Button>
      <Button
        danger
        loading={isDeleting}
        disabled={
          !status?.configured &&
          !status?.provider &&
          !status?.endpoint &&
          status?.timeoutMs == null &&
          !status?.searchDepth
        }
        onClick={onDelete}
      >
        Delete web search
      </Button>
    </Space>
    <Typography.Text type="secondary">
      Non-secret fields are written to{" "}
      <Typography.Text code>config.json</Typography.Text>. The key stays in{" "}
      <Typography.Text code>secrets.json</Typography.Text>.
    </Typography.Text>
  </Space>
);

export type SkillsMpSectionProps = {
  status?: ConfigurationSkillsMpStatus;
  baseUrlDraft: string;
  apiKeyDraft: string;
  isRevealingApiKey: boolean;
  isSaving: boolean;
  isDeleting: boolean;
  onReload: ReloadHandler;
  onBaseUrlChange: ChangeHandler<string>;
  onApiKeyChange: ChangeHandler<string>;
  onRevealApiKey: ActionHandler;
  onSave: ActionHandler;
  onDelete: ActionHandler;
};

export const SkillsMpSection: React.FC<SkillsMpSectionProps> = ({
  status,
  baseUrlDraft,
  apiKeyDraft,
  isRevealingApiKey,
  isSaving,
  isDeleting,
  onReload,
  onBaseUrlChange,
  onApiKeyChange,
  onRevealApiKey,
  onSave,
  onDelete,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
    <Alert
      type="info"
      showIcon
      message="Skills marketplace credentials"
      description="This mirrors the old SkillsMP panel and manages the API key plus optional base URL."
    />
    <Space wrap>
      <Tag color={status?.configured ? "success" : "default"}>
        {status?.configured ? "API key configured" : "API key missing"}
      </Tag>
      {status?.masked ? <Tag>{status.masked}</Tag> : null}
      <Button onClick={onReload}>Reload</Button>
    </Space>
    <Space wrap style={{ width: "100%" }}>
      <Input
        style={{ minWidth: 360 }}
        placeholder="https://skillsmp.com"
        value={baseUrlDraft}
        onChange={(event) => onBaseUrlChange(event.target.value)}
      />
      <Input.Password
        style={{ minWidth: 360 }}
        placeholder="SkillsMP API key"
        value={apiKeyDraft}
        onChange={(event) => onApiKeyChange(event.target.value)}
      />
    </Space>
    <Space wrap>
      <Button
        loading={isRevealingApiKey}
        disabled={!status?.configured}
        onClick={onRevealApiKey}
      >
        Reveal current
      </Button>
      <Button
        type="primary"
        loading={isSaving}
        disabled={!apiKeyDraft.trim() && !status?.configured}
        onClick={onSave}
      >
        Save SkillsMP
      </Button>
      <Button
        danger
        loading={isDeleting}
        disabled={!status?.configured && !status?.baseUrl}
        onClick={onDelete}
      >
        Delete SkillsMP
      </Button>
    </Space>
    <Typography.Text type="secondary">
      API key path:{" "}
      <Typography.Text code>
        {status?.keyPath ?? "SkillsMP:ApiKey"}
      </Typography.Text>
    </Typography.Text>
  </Space>
);

export type SignerKeySectionProps = {
  status?: ConfigurationSecp256k1Status;
  isGenerating: boolean;
  onReload: ReloadHandler;
  onGenerate: ActionHandler;
};

export const SignerKeySection: React.FC<SignerKeySectionProps> = ({
  status,
  isGenerating,
  onReload,
  onGenerate,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
    <Alert
      type="info"
      showIcon
      message="Local secp256k1 signer key"
      description="The public key is safe to copy. Generating a new private key automatically backs up the previous key material in secrets."
    />
    <Space wrap>
      <Tag color={status?.configured ? "success" : "default"}>
        {status?.configured ? "configured" : "not configured"}
      </Tag>
      <Tag>backups: {status?.privateKey.backupCount ?? 0}</Tag>
      <Button onClick={onReload}>Reload</Button>
      <Button type="primary" loading={isGenerating} onClick={onGenerate}>
        Generate & save
      </Button>
    </Space>
    <Space direction="vertical" size={8} style={{ width: "100%" }}>
      <Typography.Text strong>Public key</Typography.Text>
      <Input.TextArea
        rows={4}
        readOnly
        value={status?.publicKey.hex ?? ""}
        placeholder="(not configured yet)"
      />
      {status?.publicKey.hex ? (
        <Typography.Text copyable={{ text: status.publicKey.hex }}>
          Copy public key
        </Typography.Text>
      ) : null}
    </Space>
    <Space direction="vertical" size={8} style={{ width: "100%" }}>
      <Typography.Text strong>Private key status</Typography.Text>
      <Input
        readOnly
        value={status?.privateKey.masked ?? ""}
        placeholder="(not configured yet)"
      />
      <Typography.Text type="secondary">
        Stored at{" "}
        <Typography.Text code>
          {status?.privateKey.keyPath ?? "Crypto:EcdsaSecp256k1:PrivateKeyHex"}
        </Typography.Text>
      </Typography.Text>
    </Space>
  </Space>
);

export type ConnectorsSectionProps = {
  rawDocument?: ConfigurationCollectionRawDocumentLike;
  connectorsJsonDraft: string;
  isValidating: boolean;
  isSaving: boolean;
  onReload: ReloadHandler;
  onValidate: ActionHandler;
  onSave: ActionHandler;
  onConnectorsJsonChange: ChangeHandler<string>;
};

type ConfigurationCollectionRawDocumentLike = {
  exists?: boolean;
  count: number;
  path?: string;
};

export const ConnectorsSection: React.FC<ConnectorsSectionProps> = ({
  rawDocument,
  connectorsJsonDraft,
  isValidating,
  isSaving,
  onReload,
  onValidate,
  onSave,
  onConnectorsJsonChange,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
    <Alert
      type="info"
      showIcon
      message="Raw connector definitions"
      description="Connector configuration is edited as raw JSON in this phase to avoid losing disabled entries through a filtered structured view."
    />
    <Space wrap>
      <Tag color={rawDocument?.exists ? "success" : "default"}>
        {rawDocument?.exists
          ? "connectors.json present"
          : "connectors.json missing"}
      </Tag>
      <Tag>entries: {rawDocument?.count ?? 0}</Tag>
      <Button onClick={onReload}>Reload</Button>
      <Button loading={isValidating} onClick={onValidate}>
        Validate
      </Button>
      <Button type="primary" loading={isSaving} onClick={onSave}>
        Save connectors
      </Button>
    </Space>
    <Input.TextArea
      rows={18}
      value={connectorsJsonDraft}
      onChange={(event) => onConnectorsJsonChange(event.target.value)}
      placeholder={'{\n  "connectors": []\n}'}
    />
    {rawDocument?.path ? (
      <Typography.Text type="secondary">
        Path: <Typography.Text code>{rawDocument.path}</Typography.Text>
      </Typography.Text>
    ) : null}
  </Space>
);

export type LlmProvidersSectionProps = {
  defaultProvider: string;
  pendingDefaultProvider: string;
  defaultOptions: Array<{ label: string; value: string }>;
  providerTypeOptions: Array<{ label: string; value: string }>;
  llmInstances: ConfigurationLlmInstance[];
  llmProviderTypes: ConfigurationLlmProviderType[];
  llmApiKeyStatus?: ConfigurationLlmApiKeyStatus;
  selectedLlmInstanceName: string | null;
  isNewLlmInstanceDraft: boolean;
  llmInstanceNameDraft: string;
  llmProviderTypeDraft: string;
  llmModelDraft: string;
  llmEndpointDraft: string;
  llmApiKeyDraft: string;
  llmProbeResult: ConfigurationLlmProbeResult | null;
  llmModelsResult: ConfigurationLlmProbeResult | null;
  isSettingDefaultProvider: boolean;
  isSavingInstance: boolean;
  isDeletingInstance: boolean;
  isRevealingApiKey: boolean;
  isSettingApiKey: boolean;
  isDeletingApiKey: boolean;
  isTestingConnection: boolean;
  isFetchingModels: boolean;
  onPendingDefaultProviderChange: ChangeHandler<string>;
  onSetDefaultProvider: ActionHandler;
  onNewInstance: ActionHandler;
  onLlmInstanceNameChange: ChangeHandler<string>;
  onLlmProviderTypeChange: ChangeHandler<string>;
  onLlmModelChange: ChangeHandler<string>;
  onLlmEndpointChange: ChangeHandler<string>;
  onLlmApiKeyChange: ChangeHandler<string>;
  onSaveInstance: ActionHandler;
  onDeleteInstance: ActionHandler;
  onRevealApiKey: ActionHandler;
  onSetApiKey: ActionHandler;
  onDeleteApiKey: ActionHandler;
  onTestConnection: ActionHandler;
  onFetchModels: ActionHandler;
  onSelectInstance: ChangeHandler<string>;
};

export const LlmProvidersSection: React.FC<LlmProvidersSectionProps> = ({
  defaultProvider,
  pendingDefaultProvider,
  defaultOptions,
  providerTypeOptions,
  llmInstances,
  llmProviderTypes,
  llmApiKeyStatus,
  selectedLlmInstanceName,
  isNewLlmInstanceDraft,
  llmInstanceNameDraft,
  llmProviderTypeDraft,
  llmModelDraft,
  llmEndpointDraft,
  llmApiKeyDraft,
  llmProbeResult,
  llmModelsResult,
  isSettingDefaultProvider,
  isSavingInstance,
  isDeletingInstance,
  isRevealingApiKey,
  isSettingApiKey,
  isDeletingApiKey,
  isTestingConnection,
  isFetchingModels,
  onPendingDefaultProviderChange,
  onSetDefaultProvider,
  onNewInstance,
  onLlmInstanceNameChange,
  onLlmProviderTypeChange,
  onLlmModelChange,
  onLlmEndpointChange,
  onLlmApiKeyChange,
  onSaveInstance,
  onDeleteInstance,
  onRevealApiKey,
  onSetApiKey,
  onDeleteApiKey,
  onTestConnection,
  onFetchModels,
  onSelectInstance,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
    <Space wrap>
      <Tag color="processing">
        Default provider: {defaultProvider || "default"}
      </Tag>
      <Select
        style={{ minWidth: 260 }}
        value={pendingDefaultProvider || undefined}
        onChange={onPendingDefaultProviderChange}
        placeholder="Choose a configured provider"
        options={defaultOptions}
      />
      <Button
        type="primary"
        loading={isSettingDefaultProvider}
        disabled={!pendingDefaultProvider}
        onClick={onSetDefaultProvider}
      >
        Set default
      </Button>
    </Space>
    <ProCard title="Instance maintenance" ghost>
      <Space direction="vertical" style={{ width: "100%" }} size={16}>
        <Space wrap style={{ width: "100%" }}>
          <Input
            style={{ minWidth: 220 }}
            placeholder="provider name"
            value={llmInstanceNameDraft}
            onChange={(event) => onLlmInstanceNameChange(event.target.value)}
          />
          <Select
            style={{ minWidth: 240 }}
            value={llmProviderTypeDraft || undefined}
            onChange={onLlmProviderTypeChange}
            placeholder="Provider type"
            options={providerTypeOptions}
          />
          <Input
            style={{ minWidth: 220 }}
            placeholder="model"
            value={llmModelDraft}
            onChange={(event) => onLlmModelChange(event.target.value)}
          />
          <Input
            style={{ minWidth: 280 }}
            placeholder="endpoint (optional)"
            value={llmEndpointDraft}
            onChange={(event) => onLlmEndpointChange(event.target.value)}
          />
        </Space>
        <Space wrap>
          <Button onClick={onNewInstance}>New instance</Button>
          <Button
            type="primary"
            loading={isSavingInstance}
            disabled={
              !llmInstanceNameDraft.trim() ||
              !llmProviderTypeDraft.trim() ||
              !llmModelDraft.trim()
            }
            onClick={onSaveInstance}
          >
            Save instance
          </Button>
          <Button
            danger
            loading={isDeletingInstance}
            disabled={!llmInstanceNameDraft.trim() || isNewLlmInstanceDraft}
            onClick={onDeleteInstance}
          >
            Delete instance
          </Button>
        </Space>
        <Space wrap style={{ width: "100%" }}>
          <Input.Password
            style={{ minWidth: 320 }}
            placeholder="API key"
            value={llmApiKeyDraft}
            onChange={(event) => onLlmApiKeyChange(event.target.value)}
          />
          <Button
            loading={isRevealingApiKey}
            disabled={!llmInstanceNameDraft.trim() || isNewLlmInstanceDraft}
            onClick={onRevealApiKey}
          >
            Reveal current
          </Button>
          <Button
            type="primary"
            loading={isSettingApiKey}
            disabled={!llmInstanceNameDraft.trim() || !llmApiKeyDraft.trim()}
            onClick={onSetApiKey}
          >
            Set API key
          </Button>
          <Button
            danger
            loading={isDeletingApiKey}
            disabled={
              !llmInstanceNameDraft.trim() || !llmApiKeyStatus?.configured
            }
            onClick={onDeleteApiKey}
          >
            Remove API key
          </Button>
        </Space>
        <Space wrap>
          <Tag color={llmApiKeyStatus?.configured ? "success" : "default"}>
            {llmApiKeyStatus?.configured
              ? "api key configured"
              : "api key missing"}
          </Tag>
          {llmApiKeyStatus?.masked ? <Tag>{llmApiKeyStatus.masked}</Tag> : null}
        </Space>
        <Space wrap>
          <Button
            loading={isTestingConnection}
            disabled={!llmProviderTypeDraft.trim() || !llmApiKeyDraft.trim()}
            onClick={onTestConnection}
          >
            Test connection
          </Button>
          <Button
            loading={isFetchingModels}
            disabled={!llmProviderTypeDraft.trim() || !llmApiKeyDraft.trim()}
            onClick={onFetchModels}
          >
            Fetch models
          </Button>
        </Space>
        {llmProbeResult ? (
          <Alert
            type={llmProbeResult.ok ? "success" : "warning"}
            showIcon
            message="Connectivity probe"
            description={
              <Space direction="vertical" size={4}>
                <Typography.Text>
                  {formatProbeSummary(llmProbeResult)}
                </Typography.Text>
                {llmProbeResult.sampleModels?.length ? (
                  <Typography.Text code>
                    {llmProbeResult.sampleModels.join(", ")}
                  </Typography.Text>
                ) : null}
              </Space>
            }
          />
        ) : null}
        {llmModelsResult ? (
          <Alert
            type={llmModelsResult.ok ? "success" : "warning"}
            showIcon
            message="Models probe"
            description={
              <Space direction="vertical" size={4}>
                <Typography.Text>
                  {formatProbeSummary(llmModelsResult)}
                </Typography.Text>
                {llmModelsResult.models?.length ? (
                  <Typography.Text code>
                    {llmModelsResult.models.slice(0, 10).join(", ")}
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
        dataSource={llmInstances}
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
            dataIndex: "name",
            render: (_, record) => (
              <Space wrap>
                <Button
                  type={
                    !isNewLlmInstanceDraft &&
                    selectedLlmInstanceName === record.name
                      ? "primary"
                      : "link"
                  }
                  onClick={() => onSelectInstance(record.name)}
                >
                  {record.name}
                </Button>
                <Tag>{record.providerDisplayName}</Tag>
              </Space>
            ),
          },
          description: {
            render: (_, record) => (
              <Space direction="vertical" size={4} style={{ width: "100%" }}>
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
        dataSource={llmProviderTypes}
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
            dataIndex: "displayName",
            render: (_, record) => (
              <Space wrap>
                <Typography.Text strong>{record.displayName}</Typography.Text>
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
);

export type McpSectionProps = {
  servers: ConfigurationMcpServer[];
  rawDocument?: ConfigurationCollectionRawDocumentLike;
  selectedMcpServerName: string | null;
  isNewMcpDraft: boolean;
  mcpNameDraft: string;
  mcpCommandDraft: string;
  mcpArgsDraft: string;
  mcpEnvDraft: string;
  mcpTimeoutDraft: string;
  mcpJsonDraft: string;
  isSavingServer: boolean;
  isDeletingServer: boolean;
  isValidatingRaw: boolean;
  isSavingRaw: boolean;
  onReloadServers: ReloadHandler;
  onSelectServer: ChangeHandler<string>;
  onMcpNameChange: ChangeHandler<string>;
  onMcpCommandChange: ChangeHandler<string>;
  onMcpArgsChange: ChangeHandler<string>;
  onMcpEnvChange: ChangeHandler<string>;
  onMcpTimeoutChange: ChangeHandler<string>;
  onMcpJsonChange: ChangeHandler<string>;
  onNewServer: ActionHandler;
  onSaveServer: ActionHandler;
  onDeleteServer: ActionHandler;
  onReloadRaw: ReloadHandler;
  onValidateRaw: ActionHandler;
  onSaveRaw: ActionHandler;
};

export const McpSection: React.FC<McpSectionProps> = ({
  servers,
  rawDocument,
  selectedMcpServerName,
  isNewMcpDraft,
  mcpNameDraft,
  mcpCommandDraft,
  mcpArgsDraft,
  mcpEnvDraft,
  mcpTimeoutDraft,
  mcpJsonDraft,
  isSavingServer,
  isDeletingServer,
  isValidatingRaw,
  isSavingRaw,
  onReloadServers,
  onSelectServer,
  onMcpNameChange,
  onMcpCommandChange,
  onMcpArgsChange,
  onMcpEnvChange,
  onMcpTimeoutChange,
  onMcpJsonChange,
  onNewServer,
  onSaveServer,
  onDeleteServer,
  onReloadRaw,
  onValidateRaw,
  onSaveRaw,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
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
          extra={<Button onClick={onReloadServers}>Refresh</Button>}
          style={fixedListCardStyle}
          bodyStyle={fixedListCardBodyStyle}
        >
          <div style={fixedListViewportStyle}>
            <ProList<ConfigurationMcpServer>
              rowKey="name"
              search={false}
              split
              dataSource={servers}
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
                  dataIndex: "name",
                  render: (_, record) => (
                    <Space wrap>
                      <Button
                        type={
                          !isNewMcpDraft && selectedMcpServerName === record.name
                            ? "primary"
                            : "link"
                        }
                        onClick={() => onSelectServer(record.name)}
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
                      style={{ width: "100%" }}
                    >
                      <Typography.Text code>{record.command}</Typography.Text>
                      <Typography.Text type="secondary">
                        args: {record.args.length} · env:{" "}
                        {Object.keys(record.env).length}
                      </Typography.Text>
                    </Space>
                  ),
                },
              }}
            />
          </div>
        </ProCard>
      </Col>
      <Col xs={24} lg={14}>
        <ProCard title="Server editor" ghost>
          <Space direction="vertical" style={{ width: "100%" }} size={16}>
            <Space wrap style={{ width: "100%" }}>
              <Input
                style={{ minWidth: 220 }}
                placeholder="server name"
                value={mcpNameDraft}
                onChange={(event) => onMcpNameChange(event.target.value)}
              />
              <Input
                style={{ minWidth: 260 }}
                placeholder="command"
                value={mcpCommandDraft}
                onChange={(event) => onMcpCommandChange(event.target.value)}
              />
              <Input
                style={{ width: 160 }}
                type="number"
                min={1}
                placeholder="timeout ms"
                value={mcpTimeoutDraft}
                onChange={(event) => onMcpTimeoutChange(event.target.value)}
              />
              <Button onClick={onNewServer}>New server</Button>
              <Button
                type="primary"
                loading={isSavingServer}
                disabled={!mcpNameDraft.trim() || !mcpCommandDraft.trim()}
                onClick={onSaveServer}
              >
                Save server
              </Button>
              <Button
                danger
                loading={isDeletingServer}
                disabled={!mcpNameDraft.trim() || isNewMcpDraft}
                onClick={onDeleteServer}
              >
                Delete server
              </Button>
            </Space>
            <Space direction="vertical" size={8} style={{ width: "100%" }}>
              <Typography.Text strong>Args</Typography.Text>
              <Input.TextArea
                rows={6}
                value={mcpArgsDraft}
                onChange={(event) => onMcpArgsChange(event.target.value)}
                placeholder={"node\nserver.js\n--transport\nstdio"}
              />
              <Typography.Text type="secondary">
                One argument per line.
              </Typography.Text>
            </Space>
            <Space direction="vertical" size={8} style={{ width: "100%" }}>
              <Typography.Text strong>Env</Typography.Text>
              <Input.TextArea
                rows={8}
                value={mcpEnvDraft}
                onChange={(event) => onMcpEnvChange(event.target.value)}
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
      <Space direction="vertical" style={{ width: "100%" }} size={16}>
        <Space wrap>
          <Tag color={rawDocument?.exists ? "success" : "default"}>
            {rawDocument?.exists ? "mcp.json present" : "mcp.json missing"}
          </Tag>
          <Tag>servers: {rawDocument?.count ?? 0}</Tag>
          <Button onClick={onReloadRaw}>Reload raw</Button>
          <Button loading={isValidatingRaw} onClick={onValidateRaw}>
            Validate raw
          </Button>
          <Button type="primary" loading={isSavingRaw} onClick={onSaveRaw}>
            Save raw
          </Button>
        </Space>
        <Input.TextArea
          rows={14}
          value={mcpJsonDraft}
          onChange={(event) => onMcpJsonChange(event.target.value)}
          placeholder={'{\n  "mcpServers": {}\n}'}
        />
        {rawDocument?.path ? (
          <Typography.Text type="secondary">
            Path: <Typography.Text code>{rawDocument.path}</Typography.Text>
          </Typography.Text>
        ) : null}
      </Space>
    </ProCard>
  </Space>
);

export type SecretsSectionProps = {
  secretsJsonDraft: string;
  secretKeyDraft: string;
  secretValueDraft: string;
  isSavingSecret: boolean;
  isRemovingSecret: boolean;
  isSavingRaw: boolean;
  onReload: ReloadHandler;
  onSecretsJsonChange: ChangeHandler<string>;
  onSecretKeyChange: ChangeHandler<string>;
  onSecretValueChange: ChangeHandler<string>;
  onSetSecret: ActionHandler;
  onRemoveSecret: ActionHandler;
  onSaveRaw: ActionHandler;
};

export const SecretsSection: React.FC<SecretsSectionProps> = ({
  secretsJsonDraft,
  secretKeyDraft,
  secretValueDraft,
  isSavingSecret,
  isRemovingSecret,
  isSavingRaw,
  onReload,
  onSecretsJsonChange,
  onSecretKeyChange,
  onSecretValueChange,
  onSetSecret,
  onRemoveSecret,
  onSaveRaw,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
    <Alert
      type="warning"
      showIcon
      message="Sensitive local data"
      description="This editor writes raw secrets JSON on the current machine. Treat it as a local admin surface."
    />
    <ProCard title="Key-based operations" ghost>
      <Space direction="vertical" style={{ width: "100%" }} size={16}>
        <Space wrap style={{ width: "100%" }}>
          <Input
            style={{ minWidth: 320 }}
            placeholder="secret key"
            value={secretKeyDraft}
            onChange={(event) => onSecretKeyChange(event.target.value)}
          />
          <Input.Password
            style={{ minWidth: 320 }}
            placeholder="secret value"
            value={secretValueDraft}
            onChange={(event) => onSecretValueChange(event.target.value)}
          />
          <Button
            type="primary"
            loading={isSavingSecret}
            disabled={!secretKeyDraft.trim() || !secretValueDraft}
            onClick={onSetSecret}
          >
            Set secret
          </Button>
          <Button
            danger
            loading={isRemovingSecret}
            disabled={!secretKeyDraft.trim()}
            onClick={onRemoveSecret}
          >
            Remove secret
          </Button>
        </Space>
        <Typography.Text type="secondary">
          Use key-based operations for small targeted changes. Raw JSON remains
          available below for bulk edits.
        </Typography.Text>
      </Space>
    </ProCard>
    <Space wrap>
      <Button onClick={onReload}>Reload</Button>
      <Button type="primary" loading={isSavingRaw} onClick={onSaveRaw}>
        Save secrets
      </Button>
    </Space>
    <Input.TextArea
      rows={18}
      value={secretsJsonDraft}
      onChange={(event) => onSecretsJsonChange(event.target.value)}
      placeholder={'{\n  "LLMProviders": {}\n}'}
    />
  </Space>
);

export type RawConfigSectionProps = {
  configJsonDraft: string;
  isSaving: boolean;
  onReload: ReloadHandler;
  onConfigJsonChange: ChangeHandler<string>;
  onSave: ActionHandler;
};

export const RawConfigSection: React.FC<RawConfigSectionProps> = ({
  configJsonDraft,
  isSaving,
  onReload,
  onConfigJsonChange,
  onSave,
}) => (
  <Space direction="vertical" style={{ width: "100%" }} size={16}>
    <Typography.Text type="secondary">
      This editor writes the local runtime config JSON directly.
    </Typography.Text>
    <Space wrap>
      <Button onClick={onReload}>Reload</Button>
      <Button type="primary" loading={isSaving} onClick={onSave}>
        Save config
      </Button>
    </Space>
    <Input.TextArea
      rows={18}
      value={configJsonDraft}
      onChange={(event) => onConfigJsonChange(event.target.value)}
      placeholder={'{\n  "Workflow": {}\n}'}
    />
  </Space>
);
