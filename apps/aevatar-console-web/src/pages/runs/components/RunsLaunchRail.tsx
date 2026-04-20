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
          <Tag color="geekblue">Command invoke</Tag>
          <Tag>Scope binding</Tag>
        </Space>
        <Typography.Text strong style={{ display: "block", marginTop: 10 }}>
          {activeEndpointId}
        </Typography.Text>
        <Typography.Paragraph
          style={{ margin: "6px 0 0" }}
          type="secondary"
        >
          Invoke the selected endpoint with explicit protobuf bytes, or let the
          workbench derive bytes only for StringValue and AppScriptCommand
          payloads.
        </Typography.Paragraph>
      </div>
    );
  }

  if (!selectedRouteRecord) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="Select a route preview or endpoint to inspect the current route."
      />
    );
  }

  return (
    <div style={embeddedPanelStyle}>
      <Space wrap size={[6, 6]}>
        <Tag color={activeEndpointKind === "chat" ? "processing" : "geekblue"}>
          {activeEndpointKind === "chat" ? "Service SSE" : "Command invoke"}
        </Tag>
        <Tag>{selectedRouteRecord.groupLabel}</Tag>
        <Tag>{selectedRouteRecord.sourceLabel}</Tag>
        <Tag color={selectedRouteRecord.llmStatus === "processing" ? "blue" : "success"}>
          {selectedRouteRecord.llmStatus === "processing"
            ? "LLM required"
            : "LLM optional"}
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
        {selectedRouteRecord.description || "No description provided."}
      </Typography.Paragraph>
      <Space wrap size={[6, 6]}>
        {selectedRouteDetailsPrimitives.slice(0, 3).map((primitive) => (
          <Tag key={primitive}>{primitive}</Tag>
        ))}
        {selectedRouteDetailsPrimitives.length > 3 ? (
          <Tag>+{selectedRouteDetailsPrimitives.length - 3} more</Tag>
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
        description="No local runs have been recorded yet."
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
                  <Tag>{record.runId || "No runId"}</Tag>
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
                "No preview recorded."}
            </Typography.Paragraph>

            <div style={railListActionStyle}>
              <Space wrap size={[8, 8]}>
                <Button type="link" onClick={() => record.onRestore?.()}>
                  Restore
                </Button>
                {record.actorId ? (
                  <Button type="link" onClick={() => record.onOpenActor?.()}>
                    Actor
                  </Button>
                ) : null}
              </Space>
            </div>
          </div>
        ))}
      </div>

      <Space>
        <Button danger onClick={onClearRecentRuns}>
          Clear local runs
        </Button>
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
        description="No presets are currently available."
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
                {record.title}
              </Typography.Text>
              <div style={railMetaWrapStyle}>
                <Tag color="processing">{record.routeName}</Tag>
                {record.tags.slice(0, 2).map((tag) => (
                  <Tag key={`${record.key}-${tag}`}>{tag}</Tag>
                ))}
                {record.tags.length > 2 ? (
                  <Tag>+{record.tags.length - 2} more</Tag>
                ) : null}
              </div>
            </div>
          </div>

          <Typography.Paragraph
            ellipsis={{ rows: 2, expandable: true, symbol: "more" }}
            style={railDescriptionStyle}
            type="secondary"
          >
            {record.description}
          </Typography.Paragraph>

          <div style={railListActionStyle}>
            <Button type="link" onClick={() => onUsePreset(record)}>
              Use preset
            </Button>
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
      title={isChatVariant ? "Setup" : "Run setup"}
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
                <Typography.Text style={quickMetricLabelStyle}>Endpoint</Typography.Text>
                <Typography.Text style={quickMetricValueStyle}>
                  {activeEndpointId || "chat"}
                </Typography.Text>
              </div>
              <div style={quickMetricStyle}>
                <Typography.Text style={quickMetricLabelStyle}>Execution</Typography.Text>
                <Typography.Text style={quickMetricValueStyle}>
                  {isChatEndpoint ? "STREAM" : "INVOKE"}
                </Typography.Text>
              </div>
              <div style={quickMetricStyle}>
                <Typography.Text style={quickMetricLabelStyle}>Mode</Typography.Text>
                <Typography.Text style={quickMetricValueStyle}>
                  {draftMode
                    ? isChatEndpoint
                      ? "Draft run"
                      : "Prepared invoke"
                    : actorId
                      ? "Continue actor"
                      : "Endpoint invoke"}
                </Typography.Text>
              </div>
              <div style={quickMetricStyle}>
                <Typography.Text style={quickMetricLabelStyle}>Presets</Typography.Text>
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
              label: "Compose",
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
                                  Start run
                                </Button>
                                <Button onClick={onAbortRun} disabled={!streaming}>
                                  Abort
                                </Button>
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
                                    Actor explorer
                                  </Button>
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
                            message: "Prompt is required.",
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
                            message: "Transport is required.",
                          },
                        ]}
                      />
                    ) : null}
                    {!draftMode && !isChatVariant ? (
                      <ProFormSelect<RunEndpointKind>
                        name="endpointKind"
                        label="Endpoint kind"
                        options={[
                          { label: "Chat stream", value: "chat" },
                          { label: "Command invoke", value: "command" },
                        ]}
                        extra="Chat endpoints keep the service streaming path even when the endpoint id is custom."
                        rules={[
                          {
                            required: true,
                            message: "Endpoint kind is required.",
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
                            : "Selecting a route targets the published scope service with the same id. Leave it empty to use the scope default binding; binding override wins when provided."
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
                              Loading chat routes...
                            </Typography.Text>
                          ) : (
                            <Empty
                              image={Empty.PRESENTED_IMAGE_SIMPLE}
                              description="No chat routes available."
                            />
                          ),
                          searchValue: catalogSearch,
                        }}
                      />
                    ) : (
                      <Alert
                        showIcon
                        type="info"
                        title="Generic endpoint invoke"
                        description="Use the prompt as the default payload text, or provide an explicit type URL and protobuf base64 payload."
                      />
                    )}
                    <ProFormText
                      name="scopeId"
                      label="Scope ID"
                      placeholder="NyxID user / scope id"
                      rules={[
                        {
                          required: true,
                          message: "Scope ID is required.",
                        },
                      ]}
                    />
                    {isChatVariant ? (
                      <Collapse
                        ghost
                        items={[
                          {
                            key: "advanced",
                            label: "Advanced options",
                            children: (
                              <div style={compactStackStyle}>
                                <ProFormText
                                  name="endpointId"
                                  label="Endpoint"
                                  placeholder="chat (or a custom chat endpoint id)"
                                  disabled={draftMode}
                                />
                                {!draftMode ? (
                                  <ProFormText
                                    name="serviceOverrideId"
                                    label="Binding override (optional)"
                                    placeholder="Leave empty to use the scope default binding."
                                  />
                                ) : null}
                                {isChatEndpoint ? (
                                  <ProFormText
                                    name="actorId"
                                    label="Existing actor ID"
                                    placeholder="Actor:..."
                                    disabled={draftMode}
                                  />
                                ) : null}
                                <ProFormText
                                  name="payloadTypeUrl"
                                  label="Payload type URL"
                                  placeholder="type.googleapis.com/google.protobuf.StringValue"
                                  extra="When payload base64 is empty, the workbench only auto-encodes StringValue and AppScriptCommand."
                                />
                                <ProFormTextArea
                                  name="payloadBase64"
                                  label="Payload base64 (advanced)"
                                  fieldProps={{ rows: 3 }}
                                  placeholder="Required for custom payload types; leave empty only for StringValue or AppScriptCommand."
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
                              message: "Endpoint ID is required for command invokes.",
                            },
                          ]}
                        />
                        {!draftMode ? (
                          <ProFormText
                            name="serviceOverrideId"
                            label="Binding override (optional)"
                            placeholder="Leave empty to use the scope default binding."
                          />
                        ) : null}
                        {isChatEndpoint ? (
                          <ProFormText
                            name="actorId"
                            label="Existing actor ID"
                            placeholder="Actor:..."
                            disabled={draftMode}
                          />
                        ) : null}
                        <ProFormText
                          name="payloadTypeUrl"
                          label="Payload type URL"
                          placeholder="type.googleapis.com/google.protobuf.StringValue"
                          extra="When payload base64 is empty, the workbench only auto-encodes StringValue and AppScriptCommand."
                        />
                        <ProFormTextArea
                          name="payloadBase64"
                          label="Payload base64 (advanced)"
                          fieldProps={{ rows: 3 }}
                          placeholder="Required for custom payload types; leave empty only for StringValue or AppScriptCommand."
                        />
                      </>
                    )}
                  </ProForm>
                </div>
              ),
            },
            {
              key: "recent",
              label: `Recent (${recentRunRows.length})`,
              children: renderRecentRunCards(
                recentRunRows,
                onClearRecentRuns,
              ),
            },
            {
              key: "presets",
              label: `Presets (${visiblePresets.length})`,
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
