import {
  buildPlatformOverviewHref,
  buildPlatformDeploymentsHref,
  buildPlatformGovernanceHref,
  buildPlatformServicesHref,
  PLATFORM_MODULE_DESCRIPTORS,
} from "./platformRoutes";
import routes from "../../../config/routes";

type ConsoleRoute = {
  readonly component?: string;
  readonly menuGroupKey?: string;
  readonly name?: string;
  readonly path?: string;
};

function findRoute(path: string): ConsoleRoute {
  const route = (routes as ConsoleRoute[]).find((item) => item.path === path);
  if (!route) {
    throw new Error(`Expected route ${path} to exist.`);
  }
  return route;
}

function findRouteIndex(path: string): number {
  const index = (routes as ConsoleRoute[]).findIndex((item) => item.path === path);
  if (index < 0) {
    throw new Error(`Expected route ${path} to exist.`);
  }
  return index;
}

describe("platformRoutes", () => {
  it("exposes the task-oriented platform overview and module order", () => {
    expect(buildPlatformOverviewHref()).toBe("/platform");
    expect(PLATFORM_MODULE_DESCRIPTORS.map((module) => module.key)).toEqual([
      "capabilities",
      "accessRules",
      "releases",
      "runs",
      "runtimeMap",
    ]);
    expect(PLATFORM_MODULE_DESCRIPTORS.map((module) => module.routePath)).toEqual([
      "/services",
      "/governance",
      "/deployments",
      "/runtime/runs",
      "/runtime/explorer",
    ]);
  });

  it("keeps the platform menu task-oriented while preserving deep-link paths", () => {
    expect(findRoute("/platform")).toMatchObject({
      component: "./platform",
      menuGroupKey: "platform",
      name: "Overview",
    });
    expect(findRoute("/services").name).toBe("Capabilities");
    expect(findRoute("/governance").name).toBe("Access & Rules");
    expect(findRoute("/deployments").name).toBe("Releases");
    expect(findRoute("/runtime/runs").name).toBe("Runs");
    expect(findRoute("/runtime/explorer").name).toBe("Runtime Map");

    expect(findRouteIndex("/platform")).toBeLessThan(findRouteIndex("/services"));
    expect(findRouteIndex("/services")).toBeLessThan(findRouteIndex("/governance"));
    expect(findRouteIndex("/governance")).toBeLessThan(findRouteIndex("/deployments"));
    expect(findRouteIndex("/deployments")).toBeLessThan(findRouteIndex("/runtime/runs"));
    expect(findRouteIndex("/runtime/runs")).toBeLessThan(findRouteIndex("/runtime/explorer"));
  });

  it("builds service workbench links with scoped identity", () => {
    expect(
      buildPlatformServicesHref({
        tenantId: "scope-a",
        appId: "default",
        namespace: "default",
        serviceId: "service-alpha",
        take: 50,
      }),
    ).toBe(
      "/services?tenantId=scope-a&appId=default&namespace=default&take=50&serviceId=service-alpha",
    );
  });

  it("builds governance links without forcing the audit view", () => {
    expect(
      buildPlatformGovernanceHref({
        tenantId: "scope-a",
        appId: "default",
        namespace: "default",
        serviceId: "service-alpha",
        revisionId: "rev-2",
        view: "bindings",
      }),
    ).toBe(
      "/governance?tenantId=scope-a&appId=default&namespace=default&serviceId=service-alpha&revisionId=rev-2&view=bindings",
    );

    expect(
      buildPlatformGovernanceHref({
        tenantId: "scope-a",
        serviceId: "service-alpha",
        view: "audit",
      }),
    ).toBe("/governance?tenantId=scope-a&serviceId=service-alpha");
  });

  it("builds deployment links that preserve service and deployment focus", () => {
    expect(
      buildPlatformDeploymentsHref({
        tenantId: "scope-a",
        appId: "default",
        namespace: "default",
        serviceId: "service-alpha",
        deploymentId: "dep-9",
      }),
    ).toBe(
      "/deployments?tenantId=scope-a&appId=default&namespace=default&serviceId=service-alpha&deploymentId=dep-9",
    );
  });
});
