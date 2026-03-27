export type WorkflowDraftRunPayload = {
  kind: "workflow";
  workflowName: string;
  workflowYamls: string[];
  createdAt: string;
};

export type ServiceInvocationDraftPayload = {
  kind: "service_invocation";
  endpointId: string;
  prompt: string;
  payloadTypeUrl: string;
  payloadBase64?: string;
  serviceId?: string;
  createdAt: string;
};

export type DraftRunPayload =
  | WorkflowDraftRunPayload
  | ServiceInvocationDraftPayload;

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

export function saveDraftRunPayload(payload: {
  workflowName: string;
  workflowYamls: string[];
}): string {
  if (typeof window === "undefined") {
    return "";
  }

  const key = createDraftRunKey();
  const normalizedPayload: WorkflowDraftRunPayload = {
    kind: "workflow",
    workflowName: payload.workflowName.trim(),
    workflowYamls: payload.workflowYamls
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

export function saveServiceInvocationDraftPayload(payload: {
  endpointId: string;
  prompt: string;
  payloadTypeUrl: string;
  payloadBase64?: string;
  serviceId?: string;
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
  const normalizedPayload: ServiceInvocationDraftPayload = {
      kind: "service_invocation",
      endpointId,
      prompt: payload.prompt.trim(),
      payloadTypeUrl,
      payloadBase64: payloadBase64 || undefined,
      serviceId: payload.serviceId?.trim() || undefined,
      createdAt: new Date().toISOString(),
  };
  window.sessionStorage.setItem(
    buildStorageKey(key),
    JSON.stringify(normalizedPayload)
  );
  return key;
}

export function isWorkflowDraftRunPayload(
  payload: DraftRunPayload | null | undefined
): payload is WorkflowDraftRunPayload {
  return payload?.kind === "workflow";
}

export function isServiceInvocationDraftPayload(
  payload: DraftRunPayload | null | undefined
): payload is ServiceInvocationDraftPayload {
  return payload?.kind === "service_invocation";
}

export function loadDraftRunPayload(key: string | null | undefined): DraftRunPayload | null {
  const normalizedKey = key?.trim();
  if (!normalizedKey || typeof window === "undefined") {
    return null;
  }

  const raw = window.sessionStorage.getItem(buildStorageKey(normalizedKey));
  if (!raw) {
    return null;
  }

  try {
    const parsed = JSON.parse(raw) as Partial<DraftRunPayload>;
    if (parsed.kind === "service_invocation") {
      const servicePayload = parsed as Partial<ServiceInvocationDraftPayload>;
      const endpointId = servicePayload.endpointId?.trim();
      const payloadTypeUrl = servicePayload.payloadTypeUrl?.trim();
      const payloadBase64 = servicePayload.payloadBase64?.trim();
      if (!endpointId || !payloadTypeUrl) {
        return null;
      }

      return {
        kind: "service_invocation",
        endpointId,
        prompt: servicePayload.prompt?.trim() || "",
        payloadTypeUrl,
        payloadBase64: payloadBase64 || undefined,
        serviceId: servicePayload.serviceId?.trim() || undefined,
        createdAt: servicePayload.createdAt?.trim() || "",
      };
    }

    const workflowPayload = parsed as Partial<WorkflowDraftRunPayload>;
    const workflowName = workflowPayload.workflowName?.trim();
    const workflowYamls = Array.isArray(workflowPayload.workflowYamls)
      ? workflowPayload.workflowYamls
          .map((item: unknown) => String(item ?? "").trim())
          .filter((item: string) => item.length > 0)
      : [];
    if (!workflowName || workflowYamls.length === 0) {
      return null;
    }

    return {
      kind: "workflow",
      workflowName,
      workflowYamls,
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
