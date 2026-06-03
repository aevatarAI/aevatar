import { ProCard } from "@ant-design/pro-components";
import { Divider, Space, Tabs, Typography } from "antd";
import React from "react";
import { moduleCardProps } from "@/shared/ui/proComponents";
import type { ConsoleViewKey } from "../runWorkbenchConfig";
import {
  workbenchCardStyle,
  workbenchConsoleBodyStyle,
  workbenchConsoleViewportStyle,
  workbenchTraceTabPanelStyle,
  workbenchTraceTabsStyle,
} from "../runWorkbenchConfig";
import { t } from "@/shared/i18n/messages";

const runsTraceTabsClassName = "runs-trace-tabs";
const runsTraceTabsCss = `
.${runsTraceTabsClassName} {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.${runsTraceTabsClassName} .ant-tabs-content-holder {
  display: flex;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.${runsTraceTabsClassName} .ant-tabs-content {
  display: flex;
  flex: 1;
  min-height: 0;
}

.${runsTraceTabsClassName} .ant-tabs-tabpane-hidden {
  display: none !important;
}

.${runsTraceTabsClassName} .ant-tabs-tabpane-active {
  display: flex !important;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}
`;

type RunsTracePaneProps = {
  consoleView: ConsoleViewKey;
  eventConsoleView: React.ReactNode;
  eventCount: number;
  hasPendingInteraction: boolean;
  messageConsoleView: React.ReactNode;
  messageCount: number;
  messagesLabel?: string;
  onConsoleViewChange: (key: ConsoleViewKey) => void;
  preferMessagesFirst?: boolean;
  timelineView: React.ReactNode;
  title?: string;
};

const RunsTracePane: React.FC<RunsTracePaneProps> = ({
  consoleView,
  eventConsoleView,
  eventCount,
  hasPendingInteraction,
  messageConsoleView,
  messageCount,
  messagesLabel = t("pages.runs.runstracepane.messages.label", "Messages"),
  onConsoleViewChange,
  preferMessagesFirst = false,
  timelineView,
  title = t("pages.runs.runstracepane.run.trace", "Run trace"),
}) => {
  const tabItems = preferMessagesFirst
    ? [
        {
          key: "messages",
          label: messagesLabel,
          children: (
            <div style={workbenchTraceTabPanelStyle}>{messageConsoleView}</div>
          ),
        },
        {
          key: "timeline",
          label: t("pages.runs.runstracepane.timeline", "Timeline"),
          children: (
            <div style={workbenchTraceTabPanelStyle}>{timelineView}</div>
          ),
        },
        {
          key: "events",
          label: t("pages.runs.runstracepane.events.label", "Events"),
          children: (
            <div style={workbenchTraceTabPanelStyle}>{eventConsoleView}</div>
          ),
        },
      ]
    : [
        {
          key: "timeline",
          label: t("pages.runs.runstracepane.timeline", "Timeline"),
          children: (
            <div style={workbenchTraceTabPanelStyle}>{timelineView}</div>
          ),
        },
        {
          key: "messages",
          label: messagesLabel,
          children: (
            <div style={workbenchTraceTabPanelStyle}>{messageConsoleView}</div>
          ),
        },
        {
          key: "events",
          label: t("pages.runs.runstracepane.events.label", "Events"),
          children: (
            <div style={workbenchTraceTabPanelStyle}>{eventConsoleView}</div>
          ),
        },
      ];

  return (
    <ProCard
      title={title}
      hoverable
      {...moduleCardProps}
      style={workbenchCardStyle}
      bodyStyle={workbenchConsoleBodyStyle}
      extra={
        <Space separator={<Divider orientation="vertical" />} size={12}>
          <Typography.Text type="secondary">
            {t("pages.runs.runstracepane.messages.count", "{count} messages", {
              count: messageCount,
            })}</Typography.Text>
          <Typography.Text type="secondary">
            {t("pages.runs.runstracepane.events.count", "{count} events", {
              count: eventCount,
            })}</Typography.Text>
          <Typography.Text type="secondary">
            {hasPendingInteraction
              ? t("pages.runs.runstracepane.action.required", "action required")
              : t("pages.runs.runstracepane.live.trace", "live trace")}
          </Typography.Text>
        </Space>
      }
    >
      <div style={workbenchConsoleViewportStyle}>
        <style>{runsTraceTabsCss}</style>
        <Tabs
          activeKey={consoleView}
          animated={false}
          className={runsTraceTabsClassName}
          destroyOnHidden
          style={workbenchTraceTabsStyle}
          items={tabItems}
          onChange={(key) => onConsoleViewChange(key as ConsoleViewKey)}
        />
      </div>
    </ProCard>
  );
};

export default RunsTracePane;
