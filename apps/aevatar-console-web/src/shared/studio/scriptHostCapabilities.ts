import type { StudioAppContext } from './models';

export type EmbeddedOnlyCapability = 'ask-ai' | 'draft-run';

export function formatStudioHostModeLabel(mode: StudioAppContext['mode']): string {
  return mode === 'embedded' ? 'Embedded' : 'Proxy';
}

export function getStudioHostModeTooltip(mode: StudioAppContext['mode']): string {
  if (mode === 'embedded') {
    return 'Embedded host. Draft Run and Ask AI are available in this Studio session.';
  }

  return 'Proxy host. Validate is available here, and Save or Promote still work after Studio resolves the current scope. Draft Run and Ask AI require an embedded host.';
}

export function getEmbeddedOnlyUnavailableMessage(
  capability: EmbeddedOnlyCapability,
): string {
  if (capability === 'draft-run') {
    return 'Draft Run requires an embedded host. Switch this Studio session from proxy to embedded to run the current draft package.';
  }

  return 'Ask AI requires an embedded host. Switch this Studio session from proxy to embedded to generate script changes.';
}
