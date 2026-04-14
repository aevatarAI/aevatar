import {
  buildTeamDetailHref,
  buildTeamsHref,
  readTeamDetailRouteState,
} from "./teamRoutes";

describe("teamRoutes", () => {
  it("builds a workflow-aware team detail href and trims empty values", () => {
    expect(
      buildTeamDetailHref({
        scopeId: " scope-alpha ",
        workflowId: "workflow-1",
        serviceId: "service-1",
        runId: "run-1",
        tab: "events",
      }),
    ).toBe(
      "/teams/scope-alpha?workflowId=workflow-1&tab=events&serviceId=service-1&runId=run-1",
    );
  });

  it("falls back to /teams when the scope is empty", () => {
    expect(
      buildTeamDetailHref({
        scopeId: " ",
        workflowId: "workflow-1",
      }),
    ).toBe(buildTeamsHref());
  });

  it("reads the team detail route state from path and query", () => {
    expect(
      readTeamDetailRouteState(
        "?workflowId=wf-1&serviceId=service-1&runId=run-1&tab=members",
        "/teams/scope-alpha",
      ),
    ).toEqual({
      runId: "run-1",
      scopeId: "scope-alpha",
      serviceId: "service-1",
      tab: "members",
      workflowId: "wf-1",
    });
  });

  it("defaults canonical team routes to the topology tab", () => {
    expect(
      readTeamDetailRouteState(
        "?workflowId=wf-2&tab=not-real",
        "/teams/scope-query",
      ),
    ).toEqual({
      runId: "",
      scopeId: "scope-query",
      serviceId: "",
      tab: "topology",
      workflowId: "wf-2",
    });
  });

  it("falls back to the query scope and overview tab when the path is malformed", () => {
    expect(
      readTeamDetailRouteState(
        "?scopeId=scope-query&workflowId=wf-2&tab=not-real",
        "/runtime/runs",
      ),
    ).toEqual({
      runId: "",
      scopeId: "scope-query",
      serviceId: "",
      tab: "overview",
      workflowId: "wf-2",
    });
  });
});
