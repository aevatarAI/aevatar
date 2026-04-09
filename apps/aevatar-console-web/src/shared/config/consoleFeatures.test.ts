describe("consoleFeatures", () => {
  function loadModule(): typeof import("./consoleFeatures") {
    let loadedModule!: typeof import("./consoleFeatures");
    jest.isolateModules(() => {
      loadedModule = require("./consoleFeatures") as typeof import("./consoleFeatures");
    });
    return loadedModule;
  }

  beforeEach(() => {
    jest.resetModules();
  });

  it("treats Team-first as always enabled", () => {
    const module = loadModule();

    expect(module.isTeamFirstEnabled()).toBe(true);
    expect(module.CONSOLE_FEATURES.teamFirstEnabled).toBe(true);
  });
});
