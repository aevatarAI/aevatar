import type { ScriptDraft } from './models';

export function formatDateTime(value: string | null | undefined) {
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

export function isScopeDetailDirty(draft: ScriptDraft | null) {
  if (!draft?.scopeDetail?.source) {
    return false;
  }

  const savedSource = draft.scopeDetail.source.sourceText || '';
  const savedRevision = draft.scopeDetail.script?.activeRevision || draft.scopeDetail.source.revision || '';
  return savedSource !== draft.source || (savedRevision && savedRevision !== draft.revision);
}
