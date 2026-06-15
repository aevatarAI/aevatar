import { getNavigationSelectedKeys } from "./navigationMenuSelection";

describe("getNavigationSelectedKeys", () => {
  it("selects the task-oriented Platform overview entry", () => {
    expect(getNavigationSelectedKeys("/platform")).toEqual(["/platform"]);
  });

  it("does not select a primary navigation item for the hidden Create Team compatibility route", () => {
    expect(getNavigationSelectedKeys("/teams/new")).toEqual([]);
  });

  it("maps team detail pages back to My Teams", () => {
    expect(getNavigationSelectedKeys("/teams/scope-1")).toEqual(["/teams"]);
  });

  it("maps hidden governance workbench pages back to Governance", () => {
    expect(getNavigationSelectedKeys("/governance/bindings")).toEqual([
      "/governance",
    ]);
  });

  it("maps the platform overview compatibility route back to Overview", () => {
    expect(getNavigationSelectedKeys("/platform/overview")).toEqual([
      "/platform",
    ]);
  });

  it("returns no selected key for hidden routes without a menu parent", () => {
    expect(getNavigationSelectedKeys("/studio")).toEqual([]);
  });
});
