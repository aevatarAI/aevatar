import { authFetch } from '@/shared/auth/fetch';
import { jsonBody, withQuery } from './http/client';
import {
  expectArray,
  expectRecord,
  readBoolean,
  readNullableString,
  readNumber,
  readOptionalArray,
  readOptionalRecord,
  readString,
} from './http/decoders';
import { readResponseErrorDetails } from './http/error';

export type WorkOrderLifecycleStatus =
  | 'accepted'
  | 'ready'
  | 'dispatch_pending'
  | 'running'
  | 'completed'
  | 'failed'
  | 'stopped'
  | 'cancelled'
  | 'timed_out';

export type WorkOrderAvailableActions = {
  readonly canReassign: boolean;
  readonly canDispatch: boolean;
  readonly canCancel: boolean;
};

export type WorkOrderPrincipal = {
  readonly principalId: string;
  readonly principalKind: string;
};

export type WorkOrderArtifactReference = {
  readonly artifactId: string;
  readonly artifactKind: string;
  readonly uri: string | null;
  readonly revisionId: string | null;
};

export type WorkOrderRunLink = {
  readonly runId: string;
  readonly runActorId: string;
  readonly commandId: string;
  readonly correlationId: string;
  readonly revisionId: string;
  readonly deploymentId: string;
  readonly acceptedAtUtc: string;
};

export type WorkOrderRunOutcomeReference = {
  readonly deliveryId: string;
  readonly runId: string;
  readonly runActorId: string;
  readonly commandId: string;
  readonly correlationId: string;
  readonly outcome: string;
  readonly terminalAtUtc: string;
};

export type WorkOrderFailure = {
  readonly code: string;
  readonly message: string;
  readonly source: string;
  readonly referenceId: string | null;
};

export type WorkOrderCurrentState = {
  readonly workOrderId: string;
  readonly scopeId: string;
  readonly teamId: string;
  readonly requester: WorkOrderPrincipal;
  readonly memberId: string;
  readonly publishedServiceId: string;
  readonly workflowId: string | null;
  readonly serviceRevisionId: string;
  readonly implementationKind: string;
  readonly endpointId: string;
  readonly intent: string;
  readonly dedupKey: string;
  readonly lifecycleStatus: WorkOrderLifecycleStatus;
  readonly lifecycleVersion: number;
  readonly stateVersion: number;
  readonly availableActions: WorkOrderAvailableActions;
  readonly input: {
    readonly chat: { readonly prompt: string };
    readonly inputArtifacts: readonly WorkOrderArtifactReference[];
    readonly declaredResultArtifacts: readonly WorkOrderArtifactReference[];
  };
  readonly run: WorkOrderRunLink | null;
  readonly runOutcome: WorkOrderRunOutcomeReference | null;
  readonly lateRunOutcome: WorkOrderRunOutcomeReference | null;
  readonly failure: WorkOrderFailure | null;
  readonly terminalReason: string | null;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
  readonly timeoutAtUtc: string | null;
};

export type WorkOrderListResult = {
  readonly scopeId: string;
  readonly workOrders: readonly WorkOrderCurrentState[];
  readonly nextPageToken: string | null;
};

export type WorkOrderAcceptedReceipt = {
  readonly workOrderId: string;
  readonly commandId: string;
  readonly correlationId: string;
  readonly stage: string;
  readonly acceptedAtUtc: string | null;
};

export class WorkOrderApiError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = 'WorkOrderApiError';
    this.status = status;
    this.code = code;
  }
}

type Decoder<T> = (value: unknown, label?: string) => T;

const lifecycleStatuses = new Set<WorkOrderLifecycleStatus>([
  'accepted',
  'ready',
  'dispatch_pending',
  'running',
  'completed',
  'failed',
  'stopped',
  'cancelled',
  'timed_out',
]);

function decodeLifecycleStatus(
  value: string,
  label: string,
): WorkOrderLifecycleStatus {
  const normalized = value.trim().toLowerCase() as WorkOrderLifecycleStatus;
  if (!lifecycleStatuses.has(normalized)) {
    throw new Error(`${label} has unknown value '${value}'.`);
  }
  return normalized;
}

function decodeArtifact(
  value: unknown,
  label = 'workOrder.artifact',
): WorkOrderArtifactReference {
  const record = expectRecord(value, label);
  return {
    artifactId: readString(
      record,
      ['artifactId', 'ArtifactId'],
      `${label}.artifactId`,
    ),
    artifactKind: readString(
      record,
      ['artifactKind', 'ArtifactKind'],
      `${label}.artifactKind`,
    ),
    uri: readNullableString(record, ['uri', 'Uri'], `${label}.uri`),
    revisionId: readNullableString(
      record,
      ['revisionId', 'RevisionId'],
      `${label}.revisionId`,
    ),
  };
}

function decodeRun(value: unknown, label = 'workOrder.run'): WorkOrderRunLink {
  const record = expectRecord(value, label);
  return {
    runId: readString(record, ['runId', 'RunId'], `${label}.runId`),
    runActorId: readString(
      record,
      ['runActorId', 'RunActorId'],
      `${label}.runActorId`,
    ),
    commandId: readString(
      record,
      ['commandId', 'CommandId'],
      `${label}.commandId`,
    ),
    correlationId: readString(
      record,
      ['correlationId', 'CorrelationId'],
      `${label}.correlationId`,
    ),
    revisionId: readString(
      record,
      ['revisionId', 'RevisionId'],
      `${label}.revisionId`,
    ),
    deploymentId: readString(
      record,
      ['deploymentId', 'DeploymentId'],
      `${label}.deploymentId`,
    ),
    acceptedAtUtc: readString(
      record,
      ['acceptedAtUtc', 'AcceptedAtUtc'],
      `${label}.acceptedAtUtc`,
    ),
  };
}

function decodeRunOutcome(
  value: unknown,
  label = 'workOrder.runOutcome',
): WorkOrderRunOutcomeReference {
  const record = expectRecord(value, label);
  return {
    deliveryId: readString(
      record,
      ['deliveryId', 'DeliveryId'],
      `${label}.deliveryId`,
    ),
    runId: readString(record, ['runId', 'RunId'], `${label}.runId`),
    runActorId: readString(
      record,
      ['runActorId', 'RunActorId'],
      `${label}.runActorId`,
    ),
    commandId: readString(
      record,
      ['commandId', 'CommandId'],
      `${label}.commandId`,
    ),
    correlationId: readString(
      record,
      ['correlationId', 'CorrelationId'],
      `${label}.correlationId`,
    ),
    outcome: readString(record, ['outcome', 'Outcome'], `${label}.outcome`),
    terminalAtUtc: readString(
      record,
      ['terminalAtUtc', 'TerminalAtUtc'],
      `${label}.terminalAtUtc`,
    ),
  };
}

function decodeCurrentState(
  value: unknown,
  label = 'workOrder',
): WorkOrderCurrentState {
  const record = expectRecord(value, label);
  const requester = expectRecord(
    record.requester ?? record.Requester,
    `${label}.requester`,
  );
  const availableActions = expectRecord(
    record.availableActions ?? record.AvailableActions,
    `${label}.availableActions`,
  );
  const input = expectRecord(record.input ?? record.Input, `${label}.input`);
  const chat = expectRecord(input.chat ?? input.Chat, `${label}.input.chat`);
  const run = readOptionalRecord(record, ['run', 'Run'], `${label}.run`);
  const runOutcome = readOptionalRecord(
    record,
    ['runOutcome', 'RunOutcome'],
    `${label}.runOutcome`,
  );
  const lateRunOutcome = readOptionalRecord(
    record,
    ['lateRunOutcome', 'LateRunOutcome'],
    `${label}.lateRunOutcome`,
  );
  const failure = readOptionalRecord(
    record,
    ['failure', 'Failure'],
    `${label}.failure`,
  );

  return {
    workOrderId: readString(
      record,
      ['workOrderId', 'WorkOrderId'],
      `${label}.workOrderId`,
    ),
    scopeId: readString(record, ['scopeId', 'ScopeId'], `${label}.scopeId`),
    teamId: readString(record, ['teamId', 'TeamId'], `${label}.teamId`),
    requester: {
      principalId: readString(
        requester,
        ['principalId', 'PrincipalId'],
        `${label}.requester.principalId`,
      ),
      principalKind: readString(
        requester,
        ['principalKind', 'PrincipalKind'],
        `${label}.requester.principalKind`,
      ),
    },
    memberId: readString(record, ['memberId', 'MemberId'], `${label}.memberId`),
    publishedServiceId: readString(
      record,
      ['publishedServiceId', 'PublishedServiceId'],
      `${label}.publishedServiceId`,
    ),
    workflowId: readNullableString(
      record,
      ['workflowId', 'WorkflowId'],
      `${label}.workflowId`,
    ),
    serviceRevisionId: readString(
      record,
      ['serviceRevisionId', 'ServiceRevisionId'],
      `${label}.serviceRevisionId`,
    ),
    implementationKind: readString(
      record,
      ['implementationKind', 'ImplementationKind'],
      `${label}.implementationKind`,
    ),
    endpointId: readString(
      record,
      ['endpointId', 'EndpointId'],
      `${label}.endpointId`,
    ),
    intent: readString(record, ['intent', 'Intent'], `${label}.intent`),
    dedupKey: readString(record, ['dedupKey', 'DedupKey'], `${label}.dedupKey`),
    lifecycleStatus: decodeLifecycleStatus(
      readString(
        record,
        ['lifecycleStatus', 'LifecycleStatus'],
        `${label}.lifecycleStatus`,
      ),
      `${label}.lifecycleStatus`,
    ),
    lifecycleVersion: readNumber(
      record,
      ['lifecycleVersion', 'LifecycleVersion'],
      `${label}.lifecycleVersion`,
    ),
    stateVersion: readNumber(
      record,
      ['stateVersion', 'StateVersion'],
      `${label}.stateVersion`,
    ),
    availableActions: {
      canReassign: readBoolean(
        availableActions,
        ['canReassign', 'CanReassign'],
        `${label}.availableActions.canReassign`,
      ),
      canDispatch: readBoolean(
        availableActions,
        ['canDispatch', 'CanDispatch'],
        `${label}.availableActions.canDispatch`,
      ),
      canCancel: readBoolean(
        availableActions,
        ['canCancel', 'CanCancel'],
        `${label}.availableActions.canCancel`,
      ),
    },
    input: {
      chat: {
        prompt: readString(
          chat,
          ['prompt', 'Prompt'],
          `${label}.input.chat.prompt`,
        ),
      },
      inputArtifacts: readOptionalArray(
        input,
        ['inputArtifacts', 'InputArtifacts'],
        `${label}.input.inputArtifacts`,
        decodeArtifact,
      ),
      declaredResultArtifacts: readOptionalArray(
        input,
        ['declaredResultArtifacts', 'DeclaredResultArtifacts'],
        `${label}.input.declaredResultArtifacts`,
        decodeArtifact,
      ),
    },
    run: run ? decodeRun(run, `${label}.run`) : null,
    runOutcome: runOutcome
      ? decodeRunOutcome(runOutcome, `${label}.runOutcome`)
      : null,
    lateRunOutcome: lateRunOutcome
      ? decodeRunOutcome(lateRunOutcome, `${label}.lateRunOutcome`)
      : null,
    failure: failure
      ? {
          code: readString(failure, ['code', 'Code'], `${label}.failure.code`),
          message: readString(
            failure,
            ['message', 'Message'],
            `${label}.failure.message`,
          ),
          source: readString(
            failure,
            ['source', 'Source'],
            `${label}.failure.source`,
          ),
          referenceId: readNullableString(
            failure,
            ['referenceId', 'ReferenceId'],
            `${label}.failure.referenceId`,
          ),
        }
      : null,
    terminalReason: readNullableString(
      record,
      ['terminalReason', 'TerminalReason'],
      `${label}.terminalReason`,
    ),
    createdAtUtc: readString(
      record,
      ['createdAtUtc', 'CreatedAtUtc'],
      `${label}.createdAtUtc`,
    ),
    updatedAtUtc: readString(
      record,
      ['updatedAtUtc', 'UpdatedAtUtc'],
      `${label}.updatedAtUtc`,
    ),
    timeoutAtUtc: readNullableString(
      record,
      ['timeoutAtUtc', 'TimeoutAtUtc'],
      `${label}.timeoutAtUtc`,
    ),
  };
}

function decodeList(value: unknown, label = 'workOrders'): WorkOrderListResult {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ['scopeId', 'ScopeId'], `${label}.scopeId`),
    workOrders: expectArray(
      record.workOrders ?? record.WorkOrders,
      `${label}.workOrders`,
      decodeCurrentState,
    ),
    nextPageToken: readNullableString(
      record,
      ['nextPageToken', 'NextPageToken'],
      `${label}.nextPageToken`,
    ),
  };
}

function decodeReceipt(
  value: unknown,
  label = 'workOrderReceipt',
): WorkOrderAcceptedReceipt {
  const record = expectRecord(value, label);
  return {
    workOrderId: readString(
      record,
      ['workOrderId', 'WorkOrderId'],
      `${label}.workOrderId`,
    ),
    commandId: readString(
      record,
      ['commandId', 'CommandId'],
      `${label}.commandId`,
    ),
    correlationId: readString(
      record,
      ['correlationId', 'CorrelationId'],
      `${label}.correlationId`,
    ),
    stage: readString(record, ['stage', 'Stage'], `${label}.stage`),
    acceptedAtUtc: readNullableString(
      record,
      ['acceptedAtUtc', 'AcceptedAtUtc'],
      `${label}.acceptedAtUtc`,
    ),
  };
}

async function request<T>(
  input: string,
  decoder: Decoder<T>,
  init?: RequestInit,
): Promise<T> {
  const response = await authFetch(input, init);
  if (!response.ok) {
    const details = await readResponseErrorDetails(response);
    throw new WorkOrderApiError(details.message, details.status, details.code);
  }
  return decoder(await response.json());
}

function workOrderPath(scopeId: string, workOrderId?: string): string {
  const collection = `/api/scopes/${encodeURIComponent(scopeId.trim())}/work-orders`;
  return workOrderId
    ? `${collection}/${encodeURIComponent(workOrderId.trim())}`
    : collection;
}

export const workOrdersApi = {
  list(input: {
    readonly scopeId: string;
    readonly teamId: string;
    readonly pageToken?: string;
    readonly pageSize?: number;
  }): Promise<WorkOrderListResult> {
    return request(
      withQuery(workOrderPath(input.scopeId), {
        teamId: input.teamId.trim(),
        pageToken: input.pageToken?.trim(),
        pageSize: input.pageSize ?? 200,
      }),
      decodeList,
    );
  },

  get(scopeId: string, workOrderId: string): Promise<WorkOrderCurrentState> {
    return request(workOrderPath(scopeId, workOrderId), decodeCurrentState);
  },

  reassign(input: {
    readonly scopeId: string;
    readonly workOrderId: string;
    readonly memberId: string;
    readonly publishedServiceId: string;
    readonly expectedLifecycleVersion: number;
  }): Promise<WorkOrderAcceptedReceipt> {
    return request(
      `${workOrderPath(input.scopeId, input.workOrderId)}:reassign`,
      decodeReceipt,
      {
        ...jsonBody({
          memberId: input.memberId.trim(),
          publishedServiceId: input.publishedServiceId.trim(),
          expectedLifecycleVersion: input.expectedLifecycleVersion,
        }),
        method: 'POST',
      },
    );
  },

  dispatch(input: {
    readonly scopeId: string;
    readonly workOrderId: string;
    readonly expectedLifecycleVersion: number;
  }): Promise<WorkOrderAcceptedReceipt> {
    return request(
      `${workOrderPath(input.scopeId, input.workOrderId)}:dispatch`,
      decodeReceipt,
      {
        ...jsonBody({
          expectedLifecycleVersion: input.expectedLifecycleVersion,
        }),
        method: 'POST',
      },
    );
  },

  cancel(input: {
    readonly scopeId: string;
    readonly workOrderId: string;
    readonly expectedLifecycleVersion: number;
    readonly reason?: string;
  }): Promise<WorkOrderAcceptedReceipt> {
    return request(
      `${workOrderPath(input.scopeId, input.workOrderId)}:cancel`,
      decodeReceipt,
      {
        ...jsonBody({
          expectedLifecycleVersion: input.expectedLifecycleVersion,
          reason: input.reason?.trim() || null,
        }),
        method: 'POST',
      },
    );
  },
};
