import type {
  StudioAppContext,
  StudioAuthSession,
} from "@/shared/studio/models";
import { resolveStudioScopeContext } from "./resolvedScope";

describe("resolvedScope", () => {
  it("prefers the authenticated session scope", () => {
    const authSession: StudioAuthSession = {
      enabled: true,
      authenticated: true,
      scopeId: "scope-auth",
      scopeSource: "claim:scope_id",
    };
    const appContext: StudioAppContext = {
      mode: "embedded",
      scopeId: "scope-app",
      scopeResolved: true,
      scopeSource: "config:Cli:App:ScopeId",
      workflowStorageMode: "scope",
      features: {
        publishedWorkflows: true,
        scripts: true,
      },
    };

    expect(resolveStudioScopeContext(authSession, appContext)).toEqual({
      scopeId: "scope-auth",
      scopeSource: "claim:scope_id",
    });
  });

  it("falls back to the app context scope when auth session has none", () => {
    const authSession: StudioAuthSession = {
      enabled: true,
      authenticated: true,
      scopeId: " ",
      scopeSource: "claim:scope_id",
    };
    const appContext: StudioAppContext = {
      mode: "embedded",
      scopeId: "scope-app",
      scopeResolved: true,
      scopeSource: "config:Cli:App:ScopeId",
      workflowStorageMode: "scope",
      features: {
        publishedWorkflows: true,
        scripts: true,
      },
    };

    expect(resolveStudioScopeContext(authSession, appContext)).toEqual({
      scopeId: "scope-app",
      scopeSource: "config:Cli:App:ScopeId",
    });
  });

  it("returns null when neither source resolves a scope", () => {
    const authSession: StudioAuthSession = {
      enabled: true,
      authenticated: false,
      scopeId: null,
      scopeSource: null,
    };
    const appContext: StudioAppContext = {
      mode: "embedded",
      scopeId: null,
      scopeResolved: false,
      scopeSource: "",
      workflowStorageMode: "workspace",
      features: {
        publishedWorkflows: true,
        scripts: true,
      },
    };

    expect(resolveStudioScopeContext(authSession, appContext)).toBeNull();
  });
});
