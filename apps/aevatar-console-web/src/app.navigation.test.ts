describe("app navigation groups", () => {
  function loadNavigationGroups(): ReturnType<typeof import("./shared/navigation/navigationGroups").getNavigationGroupOrder> {
    let groups!: ReturnType<typeof import("./shared/navigation/navigationGroups").getNavigationGroupOrder>;
    jest.isolateModules(() => {
      groups = require("./shared/navigation/navigationGroups").getNavigationGroupOrder() as ReturnType<
        typeof import("./shared/navigation/navigationGroups").getNavigationGroupOrder
      >;
    });
    return groups;
  }

  function loadMenuGrouper(): typeof import("./shared/navigation/navigationMenuGrouping").groupNavigationMenuItems {
    let groupNavigationMenuItems!: typeof import("./shared/navigation/navigationMenuGrouping").groupNavigationMenuItems;
    jest.isolateModules(() => {
      groupNavigationMenuItems = require("./shared/navigation/navigationMenuGrouping").groupNavigationMenuItems as typeof import("./shared/navigation/navigationMenuGrouping").groupNavigationMenuItems;
    });
    return groupNavigationMenuItems;
  }

  beforeEach(() => {
    jest.resetModules();
  });

  it("places the AI workspace as a top-level navigation group below Teams", () => {
    const groups = loadNavigationGroups();

    expect(groups.map((group) => group.label)).toEqual([
      "Teams",
      "AI",
      "Settings",
    ]);
    expect(groups.map((group) => group.labelMessageId)).toEqual([
      "nav.groups.teams",
      "nav.groups.ai",
      "nav.groups.settings",
    ]);
    expect(groups.find((group) => group.key === "ai")?.flattenSingleItem).toBe(true);
    expect(groups.find((group) => group.key === "ai")?.flattenSingleItemAsGroupLabel).toBe(true);
    expect(groups.find((group) => group.key === "teams")?.flattenSingleItem).toBeUndefined();
  });

  it("renders Teams before AI even when the AI route is declared first", () => {
    const groupNavigationMenuItems = loadMenuGrouper();
    const groups = loadNavigationGroups();
    const menuItems = groupNavigationMenuItems(
      [
        {
          menuGroupKey: "ai",
          name: "AI",
          path: "/ai",
        },
        {
          menuGroupKey: "teams",
          name: "My Teams",
          path: "/scopes",
        },
        {
          menuGroupKey: "platform",
          name: "Event Stream",
          path: "/runtime/runs",
        },
        {
          menuGroupKey: "settings",
          name: "Settings",
          path: "/settings",
        },
        {
          menuGroupKey: "unknown",
          name: "Unknown",
          path: "/unknown",
        },
      ],
      groups,
      (group) => group.label,
    );

    expect(menuItems.map((item) => item.path ?? item.key)).toEqual([
      "menu-group:teams",
      "/ai",
      "/settings",
    ]);
    expect(menuItems[0].children?.map((child) => child.path)).toEqual([
      "/scopes",
    ]);
  });
});
