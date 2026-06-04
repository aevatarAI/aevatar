import type {
  ServiceBindingSnapshot,
  ServiceEndpointCatalogSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicySnapshot,
} from "@/shared/models/governance";
import {
  resolveBindingAffordance,
  resolveEndpointAffordance,
  resolveEndpointExposureAction,
  resolvePolicyAffordance,
} from "./governanceAffordance";

const activePolicy: ServicePolicySnapshot = {
  activationRequiredBindingIds: [],
  displayName: "Active policy",
  invokeAllowedCallerServiceKeys: [],
  invokeRequiresActiveDeployment: false,
  policyId: "policy-active",
  retired: false,
};

const activeBinding: ServiceBindingSnapshot = {
  bindingId: "binding-active",
  bindingKind: "service",
  connectorRef: null,
  displayName: "Active binding",
  policyIds: [],
  retired: false,
  secretRef: null,
  serviceRef: null,
};

const endpoint: ServiceEndpointExposureSnapshot = {
  description: "",
  displayName: "Invoke",
  endpointId: "invoke",
  exposureKind: "internal",
  kind: "command",
  policyIds: [],
  requestTypeUrl: "type.googleapis.com/demo.Invoke",
  responseTypeUrl: "",
};

const endpointCatalog: ServiceEndpointCatalogSnapshot = {
  endpoints: [endpoint],
  serviceKey: "tenant/app/default/service",
  updatedAt: "2026-06-02T08:00:00Z",
};

describe("governanceAffordance", () => {
  it("treats retired policies and bindings as read-only facts", () => {
    expect(resolvePolicyAffordance({ ...activePolicy, retired: true })).toMatchObject({
      editMode: "readonly",
      status: "retired",
    });
    expect(resolveBindingAffordance({ ...activeBinding, retired: true })).toMatchObject({
      editMode: "readonly",
      status: "retired",
    });
  });

  it("keeps active policies and bindings writable", () => {
    expect(resolvePolicyAffordance(activePolicy)).toMatchObject({
      editMode: "writable",
      status: "active",
    });
    expect(resolveBindingAffordance(activeBinding)).toMatchObject({
      editMode: "writable",
      status: "active",
    });
  });

  it("only exposes a public endpoint action when it can submit a real catalog update", () => {
    expect(resolveEndpointExposureAction(endpoint, endpointCatalog)).toMatchObject({
      disabled: false,
      label: "Public endpoints",
      nextExposureKind: "public",
    });
    expect(
      resolveEndpointExposureAction(
        { ...endpoint, exposureKind: "public" },
        endpointCatalog,
      ),
    ).toMatchObject({
      disabled: true,
      label: "Published",
    });
    expect(resolveEndpointExposureAction(endpoint, null)).toMatchObject({
      disabled: true,
      reason: expect.stringContaining("endpoint catalog"),
    });
    expect(resolveEndpointExposureAction({ ...endpoint, exposureKind: "public" }, null)).toMatchObject({
      disabled: true,
      label: "Published",
      reason: expect.stringContaining("confirmed or submitted"),
    });
  });

  it("marks endpoint facts read-only when the catalog is missing", () => {
    expect(resolveEndpointAffordance(endpoint, null)).toMatchObject({
      editMode: "readonly",
      status: "internal",
    });
  });
});
