import type {
  ScopeScriptCatalog,
  ScopeScriptDetail,
  ScopeScriptSource,
  ScopeScriptSummary,
  ScopeWorkflowArchiveAcceptedResult,
  ScopeWorkflowCatalogueActionCapability,
  ScopeWorkflowCatalogueCommittedFacts,
  ScopeWorkflowCatalogueQuery,
  ScopeWorkflowCatalogueResponse,
  ScopeWorkflowCatalogueRow,
  ScopeWorkflowCatalogueRowCapabilities,
  ScopeWorkflowDetail,
  ScopeWorkflowSource,
  ScopeWorkflowSummary,
} from '@/shared/models/scopes';
import { requestJson, withQuery } from './http/client';
import {
  type Decoder,
  expectArray,
  expectRecord,
  readBoolean,
  readNullableString,
  readNumber,
  readOptionalRecord,
  readString,
  readStringArray,
  readStringRecord,
} from './http/decoders';

function decodeScopeWorkflowCatalogueActionCapability(
  value: unknown,
  label: string,
): ScopeWorkflowCatalogueActionCapability {
  const record = expectRecord(value, label);
  return {
    available: readBoolean(
      record,
      ['available', 'Available'],
      `${label}.available`,
    ),
    unavailableReason: readNullableString(
      record,
      ['unavailableReason', 'UnavailableReason'],
      `${label}.unavailableReason`,
    ),
  };
}

function decodeScopeWorkflowCatalogueCapabilities(
  value: unknown,
  label: string,
): ScopeWorkflowCatalogueRowCapabilities {
  const record = expectRecord(value, label);
  return {
    open: decodeScopeWorkflowCatalogueActionCapability(
      record.open ?? record.Open,
      `${label}.open`,
    ),
    activity: decodeScopeWorkflowCatalogueActionCapability(
      record.activity ?? record.Activity,
      `${label}.activity`,
    ),
    rename: decodeScopeWorkflowCatalogueActionCapability(
      record.rename ?? record.Rename,
      `${label}.rename`,
    ),
    delete: decodeScopeWorkflowCatalogueActionCapability(
      record.delete ?? record.Delete,
      `${label}.delete`,
    ),
  };
}

function decodeScopeWorkflowCatalogueCommittedFacts(
  value: unknown,
  label: string,
): ScopeWorkflowCatalogueCommittedFacts | null {
  if (value === null || value === undefined) return null;

  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ['serviceKey', 'ServiceKey'],
      `${label}.serviceKey`,
    ),
    workflowName: readString(
      record,
      ['workflowName', 'WorkflowName'],
      `${label}.workflowName`,
    ),
    actorId: readString(record, ['actorId', 'ActorId'], `${label}.actorId`),
    activeRevisionId: readString(
      record,
      ['activeRevisionId', 'ActiveRevisionId'],
      `${label}.activeRevisionId`,
    ),
    deploymentId: readString(
      record,
      ['deploymentId', 'DeploymentId'],
      `${label}.deploymentId`,
    ),
    deploymentStatus: readString(
      record,
      ['deploymentStatus', 'DeploymentStatus'],
      `${label}.deploymentStatus`,
    ),
    serviceAppId: readString(
      record,
      ['serviceAppId', 'ServiceAppId'],
      `${label}.serviceAppId`,
    ),
    serviceNamespace: readString(
      record,
      ['serviceNamespace', 'ServiceNamespace'],
      `${label}.serviceNamespace`,
    ),
  };
}

function decodeScopeWorkflowCatalogueRow(
  value: unknown,
  label = 'ScopeWorkflowCatalogueRow',
): ScopeWorkflowCatalogueRow {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ['scopeId', 'ScopeId'], `${label}.scopeId`),
    workflowId: readString(
      record,
      ['workflowId', 'WorkflowId'],
      `${label}.workflowId`,
    ),
    name: readString(record, ['name', 'Name'], `${label}.name`),
    description: readString(
      record,
      ['description', 'Description'],
      `${label}.description`,
    ),
    hasDraftSource: readBoolean(
      record,
      ['hasDraftSource', 'HasDraftSource'],
      `${label}.hasDraftSource`,
    ),
    hasCommittedSource: readBoolean(
      record,
      ['hasCommittedSource', 'HasCommittedSource'],
      `${label}.hasCommittedSource`,
    ),
    updatedAtUtc: readString(
      record,
      ['updatedAtUtc', 'UpdatedAtUtc'],
      `${label}.updatedAtUtc`,
    ),
    updatedAtSource: readString(
      record,
      ['updatedAtSource', 'UpdatedAtSource'],
      `${label}.updatedAtSource`,
    ),
    capabilities: decodeScopeWorkflowCatalogueCapabilities(
      record.capabilities ?? record.Capabilities,
      `${label}.capabilities`,
    ),
    sourceWatermarkUtc: readString(
      record,
      ['sourceWatermarkUtc', 'SourceWatermarkUtc'],
      `${label}.sourceWatermarkUtc`,
    ),
    committed: decodeScopeWorkflowCatalogueCommittedFacts(
      record.committed ?? record.Committed,
      `${label}.committed`,
    ),
    publishedServiceId: readNullableString(
      record,
      ['publishedServiceId', 'PublishedServiceId'],
      `${label}.publishedServiceId`,
    ),
  };
}

const decodeScopeWorkflowCatalogueResponse: Decoder<
  ScopeWorkflowCatalogueResponse
> = (value, label = 'ScopeWorkflowCatalogueResponse') => {
  const record = expectRecord(value, label);
  const freshness = expectRecord(
    record.freshness ?? record.Freshness,
    `${label}.freshness`,
  );
  const search = expectRecord(
    record.search ?? record.Search,
    `${label}.search`,
  );

  return {
    items: expectArray(
      record.items ?? record.Items,
      `${label}.items`,
      decodeScopeWorkflowCatalogueRow,
    ),
    nextPageToken: readNullableString(
      record,
      ['nextPageToken', 'NextPageToken'],
      `${label}.nextPageToken`,
    ),
    freshness: {
      refreshWatermarkUtc: readNullableString(
        freshness,
        ['refreshWatermarkUtc', 'RefreshWatermarkUtc'],
        `${label}.freshness.refreshWatermarkUtc`,
      ),
      sourceVersionSemantics: readString(
        freshness,
        ['sourceVersionSemantics', 'SourceVersionSemantics'],
        `${label}.freshness.sourceVersionSemantics`,
      ),
    },
    search: {
      searchableFields: readStringArray(
        search,
        ['searchableFields', 'SearchableFields'],
        `${label}.search.searchableFields`,
      ),
      caseSemantics: readString(
        search,
        ['caseSemantics', 'CaseSemantics'],
        `${label}.search.caseSemantics`,
      ),
      unicodeNormalization: readString(
        search,
        ['unicodeNormalization', 'UnicodeNormalization'],
        `${label}.search.unicodeNormalization`,
      ),
      maximumQueryLength: readNumber(
        search,
        ['maximumQueryLength', 'MaximumQueryLength'],
        `${label}.search.maximumQueryLength`,
      ),
      emptyQuerySemantics: readString(
        search,
        ['emptyQuerySemantics', 'EmptyQuerySemantics'],
        `${label}.search.emptyQuerySemantics`,
      ),
      workflowIdSemantics: readString(
        search,
        ['workflowIdSemantics', 'WorkflowIdSemantics'],
        `${label}.search.workflowIdSemantics`,
      ),
    },
  };
};

function decodeScopeWorkflowSummary(
  value: unknown,
  label = 'ScopeWorkflowSummary',
): ScopeWorkflowSummary {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ['scopeId', 'ScopeId'], `${label}.scopeId`),
    workflowId: readString(
      record,
      ['workflowId', 'WorkflowId'],
      `${label}.workflowId`,
    ),
    displayName: readString(
      record,
      ['displayName', 'DisplayName'],
      `${label}.displayName`,
    ),
    serviceKey: readString(
      record,
      ['serviceKey', 'ServiceKey'],
      `${label}.serviceKey`,
    ),
    workflowName: readString(
      record,
      ['workflowName', 'WorkflowName'],
      `${label}.workflowName`,
    ),
    actorId: readString(record, ['actorId', 'ActorId'], `${label}.actorId`),
    activeRevisionId: readString(
      record,
      ['activeRevisionId', 'ActiveRevisionId'],
      `${label}.activeRevisionId`,
    ),
    deploymentId: readString(
      record,
      ['deploymentId', 'DeploymentId'],
      `${label}.deploymentId`,
    ),
    deploymentStatus: readString(
      record,
      ['deploymentStatus', 'DeploymentStatus'],
      `${label}.deploymentStatus`,
    ),
    updatedAt: readString(
      record,
      ['updatedAt', 'UpdatedAt'],
      `${label}.updatedAt`,
    ),
    publishedServiceId: readString(
      record,
      ['publishedServiceId', 'PublishedServiceId'],
      `${label}.publishedServiceId`,
    ),
    serviceAppId: readString(
      record,
      ['serviceAppId', 'ServiceAppId'],
      `${label}.serviceAppId`,
    ),
    serviceNamespace: readString(
      record,
      ['serviceNamespace', 'ServiceNamespace'],
      `${label}.serviceNamespace`,
    ),
  };
}

function decodeScopeWorkflowSource(
  value: unknown,
  label = 'ScopeWorkflowSource',
): ScopeWorkflowSource {
  const record = expectRecord(value, label);
  const inlineWorkflowYamls = readOptionalRecord(
    record,
    ['inlineWorkflowYamls', 'InlineWorkflowYamls'],
    `${label}.inlineWorkflowYamls`,
  );

  return {
    workflowYaml: readString(
      record,
      ['workflowYaml', 'WorkflowYaml'],
      `${label}.workflowYaml`,
    ),
    definitionActorId: readString(
      record,
      ['definitionActorId', 'DefinitionActorId'],
      `${label}.definitionActorId`,
    ),
    inlineWorkflowYamls: inlineWorkflowYamls
      ? readStringRecord(
          { inlineWorkflowYamls },
          'inlineWorkflowYamls',
          `${label}.inlineWorkflowYamls`,
        )
      : null,
  };
}

function decodeScopeWorkflowDetail(
  value: unknown,
  label = 'ScopeWorkflowDetail',
): ScopeWorkflowDetail {
  const record = expectRecord(value, label);
  const workflowValue = record.workflow ?? record.Workflow;
  const sourceValue = record.source ?? record.Source;

  return {
    available: readBoolean(
      record,
      ['available', 'Available'],
      `${label}.available`,
    ),
    scopeId: readString(record, ['scopeId', 'ScopeId'], `${label}.scopeId`),
    workflow:
      workflowValue === null || workflowValue === undefined
        ? null
        : decodeScopeWorkflowSummary(workflowValue, `${label}.workflow`),
    source:
      sourceValue === null || sourceValue === undefined
        ? null
        : decodeScopeWorkflowSource(sourceValue, `${label}.source`),
  };
}

function decodeScopeScriptSummary(
  value: unknown,
  label = 'ScopeScriptSummary',
): ScopeScriptSummary {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ['scopeId', 'ScopeId'], `${label}.scopeId`),
    scriptId: readString(record, ['scriptId', 'ScriptId'], `${label}.scriptId`),
    catalogActorId: readString(
      record,
      ['catalogActorId', 'CatalogActorId'],
      `${label}.catalogActorId`,
    ),
    definitionActorId: readString(
      record,
      ['definitionActorId', 'DefinitionActorId'],
      `${label}.definitionActorId`,
    ),
    activeRevision: readString(
      record,
      ['activeRevision', 'ActiveRevision'],
      `${label}.activeRevision`,
    ),
    activeSourceHash: readString(
      record,
      ['activeSourceHash', 'ActiveSourceHash'],
      `${label}.activeSourceHash`,
    ),
    updatedAt: readString(
      record,
      ['updatedAt', 'UpdatedAt'],
      `${label}.updatedAt`,
    ),
  };
}

function decodeScopeScriptSource(
  value: unknown,
  label = 'ScopeScriptSource',
): ScopeScriptSource {
  const record = expectRecord(value, label);
  return {
    sourceText: readString(
      record,
      ['sourceText', 'SourceText'],
      `${label}.sourceText`,
    ),
    definitionActorId: readString(
      record,
      ['definitionActorId', 'DefinitionActorId'],
      `${label}.definitionActorId`,
    ),
    revision: readString(record, ['revision', 'Revision'], `${label}.revision`),
    sourceHash: readString(
      record,
      ['sourceHash', 'SourceHash'],
      `${label}.sourceHash`,
    ),
  };
}

function decodeScopeScriptDetail(
  value: unknown,
  label = 'ScopeScriptDetail',
): ScopeScriptDetail {
  const record = expectRecord(value, label);
  const scriptValue = record.script ?? record.Script;
  const sourceValue = record.source ?? record.Source;

  return {
    available: readBoolean(
      record,
      ['available', 'Available'],
      `${label}.available`,
    ),
    scopeId: readString(record, ['scopeId', 'ScopeId'], `${label}.scopeId`),
    script:
      scriptValue === null || scriptValue === undefined
        ? null
        : decodeScopeScriptSummary(scriptValue, `${label}.script`),
    source:
      sourceValue === null || sourceValue === undefined
        ? null
        : decodeScopeScriptSource(sourceValue, `${label}.source`),
  };
}

function decodeScopeScriptCatalog(
  value: unknown,
  label = 'ScopeScriptCatalog',
): ScopeScriptCatalog {
  const record = expectRecord(value, label);
  return {
    scriptId: readString(record, ['scriptId', 'ScriptId'], `${label}.scriptId`),
    activeRevision: readString(
      record,
      ['activeRevision', 'ActiveRevision'],
      `${label}.activeRevision`,
    ),
    activeDefinitionActorId: readString(
      record,
      ['activeDefinitionActorId', 'ActiveDefinitionActorId'],
      `${label}.activeDefinitionActorId`,
    ),
    activeSourceHash: readString(
      record,
      ['activeSourceHash', 'ActiveSourceHash'],
      `${label}.activeSourceHash`,
    ),
    previousRevision: readString(
      record,
      ['previousRevision', 'PreviousRevision'],
      `${label}.previousRevision`,
    ),
    revisionHistory: readStringArray(
      record,
      ['revisionHistory', 'RevisionHistory'],
      `${label}.revisionHistory`,
    ),
    lastProposalId: readString(
      record,
      ['lastProposalId', 'LastProposalId'],
      `${label}.lastProposalId`,
    ),
    catalogActorId: readString(
      record,
      ['catalogActorId', 'CatalogActorId'],
      `${label}.catalogActorId`,
    ),
    scopeId: readString(record, ['scopeId', 'ScopeId'], `${label}.scopeId`),
    updatedAt: readString(
      record,
      ['updatedAt', 'UpdatedAt'],
      `${label}.updatedAt`,
    ),
  };
}

const decodeScopeWorkflowSummaries: Decoder<ScopeWorkflowSummary[]> = (value) =>
  expectArray(value, 'ScopeWorkflowSummary[]', decodeScopeWorkflowSummary);

const decodeScopeWorkflowDetails: Decoder<ScopeWorkflowDetail[]> = (value) =>
  expectArray(value, 'ScopeWorkflowDetail[]', decodeScopeWorkflowDetail);

const decodeScopeWorkflowArchiveAcceptedResult: Decoder<
  ScopeWorkflowArchiveAcceptedResult
> = (value, label = 'ScopeWorkflowArchiveAcceptedResult') => {
  const record = expectRecord(value, label);
  const commandHandle = expectRecord(
    record.commandHandle ?? record.CommandHandle,
    `${label}.commandHandle`,
  );
  return {
    scopeId: readString(record, ['scopeId', 'ScopeId'], `${label}.scopeId`),
    workflowId: readString(
      record,
      ['workflowId', 'WorkflowId'],
      `${label}.workflowId`,
    ),
    deploymentId: readString(
      record,
      ['deploymentId', 'DeploymentId'],
      `${label}.deploymentId`,
    ),
    commandHandle: {
      stage: readString(
        commandHandle,
        ['stage', 'Stage'],
        `${label}.commandHandle.stage`,
      ),
      targetActorId: readString(
        commandHandle,
        ['targetActorId', 'TargetActorId'],
        `${label}.commandHandle.targetActorId`,
      ),
      commandId: readString(
        commandHandle,
        ['commandId', 'CommandId'],
        `${label}.commandHandle.commandId`,
      ),
      correlationId: readString(
        commandHandle,
        ['correlationId', 'CorrelationId'],
        `${label}.commandHandle.correlationId`,
      ),
    },
    readModelUrl: readString(
      record,
      ['readModelUrl', 'ReadModelUrl'],
      `${label}.readModelUrl`,
    ),
    acceptanceStage: readString(
      record,
      ['acceptanceStage', 'AcceptanceStage'],
      `${label}.acceptanceStage`,
    ),
    propagationStage: readString(
      record,
      ['propagationStage', 'PropagationStage'],
      `${label}.propagationStage`,
    ),
  };
};

const decodeScopeScriptSummaries: Decoder<ScopeScriptSummary[]> = (value) =>
  expectArray(value, 'ScopeScriptSummary[]', decodeScopeScriptSummary);

export const scopesApi = {
  queryWorkflowCatalogue(
    input: ScopeWorkflowCatalogueQuery,
    signal?: AbortSignal,
  ): Promise<ScopeWorkflowCatalogueResponse> {
    return requestJson(
      withQuery(
        `/api/scopes/${encodeURIComponent(input.scopeId)}/workflow-catalogue`,
        {
          view: input.view,
          query: input.query?.trim() || undefined,
          cursor: input.cursor,
          take: input.take,
        },
      ),
      decodeScopeWorkflowCatalogueResponse,
      { signal },
    );
  },

  listWorkflows(scopeId: string): Promise<ScopeWorkflowSummary[]> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/workflows?includeSource=false`,
      decodeScopeWorkflowSummaries,
    );
  },

  listWorkflowDetails(scopeId: string): Promise<ScopeWorkflowDetail[]> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/workflows?includeSource=true`,
      decodeScopeWorkflowDetails,
    );
  },

  getWorkflowDetail(
    scopeId: string,
    workflowId: string,
  ): Promise<ScopeWorkflowDetail> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(
        scopeId,
      )}/workflows/${encodeURIComponent(workflowId)}`,
      decodeScopeWorkflowDetail,
    );
  },

  archiveWorkflow(
    scopeId: string,
    workflowId: string,
  ): Promise<ScopeWorkflowArchiveAcceptedResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/workflows/${encodeURIComponent(
        workflowId,
      )}:archive`,
      decodeScopeWorkflowArchiveAcceptedResult,
      { method: 'POST' },
    );
  },

  listScripts(scopeId: string): Promise<ScopeScriptSummary[]> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/scripts?includeSource=false`,
      decodeScopeScriptSummaries,
    );
  },

  getScriptDetail(
    scopeId: string,
    scriptId: string,
  ): Promise<ScopeScriptDetail> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/scripts/${encodeURIComponent(
        scriptId,
      )}`,
      decodeScopeScriptDetail,
    );
  },

  getScriptCatalog(
    scopeId: string,
    scriptId: string,
  ): Promise<ScopeScriptCatalog> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/scripts/${encodeURIComponent(
        scriptId,
      )}/catalog`,
      decodeScopeScriptCatalog,
    );
  },
};
