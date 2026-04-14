import { ProCard } from "@ant-design/pro-components";
import { Button, Empty, Space, Tag, Typography } from "antd";
import React from "react";
import type { WorkflowActorSnapshot } from "@/shared/models/runtime/actors";
import {
  cardStackStyle,
  embeddedPanelStyle,
  moduleCardProps,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";
import type { RunEventRow } from "../runEventPresentation";
import type {
  HumanInputRecord,
  RunFocusRecord,
  RunSummaryRecord,
  SelectedRouteRecord,
  WaitingSignalRecord,
} from "../runWorkbenchConfig";
import {
  formatRunRouteLabel,
  workbenchCardStyle,
  workbenchCardBodyStyle,
  workbenchScrollableBodyStyle,
} from "../runWorkbenchConfig";
import { AevatarHelpTooltip } from "@/shared/ui/aevatarPageShells";

type RunsInspectorPaneProps = {
  actorSnapshot?: WorkflowActorSnapshot;
  actorSnapshotLoading: boolean;
  humanInputRecord?: HumanInputRecord;
  latestMessagePreview?: string;
  onOpenInspector?: () => void;
  runFocus: RunFocusRecord;
  runSummaryRecord: RunSummaryRecord;
  selectedTraceItem?: RunEventRow;
  selectedRoutePrimitives: string[];
  selectedRouteRecord?: SelectedRouteRecord;
  showInteractionAction?: boolean;
  variant?: "card" | "plain";
  waitingSignalRecord?: WaitingSignalRecord;
};

type SummaryFieldProps = {
  copyable?: boolean;
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
  tone?: "default" | "info" | "success" | "warning" | "error";
  value: React.ReactNode;
};

type SectionHeaderProps = {
  action?: React.ReactNode;
  description?: React.ReactNode;
  help?: React.ReactNode;
  title: string;
};

type SectionTone = {
  background: string;
  borderColor: string;
  tagColor: string;
};

const sectionHeaderStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  gap: 12,
  justifyContent: "space-between",
  width: "100%",
};

const sectionDividerStyle: React.CSSProperties = {
  borderTop: "1px solid var(--ant-color-border-secondary)",
  display: "flex",
  flexDirection: "column",
  gap: 12,
  paddingTop: 12,
};

const focusToneMap: Record<RunFocusRecord["alertType"], SectionTone> = {
  error: {
    background: "rgba(255, 77, 79, 0.08)",
    borderColor: "rgba(255, 77, 79, 0.24)",
    tagColor: "error",
  },
  info: {
    background: "rgba(22, 119, 255, 0.08)",
    borderColor: "rgba(22, 119, 255, 0.24)",
    tagColor: "processing",
  },
  success: {
    background: "rgba(82, 196, 26, 0.08)",
    borderColor: "rgba(82, 196, 26, 0.24)",
    tagColor: "success",
  },
  warning: {
    background: "rgba(250, 173, 20, 0.10)",
    borderColor: "rgba(250, 173, 20, 0.28)",
    tagColor: "warning",
  },
};

const summaryMetricToneMap: Record<
  NonNullable<SummaryMetricProps["tone"]>,
  { color: string }
> = {
  default: { color: "var(--ant-color-text)" },
  error: { color: "var(--ant-color-error)" },
  info: { color: "var(--ant-color-primary)" },
  success: { color: "var(--ant-color-success)" },
  warning: { color: "var(--ant-color-warning)" },
};

const SummaryField: React.FC<SummaryFieldProps> = ({
  copyable,
  label,
  value,
}) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {copyable && typeof value === "string" && value && value !== "n/a" ? (
      <Typography.Text copyable>{value}</Typography.Text>
    ) : (
      <Typography.Text>{value}</Typography.Text>
    )}
  </div>
);

const SummaryMetric: React.FC<SummaryMetricProps> = ({
  label,
  tone = "default",
  value,
}) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text
      style={{
        ...summaryMetricValueStyle,
        color: summaryMetricToneMap[tone].color,
      }}
    >
      {value}
    </Typography.Text>
  </div>
);

const SectionHeader: React.FC<SectionHeaderProps> = ({
  action,
  description,
  help,
  title,
}) => (
  <div style={sectionHeaderStyle}>
    <div style={{ minWidth: 0 }}>
      <div
        style={{
          alignItems: "center",
          display: "inline-flex",
          flexWrap: "wrap",
          gap: 6,
          maxWidth: "100%",
        }}
      >
        <Typography.Text strong>{title}</Typography.Text>
        {help ? <AevatarHelpTooltip content={help} /> : null}
      </div>
      {description ? (
        <Typography.Paragraph
          style={{ margin: "4px 0 0" }}
          type="secondary"
        >
          {description}
        </Typography.Paragraph>
      ) : null}
    </div>
    {action}
  </div>
);

function renderPrimitiveTags(primitives: string[]): React.ReactNode {
  if (primitives.length === 0) {
    return <Typography.Text type="secondary">No primitives listed.</Typography.Text>;
  }

  return (
    <Space wrap size={[6, 6]}>
      {primitives.slice(0, 3).map((primitive) => (
        <Tag key={primitive}>{primitive}</Tag>
      ))}
      {primitives.length > 3 ? <Tag>+{primitives.length - 3} more</Tag> : null}
    </Space>
  );
}

function renderInteractionSummary(
  humanInputRecord: HumanInputRecord | undefined,
  waitingSignalRecord: WaitingSignalRecord | undefined,
): React.ReactNode {
  if (humanInputRecord) {
    return (
      <Space orientation="vertical" size={12} style={{ width: "100%" }}>
        <Space wrap size={[6, 6]}>
          <Tag color="warning">Human input</Tag>
          <Tag>{humanInputRecord.suspensionType || "n/a"}</Tag>
        </Space>
        <div style={summaryFieldGridStyle}>
          <SummaryField label="Step" value={humanInputRecord.stepId || "n/a"} />
          <SummaryField label="Run" value={humanInputRecord.runId || "n/a"} />
          <SummaryField
            label="Timeout"
            value={`${humanInputRecord.timeoutSeconds || 0}s`}
          />
        </div>
        <div>
          <Typography.Text style={summaryFieldLabelStyle}>Prompt</Typography.Text>
          <Typography.Paragraph
            ellipsis={{ rows: 3, expandable: true, symbol: "more" }}
            style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
          >
            {humanInputRecord.prompt || "No prompt provided."}
          </Typography.Paragraph>
        </div>
      </Space>
    );
  }

  if (waitingSignalRecord) {
    return (
      <Space orientation="vertical" size={12} style={{ width: "100%" }}>
        <Space wrap size={[6, 6]}>
          <Tag color="warning">Waiting signal</Tag>
          <Tag>{waitingSignalRecord.signalName || "n/a"}</Tag>
        </Space>
        <div style={summaryFieldGridStyle}>
          <SummaryField
            label="Signal"
            value={waitingSignalRecord.signalName || "n/a"}
          />
          <SummaryField
            label="Step"
            value={waitingSignalRecord.stepId || "n/a"}
          />
          <SummaryField label="Run" value={waitingSignalRecord.runId || "n/a"} />
        </div>
        <div>
          <Typography.Text style={summaryFieldLabelStyle}>Prompt</Typography.Text>
          <Typography.Paragraph
            ellipsis={{ rows: 3, expandable: true, symbol: "more" }}
            style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
          >
            {waitingSignalRecord.prompt || "No prompt provided."}
          </Typography.Paragraph>
        </div>
      </Space>
    );
  }

  return <Typography.Text type="secondary">No pending interaction.</Typography.Text>;
}

const RunsInspectorPane: React.FC<RunsInspectorPaneProps> = ({
  actorSnapshot,
  actorSnapshotLoading,
  humanInputRecord,
  latestMessagePreview,
  onOpenInspector,
  runFocus,
  runSummaryRecord,
  selectedTraceItem,
  selectedRoutePrimitives,
  selectedRouteRecord,
  showInteractionAction = true,
  variant = "card",
  waitingSignalRecord,
}) => {
  const focusTone = focusToneMap[runFocus.alertType];
  const content = (
    <div style={cardStackStyle}>
      <div style={embeddedPanelStyle}>
        <Space orientation="vertical" size={12} style={{ width: "100%" }}>
          <SectionHeader
            help="A compact summary of the current run state, identifiers, and latest visible output."
            title="Run digest"
          />
          <div
            style={{
              background: focusTone.background,
              border: `1px solid ${focusTone.borderColor}`,
              borderRadius: 12,
              padding: 12,
            }}
          >
            <Space orientation="vertical" size={8} style={{ width: "100%" }}>
              <Space wrap size={[6, 6]}>
                <Tag color={focusTone.tagColor}>{runFocus.title}</Tag>
                <Tag>{runSummaryRecord.focusLabel}</Tag>
              </Space>
              <Typography.Text>{runFocus.description}</Typography.Text>
            </Space>
          </div>
          <div style={summaryMetricGridStyle}>
            <SummaryMetric
              label="Route"
              value={formatRunRouteLabel(
                runSummaryRecord.routeName,
                runSummaryRecord.endpointId,
                runSummaryRecord.endpointKind,
              )}
            />
            <SummaryMetric
              label="Transport"
              value={runSummaryRecord.transport.toUpperCase()}
            />
            <SummaryMetric
              label="Messages"
              tone={runSummaryRecord.messageCount > 0 ? "info" : "default"}
              value={String(runSummaryRecord.messageCount)}
            />
            <SummaryMetric
              label="Events"
              tone={runSummaryRecord.eventCount > 0 ? "info" : "default"}
              value={String(runSummaryRecord.eventCount)}
            />
            <SummaryMetric
              label="Active steps"
              tone={
                runSummaryRecord.activeSteps.length > 0 ? "warning" : "default"
              }
              value={String(runSummaryRecord.activeSteps.length)}
            />
            <SummaryMetric
              label="Last event"
              value={runSummaryRecord.lastEventAt || "n/a"}
            />
          </div>
          <div style={summaryFieldGridStyle}>
            <SummaryField
              copyable
              label="Run ID"
              value={runSummaryRecord.runId || "n/a"}
            />
            <SummaryField
              copyable
              label="Actor ID"
              value={runSummaryRecord.actorId || "n/a"}
            />
            <SummaryField
              copyable
              label="Command ID"
              value={runSummaryRecord.commandId || "n/a"}
            />
          </div>
          <div>
            <Typography.Text style={summaryFieldLabelStyle}>Active steps</Typography.Text>
            <div style={{ marginTop: 8 }}>
              {runSummaryRecord.activeSteps.length > 0 ? (
                <Space wrap size={[6, 6]}>
                  {runSummaryRecord.activeSteps.map((step) => (
                    <Tag color="processing" key={step}>
                      {step}
                    </Tag>
                  ))}
                </Space>
              ) : (
                <Typography.Text type="secondary">No active steps.</Typography.Text>
              )}
            </div>
          </div>
          {latestMessagePreview ? (
            <div>
              <Typography.Text style={summaryFieldLabelStyle}>Latest message</Typography.Text>
              <Typography.Paragraph
                ellipsis={{ rows: 4, expandable: true, symbol: "more" }}
                style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
              >
                {latestMessagePreview}
              </Typography.Paragraph>
            </div>
          ) : null}
        </Space>
      </div>

      <div style={embeddedPanelStyle}>
        <Space orientation="vertical" size={12} style={{ width: "100%" }}>
          <SectionHeader
            help="The currently selected timeline item and its raw event payload."
            title="Selection"
            action={
              selectedTraceItem ? (
                <Space wrap size={[6, 6]}>
                  <Tag color="processing">{selectedTraceItem.timelineLabel}</Tag>
                  <Tag>{selectedTraceItem.eventCategory}</Tag>
                </Space>
              ) : undefined
            }
          />
          {selectedTraceItem ? (
            <>
              <div style={summaryFieldGridStyle}>
                <SummaryField
                  label="Timestamp"
                  value={selectedTraceItem.timestamp || "n/a"}
                />
                <SummaryField
                  label="Event type"
                  value={selectedTraceItem.eventType || "n/a"}
                />
                <SummaryField
                  label="Agent"
                  value={selectedTraceItem.agentId || "n/a"}
                />
                <SummaryField
                  label="Step"
                  value={selectedTraceItem.stepId || "n/a"}
                />
                <SummaryField
                  label="Step type"
                  value={selectedTraceItem.stepType || "n/a"}
                />
              </div>
              <div>
                <Typography.Text style={summaryFieldLabelStyle}>Description</Typography.Text>
                <Typography.Paragraph
                  style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
                >
                  {selectedTraceItem.description}
                </Typography.Paragraph>
              </div>
              <div>
                <Typography.Text style={summaryFieldLabelStyle}>Raw payload</Typography.Text>
                <pre
                  style={{
                    background: "var(--ant-color-fill-quaternary)",
                    border: "1px solid var(--ant-color-border-secondary)",
                    borderRadius: 10,
                    margin: "8px 0 0",
                    maxHeight: 220,
                    overflow: "auto",
                    padding: 12,
                    whiteSpace: "pre-wrap",
                    wordBreak: "break-word",
                  }}
                >
                  {selectedTraceItem.payloadText}
                </pre>
              </div>
            </>
          ) : (
            <Typography.Text type="secondary">
              Select a timeline row to inspect its detail.
            </Typography.Text>
          )}
        </Space>
      </div>

      <div style={embeddedPanelStyle}>
        <Space orientation="vertical" size={12} style={{ width: "100%" }}>
          <SectionHeader
            help="Operator interactions, route profile, and the latest actor-owned state."
            title="Runtime sidecars"
          />
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <div>
              <SectionHeader
                action={
                  showInteractionAction && onOpenInspector ? (
                    <Button onClick={onOpenInspector}>Open inspector</Button>
                  ) : undefined
                }
                title="Interaction"
              />
              <div style={{ marginTop: 12 }}>
                {renderInteractionSummary(humanInputRecord, waitingSignalRecord)}
              </div>
            </div>

            <div style={sectionDividerStyle}>
              <SectionHeader title="Route snapshot" />
              {selectedRouteRecord ? (
                <>
                  <Space wrap size={[6, 6]}>
                    <Tag color="processing">{selectedRouteRecord.routeName}</Tag>
                    <Tag>{selectedRouteRecord.groupLabel}</Tag>
                    <Tag>{selectedRouteRecord.sourceLabel}</Tag>
                    <Tag
                      color={
                        selectedRouteRecord.llmStatus === "processing"
                          ? "blue"
                          : "success"
                      }
                    >
                      {selectedRouteRecord.llmStatus === "processing"
                        ? "LLM required"
                        : "LLM optional"}
                    </Tag>
                  </Space>
                  <Typography.Paragraph
                    ellipsis={{ rows: 3, expandable: true, symbol: "more" }}
                    style={{ marginBottom: 0 }}
                    type="secondary"
                  >
                    {selectedRouteRecord.description || "No description provided."}
                  </Typography.Paragraph>
                  <div>{renderPrimitiveTags(selectedRoutePrimitives)}</div>
                </>
              ) : (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="Select a route preview to inspect its snapshot."
                />
              )}
            </div>

            <div style={sectionDividerStyle}>
              <SectionHeader title="Actor snapshot" />
              {actorSnapshotLoading ? (
                <Typography.Text type="secondary">
                  Loading actor snapshot...
                </Typography.Text>
              ) : actorSnapshot ? (
                <>
                  <div style={summaryFieldGridStyle}>
                    <SummaryField
                      copyable
                      label="Actor ID"
                      value={actorSnapshot.actorId}
                    />
                    <SummaryField
                      label="State version"
                      value={String(actorSnapshot.stateVersion)}
                    />
                    <SummaryField
                      label="Completed steps"
                      value={`${actorSnapshot.completedSteps}/${actorSnapshot.totalSteps}`}
                    />
                    <SummaryField
                      label="Role replies"
                      value={String(actorSnapshot.roleReplyCount)}
                    />
                  </div>
                  <div style={summaryFieldGridStyle}>
                    <SummaryField
                      label="Updated"
                      value={actorSnapshot.lastUpdatedAt || "n/a"}
                    />
                    <SummaryField
                      label="Last command"
                      value={actorSnapshot.lastCommandId || "n/a"}
                    />
                    <SummaryField
                      label="Last event"
                      value={actorSnapshot.lastEventId || "n/a"}
                    />
                  </div>
                  <div>
                    <Typography.Text style={summaryFieldLabelStyle}>Last output</Typography.Text>
                    <Typography.Paragraph
                      ellipsis={{ rows: 3, expandable: true, symbol: "more" }}
                      style={{ margin: "8px 0 0" }}
                    >
                      {actorSnapshot.lastOutput || "No output captured yet."}
                    </Typography.Paragraph>
                  </div>
                </>
              ) : (
                <Typography.Text type="secondary">
                  Actor snapshot will appear after the run binds to an actor.
                </Typography.Text>
              )}
            </div>
          </div>
        </Space>
      </div>
    </div>
  );

  if (variant === "plain") {
    return content;
  }

  return (
    <ProCard
      title="Inspector"
      hoverable
      {...moduleCardProps}
      style={workbenchCardStyle}
      bodyStyle={workbenchCardBodyStyle}
      extra={<Typography.Text type="secondary">Digest and drill-down</Typography.Text>}
    >
      <div style={workbenchScrollableBodyStyle}>{content}</div>
    </ProCard>
  );
};

export default RunsInspectorPane;
