import type {
  StudioUserLlmRemediation,
  StudioUserLlmModelGroup,
  StudioUserLlmRouteOption,
  StudioUserLlmSettings,
} from "@/shared/studio/models";

export const USER_LLM_ROUTE_GATEWAY = "/api/v1/llm/gateway/v1";
export const LLM_ROUTE_HEADER_KEY = "nyxid.route_preference";
export const LLM_MODEL_HEADER_KEY = "aevatar.model_override";
export const CONVERSATION_ROUTE_DEFAULT_VALUE = "__config_default__";
export const CONVERSATION_ROUTE_GATEWAY_VALUE = "__gateway__";
const USER_LLM_ROUTE_GATEWAY_LABEL = "Gateway";

export type ConversationRouteOption = {
  label: string;
  value: string;
};

export type ConversationLlmModelGroup = {
  id: string;
  label: string;
  models: string[];
};

export function trimConversationValue(value?: string | null): string | undefined {
  const normalized = String(value || "").trim();
  return normalized || undefined;
}

export function normalizeUserLlmRoute(value: unknown): string {
  const normalized = String(value || "").trim();
  if (!normalized || /^auto$/i.test(normalized)) {
    return "";
  }
  if (/^gateway$/i.test(normalized)) {
    return USER_LLM_ROUTE_GATEWAY;
  }

  if (normalized.includes("://") || normalized.startsWith("//")) {
    return "";
  }

  if (normalized.startsWith("/")) {
    return normalized;
  }

  return `/api/v1/proxy/s/${normalized.replace(/^\/+|\/+$/g, "")}`;
}

export function buildConversationHeaders(
  llmRoute: string | undefined,
  llmModel: string | undefined
): Record<string, string> | undefined {
  const headers: Record<string, string> = {};

  if (llmRoute !== undefined) {
    headers[LLM_ROUTE_HEADER_KEY] = llmRoute;
  }

  const normalizedModel = trimConversationValue(llmModel);
  if (normalizedModel) {
    headers[LLM_MODEL_HEADER_KEY] = normalizedModel;
  }

  return Object.keys(headers).length > 0 ? headers : undefined;
}

export function encodeConversationRouteSelectValue(
  route: string | undefined
): string {
  if (route === undefined) {
    return CONVERSATION_ROUTE_DEFAULT_VALUE;
  }

  return route === USER_LLM_ROUTE_GATEWAY
    ? CONVERSATION_ROUTE_GATEWAY_VALUE
    : route;
}

export function decodeConversationRouteSelectValue(
  value: string
): string | undefined {
  if (value === CONVERSATION_ROUTE_DEFAULT_VALUE) {
    return undefined;
  }

  return value === CONVERSATION_ROUTE_GATEWAY_VALUE
    ? USER_LLM_ROUTE_GATEWAY
    : normalizeUserLlmRoute(value);
}

export function describeConversationRoute(
  route: string | undefined,
  routeOptions: readonly ConversationRouteOption[]
): string {
  if (route === undefined) {
    return "Config default";
  }

  return routeOptions.find((option) => option.value === route)?.label ||
    route ||
    USER_LLM_ROUTE_GATEWAY_LABEL;
}

export function buildConversationRouteOptions(
  settings: StudioUserLlmSettings | undefined
): ConversationRouteOption[] {
  const options: ConversationRouteOption[] = [];
  const seen = new Set<string>();
  for (const option of settings?.routeOptions ?? []) {
    const route = normalizeUserLlmRoute(option.routeValue);
    if (seen.has(route)) {
      continue;
    }

    seen.add(route);
    options.push({
      label:
        option.label.trim() ||
        (route === USER_LLM_ROUTE_GATEWAY
          ? USER_LLM_ROUTE_GATEWAY_LABEL
          : route),
      value: route,
    });
  }

  return options;
}

export function buildConversationModelGroups(input: {
  route: string;
  settings: StudioUserLlmSettings | undefined;
}): ConversationLlmModelGroup[] {
  const normalizedRoute = normalizeUserLlmRoute(input.route);
  return (input.settings?.modelGroupsByRoute ?? [])
    .filter((group: StudioUserLlmModelGroup) =>
      normalizeUserLlmRoute(group.routeValue) === normalizedRoute
    )
    .map((group) => ({
      id: group.groupId,
      label: group.label,
      models: Array.from(new Set(group.models.filter(Boolean))),
    }))
    .filter((group) => group.models.length > 0);
}

export type SavedConversationLlmConfig =
  | { readonly status: "system_default" }
  | {
      readonly status: "ready";
      readonly route: string;
      readonly model?: string;
      readonly routeLabel: string;
    }
  | {
      readonly status: "action_required";
      readonly remediation: StudioUserLlmRemediation;
      readonly routeLabel: string;
    };

export function resolveSavedConversationLlmConfig(
  settings: StudioUserLlmSettings | undefined,
): SavedConversationLlmConfig {
  if (!settings || settings.selectionStatus === "system_default") {
    return { status: "system_default" };
  }

  const selection = settings.savedSelection;
  if (
    settings.selectionStatus !== "ready" ||
    !selection ||
    selection.routeKind === "unspecified"
  ) {
    return {
      status: "action_required",
      remediation: settings.remediation,
      routeLabel: settings.savedRouteLabel,
    };
  }

  return {
    status: "ready",
    route: selection.routeValue,
    model:
      selection.modelSelection.kind === "explicit_model"
        ? selection.modelSelection.modelId
        : undefined,
    routeLabel: settings.savedRouteLabel,
  };
}

export function findConversationRouteOption(
  settings: StudioUserLlmSettings | undefined,
  route: string
): StudioUserLlmRouteOption | undefined {
  const normalizedRoute = normalizeUserLlmRoute(route);
  return settings?.routeOptions.find(
    (option) => normalizeUserLlmRoute(option.routeValue) === normalizedRoute
  );
}
