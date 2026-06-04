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

  it("uses the Team-first group model by default", () => {
    const groups = loadNavigationGroups();

    expect(groups.map((group) => group.label)).toEqual([
      "Teams",
      "Build",
      "Run",
      "Release",
      "Operate",
      "Settings",
    ]);
    expect(groups.map((group) => group.labelMessageId)).toEqual([
      "nav.groups.teams",
      "nav.groups.build",
      "nav.groups.run",
      "nav.groups.release",
      "nav.groups.operate",
      "nav.groups.settings",
    ]);
  });
});
