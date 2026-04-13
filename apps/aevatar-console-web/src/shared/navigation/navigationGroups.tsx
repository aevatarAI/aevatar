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
};

const TEAM_FIRST_NAVIGATION_GROUP_ORDER: readonly NavigationGroup[] = [
  {
    icon: <TeamOutlined />,
    key: "teams",
    label: "Teams",
  },
  {
    icon: <DashboardOutlined />,
    key: "platform",
    label: "Platform",
  },
  {
    flattenSingleItem: true,
    icon: <SettingOutlined />,
    key: "settings",
    label: "Settings",
  },
] as const;

export function getNavigationGroupOrder(): readonly NavigationGroup[] {
  return TEAM_FIRST_NAVIGATION_GROUP_ORDER;
}
