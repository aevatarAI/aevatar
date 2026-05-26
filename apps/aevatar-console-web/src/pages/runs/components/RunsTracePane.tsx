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
  messagesLabel = "Messages",
  onConsoleViewChange,
  timelineView,
  title = "Run trace",
}) => {
  const preferMessagesFirst = messagesLabel === "Conversation";
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
          label: "Timeline",
          children: (
            <div style={workbenchTraceTabPanelStyle}>{timelineView}</div>
          ),
        },
        {
          key: "events",
          label: "Events",
          children: (
            <div style={workbenchTraceTabPanelStyle}>{eventConsoleView}</div>
          ),
        },
      ]
    : [
        {
          key: "timeline",
          label: "Timeline",
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
          label: "Events",
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
            {messageCount} messages
          </Typography.Text>
          <Typography.Text type="secondary">
            {eventCount} events
          </Typography.Text>
          <Typography.Text type="secondary">
            {hasPendingInteraction ? "action required" : "live trace"}
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
