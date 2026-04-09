describe("consoleHome", () => {
  const originalEnv = { ...process.env };

  function loadModule(): typeof import("./consoleHome") {
    let loadedModule!: typeof import("./consoleHome");
    jest.isolateModules(() => {
      loadedModule = require("./consoleHome") as typeof import("./consoleHome");
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

  it("uses the legacy scopes overview route when Team-first is disabled", () => {
    const module = loadModule();

    expect(module.getConsoleHomeRoute()).toBe("/scopes/overview");
    expect(module.CONSOLE_HOME_ROUTE).toBe("/scopes/overview");
  });

  it("uses the teams home route when Team-first is enabled", () => {
    process.env.AEVATAR_CONSOLE_TEAM_FIRST_ENABLED = "true";
    const module = loadModule();

    expect(module.getConsoleHomeRoute()).toBe("/teams");
    expect(module.CONSOLE_HOME_ROUTE).toBe("/teams");
  });
});
