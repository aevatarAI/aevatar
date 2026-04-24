import { Button, Space, Typography, theme } from "antd";
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
  readonly conversationActionLabel: string;
  readonly onOpenConversation: () => void;
  readonly onOpenServiceMapping: () => void;
  readonly onOpenTeamBuilder: () => void;
  readonly serviceMappingDisabled?: boolean;
  readonly serviceMappingHint?: string;
  readonly serviceMappingActionLabel: string;
  readonly teamBuilderActionLabel: string;
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
  <AevatarPageShell title="团队详情" content="请先进入一个具体团队，再查看详情。">
    <AevatarPanel title="未选择团队">
      <AevatarInspectorEmpty description="当前需要一个明确的 scope 才能渲染团队详情。" />
    </AevatarPanel>
  </AevatarPageShell>
);

export const TeamActionRail: React.FC<TeamActionRailProps> = ({
  conversationActionLabel,
  onOpenConversation,
  onOpenServiceMapping,
  onOpenTeamBuilder,
  serviceMappingDisabled = false,
  serviceMappingHint,
  serviceMappingActionLabel,
  teamBuilderActionLabel,
}) => (
  <Space key="team-detail-actions" wrap>
    <Button
      disabled={serviceMappingDisabled}
      onClick={onOpenServiceMapping}
      style={topActionButtonStyle}
      title={serviceMappingDisabled ? serviceMappingHint : undefined}
      type="primary"
    >
      {serviceMappingActionLabel}
    </Button>
    <Button onClick={onOpenConversation} style={topActionButtonStyle}>
      {conversationActionLabel}
    </Button>
    <Button onClick={onOpenTeamBuilder} style={topActionButtonStyle}>
      {teamBuilderActionLabel}
    </Button>
  </Space>
);

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
              Teams
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
