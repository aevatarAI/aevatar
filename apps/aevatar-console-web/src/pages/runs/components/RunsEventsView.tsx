import { Badge, Empty, Space, Tag, Typography } from "antd";
import React from "react";
import { cardListStyle, codeBlockStyle } from "@/shared/ui/proComponents";
import {
  eventCategoryValueEnum,
  eventStatusValueEnum,
  type RunEventCategory,
  type RunEventRow,
  type RunEventStatus,
} from "../runEventPresentation";
import {
  workbenchConsoleScrollStyle,
  workbenchConsoleSurfaceStyle,
} from "../runWorkbenchConfig";

type RunsEventsViewProps = {
  onSelectItem?: (item: RunEventRow) => void;
  rows: RunEventRow[];
  selectedItemKey?: string;
};

const statusBadgeMap: Record<
  RunEventStatus,
  "success" | "processing" | "error" | "default"
> = {
  default: "default",
  error: "error",
  processing: "processing",
  success: "success",
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
  RunEventStatus,
  "default" | "processing" | "success" | "warning" | "error"
> = {
  default: "default",
  error: "error",
  processing: "processing",
  success: "success",
};

const panelHeaderStyle: React.CSSProperties = {
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  padding: "10px 12px",
};

const eventListStyle: React.CSSProperties = {
  ...cardListStyle,
  gap: 10,
};

const eventSelectButtonStyle: React.CSSProperties = {
  background: "transparent",
  cursor: "pointer",
  outline: "none",
  width: "100%",
};

const eventCardStyle: React.CSSProperties = {
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 12,
  display: "flex",
  flexDirection: "column",
  gap: 10,
  minWidth: 0,
  padding: 14,
};

const eventHeaderRowStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  justifyContent: "space-between",
};

const eventHeaderLeadStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  minWidth: 0,
};

const eventTimestampStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontFamily:
    "'Monaco', 'Consolas', 'SFMono-Regular', 'Liberation Mono', monospace",
  fontSize: 12,
};

const eventMetaRowStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  minWidth: 0,
};

const eventMetaItemStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 12,
  lineHeight: 1.4,
};

const eventDescriptionStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  margin: 0,
  whiteSpace: "pre-wrap",
  wordBreak: "break-word",
};

const eventPayloadStyle: React.CSSProperties = {
  ...codeBlockStyle,
  margin: 0,
  maxHeight: 220,
  overflowX: "auto",
  overflowY: "auto",
  overscrollBehavior: "contain",
  whiteSpace: "pre",
  wordBreak: "normal",
  overflowWrap: "normal",
};

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

function buildEventMetaItems(item: RunEventRow): string[] {
  const items: string[] = [];

  if (item.stepId) {
    items.push(`Step ${item.stepId}`);
  }

  if (item.stepType) {
    items.push(`Mode ${item.stepType}`);
  }

  if (item.agentId) {
    items.push(`Agent ${item.agentId}`);
  }

  return items;
}

function handleSelectKeyDown(
  event: React.KeyboardEvent<HTMLDivElement>,
  item: RunEventRow,
  onSelectItem?: (item: RunEventRow) => void,
): void {
  if (event.key !== "Enter" && event.key !== " ") {
    return;
  }

  event.preventDefault();
  onSelectItem?.(item);
}

const RunsEventsView: React.FC<RunsEventsViewProps> = ({
  onSelectItem,
  rows,
  selectedItemKey,
}) => (
  <div style={workbenchConsoleSurfaceStyle}>
    <div style={panelHeaderStyle}>
      <Space wrap size={[8, 8]}>
        <Typography.Text type="secondary">Live event log</Typography.Text>
        <Tag>{rows.length} observed</Tag>
      </Space>
    </div>
    <div style={workbenchConsoleScrollStyle}>
      {rows.length > 0 ? (
        <div style={eventListStyle}>
          {rows.map((item) => {
            const selected = item.key === selectedItemKey;
            const metaItems = buildEventMetaItems(item);
            const showPayload = shouldShowPayloadPreview(item, selected);

            return (
              <div
                aria-label={`Select event ${item.eventType}`}
                aria-pressed={selected}
                key={item.key}
                onClick={() => onSelectItem?.(item)}
                onKeyDown={(event) => handleSelectKeyDown(event, item, onSelectItem)}
                role="button"
                style={eventSelectButtonStyle}
                tabIndex={0}
              >
                <div
                  style={{
                    ...eventCardStyle,
                    background:
                      selected
                        ? "var(--ant-color-primary-bg)"
                        : item.eventStatus === "error"
                          ? "rgba(255, 77, 79, 0.04)"
                          : "rgba(15, 23, 42, 0.02)",
                    borderColor:
                      selected
                        ? "var(--ant-color-primary-border)"
                        : item.eventStatus === "error"
                          ? "rgba(255, 77, 79, 0.24)"
                          : "var(--ant-color-border-secondary)",
                    outline:
                      selected
                        ? "1px solid var(--ant-color-primary-border)"
                        : "none",
                  }}
                >
                  <div style={eventHeaderRowStyle}>
                    <div style={eventHeaderLeadStyle}>
                      <Badge status={statusBadgeMap[item.eventStatus]} />
                      <Typography.Text style={eventTimestampStyle}>
                        {item.timestamp || "n/a"}
                      </Typography.Text>
                    </div>
                    <Space size={[6, 6]} wrap>
                      <Tag color={categoryTagToneMap[item.eventCategory]}>
                        {eventCategoryValueEnum[item.eventCategory].text}
                      </Tag>
                      <Tag color={statusTagToneMap[item.eventStatus]}>
                        {eventStatusValueEnum[item.eventStatus].text}
                      </Tag>
                      {selected ? <Tag color="processing">Selected</Tag> : null}
                    </Space>
                  </div>

                  <div>
                    <Typography.Text strong>{item.eventType}</Typography.Text>
                  </div>

                  {metaItems.length > 0 ? (
                    <div style={eventMetaRowStyle}>
                      {metaItems.map((value) => (
                        <Typography.Text
                          key={`${item.key}-${value}`}
                          style={eventMetaItemStyle}
                        >
                          {value}
                        </Typography.Text>
                      ))}
                    </div>
                  ) : null}

                  <Typography.Paragraph style={eventDescriptionStyle}>
                    {item.description}
                  </Typography.Paragraph>

                  {showPayload ? (
                    <pre
                      style={{
                        ...eventPayloadStyle,
                        maxHeight: selected ? 240 : 140,
                      }}
                    >
                      {selected ? item.payloadText : item.payloadPreview}
                    </pre>
                  ) : null}
                </div>
              </div>
            );
          })}
        </div>
      ) : (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="No events observed yet."
        />
      )}
    </div>
  </div>
);

export default RunsEventsView;
