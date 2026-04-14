import type {
  StudioUserConfigModelsResponse,
  StudioUserConfigProviderStatus,
} from "@/shared/studio/models";

export const USER_LLM_ROUTE_GATEWAY = "";
export const USER_CONFIG_PROVIDER_SOURCE_GATEWAY = "gateway_provider";
export const USER_CONFIG_PROVIDER_SOURCE_SERVICE = "user_service";
export const LLM_ROUTE_HEADER_KEY = "nyxid.route_preference";
export const LLM_MODEL_HEADER_KEY = "aevatar.model_override";
export const CONVERSATION_ROUTE_DEFAULT_VALUE = "__config_default__";
export const CONVERSATION_ROUTE_GATEWAY_VALUE = "__gateway__";

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

export function routePathFromProviderSlug(slug: string): string {
  const normalized = String(slug || "").trim();
  return normalized ? `/api/v1/proxy/s/${normalized}` : USER_LLM_ROUTE_GATEWAY;
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

  if (route === USER_LLM_ROUTE_GATEWAY) {
    return "NyxID Gateway";
  }

  return routeOptions.find((option) => option.value === route)?.label || route;
}

export function formatConversationProviderLabel(provider: {
  providerName: string;
  providerSlug: string;
}): string {
  const explicitName = provider.providerName.trim();
  if (explicitName) {
    return explicitName;
  }

  const slug = provider.providerSlug.trim();
  if (!slug) {
    return "Provider";
  }

  return slug
    .split(/[-_]+/)
    .filter(Boolean)
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join(" ");
}

function getReadyProviders(
  providers: readonly StudioUserConfigProviderStatus[]
): StudioUserConfigProviderStatus[] {
  return providers.filter(
    (provider) => provider.status.trim().toLowerCase() === "ready"
  );
}

export function buildConversationRouteOptions(
  models: StudioUserConfigModelsResponse | undefined,
  globalPreferredRoute?: string,
  conversationRoute?: string
): ConversationRouteOption[] {
  const options: ConversationRouteOption[] = [
    { label: "NyxID Gateway", value: USER_LLM_ROUTE_GATEWAY },
  ];
  const seen = new Set(options.map((option) => option.value));

  for (const provider of getReadyProviders(models?.providers ?? [])) {
    const source =
      provider.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY;
    if (source !== USER_CONFIG_PROVIDER_SOURCE_SERVICE) {
      continue;
    }

    const route = routePathFromProviderSlug(provider.providerSlug);
    if (!provider.providerSlug || seen.has(route)) {
      continue;
    }

    seen.add(route);
    options.push({
      label: formatConversationProviderLabel(provider),
      value: route,
    });
  }

  for (const route of [globalPreferredRoute, conversationRoute]) {
    if (route && !seen.has(route)) {
      seen.add(route);
      options.push({ label: route, value: route });
    }
  }

  return options;
}

function buildProviderPrefixMap(
  providers: readonly StudioUserConfigProviderStatus[]
): Record<string, string> {
  const prefixToProvider: Record<string, string> = {};

  for (const provider of providers) {
    const slug = provider.providerSlug.trim();
    const name = formatConversationProviderLabel(provider);
    if (slug === "openai") {
      for (const prefix of ["gpt-", "o1-", "o1", "o3-", "o3", "o4-", "chatgpt-"]) {
        prefixToProvider[prefix] = name;
      }
    } else if (slug === "anthropic") {
      prefixToProvider["claude-"] = name;
    } else if (slug === "google-ai") {
      prefixToProvider["gemini-"] = name;
    } else if (slug === "mistral") {
      for (const prefix of ["mistral-", "codestral-", "magistral-"]) {
        prefixToProvider[prefix] = name;
      }
    } else if (slug === "cohere") {
      prefixToProvider["command-"] = name;
    } else if (slug === "deepseek") {
      prefixToProvider["deepseek-"] = name;
    } else if (slug) {
      prefixToProvider[`${slug}-`] = name;
    }
  }

  return prefixToProvider;
}

function buildFallbackModelGroups(
  supportedModels: readonly string[],
  providers: readonly StudioUserConfigProviderStatus[]
): ConversationLlmModelGroup[] {
  const prefixToProvider = buildProviderPrefixMap(providers);
  const groupedModels = new Map<string, string[]>();

  for (const model of supportedModels) {
    if (!model.trim()) {
      continue;
    }

    let providerName = "Supported models";
    for (const [prefix, name] of Object.entries(prefixToProvider)) {
      if (model.startsWith(prefix) || model === prefix.replace(/-$/, "")) {
        providerName = name;
        break;
      }
    }

    if (!groupedModels.has(providerName)) {
      groupedModels.set(providerName, []);
    }

    groupedModels.get(providerName)?.push(model);
  }

  return Array.from(groupedModels.entries()).map(([label, models], index) => ({
    id: `fallback-${index}`,
    label,
    models,
  }));
}

export function buildConversationModelGroups(input: {
  conversationModel?: string;
  effectiveRoute: string;
  globalDefaultModel?: string;
  models: StudioUserConfigModelsResponse | undefined;
}): ConversationLlmModelGroup[] {
  const readyProviders = getReadyProviders(input.models?.providers ?? []);
  const gatewayProviders = readyProviders.filter(
    (provider) =>
      (provider.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY) ===
      USER_CONFIG_PROVIDER_SOURCE_GATEWAY
  );
  const routeProviders =
    input.effectiveRoute === USER_LLM_ROUTE_GATEWAY
      ? gatewayProviders
      : readyProviders.filter(
          (provider) =>
            routePathFromProviderSlug(provider.providerSlug) ===
            input.effectiveRoute
        );

  const explicitGroups = routeProviders
    .map((provider) => {
      const models = Array.from(
        new Set(
          (input.models?.modelsByProvider?.[provider.providerSlug] ?? []).filter(
            Boolean
          )
        )
      );

      return {
        id: provider.providerSlug || formatConversationProviderLabel(provider),
        label: formatConversationProviderLabel(provider),
        models,
      };
    })
    .filter((group) => group.models.length > 0);

  const fallbackGroups =
    explicitGroups.length > 0
      ? []
      : buildFallbackModelGroups(input.models?.supportedModels ?? [], routeProviders);

  const groups = [...explicitGroups, ...fallbackGroups];
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
