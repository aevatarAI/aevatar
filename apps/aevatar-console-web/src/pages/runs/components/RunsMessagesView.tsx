import { Empty, Space, Tag, Typography } from "antd";
import React from "react";
import { cardListStyle } from "@/shared/ui/proComponents";
import { AevatarCompactText } from "@/shared/ui/compactText";
import {
  workbenchConsoleScrollStyle,
  workbenchConsoleSurfaceStyle,
} from "../runWorkbenchConfig";

export type RunMessageRecord = {
  complete?: boolean;
  content?: string;
  messageId: string;
  role?: string;
};

type RunsMessagesViewProps = {
  emptyDescription?: string;
  messages: RunMessageRecord[];
  topAccessory?: React.ReactNode;
  title?: string;
};

const panelHeaderStyle: React.CSSProperties = {
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  padding: "10px 12px",
};

const messageListStyle: React.CSSProperties = {
  ...cardListStyle,
  gap: 10,
};

const messageConsoleBodyStyle: React.CSSProperties = {
  ...cardListStyle,
  gap: 12,
};

const accessoryWrapStyle: React.CSSProperties = {
  alignSelf: "center",
  maxWidth: 820,
  width: "100%",
};

const messageCardStyle: React.CSSProperties = {
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 12,
  display: "flex",
  flexDirection: "column",
  gap: 10,
  maxWidth: "88%",
  minWidth: 0,
  padding: 12,
};

const messageMetaStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  minWidth: 0,
};

const messageIdStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontFamily:
    "'Monaco', 'Consolas', 'SFMono-Regular', 'Liberation Mono', monospace",
  fontSize: 12,
  overflowWrap: "anywhere",
};

const messageBodyStyle: React.CSSProperties = {
  color: "var(--ant-color-text)",
  margin: 0,
  whiteSpace: "pre-wrap",
  wordBreak: "break-word",
};

function resolveRoleTone(role: string): "blue" | "cyan" | "gold" | "default" {
  switch (role.toLowerCase()) {
    case "assistant":
      return "blue";
    case "system":
      return "gold";
    case "tool":
      return "cyan";
    default:
      return "default";
  }
}

const RunsMessagesView: React.FC<RunsMessagesViewProps> = ({
  emptyDescription = "No message output yet.",
  messages,
  topAccessory,
  title = "Message stream",
}) => (
  <div style={workbenchConsoleSurfaceStyle}>
    <div style={panelHeaderStyle}>
      <Space wrap size={[8, 8]}>
        <Typography.Text type="secondary">{title}</Typography.Text>
        <Tag>{messages.length} observed</Tag>
      </Space>
    </div>
    <div style={workbenchConsoleScrollStyle}>
      <div style={messageConsoleBodyStyle}>
        {messages.length > 0 ? (
          <div style={messageListStyle}>
            {topAccessory ? (
              <div style={accessoryWrapStyle}>{topAccessory}</div>
            ) : null}
            {messages.map((record) => {
              const role = record.role || "message";
              const streaming = record.complete !== true;

              return (
                <div
                  key={record.messageId}
                  style={{
                    ...messageCardStyle,
                    alignSelf: role === "user" ? "flex-end" : "flex-start",
                    background:
                      role === "user"
                        ? "rgba(22, 119, 255, 0.10)"
                        : "rgba(15, 23, 42, 0.03)",
                    borderColor: streaming
                      ? "rgba(22, 119, 255, 0.28)"
                      : "var(--ant-color-border-secondary)",
                  }}
                >
                  <div style={messageMetaStyle}>
                    <Tag color={resolveRoleTone(role)}>{role}</Tag>
                    <Tag color={streaming ? "processing" : "success"}>
                      {streaming ? "streaming" : "complete"}
                    </Tag>
                  <AevatarCompactText
                    monospace
                    style={messageIdStyle}
                    value={record.messageId}
                  />
                  </div>
                  <Typography.Paragraph style={messageBodyStyle}>
                    {record.content || "(streaming...)"}
                  </Typography.Paragraph>
                </div>
              );
            })}
          </div>
        ) : (
          <>
            {topAccessory ? (
              <div style={accessoryWrapStyle}>{topAccessory}</div>
            ) : null}
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={emptyDescription}
            />
          </>
        )}
      </div>
    </div>
  </div>
);

export default RunsMessagesView;
