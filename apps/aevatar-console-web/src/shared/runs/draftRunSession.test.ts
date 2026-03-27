import {
  saveEndpointInvocationDraftPayload,
  saveScopeDraftRunPayload,
  loadDraftRunPayload,
} from "./draftRunSession";

describe("draftRunSession", () => {
  beforeEach(() => {
    window.sessionStorage.clear();
  });

  it("stores and restores scope draft payloads", () => {
    const key = saveScopeDraftRunPayload({
      bundleName: "main",
      bundleYamls: ["name: main\nsteps: []"],
    });

    expect(loadDraftRunPayload(key)).toEqual(
      expect.objectContaining({
        kind: "scope_draft",
        bundleName: "main",
        bundleYamls: ["name: main\nsteps: []"],
      })
    );
  });

  it("stores and restores endpoint invocation draft payloads", () => {
    const key = saveEndpointInvocationDraftPayload({
      endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
      prompt: "run this",
      payloadTypeUrl: "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
      payloadBase64: "CgBSCnJ1biB0aGlz",
    });

    expect(loadDraftRunPayload(key)).toEqual(
      expect.objectContaining({
        kind: "endpoint_invocation",
        endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
        prompt: "run this",
        payloadTypeUrl:
          "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
        payloadBase64: "CgBSCnJ1biB0aGlz",
      })
    );
  });

  it("keeps reading legacy workflow and service invocation payloads", () => {
    window.sessionStorage.setItem(
      "aevatar-console-draft-run:legacy",
      JSON.stringify({
        kind: "workflow",
        workflowName: "legacy-route",
        workflowYamls: ["name: legacy-route\nsteps: []"],
      })
    );
    window.sessionStorage.setItem(
      "aevatar-console-draft-run:legacy-invoke",
      JSON.stringify({
        kind: "service_invocation",
        endpointId: "run",
        prompt: "hello",
        payloadTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
        serviceId: "svc-1",
      })
    );

    expect(loadDraftRunPayload("legacy")).toEqual(
      expect.objectContaining({
        kind: "scope_draft",
        bundleName: "legacy-route",
        bundleYamls: ["name: legacy-route\nsteps: []"],
      })
    );
    expect(loadDraftRunPayload("legacy-invoke")).toEqual(
      expect.objectContaining({
        kind: "endpoint_invocation",
        endpointId: "run",
        serviceOverrideId: "svc-1",
      })
    );
  });
});
