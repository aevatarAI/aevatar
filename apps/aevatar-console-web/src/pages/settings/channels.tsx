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
import { t } from "@/shared/i18n/messages";
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
import { observeUserLlmSave } from "./userLlmSaveObservation";
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
  readonly credentialSourceMode: ChannelRuntimeConfig["credentialSourceMode"];
  readonly selectors: readonly ChannelSelectorDraft[];
};

type PendingChannelSettingsSave = {
  readonly saveToken: number;
  readonly registrationId: string;
  readonly baselineStateVersion: number;
  readonly submittedRevision: number;
  readonly submittedDraft: ChannelSettingsDraft;
  readonly phase: "saving" | "accepted" | "accepted_unobserved";
};

type SaveRuntimeConfigRequest = {
  readonly pendingSave: PendingChannelSettingsSave;
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
    credentialSourceMode: config.credentialSourceMode,
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
    credentialSourceMode: draft.credentialSourceMode,
  };
}

function summarizeRegistration(registration: ChannelRegistrationSummary): string {
  const enabled = [
    registration.hasInstructions ? t("pages.settings.channels.summary.instructions", "instructions") : null,
    registration.hasToolSetRefs ? t("pages.settings.channels.summary.toolSets", "tool sets") : null,
    registration.hasExtraToolNames ? t("pages.settings.channels.summary.extraTools", "extra tools") : null,
  ].filter(Boolean);
  return enabled.length > 0
    ? enabled.join(" / ")
    : t("pages.settings.channels.summary.runtimeDefaults", "runtime defaults");
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
          <SummaryMetric label={t("pages.settings.channels.metrics.stateVersion", "State version")} value={detail.stateVersion} />
          <SummaryMetric
            label={t("pages.settings.channels.metrics.agentKey", "Agent key")}
            tone={detail.agentKey.ready ? "success" : "warning"}
            value={detail.agentKey.status}
          />
          <SummaryMetric
            label={t("pages.settings.channels.metrics.workflowDelivery", "Workflow delivery")}
            tone={detail.workflowResultDeliveryStatus === "enabled" ? "success" : "warning"}
            value={detail.workflowResultDeliveryStatus || t("pages.settings.channels.status.unknown", "unknown")}
          />
        </div>

        <div style={fieldGridStyle}>
          <SummaryField label={t("pages.settings.channels.fields.registration", "Registration")} value={detail.registrationId} />
          <SummaryField label={t("pages.settings.channels.fields.platform", "Platform")} value={detail.platform || t("pages.settings.channels.status.unknown", "unknown")} />
          <SummaryField label={t("pages.settings.channels.fields.scope", "Scope")} value={detail.scopeId || t("pages.settings.channels.status.unknown", "unknown")} />
          <SummaryField label={t("pages.settings.channels.fields.agentKeyId", "Agent key id")} value={detail.agentKey.apiKeyId || t("pages.settings.channels.status.notAvailable", "not available")} />
        </div>

        {fieldErrors.length > 0 ? (
          <Alert
            message={t("pages.settings.channels.errors.runtimeConfigNotSaved", "Runtime config was not saved")}
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
          <ChannelField label={t("pages.settings.channels.fields.authorizationMode", "Authorization mode")}>
            <Select
              value={draft.authorizationMode}
              onChange={(authorizationMode) => onChange({
                ...draft,
                authorizationMode,
                serviceIds: authorizationMode === "explicit_service_allowlist" ? draft.serviceIds : [],
                selectors: authorizationMode === "explicit_service_allowlist" ? draft.selectors : [],
              })}
              options={[
                { label: t("pages.settings.channels.authorization.nyxidDefault", "NyxID default"), value: "nyxid_default" },
                {
                  label: t("pages.settings.channels.authorization.explicitServiceAllowlist", "Explicit service allowlist"),
                  value: "explicit_service_allowlist",
                },
              ]}
            />
          </ChannelField>
          <ChannelField label={t("pages.settings.channels.fields.authorizedServices", "Authorized services")}>
            <Select
              disabled={draft.authorizationMode !== "explicit_service_allowlist"}
              loading={servicesLoading}
              mode="multiple"
              options={serviceOptions}
              placeholder={t("pages.settings.channels.placeholders.verifiedServices", "Select verified NyxID services")}
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

        <ChannelField label={t("pages.settings.channels.fields.instructions", "Instructions")}>
          <Input.TextArea
            autoSize={{ minRows: 4, maxRows: 10 }}
            value={draft.instructions}
            onChange={(event) => onChange({ ...draft, instructions: event.target.value })}
          />
        </ChannelField>

        <div style={fieldGridStyle}>
          <ChannelField label={t("pages.settings.channels.fields.defaultSkillName", "Default skill name")}>
            <Input
              value={draft.defaultSkillName}
              onChange={(event) => onChange({ ...draft, defaultSkillName: event.target.value })}
            />
          </ChannelField>
          <ChannelField label={t("pages.settings.channels.fields.defaultSkillVersion", "Default skill version")}>
            <Input
              value={draft.defaultSkillVersion}
              onChange={(event) => onChange({ ...draft, defaultSkillVersion: event.target.value })}
            />
          </ChannelField>
        </div>

        <div style={fieldGridStyle}>
          <ChannelField label={t("pages.settings.channels.fields.toolSetRefs", "Tool set refs")}>
            <Input.TextArea
              autoSize={{ minRows: 3, maxRows: 8 }}
              value={draft.toolSetRefsText}
              onChange={(event) => onChange({ ...draft, toolSetRefsText: event.target.value })}
            />
          </ChannelField>
          <ChannelField label={t("pages.settings.channels.fields.extraToolNames", "Extra tool names")}>
            <Input.TextArea
              autoSize={{ minRows: 3, maxRows: 8 }}
              value={draft.extraToolNamesText}
              onChange={(event) => onChange({ ...draft, extraToolNamesText: event.target.value })}
            />
          </ChannelField>
        </div>

        <div style={fieldStackStyle}>
          <span style={fieldLabelStyle}>{t("pages.settings.channels.fields.nyxidServiceSelectors", "NyxID service selectors")}</span>
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
                  placeholder={t("pages.settings.channels.placeholders.service", "Service")}
                  value={selector.serviceSlug || undefined}
                  onChange={(serviceSlug) => {
                    const next = [...draft.selectors];
                    next[index] = { ...selector, serviceSlug };
                    onChange({ ...draft, selectors: next });
                  }}
                />
                <Input
                  placeholder={t("pages.settings.channels.placeholders.endpointNames", "Endpoint names")}
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
                  {t("pages.settings.channels.actions.remove", "Remove")}
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
              {t("pages.settings.channels.actions.addSelector", "Add selector")}
            </Button>
          </Space>
        </div>

        <Button
          icon={<SaveOutlined />}
          loading={saving}
          onClick={onSave}
          type="primary"
        >
          {t("pages.settings.channels.actions.saveRuntimeConfig", "Save runtime config")}
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
    return <Empty description={t("pages.settings.channels.empty.noRegistrations", "No channel registrations")} />;
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
                <Tag>{registration.platform || t("pages.settings.channels.status.unknown", "unknown")}</Tag>
                <Tag color={registration.agentKey.ready ? "success" : "warning"}>
                  {registration.agentKey.status}
                </Tag>
              </Space>
              <Typography.Text type="secondary">
                {registration.authorizationMode} · {summarizeRegistration(registration)}
              </Typography.Text>
              <Typography.Text type="secondary">
                {registration.defaultSkill.name || t("pages.settings.channels.empty.noDefaultSkill", "No default skill")}
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
  const [draftRevision, setDraftRevision] = React.useState(0);
  const draftRevisionRef = React.useRef(0);
  const saveTokenRef = React.useRef(0);
  const [pendingSave, setPendingSave] = React.useState<PendingChannelSettingsSave | null>(null);
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

  const handleDraftChange = React.useCallback((nextDraft: ChannelSettingsDraft) => {
    draftRevisionRef.current += 1;
    setDraftRevision(draftRevisionRef.current);
    setDraft(nextDraft);
  }, []);

  React.useEffect(() => () => {
    saveTokenRef.current += 1;
  }, []);

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
    const detail = detailQuery.data;
    if (!detail) {
      return;
    }

    if (
      pendingSave &&
      pendingSave.registrationId === detail.registrationId &&
      saveTokenRef.current === pendingSave.saveToken
    ) {
      if (detail.stateVersion <= pendingSave.baselineStateVersion) {
        setDraft((currentDraft) => currentDraft ?? pendingSave.submittedDraft);
        return;
      }

      setPendingSave(null);
      if (draftRevisionRef.current !== pendingSave.submittedRevision) {
        return;
      }
    }

    setFieldErrors([]);
    setDraft(createDraft(detail));
  }, [detailQuery.data, pendingSave]);

  const startPendingObservation = React.useCallback(
    (target: PendingChannelSettingsSave) => {
      let observedDetail: ChannelRuntimeConfigDetail | undefined;
      void observeUserLlmSave({
        saveToken: target.saveToken,
        isCurrent: (saveToken) => saveTokenRef.current === saveToken,
        read: () => channelSettingsApi.getRuntimeConfig(target.registrationId),
        isObserved: (detail) => {
          const observed = detail.stateVersion > target.baselineStateVersion;
          if (observed) {
            observedDetail = detail;
          }
          return observed;
        },
        onResponse: (detail) => {
          void queryClient.cancelQueries({
            exact: true,
            queryKey: detailQueryKey(target.registrationId),
          });
          queryClient.setQueryData(detailQueryKey(target.registrationId), detail);
        },
      }).then((result) => {
        if (saveTokenRef.current !== target.saveToken) {
          return;
        }

        if (result.phase === "accepted_unobserved") {
          setPendingSave((current) => current?.saveToken === target.saveToken
            ? { ...current, phase: "accepted_unobserved" }
            : current);
          return;
        }

        if (result.phase !== "observed") {
          return;
        }

        setPendingSave((current) => current?.saveToken === target.saveToken ? null : current);
        if (observedDetail && draftRevisionRef.current === target.submittedRevision) {
          setDraft(createDraft(observedDetail));
        }
        void queryClient.invalidateQueries({ queryKey: servicesQueryKey });
        void queryClient.invalidateQueries({ queryKey: listQueryKey });
      });
    },
    [queryClient],
  );

  const saveMutation = useMutation({
    mutationFn: async ({ pendingSave: target }: SaveRuntimeConfigRequest) => channelSettingsApi.saveRuntimeConfig({
      registrationId: target.registrationId,
      authorizationMode: target.submittedDraft.authorizationMode,
      serviceIds: target.submittedDraft.authorizationMode === "explicit_service_allowlist"
        ? target.submittedDraft.serviceIds
        : [],
      runtimeConfig: buildRuntimeConfig(target.submittedDraft),
    }),
    onError(error, request) {
      if (saveTokenRef.current === request.pendingSave.saveToken) {
        setPendingSave(null);
      }
      if (error instanceof ChannelSettingsApiError) {
        setFieldErrors(error.fieldErrors);
      }
    },
    onSuccess(_receipt, request) {
      const target = request.pendingSave;
      if (saveTokenRef.current !== target.saveToken) {
        return;
      }

      const acceptedTarget: PendingChannelSettingsSave = {
        ...target,
        phase: "accepted",
      };
      setFieldErrors([]);
      setDraft(target.submittedDraft);
      setPendingSave(acceptedTarget);
      startPendingObservation(acceptedTarget);
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
      title={t("pages.settings.channels.title", "Channel Settings")}
      content={t("pages.settings.channels.content", "Manage channel registration runtime configuration.")}
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
          {t("pages.settings.channels.actions.refresh", "Refresh")}
        </Button>
      }
    >
      {loadError ? (
        <Alert message={t("pages.settings.channels.errors.loadFailed", "Could not load channel settings")} type="error" showIcon />
      ) : null}
      <div style={gridStyle}>
        <div style={fieldStackStyle}>
          <Typography.Title level={4} style={{ margin: 0 }}>
            {t("pages.settings.channels.sections.registrations", "Registrations")}
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
                  {t("pages.settings.channels.actions.repairDelivery", "Repair delivery")}
                </Button>
                <Button
                  icon={<SendOutlined />}
                  loading={actionMutation.isPending}
                  onClick={() => actionMutation.mutate("test")}
                >
                  {t("pages.settings.channels.actions.testReply", "Test reply")}
                </Button>
                <Popconfirm
                  title={t("pages.settings.channels.confirm.deleteRegistration", "Delete registration?")}
                  onConfirm={() => actionMutation.mutate("delete")}
                >
                  <Button danger icon={<DeleteOutlined />} loading={actionMutation.isPending}>
                    {t("pages.settings.channels.actions.delete", "Delete")}
                  </Button>
                </Popconfirm>
              </Space>
              <ChannelRuntimeForm
                detail={detail}
                draft={draft}
                fieldErrors={fieldErrors}
                onChange={handleDraftChange}
                onSave={() => {
                  if (!draft || !detail) {
                    throw new Error(t("pages.settings.channels.errors.noSelectedRegistration", "No selected registration."));
                  }

                  const saveToken = saveTokenRef.current + 1;
                  saveTokenRef.current = saveToken;
                  const pendingSave: PendingChannelSettingsSave = {
                    baselineStateVersion: detail.stateVersion,
                    phase: "saving",
                    registrationId: detail.registrationId,
                    saveToken,
                    submittedDraft: draft,
                    submittedRevision: draftRevision,
                  };
                  setPendingSave(pendingSave);
                  saveMutation.mutate({ pendingSave });
                }}
                saving={saveMutation.isPending || pendingSave?.phase === "saving"}
                services={services}
                servicesLoading={servicesQuery.isLoading}
              />
            </>
          ) : (
            <Empty description={t("pages.settings.channels.empty.selectRegistration", "Select a channel registration")} />
          )}
        </div>
      </div>
    </SettingsPageShell>
  );
};

export default ChannelSettingsPage;
