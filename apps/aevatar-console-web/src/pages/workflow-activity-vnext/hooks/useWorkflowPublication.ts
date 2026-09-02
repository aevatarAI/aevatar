import { useQuery } from '@tanstack/react-query';
import { scopesApi } from '@/shared/api/scopesApi';
import type { ScopeWorkflowDetail } from '@/shared/models/scopes';

export const WORKFLOW_PUBLICATION_OBSERVATION_DELAYS_MS = [
  0, 300, 700, 1200, 2000,
] as const;

export const WORKFLOW_PUBLICATION_RETRY_INTERVAL_MS = 1500;

const DELAYED_WORKFLOW_CONFLICT_CODES = new Set([
  'USER_WORKFLOW_NOT_READY',
  'USER_WORKFLOW_STALE',
]);

export type WorkflowPublicationReceipt = {
  readonly scopeId: string;
  readonly workflowId: string;
  readonly revisionId: string;
};

export type WorkflowPublicationObservationInput = {
  readonly delaysMs?: readonly number[];
  readonly readWorkflow: (
    scopeId: string,
    workflowId: string,
  ) => Promise<ScopeWorkflowDetail>;
  readonly receipt: WorkflowPublicationReceipt;
  readonly wait?: (delayMs: number) => Promise<void>;
};

export type WorkflowPublicationObservationResult =
  | {
      readonly kind: 'observed';
      readonly publishedServiceId: string;
      readonly workflow: ScopeWorkflowDetail;
    }
  | { readonly kind: 'delayed' };

export type WorkflowPublicationObservationPhase =
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

function codeOf(error: unknown): string | undefined {
  if (
    error &&
    typeof error === 'object' &&
    'code' in error &&
    typeof error.code === 'string'
  ) {
    return error.code.trim();
  }

  return undefined;
}

function isDelayedWorkflowRead(error: unknown): boolean {
  const status = statusOf(error);
  return (
    status === 404 ||
    status === 408 ||
    status === 425 ||
    status === 429 ||
    (status !== undefined && status >= 500 && status < 600) ||
    (status === 409 && DELAYED_WORKFLOW_CONFLICT_CODES.has(codeOf(error) ?? ''))
  );
}

function assertAcceptedWorkflowIdentity(
  receipt: WorkflowPublicationReceipt,
  workflow: ScopeWorkflowDetail,
): void {
  if (workflow.scopeId !== receipt.scopeId) {
    throw new Error('The observed workflow does not match the accepted scope.');
  }
  if (workflow.workflow) {
    if (workflow.workflow.scopeId !== receipt.scopeId) {
      throw new Error(
        'The observed workflow does not match the accepted scope.',
      );
    }
    if (workflow.workflow.workflowId !== receipt.workflowId) {
      throw new Error(
        'The observed workflow does not match the accepted workflow.',
      );
    }
  }
}

function matchesAcceptedPublication(
  receipt: WorkflowPublicationReceipt,
  workflow: ScopeWorkflowDetail,
): boolean {
  return (
    workflow.available === true &&
    workflow.scopeId === receipt.scopeId &&
    workflow.workflow?.scopeId === receipt.scopeId &&
    workflow.workflow.workflowId === receipt.workflowId &&
    workflow.workflow.activeRevisionId === receipt.revisionId
  );
}

function normalizePublishedServiceId(
  publishedServiceId: string | null | undefined,
): string | null {
  const normalized = publishedServiceId?.trim();
  return normalized && normalized.toLowerCase() !== 'default'
    ? normalized
    : null;
}

export async function observeWorkflowPublication(
  input: WorkflowPublicationObservationInput,
): Promise<WorkflowPublicationObservationResult> {
  const delays = input.delaysMs ?? WORKFLOW_PUBLICATION_OBSERVATION_DELAYS_MS;
  const wait = input.wait ?? defaultWait;

  for (const delayMs of delays) {
    if (delayMs > 0) await wait(delayMs);

    let workflow: ScopeWorkflowDetail;
    try {
      workflow = await input.readWorkflow(
        input.receipt.scopeId,
        input.receipt.workflowId,
      );
    } catch (error) {
      if (isDelayedWorkflowRead(error)) continue;
      throw error;
    }

    assertAcceptedWorkflowIdentity(input.receipt, workflow);
    if (!matchesAcceptedPublication(input.receipt, workflow)) continue;

    const publishedServiceId = normalizePublishedServiceId(
      workflow.workflow?.publishedServiceId,
    );
    if (!publishedServiceId) continue;

    return {
      kind: 'observed',
      publishedServiceId,
      workflow,
    };
  }

  return { kind: 'delayed' };
}

export function resolveWorkflowPublicationPhase(input: {
  readonly data: WorkflowPublicationObservationResult | undefined;
  readonly enabled: boolean;
  readonly error: unknown;
  readonly isFetching: boolean;
  readonly isPending: boolean;
}): WorkflowPublicationObservationPhase {
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

export function useWorkflowPublication(
  receipt: WorkflowPublicationReceipt | null,
) {
  const enabled = receipt !== null;
  const query = useQuery({
    enabled,
    queryKey: [
      'workflow-activity-vnext',
      'workflow-publication',
      receipt?.scopeId ?? '',
      receipt?.workflowId ?? '',
      receipt?.revisionId ?? '',
    ],
    queryFn: () => {
      if (!receipt) {
        throw new Error('A workflow publication receipt is required.');
      }

      return observeWorkflowPublication({
        receipt,
        readWorkflow: (scopeId, workflowId) =>
          scopesApi.getWorkflowDetail(scopeId, workflowId),
      });
    },
    refetchInterval: (observationQuery) =>
      observationQuery.state.data?.kind === 'delayed'
        ? WORKFLOW_PUBLICATION_RETRY_INTERVAL_MS
        : false,
    refetchIntervalInBackground: true,
    retry: false,
  });
  const phase = resolveWorkflowPublicationPhase({
    data: query.data,
    enabled,
    error: query.error,
    isFetching: query.isFetching,
    isPending: query.isPending,
  });

  return {
    error: query.error,
    phase,
    publishedServiceId:
      phase === 'observed' && query.data?.kind === 'observed'
        ? query.data.publishedServiceId
        : null,
    receipt,
  } as const;
}
