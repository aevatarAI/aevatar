import {
  DeleteOutlined,
  PlusOutlined,
  ReloadOutlined,
  SaveOutlined,
  SendOutlined,
  ToolOutlined,
} from "@ant-design/icons";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Input,
  Popconfirm,
  Select,
  Space,
  Tag,
  Typography,
  theme,
} from "antd";
import React from "react";
import {
  channelSettingsApi,
  ChannelSettingsApiError,
  type ChannelAuthorizationMode,
  type ChannelRegistrationSummary,
  type ChannelRuntimeConfig,
  type ChannelRuntimeConfigDetail,
  type ChannelServiceChoice,
  type ChannelRuntimeNyxIdServiceSelector,
} from "./channelsApi";
import {
  buildSettingsInsetCardStyle,
  buildSettingsPanelStyle,
  SettingsPageShell,
  SummaryField,
  SummaryMetric,
} from "./shared";

type ChannelSelectorDraft = {
  readonly id: string;
  readonly serviceSlug: string;
  readonly endpointNamesText: string;
};

type ChannelSettingsDraft = {
  readonly authorizationMode: ChannelAuthorizationMode;
  readonly serviceIds: readonly string[];
  readonly instructions: string;
  readonly defaultSkillName: string;
  readonly defaultSkillVersion: string;
  readonly toolSetRefsText: string;
  readonly extraToolNamesText: string;
  readonly selectors: readonly ChannelSelectorDraft[];
};

let nextSelectorDraftId = 0;

const servicesQueryKey = ["settings", "channels", "services"] as const;
const listQueryKey = ["settings", "channels", "registrations"] as const;

function createSelectorDraftId(): string {
  nextSelectorDraftId += 1;
  return `selector-${nextSelectorDraftId}`;
}

function detailQueryKey(registrationId: string) {
  return ["settings", "channels", "runtime-config", registrationId] as const;
}

function splitLines(value: string): string[] {
  return value
    .split(/\r?\n|,/)
    .map((entry) => entry.trim())
    .filter(Boolean);
}

function serializeLines(values: readonly string[]): string {
  return values.join("\n");
}

function createSelectorDrafts(
  selectors: readonly ChannelRuntimeNyxIdServiceSelector[],
): ChannelSelectorDraft[] {
  return selectors.map((selector) => ({
    id: createSelectorDraftId(),
    serviceSlug: selector.serviceSlug,
    endpointNamesText: serializeLines(selector.endpointNames),
  }));
}

function buildSelectors(
  selectors: readonly ChannelSelectorDraft[],
): ChannelRuntimeNyxIdServiceSelector[] {
  return selectors
    .map((selector) => ({
      serviceSlug: selector.serviceSlug.trim(),
      endpointNames: splitLines(selector.endpointNamesText),
    }))
    .filter((selector) => selector.serviceSlug);
}

function formatServiceLabel(service: ChannelServiceChoice): string {
  const label = service.label || service.catalogServiceName || service.slug || service.id;
  return service.slug ? `${label} (${service.slug})` : label;
}

function createDraft(detail: ChannelRuntimeConfigDetail): ChannelSettingsDraft {
  const config = detail.runtimeConfig;
  return {
    authorizationMode: detail.authorizationMode,
    serviceIds: detail.serviceIds,
    instructions: config.instructions,
    defaultSkillName: config.defaultSkill.name,
    defaultSkillVersion: config.defaultSkill.version,
    toolSetRefsText: serializeLines(config.toolSetRefs),
    extraToolNamesText: serializeLines(config.extraToolNames),
    selectors: createSelectorDrafts(config.nyxidServiceSelectors),
  };
}

function buildRuntimeConfig(draft: ChannelSettingsDraft): ChannelRuntimeConfig {
  return {
    instructions: draft.instructions,
    defaultSkill: {
      name: draft.defaultSkillName.trim(),
      version: draft.defaultSkillVersion.trim(),
    },
    toolSetRefs: splitLines(draft.toolSetRefsText),
    extraToolNames: splitLines(draft.extraToolNamesText),
    nyxidServiceSelectors: draft.authorizationMode === "explicit_service_allowlist"
      ? buildSelectors(draft.selectors)
      : [],
  };
}

function summarizeRegistration(registration: ChannelRegistrationSummary): string {
  const enabled = [
    registration.hasInstructions ? "instructions" : null,
    registration.hasToolSetRefs ? "tool sets" : null,
    registration.hasExtraToolNames ? "extra tools" : null,
  ].filter(Boolean);
  return enabled.length > 0 ? enabled.join(" / ") : "runtime defaults";
}

const gridStyle: React.CSSProperties = {
  display: "grid",
  gap: 16,
  gridTemplateColumns: "repeat(auto-fit, minmax(min(100%, 280px), 1fr))",
};

const fieldGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 12,
  gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
};

const fieldStackStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 8,
  minWidth: 0,
};

const fieldLabelStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 12,
  fontWeight: 700,
};

const ChannelField: React.FC<{
  children: React.ReactNode;
  label: string;
}> = ({ children, label }) => (
  <div style={fieldStackStyle}>
    <span style={fieldLabelStyle}>{label}</span>
    {children}
  </div>
);

const ChannelRuntimeForm: React.FC<{
  detail: ChannelRuntimeConfigDetail;
  draft: ChannelSettingsDraft;
  fieldErrors: readonly { field: string; code: string; message: string }[];
  saving: boolean;
  services: readonly ChannelServiceChoice[];
  servicesLoading: boolean;
  onChange: (draft: ChannelSettingsDraft) => void;
  onSave: () => void;
}> = ({ detail, draft, fieldErrors, onChange, onSave, saving, services, servicesLoading }) => {
  const { token } = theme.useToken();
  const serviceOptions = services.map((service) => ({
    label: formatServiceLabel(service),
    value: service.id,
    disabled: !service.active || !service.credentialSource.allowed,
  }));
  const selectorServiceOptions = services
    .filter((service) => draft.serviceIds.includes(service.id))
    .map((service) => ({
      label: formatServiceLabel(service),
      value: service.slug,
      disabled: !service.active || !service.credentialSource.allowed,
    }));

  return (
    <div style={{ ...buildSettingsPanelStyle(token), padding: 16 }}>
      <Space direction="vertical" size={16} style={{ width: "100%" }}>
        <div style={fieldGridStyle}>
          <SummaryMetric label="State version" value={detail.stateVersion} />
          <SummaryMetric
            label="Agent key"
            tone={detail.agentKey.ready ? "success" : "warning"}
            value={detail.agentKey.status}
          />
          <SummaryMetric
            label="Workflow delivery"
            tone={detail.workflowResultDeliveryStatus === "enabled" ? "success" : "warning"}
            value={detail.workflowResultDeliveryStatus || "unknown"}
          />
        </div>

        <div style={fieldGridStyle}>
          <SummaryField label="Registration" value={detail.registrationId} />
          <SummaryField label="Platform" value={detail.platform || "unknown"} />
          <SummaryField label="Scope" value={detail.scopeId || "unknown"} />
          <SummaryField label="Agent key id" value={detail.agentKey.apiKeyId || "not available"} />
        </div>

        {fieldErrors.length > 0 ? (
          <Alert
            message="Runtime config was not saved"
            description={
              <Space direction="vertical" size={4}>
                {fieldErrors.map((error) => (
                  <Typography.Text key={`${error.field}:${error.code}`}>
                    {error.field} · {error.code}
                  </Typography.Text>
                ))}
              </Space>
            }
            type="error"
            showIcon
          />
        ) : null}

        <div style={fieldGridStyle}>
          <ChannelField label="Authorization mode">
            <Select
              value={draft.authorizationMode}
              onChange={(authorizationMode) => onChange({
                ...draft,
                authorizationMode,
                serviceIds: authorizationMode === "explicit_service_allowlist" ? draft.serviceIds : [],
                selectors: authorizationMode === "explicit_service_allowlist" ? draft.selectors : [],
              })}
              options={[
                { label: "NyxID default", value: "nyxid_default" },
                {
                  label: "Explicit service allowlist",
                  value: "explicit_service_allowlist",
                },
              ]}
            />
          </ChannelField>
          <ChannelField label="Authorized services">
            <Select
              disabled={draft.authorizationMode !== "explicit_service_allowlist"}
              loading={servicesLoading}
              mode="multiple"
              options={serviceOptions}
              placeholder="Select verified NyxID services"
              value={draft.serviceIds}
              onChange={(serviceIds) => onChange({
                ...draft,
                serviceIds,
                selectors: draft.selectors.filter((selector) =>
                  services.some((service) => service.id && serviceIds.includes(service.id) && service.slug === selector.serviceSlug),
                ),
              })}
            />
          </ChannelField>
        </div>

        <ChannelField label="Instructions">
          <Input.TextArea
            autoSize={{ minRows: 4, maxRows: 10 }}
            value={draft.instructions}
            onChange={(event) => onChange({ ...draft, instructions: event.target.value })}
          />
        </ChannelField>

        <div style={fieldGridStyle}>
          <ChannelField label="Default skill name">
            <Input
              value={draft.defaultSkillName}
              onChange={(event) => onChange({ ...draft, defaultSkillName: event.target.value })}
            />
          </ChannelField>
          <ChannelField label="Default skill version">
            <Input
              value={draft.defaultSkillVersion}
              onChange={(event) => onChange({ ...draft, defaultSkillVersion: event.target.value })}
            />
          </ChannelField>
        </div>

        <div style={fieldGridStyle}>
          <ChannelField label="Tool set refs">
            <Input.TextArea
              autoSize={{ minRows: 3, maxRows: 8 }}
              value={draft.toolSetRefsText}
              onChange={(event) => onChange({ ...draft, toolSetRefsText: event.target.value })}
            />
          </ChannelField>
          <ChannelField label="Extra tool names">
            <Input.TextArea
              autoSize={{ minRows: 3, maxRows: 8 }}
              value={draft.extraToolNamesText}
              onChange={(event) => onChange({ ...draft, extraToolNamesText: event.target.value })}
            />
          </ChannelField>
        </div>

        <div style={fieldStackStyle}>
          <span style={fieldLabelStyle}>NyxID service selectors</span>
          <Space direction="vertical" size={8} style={{ width: "100%" }}>
            {draft.selectors.map((selector, index) => (
              <div
                key={selector.id}
                style={{
                  display: "grid",
                  gap: 8,
                  gridTemplateColumns: "minmax(180px, 1fr) minmax(220px, 2fr) auto",
                }}
              >
                <Select
                  disabled={draft.authorizationMode !== "explicit_service_allowlist"}
                  options={selectorServiceOptions}
                  placeholder="Service"
                  value={selector.serviceSlug || undefined}
                  onChange={(serviceSlug) => {
                    const next = [...draft.selectors];
                    next[index] = { ...selector, serviceSlug };
                    onChange({ ...draft, selectors: next });
                  }}
                />
                <Input
                  placeholder="Endpoint names"
                  value={selector.endpointNamesText}
                  onChange={(event) => {
                    const next = [...draft.selectors];
                    next[index] = { ...selector, endpointNamesText: event.target.value };
                    onChange({ ...draft, selectors: next });
                  }}
                />
                <Button
                  onClick={() => onChange({
                    ...draft,
                    selectors: draft.selectors.filter((_, itemIndex) => itemIndex !== index),
                  })}
                >
                  Remove
                </Button>
              </div>
            ))}
            <Button
              disabled={draft.authorizationMode !== "explicit_service_allowlist" || selectorServiceOptions.length === 0}
              icon={<PlusOutlined />}
              onClick={() => onChange({
                ...draft,
                selectors: [
                  ...draft.selectors,
                  {
                    id: createSelectorDraftId(),
                    serviceSlug: String(selectorServiceOptions[0]?.value ?? ""),
                    endpointNamesText: "",
                  },
                ],
              })}
            >
              Add selector
            </Button>
          </Space>
        </div>

        <Button
          icon={<SaveOutlined />}
          loading={saving}
          onClick={onSave}
          type="primary"
        >
          Save runtime config
        </Button>
      </Space>
    </div>
  );
};

const ChannelRegistrationList: React.FC<{
  registrations: readonly ChannelRegistrationSummary[];
  selectedId: string;
  onSelect: (registrationId: string) => void;
}> = ({ onSelect, registrations, selectedId }) => {
  const { token } = theme.useToken();
  if (registrations.length === 0) {
    return <Empty description="No channel registrations" />;
  }

  return (
    <Space direction="vertical" size={10} style={{ width: "100%" }}>
      {registrations.map((registration) => {
        const selected = registration.id === selectedId;
        return (
          <button
            key={registration.id}
            type="button"
            onClick={() => onSelect(registration.id)}
            style={{
              ...buildSettingsInsetCardStyle(token),
              background: selected ? token.colorPrimaryBg : token.colorBgContainer,
              cursor: "pointer",
              textAlign: "left",
              width: "100%",
            }}
          >
            <Space direction="vertical" size={8} style={{ width: "100%" }}>
              <Space wrap>
                <Typography.Text strong>{registration.id}</Typography.Text>
                <Tag>{registration.platform || "unknown"}</Tag>
                <Tag color={registration.agentKey.ready ? "success" : "warning"}>
                  {registration.agentKey.status}
                </Tag>
              </Space>
              <Typography.Text type="secondary">
                {registration.authorizationMode} · {summarizeRegistration(registration)}
              </Typography.Text>
              <Typography.Text type="secondary">
                {registration.defaultSkill.name || "No default skill"}
              </Typography.Text>
            </Space>
          </button>
        );
      })}
    </Space>
  );
};

const ChannelSettingsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const [selectedId, setSelectedId] = React.useState("");
  const [draft, setDraft] = React.useState<ChannelSettingsDraft | null>(null);
  const [fieldErrors, setFieldErrors] = React.useState<
    readonly { field: string; code: string; message: string }[]
  >([]);

  const servicesQuery = useQuery({
    queryFn: channelSettingsApi.listServices,
    queryKey: servicesQueryKey,
  });

  const registrationsQuery = useQuery({
    queryFn: channelSettingsApi.listRegistrations,
    queryKey: listQueryKey,
  });

  const services = servicesQuery.data ?? [];
  const registrations = registrationsQuery.data ?? [];

  React.useEffect(() => {
    if (!selectedId && registrations.length > 0) {
      setSelectedId(registrations[0].id);
    }
  }, [registrations, selectedId]);

  const detailQuery = useQuery({
    enabled: Boolean(selectedId),
    queryFn: () => channelSettingsApi.getRuntimeConfig(selectedId),
    queryKey: detailQueryKey(selectedId),
  });

  React.useEffect(() => {
    if (detailQuery.data) {
      setDraft(createDraft(detailQuery.data));
      setFieldErrors([]);
    }
  }, [detailQuery.data]);

  const saveMutation = useMutation({
    mutationFn: async () => {
      if (!draft || !detailQuery.data) {
        throw new Error("No selected registration.");
      }

      return channelSettingsApi.saveRuntimeConfig({
        registrationId: detailQuery.data.registrationId,
        authorizationMode: draft.authorizationMode,
        serviceIds: draft.authorizationMode === "explicit_service_allowlist" ? draft.serviceIds : [],
        runtimeConfig: buildRuntimeConfig(draft),
      });
    },
    onError(error) {
      if (error instanceof ChannelSettingsApiError) {
        setFieldErrors(error.fieldErrors);
      }
    },
    async onSuccess() {
      setFieldErrors([]);
      await queryClient.invalidateQueries({ queryKey: servicesQueryKey });
      await queryClient.invalidateQueries({ queryKey: listQueryKey });
      if (selectedId) {
        await queryClient.invalidateQueries({ queryKey: detailQueryKey(selectedId) });
      }
    },
  });

  const actionMutation = useMutation({
    mutationFn: async (action: "delete" | "repair" | "test") => {
      if (!selectedId) {
        return;
      }
      if (action === "repair") {
        await channelSettingsApi.repairWorkflowResultDelivery(selectedId);
      } else if (action === "test") {
        await channelSettingsApi.testReply(selectedId);
      } else {
        await channelSettingsApi.deleteRegistration(selectedId);
      }
    },
    async onSuccess() {
      await queryClient.invalidateQueries({ queryKey: listQueryKey });
      if (selectedId) {
        await queryClient.invalidateQueries({ queryKey: detailQueryKey(selectedId) });
      }
    },
  });

  const detail = detailQuery.data;
  const loadError = servicesQuery.error || registrationsQuery.error || detailQuery.error;

  return (
    <SettingsPageShell
      title="Channel Settings"
      content="Manage channel registration runtime configuration."
      extra={
        <Button
          icon={<ReloadOutlined />}
          onClick={() => {
            void queryClient.invalidateQueries({ queryKey: servicesQueryKey });
            void queryClient.invalidateQueries({ queryKey: listQueryKey });
            if (selectedId) {
              void queryClient.invalidateQueries({ queryKey: detailQueryKey(selectedId) });
            }
          }}
        >
          Refresh
        </Button>
      }
    >
      {loadError ? (
        <Alert message="Could not load channel settings" type="error" showIcon />
      ) : null}
      <div style={gridStyle}>
        <div style={fieldStackStyle}>
          <Typography.Title level={4} style={{ margin: 0 }}>
            Registrations
          </Typography.Title>
          <ChannelRegistrationList
            registrations={registrations}
            selectedId={selectedId}
            onSelect={setSelectedId}
          />
        </div>
        <div style={fieldStackStyle}>
          {detail && draft ? (
            <>
              <Space wrap>
                <Button
                  icon={<ToolOutlined />}
                  loading={actionMutation.isPending}
                  onClick={() => actionMutation.mutate("repair")}
                >
                  Repair delivery
                </Button>
                <Button
                  icon={<SendOutlined />}
                  loading={actionMutation.isPending}
                  onClick={() => actionMutation.mutate("test")}
                >
                  Test reply
                </Button>
                <Popconfirm
                  title="Delete registration?"
                  onConfirm={() => actionMutation.mutate("delete")}
                >
                  <Button danger icon={<DeleteOutlined />} loading={actionMutation.isPending}>
                    Delete
                  </Button>
                </Popconfirm>
              </Space>
              <ChannelRuntimeForm
                detail={detail}
                draft={draft}
                fieldErrors={fieldErrors}
                onChange={setDraft}
                onSave={() => saveMutation.mutate()}
                saving={saveMutation.isPending}
                services={services}
                servicesLoading={servicesQuery.isLoading}
              />
            </>
          ) : (
            <Empty description="Select a channel registration" />
          )}
        </div>
      </div>
    </SettingsPageShell>
  );
};

export default ChannelSettingsPage;
