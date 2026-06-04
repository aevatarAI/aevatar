import {
  ApartmentOutlined,
  PlayCircleOutlined,
  RocketOutlined,
  SettingOutlined,
  TeamOutlined,
  ToolOutlined,
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
    icon: <ToolOutlined />,
    key: "build",
    label: "Build",
    labelMessageId: "nav.groups.build",
  },
  {
    icon: <PlayCircleOutlined />,
    key: "run",
    label: "Run",
    labelMessageId: "nav.groups.run",
  },
  {
    icon: <RocketOutlined />,
    key: "release",
    label: "Release",
    labelMessageId: "nav.groups.release",
  },
  {
    icon: <ApartmentOutlined />,
    key: "operate",
    label: "Operate",
    labelMessageId: "nav.groups.operate",
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
