import {
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
    ).toBe("返回 Workflow Library");
    expect(
      describeRunReturnTarget("/runtime/explorer/detail?actorId=actor-1")
    ).toBe("返回 Actor explorer");
    expect(describeRunReturnTarget("/studio?tab=studio")).toBe("返回 Studio");
    expect(describeRunReturnTarget("/teams/scope-a/team-a")).toBe(
      "返回团队高级编辑"
    );
    expect(describeRunReturnTarget()).toBe("返回团队高级编辑");
  });
});
