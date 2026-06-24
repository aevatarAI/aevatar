import { MoreOutlined, PlayCircleOutlined } from "@ant-design/icons";
import { Button, Dropdown, Space, Typography, theme } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import type { TeamDetailTab } from "@/shared/navigation/teamRoutes";
import {
  AevatarInspectorEmpty,
  type AevatarBreadcrumbItem,
  AevatarPageShell,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { AEVATAR_INTERACTIVE_CHIP_CLASS } from "@/shared/ui/interactionStandards";

export type TeamTabOption = {
  readonly label: string;
  readonly value: TeamDetailTab;
};

type TeamActionRailProps = {
  readonly archiveTeamActionLabel?: string;
  readonly archiveTeamDisabled?: boolean;
  readonly archiveTeamHint?: string;
  readonly editTeamDisabled?: boolean;
  readonly editTeamLabel: string;
  readonly editTeamHint?: string;
  readonly onArchiveTeam?: () => void;
  readonly onOpenTeamEditor: () => void;
  readonly onOpenTeamTest?: () => void;
  readonly testTeamDisabled?: boolean;
  readonly testTeamHint?: string;
  readonly testTeamLabel?: string;
};

type TeamTabBarProps = {
  readonly activeTab: TeamDetailTab;
  readonly onSelectTab: (tab: TeamDetailTab) => void;
  readonly tabOptions: readonly TeamTabOption[];
};

type TeamDetailShellProps = {
  readonly activeTab: TeamDetailTab;
  readonly activeTabLabel: string;
  readonly actionRail: React.ReactNode;
  readonly breadcrumbTeamTitle?: React.ReactNode;
  readonly children: React.ReactNode;
  readonly initialLoading: boolean;
  readonly onOpenTeamsList: () => void;
  readonly onSelectTab: (tab: TeamDetailTab) => void;
  readonly statusBadge: React.ReactNode;
  readonly tabOptions: readonly TeamTabOption[];
  readonly teamMeta?: React.ReactNode;
  readonly teamTitle: React.ReactNode;
  readonly teamsListHref: string;
};

const topActionButtonStyle: React.CSSProperties = {
  borderRadius: 16,
  height: 40,
  paddingInline: 18,
};

export const TeamDetailEmptyState: React.FC = () => {
  const intl = useIntl();

  return (
    <AevatarPageShell
      title={intl.formatMessage({ id: "teams.detail.empty.title" })}
      content={intl.formatMessage({ id: "teams.detail.empty.subtitle" })}
    >
      <AevatarPanel title={intl.formatMessage({ id: "teams.detail.empty.panel" })}>
        <AevatarInspectorEmpty
          description={intl.formatMessage({ id: "teams.detail.empty.description" })}
        />
      </AevatarPanel>
    </AevatarPageShell>
  );
};

export const TeamActionRail: React.FC<TeamActionRailProps> = ({
  archiveTeamActionLabel,
  archiveTeamDisabled = false,
  archiveTeamHint,
  editTeamDisabled = false,
  editTeamLabel,
  editTeamHint,
  onArchiveTeam,
  onOpenTeamEditor,
  onOpenTeamTest,
  testTeamDisabled = false,
  testTeamHint,
  testTeamLabel,
}) => {
  const intl = useIntl();
  const resolvedTestTeamLabel =
    testTeamLabel || intl.formatMessage({ id: "teams.detail.actions.test" });
  const archiveMenuItems =
    archiveTeamActionLabel && onArchiveTeam
      ? [
          {
            danger: true,
            disabled: archiveTeamDisabled,
            key: "archive-team",
            label: archiveTeamActionLabel,
          },
        ]
      : [];

  return (
    <Space key="team-detail-actions" wrap>
      {onOpenTeamTest ? (
        <Button
          disabled={testTeamDisabled}
          icon={<PlayCircleOutlined />}
          onClick={onOpenTeamTest}
          style={topActionButtonStyle}
          title={testTeamDisabled ? testTeamHint : undefined}
        >
          {resolvedTestTeamLabel}
        </Button>
      ) : null}
      <Button
        disabled={editTeamDisabled}
        onClick={onOpenTeamEditor}
        style={topActionButtonStyle}
        title={editTeamDisabled ? editTeamHint : undefined}
        type="primary"
      >
        {editTeamLabel}
      </Button>
      {archiveMenuItems.length > 0 ? (
        <Dropdown
          menu={{
            items: archiveMenuItems,
            onClick: ({ key }) => {
              if (key === "archive-team" && !archiveTeamDisabled) {
                onArchiveTeam?.();
              }
            },
          }}
          trigger={["click"]}
        >
          <span title={archiveTeamDisabled ? archiveTeamHint : undefined}>
            <Button
              aria-label={intl.formatMessage({ id: "teams.detail.actions.moreAria" })}
              disabled={archiveTeamDisabled}
              icon={<MoreOutlined />}
              style={{ ...topActionButtonStyle, paddingInline: 14 }}
              title={intl.formatMessage({ id: "teams.detail.actions.more" })}
            />
          </span>
        </Dropdown>
      ) : null}
    </Space>
  );
};

export const TeamTabBar: React.FC<TeamTabBarProps> = ({
  activeTab,
  onSelectTab,
  tabOptions,
}) => {
  const intl = useIntl();
  const { token } = theme.useToken();

  return (
    <div
      role="tablist"
      aria-label={intl.formatMessage({ id: "teams.detail.tabList.label" })}
      style={{
        alignItems: "center",
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 20,
        boxShadow: token.boxShadowSecondary,
        display: "flex",
        flexWrap: "wrap",
        gap: 10,
        padding: 8,
      }}
    >
      {tabOptions.map((option) => {
        const active = option.value === activeTab;

        return (
          <button
            aria-current={active ? "page" : undefined}
            className={AEVATAR_INTERACTIVE_CHIP_CLASS}
            key={option.value}
            onClick={() => onSelectTab(option.value)}
            style={{
              background: active ? token.colorPrimary : "transparent",
              border: `1px solid ${active ? token.colorPrimary : "transparent"}`,
              borderRadius: 999,
              color: active ? token.colorWhite : token.colorTextSecondary,
              cursor: "pointer",
              fontSize: 14,
              fontWeight: active ? 700 : 500,
              padding: "10px 16px",
              transition: "all 160ms ease",
            }}
            type="button"
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
};

export const TeamDetailShell: React.FC<TeamDetailShellProps> = ({
  activeTab,
  activeTabLabel,
  actionRail,
  breadcrumbTeamTitle,
  children,
  initialLoading,
  onOpenTeamsList,
  onSelectTab,
  statusBadge,
  tabOptions,
  teamMeta,
  teamTitle,
  teamsListHref,
}) => {
  const intl = useIntl();
  const { token } = theme.useToken();
  const breadcrumbItems: AevatarBreadcrumbItem[] = [
    {
      href: teamsListHref,
      onClick: (event) => {
        event.preventDefault();
        onOpenTeamsList();
      },
      title: intl.formatMessage({ id: "teams.detail.breadcrumb.teams" }),
    },
    {
      title:
        typeof breadcrumbTeamTitle === "string"
          ? breadcrumbTeamTitle
          : typeof teamTitle === "string"
            ? teamTitle
          : intl.formatMessage({ id: "teams.detail.breadcrumb.detail" }),
    },
    {
      current: true,
      title: activeTabLabel,
    },
  ];

  return (
    <AevatarPageShell
      backAriaLabel={intl.formatMessage({ id: "teams.detail.backToTeams" })}
      backTitle={intl.formatMessage({ id: "teams.detail.backToTeams" })}
      breadcrumbItems={breadcrumbItems}
      onBack={onOpenTeamsList}
      title={
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <div
            style={{
              alignItems: "center",
              display: "flex",
              flexWrap: "wrap",
              gap: 12,
              minWidth: 0,
            }}
          >
            <Typography.Title
              level={1}
              style={{
                lineHeight: 1.08,
                margin: 0,
                maxWidth: "100%",
                minWidth: 0,
                overflowWrap: "anywhere",
                whiteSpace: "normal",
              }}
            >
              {teamTitle}
            </Typography.Title>
            {statusBadge}
          </div>
          {teamMeta ? (
            <div
              style={{
                color: token.colorTextTertiary,
                fontSize: 13,
                fontWeight: 500,
                lineHeight: 1.4,
                minWidth: 0,
              }}
            >
              {teamMeta}
            </div>
          ) : null}
        </div>
      }
      extra={actionRail}
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <TeamTabBar
          activeTab={activeTab}
          onSelectTab={onSelectTab}
          tabOptions={tabOptions}
        />
        {children}
        {initialLoading ? (
          <Typography.Text type="secondary">
            {intl.formatMessage({ id: "teams.detail.loading" })}
          </Typography.Text>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};
