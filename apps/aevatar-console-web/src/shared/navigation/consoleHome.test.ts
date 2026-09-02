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

  it('uses the fixed-scope Workflow Activity catalogue as console home', () => {
    const module = loadModule();
    const expectedRoute =
      '/scopes/ccb108c4-dcb3-473a-a0f7-e9859bb2f2a0/workflow-activity-vnext/workflows';

    expect(module.WORKFLOW_ACTIVITY_VNEXT_HOME_ROUTE).toBe(expectedRoute);
    expect(module.getConsoleHomeRoute()).toBe(expectedRoute);
    expect(module.CONSOLE_HOME_ROUTE).toBe(expectedRoute);
  });
});
