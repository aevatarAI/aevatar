import { ProCard, ProForm, ProFormSelect, ProFormText, ProFormTextArea } from "@ant-design/pro-components";
import type { ProFormInstance } from "@ant-design/pro-components";
import { Alert, Button, Empty, Space, Tabs, Tag, Typography } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import { buildRuntimeExplorerHref } from "@/shared/navigation/runtimeRoutes";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { cardStackStyle, embeddedPanelStyle, moduleCardProps, scrollPanelStyle } from "@/shared/ui/proComponents";
import type { RunTransport } from "../runEventPresentation";
import type { RecentRunTableRow, RunFormValues, RunPreset, SelectedWorkflowRecord } from "../runWorkbenchConfig";
import { workbenchCardBodyStyle, workbenchCardStyle, workbenchScrollableBodyStyle } from "../runWorkbenchConfig";

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
  initialFormValues: RunFormValues;
  recentRunRows: RecentRunTableRow[];
  selectedTransport: RunTransport;
  selectedWorkflowDetailsPrimitives: string[];
  selectedWorkflowRecord?: SelectedWorkflowRecord;
  streaming: boolean;
  submitPathLabel: string;
  transportOptions: Array<{ label: string; value: RunTransport }>;
  visiblePresets: RunPreset[];
  workflowCatalogLoading: boolean;
  workflowOptions: WorkflowOption[];
  onAbortRun: () => void;
  onCatalogSearchChange: (value: string) => void;
  onClearRecentRuns: () => void;
  onEndpointChange: (value: string) => void;
  onSelectWorkflowName: (value: string) => void;
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

function renderWorkflowMiniCard(
  activeEndpointId: string,
  selectedWorkflowDetailsPrimitives: string[],
  selectedWorkflowRecord?: SelectedWorkflowRecord,
): React.ReactNode {
  if (activeEndpointId && activeEndpointId !== "chat" && !selectedWorkflowRecord) {
    return (
      <div style={embeddedPanelStyle}>
        <Space wrap size={[6, 6]}>
          <Tag color="geekblue">Command invoke</Tag>
          <Tag>Scope default service</Tag>
        </Space>
        <Typography.Text strong style={{ display: "block", marginTop: 10 }}>
          {activeEndpointId}
        </Typography.Text>
        <Typography.Paragraph
          style={{ margin: "6px 0 0" }}
          type="secondary"
        >
          Invoke the selected service endpoint with either an explicit protobuf
          payload or the default StringValue payload derived from the prompt.
        </Typography.Paragraph>
      </div>
    );
  }

  if (!selectedWorkflowRecord) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="Select a workflow or service endpoint to preview the target."
      />
    );
  }

  return (
    <div style={embeddedPanelStyle}>
      <Space wrap size={[6, 6]}>
        <Tag color={activeEndpointId === "chat" ? "processing" : "geekblue"}>
          {activeEndpointId === "chat" ? "Service SSE" : "Command invoke"}
        </Tag>
        <Tag>{selectedWorkflowRecord.groupLabel}</Tag>
        <Tag>{selectedWorkflowRecord.sourceLabel}</Tag>
        <Tag color={selectedWorkflowRecord.llmStatus === "processing" ? "blue" : "success"}>
          {selectedWorkflowRecord.llmStatus === "processing"
            ? "LLM required"
            : "LLM optional"}
        </Tag>
      </Space>
      <Typography.Text strong style={{ display: "block", marginTop: 10 }}>
        {selectedWorkflowRecord.workflowName}
      </Typography.Text>
      <Typography.Paragraph
        ellipsis={{ rows: 2, expandable: true, symbol: "more" }}
        style={{ margin: "6px 0 0" }}
        type="secondary"
      >
        {selectedWorkflowRecord.description || "No description provided."}
      </Typography.Paragraph>
      <Space wrap size={[6, 6]}>
        {selectedWorkflowDetailsPrimitives.slice(0, 3).map((primitive) => (
          <Tag key={primitive}>{primitive}</Tag>
        ))}
        {selectedWorkflowDetailsPrimitives.length > 3 ? (
          <Tag>+{selectedWorkflowDetailsPrimitives.length - 3} more</Tag>
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
                  {record.workflowName || record.endpointId || "n/a"}
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
                  <Tag>{record.endpointId || "chat"}</Tag>
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
                <Tag color="processing">{record.workflow}</Tag>
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
  initialFormValues,
  recentRunRows,
  selectedWorkflowDetailsPrimitives,
  selectedWorkflowRecord,
  streaming,
  submitPathLabel,
  transportOptions,
  visiblePresets,
  workflowCatalogLoading,
  workflowOptions,
  onAbortRun,
  onCatalogSearchChange,
  onClearRecentRuns,
  onEndpointChange,
  onSelectWorkflowName,
  onSubmitRun,
  onTransportChange,
  onUsePreset,
}) => {
  const isChatEndpoint = activeEndpointId === "chat";

  return (
    <ProCard
      title="Launch rail"
      hoverable
      {...moduleCardProps}
      style={workbenchCardStyle}
      bodyStyle={workbenchCardBodyStyle}
      extra={
        <Typography.Text type="secondary">Compose, restore, reuse</Typography.Text>
      }
    >
      <div style={workbenchScrollableBodyStyle}>
        <div style={compactStackStyle}>
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

        <Tabs
          items={[
            {
              key: "compose",
              label: "Compose",
              children: (
                <div style={compactStackStyle}>
                  {renderWorkflowMiniCard(
                    activeEndpointId,
                    selectedWorkflowDetailsPrimitives,
                    selectedWorkflowRecord,
                  )}

                  <ProForm<RunFormValues>
                    formRef={composerFormRef}
                    layout="vertical"
                    initialValues={initialFormValues}
                    onValuesChange={(_, values) => {
                      onSelectWorkflowName(values.workflow ?? "");
                      onEndpointChange(values.endpointId || "chat");
                      if (values.transport) {
                        onTransportChange(values.transport);
                      }
                    }}
                    onFinish={async (values) => {
                      await onSubmitRun(values);
                      return true;
                    }}
                    submitter={{
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
                              Open actor
                            </Button>
                          ) : null}
                        </Space>
                      ),
                    }}
                  >
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
                    {isChatEndpoint ? (
                      <ProFormSelect
                        name="workflow"
                        label="Workflow bundle (optional)"
                        placeholder="Select a workflow bundle"
                        disabled={draftMode}
                        options={workflowOptions}
                        fieldProps={{
                          allowClear: true,
                          showSearch: true,
                          filterOption: false,
                          onSearch: onCatalogSearchChange,
                          notFoundContent: workflowCatalogLoading ? (
                            <Typography.Text type="secondary">
                              Loading workflows...
                            </Typography.Text>
                          ) : (
                            <Empty
                              image={Empty.PRESENTED_IMAGE_SIMPLE}
                              description="No workflows available."
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
                    <ProFormText
                      name="endpointId"
                      label="Endpoint ID"
                      placeholder="chat"
                      disabled={draftMode}
                      rules={[
                        {
                          required: !draftMode,
                          message: "Endpoint ID is required.",
                        },
                      ]}
                    />
                    <ProFormText
                      name="serviceId"
                      label={draftMode ? "Service ID" : "Service ID (optional)"}
                      placeholder={
                        draftMode
                          ? "Draft runs resolve directly from the YAML bundle."
                          : "Leave empty to use the scope default service."
                      }
                      disabled={draftMode}
                    />
                    {isChatEndpoint ? (
                      <ProFormText
                        name="actorId"
                        label="Existing actorId"
                        placeholder="Workflow:..."
                        disabled={draftMode}
                      />
                    ) : null}
                    <ProFormText
                      name="payloadTypeUrl"
                      label="Payload type URL"
                      placeholder="type.googleapis.com/google.protobuf.StringValue"
                      extra="When payload base64 is empty, the workbench derives protobuf bytes from the payload text."
                    />
                    <ProFormTextArea
                      name="payloadBase64"
                      label="Payload base64 (advanced)"
                      fieldProps={{ rows: 3 }}
                      placeholder="Leave empty to let the workbench derive the payload from the prompt."
                    />
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

        <Alert
          showIcon
          type="info"
          title={`Runs will stream over ${submitPathLabel}`}
        />
        </div>
      </div>
    </ProCard>
  );
};

export default RunsLaunchRail;
