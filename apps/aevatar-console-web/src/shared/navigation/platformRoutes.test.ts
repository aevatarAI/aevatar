import {
  buildPlatformDeploymentsHref,
  buildPlatformGovernanceHref,
  buildPlatformServicesHref,
} from "./platformRoutes";

describe("platformRoutes", () => {
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
