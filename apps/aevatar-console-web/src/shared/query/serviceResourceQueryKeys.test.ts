import {
  invalidateServiceResourceQueries,
  isServiceResourceQueryKeyAlias,
  serviceResourceQueryKeys,
} from "./serviceResourceQueryKeys";

const query = {
  appId: "app-1",
  namespace: "default",
  take: 200,
  tenantId: "tenant-1",
};

describe("serviceResourceQueryKeys", () => {
  it("uses one shared service-resource namespace for catalog and deployment resources", () => {
    expect(serviceResourceQueryKeys.list(query)).toEqual([
      "service-resources",
      "list",
      query,
    ]);
    expect(serviceResourceQueryKeys.deployments(query, "svc-1")).toEqual([
      "service-resources",
      "deployments",
      query,
      "svc-1",
    ]);
  });

  it("matches the shared keys and previous page-scoped aliases for invalidation", () => {
    expect(
      isServiceResourceQueryKeyAlias(serviceResourceQueryKeys.detail(query, "svc-1")),
    ).toBe(true);
    expect(isServiceResourceQueryKeyAlias(["services", query])).toBe(true);
    expect(
      isServiceResourceQueryKeyAlias(["services", "detail", "svc-1", query]),
    ).toBe(true);
    expect(
      isServiceResourceQueryKeyAlias(["deployments", "catalog", query, "svc-1"]),
    ).toBe(true);
    expect(isServiceResourceQueryKeyAlias(["services", "auth-session"])).toBe(
      false,
    );
    expect(isServiceResourceQueryKeyAlias(["deployments", "auth-session"])).toBe(
      false,
    );
  });

  it("invalidates every matched alias through one helper", async () => {
    const invalidateQueries = jest.fn();

    await invalidateServiceResourceQueries({ invalidateQueries });

    expect(invalidateQueries).toHaveBeenCalledTimes(1);
    const [filters] = invalidateQueries.mock.calls[0];
    expect(filters.predicate({ queryKey: serviceResourceQueryKeys.list(query) })).toBe(
      true,
    );
    expect(filters.predicate({ queryKey: ["deployments", "traffic", query, "svc-1"] })).toBe(
      true,
    );
    expect(filters.predicate({ queryKey: ["chat", "services", "tenant-1"] })).toBe(
      false,
    );
  });
});
