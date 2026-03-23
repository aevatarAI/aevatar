export type ScopeQueryDraft = {
  scopeId: string;
};

function readString(value: string | null): string {
  return value?.trim() ?? '';
}

export function normalizeScopeDraft(draft: ScopeQueryDraft): ScopeQueryDraft {
  return {
    scopeId: draft.scopeId.trim(),
  };
}

export function readScopeQueryDraft(
  search = typeof window === 'undefined' ? '' : window.location.search,
): ScopeQueryDraft {
  const params = new URLSearchParams(search);
  return {
    scopeId: readString(params.get('scopeId')),
  };
}

function buildScopeParams(
  draft: ScopeQueryDraft,
  extras?: Record<string, string | null | undefined>,
): URLSearchParams {
  const params = new URLSearchParams();

  if (draft.scopeId.trim()) {
    params.set('scopeId', draft.scopeId.trim());
  }

  for (const [key, value] of Object.entries(extras ?? {})) {
    const normalized = value?.trim();
    if (normalized) {
      params.set(key, normalized);
    }
  }

  return params;
}

export function buildScopeHref(
  path: string,
  draft: ScopeQueryDraft,
  extras?: Record<string, string | null | undefined>,
): string {
  const suffix = buildScopeParams(draft, extras).toString();
  return suffix ? `${path}?${suffix}` : path;
}
