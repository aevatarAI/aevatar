const identifierFieldNames = new Set([
  "actorid",
  "actor_id",
  "agentid",
  "agent_id",
  "artifactid",
  "artifact_id",
  "artifacthash",
  "artifact_hash",
  "bindingid",
  "binding_id",
  "checksum",
  "commandid",
  "command_id",
  "conversationid",
  "conversation_id",
  "correlationid",
  "correlation_id",
  "deploymentid",
  "deployment_id",
  "draftworkflowid",
  "draft_workflow_id",
  "endpointid",
  "endpoint_id",
  "executionid",
  "execution_id",
  "fileid",
  "file_id",
  "id",
  "lastcommandid",
  "last_command_id",
  "lastcorrelationid",
  "last_correlation_id",
  "lasteventid",
  "last_event_id",
  "memberid",
  "member_id",
  "policyid",
  "policy_id",
  "publishedserviceid",
  "published_service_id",
  "revisionid",
  "revision_id",
  "roleid",
  "role_id",
  "rolloutid",
  "rollout_id",
  "runid",
  "run_id",
  "scopeid",
  "scope_id",
  "serviceid",
  "service_id",
  "sessionid",
  "session_id",
  "sourcehash",
  "source_hash",
  "stageid",
  "stage_id",
  "stateversion",
  "state_version",
  "tenantid",
  "tenant_id",
  "traceid",
  "trace_id",
  "workflowid",
  "workflow_id",
  "workerid",
  "worker_id",
]);

const identifierValuePrefixes = [
  "actor",
  "agent",
  "artifact",
  "binding",
  "cmd",
  "command",
  "conversation",
  "corr",
  "correlation",
  "deployment",
  "draft",
  "execution",
  "file",
  "member",
  "policy",
  "revision",
  "role",
  "rollout",
  "run",
  "scope",
  "service",
  "session",
  "stage",
  "svc",
  "trace",
  "wf",
  "workflow",
  "worker",
];

const identifierKeywordPattern =
  /(?:actor|agent|artifact|binding|command|conversation|correlation|deployment|draft|execution|file|hash|member|policy|published|revision|role|rollout|run|scope|service|session|stage|trace|workflow|worker)/i;

function normalizeIdentifierKey(value: string): string {
  return value.trim().replace(/[\s-]+/g, "_").toLowerCase();
}

function splitCamelKey(value: string): string {
  return value.replace(/([a-z0-9])([A-Z])/g, "$1_$2");
}

export function isMachineIdentifierFieldName(value: string | null | undefined): boolean {
  const normalized = normalizeIdentifierKey(splitCamelKey(value ?? ""));
  if (!normalized) {
    return false;
  }

  if (identifierFieldNames.has(normalized) || identifierFieldNames.has(normalized.replace(/_/g, ""))) {
    return true;
  }

  return /(^|_)(?:id|ids)$/.test(normalized);
}

export function isMachineIdentifierValue(value: string | null | undefined): boolean {
  const normalized = value?.trim() ?? "";
  if (!normalized) {
    return false;
  }

  if (/^[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+$/.test(normalized)) {
    return false;
  }

  if (/^[0-9a-f]{16,}$/i.test(normalized)) {
    return true;
  }

  if (/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(normalized)) {
    return true;
  }

  if (/^[a-z0-9]+(?:[_-][a-z0-9]+){3,}$/i.test(normalized) && identifierKeywordPattern.test(normalized)) {
    return true;
  }

  if (/^[a-z]+:\/\/[^/\s]+/i.test(normalized) && identifierKeywordPattern.test(normalized)) {
    return true;
  }

  const prefixPattern = new RegExp(
    `^(?:${identifierValuePrefixes.join("|")})[-_:][a-z0-9][a-z0-9_.:-]{3,}$`,
    "i",
  );
  return prefixPattern.test(normalized);
}

function prettifyKey(value: string): string {
  return normalizeIdentifierKey(splitCamelKey(value))
    .split("_")
    .filter(Boolean)
    .map((segment) => `${segment.charAt(0).toUpperCase()}${segment.slice(1)}`)
    .join(" ");
}

function isEmptyPlainObject(value: unknown): boolean {
  return Boolean(
    value &&
      typeof value === "object" &&
      !Array.isArray(value) &&
      Object.keys(value as Record<string, unknown>).length === 0,
  );
}

function isVisibleSanitizedPayload(value: unknown): boolean {
  return value !== undefined && value !== "" && !isEmptyPlainObject(value);
}

function sanitizePayload(value: unknown, keyHint = ""): unknown {
  if (isMachineIdentifierFieldName(keyHint)) {
    return undefined;
  }

  if (Array.isArray(value)) {
    const visibleItems = value
      .map((item) => sanitizePayload(item))
      .filter(isVisibleSanitizedPayload);
    return visibleItems.length ? visibleItems : undefined;
  }

  if (value && typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>)
      .map(([key, item]) => [key, sanitizePayload(item, key)] as const)
      .filter(([, item]) => isVisibleSanitizedPayload(item));

    if (!entries.length) {
      return undefined;
    }

    return Object.fromEntries(entries);
  }

  if (typeof value === "string") {
    const trimmed = value.trim();
    return isMachineIdentifierValue(trimmed) ? undefined : trimmed;
  }

  return value;
}

export function sanitizeUserFacingPayload(value: unknown): unknown {
  return sanitizePayload(value);
}

function sanitizeInlineIdentifierFields(value: string): string {
  return value
    .replace(
      /"([^"]+)"\s*:\s*("([^"\\]|\\.)*"|\{[^{}]*\}|\[[^\[\]]*\]|true|false|null|-?\d+(?:\.\d+)?)/g,
      (match, key) => (isMachineIdentifierFieldName(String(key)) ? "" : match),
    )
    .replace(/\{\s*,\s*/g, "{")
    .replace(/,\s*([}\]])/g, "$1")
    .replace(/([{\[])\s*([}\]])/g, "$1$2");
}

function sanitizeInlineIdentifierValues(value: string): string {
  return value
    .replace(
      /(^|[\s([{:,])([A-Za-z0-9][A-Za-z0-9_.:/-]{5,}[A-Za-z0-9])(?=$|[\s)\]}:,.])/g,
      (match, prefix, candidate) =>
        isMachineIdentifierValue(String(candidate)) ? String(prefix) : match,
    )
    .replace(/[ \t]{2,}/g, " ")
    .replace(/:\s*\{\s*\}/g, ":")
    .replace(/:\s*\[\s*\]/g, ":")
    .replace(/,\s*,+/g, ",")
    .replace(/\{\s*,/g, "{")
    .replace(/,\s*\}/g, "}")
    .replace(/\s+([,.:;)\]}])/g, "$1")
    .replace(/([([{])\s+/g, "$1")
    .replace(/:\s*([,}\]])/g, "$1")
    .replace(/[{}[\]]/g, "")
    .replace(/\s*,\s*(?=$|[.:;)\]}])/g, "")
    .trim();
}

export function sanitizeUserFacingText(value: string | null | undefined): string {
  const normalized = value?.trim() ?? "";
  if (!normalized || isMachineIdentifierValue(normalized)) {
    return "";
  }

  try {
    const parsed = JSON.parse(normalized) as unknown;
    const sanitized = sanitizeUserFacingPayload(parsed);
    return sanitized === undefined ? "" : JSON.stringify(sanitized, null, 2);
  } catch {
    return sanitizeInlineIdentifierValues(sanitizeInlineIdentifierFields(normalized));
  }
}

export function sanitizeUserFacingRecord(
  values: Readonly<Record<string, string>>,
): Readonly<Record<string, string>> {
  return Object.fromEntries(
    Object.entries(values)
      .map(([key, value]) => {
        if (isMachineIdentifierFieldName(key)) {
          return null;
        }

        const sanitized = sanitizeUserFacingText(value);
        return sanitized ? [prettifyKey(key) || key, sanitized] : null;
      })
      .filter((entry): entry is [string, string] => Boolean(entry)),
  );
}

export function getUserFacingIdentifierLabel(
  value: string | null | undefined,
  fallback: string,
): string {
  const normalized = value?.trim() ?? "";
  if (!normalized || isMachineIdentifierValue(normalized)) {
    return fallback;
  }

  return normalized;
}
