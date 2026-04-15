import {
  buildScopeOverviewHref,
  buildTeamWorkspaceRoute,
  readScopeQueryDraft,
  resolveScopeOverviewPath,
} from "./scopeRoutes";

describe("scopeRoutes", () => {
  it("reads the scope from a team detail pathname when the query is empty", () => {
    expect(readScopeQueryDraft("", "/teams/scope-alpha")).toEqual({
      scopeId: "scope-alpha",
    });
  });

  it("keeps the team pathname when building the overview href from a team detail route", () => {
    expect(
      buildScopeOverviewHref(
        { scopeId: "scope-alpha" },
        { workflowId: "wf-1" },
        "/teams/scope-alpha",
      ),
    ).toBe("/teams/scope-alpha?scopeId=scope-alpha&workflowId=wf-1");
  });

  it("builds the canonical team workspace route with scope context", () => {
    expect(buildTeamWorkspaceRoute("scope-alpha")).toBe(
      "/teams/scope-alpha?scopeId=scope-alpha",
    );
  });

  it("falls back to the legacy scope overview path outside team detail routes", () => {
    expect(resolveScopeOverviewPath({ scopeId: "scope-alpha" }, "/scopes/overview")).toBe(
      "/scopes/overview",
    );
  });
});
