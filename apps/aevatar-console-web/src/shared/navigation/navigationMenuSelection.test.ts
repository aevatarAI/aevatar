import { getNavigationSelectedKeys } from "./navigationMenuSelection";

describe("getNavigationSelectedKeys", () => {
  it("maps team create and detail pages back to My Teams", () => {
    expect(getNavigationSelectedKeys("/teams/new")).toEqual(["/teams"]);
    expect(getNavigationSelectedKeys("/teams/scope-1")).toEqual(["/teams"]);
    expect(getNavigationSelectedKeys("/teams/scope-1/team-1")).toEqual([
      "/teams",
    ]);
  });

  it("selects visible Build entries directly", () => {
    expect(getNavigationSelectedKeys("/studio")).toEqual(["/studio"]);
    expect(getNavigationSelectedKeys("/runtime/workflows")).toEqual([
      "/runtime/workflows",
    ]);
    expect(getNavigationSelectedKeys("/runtime/primitives")).toEqual([
      "/runtime/primitives",
    ]);
    expect(getNavigationSelectedKeys("/scopes/files")).toEqual(["/studio"]);
  });

  it("maps contextual run routes back to Run Console", () => {
    expect(getNavigationSelectedKeys("/chat")).toEqual(["/runtime/runs"]);
    expect(getNavigationSelectedKeys("/scopes/invoke")).toEqual([
      "/runtime/runs",
    ]);
    expect(
      getNavigationSelectedKeys("/runtime/mission-control", "?runId=run-1"),
    ).toEqual(["/runtime/runs"]);
    expect(
      getNavigationSelectedKeys("/runtime/mission-control?runId=run-1"),
    ).toEqual(["/runtime/runs"]);
  });

  it("maps actor-only and detached Mission Control routes back to Topology", () => {
    expect(
      getNavigationSelectedKeys(
        "/runtime/mission-control",
        "?actorId=actor-1",
      ),
    ).toEqual(["/runtime/explorer"]);
    expect(getNavigationSelectedKeys("/runtime/mission-control")).toEqual([
      "/runtime/explorer",
    ]);
  });

  it("maps contextual topology routes back to Topology", () => {
    expect(getNavigationSelectedKeys("/runtime/explorer/detail")).toEqual([
      "/runtime/explorer",
    ]);
    expect(getNavigationSelectedKeys("/runtime/gagents")).toEqual([
      "/runtime/explorer",
    ]);
  });

  it("maps hidden governance workbench pages back to Governance", () => {
    expect(getNavigationSelectedKeys("/governance/bindings")).toEqual([
      "/governance",
    ]);
    expect(getNavigationSelectedKeys("/governance/policies")).toEqual([
      "/governance",
    ]);
    expect(getNavigationSelectedKeys("/governance/endpoints")).toEqual([
      "/governance",
    ]);
    expect(getNavigationSelectedKeys("/governance/activation")).toEqual([
      "/governance",
    ]);
  });
});
