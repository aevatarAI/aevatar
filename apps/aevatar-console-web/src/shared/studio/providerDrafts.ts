import type {
  StudioProviderSettings,
  StudioProviderType,
} from "./models";

const fallbackProviderType: StudioProviderType = {
  category: "llm",
  defaultEndpoint: "",
  defaultModel: "",
  description: "",
  displayName: "OpenAI",
  id: "openai",
  recommended: true,
};

function pickProviderType(
  providerTypes: readonly StudioProviderType[],
  preferredTypeId?: string | null
): StudioProviderType {
  const normalizedPreferredTypeId = preferredTypeId?.trim().toLowerCase();
  if (normalizedPreferredTypeId) {
    const preferredMatch = providerTypes.find(
      (providerType) =>
        providerType.id.trim().toLowerCase() === normalizedPreferredTypeId
    );
    if (preferredMatch) {
      return preferredMatch;
    }
  }

  return (
    providerTypes.find((providerType) => providerType.recommended) ||
    providerTypes[0] ||
    fallbackProviderType
  );
}

export function createProviderDraft(
  providerTypes: readonly StudioProviderType[],
  existingProviders: readonly StudioProviderSettings[],
  preferredTypeId?: string | null
): StudioProviderSettings {
  const preferredType = pickProviderType(providerTypes, preferredTypeId);
  const used = new Set(
    existingProviders.map((provider) => provider.providerName.trim().toLowerCase())
  );
  const baseName = preferredType.id || "provider";
  let index = 1;
  let nextName = `${baseName}-${index}`;
  while (used.has(nextName.toLowerCase())) {
    index += 1;
    nextName = `${baseName}-${index}`;
  }

  return {
    apiKey: "",
    apiKeyConfigured: false,
    category: preferredType.category,
    clearApiKeyRequested: false,
    description: preferredType.description,
    displayName: preferredType.displayName,
    endpoint: preferredType.defaultEndpoint,
    model: preferredType.defaultModel,
    providerName: nextName,
    providerType: preferredType.id,
  };
}
