describe("consoleFeatures", () => {
  const originalFlag = process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED;

  function loadModule(): typeof import("./consoleFeatures") {
    let loadedModule!: typeof import("./consoleFeatures");
    jest.isolateModules(() => {
      loadedModule =
        require("./consoleFeatures") as typeof import("./consoleFeatures");
    });
    return loadedModule;
  }

  beforeEach(() => {
    jest.resetModules();
  });

  afterEach(() => {
    if (originalFlag === undefined) {
      delete process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED;
      return;
    }

    process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED = originalFlag;
  });

  it("defaults Team-first to enabled when no env flag is provided", () => {
    delete process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED;

    const module = loadModule();

    expect(module.isTeamFirstEnabled()).toBe(true);
    expect(module.CONSOLE_FEATURES.teamFirstEnabled).toBe(true);
  });

  it("disables Team-first when the env flag is false", () => {
    process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED = "false";

    const module = loadModule();

    expect(module.isTeamFirstEnabled()).toBe(false);
    expect(module.CONSOLE_FEATURES.teamFirstEnabled).toBe(false);
  });
});
