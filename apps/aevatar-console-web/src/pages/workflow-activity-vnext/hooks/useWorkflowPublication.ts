import { useQuery } from '@tanstack/react-query';
import React from 'react';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { scopesApi } from '@/shared/api/scopesApi';
import type { ScopeServiceRevisionCatalogSnapshot } from '@/shared/models/runtime/scopeServices';
import type { ScopeWorkflowDetail } from '@/shared/models/scopes';
import type { StudioScopeBindingRevision } from '@/shared/studio/models';

export const WORKFLOW_PUBLICATION_OBSERVATION_DELAYS_MS = [
  0, 300, 700, 1200, 2000,
] as const;

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
  readonly readRevisions: (
    scopeId: string,
    serviceId: string,
  ) => Promise<ScopeServiceRevisionCatalogSnapshot>;
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
      readonly catalog: ScopeServiceRevisionCatalogSnapshot;
      readonly publishedServiceId: string;
      readonly revision: StudioScopeBindingRevision;
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

function normalizeStatus(value: string): string {
  return value.toLowerCase().replace(/[\s_-]+/g, '');
}

function isTerminalRevisionFailure(
  revision: StudioScopeBindingRevision,
): boolean {
  const status = normalizeStatus(revision.status);
  return (
    status === 'preparationfailed' ||
    status === 'retired' ||
    Boolean(revision.failureReason.trim())
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

function assertAcceptedCatalogIdentity(
  receipt: WorkflowPublicationReceipt,
  publishedServiceId: string,
  catalog: ScopeServiceRevisionCatalogSnapshot,
): void {
  if (
    catalog.scopeId !== receipt.scopeId ||
    catalog.serviceId !== publishedServiceId
  ) {
    throw new Error(
      'The observed service catalog does not match the accepted service.',
    );
  }
}

function assertRevisionImplementsWorkflow(
  workflow: ScopeWorkflowDetail,
  revision: StudioScopeBindingRevision,
): void {
  if (
    workflow.workflow &&
    revision.workflowDefinitionActorId !== workflow.workflow.actorId
  ) {
    throw new Error(
      'The accepted service revision does not implement the observed workflow.',
    );
  }
}

function matchesAcceptedPublication(
  receipt: WorkflowPublicationReceipt,
  publishedServiceId: string,
  workflow: ScopeWorkflowDetail,
  catalog: ScopeServiceRevisionCatalogSnapshot,
  revision: StudioScopeBindingRevision,
): boolean {
  return (
    workflow.available === true &&
    workflow.scopeId === receipt.scopeId &&
    workflow.workflow?.scopeId === receipt.scopeId &&
    workflow.workflow?.workflowId === receipt.workflowId &&
    workflow.workflow?.activeRevisionId === receipt.revisionId &&
    catalog.scopeId === receipt.scopeId &&
    catalog.serviceId === publishedServiceId &&
    catalog.activeServingRevisionId === receipt.revisionId &&
    revision.revisionId === receipt.revisionId &&
    revision.implementationKind === 'workflow' &&
    normalizeStatus(revision.status) === 'published' &&
    revision.isActiveServing === true &&
    revision.isServingTarget === true &&
    revision.allocationWeight > 0 &&
    revision.workflowDefinitionActorId === workflow.workflow?.actorId &&
    normalizeStatus(revision.servingState) === 'active'
  );
}

function isDelayedWorkflowRead(error: unknown): boolean {
  const status = statusOf(error);
  return (
    status === 404 ||
    (status === 409 && DELAYED_WORKFLOW_CONFLICT_CODES.has(codeOf(error) ?? ''))
  );
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

    const publishedServiceId =
      workflow.workflow?.publishedServiceId.trim() ?? '';
    if (!workflow.available || !workflow.workflow || !publishedServiceId)
      continue;

    let catalog: ScopeServiceRevisionCatalogSnapshot;
    try {
      catalog = await input.readRevisions(
        input.receipt.scopeId,
        publishedServiceId,
      );
    } catch (error) {
      if (statusOf(error) === 404) continue;
      throw error;
    }
    assertAcceptedCatalogIdentity(input.receipt, publishedServiceId, catalog);

    const revision = catalog?.revisions.find(
      (candidate) => candidate.revisionId === input.receipt.revisionId,
    );
    if (revision && isTerminalRevisionFailure(revision)) {
      throw new Error(
        'The accepted workflow publication reached a terminal revision state.',
      );
    }
    if (revision) {
      assertRevisionImplementsWorkflow(workflow, revision);
    }

    if (!revision) continue;
    if (
      matchesAcceptedPublication(
        input.receipt,
        publishedServiceId,
        workflow,
        catalog,
        revision,
      )
    ) {
      return {
        kind: 'observed',
        workflow,
        catalog,
        publishedServiceId,
        revision,
      };
    }
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
        readRevisions: (scopeId, serviceId) =>
          scopeRuntimeApi.getServiceRevisions(scopeId, serviceId),
      });
    },
    retry: false,
  });
  const phase = resolveWorkflowPublicationPhase({
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
    receipt,
    publishedServiceId:
      phase === 'observed' && query.data?.kind === 'observed'
        ? query.data.publishedServiceId
        : '',
    retry,
    revision:
      phase === 'observed' && query.data?.kind === 'observed'
        ? query.data.revision
        : null,
  } as const;
}
