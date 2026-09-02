import {
  expectArray,
  expectRecord,
  expectString,
  normalizeEnumValue,
  readBoolean,
  readNullableString,
  readNumber,
  readString,
} from '@/shared/api/http/decoders';
import { readResponseErrorDetails } from '@/shared/api/http/error';
import type { WorkflowCatalogItemDetail } from '@/shared/api/models';
import { decodeWorkflowCatalogItemDetailResponse } from '@/shared/api/runtimeDecoders';
import { scopesApi } from '@/shared/api/scopesApi';
import { authFetch } from '@/shared/auth/fetch';
import { t } from '@/shared/i18n/messages';
import type {
  ScopeWorkflowDetail,
  ScopeWorkflowSummary,
} from '@/shared/models/scopes';
import type {
  StudioAppContext,
  StudioAuthSession,
  StudioConnectorCatalog,
  StudioConnectorCatalogImportResult,
  StudioConnectorDraftResponse,
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioExplicitRequestBodyMode,
  StudioExplicitRequestMethod,
  StudioExplicitRequestPreview,
  StudioExplicitRequestPreviewInput,
  StudioExplicitRequestPreviewItem,
  StudioExplicitRequestResponseMode,
  StudioExplicitRequestRisk,
  StudioLlmModelCatalog,
  StudioLlmModelCatalogCertainty,
  StudioLlmModelCatalogDiagnostic,
  StudioLlmModelSelection,
  StudioLlmSelection,
  StudioMemberBindingAcceptedResponse,
  StudioMemberBindingAckStage,
  StudioMemberBindingContract,
  StudioMemberBindingFailure,
  StudioMemberBindingRunResult,
  StudioMemberBindingRunRole,
  StudioMemberBindingRunStatus,
  StudioMemberBindingRunStatusResponse,
  StudioMemberBindingViewResponse,
  StudioMemberCommandResponse,
  StudioMemberCommandStatus,
  StudioMemberDetail,
  StudioMemberImplementationKind,
  StudioMemberImplementationRef,
  StudioMemberLifecycleStage,
  StudioMemberRoster,
  StudioMemberSummary,
  StudioMemberWorkflowBindingInput,
  StudioOrnnHealthResult,
  StudioOrnnSkillSearchResult,
  StudioParseYamlResult,
  StudioPublishWorkflowAcceptedResult,
  StudioPublishWorkflowInput,
  StudioRoleCatalog,
  StudioRoleCatalogImportResult,
  StudioRoleDraftResponse,
  StudioSaveAndBindWorkflowAcceptedResult,
  StudioSaveAndBindWorkflowInput,
  StudioSaveUserLlmIntent,
  StudioSaveWorkflowInput,
  StudioScopeBindingActivationResult,
  StudioScopeBindingImplementationKind,
  StudioScopeBindingResult,
  StudioScopeBindingRetirementResult,
  StudioScopeBindingRevision,
  StudioScopeBindingStatus,
  StudioScopeBindingTargetKind,
  StudioScopeGAgentBindingInput,
  StudioScopeGAgentBindingResult,
  StudioScopeScriptBindingActivationResult,
  StudioScopeScriptBindingInput,
  StudioScopeScriptBindingResult,
  StudioScopeScriptBindingStatus,
  StudioSerializeYamlResult,
  StudioStartExecutionInput,
  StudioTeamCommandResponse,
  StudioTeamCommandStatus,
  StudioTeamCreateInput,
  StudioTeamLifecycleStage,
  StudioTeamRoster,
  StudioTeamSummary,
  StudioTeamUpdateInput,
  StudioUserConfig,
  StudioUserConfigRuntime,
  StudioUserConfigSaveReceipt,
  StudioUserLlmRemediation,
  StudioUserLlmSelectionStatus,
  StudioUserLlmSettings,
  StudioWorkflowBoardSnapshot,
  StudioWorkflowBoardSnapshotRequest,
  StudioWorkflowCapabilityDescriptor,
  StudioWorkflowCapabilityDiagnostic,
  StudioWorkflowCapabilityDiagnosticCode,
  StudioWorkflowCapabilityList,
  StudioWorkflowCapabilityOperation,
  StudioWorkflowCapabilityParameter,
  StudioWorkflowCapabilityParameterLocation,
  StudioWorkflowCapabilityReadiness,
  StudioWorkflowCapabilityReadinessInput,
  StudioWorkflowCapabilityReadinessStatus,
  StudioWorkflowCapabilityRemediationAction,
  StudioWorkflowCapabilitySchema,
  StudioWorkflowCapabilitySelector,
  StudioWorkflowCapabilitySource,
  StudioWorkflowCapabilitySourceKind,
  StudioWorkflowCapabilityValueKind,
  StudioWorkflowDocument,
  StudioWorkflowDraft,
  StudioWorkflowDraftCreateAcceptedReceipt,
  StudioWorkflowDraftSummary,
  StudioWorkflowFile,
  StudioWorkflowSaveResult,
  StudioWorkflowSummary,
  StudioWorkspaceSettings,
} from './models';
import {
  normalizeStudioMemberLifecycleStage,
  normalizeStudioScopeBindingImplementationKind,
  normalizeStudioTeamLifecycleStage,
} from './models';
import { getOrnnRuntimeConfig } from './ornnConfig';

const JSON_HEADERS = {
  'Content-Type': 'application/json',
  Accept: 'application/json',
};
async function studioHostFetch(
  input: string,
  init?: RequestInit,
): Promise<Response> {
  const headers = new Headers(init?.headers);
  return authFetch(input, {
    credentials: 'same-origin',
    ...init,
    headers,
  });
}

async function externalFetch(
  input: string,
  init?: RequestInit,
): Promise<Response> {
  const headers = new Headers(init?.headers);
  if (!headers.has('Accept')) {
    headers.set('Accept', 'application/json');
  }

  return authFetch(input, {
    ...init,
    headers,
  });
}

export class StudioApiError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = 'StudioApiError';
    this.code = code;
    this.status = status;
  }
}

export function isStudioApiStatus(error: unknown, status: number): boolean {
  return error instanceof StudioApiError && error.status === status;
}

export function isStudioApiErrorCode(
  error: unknown,
  status: number,
  code: string,
): boolean {
  return (
    error instanceof StudioApiError &&
    error.status === status &&
    error.code === code
  );
}

async function createStudioApiError(
  response: Response,
): Promise<StudioApiError> {
  const details = await readResponseErrorDetails(response);
  return new StudioApiError(details.message, response.status, details.code);
}

function isJsonContentType(contentType: string | null): boolean {
  const value = String(contentType || '').toLowerCase();
  return value.includes('application/json') || value.includes('+json');
}

function readContentType(response: Response): string | null {
  const headers = (response as Response & { headers?: Headers }).headers;
  return headers?.get?.('content-type') ?? null;
}

function trimOptional(value: string | null | undefined): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function compactObject<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(
    Object.entries(value).filter(([, entry]) => entry !== undefined),
  ) as T;
}

function readExplicitRequestEnum<T extends string>(
  record: Record<string, unknown>,
  key: string,
  label: string,
  allowedValues: readonly T[],
): T {
  const value = readString(record, key, label);
  if (!allowedValues.includes(value as T)) {
    throw new Error(`${label} is not supported.`);
  }

  return value as T;
}

function readNonBlankExplicitRequestString(
  record: Record<string, unknown>,
  key: string,
  label: string,
): string {
  const value = readString(record, key, label);
  if (!value.trim()) {
    throw new Error(`${label} must not be blank.`);
  }

  return value;
}

function decodeStudioExplicitRequestPreviewItem(
  value: unknown,
  label = 'StudioExplicitRequestPreviewItem',
): StudioExplicitRequestPreviewItem {
  const record = expectRecord(value, label);
  const allowedExecutionModes = expectArray(
    record.allowedExecutionModes,
    `${label}.allowedExecutionModes`,
    (entry, entryLabel) => {
      if (entry !== 'interactive' && entry !== 'durable') {
        throw new Error(`${entryLabel} is not supported.`);
      }

      return entry;
    },
  );
  if (allowedExecutionModes.length === 0) {
    throw new Error(`${label}.allowedExecutionModes must not be empty.`);
  }

  return {
    callSiteId: readNonBlankExplicitRequestString(
      record,
      'callSiteId',
      `${label}.callSiteId`,
    ),
    requestContractDigest: readNonBlankExplicitRequestString(
      record,
      'requestContractDigest',
      `${label}.requestContractDigest`,
    ),
    userServiceId: readNonBlankExplicitRequestString(
      record,
      'userServiceId',
      `${label}.userServiceId`,
    ),
    method: readExplicitRequestEnum<StudioExplicitRequestMethod>(
      record,
      'method',
      `${label}.method`,
      ['get', 'head', 'options', 'post', 'put', 'patch', 'delete'],
    ),
    pathTemplate: readNonBlankExplicitRequestString(
      record,
      'pathTemplate',
      `${label}.pathTemplate`,
    ),
    bodyMode: readExplicitRequestEnum<StudioExplicitRequestBodyMode>(
      record,
      'bodyMode',
      `${label}.bodyMode`,
      ['none', 'json'],
    ),
    bodyRequired: readBoolean(record, 'bodyRequired', `${label}.bodyRequired`),
    responseMode: readExplicitRequestEnum<StudioExplicitRequestResponseMode>(
      record,
      'responseMode',
      `${label}.responseMode`,
      ['text', 'file_artifact'],
    ),
    effectiveRisk: readExplicitRequestEnum<StudioExplicitRequestRisk>(
      record,
      'effectiveRisk',
      `${label}.effectiveRisk`,
      ['read_only', 'write', 'destructive'],
    ),
    approvalRequired: readBoolean(
      record,
      'approvalRequired',
      `${label}.approvalRequired`,
    ),
    allowedExecutionModes,
  };
}

function decodeStudioExplicitRequestPreview(
  value: unknown,
): StudioExplicitRequestPreview {
  const record = expectRecord(value, 'StudioExplicitRequestPreview');
  const items = expectArray(
    record.items,
    'StudioExplicitRequestPreview.items',
    decodeStudioExplicitRequestPreviewItem,
  );
  const callSiteIds = new Set<string>();
  for (const item of items) {
    if (callSiteIds.has(item.callSiteId)) {
      throw new Error(
        `StudioExplicitRequestPreview.items contains duplicate callSiteId '${item.callSiteId}'.`,
      );
    }

    callSiteIds.add(item.callSiteId);
  }

  return {
    workflowId: readNonBlankExplicitRequestString(
      record,
      'workflowId',
      'StudioExplicitRequestPreview.workflowId',
    ),
    revisionId: readNonBlankExplicitRequestString(
      record,
      'revisionId',
      'StudioExplicitRequestPreview.revisionId',
    ),
    items,
  };
}

function decodeStudioWorkflowCapabilitySelector(
  value: unknown,
  label = 'StudioWorkflowCapabilityDescriptor.selector',
): StudioWorkflowCapabilitySelector {
  const record = expectRecord(value, label);
  const kind = readString(record, 'kind', `${label}.kind`);
  if (kind === 'nyxid_operation') {
    return {
      kind,
      userServiceId: readNonBlankExplicitRequestString(
        record,
        'userServiceId',
        `${label}.userServiceId`,
      ),
      endpointId: readNonBlankExplicitRequestString(
        record,
        'endpointId',
        `${label}.endpointId`,
      ),
    };
  }
  if (kind === 'host_connector') {
    return {
      kind,
      connectorCapabilityRef: readNonBlankExplicitRequestString(
        record,
        'connectorCapabilityRef',
        `${label}.connectorCapabilityRef`,
      ),
      operationId: readNonBlankExplicitRequestString(
        record,
        'operationId',
        `${label}.operationId`,
      ),
      contractDigest: readNonBlankExplicitRequestString(
        record,
        'contractDigest',
        `${label}.contractDigest`,
      ),
    };
  }
  if (kind === 'nyxid_request') {
    return {
      kind,
      userServiceId: readNonBlankExplicitRequestString(
        record,
        'userServiceId',
        `${label}.userServiceId`,
      ),
      method: readExplicitRequestEnum(record, 'method', `${label}.method`, [
        'get',
        'head',
        'options',
        'post',
        'put',
        'patch',
        'delete',
      ]),
      pathTemplate: readNonBlankExplicitRequestString(
        record,
        'pathTemplate',
        `${label}.pathTemplate`,
      ),
      queryParameters: expectArray(
        record.queryParameters,
        `${label}.queryParameters`,
        expectString,
      ),
      headerParameters: expectArray(
        record.headerParameters,
        `${label}.headerParameters`,
        expectString,
      ),
      bodyMode: readExplicitRequestEnum(
        record,
        'bodyMode',
        `${label}.bodyMode`,
        ['none', 'json'],
      ),
      responseMode: readExplicitRequestEnum(
        record,
        'responseMode',
        `${label}.responseMode`,
        ['text', 'file_artifact'],
      ),
      bodyRequired: readBoolean(
        record,
        'bodyRequired',
        `${label}.bodyRequired`,
      ),
    };
  }

  throw new Error(`${label}.kind is not supported.`);
}

function workflowCapabilitySelectorKey(
  selector: StudioWorkflowCapabilitySelector,
): string {
  if (selector.kind === 'nyxid_operation') {
    return [selector.kind, selector.userServiceId, selector.endpointId].join(
      '\u0000',
    );
  }
  if (selector.kind === 'host_connector') {
    return [
      selector.kind,
      selector.connectorCapabilityRef,
      selector.operationId,
      selector.contractDigest,
    ].join('\u0000');
  }
  return [
    selector.kind,
    selector.userServiceId,
    selector.method,
    selector.pathTemplate,
  ].join('\u0000');
}

function decodeStudioWorkflowCapabilitySource(
  value: unknown,
  label: string,
): StudioWorkflowCapabilitySource {
  const record = expectRecord(value, label);
  return {
    kind: readExplicitRequestEnum<StudioWorkflowCapabilitySourceKind>(
      record,
      'kind',
      `${label}.kind`,
      [
        'connector_catalog',
        'nyxid_user_services',
        'nyxid_open_api',
        'durable_authorization_catalog',
        'nyxid_mcp_config',
      ],
    ),
    sourceId: readNonBlankExplicitRequestString(
      record,
      'sourceId',
      `${label}.sourceId`,
    ),
    sourceVersion: readNumber(
      record,
      'sourceVersion',
      `${label}.sourceVersion`,
    ),
    observedAt: readNullableString(record, 'observedAt', `${label}.observedAt`),
    freshUntil: readNullableString(record, 'freshUntil', `${label}.freshUntil`),
  };
}

function decodeNullableWorkflowCapabilitySource(
  value: unknown,
  label: string,
): StudioWorkflowCapabilitySource | null {
  return value == null
    ? null
    : decodeStudioWorkflowCapabilitySource(value, label);
}

function decodeStudioWorkflowCapabilityDescriptor(
  value: unknown,
  label = 'StudioWorkflowCapabilityDescriptor',
): StudioWorkflowCapabilityDescriptor {
  const record = expectRecord(value, label);
  return {
    displayName: readNonBlankExplicitRequestString(
      record,
      'displayName',
      `${label}.displayName`,
    ),
    readOnly: readBoolean(record, 'readOnly', `${label}.readOnly`),
    destructive: readBoolean(record, 'destructive', `${label}.destructive`),
    selector: decodeStudioWorkflowCapabilitySelector(
      record.selector,
      `${label}.selector`,
    ),
    source: decodeNullableWorkflowCapabilitySource(
      record.source,
      `${label}.source`,
    ),
  };
}

function decodeStudioWorkflowCapabilityDiagnostic(
  value: unknown,
  label = 'StudioWorkflowCapabilityDiagnostic',
): StudioWorkflowCapabilityDiagnostic {
  const record = expectRecord(value, label);
  return {
    code: readExplicitRequestEnum<StudioWorkflowCapabilityDiagnosticCode>(
      record,
      'code',
      `${label}.code`,
      [
        'source_unavailable',
        'no_exact_user_service',
        'generic_proxy_rejected',
        'invalid_service_identity',
        'ambiguous_service_identity',
        'invalid_endpoint_identity',
        'ambiguous_endpoint_identity',
        'unsupported_parameter',
        'unsupported_request_body',
        'unsupported_schema',
        'unsupported_response',
      ],
    ),
    safeMessage: readString(record, 'safeMessage', `${label}.safeMessage`),
    count: readNumber(record, 'count', `${label}.count`),
    source: decodeNullableWorkflowCapabilitySource(
      record.source,
      `${label}.source`,
    ),
  };
}

function decodeStudioWorkflowCapabilityList(
  value: unknown,
): StudioWorkflowCapabilityList {
  const record = expectRecord(value, 'StudioWorkflowCapabilityList');
  const capabilities = expectArray(
    record.capabilities,
    'StudioWorkflowCapabilityList.capabilities',
    decodeStudioWorkflowCapabilityDescriptor,
  );
  const selectorKeys = new Set<string>();
  for (const descriptor of capabilities) {
    const selectorKey = workflowCapabilitySelectorKey(descriptor.selector);
    if (selectorKeys.has(selectorKey)) {
      throw new Error(
        'StudioWorkflowCapabilityList.capabilities contains a duplicate selector.',
      );
    }
    selectorKeys.add(selectorKey);
  }

  return {
    capabilities,
    candidateCount: readNumber(
      record,
      'candidateCount',
      'StudioWorkflowCapabilityList.candidateCount',
    ),
    rejectedCount: readNumber(
      record,
      'rejectedCount',
      'StudioWorkflowCapabilityList.rejectedCount',
    ),
    diagnostics: expectArray(
      record.diagnostics,
      'StudioWorkflowCapabilityList.diagnostics',
      decodeStudioWorkflowCapabilityDiagnostic,
    ),
  };
}

function decodeStudioWorkflowCapabilitySchema(
  value: unknown,
  label: string,
): StudioWorkflowCapabilitySchema {
  const record = expectRecord(value, label);
  return {
    valueKind: readExplicitRequestEnum<StudioWorkflowCapabilityValueKind>(
      record,
      'valueKind',
      `${label}.valueKind`,
      ['string', 'integer', 'number', 'boolean', 'object', 'array'],
    ),
    properties: expectArray(
      record.properties,
      `${label}.properties`,
      (entry, entryLabel = `${label}.properties[]`) => {
        const property = expectRecord(entry, entryLabel);
        return {
          name: readNonBlankExplicitRequestString(
            property,
            'name',
            `${entryLabel}.name`,
          ),
          schema: decodeStudioWorkflowCapabilitySchema(
            property.schema,
            `${entryLabel}.schema`,
          ),
        };
      },
    ),
    requiredProperties: expectArray(
      record.requiredProperties,
      `${label}.requiredProperties`,
      expectString,
    ),
    items:
      record.items == null
        ? null
        : decodeStudioWorkflowCapabilitySchema(record.items, `${label}.items`),
    allowedValues: expectArray(
      record.allowedValues,
      `${label}.allowedValues`,
      expectString,
    ),
    additionalPropertiesAllowed: readBoolean(
      record,
      'additionalPropertiesAllowed',
      `${label}.additionalPropertiesAllowed`,
    ),
  };
}

function decodeStudioWorkflowCapabilityParameter(
  value: unknown,
  label = 'StudioWorkflowCapabilityParameter',
): StudioWorkflowCapabilityParameter {
  const record = expectRecord(value, label);
  return {
    name: readNonBlankExplicitRequestString(record, 'name', `${label}.name`),
    location:
      readExplicitRequestEnum<StudioWorkflowCapabilityParameterLocation>(
        record,
        'location',
        `${label}.location`,
        ['path', 'query', 'header'],
      ),
    required: readBoolean(record, 'required', `${label}.required`),
    schema: decodeStudioWorkflowCapabilitySchema(
      record.schema,
      `${label}.schema`,
    ),
  };
}

function decodeStudioWorkflowCapabilityOperation(
  value: unknown,
  label = 'StudioWorkflowCapabilityOperation',
): StudioWorkflowCapabilityOperation {
  const record = expectRecord(value, label);
  const requestBody =
    record.requestBody == null
      ? null
      : expectRecord(record.requestBody, `${label}.requestBody`);
  const responsePolicy =
    record.responsePolicy == null
      ? null
      : expectRecord(record.responsePolicy, `${label}.responsePolicy`);
  const executionPolicy =
    record.executionPolicy == null
      ? null
      : expectRecord(record.executionPolicy, `${label}.executionPolicy`);

  return {
    userServiceId: readNonBlankExplicitRequestString(
      record,
      'userServiceId',
      `${label}.userServiceId`,
    ),
    endpointId: readNonBlankExplicitRequestString(
      record,
      'endpointId',
      `${label}.endpointId`,
    ),
    serviceSlug: readString(record, 'serviceSlug', `${label}.serviceSlug`),
    httpMethod: readNonBlankExplicitRequestString(
      record,
      'httpMethod',
      `${label}.httpMethod`,
    ),
    pathTemplate: readNonBlankExplicitRequestString(
      record,
      'pathTemplate',
      `${label}.pathTemplate`,
    ),
    parameters: expectArray(
      record.parameters,
      `${label}.parameters`,
      decodeStudioWorkflowCapabilityParameter,
    ),
    requestBody: requestBody
      ? {
          required: readBoolean(
            requestBody,
            'required',
            `${label}.requestBody.required`,
          ),
          mediaType: readString(
            requestBody,
            'mediaType',
            `${label}.requestBody.mediaType`,
          ),
          schema: decodeStudioWorkflowCapabilitySchema(
            requestBody.schema,
            `${label}.requestBody.schema`,
          ),
        }
      : null,
    responsePolicy: responsePolicy
      ? {
          textAllowed: readBoolean(
            responsePolicy,
            'textAllowed',
            `${label}.responsePolicy.textAllowed`,
          ),
          fileArtifactAllowed: readBoolean(
            responsePolicy,
            'fileArtifactAllowed',
            `${label}.responsePolicy.fileArtifactAllowed`,
          ),
          mediaTypes: expectArray(
            responsePolicy.mediaTypes,
            `${label}.responsePolicy.mediaTypes`,
            expectString,
          ),
        }
      : null,
    executionPolicy: executionPolicy
      ? {
          risk: readExplicitRequestEnum(
            executionPolicy,
            'risk',
            `${label}.executionPolicy.risk`,
            ['read_only', 'write', 'destructive'],
          ),
          approval: readExplicitRequestEnum(
            executionPolicy,
            'approval',
            `${label}.executionPolicy.approval`,
            ['none', 'required'],
          ),
          enforcementOwner: readExplicitRequestEnum(
            executionPolicy,
            'enforcementOwner',
            `${label}.executionPolicy.enforcementOwner`,
            ['aevatar', 'nyxid'],
          ),
          allowedExecutionModes: expectArray(
            executionPolicy.allowedExecutionModes,
            `${label}.executionPolicy.allowedExecutionModes`,
            (
              entry,
              entryLabel = `${label}.executionPolicy.allowedExecutionModes[]`,
            ) => {
              if (entry !== 'interactive' && entry !== 'durable') {
                throw new Error(`${entryLabel} is not supported.`);
              }
              return entry;
            },
          ),
        }
      : null,
  };
}

const workflowCapabilityReadinessStatuses = [
  'selection_required',
  'connector_not_found',
  'service_registration_required',
  'credential_connection_required',
  'service_access_denied',
  'node_binding_required',
  'node_unavailable',
  'endpoint_contract_required',
  'operation_selection_required',
  'source_stale',
  'durable_authorization_unavailable',
  'contract_drift',
  'ready',
  'admission_rebind_required',
] as const satisfies readonly StudioWorkflowCapabilityReadinessStatus[];

function decodeStudioWorkflowCapabilityReadiness(
  value: unknown,
  expectedSelector: StudioWorkflowCapabilityReadinessInput['selector'],
  expectedExecutionMode: StudioWorkflowCapabilityReadinessInput['executionMode'],
): StudioWorkflowCapabilityReadiness {
  const label = 'StudioWorkflowCapabilityReadiness';
  const record = expectRecord(value, label);
  const selectedSelector =
    record.selectedSelector == null
      ? null
      : decodeStudioWorkflowCapabilitySelector(
          record.selectedSelector,
          `${label}.selectedSelector`,
        );
  if (!selectedSelector) {
    throw new Error(
      'StudioWorkflowCapabilityReadiness requires the requested selectedSelector.',
    );
  }
  if (
    workflowCapabilitySelectorKey(selectedSelector) !==
    workflowCapabilitySelectorKey(expectedSelector)
  ) {
    throw new Error(
      'StudioWorkflowCapabilityReadiness returned a different selectedSelector.',
    );
  }

  const executionMode = readExplicitRequestEnum(
    record,
    'executionMode',
    `${label}.executionMode`,
    ['interactive', 'durable'],
  );
  if (executionMode !== expectedExecutionMode) {
    throw new Error(
      'StudioWorkflowCapabilityReadiness returned a different executionMode.',
    );
  }

  const selectedOperation =
    record.selectedOperation == null
      ? null
      : decodeStudioWorkflowCapabilityOperation(
          record.selectedOperation,
          `${label}.selectedOperation`,
        );
  if (
    selectedOperation &&
    (selectedOperation.userServiceId !== expectedSelector.userServiceId ||
      selectedOperation.endpointId !== expectedSelector.endpointId)
  ) {
    throw new Error(
      'StudioWorkflowCapabilityReadiness returned a different selectedOperation.',
    );
  }
  const status =
    readExplicitRequestEnum<StudioWorkflowCapabilityReadinessStatus>(
      record,
      'status',
      `${label}.status`,
      workflowCapabilityReadinessStatuses,
    );
  if (status === 'ready' && !selectedOperation) {
    throw new Error(
      'StudioWorkflowCapabilityReadiness returned ready without a selectedOperation.',
    );
  }

  return {
    executionMode,
    status,
    selectedSelector,
    selectedOperation,
    blockers: expectArray(
      record.blockers,
      `${label}.blockers`,
      (entry, entryLabel = `${label}.blockers[]`) => {
        const blocker = expectRecord(entry, entryLabel);
        return {
          status:
            readExplicitRequestEnum<StudioWorkflowCapabilityReadinessStatus>(
              blocker,
              'status',
              `${entryLabel}.status`,
              workflowCapabilityReadinessStatuses,
            ),
          code: readString(blocker, 'code', `${entryLabel}.code`),
          safeMessage: readString(
            blocker,
            'safeMessage',
            `${entryLabel}.safeMessage`,
          ),
        };
      },
    ),
    remediations: expectArray(
      record.remediations,
      `${label}.remediations`,
      (entry, entryLabel = `${label}.remediations[]`) => {
        const remediation = expectRecord(entry, entryLabel);
        return {
          actionKind:
            readExplicitRequestEnum<StudioWorkflowCapabilityRemediationAction>(
              remediation,
              'actionKind',
              `${entryLabel}.actionKind`,
              [
                'select_capability',
                'configure_connector',
                'register_service',
                'connect_credential',
                'request_access',
                'bind_node',
                'restore_node',
                'publish_endpoint_contract',
                'select_operation',
                'refresh_source',
                'use_interactive_execution',
                'rebind_workflow',
              ],
            ),
          label: readString(remediation, 'label', `${entryLabel}.label`),
          trustedLocator: readString(
            remediation,
            'trustedLocator',
            `${entryLabel}.trustedLocator`,
          ),
        };
      },
    ),
    sources: expectArray(
      record.sources,
      `${label}.sources`,
      decodeStudioWorkflowCapabilitySource,
    ),
  };
}

function toScopeWorkflowDirectoryId(scopeId: string): string {
  return `scope:${scopeId}`;
}

function toScopeWorkflowPath(scopeId: string, workflowId: string): string {
  return `scope://${scopeId}/${workflowId}.yaml`;
}

function resolveScopeWorkflowName(workflow: ScopeWorkflowSummary): string {
  return (
    workflow.displayName?.trim() ||
    workflow.workflowName?.trim() ||
    workflow.workflowId
  );
}

function toCommittedWorkflowSummary(
  scopeId: string,
  workflow: ScopeWorkflowSummary,
): StudioWorkflowSummary {
  return {
    activeRevisionId: trimOptional(workflow.activeRevisionId) ?? null,
    serviceKey: trimOptional(workflow.serviceKey) ?? null,
    workflowId: workflow.workflowId,
    name: resolveScopeWorkflowName(workflow),
    description: '',
    fileName: `${workflow.workflowId}.yaml`,
    filePath: toScopeWorkflowPath(scopeId, workflow.workflowId),
    directoryId: toScopeWorkflowDirectoryId(scopeId),
    directoryLabel: scopeId,
    stepCount: 0,
    hasLayout: false,
    updatedAtUtc: workflow.updatedAt,
  };
}

function toCommittedWorkflowFile(
  scopeId: string,
  detail: ScopeWorkflowDetail,
): StudioWorkflowFile {
  const workflow = detail.workflow;
  if (!workflow) {
    throw new Error('Not Found');
  }

  return {
    workflowId: workflow.workflowId,
    name: resolveScopeWorkflowName(workflow),
    fileName: `${workflow.workflowId}.yaml`,
    filePath: toScopeWorkflowPath(scopeId, workflow.workflowId),
    directoryId: toScopeWorkflowDirectoryId(scopeId),
    directoryLabel: scopeId,
    yaml: detail.source?.workflowYaml ?? '',
    document: null,
    draftExists: false,
    findings: [],
    updatedAtUtc: workflow.updatedAt,
  };
}

function toWorkflowFile(
  draft: StudioWorkflowDraft,
  draftExists: boolean,
): StudioWorkflowFile {
  return {
    ...draft,
    document: null,
    draftExists,
    findings: [],
  };
}

function decodeStudioWorkflowDraft(
  value: unknown,
  label = 'StudioWorkflowDraft',
): StudioWorkflowDraft {
  const record = expectRecord(value, label);
  return {
    workflowId: readString(record, 'workflowId', `${label}.workflowId`),
    name: readString(record, 'name', `${label}.name`),
    fileName: readString(record, 'fileName', `${label}.fileName`),
    filePath: readString(record, 'filePath', `${label}.filePath`),
    directoryId: readString(record, 'directoryId', `${label}.directoryId`),
    directoryLabel: readString(
      record,
      'directoryLabel',
      `${label}.directoryLabel`,
    ),
    yaml: readString(record, 'yaml', `${label}.yaml`),
    layout: record.layout,
    updatedAtUtc: readString(record, 'updatedAtUtc', `${label}.updatedAtUtc`),
  };
}

function decodeStudioWorkflowDraftCreateAcceptedReceipt(
  value: unknown,
  label = 'StudioWorkflowDraftCreateAcceptedReceipt',
): StudioWorkflowDraftCreateAcceptedReceipt {
  const record = expectRecord(value, label);
  const readiness = expectRecord(record.readiness, `${label}.readiness`);
  const accepted = readBoolean(record, 'accepted', `${label}.accepted`);
  if (!accepted) {
    throw new Error(`${label}.accepted must be true.`);
  }

  return {
    accepted,
    workflowId: readString(record, 'workflowId', `${label}.workflowId`),
    commandId: readString(record, 'commandId', `${label}.commandId`),
    ackStage: readString(record, 'ackStage', `${label}.ackStage`),
    actorId: readString(record, 'actorId', `${label}.actorId`),
    workspaceId: readString(record, 'workspaceId', `${label}.workspaceId`),
    expectedVersion:
      record.expectedVersion === null || record.expectedVersion === undefined
        ? null
        : readNumber(record, 'expectedVersion', `${label}.expectedVersion`),
    ackedAtUtc: readString(record, 'ackedAtUtc', `${label}.ackedAtUtc`),
    readiness: {
      readable: readBoolean(
        readiness,
        'readable',
        `${label}.readiness.readable`,
      ),
      stage: readString(readiness, 'stage', `${label}.readiness.stage`),
      message: readString(readiness, 'message', `${label}.readiness.message`),
    },
  };
}

function selectLatestTimestamp(left: string, right: string): string {
  return Date.parse(left) >= Date.parse(right) ? left : right;
}

function withOptionalScopeId(path: string, scopeId?: string | null): string {
  const normalizedScopeId = trimOptional(scopeId);
  if (!normalizedScopeId) {
    return path;
  }

  const separator = path.includes('?') ? '&' : '?';
  return `${path}${separator}scopeId=${encodeURIComponent(normalizedScopeId)}`;
}

function normalizeOrnnBaseUrl(baseUrl?: string | null): string {
  return trimOptional(baseUrl)?.replace(/\/+$/, '') ?? '';
}

function readOptionalNumber(value: unknown): number | undefined {
  return typeof value === 'number' && !Number.isNaN(value) ? value : undefined;
}

function readOptionalBoolean(value: unknown): boolean | undefined {
  return typeof value === 'boolean' ? value : undefined;
}

function decodeOrnnSkillSearchResult(
  value: unknown,
  baseUrl: string,
  fallbackPage: number,
  fallbackPageSize: number,
): StudioOrnnSkillSearchResult {
  const record = expectRecord(value, 'Ornn search response');
  const payload =
    record.data === undefined
      ? record
      : expectRecord(record.data, 'Ornn search response.data');

  const items = Array.isArray(payload.items)
    ? payload.items.map((entry, index) => {
        const skill = expectRecord(
          entry,
          `Ornn search response.items[${index}]`,
        );
        return {
          guid:
            readNullableString(
              skill,
              'guid',
              `Ornn search response.items[${index}].guid`,
            ) ?? '',
          name:
            readNullableString(
              skill,
              'name',
              `Ornn search response.items[${index}].name`,
            ) ?? 'Unnamed skill',
          description:
            readNullableString(
              skill,
              'description',
              `Ornn search response.items[${index}].description`,
            ) ?? '',
          isPrivate:
            readOptionalBoolean(skill.isPrivate) ??
            readOptionalBoolean(skill.private) ??
            false,
        };
      })
    : [];

  return {
    baseUrl,
    total: readOptionalNumber(payload.total) ?? items.length,
    totalPages: readOptionalNumber(payload.totalPages) ?? 1,
    page: readOptionalNumber(payload.page) ?? fallbackPage,
    pageSize: readOptionalNumber(payload.pageSize) ?? fallbackPageSize,
    items,
    message:
      readNullableString(payload, 'message', 'Ornn search response.message') ??
      undefined,
  };
}

function decodeStudioLlmModelCatalogDiagnostic(
  value: unknown,
  label: string,
): StudioLlmModelCatalogDiagnostic {
  const diagnostic = expectString(value, label);
  switch (diagnostic) {
    case 'unspecified':
    case 'not_published':
    case 'route_not_ready':
    case 'access_denied':
    case 'observation_unavailable':
    case 'response_invalid':
    case 'response_too_large':
    case 'pattern_only':
      return diagnostic;
    default:
      throw new Error(`${label} is not supported.`);
  }
}

function decodeStudioLlmModelSelection(
  value: unknown,
  label: string,
): StudioLlmModelSelection {
  const record = expectRecord(value, label);
  const kind = readString(record, 'kind', `${label}.kind`);
  const modelId = readNullableString(record, 'modelId', `${label}.modelId`);
  switch (kind) {
    case 'unspecified':
    case 'provider_default':
      if (modelId !== null) {
        throw new Error(`${label}.modelId must be null for ${kind}.`);
      }
      return { kind };
    case 'explicit_model':
      if (!modelId) {
        throw new Error(`${label}.modelId must not be empty.`);
      }
      return { kind, modelId };
    default:
      throw new Error(`${label}.kind is not supported.`);
  }
}

function decodeStudioLlmSelection(
  value: unknown,
  label: string,
): StudioLlmSelection {
  const record = expectRecord(value, label);
  const routeKind = readString(record, 'routeKind', `${label}.routeKind`);
  const modelSelection: StudioLlmModelSelection =
    routeKind === 'unspecified' && record.modelSelection == null
      ? { kind: 'unspecified' }
      : decodeStudioLlmModelSelection(
          record.modelSelection,
          `${label}.modelSelection`,
        );
  switch (routeKind) {
    case 'unspecified':
      if (modelSelection.kind !== 'unspecified') {
        throw new Error(`${label}.modelSelection must be unspecified.`);
      }
      return { routeKind, modelSelection };
    case 'gateway': {
      if (modelSelection.kind === 'unspecified') {
        throw new Error(
          `${label}.modelSelection must select a model behavior.`,
        );
      }
      const routeValue = readString(
        record,
        'routeValue',
        `${label}.routeValue`,
      );
      if (!routeValue) {
        throw new Error(`${label}.routeValue must not be empty.`);
      }
      return { routeKind, routeValue, modelSelection };
    }
    case 'nyx_id_user_service': {
      if (modelSelection.kind === 'unspecified') {
        throw new Error(
          `${label}.modelSelection must select a model behavior.`,
        );
      }
      const routeValue = readString(
        record,
        'routeValue',
        `${label}.routeValue`,
      );
      const nyxIdUserServiceId = readString(
        record,
        'nyxIdUserServiceId',
        `${label}.nyxIdUserServiceId`,
      );
      const serviceSlugSnapshot = readString(
        record,
        'serviceSlugSnapshot',
        `${label}.serviceSlugSnapshot`,
      );
      if (!routeValue || !nyxIdUserServiceId || !serviceSlugSnapshot) {
        throw new Error(
          `${label} must contain a complete user service identity.`,
        );
      }
      return {
        routeKind,
        routeValue,
        nyxIdUserServiceId,
        serviceSlugSnapshot,
        modelSelection,
      };
    }
    default:
      throw new Error(`${label}.routeKind is not supported.`);
  }
}

function decodeStudioLlmModelCatalogCertainty(
  value: unknown,
  label: string,
): StudioLlmModelCatalogCertainty {
  const certainty = expectString(value, label);
  switch (certainty) {
    case 'enumerated':
    case 'not_verifiable':
    case 'unavailable':
      return certainty;
    default:
      throw new Error(`${label} is not supported.`);
  }
}

function decodeStudioLlmModelCatalog(
  value: unknown,
  label: string,
): StudioLlmModelCatalog {
  const record = expectRecord(value, label);
  return {
    certainty: decodeStudioLlmModelCatalogCertainty(
      record.certainty,
      `${label}.certainty`,
    ),
    modelIds: expectArray(
      record.modelIds,
      `${label}.modelIds`,
      (entry, entryLabel) =>
        expectString(entry, entryLabel ?? `${label}.modelIds[]`),
    ),
    defaultModelId: readNullableString(
      record,
      'defaultModelId',
      `${label}.defaultModelId`,
    ),
    diagnostic: decodeStudioLlmModelCatalogDiagnostic(
      record.diagnostic,
      `${label}.diagnostic`,
    ),
  };
}

function decodeStudioUserLlmSelectionStatus(
  value: unknown,
  label: string,
): StudioUserLlmSelectionStatus {
  const status = expectString(value, label);
  switch (status) {
    case 'system_default':
    case 'ready':
    case 'verification_unavailable':
    case 'needs_repair':
    case 'legacy_repair_required':
      return status;
    default:
      throw new Error(`${label} is not supported.`);
  }
}

function decodeStudioUserLlmRemediation(
  value: unknown,
  label: string,
): StudioUserLlmRemediation {
  const remediation = expectString(value, label);
  switch (remediation) {
    case 'none':
    case 'retry_catalog':
    case 'connect_provider':
    case 'choose_replacement':
    case 'reselect':
      return remediation;
    default:
      throw new Error(`${label} is not supported.`);
  }
}

function decodeStudioUserLlmSettings(
  value: unknown,
  label = 'StudioUserLlmSettings',
): StudioUserLlmSettings {
  const record = expectRecord(value, label);
  return {
    savedSelection:
      record.savedSelection == null
        ? null
        : decodeStudioLlmSelection(
            record.savedSelection,
            `${label}.savedSelection`,
          ),
    savedRouteLabel: readString(
      record,
      'savedRouteLabel',
      `${label}.savedRouteLabel`,
    ),
    selectionStatus: decodeStudioUserLlmSelectionStatus(
      record.selectionStatus,
      `${label}.selectionStatus`,
    ),
    catalogDiagnostic: decodeStudioLlmModelCatalogDiagnostic(
      record.catalogDiagnostic,
      `${label}.catalogDiagnostic`,
    ),
    remediation: decodeStudioUserLlmRemediation(
      record.remediation,
      `${label}.remediation`,
    ),
    routeOptions: expectArray(
      record.routeOptions ?? [],
      `${label}.routeOptions`,
      (entry, optionLabel) => {
        const resolvedOptionLabel = optionLabel ?? `${label}.routeOptions[]`;
        const option = expectRecord(entry, resolvedOptionLabel);
        return {
          routeValue: readString(
            option,
            'routeValue',
            `${resolvedOptionLabel}.routeValue`,
          ),
          label: readString(option, 'label', `${resolvedOptionLabel}.label`),
          source: readString(option, 'source', `${resolvedOptionLabel}.source`),
          status: readString(option, 'status', `${resolvedOptionLabel}.status`),
          allowed: readBoolean(
            option,
            'allowed',
            `${resolvedOptionLabel}.allowed`,
          ),
          ready: readBoolean(option, 'ready', `${resolvedOptionLabel}.ready`),
          userServiceId: readNullableString(
            option,
            'userServiceId',
            `${resolvedOptionLabel}.userServiceId`,
          ),
          serviceSlug: readNullableString(
            option,
            'serviceSlug',
            `${resolvedOptionLabel}.serviceSlug`,
          ),
          modelCatalog: decodeStudioLlmModelCatalog(
            option.modelCatalog,
            `${resolvedOptionLabel}.modelCatalog`,
          ),
          description: readNullableString(
            option,
            'description',
            `${resolvedOptionLabel}.description`,
          ),
        };
      },
    ),
    modelGroupsByRoute: expectArray(
      record.modelGroupsByRoute ?? [],
      `${label}.modelGroupsByRoute`,
      (entry, groupLabel) => {
        const resolvedGroupLabel =
          groupLabel ?? `${label}.modelGroupsByRoute[]`;
        const group = expectRecord(entry, resolvedGroupLabel);
        return {
          routeValue: readString(
            group,
            'routeValue',
            `${resolvedGroupLabel}.routeValue`,
          ),
          groupId: readString(
            group,
            'groupId',
            `${resolvedGroupLabel}.groupId`,
          ),
          label: readString(group, 'label', `${resolvedGroupLabel}.label`),
          models: expectArray(
            group.models ?? [],
            `${resolvedGroupLabel}.models`,
            (entryModel, modelLabel) =>
              expectString(
                entryModel,
                modelLabel ?? `${resolvedGroupLabel}.models[]`,
              ),
          ),
        };
      },
    ),
    catalogStatus: readString(
      record,
      'catalogStatus',
      `${label}.catalogStatus`,
    ),
    capabilities: (() => {
      const capabilities = expectRecord(
        record.capabilities,
        `${label}.capabilities`,
      );
      return {
        canEditRoute: readBoolean(
          capabilities,
          'canEditRoute',
          `${label}.capabilities.canEditRoute`,
        ),
        canEditModel: readBoolean(
          capabilities,
          'canEditModel',
          `${label}.capabilities.canEditModel`,
        ),
        canSave: readBoolean(
          capabilities,
          'canSave',
          `${label}.capabilities.canSave`,
        ),
        canRetryCatalog: readBoolean(
          capabilities,
          'canRetryCatalog',
          `${label}.capabilities.canRetryCatalog`,
        ),
      };
    })(),
    setupHint: record.setupHint,
  };
}

function decodeStudioUserConfigRuntime(
  value: unknown,
  label = 'StudioUserConfigRuntime',
): StudioUserConfigRuntime {
  const record = expectRecord(value, label);
  const runtimeDefaults = expectRecord(
    record.runtimeDefaults,
    `${label}.runtimeDefaults`,
  );
  return {
    runtimeMode: readString(record, 'runtimeMode', `${label}.runtimeMode`),
    activeRuntimeBaseUrl: readString(
      record,
      'activeRuntimeBaseUrl',
      `${label}.activeRuntimeBaseUrl`,
    ),
    localRuntimeBaseUrl: readString(
      record,
      'localRuntimeBaseUrl',
      `${label}.localRuntimeBaseUrl`,
    ),
    remoteRuntimeBaseUrl: readString(
      record,
      'remoteRuntimeBaseUrl',
      `${label}.remoteRuntimeBaseUrl`,
    ),
    runtimeDefaults: {
      localRuntimeBaseUrl: readString(
        runtimeDefaults,
        'localRuntimeBaseUrl',
        `${label}.runtimeDefaults.localRuntimeBaseUrl`,
      ),
      remoteRuntimeBaseUrl: readString(
        runtimeDefaults,
        'remoteRuntimeBaseUrl',
        `${label}.runtimeDefaults.remoteRuntimeBaseUrl`,
      ),
      localMode: readString(
        runtimeDefaults,
        'localMode',
        `${label}.runtimeDefaults.localMode`,
      ),
      remoteMode: readString(
        runtimeDefaults,
        'remoteMode',
        `${label}.runtimeDefaults.remoteMode`,
      ),
    },
  };
}

function decodeStudioUserConfigSaveReceipt(
  value: unknown,
  label = 'StudioUserConfigSaveReceipt',
): StudioUserConfigSaveReceipt {
  const record = expectRecord(value, label);
  return {
    accepted: readBoolean(record, 'accepted', `${label}.accepted`),
    commandId: readString(record, 'commandId', `${label}.commandId`),
    ackStage: readString(record, 'ackStage', `${label}.ackStage`),
    actorId: readString(record, 'actorId', `${label}.actorId`),
    correlationId: readString(
      record,
      'correlationId',
      `${label}.correlationId`,
    ),
    ackedAtUtc: readString(record, 'ackedAtUtc', `${label}.ackedAtUtc`),
  };
}

async function requestJson<T>(input: string, init?: RequestInit): Promise<T> {
  const response = await studioHostFetch(input, init);
  if (!response.ok) {
    throw await createStudioApiError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function requestJsonOrNull<T>(
  input: string,
  init?: RequestInit,
): Promise<T | null> {
  const response = await studioHostFetch(input, init);
  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw await createStudioApiError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function requestDecodedJson<T>(
  input: string,
  decoder: (value: unknown) => T,
  init?: RequestInit,
): Promise<T> {
  const response = await studioHostFetch(input, init);
  if (!response.ok) {
    throw await createStudioApiError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return decoder(await response.json());
}

async function requestDecodedJsonOrAccepted<T>(
  input: string,
  decoder: (value: unknown) => T,
  init?: RequestInit,
): Promise<T | undefined> {
  const response = await studioHostFetch(input, init);
  if (!response.ok) {
    throw await createStudioApiError(response);
  }

  if (response.status === 204) {
    return undefined;
  }

  if (
    response.status === 202 &&
    !isJsonContentType(readContentType(response))
  ) {
    return undefined;
  }

  return decoder(await response.json());
}

async function request<T>(input: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  const isFormDataBody =
    typeof FormData !== 'undefined' && init?.body instanceof FormData;
  if (!isFormDataBody && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  if (!headers.has('Accept')) {
    headers.set('Accept', 'application/json');
  }

  const response = await studioHostFetch(input, {
    ...init,
    headers,
  });
  if (!response.ok) {
    throw await createStudioApiError(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  if (!isJsonContentType(readContentType(response))) {
    throw new Error('Studio API returned an unexpected response format.');
  }

  return (await response.json()) as T;
}

async function streamSse(
  input: string,
  body: unknown,
  onFrame: (frame: unknown) => void,
  signal?: AbortSignal,
): Promise<void> {
  const response = await studioHostFetch(input, {
    method: 'POST',
    headers: {
      Accept: 'text/event-stream',
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
    signal,
  });
  if (!response.ok) {
    throw await createStudioApiError(response);
  }

  if (!response.body) {
    return;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  while (true) {
    const { done, value } = await reader.read();
    buffer += decoder.decode(value || new Uint8Array(), { stream: !done });

    let boundary = buffer.indexOf('\n\n');
    while (boundary >= 0) {
      const block = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);

      const data = block
        .split('\n')
        .filter((line) => line.startsWith('data:'))
        .map((line) => line.slice(5).trim())
        .join('\n');

      if (data && data !== '[DONE]') {
        onFrame(JSON.parse(data) as unknown);
      }

      boundary = buffer.indexOf('\n\n');
    }

    if (done) {
      break;
    }
  }
}

function normalizeAssistantFrame(
  frame: unknown,
): { type: string; delta?: string; message?: string } | null {
  if (!frame || typeof frame !== 'object') {
    return null;
  }

  const candidate = frame as Record<string, unknown>;
  if (typeof candidate.type === 'string') {
    return {
      type: candidate.type,
      delta: typeof candidate.delta === 'string' ? candidate.delta : undefined,
      message:
        typeof candidate.message === 'string' ? candidate.message : undefined,
    };
  }

  if (candidate.textMessageContent) {
    const payload = candidate.textMessageContent as Record<string, unknown>;
    return {
      type: 'TEXT_MESSAGE_CONTENT',
      delta: typeof payload.delta === 'string' ? payload.delta : '',
    };
  }

  if (candidate.textMessageReasoning) {
    const payload = candidate.textMessageReasoning as Record<string, unknown>;
    return {
      type: 'TEXT_MESSAGE_REASONING',
      delta: typeof payload.delta === 'string' ? payload.delta : '',
    };
  }

  if (candidate.textMessageEnd) {
    const payload = candidate.textMessageEnd as Record<string, unknown>;
    return {
      type: 'TEXT_MESSAGE_END',
      delta: typeof payload.delta === 'string' ? payload.delta : '',
      message: typeof payload.message === 'string' ? payload.message : '',
    };
  }

  if (candidate.runError) {
    const payload = candidate.runError as Record<string, unknown>;
    return {
      type: 'RUN_ERROR',
      message:
        typeof payload.message === 'string'
          ? payload.message
          : 'Assistant run failed.',
    };
  }

  return null;
}

function decodeStudioScopeBindingRevision(
  value: unknown,
  label = 'StudioScopeBindingRevision',
): StudioScopeBindingRevision {
  const record = expectRecord(value, label);
  const implementationKind = readScopeBindingImplementationKind(record, [
    'implementationKind',
    'ImplementationKind',
  ]);
  return {
    revisionId: readString(
      record,
      ['revisionId', 'RevisionId'],
      `${label}.revisionId`,
    ),
    implementationKind,
    status: readString(record, ['status', 'Status'], `${label}.status`),
    artifactHash: readString(
      record,
      ['artifactHash', 'ArtifactHash'],
      `${label}.artifactHash`,
    ),
    failureReason: readString(
      record,
      ['failureReason', 'FailureReason'],
      `${label}.failureReason`,
    ),
    isDefaultServing: readBoolean(
      record,
      ['isDefaultServing', 'IsDefaultServing'],
      `${label}.isDefaultServing`,
    ),
    isActiveServing: readBoolean(
      record,
      ['isActiveServing', 'IsActiveServing'],
      `${label}.isActiveServing`,
    ),
    isServingTarget: readBoolean(
      record,
      ['isServingTarget', 'IsServingTarget'],
      `${label}.isServingTarget`,
    ),
    allocationWeight: readNumber(
      record,
      ['allocationWeight', 'AllocationWeight'],
      `${label}.allocationWeight`,
    ),
    servingState: readString(
      record,
      ['servingState', 'ServingState'],
      `${label}.servingState`,
    ),
    deploymentId: readString(
      record,
      ['deploymentId', 'DeploymentId'],
      `${label}.deploymentId`,
    ),
    primaryActorId: readString(
      record,
      ['primaryActorId', 'PrimaryActorId'],
      `${label}.primaryActorId`,
    ),
    createdAt: readNullableString(
      record,
      ['createdAt', 'CreatedAt'],
      `${label}.createdAt`,
    ),
    preparedAt: readNullableString(
      record,
      ['preparedAt', 'PreparedAt'],
      `${label}.preparedAt`,
    ),
    publishedAt: readNullableString(
      record,
      ['publishedAt', 'PublishedAt'],
      `${label}.publishedAt`,
    ),
    retiredAt: readNullableString(
      record,
      ['retiredAt', 'RetiredAt'],
      `${label}.retiredAt`,
    ),
    workflowName:
      readOptionalString(record, ['workflowName', 'WorkflowName']) || '',
    workflowDefinitionActorId:
      readOptionalString(record, [
        'workflowDefinitionActorId',
        'WorkflowDefinitionActorId',
      ]) || '',
    inlineWorkflowCount:
      record.inlineWorkflowCount === undefined &&
      record.InlineWorkflowCount === undefined
        ? 0
        : readNumber(
            record,
            ['inlineWorkflowCount', 'InlineWorkflowCount'],
            `${label}.inlineWorkflowCount`,
          ),
    scriptId: readOptionalString(record, ['scriptId', 'ScriptId']) || '',
    scriptRevision:
      readOptionalString(record, ['scriptRevision', 'ScriptRevision']) || '',
    scriptDefinitionActorId:
      readOptionalString(record, [
        'scriptDefinitionActorId',
        'ScriptDefinitionActorId',
      ]) || '',
    scriptSourceHash:
      readOptionalString(record, ['scriptSourceHash', 'ScriptSourceHash']) ||
      '',
    staticActorTypeName:
      readOptionalString(record, [
        'staticActorTypeName',
        'StaticActorTypeName',
      ]) || '',
    staticAgentKind:
      readOptionalString(record, ['staticAgentKind', 'StaticAgentKind']) || '',
  };
}

function readOptionalString(
  record: Record<string, unknown>,
  keys: string[],
): string | undefined {
  for (const key of keys) {
    const rawValue = record[key];
    if (typeof rawValue !== 'string') {
      continue;
    }

    const normalized = rawValue.trim();
    if (normalized) {
      return normalized;
    }
  }

  return undefined;
}

function readOptionalScalar(
  record: Record<string, unknown>,
  keys: string[],
): string | number | undefined {
  for (const key of keys) {
    const rawValue = record[key];
    if (
      typeof rawValue === 'string' ||
      (typeof rawValue === 'number' && !Number.isNaN(rawValue))
    ) {
      return rawValue;
    }
  }

  return undefined;
}

function readWorkflowBoardExecutionAvailability(
  value: unknown,
): StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['executionAvailability'] {
  return normalizeEnumValue(value, 'executionAvailability', {
    '0': 'unknown',
    '1': 'available',
    '2': 'unavailable',
    '3': 'pending_backend_contract',
    available: 'available',
    pendingbackendcontract: 'pending_backend_contract',
    pending_backend_contract: 'pending_backend_contract',
    unavailable: 'unavailable',
    unknown: 'unknown',
    unspecified: 'unknown',
  }) as StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['executionAvailability'];
}

function readWorkflowBoardExecutionStatus(
  value: unknown,
): StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['executionStatus'] {
  return normalizeEnumValue(value, 'executionStatus', {
    '0': 'unknown',
    '1': 'running',
    '2': 'waiting',
    '3': 'failed',
    '4': 'timed_out',
    '5': 'retrying',
    '6': 'completed',
    '7': 'stopped',
    active: 'running',
    awaiting_input: 'waiting',
    awaitinginput: 'waiting',
    canceled: 'stopped',
    cancelled: 'stopped',
    completed: 'completed',
    done: 'completed',
    failed: 'failed',
    human_input_required: 'waiting',
    humaninputrequired: 'waiting',
    retry_pending: 'retrying',
    retrypending: 'retrying',
    retrying: 'retrying',
    running: 'running',
    stopped: 'stopped',
    succeeded: 'completed',
    success: 'completed',
    suspended: 'waiting',
    timed_out: 'timed_out',
    timedout: 'timed_out',
    timeout: 'timed_out',
    waiting: 'waiting',
    unknown: 'unknown',
    unspecified: 'unknown',
  }) as StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['executionStatus'];
}

function readWorkflowBoardCurrentNodeStatus(
  value: unknown,
): NonNullable<
  StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['currentNode']
>['status'] {
  return normalizeEnumValue(value, 'currentNode.status', {
    '0': 'unknown',
    '1': 'running',
    '2': 'waiting',
    '3': 'pending',
    '4': 'failed',
    '5': 'completed',
    active: 'running',
    completed: 'completed',
    done: 'completed',
    failed: 'failed',
    in_progress: 'running',
    inprogress: 'running',
    pending: 'pending',
    queued: 'pending',
    running: 'running',
    waiting: 'waiting',
    unknown: 'unknown',
    unspecified: 'unknown',
  }) as NonNullable<
    StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['currentNode']
  >['status'];
}

function readWorkflowBoardPendingNodeStatus(
  value: unknown,
): StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['pendingNodes'][number]['status'] {
  return normalizeEnumValue(value, 'pendingNode.status', {
    '0': 'unknown',
    '1': 'waiting',
    '2': 'pending',
    '3': 'queued',
    pending: 'pending',
    queued: 'queued',
    waiting: 'waiting',
    unknown: 'unknown',
    unspecified: 'unknown',
  }) as StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['pendingNodes'][number]['status'];
}

function decodeStudioWorkflowBoardCounts(
  value: unknown,
  label = 'StudioWorkflowBoardCounts',
): StudioWorkflowBoardSnapshot['counts'] {
  const record = expectRecord(value, label);
  return {
    completed: readNumber(
      record,
      ['completed', 'Completed'],
      `${label}.completed`,
    ),
    failed: readNumber(record, ['failed', 'Failed'], `${label}.failed`),
    retrying: readNumber(record, ['retrying', 'Retrying'], `${label}.retrying`),
    running: readNumber(record, ['running', 'Running'], `${label}.running`),
    waiting: readNumber(record, ['waiting', 'Waiting'], `${label}.waiting`),
  };
}

function decodeStudioWorkflowBoardProgress(
  value: unknown,
  label = 'StudioWorkflowBoardProgress',
): StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['progress'] {
  const record = expectRecord(value, label);
  return {
    completedSteps: readNumber(
      record,
      ['completedSteps', 'CompletedSteps'],
      `${label}.completedSteps`,
    ),
    totalSteps: readNumber(
      record,
      ['totalSteps', 'TotalSteps'],
      `${label}.totalSteps`,
    ),
  };
}

function decodeStudioWorkflowBoardCurrentNode(
  value: unknown,
  label = 'StudioWorkflowBoardCurrentNode',
): NonNullable<
  StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['currentNode']
> {
  const record = expectRecord(value, label);
  return {
    nodeId: readString(record, ['nodeId', 'NodeId'], `${label}.nodeId`),
    name: readString(record, ['name', 'Name'], `${label}.name`),
    status: readWorkflowBoardCurrentNodeStatus(
      record.status ?? record.Status ?? 'unknown',
    ),
    startedAt:
      readNullableString(
        record,
        ['startedAt', 'StartedAt'],
        `${label}.startedAt`,
      ) ?? null,
    updatedAt:
      readNullableString(
        record,
        ['updatedAt', 'UpdatedAt'],
        `${label}.updatedAt`,
      ) ?? null,
    durationMs:
      readOptionalNumber(record.durationMs ?? record.DurationMs) ?? null,
  };
}

function decodeStudioWorkflowBoardCompletedNode(
  value: unknown,
  label = 'StudioWorkflowBoardCompletedNode',
): StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['completedNodes'][number] {
  const record = expectRecord(value, label);
  return {
    nodeId: readString(record, ['nodeId', 'NodeId'], `${label}.nodeId`),
    name: readString(record, ['name', 'Name'], `${label}.name`),
    completedAt:
      readNullableString(
        record,
        ['completedAt', 'CompletedAt'],
        `${label}.completedAt`,
      ) ?? null,
    durationMs:
      readOptionalNumber(record.durationMs ?? record.DurationMs) ?? null,
  };
}

function decodeStudioWorkflowBoardPendingNode(
  value: unknown,
  label = 'StudioWorkflowBoardPendingNode',
): StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['pendingNodes'][number] {
  const record = expectRecord(value, label);
  return {
    nodeId: readString(record, ['nodeId', 'NodeId'], `${label}.nodeId`),
    name: readString(record, ['name', 'Name'], `${label}.name`),
    status: readWorkflowBoardPendingNodeStatus(
      record.status ?? record.Status ?? 'unknown',
    ),
    reason:
      readNullableString(record, ['reason', 'Reason'], `${label}.reason`) ??
      null,
  };
}

function decodeStudioWorkflowBoardFailedNode(
  value: unknown,
  label = 'StudioWorkflowBoardFailedNode',
): StudioWorkflowBoardSnapshot['teams'][number]['members'][number]['failedNodes'][number] {
  const record = expectRecord(value, label);
  return {
    nodeId: readString(record, ['nodeId', 'NodeId'], `${label}.nodeId`),
    name: readString(record, ['name', 'Name'], `${label}.name`),
    failedAt:
      readNullableString(
        record,
        ['failedAt', 'FailedAt'],
        `${label}.failedAt`,
      ) ?? null,
  };
}

function decodeStudioWorkflowBoardMemberSnapshot(
  value: unknown,
  label = 'StudioWorkflowBoardMemberSnapshot',
): StudioWorkflowBoardSnapshot['teams'][number]['members'][number] {
  const record = expectRecord(value, label);
  const currentNode =
    record.currentNode == null && record.CurrentNode == null
      ? null
      : decodeStudioWorkflowBoardCurrentNode(
          record.currentNode ?? record.CurrentNode,
          `${label}.currentNode`,
        );
  return {
    actorId:
      readNullableString(record, ['actorId', 'ActorId'], `${label}.actorId`) ??
      null,
    completedNodes: expectArray(
      record.completedNodes ?? record.CompletedNodes ?? [],
      `${label}.completedNodes`,
      decodeStudioWorkflowBoardCompletedNode,
    ),
    currentExecutionId:
      readNullableString(
        record,
        ['currentExecutionId', 'CurrentExecutionId'],
        `${label}.currentExecutionId`,
      ) ?? null,
    currentNode,
    displayName: readString(
      record,
      ['displayName', 'DisplayName'],
      `${label}.displayName`,
    ),
    executionAvailability: readWorkflowBoardExecutionAvailability(
      record.executionAvailability ?? record.ExecutionAvailability ?? 'unknown',
    ),
    executionStatus: readWorkflowBoardExecutionStatus(
      record.executionStatus ?? record.ExecutionStatus ?? 'unknown',
    ),
    failedNodes: expectArray(
      record.failedNodes ?? record.FailedNodes ?? [],
      `${label}.failedNodes`,
      decodeStudioWorkflowBoardFailedNode,
    ),
    lastNodeUpdatedAt:
      readNullableString(
        record,
        ['lastNodeUpdatedAt', 'LastNodeUpdatedAt'],
        `${label}.lastNodeUpdatedAt`,
      ) ?? null,
    memberId: readString(record, ['memberId', 'MemberId'], `${label}.memberId`),
    pendingNodes: expectArray(
      record.pendingNodes ?? record.PendingNodes ?? [],
      `${label}.pendingNodes`,
      decodeStudioWorkflowBoardPendingNode,
    ),
    progress: decodeStudioWorkflowBoardProgress(
      record.progress ??
        record.Progress ?? {
          completedSteps: 0,
          totalSteps: 0,
        },
      `${label}.progress`,
    ),
    publishedServiceId:
      readNullableString(
        record,
        ['publishedServiceId', 'PublishedServiceId'],
        `${label}.publishedServiceId`,
      ) ?? null,
    roleSummary:
      readNullableString(
        record,
        ['roleSummary', 'RoleSummary'],
        `${label}.roleSummary`,
      ) ?? null,
    workflowId:
      readNullableString(
        record,
        ['workflowId', 'WorkflowId'],
        `${label}.workflowId`,
      ) ?? null,
    workflowName:
      readNullableString(
        record,
        ['workflowName', 'WorkflowName'],
        `${label}.workflowName`,
      ) ?? null,
  };
}

function decodeStudioWorkflowBoardTeamSnapshot(
  value: unknown,
  label = 'StudioWorkflowBoardTeamSnapshot',
): StudioWorkflowBoardSnapshot['teams'][number] {
  const record = expectRecord(value, label);
  const members = expectArray(
    record.members ?? record.Members ?? [],
    `${label}.members`,
    decodeStudioWorkflowBoardMemberSnapshot,
  );
  return {
    members,
    teamId: readString(record, ['teamId', 'TeamId'], `${label}.teamId`),
    teamName: readString(record, ['teamName', 'TeamName'], `${label}.teamName`),
    totalMemberCount:
      readOptionalNumber(record.totalMemberCount ?? record.TotalMemberCount) ??
      null,
  };
}

function decodeStudioWorkflowBoardSnapshot(
  value: unknown,
): StudioWorkflowBoardSnapshot {
  const record = expectRecord(value, 'StudioWorkflowBoardSnapshot');
  return {
    counts: decodeStudioWorkflowBoardCounts(
      record.counts ?? record.Counts ?? {},
      'StudioWorkflowBoardSnapshot.counts',
    ),
    generatedAt: readString(
      record,
      ['generatedAt', 'GeneratedAt'],
      'StudioWorkflowBoardSnapshot.generatedAt',
    ),
    lastNodeUpdatedAt:
      readNullableString(
        record,
        ['lastNodeUpdatedAt', 'LastNodeUpdatedAt'],
        'StudioWorkflowBoardSnapshot.lastNodeUpdatedAt',
      ) ?? null,
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioWorkflowBoardSnapshot.scopeId',
    ),
    teams: expectArray(
      record.teams ?? record.Teams ?? [],
      'StudioWorkflowBoardSnapshot.teams',
      decodeStudioWorkflowBoardTeamSnapshot,
    ),
    watermark:
      readNullableString(
        record,
        ['watermark', 'Watermark'],
        'StudioWorkflowBoardSnapshot.watermark',
      ) ?? null,
  };
}

function compactWorkflowBoardSnapshotRequest(
  request: StudioWorkflowBoardSnapshotRequest,
): Record<string, unknown> {
  return compactObject({
    memberId: trimOptional(request.memberId),
    take: request.take,
    teamId: trimOptional(request.teamId),
  });
}

function readScopeBindingImplementationKind(
  record: Record<string, unknown>,
  keys: string[],
  fallback?: string | number,
): StudioScopeBindingImplementationKind {
  const rawValue = readOptionalScalar(record, keys) ?? fallback;
  if (rawValue === undefined) {
    return 'unknown';
  }

  return normalizeStudioScopeBindingImplementationKind(
    normalizeEnumValue(rawValue, 'implementationKind', {
      '0': 'unknown',
      '1': 'workflow',
      '2': 'script',
      '3': 'gagent',
      workflow: 'workflow',
      scripting: 'script',
      script: 'script',
      gagent: 'gagent',
      unspecified: 'unknown',
    }),
  );
}

function decodeStudioScopeBindingResult(
  value: unknown,
): StudioScopeBindingResult {
  const record = expectRecord(value, 'StudioScopeBindingResult');
  const displayName =
    readOptionalString(record, ['displayName', 'DisplayName']) || '';
  const serviceId = readOptionalString(record, [
    'serviceId',
    'ServiceId',
    'publishedServiceId',
    'PublishedServiceId',
  ]);
  const workflowRecord =
    record.workflow && typeof record.workflow === 'object'
      ? expectRecord(record.workflow, 'StudioScopeBindingResult.workflow')
      : null;
  const scriptRecord =
    record.script && typeof record.script === 'object'
      ? expectRecord(record.script, 'StudioScopeBindingResult.script')
      : null;
  const gAgentRecord =
    (record.gAgent ?? record.gagent) &&
    typeof (record.gAgent ?? record.gagent) === 'object'
      ? expectRecord(
          record.gAgent ?? record.gagent,
          'StudioScopeBindingResult.gAgent',
        )
      : null;

  const workflowName =
    workflowRecord == null
      ? readOptionalString(record, ['workflowName', 'WorkflowName'])
      : readOptionalString(workflowRecord, ['workflowName', 'WorkflowName']) ||
        readOptionalString(record, ['workflowName', 'WorkflowName']);
  const definitionActorIdPrefix =
    workflowRecord == null
      ? readOptionalString(record, [
          'definitionActorIdPrefix',
          'DefinitionActorIdPrefix',
        ])
      : readOptionalString(workflowRecord, [
          'definitionActorIdPrefix',
          'DefinitionActorIdPrefix',
        ]) ||
        readOptionalString(record, [
          'definitionActorIdPrefix',
          'DefinitionActorIdPrefix',
        ]);
  const implementationKind = readScopeBindingImplementationKind(
    record,
    ['implementationKind', 'ImplementationKind'],
    scriptRecord
      ? 'script'
      : gAgentRecord
        ? 'gagent'
        : workflowRecord || workflowName
          ? 'workflow'
          : 'unknown',
  );
  const targetKind: StudioScopeBindingTargetKind = implementationKind;
  const targetName =
    (targetKind === 'workflow'
      ? workflowName
      : targetKind === 'script'
        ? readOptionalString(scriptRecord ?? {}, ['scriptId', 'ScriptId'])
        : targetKind === 'gagent'
          ? readOptionalString(gAgentRecord ?? {}, [
              'diagnosticClrTypeName',
              'DiagnosticClrTypeName',
            ])
          : undefined) ||
    displayName ||
    serviceId ||
    readString(
      record,
      ['revisionId', 'RevisionId'],
      'StudioScopeBindingResult.revisionId',
    );

  return {
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioScopeBindingResult.scopeId',
    ),
    serviceId,
    displayName,
    revisionId: readString(
      record,
      ['revisionId', 'RevisionId'],
      'StudioScopeBindingResult.revisionId',
    ),
    implementationKind,
    targetKind,
    targetName,
    workflowName,
    definitionActorIdPrefix,
    expectedActorId: readOptionalString(record, [
      'expectedActorId',
      'ExpectedActorId',
    ]),
    workflow:
      targetKind === 'workflow' && (workflowName || definitionActorIdPrefix)
        ? {
            workflowName: workflowName || displayName || targetName,
            definitionActorIdPrefix: definitionActorIdPrefix || '',
          }
        : null,
    script: scriptRecord
      ? {
          scriptId:
            readOptionalString(scriptRecord, ['scriptId', 'ScriptId']) || '',
          scriptRevision:
            readOptionalString(scriptRecord, [
              'scriptRevision',
              'ScriptRevision',
            ]) || '',
          definitionActorId:
            readOptionalString(scriptRecord, [
              'definitionActorId',
              'DefinitionActorId',
            ]) || '',
        }
      : null,
    gAgent: gAgentRecord
      ? {
          diagnosticClrTypeName:
            readOptionalString(gAgentRecord, [
              'diagnosticClrTypeName',
              'DiagnosticClrTypeName',
            ]) || '',
        }
      : null,
  };
}

function decodeStudioSaveAndBindWorkflowResult(
  value: unknown,
): StudioSaveAndBindWorkflowAcceptedResult {
  const record = expectRecord(value, 'StudioSaveAndBindWorkflowAcceptedResult');
  const workflowRecord =
    record.workflow == null && record.Workflow == null
      ? null
      : expectRecord(
          record.workflow ?? record.Workflow,
          'StudioSaveAndBindWorkflowAcceptedResult.workflow',
        );
  const binding =
    record.binding == null && record.Binding == null
      ? undefined
      : decodeStudioScopeBindingResult(record.binding ?? record.Binding);
  const scopeId = readString(
    record,
    ['scopeId', 'ScopeId'],
    'StudioSaveAndBindWorkflowAcceptedResult.scopeId',
  );
  const workflowId = readString(
    record,
    ['workflowId', 'WorkflowId'],
    'StudioSaveAndBindWorkflowAcceptedResult.workflowId',
  );
  const revisionId = readString(
    record,
    ['revisionId', 'RevisionId'],
    'StudioSaveAndBindWorkflowAcceptedResult.revisionId',
  );

  return {
    scopeId,
    workflowId,
    revisionId,
    workflow: workflowRecord
      ? {
          scopeId: readString(
            workflowRecord,
            ['scopeId', 'ScopeId'],
            'StudioSaveAndBindWorkflowAcceptedResult.workflow.scopeId',
          ),
          workflowId: readString(
            workflowRecord,
            ['workflowId', 'WorkflowId'],
            'StudioSaveAndBindWorkflowAcceptedResult.workflow.workflowId',
          ),
          serviceKey: readOptionalString(workflowRecord, [
            'serviceKey',
            'ServiceKey',
          ]),
          revisionId: readString(
            workflowRecord,
            ['revisionId', 'RevisionId'],
            'StudioSaveAndBindWorkflowAcceptedResult.workflow.revisionId',
          ),
          readModelUrl: readOptionalString(workflowRecord, [
            'readModelUrl',
            'ReadModelUrl',
          ]),
          acceptanceStage: readOptionalString(workflowRecord, [
            'acceptanceStage',
            'AcceptanceStage',
          ]),
          propagationStage: readOptionalString(workflowRecord, [
            'propagationStage',
            'PropagationStage',
          ]),
          displayName: readOptionalString(workflowRecord, [
            'displayName',
            'DisplayName',
          ]),
          workflowName: readOptionalString(workflowRecord, [
            'workflowName',
            'WorkflowName',
          ]),
        }
      : undefined,
    binding,
    acceptanceStage:
      readOptionalString(record, ['acceptanceStage', 'AcceptanceStage']) ||
      'accepted',
    propagationStage:
      readOptionalString(record, ['propagationStage', 'PropagationStage']) ||
      'readmodel_propagating',
  };
}

function decodeStudioPublishWorkflowResult(
  value: unknown,
): StudioPublishWorkflowAcceptedResult {
  const record = expectRecord(value, 'StudioPublishWorkflowAcceptedResult');
  return {
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioPublishWorkflowAcceptedResult.scopeId',
    ),
    workflowId: readString(
      record,
      ['workflowId', 'WorkflowId'],
      'StudioPublishWorkflowAcceptedResult.workflowId',
    ),
    serviceKey: readString(
      record,
      ['serviceKey', 'ServiceKey'],
      'StudioPublishWorkflowAcceptedResult.serviceKey',
    ),
    revisionId: readString(
      record,
      ['revisionId', 'RevisionId'],
      'StudioPublishWorkflowAcceptedResult.revisionId',
    ),
    acceptanceStage:
      readOptionalString(record, ['acceptanceStage', 'AcceptanceStage']) ||
      'accepted',
    propagationStage:
      readOptionalString(record, ['propagationStage', 'PropagationStage']) ||
      'readmodel_propagating',
  };
}

function decodeStudioScopeBindingStatus(
  value: unknown,
): StudioScopeBindingStatus {
  const record = expectRecord(value, 'StudioScopeBindingStatus');
  return {
    available: readBoolean(
      record,
      ['available', 'Available'],
      'StudioScopeBindingStatus.available',
    ),
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioScopeBindingStatus.scopeId',
    ),
    serviceId: readString(
      record,
      ['serviceId', 'ServiceId', 'publishedServiceId', 'PublishedServiceId'],
      'StudioScopeBindingStatus.serviceId',
    ),
    displayName: readString(
      record,
      ['displayName', 'DisplayName'],
      'StudioScopeBindingStatus.displayName',
    ),
    serviceKey: readString(
      record,
      [
        'serviceKey',
        'ServiceKey',
        'publishedServiceKey',
        'PublishedServiceKey',
      ],
      'StudioScopeBindingStatus.serviceKey',
    ),
    defaultServingRevisionId: readString(
      record,
      ['defaultServingRevisionId', 'DefaultServingRevisionId'],
      'StudioScopeBindingStatus.defaultServingRevisionId',
    ),
    activeServingRevisionId: readString(
      record,
      ['activeServingRevisionId', 'ActiveServingRevisionId'],
      'StudioScopeBindingStatus.activeServingRevisionId',
    ),
    deploymentId: readString(
      record,
      ['deploymentId', 'DeploymentId'],
      'StudioScopeBindingStatus.deploymentId',
    ),
    deploymentStatus: readString(
      record,
      ['deploymentStatus', 'DeploymentStatus'],
      'StudioScopeBindingStatus.deploymentStatus',
    ),
    primaryActorId: readString(
      record,
      ['primaryActorId', 'PrimaryActorId'],
      'StudioScopeBindingStatus.primaryActorId',
    ),
    updatedAt: readNullableString(
      record,
      ['updatedAt', 'UpdatedAt'],
      'StudioScopeBindingStatus.updatedAt',
    ),
    revisions: expectArray(
      record.revisions ?? record.Revisions,
      'StudioScopeBindingStatus.revisions',
      decodeStudioScopeBindingRevision,
    ),
  };
}

function readStudioMemberImplementationKind(
  record: Record<string, unknown>,
  keys: string | string[],
): StudioMemberImplementationKind {
  return normalizeStudioScopeBindingImplementationKind(
    readNullableString(record, keys, 'StudioMemberSummary.implementationKind'),
  );
}

function readStudioMemberLifecycle(
  record: Record<string, unknown>,
  keys: string | string[],
): StudioMemberLifecycleStage {
  return normalizeStudioMemberLifecycleStage(
    readNullableString(record, keys, 'StudioMemberSummary.lifecycleStage'),
  );
}

function readStudioTeamLifecycle(
  record: Record<string, unknown>,
  keys: string | string[],
): StudioTeamLifecycleStage {
  return normalizeStudioTeamLifecycleStage(
    readNullableString(record, keys, 'StudioTeamSummary.lifecycleStage'),
  );
}

function decodeStudioMemberSummary(value: unknown): StudioMemberSummary {
  const record = expectRecord(value, 'StudioMemberSummary');
  return {
    memberId: readString(
      record,
      ['memberId', 'MemberId'],
      'StudioMemberSummary.memberId',
    ),
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioMemberSummary.scopeId',
    ),
    displayName: readString(
      record,
      ['displayName', 'DisplayName'],
      'StudioMemberSummary.displayName',
    ),
    description:
      readNullableString(
        record,
        ['description', 'Description'],
        'StudioMemberSummary.description',
      ) ?? '',
    implementationKind: readStudioMemberImplementationKind(record, [
      'implementationKind',
      'ImplementationKind',
    ]),
    ...(record.implementationRef == null && record.ImplementationRef == null
      ? {}
      : {
          implementationRef: decodeStudioMemberImplementationRef(
            record.implementationRef ?? record.ImplementationRef,
          ),
        }),
    lifecycleStage: readStudioMemberLifecycle(record, [
      'lifecycleStage',
      'LifecycleStage',
    ]),
    publishedServiceId: readString(
      record,
      ['publishedServiceId', 'PublishedServiceId'],
      'StudioMemberSummary.publishedServiceId',
    ),
    lastBoundRevisionId:
      readNullableString(
        record,
        ['lastBoundRevisionId', 'LastBoundRevisionId'],
        'StudioMemberSummary.lastBoundRevisionId',
      ) ?? null,
    teamId:
      readNullableString(
        record,
        ['teamId', 'TeamId'],
        'StudioMemberSummary.teamId',
      ) ?? null,
    createdAt: readString(
      record,
      ['createdAt', 'CreatedAt'],
      'StudioMemberSummary.createdAt',
    ),
    updatedAt: readString(
      record,
      ['updatedAt', 'UpdatedAt'],
      'StudioMemberSummary.updatedAt',
    ),
  };
}

function decodeStudioTeamSummary(value: unknown): StudioTeamSummary {
  const record = expectRecord(value, 'StudioTeamSummary');
  return {
    teamId: readString(
      record,
      ['teamId', 'TeamId'],
      'StudioTeamSummary.teamId',
    ),
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioTeamSummary.scopeId',
    ),
    displayName: readString(
      record,
      ['displayName', 'DisplayName'],
      'StudioTeamSummary.displayName',
    ),
    description:
      readNullableString(
        record,
        ['description', 'Description'],
        'StudioTeamSummary.description',
      ) ?? '',
    entryMemberId:
      readNullableString(
        record,
        ['entryMemberId', 'EntryMemberId'],
        'StudioTeamSummary.entryMemberId',
      ) ?? null,
    lifecycleStage: readStudioTeamLifecycle(record, [
      'lifecycleStage',
      'LifecycleStage',
    ]),
    memberCount: readNumber(
      record,
      ['memberCount', 'MemberCount'],
      'StudioTeamSummary.memberCount',
    ),
    createdAt: readString(
      record,
      ['createdAt', 'CreatedAt'],
      'StudioTeamSummary.createdAt',
    ),
    updatedAt: readString(
      record,
      ['updatedAt', 'UpdatedAt'],
      'StudioTeamSummary.updatedAt',
    ),
  };
}

function decodeStudioTeamCommandResponse(
  value: unknown,
): StudioTeamCommandResponse {
  const record = expectRecord(value, 'StudioTeamCommandResponse');
  const status = readOptionalScalar(record, ['status', 'Status']);
  if (status === undefined) {
    throw new Error('StudioTeamCommandResponse.status is required.');
  }

  return {
    status: normalizeStudioTeamCommandStatus(status),
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioTeamCommandResponse.scopeId',
    ),
    teamId: readString(
      record,
      ['teamId', 'TeamId'],
      'StudioTeamCommandResponse.teamId',
    ),
    commandId:
      readNullableString(
        record,
        ['commandId', 'CommandId'],
        'StudioTeamCommandResponse.commandId',
      ) ?? null,
    correlationId:
      readNullableString(
        record,
        ['correlationId', 'CorrelationId'],
        'StudioTeamCommandResponse.correlationId',
      ) ?? null,
    ackedAt:
      readNullableString(
        record,
        ['ackedAt', 'AckedAt'],
        'StudioTeamCommandResponse.ackedAt',
      ) ?? null,
  };
}

function synthesizeStudioTeamCommandResponseFromSummary(
  summary: StudioTeamSummary,
): StudioTeamCommandResponse {
  return {
    status: 'accepted',
    scopeId: summary.scopeId,
    teamId: summary.teamId,
    commandId: null,
    correlationId: null,
    ackedAt: null,
  };
}

function decodeCompatibleStudioTeamCommandResponse(
  value: unknown,
): StudioTeamCommandResponse {
  try {
    return decodeStudioTeamCommandResponse(value);
  } catch (commandResponseError) {
    try {
      return synthesizeStudioTeamCommandResponseFromSummary(
        decodeStudioTeamSummary(value),
      );
    } catch {
      throw commandResponseError;
    }
  }
}

function decodeStudioTeamRoster(value: unknown): StudioTeamRoster {
  const record = expectRecord(value, 'StudioTeamRoster');
  return {
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioTeamRoster.scopeId',
    ),
    teams: expectArray(
      record.teams ?? record.Teams,
      'StudioTeamRoster.teams',
      decodeStudioTeamSummary,
    ),
    nextPageToken:
      readNullableString(
        record,
        ['nextPageToken', 'NextPageToken'],
        'StudioTeamRoster.nextPageToken',
      ) ?? null,
  };
}

function decodeStudioMemberRoster(value: unknown): StudioMemberRoster {
  const record = expectRecord(value, 'StudioMemberRoster');
  return {
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioMemberRoster.scopeId',
    ),
    members: expectArray(
      record.members ?? record.Members,
      'StudioMemberRoster.members',
      decodeStudioMemberSummary,
    ),
    nextPageToken:
      readNullableString(
        record,
        ['nextPageToken', 'NextPageToken'],
        'StudioMemberRoster.nextPageToken',
      ) ?? null,
  };
}

function decodeStudioMemberImplementationRef(
  value: unknown,
): StudioMemberImplementationRef {
  const record = expectRecord(value, 'StudioMemberImplementationRef');
  return {
    implementationKind: readStudioMemberImplementationKind(record, [
      'implementationKind',
      'ImplementationKind',
    ]),
    workflowId:
      readNullableString(
        record,
        ['workflowId', 'WorkflowId'],
        'StudioMemberImplementationRef.workflowId',
      ) ?? null,
    workflowRevision:
      readNullableString(
        record,
        ['workflowRevision', 'WorkflowRevision'],
        'StudioMemberImplementationRef.workflowRevision',
      ) ?? null,
    scriptId:
      readNullableString(
        record,
        ['scriptId', 'ScriptId'],
        'StudioMemberImplementationRef.scriptId',
      ) ?? null,
    scriptRevision:
      readNullableString(
        record,
        ['scriptRevision', 'ScriptRevision'],
        'StudioMemberImplementationRef.scriptRevision',
      ) ?? null,
    agentKind:
      readNullableString(
        record,
        ['agentKind', 'AgentKind'],
        'StudioMemberImplementationRef.agentKind',
      ) ?? null,
    diagnosticActorTypeName:
      readNullableString(
        record,
        ['diagnosticActorTypeName', 'DiagnosticActorTypeName'],
        'StudioMemberImplementationRef.diagnosticActorTypeName',
      ) ?? null,
  };
}

function decodeStudioMemberBindingContract(
  value: unknown,
): StudioMemberBindingContract {
  const record = expectRecord(value, 'StudioMemberBindingContract');
  return {
    publishedServiceId: readString(
      record,
      ['publishedServiceId', 'PublishedServiceId'],
      'StudioMemberBindingContract.publishedServiceId',
    ),
    revisionId: readString(
      record,
      ['revisionId', 'RevisionId'],
      'StudioMemberBindingContract.revisionId',
    ),
    implementationKind: readStudioMemberImplementationKind(record, [
      'implementationKind',
      'ImplementationKind',
    ]),
    boundAt: readString(
      record,
      ['boundAt', 'BoundAt'],
      'StudioMemberBindingContract.boundAt',
    ),
  };
}

function decodeStudioMemberBindingRunResult(
  value: unknown,
): StudioMemberBindingRunResult {
  const record = expectRecord(value, 'StudioMemberBindingRunResult');
  return {
    publishedServiceId: readString(
      record,
      ['publishedServiceId', 'PublishedServiceId'],
      'StudioMemberBindingRunResult.publishedServiceId',
    ),
    revisionId: readString(
      record,
      ['revisionId', 'RevisionId'],
      'StudioMemberBindingRunResult.revisionId',
    ),
    implementationKind: readStudioMemberImplementationKind(record, [
      'implementationKind',
      'ImplementationKind',
    ]),
    expectedActorId:
      readNullableString(
        record,
        ['expectedActorId', 'ExpectedActorId'],
        'StudioMemberBindingRunResult.expectedActorId',
      ) ?? null,
  };
}

function normalizeStudioMemberBindingRunStatus(
  value: string | number | null | undefined,
): StudioMemberBindingRunStatus {
  if (value == null) {
    return 'unknown';
  }

  return normalizeEnumValue(value, 'status', {
    '0': 'unknown',
    '1': 'accepted',
    '2': 'admission_pending',
    '3': 'admitted',
    '4': 'platform_binding_pending',
    '5': 'succeeded',
    '6': 'failed',
    '7': 'rejected',
    '8': 'member_notification_pending',
    accepted: 'accepted',
    admission_pending: 'admission_pending',
    admissionpending: 'admission_pending',
    admitted: 'admitted',
    platform_binding_pending: 'platform_binding_pending',
    platformbindingpending: 'platform_binding_pending',
    platform_pending: 'platform_binding_pending',
    platformpending: 'platform_binding_pending',
    member_notification_pending: 'member_notification_pending',
    membernotificationpending: 'member_notification_pending',
    notification_pending: 'member_notification_pending',
    notificationpending: 'member_notification_pending',
    succeeded: 'succeeded',
    completed: 'succeeded',
    failed: 'failed',
    rejected: 'rejected',
    unspecified: 'unknown',
    unknown: 'unknown',
  }) as StudioMemberBindingRunStatus;
}

function normalizeStudioMemberBindingAckStage(
  value: string | number | null | undefined,
): StudioMemberBindingAckStage {
  if (value == null) {
    return 'unknown';
  }

  const normalized = normalizeEnumValue(value, 'ackStage', {
    '0': 'unknown',
    '1': 'dispatch_accepted',
    dispatch_accepted: 'dispatch_accepted',
    dispatchaccepted: 'dispatch_accepted',
    unknown: 'unknown',
  });

  return normalized === 'dispatch_accepted' ? normalized : 'unknown';
}

function normalizeStudioMemberBindingRunRole(
  value: string | number | null | undefined,
): StudioMemberBindingRunRole {
  if (value == null) {
    return 'unknown';
  }

  const normalized = normalizeEnumValue(value, 'bindingRunRole', {
    '0': 'unknown',
    '1': 'candidate',
    candidate: 'candidate',
    unknown: 'unknown',
  });

  return normalized === 'candidate' ? normalized : 'unknown';
}

function normalizeStudioMemberCommandStatus(
  value: string | number | null | undefined,
): StudioMemberCommandStatus {
  if (value == null) {
    return 'unknown';
  }

  const normalized = normalizeEnumValue(value, 'status', {
    '0': 'unknown',
    '1': 'accepted',
    '2': 'no_change',
    '3': 'delete_accepted',
    accepted: 'accepted',
    delete_accepted: 'delete_accepted',
    deleteaccepted: 'delete_accepted',
    no_change: 'no_change',
    nochange: 'no_change',
    unchanged: 'no_change',
    unknown: 'unknown',
  });

  return normalized === 'accepted' ||
    normalized === 'delete_accepted' ||
    normalized === 'no_change'
    ? normalized
    : 'unknown';
}

function normalizeCommandReceiptStatus(
  value: string | number | null | undefined,
): StudioTeamCommandStatus {
  if (value == null) {
    return 'unknown';
  }

  const normalized = normalizeEnumValue(value, 'status', {
    '0': 'unknown',
    '1': 'accepted',
    '2': 'no_change',
    accepted: 'accepted',
    no_change: 'no_change',
    nochange: 'no_change',
    unchanged: 'no_change',
    unknown: 'unknown',
  });

  return normalized === 'accepted' || normalized === 'no_change'
    ? normalized
    : 'unknown';
}

function normalizeStudioTeamCommandStatus(
  value: string | number | null | undefined,
): StudioTeamCommandStatus {
  return normalizeCommandReceiptStatus(value);
}

function decodeStudioMemberBindingFailure(
  value: unknown,
): StudioMemberBindingFailure {
  const record = expectRecord(value, 'StudioMemberBindingFailure');
  return {
    code: readString(
      record,
      ['code', 'Code'],
      'StudioMemberBindingFailure.code',
    ),
    message:
      readNullableString(
        record,
        ['message', 'Message'],
        'StudioMemberBindingFailure.message',
      ) ?? null,
    failedAt:
      readNullableString(
        record,
        ['failedAt', 'FailedAt', 'failedAtUtc', 'FailedAtUtc'],
        'StudioMemberBindingFailure.failedAt',
      ) ?? null,
  };
}

function decodeStudioMemberBindingRunStatusResponse(
  value: unknown,
): StudioMemberBindingRunStatusResponse {
  const record = expectRecord(value, 'StudioMemberBindingRunStatusResponse');
  const result =
    record.result == null && record.Result == null
      ? undefined
      : decodeStudioMemberBindingRunResult(record.result ?? record.Result);
  return {
    status: normalizeStudioMemberBindingRunStatus(
      readOptionalScalar(record, ['status', 'Status']),
    ),
    bindingRunId: readString(
      record,
      ['bindingRunId', 'BindingRunId'],
      'StudioMemberBindingRunStatusResponse.bindingRunId',
    ),
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioMemberBindingRunStatusResponse.scopeId',
    ),
    memberId: readString(
      record,
      ['memberId', 'MemberId'],
      'StudioMemberBindingRunStatusResponse.memberId',
    ),
    stateVersion:
      readOptionalNumber(record.stateVersion ?? record.StateVersion) ?? null,
    platformBindingCommandId:
      readNullableString(
        record,
        ['platformBindingCommandId', 'PlatformBindingCommandId'],
        'StudioMemberBindingRunStatusResponse.platformBindingCommandId',
      ) ?? null,
    ...(result === undefined ? {} : { result }),
    failure:
      record.failure == null && record.Failure == null
        ? null
        : decodeStudioMemberBindingFailure(record.failure ?? record.Failure),
    updatedAt:
      readNullableString(
        record,
        ['updatedAt', 'UpdatedAt', 'updatedAtUtc', 'UpdatedAtUtc'],
        'StudioMemberBindingRunStatusResponse.updatedAt',
      ) ?? null,
  };
}

function decodeStudioMemberBindingAcceptedResponse(
  value: unknown,
): StudioMemberBindingAcceptedResponse {
  const record = expectRecord(value, 'StudioMemberBindingAcceptedResponse');
  return {
    status: normalizeStudioMemberBindingRunStatus(
      readOptionalScalar(record, ['status', 'Status']),
    ),
    bindingRunId: readString(
      record,
      ['bindingRunId', 'BindingRunId'],
      'StudioMemberBindingAcceptedResponse.bindingRunId',
    ),
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioMemberBindingAcceptedResponse.scopeId',
    ),
    memberId: readString(
      record,
      ['memberId', 'MemberId'],
      'StudioMemberBindingAcceptedResponse.memberId',
    ),
    ackStage: normalizeStudioMemberBindingAckStage(
      readOptionalScalar(record, ['ackStage', 'AckStage']),
    ),
    bindingRunRole: normalizeStudioMemberBindingRunRole(
      readOptionalScalar(record, ['bindingRunRole', 'BindingRunRole']),
    ),
  };
}

function decodeStudioMemberCommandResponse(
  value: unknown,
): StudioMemberCommandResponse {
  const record = expectRecord(value, 'StudioMemberCommandResponse');
  return {
    status: normalizeStudioMemberCommandStatus(
      readOptionalScalar(record, ['status', 'Status']),
    ),
    scopeId: readString(
      record,
      ['scopeId', 'ScopeId'],
      'StudioMemberCommandResponse.scopeId',
    ),
    memberId: readString(
      record,
      ['memberId', 'MemberId'],
      'StudioMemberCommandResponse.memberId',
    ),
    ackedAt:
      readNullableString(
        record,
        ['ackedAt', 'AckedAt'],
        'StudioMemberCommandResponse.ackedAt',
      ) ?? null,
  };
}

function decodeStudioMemberBindingViewResponse(
  value: unknown,
): StudioMemberBindingViewResponse {
  const record = expectRecord(value, 'StudioMemberBindingViewResponse');
  const currentBindingRun =
    record.currentBindingRun == null && record.CurrentBindingRun == null
      ? undefined
      : decodeStudioMemberBindingRunStatusResponse(
          record.currentBindingRun ?? record.CurrentBindingRun,
        );
  return {
    lastBinding:
      record.lastBinding == null && record.LastBinding == null
        ? null
        : decodeStudioMemberBindingContract(
            record.lastBinding ?? record.LastBinding,
          ),
    ...(currentBindingRun === undefined ? {} : { currentBindingRun }),
  };
}

function decodeStudioMemberDetail(value: unknown): StudioMemberDetail {
  const record = expectRecord(value, 'StudioMemberDetail');
  const currentBindingRun =
    record.currentBindingRun == null && record.CurrentBindingRun == null
      ? undefined
      : decodeStudioMemberBindingRunStatusResponse(
          record.currentBindingRun ?? record.CurrentBindingRun,
        );
  return {
    summary: decodeStudioMemberSummary(
      expectRecord(
        record.summary ?? record.Summary,
        'StudioMemberDetail.summary',
      ),
    ),
    implementationRef:
      record.implementationRef == null && record.ImplementationRef == null
        ? null
        : decodeStudioMemberImplementationRef(
            record.implementationRef ?? record.ImplementationRef,
          ),
    lastBinding:
      record.lastBinding == null && record.LastBinding == null
        ? null
        : decodeStudioMemberBindingContract(
            record.lastBinding ?? record.LastBinding,
          ),
    ...(currentBindingRun === undefined ? {} : { currentBindingRun }),
  };
}

function synthesizeStudioMemberCommandResponseFromDetail(
  detail: StudioMemberDetail,
): StudioMemberCommandResponse {
  return {
    status: 'accepted',
    scopeId: detail.summary.scopeId,
    memberId: detail.summary.memberId,
    ackedAt: null,
  };
}

function decodeCompatibleStudioMemberPatchResponse(
  value: unknown,
): StudioMemberCommandResponse {
  try {
    return decodeStudioMemberCommandResponse(value);
  } catch (commandResponseError) {
    try {
      return synthesizeStudioMemberCommandResponseFromDetail(
        decodeStudioMemberDetail(value),
      );
    } catch {
      throw commandResponseError;
    }
  }
}

export const studioApi = {
  getAppContext(): Promise<StudioAppContext> {
    return requestJson('/api/studio/context');
  },

  getAuthSession(): Promise<StudioAuthSession> {
    return requestJson('/api/auth/me');
  },

  getWorkspaceSettings(
    scopeId?: string | null,
  ): Promise<StudioWorkspaceSettings> {
    return requestJson(withOptionalScopeId('/api/workspace/', scopeId));
  },

  getWorkflowBoardSnapshot(
    scopeId: string,
    request: StudioWorkflowBoardSnapshotRequest,
  ): Promise<StudioWorkflowBoardSnapshot> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/workflow-board/snapshot`,
      decodeStudioWorkflowBoardSnapshot,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify(compactWorkflowBoardSnapshotRequest(request)),
      },
    );
  },

  listTeams(scopeId: string): Promise<StudioTeamRoster> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/teams`,
      decodeStudioTeamRoster,
    );
  },

  getTeam(scopeId: string, teamId: string): Promise<StudioTeamSummary> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/teams/${encodeURIComponent(teamId.trim())}`,
      decodeStudioTeamSummary,
    );
  },

  createTeam(input: StudioTeamCreateInput): Promise<StudioTeamSummary> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/teams`,
      decodeStudioTeamSummary,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            displayName: input.displayName.trim(),
            description: trimOptional(input.description),
            teamId: trimOptional(input.teamId),
          }),
        ),
      },
    );
  },

  updateTeam(
    input: StudioTeamUpdateInput,
  ): Promise<StudioTeamCommandResponse | undefined> {
    return requestDecodedJsonOrAccepted(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/teams/${encodeURIComponent(input.teamId.trim())}`,
      decodeCompatibleStudioTeamCommandResponse,
      {
        method: 'PATCH',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            displayName:
              input.displayName === null
                ? null
                : trimOptional(input.displayName),
            description:
              input.description === null
                ? null
                : trimOptional(input.description),
          }),
        ),
      },
    );
  },

  archiveTeam(
    scopeId: string,
    teamId: string,
  ): Promise<StudioTeamCommandResponse | undefined> {
    return requestDecodedJsonOrAccepted(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/teams/${encodeURIComponent(teamId.trim())}/archive`,
      decodeCompatibleStudioTeamCommandResponse,
      {
        method: 'POST',
        headers: JSON_HEADERS,
      },
    );
  },

  setTeamEntryMember(
    scopeId: string,
    teamId: string,
    memberId: string,
  ): Promise<StudioTeamSummary | undefined> {
    return requestDecodedJsonOrAccepted(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/teams/${encodeURIComponent(teamId.trim())}/entry-member`,
      decodeStudioTeamSummary,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          memberId: memberId.trim(),
        }),
      },
    );
  },

  clearTeamEntryMember(
    scopeId: string,
    teamId: string,
  ): Promise<StudioTeamSummary | undefined> {
    return requestDecodedJsonOrAccepted(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/teams/${encodeURIComponent(teamId.trim())}/entry-member`,
      decodeStudioTeamSummary,
      {
        method: 'DELETE',
        headers: JSON_HEADERS,
      },
    );
  },

  listTeamMembers(
    scopeId: string,
    teamId: string,
  ): Promise<StudioMemberRoster> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/teams/${encodeURIComponent(teamId.trim())}/members`,
      decodeStudioMemberRoster,
    );
  },

  listMembers(scopeId: string): Promise<StudioMemberRoster> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/members`,
      decodeStudioMemberRoster,
    );
  },

  getMember(scopeId: string, memberId: string): Promise<StudioMemberDetail> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/members/${encodeURIComponent(memberId.trim())}`,
      decodeStudioMemberDetail,
    );
  },

  createMember(input: {
    scopeId: string;
    displayName: string;
    implementationKind: StudioMemberImplementationKind;
    description?: string | null;
    teamId?: string | null;
  }): Promise<StudioMemberSummary> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members`,
      decodeStudioMemberSummary,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            displayName: input.displayName.trim(),
            implementationKind: input.implementationKind,
            description: trimOptional(input.description),
            teamId: trimOptional(input.teamId),
          }),
        ),
      },
    );
  },

  createMemberWithId(input: {
    scopeId: string;
    memberId: string;
    displayName: string;
    implementationKind: StudioMemberImplementationKind;
    description?: string | null;
    teamId?: string | null;
  }): Promise<StudioMemberSummary> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members`,
      decodeStudioMemberSummary,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            displayName: input.displayName.trim(),
            implementationKind: input.implementationKind,
            description: trimOptional(input.description),
            memberId: input.memberId.trim(),
            teamId: trimOptional(input.teamId),
          }),
        ),
      },
    );
  },

  updateMemberTeamAssignment(input: {
    scopeId: string;
    memberId: string;
    teamId: string | null;
  }): Promise<StudioMemberCommandResponse> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}`,
      decodeCompatibleStudioMemberPatchResponse,
      {
        method: 'PATCH',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          teamId: input.teamId === null ? null : input.teamId.trim(),
        }),
      },
    );
  },

  updateMemberDisplayName(input: {
    scopeId: string;
    memberId: string;
    displayName: string;
  }): Promise<StudioMemberCommandResponse> {
    const displayName = input.displayName.trim();
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}`,
      decodeCompatibleStudioMemberPatchResponse,
      {
        method: 'PATCH',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          displayName,
        }),
      },
    );
  },

  updateMemberImplementationRef(input: {
    scopeId: string;
    memberId: string;
    implementationRef: StudioMemberImplementationRef;
  }): Promise<StudioMemberCommandResponse> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}`,
      decodeCompatibleStudioMemberPatchResponse,
      {
        method: 'PATCH',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          implementationRef: compactObject({
            implementationKind: input.implementationRef.implementationKind,
            workflowId: trimOptional(input.implementationRef.workflowId),
            workflowRevision: trimOptional(
              input.implementationRef.workflowRevision,
            ),
            scriptId: trimOptional(input.implementationRef.scriptId),
            scriptRevision: trimOptional(
              input.implementationRef.scriptRevision,
            ),
            agentKind: trimOptional(input.implementationRef.agentKind),
          }),
        }),
      },
    );
  },

  deleteMember(input: {
    scopeId: string;
    memberId: string;
  }): Promise<StudioMemberCommandResponse> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}`,
      decodeStudioMemberCommandResponse,
      {
        method: 'DELETE',
        headers: JSON_HEADERS,
      },
    );
  },

  listWorkflowDrafts(
    scopeId?: string | null,
  ): Promise<StudioWorkflowDraftSummary[]> {
    return requestJson(
      withOptionalScopeId('/api/workspace/workflow-drafts', scopeId),
    );
  },

  getTemplateWorkflow(
    workflowName: string,
  ): Promise<WorkflowCatalogItemDetail> {
    return requestDecodedJson(
      `/api/workflows/${encodeURIComponent(workflowName)}`,
      decodeWorkflowCatalogItemDetailResponse,
    );
  },

  getWorkflowDraft(
    workflowId: string,
    scopeId?: string | null,
  ): Promise<StudioWorkflowDraft> {
    return requestJson(
      withOptionalScopeId(
        `/api/workspace/workflow-drafts/${encodeURIComponent(workflowId)}`,
        scopeId,
      ),
    );
  },

  async getWorkflowDraftFile(
    workflowId: string,
    scopeId?: string | null,
  ): Promise<StudioWorkflowFile> {
    const draft = await this.getWorkflowDraft(workflowId, scopeId);
    return toWorkflowFile(draft, true);
  },

  createWorkflowDraft(
    input: Omit<StudioSaveWorkflowInput, 'workflowId'>,
  ): Promise<StudioWorkflowSaveResult> {
    return studioHostFetch(
      withOptionalScopeId('/api/workspace/workflow-drafts', input.scopeId),
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            directoryId: input.directoryId,
            workflowName: input.workflowName.trim(),
            fileName: trimOptional(input.fileName),
            yaml: input.yaml,
            layout: input.layout,
          }),
        ),
      },
    ).then(async (response) => {
      if (!response.ok) {
        throw await createStudioApiError(response);
      }

      const payload = await response.json();
      if (response.status === 202) {
        return {
          kind: 'accepted',
          receipt: decodeStudioWorkflowDraftCreateAcceptedReceipt(payload),
        };
      }

      return {
        kind: 'materialized',
        workflow: toWorkflowFile(decodeStudioWorkflowDraft(payload), true),
      };
    });
  },

  instantiateWorkflowTemplate(input: {
    scopeId: string;
    templateId: string;
    expectedAuthorityStateVersion: number;
  }): Promise<StudioWorkflowDraftCreateAcceptedReceipt> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/workflow-templates/${encodeURIComponent(input.templateId.trim())}:instantiate`,
      decodeStudioWorkflowDraftCreateAcceptedReceipt,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          expectedAuthorityStateVersion: input.expectedAuthorityStateVersion,
        }),
      },
    );
  },

  updateWorkflowDraft(
    input: StudioSaveWorkflowInput & { workflowId: string },
  ): Promise<StudioWorkflowDraft> {
    return requestJson(
      withOptionalScopeId(
        `/api/workspace/workflow-drafts/${encodeURIComponent(input.workflowId)}`,
        input.scopeId,
      ),
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            directoryId: input.directoryId,
            workflowName: input.workflowName.trim(),
            fileName: trimOptional(input.fileName),
            yaml: input.yaml,
            layout: input.layout,
          }),
        ),
      },
    );
  },

  deleteWorkflowDraft(
    workflowId: string,
    scopeId?: string | null,
  ): Promise<void> {
    return requestJson(
      withOptionalScopeId(
        `/api/workspace/workflow-drafts/${encodeURIComponent(workflowId)}`,
        scopeId,
      ),
      {
        method: 'DELETE',
      },
    );
  },

  listWorkflows(scopeId?: string | null): Promise<StudioWorkflowSummary[]> {
    const normalizedScopeId = trimOptional(scopeId);
    if (!normalizedScopeId) {
      return this.listWorkflowDrafts(scopeId);
    }

    return Promise.all([
      this.listWorkflowDrafts(normalizedScopeId),
      scopesApi.listWorkflows(normalizedScopeId),
    ]).then(([drafts, committed]) => {
      const merged = new Map<string, StudioWorkflowSummary>();

      for (const workflow of committed) {
        merged.set(
          workflow.workflowId,
          toCommittedWorkflowSummary(normalizedScopeId, workflow),
        );
      }

      for (const draft of drafts) {
        const existing = merged.get(draft.workflowId);
        merged.set(
          draft.workflowId,
          existing
            ? {
                ...draft,
                activeRevisionId: existing.activeRevisionId ?? null,
                serviceKey: existing.serviceKey ?? null,
                updatedAtUtc: selectLatestTimestamp(
                  draft.updatedAtUtc,
                  existing.updatedAtUtc,
                ),
              }
            : draft,
        );
      }

      return Array.from(merged.values()).sort(
        (left, right) =>
          Date.parse(right.updatedAtUtc) - Date.parse(left.updatedAtUtc),
      );
    });
  },

  async getWorkflow(
    workflowId: string,
    scopeId?: string | null,
  ): Promise<StudioWorkflowFile> {
    const normalizedScopeId = trimOptional(scopeId);
    if (!normalizedScopeId) {
      const draft = await this.getWorkflowDraft(workflowId, scopeId);
      return toWorkflowFile(draft, true);
    }

    const draft = await requestJsonOrNull<StudioWorkflowDraft>(
      withOptionalScopeId(
        `/api/workspace/workflow-drafts/${encodeURIComponent(workflowId)}`,
        normalizedScopeId,
      ),
    );
    if (draft) {
      return toWorkflowFile(draft, true);
    }

    const committed = (
      await scopesApi.listWorkflowDetails(normalizedScopeId)
    ).find((detail) => detail.workflow?.workflowId === workflowId);
    if (!committed) {
      throw new Error('Not Found');
    }

    return toCommittedWorkflowFile(normalizedScopeId, committed);
  },

  async getPublishedWorkflow(
    workflowId: string,
    scopeId: string,
  ): Promise<StudioWorkflowFile> {
    return toCommittedWorkflowFile(
      scopeId.trim(),
      await scopesApi.getWorkflowDetail(scopeId.trim(), workflowId),
    );
  },

  listWorkflowCapabilities(
    scopeId: string,
  ): Promise<StudioWorkflowCapabilityList> {
    return requestDecodedJson(
      '/api/scopes/' +
        encodeURIComponent(scopeId.trim()) +
        '/workflow-capabilities',
      decodeStudioWorkflowCapabilityList,
    );
  },

  inspectWorkflowCapabilityReadiness(
    input: StudioWorkflowCapabilityReadinessInput,
  ): Promise<StudioWorkflowCapabilityReadiness> {
    return requestDecodedJson(
      '/api/scopes/' +
        encodeURIComponent(input.scopeId.trim()) +
        '/workflow-capabilities:readiness',
      (value) =>
        decodeStudioWorkflowCapabilityReadiness(
          value,
          input.selector,
          input.executionMode,
        ),
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          selector: input.selector,
          executionMode: input.executionMode,
        }),
      },
    );
  },

  previewExplicitRequests(
    input: StudioExplicitRequestPreviewInput,
  ): Promise<StudioExplicitRequestPreview> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/workflows:explicit-request-preview`,
      decodeStudioExplicitRequestPreview,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            workflowYaml: input.workflowYaml,
            executionMode: input.executionMode,
            inlineWorkflowYamls:
              input.inlineWorkflowYamls &&
              Object.keys(input.inlineWorkflowYamls).length > 0
                ? input.inlineWorkflowYamls
                : undefined,
            workflowId: input.workflowId.trim(),
            revisionId: trimOptional(input.revisionId),
          }),
        ),
      },
    );
  },

  async saveWorkflow(
    input: StudioSaveWorkflowInput,
  ): Promise<StudioWorkflowSaveResult> {
    const normalizedWorkflowId = trimOptional(input.workflowId);
    const shouldUpdate =
      Boolean(normalizedWorkflowId) &&
      (input.draftExists ?? Boolean(normalizedWorkflowId));
    if (shouldUpdate && normalizedWorkflowId) {
      const draft = await this.updateWorkflowDraft({
        ...input,
        workflowId: normalizedWorkflowId,
      });
      return {
        kind: 'materialized',
        workflow: toWorkflowFile(draft, true),
      };
    }

    return this.createWorkflowDraft({
      scopeId: input.scopeId,
      directoryId: input.directoryId,
      workflowName: input.workflowName,
      fileName: input.fileName,
      yaml: input.yaml,
      layout: input.layout,
    });
  },

  saveAndBindWorkflow(
    input: StudioSaveAndBindWorkflowInput,
  ): Promise<StudioSaveAndBindWorkflowAcceptedResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/workflows:save-and-bind`,
      decodeStudioSaveAndBindWorkflowResult,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            workflowId: trimOptional(input.workflowId),
            revisionId: input.revisionId.trim(),
            workflowYaml: input.workflowYaml,
            workflowName: trimOptional(input.workflowName),
            displayName: trimOptional(input.displayName),
            inlineWorkflowYamls:
              input.inlineWorkflowYamls &&
              Object.keys(input.inlineWorkflowYamls).length > 0
                ? input.inlineWorkflowYamls
                : undefined,
            appId: trimOptional(input.appId),
            serviceId: trimOptional(input.serviceId),
            exposureDesired: input.exposureDesired ?? undefined,
            explicitRequestConfirmations:
              input.explicitRequestConfirmations &&
              input.explicitRequestConfirmations.length > 0
                ? input.explicitRequestConfirmations.map((confirmation) => ({
                    workflowId: confirmation.workflowId,
                    revisionId: confirmation.revisionId,
                    callSiteId: confirmation.callSiteId,
                    requestContractDigest: confirmation.requestContractDigest,
                    attestedRisk: confirmation.attestedRisk,
                  }))
                : undefined,
          }),
        ),
      },
    );
  },

  publishWorkflow(
    input: StudioPublishWorkflowInput,
  ): Promise<StudioPublishWorkflowAcceptedResult> {
    const scopeId = input.scopeId.trim();
    const workflowId = input.workflowId.trim();
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/workflows/${encodeURIComponent(workflowId)}`,
      decodeStudioPublishWorkflowResult,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            revisionId: input.revisionId.trim(),
            workflowYaml: input.workflowYaml,
            workflowName: trimOptional(input.workflowName),
            displayName: trimOptional(input.displayName),
            inlineWorkflowYamls:
              input.inlineWorkflowYamls &&
              Object.keys(input.inlineWorkflowYamls).length > 0
                ? input.inlineWorkflowYamls
                : undefined,
            explicitRequestConfirmations:
              input.explicitRequestConfirmations &&
              input.explicitRequestConfirmations.length > 0
                ? input.explicitRequestConfirmations.map((confirmation) => ({
                    workflowId: confirmation.workflowId,
                    revisionId: confirmation.revisionId,
                    callSiteId: confirmation.callSiteId,
                    requestContractDigest: confirmation.requestContractDigest,
                    attestedRisk: confirmation.attestedRisk,
                  }))
                : undefined,
          }),
        ),
      },
    );
  },

  deleteWorkflow(workflowId: string, scopeId?: string | null): Promise<void> {
    return this.deleteWorkflowDraft(workflowId, scopeId);
  },

  parseYaml(input: {
    yaml: string;
    availableWorkflowNames?: string[];
    availableStepTypes?: string[];
  }): Promise<StudioParseYamlResult> {
    return requestJson('/api/editor/parse-yaml', {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify({
        yaml: input.yaml,
        availableWorkflowNames: input.availableWorkflowNames,
        availableStepTypes: input.availableStepTypes,
      }),
    });
  },

  serializeYaml(input: {
    document: StudioWorkflowDocument;
    availableWorkflowNames?: string[];
    availableStepTypes?: string[];
  }): Promise<StudioSerializeYamlResult> {
    return requestJson('/api/editor/serialize-yaml', {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify({
        document: input.document,
        availableWorkflowNames: input.availableWorkflowNames,
        availableStepTypes: input.availableStepTypes,
      }),
    });
  },

  listExecutions(): Promise<StudioExecutionSummary[]> {
    return requestJson('/api/executions/');
  },

  getExecution(executionId: string): Promise<StudioExecutionDetail> {
    return requestJson(`/api/executions/${encodeURIComponent(executionId)}`);
  },

  startExecution(
    input: StudioStartExecutionInput,
  ): Promise<StudioExecutionDetail> {
    return requestJson('/api/executions/', {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify(
        compactObject({
          workflowName: input.workflowName.trim(),
          prompt: input.prompt.trim(),
          workflowYamls: input.workflowYamls,
          runtimeBaseUrl: trimOptional(input.runtimeBaseUrl),
          scopeId: trimOptional(input.scopeId),
          workflowId: trimOptional(input.workflowId),
          eventFormat: trimOptional(input.eventFormat),
        }),
      ),
    });
  },

  bindScopeWorkflow(input: {
    scopeId: string;
    displayName?: string | null;
    workflowYamls: string[];
    revisionId?: string | null;
  }): Promise<StudioScopeBindingResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/binding`,
      decodeStudioScopeBindingResult,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: 'workflow',
            displayName: trimOptional(input.displayName),
            workflowYamls:
              input.workflowYamls.length > 0 ? input.workflowYamls : undefined,
            revisionId: trimOptional(input.revisionId),
          }),
        ),
      },
    );
  },

  bindScopeScript(
    input: StudioScopeScriptBindingInput,
  ): Promise<StudioScopeScriptBindingResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/binding`,
      decodeStudioScopeBindingResult,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: 'script',
            displayName: trimOptional(input.displayName),
            serviceId: trimOptional(input.serviceId),
            script: compactObject({
              scriptId: input.scriptId.trim(),
              scriptRevision: input.scriptRevision.trim(),
            }),
          }),
        ),
      },
    );
  },

  bindScopeGAgent(
    input: StudioScopeGAgentBindingInput,
  ): Promise<StudioScopeGAgentBindingResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/binding`,
      decodeStudioScopeBindingResult,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: 'gagent',
            serviceId: trimOptional(input.serviceId),
            displayName: trimOptional(input.displayName),
            gagent: compactObject({
              agentKind: input.agentKind.trim(),
              endpoints: input.endpoints.map((endpoint) =>
                compactObject({
                  endpointId: endpoint.endpointId.trim(),
                  displayName:
                    trimOptional(endpoint.displayName) ||
                    endpoint.endpointId.trim(),
                  kind: trimOptional(endpoint.kind)?.toLowerCase() || 'command',
                  requestTypeUrl: trimOptional(endpoint.requestTypeUrl),
                  responseTypeUrl: trimOptional(endpoint.responseTypeUrl),
                  description: trimOptional(endpoint.description),
                }),
              ),
            }),
            revisionId: trimOptional(input.revisionId),
          }),
        ),
      },
    );
  },

  getScopeBinding(scopeId: string): Promise<StudioScopeBindingStatus> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/binding`,
      decodeStudioScopeBindingStatus,
    );
  },

  bindMemberWorkflow(
    input: StudioMemberWorkflowBindingInput,
  ): Promise<StudioMemberBindingAcceptedResponse> {
    const workflowId = trimOptional(input.workflowId);
    if (!workflowId) {
      throw new Error('Workflow member binding requires a stable workflow id.');
    }

    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}/binding`,
      decodeStudioMemberBindingAcceptedResponse,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: 'workflow',
            displayName: trimOptional(input.displayName),
            workflow: {
              workflowId,
              workflowYamls: input.workflowYamls,
            },
            revisionId: trimOptional(input.revisionId),
            explicitRequestConfirmations:
              input.explicitRequestConfirmations &&
              input.explicitRequestConfirmations.length > 0
                ? input.explicitRequestConfirmations.map((confirmation) => ({
                    workflowId: confirmation.workflowId,
                    revisionId: confirmation.revisionId,
                    callSiteId: confirmation.callSiteId,
                    requestContractDigest: confirmation.requestContractDigest,
                    attestedRisk: confirmation.attestedRisk,
                  }))
                : undefined,
          }),
        ),
      },
    );
  },

  bindMemberScript(input: {
    scopeId: string;
    memberId: string;
    displayName?: string | null;
    scriptId: string;
    scriptRevision: string;
    revisionId?: string | null;
  }): Promise<StudioMemberBindingAcceptedResponse> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}/binding`,
      decodeStudioMemberBindingAcceptedResponse,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: 'script',
            displayName: trimOptional(input.displayName),
            script: compactObject({
              scriptId: input.scriptId.trim(),
              scriptRevision: input.scriptRevision.trim(),
            }),
            revisionId: trimOptional(input.revisionId),
          }),
        ),
      },
    );
  },

  bindMemberGAgent(input: {
    scopeId: string;
    memberId: string;
    displayName?: string | null;
    agentKind: string;
    endpoints: StudioScopeGAgentBindingInput['endpoints'];
    revisionId?: string | null;
  }): Promise<StudioMemberBindingAcceptedResponse> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}/binding`,
      decodeStudioMemberBindingAcceptedResponse,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: 'gagent',
            displayName: trimOptional(input.displayName),
            gagent: compactObject({
              agentKind: input.agentKind.trim(),
              endpoints: input.endpoints.map((endpoint) =>
                compactObject({
                  endpointId: endpoint.endpointId.trim(),
                  displayName:
                    trimOptional(endpoint.displayName) ||
                    endpoint.endpointId.trim(),
                  kind: trimOptional(endpoint.kind)?.toLowerCase() || 'command',
                  requestTypeUrl: trimOptional(endpoint.requestTypeUrl),
                  responseTypeUrl: trimOptional(endpoint.responseTypeUrl),
                  description: trimOptional(endpoint.description),
                }),
              ),
            }),
            revisionId: trimOptional(input.revisionId),
          }),
        ),
      },
    );
  },

  getMemberBinding(
    scopeId: string,
    memberId: string,
  ): Promise<StudioMemberBindingViewResponse> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/members/${encodeURIComponent(memberId.trim())}/binding`,
      decodeStudioMemberBindingViewResponse,
    );
  },

  getMemberBindingRun(
    scopeId: string,
    memberId: string,
    bindingRunId: string,
  ): Promise<StudioMemberBindingRunStatusResponse> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/members/${encodeURIComponent(memberId.trim())}/binding-runs/${encodeURIComponent(bindingRunId.trim())}`,
      decodeStudioMemberBindingRunStatusResponse,
    );
  },

  getDefaultRouteTarget(scopeId: string): Promise<StudioScopeBindingStatus> {
    return this.getScopeBinding(scopeId);
  },

  getScopeScriptBinding(
    scopeId: string,
  ): Promise<StudioScopeScriptBindingStatus> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/binding`,
      decodeStudioScopeBindingStatus,
    );
  },

  activateScopeBindingRevision(input: {
    scopeId: string;
    revisionId: string;
  }): Promise<StudioScopeBindingActivationResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(
        input.scopeId.trim(),
      )}/binding/revisions/${encodeURIComponent(
        input.revisionId.trim(),
      )}:activate`,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify({}),
      },
    );
  },

  activateScopeScriptBindingRevision(input: {
    scopeId: string;
    revisionId: string;
  }): Promise<StudioScopeScriptBindingActivationResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(
        input.scopeId.trim(),
      )}/binding/revisions/${encodeURIComponent(
        input.revisionId.trim(),
      )}:activate`,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify({}),
      },
    );
  },

  retireScopeBindingRevision(input: {
    scopeId: string;
    revisionId: string;
  }): Promise<StudioScopeBindingRetirementResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(
        input.scopeId.trim(),
      )}/binding/revisions/${encodeURIComponent(
        input.revisionId.trim(),
      )}:retire`,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify({}),
      },
    );
  },

  stopExecution(
    executionId: string,
    input: { reason?: string | null },
  ): Promise<StudioExecutionDetail> {
    return requestJson(
      `/api/executions/${encodeURIComponent(executionId)}/stop`,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          reason: trimOptional(input.reason),
        }),
      },
    );
  },

  resumeExecution(
    executionId: string,
    input: {
      runId: string;
      stepId: string;
      approved: boolean;
      userInput?: string | null;
      suspensionType: 'human_input' | 'human_approval';
    },
  ): Promise<StudioExecutionDetail> {
    return requestJson(
      `/api/executions/${encodeURIComponent(executionId)}/resume`,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          runId: input.runId,
          stepId: input.stepId,
          approved: input.approved,
          userInput: trimOptional(input.userInput),
          suspensionType: input.suspensionType,
        }),
      },
    );
  },

  getConnectorCatalog(): Promise<StudioConnectorCatalog> {
    return requestJson('/api/connectors/');
  },

  getConnectorDraft(): Promise<StudioConnectorDraftResponse> {
    return requestJson('/api/connectors/draft');
  },

  saveConnectorCatalog(input: {
    connectors: StudioConnectorCatalog['connectors'];
  }): Promise<StudioConnectorCatalog> {
    return requestJson('/api/connectors/', {
      method: 'PUT',
      headers: JSON_HEADERS,
      body: JSON.stringify({
        connectors: input.connectors,
      }),
    });
  },

  saveConnectorDraft(input: {
    draft: StudioConnectorDraftResponse['draft'];
  }): Promise<StudioConnectorDraftResponse> {
    return requestJson('/api/connectors/draft', {
      method: 'PUT',
      headers: JSON_HEADERS,
      body: JSON.stringify({
        draft: input.draft,
      }),
    });
  },

  deleteConnectorDraft(): Promise<void> {
    return request<void>('/api/connectors/draft', {
      method: 'DELETE',
    });
  },

  importConnectorCatalog(
    file: File,
  ): Promise<StudioConnectorCatalogImportResult> {
    const form = new FormData();
    form.set('file', file, file.name);
    return request('/api/connectors/import', {
      method: 'POST',
      body: form,
    });
  },

  getRoleCatalog(): Promise<StudioRoleCatalog> {
    return requestJson('/api/roles/');
  },

  getRoleDraft(): Promise<StudioRoleDraftResponse> {
    return requestJson('/api/roles/draft');
  },

  saveRoleCatalog(input: {
    roles: StudioRoleCatalog['roles'];
  }): Promise<StudioRoleCatalog> {
    return requestJson('/api/roles/', {
      method: 'PUT',
      headers: JSON_HEADERS,
      body: JSON.stringify({
        roles: input.roles,
      }),
    });
  },

  saveRoleDraft(input: {
    draft: StudioRoleDraftResponse['draft'];
  }): Promise<StudioRoleDraftResponse> {
    return requestJson('/api/roles/draft', {
      method: 'PUT',
      headers: JSON_HEADERS,
      body: JSON.stringify({
        draft: input.draft,
      }),
    });
  },

  deleteRoleDraft(): Promise<void> {
    return request<void>('/api/roles/draft', {
      method: 'DELETE',
    });
  },

  importRoleCatalog(file: File): Promise<StudioRoleCatalogImportResult> {
    const form = new FormData();
    form.set('file', file, file.name);
    return request('/api/roles/import', {
      method: 'POST',
      body: form,
    });
  },

  getUserConfig(): Promise<StudioUserConfig> {
    return requestJson('/api/user-config');
  },

  saveUserConfig(
    input: StudioUserConfig,
  ): Promise<StudioUserConfigSaveReceipt> {
    return requestDecodedJson(
      '/api/user-config',
      decodeStudioUserConfigSaveReceipt,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify({
          defaultModel: input.defaultModel.trim(),
          preferredLlmRoute:
            input.preferredLlmRoute === undefined ||
            input.preferredLlmRoute === null
              ? undefined
              : input.preferredLlmRoute.trim(),
          runtimeMode: trimOptional(input.runtimeMode),
          localRuntimeBaseUrl: trimOptional(input.localRuntimeBaseUrl),
          remoteRuntimeBaseUrl: trimOptional(input.remoteRuntimeBaseUrl),
          maxToolRounds: input.maxToolRounds ?? null,
        }),
      },
    );
  },

  getUserLlmSettings(signal?: AbortSignal): Promise<StudioUserLlmSettings> {
    return requestDecodedJson(
      '/api/user-config/llm',
      decodeStudioUserLlmSettings,
      signal ? { signal } : undefined,
    );
  },

  saveUserLlmSettings(
    input: StudioSaveUserLlmIntent,
  ): Promise<StudioUserConfigSaveReceipt> {
    return requestDecodedJson(
      '/api/user-config/llm',
      decodeStudioUserConfigSaveReceipt,
      {
        method: 'PUT',
        headers: JSON_HEADERS,
        body: JSON.stringify(input),
      },
    );
  },

  getUserConfigRuntime(): Promise<StudioUserConfigRuntime> {
    return requestDecodedJson(
      '/api/user-config/runtime',
      decodeStudioUserConfigRuntime,
    );
  },

  async getSkillsHealth(): Promise<StudioOrnnHealthResult> {
    const ornnConfig = getOrnnRuntimeConfig();
    const baseUrl = normalizeOrnnBaseUrl(ornnConfig.baseUrl);
    if (ornnConfig.configurationError || !baseUrl) {
      return {
        baseUrl,
        reachable: false,
        message:
          ornnConfig.configurationError ?? 'Ornn base URL is not configured.',
      };
    }

    const url = `${baseUrl}/api/web/skill-search?query=&scope=public&page=1&pageSize=1`;

    try {
      const response = await externalFetch(url);
      if (!response.ok) {
        return {
          baseUrl,
          reachable: false,
          message: t(
            'shared.studio.api.cannot.reach.ornn.status',
            'Cannot reach Ornn ({status}).',
            {
              status: response.status,
            },
          ),
        };
      }

      return {
        baseUrl,
        reachable: true,
        message: t('shared.studio.api.connected.to.ornn', 'Connected to Ornn.'),
      };
    } catch (error) {
      return {
        baseUrl,
        reachable: false,
        message:
          error instanceof Error && error.message
            ? error.message
            : 'Cannot reach Ornn.',
      };
    }
  },

  async searchSkills(input?: {
    query?: string | null;
    scope?: string | null;
    page?: number | null;
    pageSize?: number | null;
  }): Promise<StudioOrnnSkillSearchResult> {
    const ornnConfig = getOrnnRuntimeConfig();
    const baseUrl = normalizeOrnnBaseUrl(ornnConfig.baseUrl);
    const query = trimOptional(input?.query) ?? '';
    const scope = trimOptional(input?.scope) ?? 'mixed';
    const page = input?.page && input.page > 0 ? input.page : 1;
    const pageSize =
      input?.pageSize && input.pageSize > 0 ? input.pageSize : 50;
    if (ornnConfig.configurationError || !baseUrl) {
      return {
        baseUrl,
        total: 0,
        totalPages: 0,
        page,
        pageSize,
        items: [],
        message:
          ornnConfig.configurationError ?? 'Ornn base URL is not configured.',
      };
    }

    const params = new URLSearchParams({
      query,
      mode: 'keyword',
      scope,
      page: String(page),
      pageSize: String(pageSize),
    });

    const response = await externalFetch(
      `${baseUrl}/api/web/skill-search?${params.toString()}`,
    );
    if (!response.ok) {
      throw await createStudioApiError(response);
    }

    const contentType = response.headers?.get?.('content-type') ?? null;
    if (contentType !== null && !isJsonContentType(contentType)) {
      throw new Error('Ornn API returned an unexpected response format.');
    }

    return decodeOrnnSkillSearchResult(
      await response.json(),
      baseUrl,
      page,
      pageSize,
    );
  },

  addWorkflowDirectory(input: {
    path: string;
    label?: string | null;
  }): Promise<StudioWorkspaceSettings> {
    return requestJson('/api/workspace/directories', {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify(
        compactObject({
          path: input.path.trim(),
          label: trimOptional(input.label),
        }),
      ),
    });
  },

  removeWorkflowDirectory(directoryId: string): Promise<void> {
    return request<void>(
      `/api/workspace/directories/${encodeURIComponent(directoryId)}`,
      {
        method: 'DELETE',
      },
    );
  },

  async authorWorkflow(
    input: {
      prompt: string;
      currentYaml?: string;
      availableWorkflowNames?: string[];
      metadata?: Record<string, string>;
    },
    options?: {
      signal?: AbortSignal;
      onText?: (text: string) => void;
      onReasoning?: (text: string) => void;
    },
  ): Promise<string> {
    let generatedText = '';
    let reasoningText = '';

    await streamSse(
      '/api/workflows/generator',
      {
        prompt: input.prompt.trim(),
        currentYaml: input.currentYaml,
        availableWorkflowNames: input.availableWorkflowNames,
        metadata: input.metadata,
      },
      (frame) => {
        const normalized = normalizeAssistantFrame(frame);
        if (!normalized) {
          return;
        }

        if (normalized.type === 'TEXT_MESSAGE_CONTENT') {
          generatedText += normalized.delta || '';
          options?.onText?.(generatedText);
          return;
        }

        if (normalized.type === 'TEXT_MESSAGE_REASONING') {
          reasoningText += normalized.delta || '';
          options?.onReasoning?.(reasoningText);
          return;
        }

        if (normalized.type === 'TEXT_MESSAGE_END') {
          generatedText =
            generatedText || normalized.message || normalized.delta || '';
          options?.onText?.(generatedText);
          return;
        }

        if (normalized.type === 'RUN_ERROR') {
          throw new Error(normalized.message || 'Assistant run failed.');
        }
      },
      options?.signal,
    );

    return generatedText;
  },
};
