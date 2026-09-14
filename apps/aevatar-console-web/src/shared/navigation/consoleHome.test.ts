describe('consoleHome', () => {
  function loadModule(): typeof import('./consoleHome') {
    let loadedModule!: typeof import('./consoleHome');
    jest.isolateModules(() => {
      loadedModule = require('./consoleHome') as typeof import('./consoleHome');
    });
    return loadedModule;
  }

  beforeEach(() => {
    jest.resetModules();
  });

  it('opens the workflow home without choosing a workspace before authentication', () => {
    const module = loadModule();
    const expectedRoute = '/workflows';

    expect(module.getConsoleHomeRoute()).toBe(expectedRoute);
    expect(module.CONSOLE_HOME_ROUTE).toBe(expectedRoute);
  });
});
