import { useQuery } from '@tanstack/react-query';
import React from 'react';
import { workflowActivityApi } from '@/shared/api/workflowActivityApi';
import type { WorkflowActivityRunDetail } from '@/shared/models/workflowActivity';

export const RUN_OBSERVATION_DELAYS_MS = [0, 300, 700, 1200, 2000] as const;

type RunObservationInput = {
  readonly delaysMs?: readonly number[];
  readonly read: (
    scopeId: string,
    runId: string,
  ) => Promise<WorkflowActivityRunDetail>;
  readonly runId: string;
  readonly scopeId: string;
  readonly wait?: (delayMs: number) => Promise<void>;
};

export type RunObservationResult =
  | { readonly kind: 'observed'; readonly run: WorkflowActivityRunDetail }
  | { readonly kind: 'delayed' };

export type RunObservationPhase =
  | 'idle'
  | 'observing'
  | 'observed'
  | 'delayed'
  | 'unauthorized'
  | 'forbidden'
  | 'failed';

function defaultWait(delayMs: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, delayMs));
}

function statusOf(error: unknown): number | undefined {
  if (
    error &&
    typeof error === 'object' &&
    'status' in error &&
    typeof error.status === 'number'
  ) {
    return error.status;
  }
  return undefined;
}

function assertObservedRun(
  run: WorkflowActivityRunDetail,
  scopeId: string,
  runId: string,
): void {
  if (run.summary.runId !== runId) {
    throw new Error('The observed run does not match the started run.');
  }
  if (run.summary.scopeId !== scopeId) {
    throw new Error('The observed run belongs to a different workspace.');
  }
  if (
    typeof run.summary.stateVersion !== 'number' ||
    !Number.isFinite(run.summary.stateVersion)
  ) {
    throw new Error('The observed run is missing a valid activity version.');
  }
}

export async function observeRunActivity(
  input: RunObservationInput,
): Promise<RunObservationResult> {
  const scopeId = input.scopeId.trim();
  const runId = input.runId.trim();
  const delays = input.delaysMs ?? RUN_OBSERVATION_DELAYS_MS;
  const wait = input.wait ?? defaultWait;

  for (const delayMs of delays) {
    if (delayMs > 0) await wait(delayMs);
    try {
      const run = await input.read(scopeId, runId);
      assertObservedRun(run, scopeId, runId);
      return { kind: 'observed', run };
    } catch (error) {
      if (statusOf(error) !== 404) throw error;
    }
  }

  return { kind: 'delayed' };
}

export function resolveRunObservationPhase(input: {
  readonly data: RunObservationResult | undefined;
  readonly enabled: boolean;
  readonly error: unknown;
  readonly isFetching: boolean;
  readonly isPending: boolean;
}): RunObservationPhase {
  if (!input.enabled) return 'idle';
  if (input.isPending || input.isFetching) return 'observing';

  const status = statusOf(input.error);
  if (status === 401) return 'unauthorized';
  if (status === 403) return 'forbidden';
  if (input.error) return 'failed';
  if (input.data?.kind === 'observed') return 'observed';
  if (input.data?.kind === 'delayed') return 'delayed';
  return 'failed';
}

export function useRunObservation(scopeId: string, runId: string) {
  const normalizedScopeId = scopeId.trim();
  const normalizedRunId = runId.trim();
  const enabled = Boolean(normalizedScopeId && normalizedRunId);
  const query = useQuery({
    enabled,
    queryKey: [
      'workflow-activity-vnext',
      'run-observation',
      normalizedScopeId,
      normalizedRunId,
    ],
    queryFn: () =>
      observeRunActivity({
        scopeId: normalizedScopeId,
        runId: normalizedRunId,
        read: (nextScopeId, nextRunId) =>
          workflowActivityApi.getRun(nextScopeId, nextRunId),
      }),
    refetchInterval: (currentQuery) => {
      const result = currentQuery.state.data;
      if (result?.kind !== 'observed') return false;

      return ['accepted', 'pending', 'running', 'waiting'].includes(
        result.run.summary.status.toLowerCase(),
      )
        ? 1000
        : false;
    },
    retry: false,
  });

  const phase = resolveRunObservationPhase({
    data: query.data,
    enabled,
    error: query.error,
    isFetching: query.isFetching,
    isPending: query.isPending,
  });

  const retry = React.useCallback(async () => {
    if (!enabled) return null;
    const result = await query.refetch();
    return result.data ?? null;
  }, [enabled, query]);

  return {
    error: query.error,
    phase,
    retry,
    run:
      phase === 'observed' && query.data?.kind === 'observed'
        ? query.data.run
        : null,
  } as const;
}
