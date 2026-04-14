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
  onConsoleViewChange: (key: ConsoleViewKey) => void;
  timelineView: React.ReactNode;
};

const RunsTracePane: React.FC<RunsTracePaneProps> = ({
  consoleView,
  eventConsoleView,
  eventCount,
  hasPendingInteraction,
  messageConsoleView,
  messageCount,
  onConsoleViewChange,
  timelineView,
}) => (
  <ProCard
    title="Run trace"
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
          {hasPendingInteraction ? "interaction pending" : "monitoring"}
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
        items={[
          {
            key: "timeline",
            label: "Timeline",
            children: <div style={workbenchTraceTabPanelStyle}>{timelineView}</div>,
          },
          {
            key: "messages",
            label: "Messages",
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
        ]}
        onChange={(key) => onConsoleViewChange(key as ConsoleViewKey)}
      />
    </div>
  </ProCard>
);

export default RunsTracePane;
