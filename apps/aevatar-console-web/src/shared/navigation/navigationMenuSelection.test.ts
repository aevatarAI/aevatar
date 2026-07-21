import { getNavigationSelectedKeys } from "./navigationMenuSelection";

describe("getNavigationSelectedKeys", () => {
  it("selects Chat for its primary route", () => {
    expect(getNavigationSelectedKeys("/chat")).toEqual(["/chat"]);
  });

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

  it("returns no selected key for hidden routes without a menu parent", () => {
    expect(getNavigationSelectedKeys("/studio")).toEqual([]);
  });
});
