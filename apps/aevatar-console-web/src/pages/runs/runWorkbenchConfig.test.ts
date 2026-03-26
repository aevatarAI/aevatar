import {
  composerRailCompactWidth,
  composerRailComfortWidth,
  composerRailDefaultWidth,
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
});
