import {
  DashboardOutlined,
  SettingOutlined,
  TeamOutlined,
} from "@ant-design/icons";
import React from "react";

export type NavigationGroup = {
  flattenSingleItem?: boolean;
  icon: React.ReactNode;
  key: string;
  label: string;
  labelMessageId: string;
};

const TEAM_FIRST_NAVIGATION_GROUP_ORDER: readonly NavigationGroup[] = [
  {
    icon: <TeamOutlined />,
    key: "teams",
    label: "Teams",
    labelMessageId: "nav.groups.teams",
  },
  {
    icon: <DashboardOutlined />,
    key: "platform",
    label: "Platform",
    labelMessageId: "nav.groups.platform",
  },
  {
    flattenSingleItem: true,
    icon: <SettingOutlined />,
    key: "settings",
    label: "Settings",
    labelMessageId: "nav.groups.settings",
  },
] as const;

export function getNavigationGroupOrder(): readonly NavigationGroup[] {
  return TEAM_FIRST_NAVIGATION_GROUP_ORDER;
}
