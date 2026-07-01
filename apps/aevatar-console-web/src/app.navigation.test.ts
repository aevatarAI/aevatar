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

  beforeEach(() => {
    jest.resetModules();
  });

  it("puts Chat at the top-level next to Teams, Platform, and Settings", () => {
    const groups = loadNavigationGroups();

    expect(groups.map((group) => group.label)).toEqual([
      "Chat",
      "Teams",
      "Platform",
      "Settings",
    ]);
    expect(groups.map((group) => group.labelMessageId)).toEqual([
      "nav.groups.chat",
      "nav.groups.teams",
      "nav.groups.platform",
      "nav.groups.settings",
    ]);
    expect(groups.find((group) => group.key === "chat")?.flattenSingleItem).toBe(true);
    expect(groups.find((group) => group.key === "chat")?.flattenSingleItemAsGroupLabel).toBe(true);
    expect(groups.find((group) => group.key === "teams")?.flattenSingleItem).toBeUndefined();
  });
});
