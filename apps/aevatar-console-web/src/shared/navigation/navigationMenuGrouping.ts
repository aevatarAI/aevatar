import type React from "react";
import type { NavigationGroup } from "./navigationGroups";

export type NavigationMenuItem = {
  children?: NavigationMenuItem[];
  className?: string;
  disabled?: boolean;
  icon?: React.ReactNode;
  menuBadgeKey?: string;
  menuGroupKey?: string;
  name?: React.ReactNode;
  path?: string;
  key?: React.Key;
  [key: string]: unknown;
};

export function groupNavigationMenuItems(
  items: NavigationMenuItem[],
  groups: readonly NavigationGroup[],
  renderGroupLabel: (group: NavigationGroup) => React.ReactNode,
): NavigationMenuItem[] {
  const grouped = new Map<string, NavigationMenuItem[]>();
  const ungrouped: NavigationMenuItem[] = [];
  const groupKeys = new Set(groups.map((group) => group.key));

  for (const item of items) {
    const groupKey =
      typeof item.menuGroupKey === "string" ? item.menuGroupKey : undefined;
    if (!groupKey) {
      ungrouped.push(item);
      continue;
    }
    if (!groupKeys.has(groupKey)) {
      continue;
    }

    const existing = grouped.get(groupKey);
    if (existing) {
      existing.push(item);
      continue;
    }

    grouped.set(groupKey, [item]);
  }

  const groupedItems = groups.reduce<NavigationMenuItem[]>((result, group) => {
    const children = grouped.get(group.key);
    if (!children?.length) {
      return result;
    }

    if (group.flattenSingleItem && children.length === 1) {
      result.push({
        ...children[0],
        icon: group.flattenSingleItemAsGroupLabel
          ? children[0].icon
          : (children[0].icon ?? group.icon),
        menuGroupKey: group.key,
        name: group.flattenSingleItemAsGroupLabel
          ? renderGroupLabel(group)
          : children[0].name,
      });
      return result;
    }

    result.push({
      children: children.map((child) => ({
        ...child,
        menuGroupKey: group.key,
      })),
      key: `menu-group:${group.key}`,
      menuGroupKey: group.key,
      name: renderGroupLabel(group),
    });
    return result;
  }, []);

  return [...ungrouped, ...groupedItems];
}
