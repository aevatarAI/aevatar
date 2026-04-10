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
    expect(findRoute(routes, "/runtime/gagents").name).toBe("Member Runtime");
    expect(findRoute(routes, "/scopes/assets").name).toBe("Legacy Team Assets");
    expect(findRoute(routes, "/scopes/invoke").name).toBe("Legacy Invoke Lab");
    expect(findRoute(routes, "/scopes/overview").name).toBe("Legacy Team Workspace");
    expect(findRoute(routes, "/scopes").redirect).toBe("/teams");
    expect(hasRoute(routes, "/workflows")).toBe(false);
    expect(hasRoute(routes, "/primitives")).toBe(false);
    expect(hasRoute(routes, "/runs")).toBe(false);
    expect(hasRoute(routes, "/actors")).toBe(false);
    expect(hasRoute(routes, "/gagents")).toBe(false);
    expect(hasRoute(routes, "/mission-control")).toBe(false);
    expect(findRoute(routes, "/runtime/explorer").menuGroupKey).toBe("platform");
    expect(findRoute(routes, "/services").menuGroupKey).toBe("platform");
    expect(findRoute(routes, "/deployments").menuGroupKey).toBe("platform");
    expect(findRoute(routes, "/governance").menuGroupKey).toBe("platform");
    expect(findRoute(routes, "/settings").name).toBe("Settings");
  });
});
