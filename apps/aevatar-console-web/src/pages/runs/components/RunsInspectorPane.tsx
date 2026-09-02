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
import { t } from "@/shared/i18n/messages";
import {
  getUserFacingIdentifierLabel,
  sanitizeUserFacingText,
} from "@/shared/ui/userFacingIdentifiers";

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
    return <Typography.Text type="secondary">{t("pages.runs.runsinspectorpane.no.primitives.listed", "No primitives listed.")}</Typography.Text>;
  }

  return (
    <Space wrap size={[6, 6]}>
      {primitives.slice(0, 3).map((primitive) => (
        <Tag key={primitive}>{primitive}</Tag>
      ))}
      {primitives.length > 3 ? <Tag>+{primitives.length - 3} {t("pages.runs.runsinspectorpane.more", "more")}</Tag> : null}
    </Space>
  );
}

function formatRuntimeAvailability(
  value: string | null | undefined,
  availableLabel: string,
): string {
  return value?.trim() ? availableLabel : "n/a";
}

function renderInteractionSummary(
  humanInputRecord: HumanInputRecord | undefined,
  waitingSignalRecord: WaitingSignalRecord | undefined,
): React.ReactNode {
  if (humanInputRecord) {
    return (
      <Space orientation="vertical" size={12} style={{ width: "100%" }}>
        <Space wrap size={[6, 6]}>
          <Tag color="warning">{t("pages.runs.runsinspectorpane.human.input", "Human input")}</Tag>
          <Tag>{humanInputRecord.suspensionType || "n/a"}</Tag>
        </Space>
        <div style={summaryFieldGridStyle}>
          <SummaryField label={t("pages.runs.runsinspectorpane.step", "Step")} value={humanInputRecord.stepId || "n/a"} />
          <SummaryField
            label={t("pages.runs.runsinspectorpane.run", "Run")}
            value={formatRuntimeAvailability(
              humanInputRecord.runId,
              t("pages.runs.runsinspectorpane.current.run", "Current run"),
            )}
          />
          <SummaryField
            label={t("pages.runs.runsinspectorpane.timeout", "Timeout")}
            value={`${humanInputRecord.timeoutSeconds || 0}s`}
          />
        </div>
        <div>
          <Typography.Text style={summaryFieldLabelStyle}>{t("pages.runs.runsinspectorpane.prompt", "Prompt")}</Typography.Text>
          <Typography.Paragraph
            ellipsis={{ rows: 3, expandable: true, symbol: "more" }}
            style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
          >
            {humanInputRecord.prompt ||
              t("pages.runs.runsinspectorpane.no.prompt.provided", "No prompt provided.")}
          </Typography.Paragraph>
        </div>
      </Space>
    );
  }

  if (waitingSignalRecord) {
    return (
      <Space orientation="vertical" size={12} style={{ width: "100%" }}>
        <Space wrap size={[6, 6]}>
          <Tag color="warning">{t("pages.runs.runsinspectorpane.waiting.signal", "Waiting signal")}</Tag>
          <Tag>{waitingSignalRecord.signalName || "n/a"}</Tag>
        </Space>
        <div style={summaryFieldGridStyle}>
          <SummaryField
            label={t("pages.runs.runsinspectorpane.signal", "Signal")}
            value={waitingSignalRecord.signalName || "n/a"}
          />
          <SummaryField
            label={t("pages.runs.runsinspectorpane.step.2", "Step")}
            value={waitingSignalRecord.stepId || "n/a"}
          />
          <SummaryField
            label={t("pages.runs.runsinspectorpane.run.2", "Run")}
            value={formatRuntimeAvailability(
              waitingSignalRecord.runId,
              t("pages.runs.runsinspectorpane.current.run.2", "Current run"),
            )}
          />
        </div>
        <div>
          <Typography.Text style={summaryFieldLabelStyle}>{t("pages.runs.runsinspectorpane.prompt.2", "Prompt")}</Typography.Text>
          <Typography.Paragraph
            ellipsis={{ rows: 3, expandable: true, symbol: "more" }}
            style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
          >
            {waitingSignalRecord.prompt ||
              t("pages.runs.runsinspectorpane.no.prompt.provided.2", "No prompt provided.")}
          </Typography.Paragraph>
        </div>
      </Space>
    );
  }

  return <Typography.Text type="secondary">{t("pages.runs.runsinspectorpane.no.pending.interaction", "No pending interaction.")}</Typography.Text>;
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
            help={t("pages.runs.runsinspectorpane.compact.summary.help", "A compact summary of the current run state and latest visible output.")}
            title={t("pages.runs.runsinspectorpane.run.summary", "Run summary")}
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
              label={t("pages.runs.runsinspectorpane.route", "Route")}
              value={formatRunRouteLabel(
                runSummaryRecord.routeName,
                runSummaryRecord.endpointId,
                runSummaryRecord.endpointKind,
              )}
            />
            <SummaryMetric
              label={t("pages.runs.runsinspectorpane.transport", "Transport")}
              value={runSummaryRecord.transport.toUpperCase()}
            />
            <SummaryMetric
              label={t("pages.runs.runsinspectorpane.messages", "Messages")}
              tone={runSummaryRecord.messageCount > 0 ? "info" : "default"}
              value={String(runSummaryRecord.messageCount)}
            />
            <SummaryMetric
              label={t("pages.runs.runsinspectorpane.events", "Events")}
              tone={runSummaryRecord.eventCount > 0 ? "info" : "default"}
              value={String(runSummaryRecord.eventCount)}
            />
            <SummaryMetric
              label={t("pages.runs.runsinspectorpane.active.steps", "Active steps")}
              tone={
                runSummaryRecord.activeSteps.length > 0 ? "warning" : "default"
              }
              value={String(runSummaryRecord.activeSteps.length)}
            />
            <SummaryMetric
              label={t("pages.runs.runsinspectorpane.last.event", "Last event")}
              value={runSummaryRecord.lastEventAt || "n/a"}
            />
          </div>
          <div style={summaryFieldGridStyle}>
            <SummaryField
              label={t("pages.runs.runsinspectorpane.run", "Run")}
              value={formatRuntimeAvailability(
                runSummaryRecord.runId,
                t("pages.runs.runsinspectorpane.current.run.ready", "Current run ready"),
              )}
            />
            <SummaryField
              label={t("pages.runs.runsinspectorpane.actor", "Actor")}
              value={formatRuntimeAvailability(
                runSummaryRecord.actorId,
                t("pages.runs.runsinspectorpane.runtime.actor.ready", "Runtime actor ready"),
              )}
            />
            <SummaryField
              label={t("pages.runs.runsinspectorpane.command", "Command")}
              value={formatRuntimeAvailability(
                runSummaryRecord.commandId,
                t("pages.runs.runsinspectorpane.command.accepted", "Command accepted"),
              )}
            />
          </div>
          <div>
            <Typography.Text style={summaryFieldLabelStyle}>{t("pages.runs.runsinspectorpane.active.steps.2", "Active steps")}</Typography.Text>
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
                <Typography.Text type="secondary">{t("pages.runs.runsinspectorpane.no.active.steps", "No active steps.")}</Typography.Text>
              )}
            </div>
          </div>
          {latestMessagePreview ? (
            <div>
              <Typography.Text style={summaryFieldLabelStyle}>{t("pages.runs.runsinspectorpane.latest.message", "Latest message")}</Typography.Text>
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
            help={t("pages.runs.runsinspectorpane.selected.event.help", "The currently selected timeline item and its raw event payload.")}
            title={t("pages.runs.runsinspectorpane.selected.event", "Selected event")}
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
                  label={t("pages.runs.runsinspectorpane.timestamp", "Timestamp")}
                  value={selectedTraceItem.timestamp || "n/a"}
                />
                <SummaryField
                  label={t("pages.runs.runsinspectorpane.event.type", "Event type")}
                  value={selectedTraceItem.eventType || "n/a"}
                />
                <SummaryField
                  label={t("pages.runs.runsinspectorpane.agent", "Agent")}
                  value={formatRuntimeAvailability(
                    selectedTraceItem.agentId,
                    t("pages.runs.runsinspectorpane.runtime.actor.ready.2", "Runtime actor ready"),
                  )}
                />
                <SummaryField
                  label={t("pages.runs.runsinspectorpane.step.3", "Step")}
                  value={getUserFacingIdentifierLabel(
                    selectedTraceItem.stepId,
                    t("pages.runs.runsinspectorpane.step", "Step"),
                  )}
                />
                <SummaryField
                  label={t("pages.runs.runsinspectorpane.step.type", "Step type")}
                  value={selectedTraceItem.stepType || "n/a"}
                />
              </div>
              <div>
                <Typography.Text style={summaryFieldLabelStyle}>{t("pages.runs.runsinspectorpane.description", "Description")}</Typography.Text>
                <Typography.Paragraph
                  style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
                >
                  {selectedTraceItem.description}
                </Typography.Paragraph>
              </div>
              <div>
                <Typography.Text style={summaryFieldLabelStyle}>{t("pages.runs.runsinspectorpane.raw.payload", "Raw payload")}</Typography.Text>
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
                  {sanitizeUserFacingText(selectedTraceItem.payloadText) ||
                    t("pages.runs.runsinspectorpane.no.user.visible.payload", "No user-visible payload fields.")}
                </pre>
              </div>
            </>
          ) : (
            <Typography.Text type="secondary">
              {t("pages.runs.runsinspectorpane.select.timeline.row.to.inspect", "Select a timeline row to inspect its detail.")}</Typography.Text>
          )}
        </Space>
      </div>

      <div style={embeddedPanelStyle}>
        <Space orientation="vertical" size={12} style={{ width: "100%" }}>
          <SectionHeader
            help={t("pages.runs.runsinspectorpane.context.help", "Operator interactions, route profile, and the latest actor-owned state.")}
            title={t("pages.runs.runsinspectorpane.context", "Context")}
          />
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <div>
              <SectionHeader
                action={
                  showInteractionAction && onOpenInspector ? (
                    <Button onClick={onOpenInspector}>{t("pages.runs.runsinspectorpane.open.details", "Open details")}</Button>
                  ) : undefined
                }
                title={t("pages.runs.runsinspectorpane.interaction", "Interaction")}
              />
              <div style={{ marginTop: 12 }}>
                {renderInteractionSummary(humanInputRecord, waitingSignalRecord)}
              </div>
            </div>

            <div style={sectionDividerStyle}>
              <SectionHeader title={t("pages.runs.runsinspectorpane.route.2", "Route")} />
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
                        ? t("pages.runs.runsinspectorpane.llm.required", "LLM required")
                        : t("pages.runs.runsinspectorpane.llm.optional", "LLM optional")}
                    </Tag>
                  </Space>
                  <Typography.Paragraph
                    ellipsis={{ rows: 3, expandable: true, symbol: "more" }}
                    style={{ marginBottom: 0 }}
                    type="secondary"
                  >
                    {selectedRouteRecord.description ||
                      t("pages.runs.runsinspectorpane.no.description.provided", "No description provided.")}
                  </Typography.Paragraph>
                  <div>{renderPrimitiveTags(selectedRoutePrimitives)}</div>
                </>
              ) : (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description={t("pages.runs.runsinspectorpane.select.route.preview.to.inspect", "Select a route preview to inspect its snapshot.")}
                />
              )}
            </div>

            <div style={sectionDividerStyle}>
              <SectionHeader title={t("pages.runs.runsinspectorpane.actor.state", "Actor state")} />
              {actorSnapshotLoading ? (
                <Typography.Text type="secondary">
                  {t("pages.runs.runsinspectorpane.loading.actor.snapshot", "Loading actor snapshot...")}</Typography.Text>
              ) : actorSnapshot ? (
                <>
                  <div style={summaryFieldGridStyle}>
                    <SummaryField
                      label={t("pages.runs.runsinspectorpane.actor", "Actor")}
                      value={formatRuntimeAvailability(
                        actorSnapshot.actorId,
                        t("pages.runs.runsinspectorpane.runtime.actor.ready.3", "Runtime actor ready"),
                      )}
                    />
                    <SummaryField
                      label={t("pages.runs.runsinspectorpane.state.version", "State version")}
                      value={String(actorSnapshot.stateVersion)}
                    />
                    <SummaryField
                      label={t("pages.runs.runsinspectorpane.completed.steps", "Completed steps")}
                      value={`${actorSnapshot.completedSteps}/${actorSnapshot.totalSteps}`}
                    />
                    <SummaryField
                      label={t("pages.runs.runsinspectorpane.role.replies", "Role replies")}
                      value={String(actorSnapshot.roleReplyCount)}
                    />
                  </div>
                  <div style={summaryFieldGridStyle}>
                    <SummaryField
                      label={t("pages.runs.runsinspectorpane.updated", "Updated")}
                      value={actorSnapshot.lastUpdatedAt || "n/a"}
                    />
                    <SummaryField
                      label={t("pages.runs.runsinspectorpane.last.command", "Last command")}
                      value={formatRuntimeAvailability(
                        actorSnapshot.lastCommandId,
                        t("pages.runs.runsinspectorpane.command.accepted.2", "Command accepted"),
                      )}
                    />
                    <SummaryField
                      label={t("pages.runs.runsinspectorpane.last.event.2", "Last event")}
                      value={formatRuntimeAvailability(
                        actorSnapshot.lastEventId,
                        t("pages.runs.runsinspectorpane.event.recorded", "Event recorded"),
                      )}
                    />
                  </div>
                  <div>
                    <Typography.Text style={summaryFieldLabelStyle}>{t("pages.runs.runsinspectorpane.last.output", "Last output")}</Typography.Text>
                    <Typography.Paragraph
                      ellipsis={{ rows: 3, expandable: true, symbol: "more" }}
                      style={{ margin: "8px 0 0" }}
                    >
                      {actorSnapshot.lastOutput ||
                        t("pages.runs.runsinspectorpane.no.output.captured.yet", "No output captured yet.")}
                    </Typography.Paragraph>
                  </div>
                </>
              ) : (
                <Typography.Text type="secondary">
                  {t("pages.runs.runsinspectorpane.actor.state.will.appear.after", "Actor state will appear after the run binds to an actor.")}</Typography.Text>
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
      title={t("pages.runs.runsinspectorpane.details", "Details")}
      hoverable
      {...moduleCardProps}
      style={workbenchCardStyle}
      bodyStyle={workbenchCardBodyStyle}
      extra={<Typography.Text type="secondary">{t("pages.runs.runsinspectorpane.digest.and.drill.down", "Digest and drill-down")}</Typography.Text>}
    >
      <div style={workbenchScrollableBodyStyle}>{content}</div>
    </ProCard>
  );
};

export default RunsInspectorPane;
