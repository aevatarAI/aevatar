import type {
  StudioUserLlmModelGroup,
  StudioUserLlmRouteOption,
  StudioUserLlmSettings,
} from "@/shared/studio/models";

export const USER_LLM_ROUTE_GATEWAY = "";
export const LLM_ROUTE_HEADER_KEY = "nyxid.route_preference";
export const LLM_MODEL_HEADER_KEY = "aevatar.model_override";
export const CONVERSATION_ROUTE_DEFAULT_VALUE = "__config_default__";
export const CONVERSATION_ROUTE_GATEWAY_VALUE = "__gateway__";
/**
 * Refactor (issue1525): Old pattern: conversation route labels hard-coded
 * gateway branding when user settings did not provide a label.
 * New principle: backend route labels win, with only a neutral generic fallback.
 */
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
  if (!normalized || /^auto$/i.test(normalized) || /^gateway$/i.test(normalized)) {
    return USER_LLM_ROUTE_GATEWAY;
  }

  if (normalized.includes("://") || normalized.startsWith("//")) {
    return USER_LLM_ROUTE_GATEWAY;
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

/**
 * Refactor (issue1525): Old pattern: callers depended on route names or
 * service-specific literals when a typed backend label was unavailable.
 * New principle: resolve labels from backend route options first, then fall
 * back to the route value or neutral gateway wording.
 */
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

/**
 * Refactor (issue1525): Old pattern: UI code derived gateway wording from
 * route/provider details when building route choices.
 * New principle: preserve backend option labels and synthesize only neutral
 * labels for routes that are present locally but absent from backend options.
 */
export function buildConversationRouteOptions(
  settings: StudioUserLlmSettings | undefined,
  globalPreferredRoute?: string,
  conversationRoute?: string
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
      label: option.label.trim() || route || USER_LLM_ROUTE_GATEWAY_LABEL,
      value: route,
    });
  }

  for (const route of [globalPreferredRoute, conversationRoute]) {
    if (route !== undefined) {
      const normalizedRoute = normalizeUserLlmRoute(route);
      if (!seen.has(normalizedRoute)) {
        seen.add(normalizedRoute);
        options.push({
          label: normalizedRoute || USER_LLM_ROUTE_GATEWAY_LABEL,
          value: normalizedRoute,
        });
      }
    }
  }

  return options;
}

export function buildConversationModelGroups(input: {
  conversationModel?: string;
  effectiveRoute: string;
  globalDefaultModel?: string;
  settings: StudioUserLlmSettings | undefined;
}): ConversationLlmModelGroup[] {
  const normalizedRoute = normalizeUserLlmRoute(input.effectiveRoute);
  const groups = (input.settings?.modelGroupsByRoute ?? [])
    .filter((group: StudioUserLlmModelGroup) =>
      normalizeUserLlmRoute(group.routeValue) === normalizedRoute
    )
    .map((group) => ({
      id: group.groupId,
      label: group.label,
      models: Array.from(new Set(group.models.filter(Boolean))),
    }))
    .filter((group) => group.models.length > 0);
  const selectedModel =
    trimConversationValue(input.conversationModel) ||
    trimConversationValue(input.globalDefaultModel);

  if (
    selectedModel &&
    !groups.some((group) => group.models.includes(selectedModel))
  ) {
    groups.unshift({
      id: "__current__",
      label: "Current",
      models: [selectedModel],
    });
  }

  return groups;
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
