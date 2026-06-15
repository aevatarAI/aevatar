import {
  ApiOutlined,
  BranchesOutlined,
  ClockCircleOutlined,
  DeploymentUnitOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import { Button, Grid, Space, Tag, Typography, theme } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import type {
  PlatformOverviewModule,
  PlatformOverviewModuleKey,
} from "@/shared/models/platform";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import { t } from "@/shared/i18n/messages";
import { getPlatformOverviewModules } from "./overviewModules";

const moduleIconByKey: Record<PlatformOverviewModuleKey, React.ReactNode> = {
  capabilities: <ApiOutlined />,
  accessRules: <SafetyCertificateOutlined />,
  releases: <DeploymentUnitOutlined />,
  runs: <ClockCircleOutlined />,
  runtimeMap: <BranchesOutlined />,
};

const overviewStackStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 18,
  minWidth: 0,
  width: "100%",
};

const journeyGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 12,
  gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))",
  width: "100%",
};

const moduleGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 14,
  gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))",
  width: "100%",
};

const journeyCardStyle: React.CSSProperties = {
  background: "var(--ant-color-bg-container)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 8,
  display: "flex",
  flexDirection: "column",
  gap: 6,
  minHeight: 112,
  minWidth: 0,
  padding: 14,
};

function buildModuleCardStyle(isCompact: boolean): React.CSSProperties {
  return {
    background: "var(--ant-color-bg-container)",
    border: "1px solid var(--ant-color-border-secondary)",
    borderRadius: 8,
    boxShadow: "0 8px 20px rgba(15, 23, 42, 0.05)",
    display: "flex",
    flexDirection: "column",
    gap: 14,
    minHeight: isCompact ? 0 : 250,
    minWidth: 0,
    padding: 18,
  };
}

const moduleHeaderStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  gap: 12,
  minWidth: 0,
};

const moduleIconStyle: React.CSSProperties = {
  alignItems: "center",
  background: "var(--ant-color-primary-bg)",
  border: "1px solid var(--ant-color-primary-border)",
  borderRadius: 8,
  color: "var(--ant-color-primary)",
  display: "inline-flex",
  flex: "0 0 auto",
  fontSize: 18,
  height: 38,
  justifyContent: "center",
  width: 38,
};

const sectionHeaderStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 4,
  minWidth: 0,
};

const PlatformJourneyCard: React.FC<{
  description: string;
  label: string;
  step: string;
}> = ({ description, label, step }) => (
  <div style={journeyCardStyle}>
    <Typography.Text style={{ color: "var(--ant-color-text-tertiary)", fontSize: 12, fontWeight: 700 }}>
      {step}
    </Typography.Text>
    <Typography.Text style={{ color: "var(--ant-color-text-heading)", fontSize: 15, fontWeight: 700 }}>
      {label}
    </Typography.Text>
    <Typography.Paragraph
      style={{ color: "var(--ant-color-text-secondary)", lineHeight: 1.55, margin: 0 }}
    >
      {description}
    </Typography.Paragraph>
  </div>
);

const PlatformModuleCard: React.FC<{
  isCompact: boolean;
  module: PlatformOverviewModule;
}> = ({ isCompact, module }) => (
  <div style={buildModuleCardStyle(isCompact)}>
    <div style={moduleHeaderStyle}>
      <div aria-hidden style={moduleIconStyle}>
        {moduleIconByKey[module.key]}
      </div>
      <div style={{ display: "flex", flex: 1, flexDirection: "column", gap: 6, minWidth: 0 }}>
        <Typography.Title level={3} style={{ fontSize: 18, lineHeight: 1.25, margin: 0 }}>
          {module.title}
        </Typography.Title>
        <Typography.Text style={{ color: "var(--ant-color-text-tertiary)", fontSize: 12 }}>
          {module.summary}
        </Typography.Text>
      </div>
    </div>

    <Typography.Paragraph
      style={{
        color: "var(--ant-color-text-secondary)",
        flex: 1,
        lineHeight: 1.6,
        margin: 0,
      }}
    >
      {module.description}
    </Typography.Paragraph>

    <Button
      block={isCompact}
      onClick={() => history.push(module.href)}
      type="primary"
    >
      {module.ctaLabel}
    </Button>
  </div>
);

const PlatformOverviewPage: React.FC = () => {
  const { token } = theme.useToken();
  const screens = Grid.useBreakpoint();
  const modules = getPlatformOverviewModules();
  const journeySteps = [
    {
      step: t("pages.platform.overview.journey.step1", "Step 1"),
      label: t("pages.platform.overview.journey.capability", "Confirm capability"),
      description: t(
        "pages.platform.overview.journey.capability.description",
        "Start from the published service entry instead of a backend object list.",
      ),
    },
    {
      step: t("pages.platform.overview.journey.step2", "Step 2"),
      label: t("pages.platform.overview.journey.rules", "Govern release"),
      description: t(
        "pages.platform.overview.journey.rules.description",
        "Check access, policy, binding, revision, and rollout facts before traffic moves.",
      ),
    },
    {
      step: t("pages.platform.overview.journey.step3", "Step 3"),
      label: t("pages.platform.overview.journey.run", "Run and inspect"),
      description: t(
        "pages.platform.overview.journey.run.description",
        "Open runs and the runtime map when a real execution needs observation.",
      ),
    },
  ];

  return (
    <ConsoleMenuPageShell
      breadcrumb={t("pages.platform.overview.breadcrumb", "Aevatar / Platform")}
      description={t(
        "pages.platform.overview.description",
        "A task-oriented entry for publishing, governing, running, and diagnosing capabilities. Each card keeps its existing deep link while the overview stays honest about what it has loaded.",
      )}
      surfacePadding={screens.md ? 24 : 16}
      surfaceStyle={{
        background: token.colorBgLayout,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 8,
        boxShadow: "none",
      }}
      title={t("pages.platform.overview.title", "Platform overview")}
    >
      <div style={overviewStackStyle}>
        <Space wrap size={[8, 8]}>
          <Tag color="blue">
            {t("pages.platform.overview.badge.deepLinks", "Stable deep links")}
          </Tag>
          <Tag color="green">
            {t("pages.platform.overview.badge.taskFirst", "Task-first workflow")}
          </Tag>
          <Tag>
            {t("pages.platform.overview.badge.noSyntheticHealth", "No synthetic health score")}
          </Tag>
        </Space>

        <section style={sectionHeaderStyle}>
          <Typography.Title level={2} style={{ fontSize: 18, margin: 0 }}>
            {t("pages.platform.overview.journey.title", "Publish-and-run path")}
          </Typography.Title>
          <Typography.Text style={{ color: token.colorTextSecondary }}>
            {t(
              "pages.platform.overview.journey.description",
              "The overview names the main operator tasks; detailed facts load inside the unchanged workbenches.",
            )}
          </Typography.Text>
        </section>

        <div style={journeyGridStyle}>
          {journeySteps.map((step) => (
            <PlatformJourneyCard
              description={step.description}
              key={step.label}
              label={step.label}
              step={step.step}
            />
          ))}
        </div>

        <section style={sectionHeaderStyle}>
          <Typography.Title level={2} style={{ fontSize: 18, margin: 0 }}>
            {t("pages.platform.overview.modules.title", "Platform modules")}
          </Typography.Title>
          <Typography.Text style={{ color: token.colorTextSecondary }}>
            {t(
              "pages.platform.overview.modules.description",
              "Use these stable entry points to move from capability discovery to access, release, run, and diagnostics.",
            )}
          </Typography.Text>
        </section>

        <div style={moduleGridStyle}>
          {modules.map((module) => (
            <PlatformModuleCard
              isCompact={!screens.md}
              key={module.key}
              module={module}
            />
          ))}
        </div>
      </div>
    </ConsoleMenuPageShell>
  );
};

export default PlatformOverviewPage;
