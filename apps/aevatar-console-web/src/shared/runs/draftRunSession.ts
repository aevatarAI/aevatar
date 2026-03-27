export type ScopeDraftRunPayload = {
  kind: "scope_draft";
  bundleName: string;
  bundleYamls: string[];
  createdAt: string;
};

export type EndpointInvocationDraftPayload = {
  kind: "endpoint_invocation";
  endpointId: string;
  prompt: string;
  payloadTypeUrl: string;
  payloadBase64?: string;
  serviceOverrideId?: string;
  createdAt: string;
};

export type DraftRunPayload =
  | ScopeDraftRunPayload
  | EndpointInvocationDraftPayload;

const STORAGE_PREFIX = "aevatar-console-draft-run:";

function buildStorageKey(key: string): string {
  return `${STORAGE_PREFIX}${key}`;
}

function createDraftRunKey(): string {
  const random = globalThis.crypto?.randomUUID?.();
  if (random) {
    return random;
  }

  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

export function saveScopeDraftRunPayload(payload: {
  bundleName: string;
  bundleYamls: string[];
}): string {
  if (typeof window === "undefined") {
    return "";
  }

  const key = createDraftRunKey();
  const normalizedPayload: ScopeDraftRunPayload = {
    kind: "scope_draft",
    bundleName: payload.bundleName.trim(),
    bundleYamls: payload.bundleYamls
      .map((item) => item.trim())
      .filter((item) => item.length > 0),
    createdAt: new Date().toISOString(),
  };
  window.sessionStorage.setItem(
    buildStorageKey(key),
    JSON.stringify(normalizedPayload)
  );
  return key;
}

export function saveEndpointInvocationDraftPayload(payload: {
  endpointId: string;
  prompt: string;
  payloadTypeUrl: string;
  payloadBase64?: string;
  serviceOverrideId?: string;
}): string {
  if (typeof window === "undefined") {
    return "";
  }

  const endpointId = payload.endpointId.trim();
  const payloadTypeUrl = payload.payloadTypeUrl.trim();
  const payloadBase64 = payload.payloadBase64?.trim();
  if (!endpointId || !payloadTypeUrl) {
    return "";
  }

  const key = createDraftRunKey();
  const normalizedPayload: EndpointInvocationDraftPayload = {
    kind: "endpoint_invocation",
    endpointId,
    prompt: payload.prompt.trim(),
    payloadTypeUrl,
    payloadBase64: payloadBase64 || undefined,
    serviceOverrideId: payload.serviceOverrideId?.trim() || undefined,
    createdAt: new Date().toISOString(),
  };
  window.sessionStorage.setItem(
    buildStorageKey(key),
    JSON.stringify(normalizedPayload)
  );
  return key;
}

export function isScopeDraftRunPayload(
  payload: DraftRunPayload | null | undefined
): payload is ScopeDraftRunPayload {
  return payload?.kind === "scope_draft";
}

export function isEndpointInvocationDraftPayload(
  payload: DraftRunPayload | null | undefined
): payload is EndpointInvocationDraftPayload {
  return payload?.kind === "endpoint_invocation";
}

export function loadDraftRunPayload(
  key: string | null | undefined
): DraftRunPayload | null {
  const normalizedKey = key?.trim();
  if (!normalizedKey || typeof window === "undefined") {
    return null;
  }

  const raw = window.sessionStorage.getItem(buildStorageKey(normalizedKey));
  if (!raw) {
    return null;
  }

  try {
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    const parsedKind =
      typeof parsed.kind === "string" ? parsed.kind : undefined;
    if (
      parsedKind === "endpoint_invocation" ||
      parsedKind === "service_invocation"
    ) {
      const servicePayload = parsed as Partial<
        EndpointInvocationDraftPayload & {
          serviceId?: string;
        }
      >;
      const endpointId = servicePayload.endpointId?.trim();
      const payloadTypeUrl = servicePayload.payloadTypeUrl?.trim();
      const payloadBase64 = servicePayload.payloadBase64?.trim();
      if (!endpointId || !payloadTypeUrl) {
        return null;
      }

      return {
        kind: "endpoint_invocation",
        endpointId,
        prompt: servicePayload.prompt?.trim() || "",
        payloadTypeUrl,
        payloadBase64: payloadBase64 || undefined,
        serviceOverrideId:
          servicePayload.serviceOverrideId?.trim() ||
          servicePayload.serviceId?.trim() ||
          undefined,
        createdAt: servicePayload.createdAt?.trim() || "",
      };
    }

    const workflowPayload = parsed as Partial<
      ScopeDraftRunPayload & {
        workflowName?: string;
        workflowYamls?: unknown[];
      }
    >;
    const bundleName =
      workflowPayload.bundleName?.trim() ||
      workflowPayload.workflowName?.trim();
    const bundleYamls = Array.isArray(workflowPayload.bundleYamls)
      ? workflowPayload.bundleYamls
          .map((item: unknown) => String(item ?? "").trim())
          .filter((item: string) => item.length > 0)
      : Array.isArray(workflowPayload.workflowYamls)
      ? workflowPayload.workflowYamls
          .map((item: unknown) => String(item ?? "").trim())
          .filter((item: string) => item.length > 0)
      : [];
    if (!bundleName || bundleYamls.length === 0) {
      return null;
    }

    return {
      kind: "scope_draft",
      bundleName,
      bundleYamls,
      createdAt: workflowPayload.createdAt?.trim() || "",
    };
  } catch {
    return null;
  }
}

export function deleteDraftRunPayload(key: string | null | undefined): void {
  const normalizedKey = key?.trim();
  if (!normalizedKey || typeof window === "undefined") {
    return;
  }

  window.sessionStorage.removeItem(buildStorageKey(normalizedKey));
}

export type WorkflowDraftRunPayload = ScopeDraftRunPayload;
export type ServiceInvocationDraftPayload = EndpointInvocationDraftPayload;

export const saveDraftRunPayload = (payload: {
  workflowName: string;
  workflowYamls: string[];
}): string =>
  saveScopeDraftRunPayload({
    bundleName: payload.workflowName,
    bundleYamls: payload.workflowYamls,
  });

export const saveServiceInvocationDraftPayload = (payload: {
  endpointId: string;
  prompt: string;
  payloadTypeUrl: string;
  payloadBase64?: string;
  serviceId?: string;
}): string =>
  saveEndpointInvocationDraftPayload({
    endpointId: payload.endpointId,
    prompt: payload.prompt,
    payloadTypeUrl: payload.payloadTypeUrl,
    payloadBase64: payload.payloadBase64,
    serviceOverrideId: payload.serviceId,
  });

export const isWorkflowDraftRunPayload = isScopeDraftRunPayload;
export const isServiceInvocationDraftPayload = isEndpointInvocationDraftPayload;
