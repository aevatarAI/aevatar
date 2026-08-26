import {
  AppstoreOutlined,
  CloseCircleOutlined,
  ExclamationCircleOutlined,
  LoadingOutlined,
  PlayCircleOutlined,
  ReloadOutlined,
} from "@ant-design/icons";
import {
  Alert,
  Button,
  Grid,
  Segmented,
  Skeleton,
  Space,
  Tooltip,
  Typography,
  theme,
} from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import TeamRecentRunsList, {
  type TeamActivityRunRow,
} from "../components/TeamRecentRunsList";

type ActivityFilter = "all" | "attention" | "running" | "failed";

type TeamActivityTabProps = {
  readonly error: boolean;
  readonly loading: boolean;
  readonly onNavigate: (href: string) => void;
  readonly onOpenTeamTest?: () => void;
  readonly onRefresh: () => void;
  readonly refreshing: boolean;
  readonly runs: readonly TeamActivityRunRow[];
  readonly teamRunDisabled?: boolean;
  readonly teamRunDisabledReason?: string;
};

function normalizeStatus(status: string): string {
  return status.trim().toLowerCase();
}

function isAttentionStatus(status: string): boolean {
  const normalized = normalizeStatus(status);
  return ["approval", "input", "paused", "waiting", "action"].some((part) =>
    normalized.includes(part),
  );
}

function isRunningStatus(status: string): boolean {
  const normalized = normalizeStatus(status);
  return ["accepted", "queued", "retrying", "running", "streaming"].some(
    (part) => normalized.includes(part),
  );
}

function isFailedStatus(status: string): boolean {
  const normalized = normalizeStatus(status);
  return ["error", "fail", "timed_out", "timed out"].some((part) =>
    normalized.includes(part),
  );
}

const TeamActivityTab: React.FC<TeamActivityTabProps> = ({
  error,
  loading,
  onNavigate,
  onOpenTeamTest,
  onRefresh,
  refreshing,
  runs,
  teamRunDisabled = false,
  teamRunDisabledReason,
}) => {
  const intl = useIntl();
  const { token } = theme.useToken();
  const screens = Grid.useBreakpoint();
  const [filter, setFilter] = React.useState<ActivityFilter>("all");
  const useCompactFilterLabels = screens.xs === true && screens.sm !== true;
  const filteredRuns = React.useMemo(() => {
    switch (filter) {
      case "attention":
        return runs.filter((run) => isAttentionStatus(run.statusKey));
      case "running":
        return runs.filter((run) => isRunningStatus(run.statusKey));
      case "failed":
        return runs.filter((run) => isFailedStatus(run.statusKey));
      default:
        return runs;
    }
  }, [filter, runs]);
  const hasFilteredResults = filteredRuns.length > 0;
  const emptyTitle =
    runs.length > 0
      ? intl.formatMessage({
          defaultMessage: "No recent runs match this filter",
          id: "teams.detail.activity.filteredEmpty.title",
        })
      : intl.formatMessage({
          defaultMessage: "No recent activity yet",
          id: "teams.detail.activity.empty.title",
        });
  const emptyDescription =
    runs.length > 0
      ? intl.formatMessage({
          defaultMessage: "Choose another status to return to the visible runs.",
          id: "teams.detail.activity.filteredEmpty.description",
        })
      : intl.formatMessage({
          defaultMessage:
            "Run the team or a published member to create the first visible run.",
          id: "teams.detail.activity.empty.description",
        });
  const filterLabels = {
    all: intl.formatMessage({
      defaultMessage: "All",
      id: "teams.detail.activity.filters.all",
    }),
    attention: intl.formatMessage({
      defaultMessage: "Needs attention",
      id: "teams.detail.activity.filters.attention",
    }),
    failed: intl.formatMessage({
      defaultMessage: "Failed",
      id: "teams.detail.activity.filters.failed",
    }),
    running: intl.formatMessage({
      defaultMessage: "Running",
      id: "teams.detail.activity.filters.running",
    }),
  };
  const renderFilterLabel = (
    label: string,
    icon: React.ReactNode,
  ): React.ReactNode =>
    useCompactFilterLabels ? (
      <Tooltip title={label}>
        <span
          aria-label={label}
          role="img"
          style={{ alignItems: "center", display: "inline-flex", justifyContent: "center" }}
        >
          {icon}
        </span>
      </Tooltip>
    ) : (
      label
    );

  return (
    <section
      aria-labelledby="team-activity-title"
      data-testid="team-activity-tab"
      style={{ display: "flex", flexDirection: "column", gap: 16 }}
    >
      <header
        style={{
          alignItems: "flex-start",
          borderBottom: `1px solid ${token.colorBorderSecondary}`,
          display: "flex",
          flexWrap: "wrap",
          gap: 16,
          justifyContent: "space-between",
          paddingBottom: 16,
        }}
      >
        <div style={{ display: "grid", gap: 4, minWidth: 0 }}>
          <Typography.Title id="team-activity-title" level={3} style={{ margin: 0 }}>
            {intl.formatMessage({
              defaultMessage: "Recent activity",
              id: "teams.detail.activity.title",
            })}
          </Typography.Title>
          <Typography.Text type="secondary">
            {intl.formatMessage({
              defaultMessage:
                "Latest runs exposed by the current team entry service.",
              id: "teams.detail.activity.subtitle",
            })}
          </Typography.Text>
        </div>
        <Space wrap>
          <Button
            aria-label={intl.formatMessage({
              defaultMessage: "Refresh recent activity",
              id: "teams.detail.activity.actions.refreshAria",
            })}
            icon={<ReloadOutlined />}
            loading={refreshing}
            onClick={onRefresh}
          >
            {intl.formatMessage({
              defaultMessage: "Refresh",
              id: "teams.detail.activity.actions.refresh",
            })}
          </Button>
          {onOpenTeamTest ? (
            <Button
              disabled={teamRunDisabled}
              icon={<PlayCircleOutlined />}
              onClick={onOpenTeamTest}
              title={teamRunDisabled ? teamRunDisabledReason : undefined}
              type="primary"
            >
              {intl.formatMessage({
                defaultMessage: "Run team",
                id: "teams.detail.activity.actions.runTeam",
              })}
            </Button>
          ) : null}
        </Space>
      </header>

      <Segmented<ActivityFilter>
        aria-label={intl.formatMessage({
          defaultMessage: "Filter recent activity",
          id: "teams.detail.activity.filters.aria",
        })}
        block
        onChange={setFilter}
        options={[
          {
            label: renderFilterLabel(filterLabels.all, <AppstoreOutlined />),
            value: "all",
          },
          {
            label: renderFilterLabel(
              filterLabels.attention,
              <ExclamationCircleOutlined />,
            ),
            value: "attention",
          },
          {
            label: renderFilterLabel(filterLabels.running, <LoadingOutlined />),
            value: "running",
          },
          {
            label: renderFilterLabel(filterLabels.failed, <CloseCircleOutlined />),
            value: "failed",
          },
        ]}
        value={filter}
      />

      {loading ? (
        <div aria-live="polite" style={{ display: "grid", gap: 12 }}>
          {[0, 1, 2].map((item) => (
            <div
              key={item}
              style={{
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 8,
                padding: 16,
              }}
            >
              <Skeleton active paragraph={{ rows: 2 }} title={{ width: "32%" }} />
            </div>
          ))}
        </div>
      ) : error ? (
        <Alert
          action={
            <Button loading={refreshing} onClick={onRefresh} size="small">
              {intl.formatMessage({
                defaultMessage: "Retry",
                id: "teams.detail.activity.actions.retry",
              })}
            </Button>
          }
          description={intl.formatMessage({
            defaultMessage:
              "The last stable team facts remain available on Overview. Retry this read when the run list is available.",
            id: "teams.detail.activity.error.description",
          })}
          title={intl.formatMessage({
            defaultMessage: "Recent activity could not be loaded",
            id: "teams.detail.activity.error.title",
          })}
          showIcon
          type="error"
        />
      ) : (
        <>
          <TeamRecentRunsList
            emptyDescription={emptyDescription}
            emptyTitle={emptyTitle}
            onNavigate={onNavigate}
            runs={filteredRuns}
          />
          {!hasFilteredResults && runs.length > 0 ? (
            <div style={{ display: "flex", justifyContent: "center" }}>
              <Button onClick={() => setFilter("all")}>
                {intl.formatMessage({
                  defaultMessage: "Show all recent runs",
                  id: "teams.detail.activity.filters.reset",
                })}
              </Button>
            </div>
          ) : null}
        </>
      )}
    </section>
  );
};

export default TeamActivityTab;
