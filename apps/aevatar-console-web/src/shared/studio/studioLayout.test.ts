import {
  isStudioHostRoute,
  STUDIO_HOST_BODY_CLASS,
  syncStudioHostBodyClass,
} from "./studioLayout";

describe("studioLayout", () => {
  beforeEach(() => {
    document.body.classList.remove(STUDIO_HOST_BODY_CLASS);
  });

  it("recognizes canonical Studio host routes", () => {
    expect(isStudioHostRoute("/studio")).toBe(true);
    expect(
      isStudioHostRoute(
        "/scopes/scope-alpha/teams/t-alpha/members/new/workflow",
      ),
    ).toBe(true);
    expect(
      isStudioHostRoute(
        "/scopes/scope-alpha/teams/t-alpha/members/m-alpha/workflow",
      ),
    ).toBe(true);
    expect(
      isStudioHostRoute(
        "/scopes/scope-alpha/teams/t-alpha/members/m-alpha/invoke",
      ),
    ).toBe(false);
  });

  it("marks the document body while the Studio host route is active", () => {
    const cleanup = syncStudioHostBodyClass(true);

    expect(document.body.classList.contains(STUDIO_HOST_BODY_CLASS)).toBe(true);

    cleanup();

    expect(document.body.classList.contains(STUDIO_HOST_BODY_CLASS)).toBe(false);
  });

  it("clears any stale body marker when Studio host mode is not active", () => {
    document.body.classList.add(STUDIO_HOST_BODY_CLASS);

    const cleanup = syncStudioHostBodyClass(false);

    expect(document.body.classList.contains(STUDIO_HOST_BODY_CLASS)).toBe(false);

    cleanup();

    expect(document.body.classList.contains(STUDIO_HOST_BODY_CLASS)).toBe(false);
  });
});
