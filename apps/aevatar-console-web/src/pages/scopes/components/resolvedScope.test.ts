import type { StudioAuthSession } from "@/shared/studio/models";
import { resolveStudioScopeContext } from "./resolvedScope";

describe("resolvedScope", () => {
  it("resolves the scope from the authenticated session", () => {
    const authSession: StudioAuthSession = {
      enabled: true,
      authenticated: true,
      scopeId: "scope-auth",
      scopeSource: "claim:scope_id",
    };

    expect(resolveStudioScopeContext(authSession)).toEqual({
      scopeId: "scope-auth",
      scopeSource: "claim:scope_id",
    });
  });

  it("returns null when auth session has no scope", () => {
    const authSession: StudioAuthSession = {
      enabled: true,
      authenticated: true,
      scopeId: " ",
      scopeSource: "claim:scope_id",
    };

    expect(resolveStudioScopeContext(authSession)).toBeNull();
  });

  it("returns null when auth session is not authenticated", () => {
    const authSession: StudioAuthSession = {
      enabled: true,
      authenticated: false,
      scopeId: null,
      scopeSource: null,
    };

    expect(resolveStudioScopeContext(authSession)).toBeNull();
  });
});
