import type {
  StudioAppContext,
  StudioAuthSession,
} from "@/shared/studio/models";

export type ResolvedScopeContext = {
  scopeId: string;
  scopeSource: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

export function resolveStudioScopeContext(
  authSession?: StudioAuthSession | null,
  appContext?: StudioAppContext | null
): ResolvedScopeContext | null {
  const authScopeId = trimOptional(authSession?.scopeId);
  if (authScopeId) {
    return {
      scopeId: authScopeId,
      scopeSource: trimOptional(authSession?.scopeSource),
    };
  }

  const appScopeId = appContext?.scopeResolved
    ? trimOptional(appContext.scopeId)
    : "";
  if (appScopeId) {
    return {
      scopeId: appScopeId,
      scopeSource: trimOptional(appContext?.scopeSource),
    };
  }

  return null;
}
