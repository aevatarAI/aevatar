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

export function buildSettingsSurfaceStyle(
  token: AevatarThemeSurfaceToken,
): React.CSSProperties {
  return {
    background: `linear-gradient(180deg, ${token.colorBgContainer} 0%, ${token.colorBgLayout} 100%)`,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: 16,
    boxShadow: "0 16px 36px rgba(15, 23, 42, 0.045)",
  };
}

export function buildSettingsPanelStyle(
  token: AevatarThemeSurfaceToken,
): React.CSSProperties {
  return {
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: 12,
    boxShadow: "0 8px 22px rgba(15, 23, 42, 0.04)",
  };
}

export function buildSettingsInsetCardStyle(
  token: AevatarThemeSurfaceToken,
): React.CSSProperties {
  return {
    background: token.colorFillTertiary,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: 12,
    padding: 14,
  };
}

export function buildSettingsSwitchRailStyle(
  token: AevatarThemeSurfaceToken,
): React.CSSProperties {
  return {
    alignSelf: "flex-start",
    alignItems: "center",
    background: token.colorFillTertiary,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: 999,
    display: "inline-flex",
    gap: 6,
    maxWidth: "100%",
    padding: 3,
    width: "fit-content",
  };
}

export function buildSettingsSwitchButtonStyle(
  token: AevatarThemeSurfaceToken,
  active: boolean,
): React.CSSProperties {
  return {
    background: active ? token.colorBgContainer : "transparent",
    border: "none",
    borderRadius: 999,
    boxShadow: active ? "0 4px 14px rgba(15, 23, 42, 0.07)" : "none",
    color: active ? token.colorTextHeading : token.colorTextSecondary,
    cursor: "pointer",
    fontSize: 13,
    fontWeight: 700,
    lineHeight: 1,
    padding: "9px 14px",
    transition: "all 160ms ease",
  };
}

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
      style={{
        ...buildAevatarMetricCardStyle(
          token as AevatarThemeSurfaceToken,
          tone,
        ),
        borderRadius: 12,
        boxShadow: "0 8px 20px rgba(15, 23, 42, 0.035)",
      }}
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
}) => {
  const { token } = theme.useToken();

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Settings"
      description={content}
      extra={
        extra ? (
          <div
            style={{
              display: "flex",
              justifyContent: "flex-end",
              paddingTop: 26,
            }}
          >
            {extra}
          </div>
        ) : undefined
      }
      surfacePadding={20}
      surfaceStyle={buildSettingsSurfaceStyle(token as AevatarThemeSurfaceToken)}
      title={title}
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        {children}
      </div>
    </ConsoleMenuPageShell>
  );
};
