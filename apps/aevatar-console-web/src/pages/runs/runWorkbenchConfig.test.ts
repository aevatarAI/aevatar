import {
  buildRunReadinessSummary,
  composerRailCompactWidth,
  composerRailComfortWidth,
  composerRailDefaultWidth,
  describeRunReturnTarget,
  resolveResponsiveComposerWidth,
} from "./runWorkbenchConfig";

describe("resolveResponsiveComposerWidth", () => {
  it("caps the launch rail more aggressively on medium and narrow layouts", () => {
    expect(resolveResponsiveComposerWidth(composerRailDefaultWidth, 1080)).toBe(
      composerRailCompactWidth
    );
    expect(resolveResponsiveComposerWidth(composerRailDefaultWidth, 1280)).toBe(
      composerRailComfortWidth
    );
    expect(resolveResponsiveComposerWidth(composerRailDefaultWidth, 1560)).toBe(
      composerRailDefaultWidth
    );
  });

  it("still respects smaller manual widths when the rail is already compressed", () => {
    expect(resolveResponsiveComposerWidth(324, 1280)).toBe(324);
  });

  it("describes return targets by their source surface", () => {
    expect(
      describeRunReturnTarget("/runtime/workflows?workflow=demo_flow")
    ).toBe("Back to Workflow Library");
    expect(
      describeRunReturnTarget("/runtime/explorer/detail?actorId=actor-1")
    ).toBe("Back to Actor Explorer");
    expect(describeRunReturnTarget("/studio?tab=studio")).toBe("Back to Studio");
    expect(describeRunReturnTarget("/teams/scope-a/team-a")).toBe(
      "Back to advanced team editing"
    );
    expect(describeRunReturnTarget()).toBe("Back to advanced team editing");
  });
});

describe("buildRunReadinessSummary", () => {
  it("marks workspace as the only blocking readiness item", () => {
    expect(
      buildRunReadinessSummary({
        endpointLabel: "chat",
        routeLabel: "direct",
        scopeId: "",
      })
    ).toEqual({
      ready: false,
      blockingReason: "Workspace is required before the prompt can be sent.",
      items: [
        {
          key: "workspace",
          label: "Workspace",
          value: "Required",
          status: "required",
          helper: "Add a workspace ID to unlock Send.",
        },
        {
          key: "route",
          label: "Route",
          value: "direct",
          status: "context",
          helper: "The prompt will target this chat route.",
        },
        {
          key: "endpoint",
          label: "Endpoint",
          value: "chat",
          status: "context",
          helper: "Advanced endpoint and payload controls stay available below.",
        },
      ],
    });
  });

  it("uses workspace default route and chat endpoint fallbacks", () => {
    expect(
      buildRunReadinessSummary({
        endpointLabel: "",
        routeLabel: "",
        scopeId: " scope-1 ",
      })
    ).toMatchObject({
      ready: true,
      blockingReason: undefined,
      items: [
        {
          key: "workspace",
          value: "scope-1",
          status: "ready",
        },
        {
          key: "route",
          value: "Workspace default",
          status: "context",
        },
        {
          key: "endpoint",
          value: "chat",
          status: "context",
        },
      ],
    });
  });
});
