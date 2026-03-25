import type { ScriptDraft } from './scriptsModels';
import { serializePersistedSource } from './scriptPackage';

export function formatScriptDateTime(
  value: string | null | undefined,
): string {
  if (!value) {
    return '-';
  }

  return new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value));
}

export function isScopeDetailDirty(draft: ScriptDraft | null): boolean {
  if (!draft?.scopeDetail?.source) {
    return false;
  }

  const savedSource = draft.scopeDetail.source.sourceText || '';
  const savedRevision =
    draft.scopeDetail.script?.activeRevision ||
    draft.scopeDetail.source.revision ||
    '';
  return Boolean(
    savedSource !== serializePersistedSource(draft.package) ||
      (savedRevision && savedRevision !== draft.revision),
  );
}
