import { UserOutlined } from "@ant-design/icons";
import { Menu, Space, Typography } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import { CONSOLE_HOME_ROUTE } from "@/shared/navigation/consoleHome";
import {
  buildAevatarMetricCardStyle,
  resolveAevatarMetricVisual,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import {
  AevatarPageShell,
  AevatarPanel,
  AevatarWorkbenchLayout,
} from "@/shared/ui/aevatarPageShells";
import {
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";
import { theme } from "antd";

type SettingsPageShellProps = {
  children: React.ReactNode;
  content: string;
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

const settingsTabs = [
  {
    icon: <UserOutlined />,
    key: "account",
    label: "Account",
    path: "/settings",
  },
] as const;

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
  title = "Settings",
}) => (
  <AevatarPageShell
    content={content}
    onBack={() => history.push(CONSOLE_HOME_ROUTE)}
    title={title}
  >
    <AevatarWorkbenchLayout
      rail={
        <AevatarPanel
          description="Workspace preferences, access posture, and identity stay anchored in the same focused shell as the rest of the console."
          title="Preferences"
        >
          <Menu
            items={[...settingsTabs]}
            mode="inline"
            onClick={({ key }) => {
              const target = settingsTabs.find((item) => item.key === key);
              if (target) {
                history.push(target.path);
              }
            }}
            selectedKeys={["account"]}
            style={{ background: "transparent", borderInlineEnd: "none" }}
          />
          <Space direction="vertical" size={4}>
            <Typography.Text strong>Industrial minimalism</Typography.Text>
            <Typography.Text type="secondary">
              Settings pages use the same viewport, panel density, and navigation
              rhythm as production modules so the console feels continuous.
            </Typography.Text>
          </Space>
        </AevatarPanel>
      }
      stage={<div style={{ display: "flex", flexDirection: "column", gap: 16 }}>{children}</div>}
      stageAside={null}
    />
  </AevatarPageShell>
);
