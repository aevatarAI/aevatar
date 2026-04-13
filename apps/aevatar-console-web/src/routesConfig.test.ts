describe("console routes", () => {
  function loadRoutes(): typeof import("../config/routes").default {
    let loadedRoutes!: typeof import("../config/routes").default;
    jest.isolateModules(() => {
      loadedRoutes = require("../config/routes").default as typeof import("../config/routes").default;
    });
    return loadedRoutes;
  }

  function findRoute(
    routes: ReturnType<typeof loadRoutes>,
    path: string,
  ): Record<string, unknown> {
    const matchedRoute = routes.find((route) => route.path === path);
    if (!matchedRoute) {
      throw new Error(`Expected route ${path} to exist.`);
    }

    return matchedRoute as Record<string, unknown>;
  }

  function hasRoute(
    routes: ReturnType<typeof loadRoutes>,
    path: string,
  ): boolean {
    return routes.some((route) => route.path === path);
  }

  beforeEach(() => {
    jest.resetModules();
  });

  it("keeps Team-first navigation as the default route model", () => {
    const routes = loadRoutes();

    expect(findRoute(routes, "/teams").hideInMenu).toBe(false);
    expect(findRoute(routes, "/studio").hideInMenu).toBe(true);
    expect(findRoute(routes, "/chat").hideInMenu).toBe(true);
    expect(findRoute(routes, "/runtime/runs").hideInMenu).toBe(true);
    expect(findRoute(routes, "/scopes/overview").hideInMenu).toBe(true);
    expect(findRoute(routes, "/teams").name).toBe("My Teams");
    expect(findRoute(routes, "/teams/new").name).toBe("Create Team");
    expect(findRoute(routes, "/runtime/gagents").name).toBe("Members");
    expect(findRoute(routes, "/scopes/assets").name).toBeUndefined();
    expect(findRoute(routes, "/scopes/invoke").name).toBeUndefined();
    expect(findRoute(routes, "/scopes/overview").redirect).toBe("/teams");
    expect(findRoute(routes, "/scopes").redirect).toBe("/teams");
    expect(hasRoute(routes, "/workflows")).toBe(true);
    expect(findRoute(routes, "/workflows").redirect).toBe("/runtime/workflows");
    expect(hasRoute(routes, "/primitives")).toBe(true);
    expect(findRoute(routes, "/primitives").redirect).toBe("/runtime/primitives");
    expect(hasRoute(routes, "/runs")).toBe(true);
    expect(findRoute(routes, "/runs").redirect).toBe("/runtime/runs");
    expect(hasRoute(routes, "/actors")).toBe(true);
    expect(findRoute(routes, "/actors").redirect).toBe("/runtime/explorer");
    expect(hasRoute(routes, "/gagents")).toBe(true);
    expect(findRoute(routes, "/gagents").redirect).toBe("/runtime/gagents");
    expect(hasRoute(routes, "/mission-control")).toBe(true);
    expect(findRoute(routes, "/mission-control").redirect).toBe("/runtime/mission-control");
    expect(findRoute(routes, "/runtime/explorer").menuGroupKey).toBe("platform");
    expect(findRoute(routes, "/services").menuGroupKey).toBe("platform");
    expect(findRoute(routes, "/deployments").menuGroupKey).toBe("platform");
    expect(findRoute(routes, "/governance").menuGroupKey).toBe("platform");
    expect(findRoute(routes, "/settings").name).toBe("Settings");
  });
});
