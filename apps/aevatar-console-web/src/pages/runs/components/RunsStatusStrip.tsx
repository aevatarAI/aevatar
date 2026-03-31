import { Badge, Button, Space, Tag, Typography } from "antd";
import React from "react";
import type { RunTransport } from "../runEventPresentation";

type RunsStatusStripProps = {
  activeStepCount: number;
  elapsedLabel: string;
  eventCount: number;
  hasPendingInteraction: boolean;
  isRunLive: boolean;
  messageCount: number;
  onAbort: () => void;
  onOpenInspector: () => void;
  runId: string;
  runStatusLabel: string;
  statusTone: "success" | "processing" | "error" | "default";
  transport: RunTransport;
  endpointId: string;
};

const stripStyle: React.CSSProperties = {
  alignItems: "center",
  backdropFilter: "blur(8px)",
  background: "var(--ant-color-bg-container)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 12,
  display: "flex",
  gap: 10,
  justifyContent: "space-between",
  minHeight: 58,
  padding: "10px 12px",
};

const metricsWrapStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexWrap: "wrap",
  gap: 6,
  minWidth: 0,
};

const metricPillStyle: React.CSSProperties = {
  alignItems: "flex-start",
  background: "var(--ant-color-fill-quaternary)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 10,
  display: "flex",
  flexDirection: "column",
  gap: 3,
  minWidth: 96,
  padding: "6px 8px",
};

const metricLabelStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 11,
  lineHeight: 1,
};

const metricValueStyle: React.CSSProperties = {
  color: "var(--ant-color-text)",
  fontSize: 12,
  fontWeight: 600,
  lineHeight: 1.3,
};

const actionWrapStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flex: "0 0 auto",
  flexWrap: "wrap",
  gap: 6,
  justifyContent: "flex-end",
};

const RunsStatusStrip: React.FC<RunsStatusStripProps> = ({
  activeStepCount,
  elapsedLabel,
  eventCount,
  hasPendingInteraction,
  isRunLive,
  messageCount,
  onAbort,
  onOpenInspector,
  runId,
  runStatusLabel,
  statusTone,
  transport,
  endpointId,
}) => {
  const transportLabel =
    endpointId && endpointId !== "chat" ? "INVOKE" : transport.toUpperCase();

  return (
    <div style={stripStyle}>
      <div style={metricsWrapStyle}>
        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>Status</Typography.Text>
          <Space size={6}>
            <Badge status={statusTone} />
            <Typography.Text style={metricValueStyle}>
              {runStatusLabel}
            </Typography.Text>
          </Space>
        </div>

        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>Run ID</Typography.Text>
          <Typography.Text code style={metricValueStyle}>
            {runId}
          </Typography.Text>
        </div>

        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>Elapsed</Typography.Text>
          <Typography.Text style={metricValueStyle}>{elapsedLabel}</Typography.Text>
        </div>

        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>Endpoint</Typography.Text>
          <Typography.Text style={metricValueStyle}>
            {endpointId || "chat"}
          </Typography.Text>
        </div>

        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>Transport</Typography.Text>
          <Tag color="processing">
            {transportLabel}
          </Tag>
        </div>

        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>Activity</Typography.Text>
          <Typography.Text style={metricValueStyle}>
            {messageCount} msg · {eventCount} evt · {activeStepCount} active
          </Typography.Text>
        </div>
      </div>

      <div style={actionWrapStyle}>
        <Button
          size="small"
          onClick={onOpenInspector}
          type={hasPendingInteraction ? "primary" : "default"}
        >
          {hasPendingInteraction ? "Interaction pending" : "Inspector"}
        </Button>
        <Button
          danger
          size="small"
          type="primary"
          disabled={!isRunLive}
          onClick={onAbort}
        >
          Abort
        </Button>
      </div>
    </div>
  );
};

export default RunsStatusStrip;
