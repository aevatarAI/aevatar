import { Typography, theme } from "antd";
import React from "react";
import {
  buildAevatarMetricCardStyle,
  resolveAevatarMetricVisual,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import {
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";

type SettingsPageShellProps = {
  children: React.ReactNode;
  content?: React.ReactNode;
  extra?: React.ReactNode;
  title?: string;
};

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
  tone?: "default" | "error" | "info" | "success" | "warning";
  value: React.ReactNode;
};

function renderSummaryFieldValue(value: React.ReactNode): React.ReactNode {
  if (typeof value === "string" || typeof value === "number") {
    return <Typography.Text>{value}</Typography.Text>;
  }

  return value;
}

export const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <div style={{ minWidth: 0 }}>{renderSummaryFieldValue(value)}</div>
  </div>
);

export const SummaryMetric: React.FC<SummaryMetricProps> = ({
  label,
  tone = "default",
  value,
}) => {
  const { token } = theme.useToken();
  const visual = resolveAevatarMetricVisual(
    token as AevatarThemeSurfaceToken,
    tone,
  );

  return (
    <div
      style={buildAevatarMetricCardStyle(
        token as AevatarThemeSurfaceToken,
        tone,
      )}
    >
      <Typography.Text style={{ ...summaryFieldLabelStyle, color: visual.labelColor }}>
        {label}
      </Typography.Text>
      <Typography.Text style={{ ...summaryMetricValueStyle, color: visual.valueColor }}>
        {value}
      </Typography.Text>
    </div>
  );
};

export const SettingsPageShell: React.FC<SettingsPageShellProps> = ({
  children,
  content,
  extra,
  title = "Account Settings",
}) => (
  <ConsoleMenuPageShell
    breadcrumb="Aevatar / Settings"
    description={content}
    extra={extra}
    title={title}
  >
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      {children}
    </div>
  </ConsoleMenuPageShell>
);
