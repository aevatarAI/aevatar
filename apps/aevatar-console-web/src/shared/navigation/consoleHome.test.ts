describe("consoleHome", () => {
  function loadModule(): typeof import("./consoleHome") {
    let loadedModule!: typeof import("./consoleHome");
    jest.isolateModules(() => {
      loadedModule = require("./consoleHome") as typeof import("./consoleHome");
    });
    return loadedModule;
  }

  beforeEach(() => {
    jest.resetModules();
  });

  it("uses the teams home route by default", () => {
    const module = loadModule();

    expect(module.getConsoleHomeRoute()).toBe("/teams");
    expect(module.CONSOLE_HOME_ROUTE).toBe("/teams");
  });
});
