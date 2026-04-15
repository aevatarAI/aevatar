const runtimePaths = {
  workflows: "/runtime/workflows",
  primitives: "/runtime/primitives",
  runs: "/runtime/runs",
  explorer: "/runtime/explorer",
  gagents: "/runtime/gagents",
} as const;

type QueryValue = string | undefined;

function buildHref(
  pathname: string,
  query?: Record<string, QueryValue>
): string {
  if (!query) {
    return pathname;
  }

  const searchParams = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (!value) {
      continue;
    }

    searchParams.set(key, value);
  }

  const search = searchParams.toString();
  return search ? `${pathname}?${search}` : pathname;
}

export const runtimeNavigation = runtimePaths;

export function buildRuntimeWorkflowsHref(options?: {
  workflow?: string;
  tab?: string;
}): string {
  return buildHref(runtimePaths.workflows, options);
}

export function buildRuntimePrimitivesHref(options?: {
  primitive?: string;
}): string {
  return buildHref(runtimePaths.primitives, options);
}

export function buildRuntimeRunsHref(options?: {
  route?: string;
  workflow?: string;
  prompt?: string;
  scopeId?: string;
  serviceOverrideId?: string;
  serviceId?: string;
  endpointId?: string;
  endpointKind?: string;
  payloadTypeUrl?: string;
  payloadBase64?: string;
  actorId?: string;
  draftKey?: string;
  returnTo?: string;
}): string {
  return buildHref(runtimePaths.runs, {
    route: options?.route ?? options?.workflow,
    prompt: options?.prompt,
    scopeId: options?.scopeId,
    serviceOverrideId: options?.serviceOverrideId ?? options?.serviceId,
    endpointId: options?.endpointId,
    endpointKind: options?.endpointKind,
    payloadTypeUrl: options?.payloadTypeUrl,
    payloadBase64: options?.payloadBase64,
    actorId: options?.actorId,
    draftKey: options?.draftKey,
    returnTo: options?.returnTo,
  });
}

export function buildRuntimeExplorerHref(options?: {
  actorId?: string;
  runId?: string;
  scopeId?: string;
  serviceId?: string;
  serviceOverrideId?: string;
}): string {
  return buildHref(runtimePaths.explorer, {
    actorId: options?.actorId,
    runId: options?.runId,
    scopeId: options?.scopeId,
    serviceId: options?.serviceId ?? options?.serviceOverrideId,
  });
}

export function buildRuntimeGAgentsHref(options?: {
  scopeId?: string;
  actorTypeName?: string;
  actorId?: string;
}): string {
  return buildHref(runtimePaths.gagents, {
    scopeId: options?.scopeId,
    type: options?.actorTypeName,
    actorId: options?.actorId,
  });
}
