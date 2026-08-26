import {
  ClockCircleOutlined,
  EyeOutlined,
  InfoCircleOutlined,
} from "@ant-design/icons";
import { Button, Tooltip, Typography, theme } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import { AevatarInspectorEmpty } from "@/shared/ui/aevatarPageShells";
import { DetailPill, FactLine } from "./TeamDetailPrimitives";

export type TeamActivityRunRow = {
  readonly detailsHref?: string;
  readonly detailItems: readonly {
    readonly label: string;
    readonly value: string;
  }[];
  readonly detailTooltipLabel: string;
  readonly memberLabel: string;
  readonly outputPreview: string;
  readonly runId: string;
  readonly statusKey: string;
  readonly statusLabel: string;
  readonly statusStyle: React.CSSProperties;
  readonly updatedLabel: string;
  readonly workflowLabel: string;
  readonly workflowMetaLabel: string;
};

type TeamRecentRunsListProps = {
  readonly emptyDescription: string;
  readonly emptyTitle: string;
  readonly onNavigate?: (href: string) => void;
  readonly runs: readonly TeamActivityRunRow[];
};

const TeamRecentRunsList: React.FC<TeamRecentRunsListProps> = ({
  emptyDescription,
  emptyTitle,
  onNavigate,
  runs,
}) => {
  const intl = useIntl();
  const { token } = theme.useToken();
  const handleNavigate = React.useCallback(
    (href?: string) => (event: React.MouseEvent<HTMLElement>) => {
      if (!href || !onNavigate) {
        return;
      }

      event.preventDefault();
      onNavigate(href);
    },
    [onNavigate],
  );

  if (runs.length === 0) {
    return (
      <div
        style={{
          alignItems: "center",
          border: `1px dashed ${token.colorBorderSecondary}`,
          borderRadius: 8,
          display: "flex",
          justifyContent: "center",
          minHeight: 144,
          padding: "24px 16px",
          textAlign: "center",
        }}
      >
        <AevatarInspectorEmpty
          compact
          description={emptyDescription}
          title={emptyTitle}
        />
      </div>
    );
  }

  return (
    <div style={{ display: "grid", gap: 10 }}>
      {runs.map((run) => {
        const statusAccent =
          typeof run.statusStyle.color === "string"
            ? run.statusStyle.color
            : token.colorBorder;

        return (
          <article
            key={run.runId}
            style={{
              alignItems: "start",
              background: token.colorBgContainer,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderInlineStart: `4px solid ${statusAccent}`,
              borderRadius: 8,
              display: "grid",
              gap: 12,
              gridTemplateColumns: "minmax(0, 1fr) max-content",
              minWidth: 0,
              padding: 14,
            }}
          >
            <div
              style={{
                display: "flex",
                flexDirection: "column",
                gap: 6,
                minWidth: 0,
              }}
            >
              <div
                style={{
                  alignItems: "center",
                  display: "flex",
                  flexWrap: "wrap",
                  gap: 8,
                }}
              >
                <DetailPill
                  compact
                  style={run.statusStyle}
                  text={run.statusLabel}
                />
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  <ClockCircleOutlined /> {run.updatedLabel}
                </Typography.Text>
                <Tooltip
                  title={
                    <div style={{ display: "grid", gap: 4 }}>
                      {run.detailItems.map((item) => (
                        <span key={item.label}>
                          {item.label}: {item.value}
                        </span>
                      ))}
                    </div>
                  }
                >
                  <button
                    aria-label={run.detailTooltipLabel}
                    style={{
                      background: "transparent",
                      border: 0,
                      color: token.colorTextTertiary,
                      cursor: "help",
                      lineHeight: 1,
                      padding: 0,
                    }}
                    type="button"
                  >
                    <InfoCircleOutlined />
                  </button>
                </Tooltip>
              </div>
              <Typography.Text strong>{run.memberLabel}</Typography.Text>
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {run.workflowMetaLabel}
              </Typography.Text>
              <FactLine rows={2} secondary text={run.outputPreview} />
            </div>
            {run.detailsHref ? (
              <Tooltip
                title={intl.formatMessage({
                  id: "teams.detail.overview.history.actions.view",
                })}
              >
                <Button
                  aria-label={intl.formatMessage({
                    id: "teams.detail.overview.history.actions.view",
                  })}
                  href={run.detailsHref}
                  icon={<EyeOutlined />}
                  onClick={handleNavigate(run.detailsHref)}
                />
              </Tooltip>
            ) : null}
          </article>
        );
      })}
    </div>
  );
};

export default TeamRecentRunsList;
