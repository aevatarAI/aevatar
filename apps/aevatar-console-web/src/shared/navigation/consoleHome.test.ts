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

  it('uses the session scope resolver as console home', () => {
    const module = loadModule();
    const expectedRoute = '/scopes';

    expect(module.WORKFLOW_ACTIVITY_VNEXT_HOME_ROUTE).toBe(expectedRoute);
    expect(module.getConsoleHomeRoute()).toBe(expectedRoute);
    expect(module.CONSOLE_HOME_ROUTE).toBe(expectedRoute);
  });
});
