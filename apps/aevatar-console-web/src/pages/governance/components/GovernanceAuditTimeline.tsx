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
import { AEVATAR_INTERACTIVE_BUTTON_CLASS } from "@/shared/ui/interactionStandards";
import { t } from "@/shared/i18n/messages";

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

function renderEventCard(
  surfaceToken: AevatarThemeSurfaceToken,
  event: GovernanceAuditEvent,
) {
  return (
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
        orientation="vertical"
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
            {t("pages.governance.governanceaudittimeline.source.2", "source:")}{event.actor}
          </Typography.Text>
          <Typography.Text type="secondary">
            {t("pages.governance.governanceaudittimeline.object.2", "Object:")}{event.targetLabel}
          </Typography.Text>
        </Space>
      </Space>
    </div>
  );
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
          description={t("pages.governance.governanceaudittimeline.no.change.record.yet.2", "No change record yet")}
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
        orientation="vertical"
        size={16}
        style={{ display: "flex", minHeight: 0 }}
      >
        <Typography.Text strong>{t("pages.governance.governanceaudittimeline.change.history.2", "Change history")}</Typography.Text>

        <div style={{ minHeight: 0, overflowY: "auto", paddingRight: 4 }}>
          <Timeline
            pending={loading ? t("pages.governance.governanceaudittimeline.loading.2", "loading...") : null}
            items={events.map((event) => ({
              color: buildEventDotColor(surfaceToken, event.status),
              icon: (
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
              content: onSelect ? (
                <button
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  onClick={() => onSelect(event)}
                  style={{
                    background: "transparent",
                    border: "none",
                    cursor: "pointer",
                    display: "block",
                    padding: 0,
                    textAlign: "left",
                    width: "100%",
                  }}
                  type="button"
                >
                  {renderEventCard(surfaceToken, event)}
                </button>
              ) : (
                renderEventCard(surfaceToken, event)
              ),
            }))}
          />
        </div>
      </Space>
    </div>
  );
};

export default GovernanceAuditTimeline;
