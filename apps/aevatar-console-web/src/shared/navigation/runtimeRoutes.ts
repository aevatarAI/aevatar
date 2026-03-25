const runtimePaths = {
  workflows: "/runtime/workflows",
  primitives: "/runtime/primitives",
  runs: "/runtime/runs",
  explorer: "/runtime/explorer",
  observability: "/runtime/observability",
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
  workflow?: string;
}): string {
  return buildHref(runtimePaths.runs, options);
}

export function buildRuntimeExplorerHref(options?: {
  actorId?: string;
}): string {
  return buildHref(runtimePaths.explorer, options);
}

export function buildRuntimeObservabilityHref(options?: {
  workflow?: string;
  actorId?: string;
  commandId?: string;
  runId?: string;
  stepId?: string;
}): string {
  return buildHref(runtimePaths.observability, options);
}
