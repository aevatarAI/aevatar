import { getNavigationSelectedKeys } from "./navigationMenuSelection";

describe("getNavigationSelectedKeys", () => {
  it("selects Create Team without keeping My Teams selected", () => {
    expect(getNavigationSelectedKeys("/teams/new")).toEqual(["/teams/new"]);
  });

  it("maps team detail pages back to My Teams", () => {
    expect(getNavigationSelectedKeys("/teams/scope-1")).toEqual(["/teams"]);
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
