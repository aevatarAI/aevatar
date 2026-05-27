import { MoreOutlined, PlayCircleOutlined } from "@ant-design/icons";
import { Button, Dropdown, Space, Typography, theme } from "antd";
import React from "react";
import type { TeamDetailTab } from "@/shared/navigation/teamRoutes";
import {
  AevatarInspectorEmpty,
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

export const TeamDetailEmptyState: React.FC = () => (
  <AevatarPageShell title="团队详情" content="请先从团队列表选择一个具体团队，再查看详情。">
    <AevatarPanel title="未选择团队">
      <AevatarInspectorEmpty description="当前链接只有工作区上下文，没有具体 Team 标识。返回团队列表后选择一个团队。" />
    </AevatarPanel>
  </AevatarPageShell>
);

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
  testTeamLabel = "测试团队",
}) => {
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
          {testTeamLabel}
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
              aria-label="团队更多操作"
              disabled={archiveTeamDisabled}
              icon={<MoreOutlined />}
              style={{ ...topActionButtonStyle, paddingInline: 14 }}
              title="更多操作"
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
  const { token } = theme.useToken();

  return (
    <div
      role="tablist"
      aria-label="团队详情标签"
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
  const { token } = theme.useToken();

  return (
    <AevatarPageShell
      breadcrumbRender={false}
      title={
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <Typography.Text
            style={{
              color: token.colorTextTertiary,
              fontSize: 13,
              fontWeight: 500,
              lineHeight: 1.4,
            }}
          >
            <Typography.Link
              href={teamsListHref}
              onClick={(event) => {
                event.preventDefault();
                onOpenTeamsList();
              }}
              style={{
                color: token.colorTextTertiary,
                fontSize: "inherit",
                fontWeight: "inherit",
              }}
            >
              Aevatar
            </Typography.Link>
            {" / "}
            <Typography.Link
              href={teamsListHref}
              onClick={(event) => {
                event.preventDefault();
                onOpenTeamsList();
              }}
              style={{
                color: token.colorTextTertiary,
                fontSize: "inherit",
                fontWeight: "inherit",
              }}
            >
              团队
            </Typography.Link>
            {` / 团队详情 / ${activeTabLabel}`}
          </Typography.Text>
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
          <Typography.Text type="secondary">正在加载团队详情...</Typography.Text>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};
