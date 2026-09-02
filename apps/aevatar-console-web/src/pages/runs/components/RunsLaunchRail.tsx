import { ProCard, ProForm, ProFormSelect, ProFormText, ProFormTextArea } from "@ant-design/pro-components";
import type { ProFormInstance } from "@ant-design/pro-components";
import { Alert, Button, Collapse, Empty, Space, Tabs, Tag, Typography } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import { buildRuntimeExplorerHref } from "@/shared/navigation/runtimeRoutes";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { formatConsoleMessage, t } from "@/shared/i18n/messages";
import {
  type RunEndpointKind,
  normalizeRunEndpointKind,
  resolveRunEndpointId,
} from "@/shared/runs/endpointKinds";
import { cardStackStyle, embeddedPanelStyle, moduleCardProps, scrollPanelStyle } from "@/shared/ui/proComponents";
import type { RunTransport } from "../runEventPresentation";
import type {
  RecentRunTableRow,
  RunFormValues,
  RunPreset,
  RunReadinessSummary,
  SelectedRouteRecord,
} from "../runWorkbenchConfig";
import {
  formatRunRouteLabel,
  workbenchCardBodyStyle,
  workbenchCardStyle,
  workbenchScrollableBodyStyle,
} from "../runWorkbenchConfig";

type WorkflowOption = {
  label: string;
  value: string;
};

type RunsLaunchRailProps = {
  actorId?: string;
  catalogSearch: string;
  composerFormRef: React.RefObject<ProFormInstance<RunFormValues> | undefined>;
  draftMode?: boolean;
  activeEndpointId: string;
  activeEndpointKind: RunEndpointKind;
  initialFormValues: RunFormValues;
  recentRunRows: RecentRunTableRow[];
  selectedTransport: RunTransport;
  selectedRouteDetailsPrimitives: string[];
  selectedRouteRecord?: SelectedRouteRecord;
  showPromptField?: boolean;
  showSubmitActions?: boolean;
  streaming: boolean;
  submitPathLabel: string;
  transportOptions: Array<{ label: string; value: RunTransport }>;
  variant?: "default" | "chat";
  visiblePresets: RunPreset[];
  workflowCatalogLoading: boolean;
  routeOptions: WorkflowOption[];
  runReadiness?: RunReadinessSummary;
  onAbortRun: () => void;
  onCatalogSearchChange: (value: string) => void;
  onClearRecentRuns: () => void;
  onEndpointChange: (value: string) => void;
  onEndpointKindChange: (value: RunEndpointKind) => void;
  onSelectRouteName: (value: string) => void;
  onScopeIdChange: (value: string) => void;
  onSubmitRun: (values: RunFormValues) => Promise<void>;
  onTransportChange: (value: RunTransport) => void;
  onUsePreset: (preset: RunPreset) => void;
};

const compactStackStyle: React.CSSProperties = {
  ...cardStackStyle,
  gap: 12,
};

const quickGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 8,
  gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
};

const quickMetricStyle: React.CSSProperties = {
  background: "var(--ant-color-fill-quaternary)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 10,
  display: "flex",
  flexDirection: "column",
  gap: 4,
  minWidth: 0,
  padding: "10px 12px",
};

const quickMetricLabelStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 12,
  lineHeight: 1,
};

const quickMetricValueStyle: React.CSSProperties = {
  color: "var(--ant-color-text)",
  fontSize: 13,
  fontWeight: 600,
  lineHeight: 1.3,
};

const railListStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 10,
};

const railListItemStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  background: "var(--ant-color-fill-quaternary)",
  display: "flex",
  flexDirection: "column",
  gap: 10,
  padding: 14,
};

const railListHeaderStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  gap: 12,
  justifyContent: "space-between",
  width: "100%",
};

const railListContentStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: 8,
  minWidth: 0,
};

const railListActionStyle: React.CSSProperties = {
  display: "flex",
  flex: "0 0 auto",
  justifyContent: "flex-end",
};

const railTitleStyle: React.CSSProperties = {
  display: "block",
  lineHeight: 1.4,
  margin: 0,
  minWidth: 0,
  wordBreak: "normal",
};

const railMetaWrapStyle: React.CSSProperties = {
  display: "flex",
  flexWrap: "wrap",
  gap: 6,
};

const railDescriptionStyle: React.CSSProperties = {
  marginBottom: 0,
};

const readinessPanelStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  background: "linear-gradient(180deg, rgba(248, 250, 252, 0.96) 0%, rgba(255, 255, 255, 0.92) 100%)",
  display: "flex",
  flexDirection: "column",
  gap: 10,
  padding: 14,
};

const readinessHeaderStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  gap: 12,
  justifyContent: "space-between",
};

const readinessGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 8,
  gridTemplateColumns: "repeat(auto-fit, minmax(132px, 1fr))",
};

const readinessItemStyle: React.CSSProperties = {
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 10,
  display: "flex",
  flexDirection: "column",
  gap: 5,
  minWidth: 0,
  padding: "9px 10px",
};

const readinessLabelStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 11,
  lineHeight: 1,
};

const readinessValueStyle: React.CSSProperties = {
  color: "var(--ant-color-text)",
  fontSize: 13,
  fontWeight: 700,
  lineHeight: 1.25,
  overflow: "hidden",
  textOverflow: "ellipsis",
  whiteSpace: "nowrap",
};

const readinessHelperStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 12,
  lineHeight: 1.35,
  margin: 0,
};

function renderReadinessSummary(
  runReadiness?: RunReadinessSummary,
): React.ReactNode {
  if (!runReadiness) {
    return null;
  }

  return (
    <div style={readinessPanelStyle}>
      <div style={readinessHeaderStyle}>
        <div>
          <Typography.Text strong>
            {runReadiness.ready ? t("pages.runs.runslaunchrail.ready.to.send", "Ready to send") : t("pages.runs.runslaunchrail.send.readiness", "Send readiness")}
          </Typography.Text>
          <Typography.Paragraph style={readinessHelperStyle}>
            {runReadiness.ready
              ? t("pages.runs.runslaunchrail.prompt.runs.will.use.this.workspace", "Prompt runs will use this workspace context.")
              : runReadiness.blockingReason}
          </Typography.Paragraph>
        </div>
        <Tag color={runReadiness.ready ? "success" : "warning"}>
          {runReadiness.ready ? t("pages.runs.runslaunchrail.ready", "Ready") : t("pages.runs.runslaunchrail.blocked", "Blocked")}
        </Tag>
      </div>

      <div style={readinessGridStyle}>
        {runReadiness.items.map((item) => (
          <div key={item.key} style={readinessItemStyle}>
            <Typography.Text style={readinessLabelStyle}>
              {item.label}
            </Typography.Text>
            <Typography.Text title={item.value} style={readinessValueStyle}>
              {item.value}
            </Typography.Text>
            <Tag
              color={
                item.status === "ready"
                  ? "success"
                  : item.status === "required"
                    ? "warning"
                    : "default"
              }
            >
              {item.status === "ready"
                ? t("pages.runs.runslaunchrail.ready.2", "Ready")
                : item.status === "required"
                  ? t("pages.runs.runslaunchrail.required.2", "Required")
                  : t("pages.runs.runslaunchrail.context", "Context")}
            </Tag>
            <Typography.Paragraph style={readinessHelperStyle}>
              {item.helper}
            </Typography.Paragraph>
          </div>
        ))}
      </div>
    </div>
  );
}

function renderRouteMiniCard(
  activeEndpointId: string,
  activeEndpointKind: RunEndpointKind,
  selectedRouteDetailsPrimitives: string[],
  selectedRouteRecord?: SelectedRouteRecord,
): React.ReactNode {
  if (
    activeEndpointKind !== "chat" &&
    activeEndpointId &&
    !selectedRouteRecord
  ) {
    return (
      <div style={embeddedPanelStyle}>
        <Space wrap size={[6, 6]}>
          <Tag color="geekblue">{t("pages.runs.runslaunchrail.command.invoke.3", "Command invoke")}</Tag>
          <Tag>{t("pages.runs.runslaunchrail.workspace.binding.2", "Workspace binding")}</Tag>
        </Space>
        <Typography.Text strong style={{ display: "block", marginTop: 10 }}>
          {activeEndpointId}
        </Typography.Text>
        <Typography.Paragraph
          style={{ margin: "6px 0 0" }}
          type="secondary"
        >
          {t("pages.runs.runslaunchrail.invoke.the.selected.endpoint.with.2", "Invoke the selected endpoint with explicit protobuf bytes, or let the workbench derive bytes only for StringValue and AppScriptCommand payloads.")}</Typography.Paragraph>
      </div>
    );
  }

  if (!selectedRouteRecord) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description={t("pages.runs.runslaunchrail.select.route.preview.or.endpoint.2", "Select a route preview or endpoint to inspect the current route.")}
      />
    );
  }

  return (
    <div style={embeddedPanelStyle}>
      <Space wrap size={[6, 6]}>
        <Tag color={activeEndpointKind === "chat" ? "processing" : "geekblue"}>
          {activeEndpointKind === "chat" ? t("pages.runs.runslaunchrail.service.sse.2", "Service SSE") : t("pages.runs.runslaunchrail.command.invoke.5", "Command invoke")}
        </Tag>
        <Tag>{selectedRouteRecord.groupLabel}</Tag>
        <Tag>{selectedRouteRecord.sourceLabel}</Tag>
        <Tag color={selectedRouteRecord.llmStatus === "processing" ? "blue" : "success"}>
          {selectedRouteRecord.llmStatus === "processing"
            ? t("pages.runs.runslaunchrail.llm.required.2", "LLM required")
            : t("pages.runs.runslaunchrail.llm.optional.2", "LLM optional")}
        </Tag>
      </Space>
      <Typography.Text strong style={{ display: "block", marginTop: 10 }}>
        {selectedRouteRecord.routeName}
      </Typography.Text>
      <Typography.Paragraph
        ellipsis={{ rows: 2, expandable: true, symbol: "more" }}
        style={{ margin: "6px 0 0" }}
        type="secondary"
      >
        {selectedRouteRecord.description || t("pages.runs.runslaunchrail.no.description.provided.2", "No description provided.")}
      </Typography.Paragraph>
      <Space wrap size={[6, 6]}>
        {selectedRouteDetailsPrimitives.slice(0, 3).map((primitive) => (
          <Tag key={primitive}>{primitive}</Tag>
        ))}
        {selectedRouteDetailsPrimitives.length > 3 ? (
          <Tag>+{selectedRouteDetailsPrimitives.length - 3} {t("pages.runs.runslaunchrail.more.3", "more")}</Tag>
        ) : null}
      </Space>
    </div>
  );
}

function resolveRunTargetSummary(input: {
  activeEndpointId: string;
  activeEndpointKind: RunEndpointKind;
  actorId?: string;
  draftMode: boolean;
  initialFormValues: RunFormValues;
  selectedRouteRecord?: SelectedRouteRecord;
}): {
  description: string;
  mode: string;
  required: string;
  target: string;
} {
  const workspaceId = input.initialFormValues.scopeId || "Workspace not set";
  const routeLabel =
    input.selectedRouteRecord?.routeName ||
    input.initialFormValues.routeName ||
    "";
  const endpointLabel = resolveRunEndpointId(
    input.activeEndpointKind,
    input.activeEndpointId || input.initialFormValues.endpointId,
  );
  const target = routeLabel || endpointLabel || "chat";

  if (input.draftMode) {
    return {
      description: t("pages.runs.runslaunchrail.will.run.the.bundled.studio.draft", "{value1} will run the bundled Studio draft before it is published.", { value1: workspaceId }),
      mode: "Studio draft",
      required: "Workspace, draft bundle, prompt",
      target,
    };
  }

  if (input.actorId) {
    return {
      description: t("pages.runs.runslaunchrail.continue.actor.in", "Continue actor {value1} in {value2}.", { value1: input.actorId, value2: workspaceId }),
      mode: "Existing actor",
      required: "Workspace, actor, prompt",
      target,
    };
  }

  const isChatEndpoint = input.activeEndpointKind === "chat";

  return {
    description:
      isChatEndpoint
        ? `${workspaceId} will stream through the selected chat route or workspace default binding.`
        : `${workspaceId} will invoke the selected command endpoint with the current payload.`,
    mode: isChatEndpoint ? "Chat stream" : "Command invoke",
    required: isChatEndpoint
      ? "Workspace, route/default binding, prompt"
      : "Workspace, endpoint, prompt or payload",
    target,
  };
}

function renderRecentRunCards(
  recentRunRows: RecentRunTableRow[],
  onClearRecentRuns: () => void,
): React.ReactNode {
  if (recentRunRows.length === 0) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description={t("pages.runs.runslaunchrail.no.local.runs.have.been.2", "No local runs have been recorded yet.")}
      />
    );
  }

  return (
    <div style={compactStackStyle}>
      <div style={railListStyle}>
        {recentRunRows.map((record) => (
          <div key={record.key} style={railListItemStyle}>
            <div style={railListHeaderStyle}>
              <div style={railListContentStyle}>
                <Typography.Text strong style={railTitleStyle}>
                  {formatRunRouteLabel(
                    record.routeName,
                    record.endpointId,
                    record.endpointKind,
                  )}
                </Typography.Text>
                <div style={railMetaWrapStyle}>
                  <Tag
                    color={
                      record.statusValue === "finished"
                        ? "success"
                        : record.statusValue === "running"
                          ? "processing"
                          : record.statusValue === "error"
                            ? "error"
                            : "default"
                    }
                  >
                    {record.statusValue}
                  </Tag>
                  <Tag>
                    {resolveRunEndpointId(record.endpointKind, record.endpointId)}
                  </Tag>
                  <Tag>{formatDateTime(record.recordedAt)}</Tag>
                  <Tag>
                    {record.runId
                      ? t("pages.runs.runslaunchrail.run.ready", "Run ready")
                      : t("pages.runs.runslaunchrail.no.run", "No run")}
                  </Tag>
                </div>
              </div>
            </div>

            <Typography.Paragraph
              ellipsis={{ rows: 2, expandable: true, symbol: "more" }}
              style={railDescriptionStyle}
              type="secondary"
            >
              {record.lastMessagePreview ||
                record.prompt ||
                t("pages.runs.runslaunchrail.no.preview.recorded.2", "No preview recorded.")}
            </Typography.Paragraph>

            <div style={railListActionStyle}>
              <Space wrap size={[8, 8]}>
                <Button type="link" onClick={() => record.onRestore?.()}>
                  {t("pages.runs.runslaunchrail.restore.2", "Restore")}</Button>
                {record.actorId ? (
                  <Button type="link" onClick={() => record.onOpenActor?.()}>
                    {t("pages.runs.runslaunchrail.actor.2", "Actor")}</Button>
                ) : null}
              </Space>
            </div>
          </div>
        ))}
      </div>

      <Space>
        <Button danger onClick={onClearRecentRuns}>
          {t("pages.runs.runslaunchrail.clear.local.runs.2", "Clear local runs")}</Button>
      </Space>
    </div>
  );
}

function renderPresetCards(
  visiblePresets: RunPreset[],
  onUsePreset: (preset: RunPreset) => void,
): React.ReactNode {
  if (visiblePresets.length === 0) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description={t("pages.runs.runslaunchrail.no.presets.are.currently.available.2", "No presets are currently available.")}
      />
    );
  }

  return (
    <div style={railListStyle}>
      {visiblePresets.map((record) => (
        <div key={record.key} style={railListItemStyle}>
          <div style={railListHeaderStyle}>
            <div style={railListContentStyle}>
              <Typography.Text strong style={railTitleStyle}>
                {formatConsoleMessage(record.title)}
              </Typography.Text>
              <div style={railMetaWrapStyle}>
                <Tag color="processing">{record.routeName}</Tag>
                {record.tags.slice(0, 2).map((tag) => (
                  <Tag key={`${record.key}-${tag}`}>{tag}</Tag>
                ))}
                {record.tags.length > 2 ? (
                  <Tag>+{record.tags.length - 2} {t("pages.runs.runslaunchrail.more.4", "more")}</Tag>
                ) : null}
              </div>
            </div>
          </div>

          <Typography.Paragraph
            ellipsis={{ rows: 2, expandable: true, symbol: "more" }}
            style={railDescriptionStyle}
            type="secondary"
          >
            {formatConsoleMessage(record.description)}
          </Typography.Paragraph>

          <div style={railListActionStyle}>
            <Button type="link" onClick={() => onUsePreset(record)}>
              {t("pages.runs.runslaunchrail.use.preset.2", "Use preset")}</Button>
          </div>
        </div>
      ))}
    </div>
  );
}

const RunsLaunchRail: React.FC<RunsLaunchRailProps> = ({
  actorId,
  catalogSearch,
  composerFormRef,
  draftMode = false,
  activeEndpointId,
  activeEndpointKind,
  initialFormValues,
  recentRunRows,
  selectedRouteDetailsPrimitives,
  selectedRouteRecord,
  showPromptField = true,
  showSubmitActions = true,
  streaming,
  submitPathLabel,
  transportOptions,
  variant = "default",
  visiblePresets,
  workflowCatalogLoading,
  routeOptions,
  runReadiness,
  onAbortRun,
  onCatalogSearchChange,
  onClearRecentRuns,
  onEndpointChange,
  onEndpointKindChange,
  onSelectRouteName,
  onScopeIdChange,
  onSubmitRun,
  onTransportChange,
  onUsePreset,
}) => {
  const isChatEndpoint = activeEndpointKind === "chat";
  const isChatVariant = variant === "chat";
  const runTargetSummary = resolveRunTargetSummary({
    activeEndpointId,
    activeEndpointKind,
    actorId,
    draftMode,
    initialFormValues,
    selectedRouteRecord,
  });

  return (
    <ProCard
      title={isChatVariant ? "Run context" : "Run setup"}
      hoverable
      {...moduleCardProps}
      style={workbenchCardStyle}
      bodyStyle={workbenchCardBodyStyle}
    >
      <div style={workbenchScrollableBodyStyle}>
        <div style={compactStackStyle}>
          {!isChatVariant ? (
            <div style={compactStackStyle}>
              <Alert
                showIcon
                type="info"
                message={`Target: ${runTargetSummary.target}`}
                description={runTargetSummary.description}
              />
              <div style={quickGridStyle}>
                <div style={quickMetricStyle}>
                  <Typography.Text style={quickMetricLabelStyle}>{t("pages.runs.runslaunchrail.required", "Required")}</Typography.Text>
                  <Typography.Text style={quickMetricValueStyle}>
                    {runTargetSummary.required}
                  </Typography.Text>
                </div>
                <div style={quickMetricStyle}>
                  <Typography.Text style={quickMetricLabelStyle}>{t("pages.runs.runslaunchrail.mode.2", "Mode")}</Typography.Text>
                  <Typography.Text style={quickMetricValueStyle}>
                    {runTargetSummary.mode}
                  </Typography.Text>
                </div>
                <div style={quickMetricStyle}>
                  <Typography.Text style={quickMetricLabelStyle}>{t("pages.runs.runslaunchrail.endpoint.3", "Endpoint")}</Typography.Text>
                  <Typography.Text style={quickMetricValueStyle}>
                    {activeEndpointId || "chat"}
                  </Typography.Text>
                </div>
                <div style={quickMetricStyle}>
                  <Typography.Text style={quickMetricLabelStyle}>{t("pages.runs.runslaunchrail.presets.2", "Presets")}</Typography.Text>
                  <Typography.Text style={quickMetricValueStyle}>
                    {visiblePresets.length}
                  </Typography.Text>
                </div>
              </div>
            </div>
          ) : null}

        <Tabs
          items={[
            {
              key: "compose",
              label: isChatVariant ? "Context" : "Compose",
              children: (
                <div style={compactStackStyle}>
                  {isChatVariant ? renderReadinessSummary(runReadiness) : null}

                  {renderRouteMiniCard(
                    activeEndpointId,
                    activeEndpointKind,
                    selectedRouteDetailsPrimitives,
                    selectedRouteRecord,
                  )}

                  <ProForm<RunFormValues>
                    formRef={composerFormRef}
                    layout="vertical"
                    initialValues={initialFormValues}
                    onValuesChange={(changedValues, values) => {
                      if ("routeName" in changedValues) {
                        onSelectRouteName(
                          typeof values.routeName === "string"
                            ? values.routeName
                            : "",
                        );
                      }
                      if (
                        "endpointId" in changedValues ||
                        "endpointKind" in changedValues
                      ) {
                        const nextEndpointKind = normalizeRunEndpointKind(
                          values.endpointKind,
                          values.endpointId,
                        );
                        onEndpointKindChange(nextEndpointKind);
                        onEndpointChange(
                          resolveRunEndpointId(
                            nextEndpointKind,
                            values.endpointId,
                          ),
                        );
                      }
                      if (
                        "transport" in changedValues &&
                        values.transport
                      ) {
                        onTransportChange(values.transport);
                      }
                      if ("scopeId" in changedValues) {
                        onScopeIdChange(
                          typeof values.scopeId === "string"
                            ? values.scopeId
                            : "",
                        );
                      }
                    }}
                    onFinish={async (values) => {
                      await onSubmitRun(values);
                      return true;
                    }}
                    submitter={
                      showSubmitActions
                        ? {
                            render: (props) => (
                              <Space wrap>
                                <Button
                                  type="primary"
                                  loading={streaming}
                                  onClick={() => props.form?.submit?.()}
                                >
                                  {t("pages.runs.runslaunchrail.start.run.2", "Start run")}</Button>
                                <Button onClick={onAbortRun} disabled={!streaming}>
                                  {t("pages.runs.runslaunchrail.abort.2", "Abort")}</Button>
                                {actorId ? (
                                  <Button
                                    onClick={() =>
                                      history.push(
                                        buildRuntimeExplorerHref({
                                          actorId,
                                        }),
                                      )
                                    }
                                  >
                                    {t("pages.runs.runslaunchrail.actor.explorer.2", "Actor explorer")}</Button>
                                ) : null}
                              </Space>
                            ),
                          }
                        : false
                    }
                  >
                    {showPromptField ? (
                      <ProFormTextArea
                        name="prompt"
                        label={isChatEndpoint ? "Prompt" : "Payload text"}
                        fieldProps={{ rows: 5 }}
                        placeholder={
                          isChatEndpoint
                            ? "Describe the task to run."
                            : "Provide the payload text that should be encoded for this endpoint."
                        }
                        rules={[
                          {
                            required: true,
                            message: t("pages.runs.runslaunchrail.prompt.is.required.2", "Prompt is required."),
                          },
                        ]}
                      />
                    ) : (
                      <ProFormTextArea hidden name="prompt" />
                    )}
                    {!isChatVariant ? (
                      <ProFormSelect<RunTransport>
                        name="transport"
                        label="Transport"
                        options={transportOptions}
                        rules={[
                          {
                            required: true,
                            message: t("pages.runs.runslaunchrail.transport.is.required.2", "Transport is required."),
                          },
                        ]}
                      />
                    ) : null}
                    {!draftMode && !isChatVariant ? (
                      <ProFormSelect<RunEndpointKind>
                        name="endpointKind"
                        label={t("pages.runs.runslaunchrail.endpoint.kind.2", "Endpoint kind")}
                        options={[
                          { label: t("pages.runs.runslaunchrail.chat.stream.2", "Chat stream"), value: "chat" },
                          { label: t("pages.runs.runslaunchrail.command.invoke.4", "Command invoke"), value: "command" },
                        ]}
                        extra={t("pages.runs.runslaunchrail.chat.endpoints.keep.the.service.2", "Chat endpoints keep the service streaming path even when the endpoint id is custom.")}
                        rules={[
                          {
                            required: true,
                            message: t("pages.runs.runslaunchrail.endpoint.kind.is.required.2", "Endpoint kind is required."),
                          },
                        ]}
                      />
                    ) : null}
                    {isChatEndpoint ? (
                      <ProFormSelect
                        name="routeName"
                        label={
                          draftMode
                            ? "Draft bundle"
                            : "Chat route (optional)"
                        }
                        placeholder={
                          draftMode
                            ? "Studio draft bundle"
                            : "Preview a chat route"
                        }
                        extra={
                          draftMode
                            ? "Draft runs execute the bundled Studio draft."
                            : "Selecting a route targets the published workspace service with the same id. Leave it empty to use the workspace default binding; binding override wins when provided."
                        }
                        disabled={draftMode}
                        options={routeOptions}
                        fieldProps={{
                          allowClear: true,
                          showSearch: true,
                          filterOption: false,
                          onSearch: onCatalogSearchChange,
                          notFoundContent: workflowCatalogLoading ? (
                            <Typography.Text type="secondary">
                              {t("pages.runs.runslaunchrail.loading.chat.routes.2", "Loading chat routes...")}</Typography.Text>
                          ) : (
                            <Empty
                              image={Empty.PRESENTED_IMAGE_SIMPLE}
                              description={t("pages.runs.runslaunchrail.no.chat.routes.available.2", "No chat routes available.")}
                            />
                          ),
                          searchValue: catalogSearch,
                        }}
                      />
                    ) : (
                      <Alert
                        showIcon
                        type="info"
                        title={t("pages.runs.runslaunchrail.generic.endpoint.invoke.2", "Generic endpoint invoke")}
                        description={t("pages.runs.runslaunchrail.use.the.prompt.as.the.2", "Use the prompt as the default payload text, or provide an explicit type URL and protobuf base64 payload.")}
                      />
                    )}
                    <ProFormText
                      name="scopeId"
                      label={t("pages.runs.runslaunchrail.workspace.id.2", "Workspace ID")}
                      placeholder={t("pages.runs.runslaunchrail.nyxid.user.workspace.id.2", "NyxID user / workspace id")}
                      rules={[
                        {
                          required: true,
                          message: t("pages.runs.runslaunchrail.workspace.id.is.required.2", "Workspace ID is required."),
                        },
                      ]}
                    />
                    {isChatVariant ? (
                      <Collapse
                        ghost
                        items={[
                          {
                            key: "advanced",
                            label: t("pages.runs.runslaunchrail.advanced.payload.and.transport", "Advanced payload and transport"),
                            children: (
                              <div style={compactStackStyle}>
                                <ProFormText
                                  name="endpointId"
                                  label="Endpoint"
                                  placeholder={t("pages.runs.runslaunchrail.chat.or.custom.chat.endpoint.2", "chat (or a custom chat endpoint id)")}
                                  disabled={draftMode}
                                />
                                {!draftMode ? (
                                  <ProFormText
                                    name="serviceOverrideId"
                                    label={t("pages.runs.runslaunchrail.binding.override.optional.3", "Binding override (optional)")}
                                    placeholder={t("pages.runs.runslaunchrail.leave.empty.to.use.the.3", "Leave empty to use the workspace default binding.")}
                                  />
                                ) : null}
                                {isChatEndpoint ? (
                                  <ProFormText
                                    name="actorId"
                                    label={t("pages.runs.runslaunchrail.existing.actor.id.3", "Existing actor ID")}
                                    placeholder="Actor:..."
                                    disabled={draftMode}
                                  />
                                ) : null}
                                <ProFormText
                                  name="payloadTypeUrl"
                                  label={t("pages.runs.runslaunchrail.payload.type.url.3", "Payload type URL")}
                                  placeholder="type.googleapis.com/google.protobuf.StringValue"
                                  extra={t("pages.runs.runslaunchrail.when.payload.base64.is.empty.3", "When payload base64 is empty, the workbench only auto-encodes StringValue and AppScriptCommand.")}
                                />
                                <ProFormTextArea
                                  name="payloadBase64"
                                  label={t("pages.runs.runslaunchrail.payload.base64.advanced.3", "Payload base64 (advanced)")}
                                  fieldProps={{ rows: 3 }}
                                  placeholder={t("pages.runs.runslaunchrail.required.for.custom.payload.types.3", "Required for custom payload types; leave empty only for StringValue or AppScriptCommand.")}
                                />
                              </div>
                            ),
                          },
                        ]}
                      />
                    ) : (
                      <Collapse
                        ghost
                        items={[
                          {
                            key: "advanced",
                            label: t("pages.runs.runslaunchrail.advanced.endpoint.and.payload.options", "Advanced endpoint and payload options"),
                            children: (
                              <div style={compactStackStyle}>
                                <ProFormText
                                  name="endpointId"
                                  label="Endpoint"
                                  placeholder={
                                    isChatEndpoint
                                      ? "chat (or a custom chat endpoint id)"
                                      : "endpoint-id"
                                  }
                                  disabled={draftMode}
                                  rules={[
                                    {
                                      required: !draftMode && !isChatEndpoint,
                                      message: t("pages.runs.runslaunchrail.endpoint.id.is.required.for.2", "Endpoint ID is required for command invokes."),
                                    },
                                  ]}
                                />
                                {!draftMode ? (
                                  <ProFormText
                                    name="serviceOverrideId"
                                    label={t("pages.runs.runslaunchrail.binding.override.optional.4", "Binding override (optional)")}
                                    placeholder={t("pages.runs.runslaunchrail.leave.empty.to.use.the.4", "Leave empty to use the workspace default binding.")}
                                  />
                                ) : null}
                                {isChatEndpoint ? (
                                  <ProFormText
                                    name="actorId"
                                    label={t("pages.runs.runslaunchrail.existing.actor.id.4", "Existing actor ID")}
                                    placeholder="Actor:..."
                                    disabled={draftMode}
                                  />
                                ) : null}
                                <ProFormText
                                  name="payloadTypeUrl"
                                  label={t("pages.runs.runslaunchrail.payload.type.url.4", "Payload type URL")}
                                  placeholder="type.googleapis.com/google.protobuf.StringValue"
                                  extra={t("pages.runs.runslaunchrail.when.payload.base64.is.empty.4", "When payload base64 is empty, the workbench only auto-encodes StringValue and AppScriptCommand.")}
                                />
                                <ProFormTextArea
                                  name="payloadBase64"
                                  label={t("pages.runs.runslaunchrail.payload.base64.advanced.4", "Payload base64 (advanced)")}
                                  fieldProps={{ rows: 3 }}
                                  placeholder={t("pages.runs.runslaunchrail.required.for.custom.payload.types.4", "Required for custom payload types; leave empty only for StringValue or AppScriptCommand.")}
                                />
                              </div>
                            ),
                          },
                        ]}
                      />
                    )}
                  </ProForm>
                </div>
              ),
            },
            {
              key: "recent",
              label: t("pages.runs.runslaunchrail.recent", "Recent ({value1})", { value1: recentRunRows.length }),
              children: renderRecentRunCards(
                recentRunRows,
                onClearRecentRuns,
              ),
            },
            {
              key: "presets",
              label: t("pages.runs.runslaunchrail.presets.3", "Presets ({value1})", { value1: visiblePresets.length }),
              children: (
                <div style={scrollPanelStyle}>
                  {renderPresetCards(visiblePresets, onUsePreset)}
                </div>
              ),
              disabled: !isChatEndpoint,
            },
          ]}
        />

        {!isChatVariant ? (
          <Alert
            showIcon
            type="info"
            title={`Requests go through ${submitPathLabel}`}
          />
        ) : null}
        </div>
      </div>
    </ProCard>
  );
};

export default RunsLaunchRail;
