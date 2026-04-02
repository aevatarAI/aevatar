import type { AGUIEvent } from "@aevatar-react-sdk/types";
import {
  type RunEndpointKind,
  normalizeRunEndpointKind,
} from "./endpointKinds";

export type ScopeDraftRunPayload = {
  kind: "scope_draft";
  bundleName: string;
  bundleYamls: string[];
  createdAt: string;
};

export type EndpointInvocationDraftPayload = {
  kind: "endpoint_invocation";
  endpointId: string;
  endpointKind: RunEndpointKind;
  prompt: string;
  payloadTypeUrl: string;
  payloadBase64?: string;
  serviceOverrideId?: string;
  createdAt: string;
};

export type ObservedRunSessionPayload = {
  kind: "observed_run_session";
  scopeId: string;
  routeName?: string;
  serviceOverrideId?: string;
  endpointId: string;
  endpointKind: RunEndpointKind;
  prompt: string;
  payloadTypeUrl?: string;
  payloadBase64?: string;
  actorId?: string;
  commandId?: string;
  runId?: string;
  events: AGUIEvent[];
  createdAt: string;
};

export type DraftRunPayload =
  | ScopeDraftRunPayload
  | EndpointInvocationDraftPayload
  | ObservedRunSessionPayload;

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
  endpointKind?: RunEndpointKind;
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
  const endpointKind = normalizeRunEndpointKind(
    payload.endpointKind,
    endpointId
  );
  if (!endpointId || !payloadTypeUrl) {
    return "";
  }

  const key = createDraftRunKey();
  const normalizedPayload: EndpointInvocationDraftPayload = {
    kind: "endpoint_invocation",
    endpointId,
    endpointKind,
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

export function saveObservedRunSessionPayload(payload: {
  scopeId: string;
  routeName?: string;
  endpointId: string;
  endpointKind?: RunEndpointKind;
  prompt: string;
  events: AGUIEvent[];
  serviceOverrideId?: string;
  payloadTypeUrl?: string;
  payloadBase64?: string;
  actorId?: string;
  commandId?: string;
  runId?: string;
}): string {
  if (typeof window === "undefined") {
    return "";
  }

  const scopeId = payload.scopeId.trim();
  const endpointId = payload.endpointId.trim();
  const endpointKind = normalizeRunEndpointKind(
    payload.endpointKind,
    endpointId
  );
  if (!scopeId || !endpointId || payload.events.length === 0) {
    return "";
  }

  const key = createDraftRunKey();
  const normalizedPayload: ObservedRunSessionPayload = {
    kind: "observed_run_session",
    scopeId,
    routeName: payload.routeName?.trim() || undefined,
    endpointId,
    endpointKind,
    prompt: payload.prompt.trim(),
    events: payload.events.map((event) => ({ ...event })),
    serviceOverrideId: payload.serviceOverrideId?.trim() || undefined,
    payloadTypeUrl: payload.payloadTypeUrl?.trim() || undefined,
    payloadBase64: payload.payloadBase64?.trim() || undefined,
    actorId: payload.actorId?.trim() || undefined,
    commandId: payload.commandId?.trim() || undefined,
    runId: payload.runId?.trim() || undefined,
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

export function isObservedRunSessionPayload(
  payload: DraftRunPayload | null | undefined
): payload is ObservedRunSessionPayload {
  return payload?.kind === "observed_run_session";
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
    if (parsedKind === "observed_run_session") {
      const observedPayload = parsed as Partial<ObservedRunSessionPayload>;
      const scopeId = observedPayload.scopeId?.trim();
      const endpointId = observedPayload.endpointId?.trim();
      const events = Array.isArray(observedPayload.events)
        ? observedPayload.events.filter(
            (event): event is AGUIEvent =>
              typeof event === "object" && event !== null
          )
        : [];
      if (!scopeId || !endpointId || events.length === 0) {
        return null;
      }

      return {
        kind: "observed_run_session",
        scopeId,
        routeName: observedPayload.routeName?.trim() || undefined,
        endpointId,
        endpointKind: normalizeRunEndpointKind(
          observedPayload.endpointKind,
          endpointId
        ),
        prompt: observedPayload.prompt?.trim() || "",
        serviceOverrideId: observedPayload.serviceOverrideId?.trim() || undefined,
        payloadTypeUrl: observedPayload.payloadTypeUrl?.trim() || undefined,
        payloadBase64: observedPayload.payloadBase64?.trim() || undefined,
        actorId: observedPayload.actorId?.trim() || undefined,
        commandId: observedPayload.commandId?.trim() || undefined,
        runId: observedPayload.runId?.trim() || undefined,
        events,
        createdAt: observedPayload.createdAt?.trim() || "",
      };
    }

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
        endpointKind: normalizeRunEndpointKind(
          servicePayload.endpointKind,
          endpointId
        ),
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
export type ObservedServiceRunPayload = ObservedRunSessionPayload;

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
  endpointKind?: RunEndpointKind;
  prompt: string;
  payloadTypeUrl: string;
  payloadBase64?: string;
  serviceId?: string;
}): string =>
  saveEndpointInvocationDraftPayload({
    endpointId: payload.endpointId,
    endpointKind: payload.endpointKind,
    prompt: payload.prompt,
    payloadTypeUrl: payload.payloadTypeUrl,
    payloadBase64: payload.payloadBase64,
    serviceOverrideId: payload.serviceId,
  });

export const isWorkflowDraftRunPayload = isScopeDraftRunPayload;
export const isServiceInvocationDraftPayload = isEndpointInvocationDraftPayload;
