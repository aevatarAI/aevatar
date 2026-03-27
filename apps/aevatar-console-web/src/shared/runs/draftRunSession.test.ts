import {
  loadDraftRunPayload,
  saveDraftRunPayload,
  saveServiceInvocationDraftPayload,
} from "./draftRunSession";

describe("draftRunSession", () => {
  beforeEach(() => {
    window.sessionStorage.clear();
  });

  it("stores and restores workflow draft payloads", () => {
    const key = saveDraftRunPayload({
      workflowName: "main",
      workflowYamls: ["name: main\nsteps: []"],
    });

    expect(loadDraftRunPayload(key)).toEqual(
      expect.objectContaining({
        kind: "workflow",
        workflowName: "main",
        workflowYamls: ["name: main\nsteps: []"],
      })
    );
  });

  it("stores and restores service invocation draft payloads", () => {
    const key = saveServiceInvocationDraftPayload({
      endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
      prompt: "run this",
      payloadTypeUrl: "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
      payloadBase64: "CgBSCnJ1biB0aGlz",
    });

    expect(loadDraftRunPayload(key)).toEqual(
      expect.objectContaining({
        kind: "service_invocation",
        endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
        prompt: "run this",
        payloadTypeUrl:
          "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
        payloadBase64: "CgBSCnJ1biB0aGlz",
      })
    );
  });
});
