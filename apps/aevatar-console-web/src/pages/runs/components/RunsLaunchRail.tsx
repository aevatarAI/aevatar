import { ProCard, ProForm, ProFormSelect, ProFormText, ProFormTextArea } from "@ant-design/pro-components";
import type { ProFormInstance } from "@ant-design/pro-components";
import { Alert, Button, Collapse, Empty, Space, Tabs, Tag, Typography } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import { buildRuntimeExplorerHref } from "@/shared/navigation/runtimeRoutes";
import { formatDateTime } from "@/shared/datetime/dateTime";
import {
  type RunEndpointKind,
  normalizeRunEndpointKind,
  resolveRunEndpointId,
} from "@/shared/runs/endpointKinds";
import { cardStackStyle, embeddedPanelStyle, moduleCardProps, scrollPanelStyle } from "@/shared/ui/proComponents";
import type { RunTransport } from "../runEventPresentation";
import type { RecentRunTableRow, RunFormValues, RunPreset, SelectedRouteRecord } from "../runWorkbenchConfig";
import {
  formatRunRouteLabel,
  workbenchCardBodyStyle,
  workbenchCardStyle,
  workbenchScrollableBodyStyle,
} from "../runWorkbenchConfig";
import { formatConsoleMessage, t } from "@/shared/i18n/messages";

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
  onAbortRun: () => void;
  onCatalogSearchChange: (value: string) => void;
  onClearRecentRuns: () => void;
  onEndpointChange: (value: string) => void;
  onEndpointKindChange: (value: RunEndpointKind) => void;
  onSelectRouteName: (value: string) => void;
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
          <Tag color="geekblue">{t("pages.runs.runslaunchrail.command.invoke", "Command invoke")}</Tag>
          <Tag>{t("pages.runs.runslaunchrail.workspace.binding", "Workspace binding")}</Tag>
        </Space>
        <Typography.Text strong style={{ display: "block", marginTop: 10 }}>
          {activeEndpointId}
        </Typography.Text>
        <Typography.Paragraph
          style={{ margin: "6px 0 0" }}
          type="secondary"
        >
          {t("pages.runs.runslaunchrail.invoke.the.selected.endpoint.with", "Invoke the selected endpoint with explicit protobuf bytes, or let the workbench derive bytes only for StringValue and AppScriptCommand payloads.")}</Typography.Paragraph>
      </div>
    );
  }

  if (!selectedRouteRecord) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description={t("pages.runs.runslaunchrail.select.route.preview.or.endpoint", "Select a route preview or endpoint to inspect the current route.")}
      />
    );
  }

  return (
    <div style={embeddedPanelStyle}>
      <Space wrap size={[6, 6]}>
        <Tag color={activeEndpointKind === "chat" ? "processing" : "geekblue"}>
          {activeEndpointKind === "chat"
            ? t("pages.runs.runslaunchrail.service.sse", "Service SSE")
            : t("pages.runs.runslaunchrail.command.invoke", "Command invoke")}
        </Tag>
        <Tag>{selectedRouteRecord.groupLabel}</Tag>
        <Tag>{selectedRouteRecord.sourceLabel}</Tag>
        <Tag color={selectedRouteRecord.llmStatus === "processing" ? "blue" : "success"}>
          {selectedRouteRecord.llmStatus === "processing"
            ? t("pages.runs.runslaunchrail.llm.required", "LLM required")
            : t("pages.runs.runslaunchrail.llm.optional", "LLM optional")}
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
        {selectedRouteRecord.description ||
          t("pages.runs.runslaunchrail.no.description.provided", "No description provided.")}
      </Typography.Paragraph>
      <Space wrap size={[6, 6]}>
        {selectedRouteDetailsPrimitives.slice(0, 3).map((primitive) => (
          <Tag key={primitive}>{primitive}</Tag>
        ))}
        {selectedRouteDetailsPrimitives.length > 3 ? (
          <Tag>+{selectedRouteDetailsPrimitives.length - 3} {t("pages.runs.runslaunchrail.more", "more")}</Tag>
        ) : null}
      </Space>
    </div>
  );
}

function renderRecentRunCards(
  recentRunRows: RecentRunTableRow[],
  onClearRecentRuns: () => void,
): React.ReactNode {
  if (recentRunRows.length === 0) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description={t("pages.runs.runslaunchrail.no.local.runs.have.been", "No local runs have been recorded yet.")}
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
                  <Tag>{record.runId || t("pages.runs.runslaunchrail.no.run.id", "No run ID")}</Tag>
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
                t("pages.runs.runslaunchrail.no.preview.recorded", "No preview recorded.")}
            </Typography.Paragraph>

            <div style={railListActionStyle}>
              <Space wrap size={[8, 8]}>
                <Button type="link" onClick={() => record.onRestore?.()}>
                  {t("pages.runs.runslaunchrail.restore", "Restore")}</Button>
                {record.actorId ? (
                  <Button type="link" onClick={() => record.onOpenActor?.()}>
                    {t("pages.runs.runslaunchrail.actor", "Actor")}</Button>
                ) : null}
              </Space>
            </div>
          </div>
        ))}
      </div>

      <Space>
        <Button danger onClick={onClearRecentRuns}>
          {t("pages.runs.runslaunchrail.clear.local.runs", "Clear local runs")}</Button>
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
        description={t("pages.runs.runslaunchrail.no.presets.are.currently.available", "No presets are currently available.")}
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
                  <Tag>+{record.tags.length - 2} {t("pages.runs.runslaunchrail.more.2", "more")}</Tag>
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
              {t("pages.runs.runslaunchrail.use.preset", "Use preset")}</Button>
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
  onAbortRun,
  onCatalogSearchChange,
  onClearRecentRuns,
  onEndpointChange,
  onEndpointKindChange,
  onSelectRouteName,
  onSubmitRun,
  onTransportChange,
  onUsePreset,
}) => {
  const isChatEndpoint = activeEndpointKind === "chat";
  const isChatVariant = variant === "chat";

  return (
    <ProCard
      title={
        isChatVariant
          ? t("pages.runs.runslaunchrail.setup", "Setup")
          : t("pages.runs.runslaunchrail.run.setup", "Run setup")
      }
      hoverable
      {...moduleCardProps}
      style={workbenchCardStyle}
      bodyStyle={workbenchCardBodyStyle}
    >
      <div style={workbenchScrollableBodyStyle}>
        <div style={compactStackStyle}>
          {!isChatVariant ? (
            <div style={quickGridStyle}>
              <div style={quickMetricStyle}>
                <Typography.Text style={quickMetricLabelStyle}>{t("pages.runs.runslaunchrail.endpoint", "Endpoint")}</Typography.Text>
                <Typography.Text style={quickMetricValueStyle}>
                  {activeEndpointId || "chat"}
                </Typography.Text>
              </div>
              <div style={quickMetricStyle}>
                <Typography.Text style={quickMetricLabelStyle}>{t("pages.runs.runslaunchrail.execution", "Execution")}</Typography.Text>
                <Typography.Text style={quickMetricValueStyle}>
                  {isChatEndpoint
                    ? t("pages.runs.runslaunchrail.stream", "STREAM")
                    : t("pages.runs.runslaunchrail.invoke", "INVOKE")}
                </Typography.Text>
              </div>
              <div style={quickMetricStyle}>
                <Typography.Text style={quickMetricLabelStyle}>{t("pages.runs.runslaunchrail.mode", "Mode")}</Typography.Text>
                <Typography.Text style={quickMetricValueStyle}>
                  {draftMode
                    ? isChatEndpoint
                      ? t("pages.runs.runslaunchrail.draft.run", "Draft run")
                      : t("pages.runs.runslaunchrail.prepared.invoke", "Prepared invoke")
                    : actorId
                      ? t("pages.runs.runslaunchrail.continue.actor", "Continue actor")
                      : t("pages.runs.runslaunchrail.endpoint.invoke", "Endpoint invoke")}
                </Typography.Text>
              </div>
              <div style={quickMetricStyle}>
                <Typography.Text style={quickMetricLabelStyle}>{t("pages.runs.runslaunchrail.presets", "Presets")}</Typography.Text>
                <Typography.Text style={quickMetricValueStyle}>
                  {visiblePresets.length}
                </Typography.Text>
              </div>
            </div>
          ) : null}

        <Tabs
          items={[
            {
              key: "compose",
              label: t("pages.runs.runslaunchrail.compose", "Compose"),
              children: (
                <div style={compactStackStyle}>
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
                                  {t("pages.runs.runslaunchrail.start.run", "Start run")}</Button>
                                <Button onClick={onAbortRun} disabled={!streaming}>
                                  {t("pages.runs.runslaunchrail.abort", "Abort")}</Button>
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
                                    {t("pages.runs.runslaunchrail.actor.explorer", "Actor explorer")}</Button>
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
                        label={
                          isChatEndpoint
                            ? t("pages.runs.runslaunchrail.prompt", "Prompt")
                            : t("pages.runs.runslaunchrail.payload.text", "Payload text")
                        }
                        fieldProps={{ rows: 5 }}
                        placeholder={
                          isChatEndpoint
                            ? t("pages.runs.runslaunchrail.describe.the.task.to.run", "Describe the task to run.")
                            : t("pages.runs.runslaunchrail.provide.payload.text", "Provide the payload text that should be encoded for this endpoint.")
                        }
                        rules={[
                          {
                            required: true,
                            message: t("pages.runs.runslaunchrail.prompt.is.required", "Prompt is required."),
                          },
                        ]}
                      />
                    ) : (
                      <ProFormTextArea hidden name="prompt" />
                    )}
                    {!isChatVariant ? (
                      <ProFormSelect<RunTransport>
                        name="transport"
                        label={t("pages.runs.runslaunchrail.transport", "Transport")}
                        options={transportOptions}
                        rules={[
                          {
                            required: true,
                            message: t("pages.runs.runslaunchrail.transport.is.required", "Transport is required."),
                          },
                        ]}
                      />
                    ) : null}
                    {!draftMode && !isChatVariant ? (
                      <ProFormSelect<RunEndpointKind>
                        name="endpointKind"
                        label={t("pages.runs.runslaunchrail.endpoint.kind", "Endpoint kind")}
                        options={[
                          { label: t("pages.runs.runslaunchrail.chat.stream", "Chat stream"), value: "chat" },
                          { label: t("pages.runs.runslaunchrail.command.invoke.2", "Command invoke"), value: "command" },
                        ]}
                        extra={t("pages.runs.runslaunchrail.chat.endpoints.keep.the.service", "Chat endpoints keep the service streaming path even when the endpoint id is custom.")}
                        rules={[
                          {
                            required: true,
                            message: t("pages.runs.runslaunchrail.endpoint.kind.is.required", "Endpoint kind is required."),
                          },
                        ]}
                      />
                    ) : null}
                    {isChatEndpoint ? (
                      <ProFormSelect
                        name="routeName"
                        label={
                          draftMode
                            ? t("pages.runs.runslaunchrail.draft.bundle", "Draft bundle")
                            : t("pages.runs.runslaunchrail.chat.route.optional", "Chat route (optional)")
                        }
                        placeholder={
                          draftMode
                            ? t("pages.runs.runslaunchrail.studio.draft.bundle", "Studio draft bundle")
                            : t("pages.runs.runslaunchrail.preview.chat.route", "Preview a chat route")
                        }
                        extra={
                          draftMode
                            ? t("pages.runs.runslaunchrail.draft.runs.execute.bundle", "Draft runs execute the bundled Studio draft.")
                            : t("pages.runs.runslaunchrail.selecting.route.targets.service", "Selecting a route targets the published workspace service with the same id. Leave it empty to use the workspace default binding; binding override wins when provided.")
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
                              {t("pages.runs.runslaunchrail.loading.chat.routes", "Loading chat routes...")}</Typography.Text>
                          ) : (
                            <Empty
                              image={Empty.PRESENTED_IMAGE_SIMPLE}
                              description={t("pages.runs.runslaunchrail.no.chat.routes.available", "No chat routes available.")}
                            />
                          ),
                          searchValue: catalogSearch,
                        }}
                      />
                    ) : (
                      <Alert
                        showIcon
                        type="info"
                        title={t("pages.runs.runslaunchrail.generic.endpoint.invoke", "Generic endpoint invoke")}
                        description={t("pages.runs.runslaunchrail.use.the.prompt.as.the", "Use the prompt as the default payload text, or provide an explicit type URL and protobuf base64 payload.")}
                      />
                    )}
                    <ProFormText
                      name="scopeId"
                      label={t("pages.runs.runslaunchrail.workspace.id", "Workspace ID")}
                      placeholder={t("pages.runs.runslaunchrail.nyxid.user.workspace.id", "NyxID user / workspace id")}
                      rules={[
                        {
                          required: true,
                          message: t("pages.runs.runslaunchrail.workspace.id.is.required", "Workspace ID is required."),
                        },
                      ]}
                    />
                    {isChatVariant ? (
                      <Collapse
                        ghost
                        items={[
                          {
                            key: "advanced",
                            label: t("pages.runs.runslaunchrail.advanced.options", "Advanced options"),
                            children: (
                              <div style={compactStackStyle}>
                                <ProFormText
                                  name="endpointId"
	                                  label={t("pages.runs.runslaunchrail.endpoint", "Endpoint")}
                                  placeholder={t("pages.runs.runslaunchrail.chat.or.custom.chat.endpoint", "chat (or a custom chat endpoint id)")}
                                  disabled={draftMode}
                                />
                                {!draftMode ? (
                                  <ProFormText
                                    name="serviceOverrideId"
                                    label={t("pages.runs.runslaunchrail.binding.override.optional", "Binding override (optional)")}
                                    placeholder={t("pages.runs.runslaunchrail.leave.empty.to.use.the", "Leave empty to use the workspace default binding.")}
                                  />
                                ) : null}
                                {isChatEndpoint ? (
                                  <ProFormText
                                    name="actorId"
                                    label={t("pages.runs.runslaunchrail.existing.actor.id", "Existing actor ID")}
                                    placeholder="Actor:..."
                                    disabled={draftMode}
                                  />
                                ) : null}
                                <ProFormText
                                  name="payloadTypeUrl"
                                  label={t("pages.runs.runslaunchrail.payload.type.url", "Payload type URL")}
                                  placeholder="type.googleapis.com/google.protobuf.StringValue"
                                  extra={t("pages.runs.runslaunchrail.when.payload.base64.is.empty", "When payload base64 is empty, the workbench only auto-encodes StringValue and AppScriptCommand.")}
                                />
                                <ProFormTextArea
                                  name="payloadBase64"
                                  label={t("pages.runs.runslaunchrail.payload.base64.advanced", "Payload base64 (advanced)")}
                                  fieldProps={{ rows: 3 }}
                                  placeholder={t("pages.runs.runslaunchrail.required.for.custom.payload.types", "Required for custom payload types; leave empty only for StringValue or AppScriptCommand.")}
                                />
                              </div>
                            ),
                          },
                        ]}
                      />
                    ) : (
                      <>
                        <ProFormText
                          name="endpointId"
	                          label={t("pages.runs.runslaunchrail.endpoint.2", "Endpoint")}
                          placeholder={
                            isChatEndpoint
	                              ? t("pages.runs.runslaunchrail.chat.or.custom.chat.endpoint.2", "chat (or a custom chat endpoint id)")
	                              : "endpoint-id"
                          }
                          disabled={draftMode}
                          rules={[
                            {
                              required: !draftMode && !isChatEndpoint,
                              message: t("pages.runs.runslaunchrail.endpoint.id.is.required.for", "Endpoint ID is required for command invokes."),
                            },
                          ]}
                        />
                        {!draftMode ? (
                          <ProFormText
                            name="serviceOverrideId"
                            label={t("pages.runs.runslaunchrail.binding.override.optional.2", "Binding override (optional)")}
                            placeholder={t("pages.runs.runslaunchrail.leave.empty.to.use.the.2", "Leave empty to use the workspace default binding.")}
                          />
                        ) : null}
                        {isChatEndpoint ? (
                          <ProFormText
                            name="actorId"
                            label={t("pages.runs.runslaunchrail.existing.actor.id.2", "Existing actor ID")}
                            placeholder="Actor:..."
                            disabled={draftMode}
                          />
                        ) : null}
                        <ProFormText
                          name="payloadTypeUrl"
                          label={t("pages.runs.runslaunchrail.payload.type.url.2", "Payload type URL")}
                          placeholder="type.googleapis.com/google.protobuf.StringValue"
                          extra={t("pages.runs.runslaunchrail.when.payload.base64.is.empty.2", "When payload base64 is empty, the workbench only auto-encodes StringValue and AppScriptCommand.")}
                        />
                        <ProFormTextArea
                          name="payloadBase64"
                          label={t("pages.runs.runslaunchrail.payload.base64.advanced.2", "Payload base64 (advanced)")}
                          fieldProps={{ rows: 3 }}
                          placeholder={t("pages.runs.runslaunchrail.required.for.custom.payload.types.2", "Required for custom payload types; leave empty only for StringValue or AppScriptCommand.")}
                        />
                      </>
                    )}
                  </ProForm>
                </div>
              ),
            },
            {
              key: "recent",
              label: t("pages.runs.runslaunchrail.recent.count", "Recent ({count})", {
                count: recentRunRows.length,
              }),
              children: renderRecentRunCards(
                recentRunRows,
                onClearRecentRuns,
              ),
            },
            {
              key: "presets",
              label: t("pages.runs.runslaunchrail.presets.count", "Presets ({count})", {
                count: visiblePresets.length,
              }),
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
	            title={t("pages.runs.runslaunchrail.requests.go.through", "Requests go through {path}", {
	              path: submitPathLabel,
	            })}
          />
        ) : null}
        </div>
      </div>
    </ProCard>
  );
};

export default RunsLaunchRail;
