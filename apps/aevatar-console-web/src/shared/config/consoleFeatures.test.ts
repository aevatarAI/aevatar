describe("consoleFeatures", () => {
  const originalEnv = { ...process.env };

  function loadModule(): typeof import("./consoleFeatures") {
    let loadedModule!: typeof import("./consoleFeatures");
    jest.isolateModules(() => {
      loadedModule = require("./consoleFeatures") as typeof import("./consoleFeatures");
    });
    return loadedModule;
  }

  beforeEach(() => {
    process.env = {
      ...originalEnv,
    };
    delete process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED;
    jest.resetModules();
  });

  afterAll(() => {
    process.env = originalEnv;
  });

  it("treats the Team-first flag as disabled by default", () => {
    const module = loadModule();

    expect(module.isTeamFirstEnabled()).toBe(false);
    expect(module.CONSOLE_FEATURES.teamFirstEnabled).toBe(false);
  });

  it("treats the Team-first flag as enabled for truthy values", () => {
    process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED = "true";
    const module = loadModule();

    expect(module.isTeamFirstEnabled()).toBe(true);
    expect(module.CONSOLE_FEATURES.teamFirstEnabled).toBe(true);
  });
});
