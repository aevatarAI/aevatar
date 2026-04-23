import type { StudioUserConfig } from "./models";

export type StudioUserConfigRuntimeMode = "local" | "remote";

export const studioUserConfigLocalRuntimeMode: StudioUserConfigRuntimeMode = "local";
export const studioUserConfigRemoteRuntimeMode: StudioUserConfigRuntimeMode = "remote";
export const studioUserConfigLocalRuntimeBaseUrl = "http://127.0.0.1:5080";
export const studioUserConfigRemoteRuntimeBaseUrl =
  "https://aevatar-console-backend-api.aevatar.ai";

function trimRuntimeValue(value?: string | null): string | undefined {
  const normalized = String(value || "").trim();
  return normalized || undefined;
}

export function normalizeStudioUserConfigRuntimeMode(
  value?: string | null,
): StudioUserConfigRuntimeMode {
  return trimRuntimeValue(value)?.toLowerCase() === studioUserConfigRemoteRuntimeMode
    ? studioUserConfigRemoteRuntimeMode
    : studioUserConfigLocalRuntimeMode;
}

export function resolveStudioUserConfigRuntimeBaseUrl(
  config?: Pick<
    StudioUserConfig,
    "runtimeMode" | "localRuntimeBaseUrl" | "remoteRuntimeBaseUrl"
  > | null,
): string {
  const runtimeMode = normalizeStudioUserConfigRuntimeMode(config?.runtimeMode);
  const configuredValue =
    runtimeMode === studioUserConfigRemoteRuntimeMode
      ? trimRuntimeValue(config?.remoteRuntimeBaseUrl)
      : trimRuntimeValue(config?.localRuntimeBaseUrl);

  return configuredValue
    ? configuredValue.replace(/\/+$/, "")
    : runtimeMode === studioUserConfigRemoteRuntimeMode
      ? studioUserConfigRemoteRuntimeBaseUrl
      : studioUserConfigLocalRuntimeBaseUrl;
}

export function formatStudioUserConfigRuntimeModeLabel(
  runtimeMode: StudioUserConfigRuntimeMode,
): string {
  return runtimeMode === studioUserConfigRemoteRuntimeMode ? "Remote" : "Local";
}
