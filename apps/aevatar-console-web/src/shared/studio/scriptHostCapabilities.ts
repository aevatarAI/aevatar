import type { StudioAppContext } from './models';
import { t } from "@/shared/i18n/messages";

export type EmbeddedOnlyCapability = 'ask-ai' | 'draft-run';

export function formatStudioHostModeLabel(mode: StudioAppContext['mode']): string {
  return mode === 'embedded' ? t("shared.studio.scripthostcapabilities.embedded.host", "Embedded Host") : t("shared.studio.scripthostcapabilities.proxy.host", "Proxy Host");
}

export function getStudioHostModeTooltip(mode: StudioAppContext['mode']): string {
  if (mode === 'embedded') {
    return t("shared.studio.scripthostcapabilities.the.current.studio.session", "The current Studio session runs in the embedded Host and can be tested and run directly or modified using AI-assisted generation scripts.");
  }

  return t("shared.studio.scripthostcapabilities.the.current.studio.session.2", "The current Studio session is running on the proxy Host. You can continue to verify, save and publish here, but test running and AI assistance need to switch to the embedded Host.");
}

export function getEmbeddedOnlyUnavailableMessage(
  capability: EmbeddedOnlyCapability,
): string {
  if (capability === 'draft-run') {
    return t("shared.studio.scripthostcapabilities.test.running.requires.an", "Test running requires an embedded host. Please switch the current Studio session from proxy mode to embedded mode before running this draft.");
  }

  return t("shared.studio.scripthostcapabilities.ai.assistance.requires.an", "AI assistance requires an embedded host. Please switch the current Studio session from proxy mode to embedded mode before generating script modifications.");
}
