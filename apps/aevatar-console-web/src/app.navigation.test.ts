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

  it("puts Chat first, then scoped Teams, then platform items", () => {
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
  });
});
