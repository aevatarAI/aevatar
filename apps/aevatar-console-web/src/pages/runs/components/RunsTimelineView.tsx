import { Badge, Empty, Space, Tag, Typography } from "antd";
import React from "react";
import { embeddedPanelStyle } from "@/shared/ui/proComponents";
import {
  workbenchConsoleScrollStyle,
  workbenchConsoleSurfaceStyle,
} from "../runWorkbenchConfig";
import {
  eventCategoryValueEnum,
  eventStatusValueEnum,
  type RunEventCategory,
  type RunEventRow,
  type RunTimelineGroup,
} from "../runEventPresentation";

type RunsTimelineViewProps = {
  groups: RunTimelineGroup[];
  onSelectItem?: (item: RunEventRow) => void;
  selectedItemKey?: string;
};

const statusBadgeMap: Record<
  RunTimelineGroup["status"],
  "success" | "processing" | "error" | "default"
> = {
  default: "default",
  error: "error",
  processing: "processing",
  success: "success",
};

const timelineHeaderStyle: React.CSSProperties = {
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  color: "var(--ant-color-text-secondary)",
  padding: "10px 12px",
};

const timelineGroupStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  background: "var(--ant-color-fill-quaternary)",
  padding: 12,
};

const timelineRowStyle: React.CSSProperties = {
  borderTop: "1px solid var(--ant-color-border-secondary)",
  display: "grid",
  gap: 12,
  gridTemplateColumns: "88px minmax(0, 1fr)",
  padding: "8px 0",
};

const timelineMetaStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  display: "flex",
  flexDirection: "column",
  fontSize: 12,
  gap: 4,
  lineHeight: 1.35,
};

const timelineBodyStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 12,
};

const timelineRowBodyStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 8,
  minWidth: 0,
};

const timelineSelectButtonStyle: React.CSSProperties = {
  background: "transparent",
  border: "none",
  cursor: "pointer",
  font: "inherit",
  padding: 0,
  textAlign: "left",
  width: "100%",
};

const timelineGroupMetaStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 8,
  marginTop: 8,
};

const timelineGroupSummaryStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 12,
  lineHeight: 1.4,
};

const timelineRowHeadlineStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  minWidth: 0,
};

const timelineRowDetailStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  minWidth: 0,
};

const timelineTimeTextStyle: React.CSSProperties = {
  color: "var(--ant-color-text)",
  fontWeight: 600,
};

const timelineSecondaryMetaStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 11,
  lineHeight: 1.3,
};

const categoryTagToneMap: Record<
  RunEventCategory,
  "default" | "processing" | "success" | "warning" | "error"
> = {
  error: "error",
  human_approval: "warning",
  human_input: "warning",
  lifecycle: "default",
  message: "processing",
  state: "success",
  tool: "processing",
  wait_signal: "warning",
};

const statusTagToneMap: Record<
  RunTimelineGroup["status"],
  "default" | "processing" | "success" | "warning" | "error"
> = {
  default: "default",
  error: "error",
  processing: "processing",
  success: "success",
};

function buildProgressionLabel(group: RunTimelineGroup): string {
  const latest = group.items[0];
  const earliest = group.items[group.items.length - 1];
  if (!latest || !earliest) {
    return "";
  }

  if (latest.eventType === earliest.eventType) {
    return latest.eventType;
  }

  return `${earliest.eventType} -> ${latest.eventType}`;
}

function getGroupAgents(group: RunTimelineGroup): string[] {
  return [...new Set(group.items.map((item) => item.agentId).filter(Boolean))];
}

function getGroupStepTypes(group: RunTimelineGroup): string[] {
  return [...new Set(group.items.map((item) => item.stepType).filter(Boolean))];
}

function shouldShowPayloadPreview(
  item: RunEventRow,
  selected: boolean,
): boolean {
  return (
    selected ||
    item.eventStatus === "error" ||
    item.eventCategory === "human_input" ||
    item.eventCategory === "human_approval" ||
    item.eventCategory === "wait_signal"
  );
}

const RunsTimelineView: React.FC<RunsTimelineViewProps> = ({
  groups,
  onSelectItem,
  selectedItemKey,
}) => (
  <div style={workbenchConsoleSurfaceStyle}>
    <div style={timelineHeaderStyle}>Execution timeline</div>
    <div style={workbenchConsoleScrollStyle}>
      {groups.length > 0 ? (
        <div style={timelineBodyStyle}>
          {groups.map((group) => (
            <div key={group.key} style={timelineGroupStyle}>
              {(() => {
                const agents = getGroupAgents(group);
                const stepTypes = getGroupStepTypes(group);
                const latestItem = group.items[0];
                const categoryLabel = latestItem
                  ? eventCategoryValueEnum[latestItem.eventCategory].text
                  : "Observed";
                const progressionLabel = buildProgressionLabel(group);

                return (
                  <>
              <Space
                align="center"
                style={{ justifyContent: "space-between", width: "100%" }}
                wrap
              >
                <Space align="center" size={8} wrap>
                  <Badge status={statusBadgeMap[group.status]} />
                  <Typography.Text strong>{group.label}</Typography.Text>
                  <Tag color={statusTagToneMap[group.status]}>
                    {eventStatusValueEnum[group.status].text}
                  </Tag>
                </Space>
                <Typography.Text type="secondary">
                  {group.latestTimestamp || "n/a"}
                </Typography.Text>
              </Space>
                    <div style={timelineGroupMetaStyle}>
                      <Space wrap size={[6, 6]}>
                        <Tag>{group.eventCount} events</Tag>
                        <Tag color={categoryTagToneMap[latestItem?.eventCategory ?? "state"]}>
                          {categoryLabel}
                        </Tag>
                        {progressionLabel ? <Tag>{progressionLabel}</Tag> : null}
                      </Space>
                      <div style={timelineGroupSummaryStyle}>
                        <Space wrap size={[10, 6]}>
                          {agents.length === 1 ? (
                            <span>Agent {agents[0]}</span>
                          ) : agents.length > 1 ? (
                            <span>{agents.length} agents</span>
                          ) : null}
                          {stepTypes.length === 1 ? (
                            <span>Step type {stepTypes[0]}</span>
                          ) : stepTypes.length > 1 ? (
                            <span>{stepTypes.length} step modes</span>
                          ) : null}
                        </Space>
                      </div>
                    </div>
                  </>
                );
              })()}

              <div style={{ marginTop: 8 }}>
                {group.items.map((item, index) => (
                  <button
                    aria-label={`Select trace item ${item.eventType}`}
                    aria-pressed={selectedItemKey === item.key}
                    key={item.key}
                    onClick={() => onSelectItem?.(item)}
                    style={timelineSelectButtonStyle}
                    type="button"
                  >
                    {(() => {
                      const selected = selectedItemKey === item.key;
                      const groupAgents = getGroupAgents(group);
                      const showAgent = Boolean(item.agentId) && groupAgents.length > 1;
                      const showPayload = shouldShowPayloadPreview(item, selected);

                      return (
                    <div
                      style={{
                        ...timelineRowStyle,
                        background:
                          selected
                            ? "var(--ant-color-primary-bg)"
                            : item.eventStatus === "error"
                              ? "rgba(255, 77, 79, 0.04)"
                            : "transparent",
                        borderRadius: 10,
                        borderTopWidth: index === 0 ? 0 : 1,
                        outline:
                          selected
                            ? "1px solid var(--ant-color-primary-border)"
                            : "none",
                        paddingInline: 8,
                        paddingTop: index === 0 ? 6 : 8,
                      }}
                    >
                      <div style={timelineMetaStyle}>
                        <Space align="center" size={6}>
                          <Badge status={statusBadgeMap[item.eventStatus]} />
                          <Typography.Text style={timelineTimeTextStyle}>
                            {item.timestamp || "n/a"}
                          </Typography.Text>
                        </Space>
                        <Typography.Text style={timelineSecondaryMetaStyle}>
                          {index === 0
                            ? "Latest"
                            : eventStatusValueEnum[item.eventStatus].text}
                        </Typography.Text>
                      </div>
                      <div style={timelineRowBodyStyle}>
                        <div style={timelineRowHeadlineStyle}>
                          <Tag color={statusTagToneMap[item.eventStatus]}>
                            {eventCategoryValueEnum[item.eventCategory].text}
                          </Tag>
                          <Typography.Text strong>{item.eventType}</Typography.Text>
                        </div>
                        <div style={timelineRowDetailStyle}>
                          {showAgent ? (
                            <Typography.Text type="secondary">
                              {item.agentId}
                            </Typography.Text>
                          ) : null}
                          {item.stepType ? (
                            <Typography.Text type="secondary">
                              {item.stepType}
                            </Typography.Text>
                          ) : null}
                          {selected ? (
                            <Typography.Text type="secondary">
                              Selected in inspector
                            </Typography.Text>
                          ) : null}
                        </div>
                        <Typography.Text>{item.description}</Typography.Text>
                        {showPayload && item.payloadPreview ? (
                          <Typography.Paragraph
                            ellipsis={{ rows: 2, expandable: true, symbol: "more" }}
                            style={{ marginBottom: 0 }}
                            type="secondary"
                          >
                            {item.payloadPreview}
                          </Typography.Paragraph>
                        ) : null}
                      </div>
                    </div>
                      );
                    })()}
                  </button>
                ))}
              </div>
            </div>
          ))}
        </div>
      ) : (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="No timeline events observed yet."
        />
      )}
    </div>
  </div>
);

export default RunsTimelineView;
