import {
  buildTeamCreateRoute,
  buildScopeOverviewHref,
  buildTeamWorkspaceRoute,
  readScopeQueryDraft,
  resolveScopeOverviewPath,
} from "./scopeRoutes";

describe("scopeRoutes", () => {
  it("reads the scope from a team detail pathname when the query is empty", () => {
    expect(readScopeQueryDraft("", "/scopes/scope-alpha/teams")).toEqual({
      scopeId: "scope-alpha",
    });
  });

  it("does not read scope from removed legacy team pathnames", () => {
    expect(readScopeQueryDraft("", "/teams/scope-alpha")).toEqual({
      scopeId: "",
    });
  });

  it("does not treat removed legacy create routes as scoped team routes", () => {
    expect(readScopeQueryDraft("", "/teams/new")).toEqual({
      scopeId: "",
    });
  });

  it("ignores scopeId=new only on scoped team create routes", () => {
    expect(readScopeQueryDraft("?scopeId=new", "/scopes/scope-alpha/teams/new")).toEqual({
      scopeId: "scope-alpha",
    });
  });

  it("keeps an explicit query scope only as a route draft outside scoped team routes", () => {
    expect(readScopeQueryDraft("?scopeId=scope-alpha", "/teams/new")).toEqual({
      scopeId: "scope-alpha",
    });
  });

  it("keeps the team pathname when building the overview href from a team detail route", () => {
    expect(
      buildScopeOverviewHref(
        { scopeId: "scope-alpha" },
        { workflowId: "wf-1" },
        "/scopes/scope-alpha/teams",
      ),
    ).toBe("/scopes/scope-alpha/teams?workflowId=wf-1");
  });

  it("builds the canonical team workspace route with scope context", () => {
    expect(buildTeamWorkspaceRoute("scope-alpha")).toBe(
      "/scopes/scope-alpha/teams",
    );
    expect(buildTeamWorkspaceRoute("")).toBe("/scopes");
  });

  it("builds the canonical scoped team create route", () => {
    expect(buildTeamCreateRoute("scope-alpha", { teamName: "Support" })).toBe(
      "/scopes/scope-alpha/teams/new?teamName=Support",
    );
    expect(buildTeamCreateRoute("")).toBe("/scopes");
  });

  it("falls back to the scope overview path outside team detail routes", () => {
    expect(resolveScopeOverviewPath({ scopeId: "scope-alpha" }, "/scopes/overview")).toBe(
      "/scopes/overview",
    );
  });
});
