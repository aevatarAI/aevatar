import {
  expectArray,
  expectRecord,
  readBoolean,
  readOptionalRecord,
  readString,
  readStringArray,
  readStringRecord,
  type Decoder,
} from "./http/decoders";
import { requestJson } from "./http/client";
import type {
  ScopeScriptCatalog,
  ScopeScriptDetail,
  ScopeScriptSource,
  ScopeScriptSummary,
  ScopeWorkflowDetail,
  ScopeWorkflowSource,
  ScopeWorkflowSummary,
} from "@/shared/models/scopes";

function decodeScopeWorkflowSummary(
  value: unknown,
  label = "ScopeWorkflowSummary"
): ScopeWorkflowSummary {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    workflowId: readString(
      record,
      ["workflowId", "WorkflowId"],
      `${label}.workflowId`
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      `${label}.displayName`
    ),
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    workflowName: readString(
      record,
      ["workflowName", "WorkflowName"],
      `${label}.workflowName`
    ),
    actorId: readString(record, ["actorId", "ActorId"], `${label}.actorId`),
    activeRevisionId: readString(
      record,
      ["activeRevisionId", "ActiveRevisionId"],
      `${label}.activeRevisionId`
    ),
    deploymentId: readString(
      record,
      ["deploymentId", "DeploymentId"],
      `${label}.deploymentId`
    ),
    deploymentStatus: readString(
      record,
      ["deploymentStatus", "DeploymentStatus"],
      `${label}.deploymentStatus`
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeScopeWorkflowSource(
  value: unknown,
  label = "ScopeWorkflowSource"
): ScopeWorkflowSource {
  const record = expectRecord(value, label);
  const inlineWorkflowYamls = readOptionalRecord(
    record,
    ["inlineWorkflowYamls", "InlineWorkflowYamls"],
    `${label}.inlineWorkflowYamls`
  );

  return {
    workflowYaml: readString(
      record,
      ["workflowYaml", "WorkflowYaml"],
      `${label}.workflowYaml`
    ),
    definitionActorId: readString(
      record,
      ["definitionActorId", "DefinitionActorId"],
      `${label}.definitionActorId`
    ),
    inlineWorkflowYamls: inlineWorkflowYamls
      ? readStringRecord(
          { inlineWorkflowYamls },
          "inlineWorkflowYamls",
          `${label}.inlineWorkflowYamls`
        )
      : null,
  };
}

function decodeScopeWorkflowDetail(
  value: unknown,
  label = "ScopeWorkflowDetail"
): ScopeWorkflowDetail {
  const record = expectRecord(value, label);
  const workflowValue = record.workflow ?? record.Workflow;
  const sourceValue = record.source ?? record.Source;

  return {
    available: readBoolean(
      record,
      ["available", "Available"],
      `${label}.available`
    ),
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
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
  label = "ScopeScriptSummary"
): ScopeScriptSummary {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    scriptId: readString(record, ["scriptId", "ScriptId"], `${label}.scriptId`),
    catalogActorId: readString(
      record,
      ["catalogActorId", "CatalogActorId"],
      `${label}.catalogActorId`
    ),
    definitionActorId: readString(
      record,
      ["definitionActorId", "DefinitionActorId"],
      `${label}.definitionActorId`
    ),
    activeRevision: readString(
      record,
      ["activeRevision", "ActiveRevision"],
      `${label}.activeRevision`
    ),
    activeSourceHash: readString(
      record,
      ["activeSourceHash", "ActiveSourceHash"],
      `${label}.activeSourceHash`
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeScopeScriptSource(
  value: unknown,
  label = "ScopeScriptSource"
): ScopeScriptSource {
  const record = expectRecord(value, label);
  return {
    sourceText: readString(
      record,
      ["sourceText", "SourceText"],
      `${label}.sourceText`
    ),
    definitionActorId: readString(
      record,
      ["definitionActorId", "DefinitionActorId"],
      `${label}.definitionActorId`
    ),
    revision: readString(record, ["revision", "Revision"], `${label}.revision`),
    sourceHash: readString(
      record,
      ["sourceHash", "SourceHash"],
      `${label}.sourceHash`
    ),
  };
}

function decodeScopeScriptDetail(
  value: unknown,
  label = "ScopeScriptDetail"
): ScopeScriptDetail {
  const record = expectRecord(value, label);
  const scriptValue = record.script ?? record.Script;
  const sourceValue = record.source ?? record.Source;

  return {
    available: readBoolean(
      record,
      ["available", "Available"],
      `${label}.available`
    ),
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
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
  label = "ScopeScriptCatalog"
): ScopeScriptCatalog {
  const record = expectRecord(value, label);
  return {
    scriptId: readString(record, ["scriptId", "ScriptId"], `${label}.scriptId`),
    activeRevision: readString(
      record,
      ["activeRevision", "ActiveRevision"],
      `${label}.activeRevision`
    ),
    activeDefinitionActorId: readString(
      record,
      ["activeDefinitionActorId", "ActiveDefinitionActorId"],
      `${label}.activeDefinitionActorId`
    ),
    activeSourceHash: readString(
      record,
      ["activeSourceHash", "ActiveSourceHash"],
      `${label}.activeSourceHash`
    ),
    previousRevision: readString(
      record,
      ["previousRevision", "PreviousRevision"],
      `${label}.previousRevision`
    ),
    revisionHistory: readStringArray(
      record,
      ["revisionHistory", "RevisionHistory"],
      `${label}.revisionHistory`
    ),
    lastProposalId: readString(
      record,
      ["lastProposalId", "LastProposalId"],
      `${label}.lastProposalId`
    ),
    catalogActorId: readString(
      record,
      ["catalogActorId", "CatalogActorId"],
      `${label}.catalogActorId`
    ),
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

const decodeScopeWorkflowSummaries: Decoder<ScopeWorkflowSummary[]> = (value) =>
  expectArray(value, "ScopeWorkflowSummary[]", decodeScopeWorkflowSummary);

const decodeScopeScriptSummaries: Decoder<ScopeScriptSummary[]> = (value) =>
  expectArray(value, "ScopeScriptSummary[]", decodeScopeScriptSummary);

export const scopesApi = {
  listWorkflows(scopeId: string): Promise<ScopeWorkflowSummary[]> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/workflows?includeSource=false`,
      decodeScopeWorkflowSummaries
    );
  },

  getWorkflowDetail(
    scopeId: string,
    workflowId: string
  ): Promise<ScopeWorkflowDetail> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(
        scopeId
      )}/workflows/${encodeURIComponent(workflowId)}`,
      decodeScopeWorkflowDetail
    );
  },

  listScripts(scopeId: string): Promise<ScopeScriptSummary[]> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/scripts?includeSource=false`,
      decodeScopeScriptSummaries
    );
  },

  getScriptDetail(
    scopeId: string,
    scriptId: string
  ): Promise<ScopeScriptDetail> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/scripts/${encodeURIComponent(
        scriptId
      )}`,
      decodeScopeScriptDetail
    );
  },

  getScriptCatalog(
    scopeId: string,
    scriptId: string
  ): Promise<ScopeScriptCatalog> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/scripts/${encodeURIComponent(
        scriptId
      )}/catalog`,
      decodeScopeScriptCatalog
    );
  },
};
