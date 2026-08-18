import { getNavigationSelectedKeys } from "./navigationMenuSelection";

describe("getNavigationSelectedKeys", () => {
  it("does not select a primary navigation item for removed Team compatibility routes", () => {
    expect(getNavigationSelectedKeys("/teams/new")).toEqual([]);
  });

  it("maps team detail pages back to My Teams", () => {
    expect(getNavigationSelectedKeys("/scopes/scope-1/teams")).toEqual([
      "/scopes",
    ]);
    expect(getNavigationSelectedKeys("/scopes/scope-1/teams/t-alpha")).toEqual([
      "/scopes",
    ]);
  });

  it("does not map removed legacy team detail pages back to My Teams", () => {
    expect(getNavigationSelectedKeys("/teams/scope-1")).toEqual([]);
  });

  it("maps hidden governance workbench pages back to Governance", () => {
    expect(getNavigationSelectedKeys("/governance/bindings")).toEqual([
      "/governance",
    ]);
  });

  it("maps AI workspace pages and the compatibility Chat route back to AI", () => {
    expect(getNavigationSelectedKeys("/ai/chat")).toEqual(["/ai"]);
    expect(getNavigationSelectedKeys("/ai/agents")).toEqual(["/ai"]);
    expect(getNavigationSelectedKeys("/ai/models")).toEqual(["/ai"]);
    expect(getNavigationSelectedKeys("/chat")).toEqual(["/ai"]);
  });

  it("returns no selected key for hidden routes without a menu parent", () => {
    expect(getNavigationSelectedKeys("/studio")).toEqual([]);
  });
});
