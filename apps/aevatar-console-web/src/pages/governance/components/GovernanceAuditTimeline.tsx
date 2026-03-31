import {
  ApiOutlined,
  ClockCircleOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import { Empty, Space, Timeline, Typography, theme } from "antd";
import React from "react";
import { formatDateTime } from "@/shared/datetime/dateTime";
import {
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  formatAevatarStatusLabel,
  type AevatarStatusDomain,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";

export type GovernanceAuditEventTargetKind =
  | "service"
  | "policy"
  | "binding"
  | "endpoint"
  | "activation";

export interface GovernanceAuditEvent {
  id: string;
  actor: string;
  at: string;
  action: string;
  status: string;
  summary: string;
  targetKind: GovernanceAuditEventTargetKind;
  targetId: string;
  targetLabel: string;
}

type GovernanceAuditTimelineProps = {
  events: GovernanceAuditEvent[];
  loading?: boolean;
  onSelect?: (event: GovernanceAuditEvent) => void;
};

function buildEventDotColor(
  token: AevatarThemeSurfaceToken,
  status: string,
): string {
  const normalized = status.trim().toLowerCase();
  if (
    normalized === "blocked" ||
    normalized === "missing" ||
    normalized === "failed"
  ) {
    return token.colorError;
  }

  if (
    normalized === "retired" ||
    normalized === "disabled" ||
    normalized === "internal" ||
    normalized === "canary"
  ) {
    return token.colorWarning;
  }

  if (
    normalized === "active" ||
    normalized === "published" ||
    normalized === "public" ||
    normalized === "ready"
  ) {
    return token.colorSuccess;
  }

  return token.colorPrimary;
}

function renderEventIcon(kind: GovernanceAuditEventTargetKind) {
  if (kind === "policy" || kind === "activation") {
    return <SafetyCertificateOutlined />;
  }

  if (kind === "endpoint") {
    return <ApiOutlined />;
  }

  return <ClockCircleOutlined />;
}

const GovernanceAuditTimeline: React.FC<GovernanceAuditTimelineProps> = ({
  events,
  loading = false,
  onSelect,
}) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;

  if (!loading && events.length === 0) {
    return (
      <div
        style={{
          ...buildAevatarPanelStyle(surfaceToken, {
            background: surfaceToken.colorFillAlter,
            padding: 24,
          }),
          display: "flex",
          minHeight: 320,
        }}
      >
        <Empty
          description="Governance activity will appear here after a service is selected."
          style={{ margin: "auto" }}
        />
      </div>
    );
  }

  return (
    <div
      style={{
        ...buildAevatarPanelStyle(surfaceToken, {
          background: surfaceToken.colorBgContainer,
          padding: 20,
        }),
        minHeight: 0,
      }}
    >
      <Space
        direction="vertical"
        size={16}
        style={{ display: "flex", minHeight: 0 }}
      >
        <div>
          <Typography.Text strong>Governance Audit Timeline</Typography.Text>
          <Typography.Paragraph style={{ margin: "6px 0 0" }} type="secondary">
            Compiled from live governance catalogs, revision lifecycle state,
            and activation diagnostics so operators can follow who changed the
            surface, when it happened, and which capability was affected.
          </Typography.Paragraph>
        </div>

        <div style={{ minHeight: 0, overflowY: "auto", paddingRight: 4 }}>
          <Timeline
            pending={loading ? "Synchronizing governance activity..." : null}
            items={events.map((event) => ({
              color: buildEventDotColor(surfaceToken, event.status),
              dot: (
                <span
                  style={{
                    alignItems: "center",
                    color: buildEventDotColor(surfaceToken, event.status),
                    display: "inline-flex",
                    fontSize: 14,
                    justifyContent: "center",
                  }}
                >
                  {renderEventIcon(event.targetKind)}
                </span>
              ),
              children: (
                <button
                  onClick={() => onSelect?.(event)}
                  style={{
                    background: "transparent",
                    border: "none",
                    cursor: onSelect ? "pointer" : "default",
                    display: "block",
                    padding: 0,
                    textAlign: "left",
                    width: "100%",
                  }}
                  type="button"
                >
                  <div
                    style={{
                      ...buildAevatarPanelStyle(surfaceToken, {
                        background: surfaceToken.colorFillAlter,
                        padding: 14,
                      }),
                      boxShadow: "none",
                    }}
                  >
                    <Space
                      align="start"
                      direction="vertical"
                      size={10}
                      style={{ display: "flex" }}
                    >
                      <Space
                        align="center"
                        style={{
                          justifyContent: "space-between",
                          width: "100%",
                        }}
                        wrap
                      >
                        <Space size={[8, 8]} wrap>
                          <Typography.Text strong>{event.action}</Typography.Text>
                          <span
                            style={buildAevatarTagStyle(
                              surfaceToken,
                              "governance" as AevatarStatusDomain,
                              event.status,
                            )}
                          >
                            {formatAevatarStatusLabel(event.status)}
                          </span>
                        </Space>
                        <Typography.Text type="secondary">
                          {formatDateTime(event.at)}
                        </Typography.Text>
                      </Space>

                      <Typography.Paragraph style={{ margin: 0 }}>
                        {event.summary}
                      </Typography.Paragraph>

                      <Space size={[8, 8]} wrap>
                        <Typography.Text type="secondary">
                          Actor: {event.actor}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          Target: {event.targetLabel}
                        </Typography.Text>
                      </Space>
                    </Space>
                  </div>
                </button>
              ),
            }))}
          />
        </div>
      </Space>
    </div>
  );
};

export default GovernanceAuditTimeline;
