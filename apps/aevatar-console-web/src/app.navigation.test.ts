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

  it("places Chat as a top-level navigation item below Teams", () => {
    const groups = loadNavigationGroups();
    const chatGroup = groups.find((group) => group.key === "chat");

    expect(groups.map((group) => group.label)).toEqual([
      "Teams",
      "Chat",
      "Platform",
      "Settings",
    ]);
    expect(groups.map((group) => group.labelMessageId)).toEqual([
      "nav.groups.teams",
      "nav.groups.chat",
      "nav.groups.platform",
      "nav.groups.settings",
    ]);
    expect(chatGroup?.flattenSingleItem).toBe(true);
    expect(chatGroup?.icon).toBeTruthy();
    expect(groups.find((group) => group.key === "teams")?.flattenSingleItem).toBeUndefined();
  });

  it("renders Teams before Chat even when the Chat route is declared first", () => {
    const groupNavigationMenuItems = loadMenuGrouper();
    const groups = loadNavigationGroups();
    const menuItems = groupNavigationMenuItems(
      [
        {
          menuGroupKey: "chat",
          name: "Chat",
          path: "/chat",
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
      (group) => `Group:${group.label}`,
    );

    expect(menuItems.map((item) => item.path ?? item.key)).toEqual([
      "menu-group:teams",
      "/chat",
      "menu-group:platform",
      "/settings",
    ]);
    expect(menuItems[0].children?.map((child) => child.path)).toEqual([
      "/scopes",
    ]);
    expect(menuItems[0].name).toBe("Group:Teams");
    expect(menuItems[1]).toMatchObject({
      menuGroupKey: "chat",
      name: "Chat",
      path: "/chat",
    });
    expect(menuItems[1].icon).toBeTruthy();
    expect(menuItems[2].name).toBe("Group:Platform");
  });
});
