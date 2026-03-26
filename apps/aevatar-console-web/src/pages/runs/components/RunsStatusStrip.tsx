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
  workflowName: string;
};

const stripStyle: React.CSSProperties = {
  alignItems: "center",
  backdropFilter: "blur(8px)",
  background: "var(--ant-color-bg-container)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 14,
  display: "flex",
  gap: 12,
  justifyContent: "space-between",
  minHeight: 64,
  padding: "12px 16px",
};

const metricsWrapStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexWrap: "wrap",
  gap: 8,
  minWidth: 0,
};

const metricPillStyle: React.CSSProperties = {
  alignItems: "flex-start",
  background: "var(--ant-color-fill-quaternary)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 12,
  display: "flex",
  flexDirection: "column",
  gap: 4,
  minWidth: 108,
  padding: "8px 10px",
};

const metricLabelStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 12,
  lineHeight: 1,
};

const metricValueStyle: React.CSSProperties = {
  color: "var(--ant-color-text)",
  fontSize: 13,
  fontWeight: 600,
  lineHeight: 1.3,
};

const actionWrapStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flex: "0 0 auto",
  flexWrap: "wrap",
  gap: 8,
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
  workflowName,
}) => (
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
        <Typography.Text style={metricLabelStyle}>Workflow</Typography.Text>
        <Typography.Text style={metricValueStyle}>
          {workflowName || "n/a"}
        </Typography.Text>
      </div>

      <div style={metricPillStyle}>
        <Typography.Text style={metricLabelStyle}>Transport</Typography.Text>
        <Tag color="processing">
          {transport.toUpperCase()}
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
        onClick={onOpenInspector}
        type={hasPendingInteraction ? "primary" : "default"}
      >
        {hasPendingInteraction ? "Interaction pending" : "Inspector"}
      </Button>
      <Button danger type="primary" disabled={!isRunLive} onClick={onAbort}>
        Abort
      </Button>
    </div>
  </div>
);

export default RunsStatusStrip;
