import { Badge, Button, Space, Tag, Typography } from "antd";
import React from "react";
import type { RunEndpointKind } from "@/shared/runs/endpointKinds";
import { AevatarCompactText } from "@/shared/ui/compactText";
import type { RunTransport } from "../runEventPresentation";
import { t } from "@/shared/i18n/messages";

type RunsStatusStripProps = {
  activeStepCount: number;
  elapsedLabel: string;
  eventCount: number;
  hasPendingInteraction: boolean;
  isRunLive: boolean;
  messageCount: number;
  onAbort: () => void;
  onOpenDetails: () => void;
  onOpenSetup: () => void;
  runId: string;
  runStatusLabel: string;
  compact?: boolean;
  showSetupAction?: boolean;
  statusTone: "success" | "processing" | "error" | "default";
  transport: RunTransport;
  endpointId: string;
  endpointKind: RunEndpointKind;
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
  onOpenDetails,
  onOpenSetup,
  runId,
  runStatusLabel,
  compact = false,
  showSetupAction = true,
  statusTone,
  transport,
  endpointId,
  endpointKind,
}) => {
  const transportLabel =
    endpointKind === "chat" ? transport.toUpperCase() : "INVOKE";

  return (
    <div style={stripStyle}>
      <div style={metricsWrapStyle}>
        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>{t("pages.runs.runsstatusstrip.status", "Status")}</Typography.Text>
          <Space size={6}>
            <Badge status={statusTone} />
            <Typography.Text style={metricValueStyle}>
              {runStatusLabel}
            </Typography.Text>
          </Space>
        </div>

        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>{t("pages.runs.runsstatusstrip.run", "Run")}</Typography.Text>
          <Typography.Text style={metricValueStyle}>
            {runId
              ? t("pages.runs.runsstatusstrip.current.run.ready", "Current run ready")
              : t("pages.runs.runsstatusstrip.pending", "Pending")}
          </Typography.Text>
        </div>

        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>{t("pages.runs.runsstatusstrip.elapsed", "Elapsed")}</Typography.Text>
          <Typography.Text style={metricValueStyle}>{elapsedLabel}</Typography.Text>
        </div>

        <div style={metricPillStyle}>
          <Typography.Text style={metricLabelStyle}>{t("pages.runs.runsstatusstrip.endpoint", "Endpoint")}</Typography.Text>
          <AevatarCompactText
            maxWidth={180}
            monospace={endpointKind !== "chat"}
            strong
            style={metricValueStyle}
            value={endpointId || "chat"}
          />
        </div>

        {!compact ? (
          <>
            <div style={metricPillStyle}>
              <Typography.Text style={metricLabelStyle}>{t("pages.runs.runsstatusstrip.transport", "Transport")}</Typography.Text>
              <Tag color="processing">
                {transportLabel}
              </Tag>
            </div>

            <div style={metricPillStyle}>
              <Typography.Text style={metricLabelStyle}>{t("pages.runs.runsstatusstrip.activity", "Activity")}</Typography.Text>
              <Typography.Text style={metricValueStyle}>
                {messageCount} {t("pages.runs.runsstatusstrip.msg", "msg ·")}{eventCount} {t("pages.runs.runsstatusstrip.evt", "evt ·")}{activeStepCount} {t("pages.runs.runsstatusstrip.active", "active")}</Typography.Text>
            </div>
          </>
        ) : null}
      </div>

      <div style={actionWrapStyle}>
        {showSetupAction ? (
          <Button
            size="small"
            onClick={onOpenSetup}
            type="default"
          >
            {t("pages.runs.runsstatusstrip.run.setup", "Run setup")}</Button>
        ) : null}
        <Button
          size="small"
          onClick={onOpenDetails}
          type={hasPendingInteraction ? "primary" : "default"}
        >
          {t("pages.runs.runsstatusstrip.details", "Details")}</Button>
        <Button
          danger
          size="small"
          type="primary"
          disabled={!isRunLive}
          onClick={onAbort}
        >
          {t("pages.runs.runsstatusstrip.abort", "Abort")}</Button>
      </div>
    </div>
  );
};

export default RunsStatusStrip;
