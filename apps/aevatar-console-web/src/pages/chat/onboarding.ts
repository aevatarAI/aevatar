import { createProviderDraft } from "@/shared/studio/providerDrafts";
import type {
  StudioProviderSettings,
  StudioProviderType,
  StudioSettings,
} from "@/shared/studio/models";
import type { ServiceOption } from "./chatTypes";

export const onboardingServiceId = "onboarding";
export const onboardingServiceLabel = "Onboarding";

export type OnboardingStep =
  | "select_provider"
  | "select_endpoint_mode"
  | "ask_custom_endpoint"
  | "ask_api_key"
  | "creating"
  | "done";

export type OnboardingState = {
  endpointUrl?: string;
  providerTypeId?: string;
  providerTypeLabel?: string;
  step: OnboardingStep;
};

export function createOnboardingServiceOption(): ServiceOption {
  return {
    endpoints: [
      {
        description: "Guide the operator through provider setup for NyxID Chat.",
        displayName: "Chat",
        endpointId: "chat",
        kind: "chat",
      },
    ],
    id: onboardingServiceId,
    kind: "onboarding",
    label: onboardingServiceLabel,
  };
}

export function hasConfiguredProviders(
  providers: readonly Pick<StudioProviderSettings, "apiKey" | "apiKeyConfigured">[]
): boolean {
  return providers.some(
    (provider) => provider.apiKeyConfigured || provider.apiKey.trim().length > 0
  );
}

export function buildOnboardingProviderPrompt(
  providerTypes: readonly StudioProviderType[]
): string {
  if (providerTypes.length === 0) {
    return [
      "No provider types are available yet.",
      "",
      "Open Studio Settings to refresh the provider catalog, then try onboarding again.",
    ].join("\n");
  }

  return [
    "No AI provider is configured for NyxID Chat yet. Pick a provider:",
    "",
    ...providerTypes.map((providerType, index) => {
      const parts = [`${index + 1}. ${providerType.displayName}`];
      if (providerType.recommended) {
        parts.push("(recommended)");
      }
      if (providerType.description.trim()) {
        parts.push(`- ${providerType.description.trim()}`);
      }
      return parts.join(" ");
    }),
    "",
    `Enter a number from 1 to ${providerTypes.length}.`,
  ].join("\n");
}

export function resolveOnboardingProviderType(
  input: string,
  providerTypes: readonly StudioProviderType[]
): StudioProviderType | null {
  const normalized = input.trim().toLowerCase();
  if (!normalized) {
    return null;
  }

  const numeric = Number(normalized);
  if (Number.isInteger(numeric) && numeric >= 1 && numeric <= providerTypes.length) {
    return providerTypes[numeric - 1] || null;
  }

  return (
    providerTypes.find((providerType) => {
      const displayName = providerType.displayName.trim().toLowerCase();
      const id = providerType.id.trim().toLowerCase();
      return normalized === displayName || normalized === id;
    }) || null
  );
}

export function buildOnboardingEndpointModePrompt(
  providerType: StudioProviderType
): string {
  return [
    `Configure ${providerType.displayName}:`,
    "",
    "1. Use the default endpoint",
    "2. Enter a custom endpoint",
    "",
    `Default endpoint: ${providerType.defaultEndpoint || "Not provided"}`,
    "Enter 1 or 2.",
  ].join("\n");
}

export function resolveOnboardingEndpointMode(
  input: string
): "custom" | "default" | null {
  const normalized = input.trim().toLowerCase();
  if (!normalized) {
    return null;
  }

  if (["1", "default", "use default"].includes(normalized)) {
    return "default";
  }

  if (["2", "custom", "custom endpoint"].includes(normalized)) {
    return "custom";
  }

  return null;
}

export function buildOnboardingCustomEndpointPrompt(
  providerTypeLabel: string
): string {
  return `Enter a custom endpoint URL for ${providerTypeLabel} (for example https://api.example.com/v1):`;
}

export function buildOnboardingApiKeyPrompt(
  providerTypeLabel: string,
  endpointUrl: string
): string {
  return [
    `Ready to connect ${providerTypeLabel}.`,
    `Endpoint: ${endpointUrl || "Not set"}`,
    "",
    "Enter your API key.",
  ].join("\n");
}

export function buildOnboardingCreatingMessage(
  providerTypeLabel: string
): string {
  return `Saving ${providerTypeLabel} to Studio Settings...`;
}

export function buildOnboardingSuccessPrompt(
  providerName: string,
  providerTypeLabel: string
): string {
  return [
    `Connected! Saved ${providerTypeLabel} as \`${providerName}\` in Studio Settings.`,
    "",
    "Switch the service selector to `NyxID Chat` to start using it, or type `restart` to configure another provider.",
  ].join("\n");
}

export function buildOnboardingDonePrompt(): string {
  return [
    "Onboarding is already complete for this chat.",
    "",
    "Switch the service selector to `NyxID Chat`, or type `restart` to configure another provider.",
  ].join("\n");
}

export function buildOnboardingProviderErrorPrompt(
  providerTypes: readonly StudioProviderType[]
): string {
  return [
    "I couldn't match that provider selection.",
    "",
    buildOnboardingProviderPrompt(providerTypes),
  ].join("\n");
}

export function buildOnboardingEndpointModeErrorPrompt(
  providerType: StudioProviderType
): string {
  return [
    "Please reply with `1` for the default endpoint or `2` for a custom endpoint.",
    "",
    buildOnboardingEndpointModePrompt(providerType),
  ].join("\n");
}

export function buildOnboardingCustomEndpointErrorPrompt(
  providerTypeLabel: string
): string {
  return [
    "That endpoint does not look like a valid http(s) URL.",
    "",
    buildOnboardingCustomEndpointPrompt(providerTypeLabel),
  ].join("\n");
}

export function buildOnboardingApiKeyErrorPrompt(
  providerTypeLabel: string,
  endpointUrl: string,
  errorMessage?: string
): string {
  return [
    errorMessage || "Saving the provider failed.",
    "",
    buildOnboardingApiKeyPrompt(providerTypeLabel, endpointUrl),
  ].join("\n");
}

export function createOnboardingProviderSettings(
  settings: StudioSettings,
  providerTypeId: string,
  apiKey: string,
  endpointUrl?: string
): StudioProviderSettings {
  const providerDraft = createProviderDraft(
    settings.providerTypes,
    settings.providers,
    providerTypeId
  );

  return {
    ...providerDraft,
    apiKey: apiKey.trim(),
    apiKeyConfigured: apiKey.trim().length > 0,
    endpoint: endpointUrl?.trim() || providerDraft.endpoint,
  };
}

export function buildOnboardingSaveSettingsInput(
  settings: StudioSettings,
  nextProvider: StudioProviderSettings
): Pick<StudioSettings, "defaultProviderName" | "runtimeBaseUrl"> & {
  providers: Array<{
    apiKey?: string;
    endpoint?: string;
    model: string;
    providerName: string;
    providerType: string;
  }>;
} {
  const providers = [
    nextProvider,
    ...settings.providers.filter(
      (provider) => provider.providerName !== nextProvider.providerName
    ),
  ];

  return {
    defaultProviderName:
      settings.defaultProviderName.trim() || nextProvider.providerName,
    providers: providers.map((provider) => ({
      apiKey: provider.apiKey.trim() || undefined,
      endpoint: provider.endpoint.trim() || undefined,
      model: provider.model.trim(),
      providerName: provider.providerName.trim(),
      providerType: provider.providerType.trim(),
    })),
    runtimeBaseUrl: settings.runtimeBaseUrl,
  };
}

export function getOnboardingComposerPlaceholder(
  onboardingState: OnboardingState | null
): string {
  switch (onboardingState?.step) {
    case "select_provider":
      return "Reply with a provider number, like 1.";
    case "select_endpoint_mode":
      return "Reply with 1 for default or 2 for custom.";
    case "ask_custom_endpoint":
      return "Paste the custom endpoint URL.";
    case "ask_api_key":
      return "Paste the API key. It will be redacted in chat history.";
    case "creating":
      return "Saving provider configuration...";
    case "done":
      return "Type restart to configure another provider, or switch to NyxID Chat.";
    default:
      return "Reply to continue onboarding.";
  }
}

export function isValidOnboardingEndpoint(value: string): boolean {
  const normalized = value.trim();
  if (!normalized) {
    return false;
  }

  try {
    const url = new URL(normalized);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}

export function redactOnboardingSecret(value: string): string {
  const normalized = value.trim();
  if (!normalized) {
    return "API key provided";
  }

  const suffix = normalized.slice(-4);
  return suffix ? `API key provided (••••${suffix})` : "API key provided";
}
