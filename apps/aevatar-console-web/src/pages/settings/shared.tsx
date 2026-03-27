import { PageContainer } from "@ant-design/pro-components";
import { history } from "@/shared/navigation/history";
import { Typography } from "antd";
import React from "react";
import {
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";

type SettingsPageShellProps = {
  children: React.ReactNode;
  content: string;
};

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
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

export const SummaryMetric: React.FC<SummaryMetricProps> = ({ label, value }) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

export const SettingsPageShell: React.FC<SettingsPageShellProps> = ({
  children,
  content,
}) => (
  <PageContainer
    title="Settings"
    content={content}
    onBack={() => history.push("/overview")}
  >
    {children}
  </PageContainer>
);
