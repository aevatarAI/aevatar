export interface TeamCreateDraftPointer {
  scopeId: string;
  teamName: string;
  entryName: string;
  teamDraftWorkflowId: string;
  teamDraftWorkflowName: string;
  sourceBehaviorDefinitionId: string;
  sourceBehaviorDefinitionName: string;
  updatedAt: string;
}

type TeamCreateDraftPointerStore = {
  drafts: TeamCreateDraftPointer[];
  selectedWorkflowId: string;
};

const STORAGE_KEY = 'aevatar-console-team-create-draft-pointer';

export const defaultTeamCreateDraftPointer: TeamCreateDraftPointer = {
  scopeId: '',
  teamName: '',
  entryName: '',
  teamDraftWorkflowId: '',
  teamDraftWorkflowName: '',
  sourceBehaviorDefinitionId: '',
  sourceBehaviorDefinitionName: '',
  updatedAt: '',
};

const defaultTeamCreateDraftPointerStore: TeamCreateDraftPointerStore = {
  drafts: [],
  selectedWorkflowId: '',
};

function sanitizePointer(
  value: Partial<TeamCreateDraftPointer> | null | undefined,
): TeamCreateDraftPointer {
  return {
    scopeId: typeof value?.scopeId === 'string' ? value.scopeId.trim() : '',
    teamName: typeof value?.teamName === 'string' ? value.teamName.trim() : '',
    entryName: typeof value?.entryName === 'string' ? value.entryName.trim() : '',
    teamDraftWorkflowId:
      typeof value?.teamDraftWorkflowId === 'string'
        ? value.teamDraftWorkflowId.trim()
        : '',
    teamDraftWorkflowName:
      typeof value?.teamDraftWorkflowName === 'string'
        ? value.teamDraftWorkflowName.trim()
        : '',
    sourceBehaviorDefinitionId:
      typeof value?.sourceBehaviorDefinitionId === 'string'
        ? value.sourceBehaviorDefinitionId.trim()
        : '',
    sourceBehaviorDefinitionName:
      typeof value?.sourceBehaviorDefinitionName === 'string'
        ? value.sourceBehaviorDefinitionName.trim()
        : '',
    updatedAt: typeof value?.updatedAt === 'string' ? value.updatedAt : '',
  };
}

function comparePointers(
  left: TeamCreateDraftPointer,
  right: TeamCreateDraftPointer,
): number {
  const rightTime = Date.parse(right.updatedAt || '');
  const leftTime = Date.parse(left.updatedAt || '');
  const normalizedRightTime = Number.isFinite(rightTime) ? rightTime : 0;
  const normalizedLeftTime = Number.isFinite(leftTime) ? leftTime : 0;

  if (normalizedRightTime !== normalizedLeftTime) {
    return normalizedRightTime - normalizedLeftTime;
  }

  return right.teamDraftWorkflowId.localeCompare(left.teamDraftWorkflowId);
}

function normalizeScopeId(scopeId?: string): string {
  return scopeId?.trim() ?? '';
}

function filterPointersByScope(
  drafts: TeamCreateDraftPointer[],
  scopeId?: string,
): TeamCreateDraftPointer[] {
  const normalizedScopeId = normalizeScopeId(scopeId);
  if (!normalizedScopeId) {
    return drafts;
  }

  return drafts.filter((item) => item.scopeId === normalizedScopeId);
}

function sanitizePointers(
  values: Array<Partial<TeamCreateDraftPointer> | null | undefined>,
): TeamCreateDraftPointer[] {
  const deduped = new Map<string, TeamCreateDraftPointer>();

  values.forEach((value) => {
    const pointer = sanitizePointer(value);
    if (!pointer.teamDraftWorkflowId) {
      return;
    }

    deduped.set(
      `${pointer.scopeId}::${pointer.teamDraftWorkflowId}`,
      pointer,
    );
  });

  return Array.from(deduped.values()).sort(comparePointers);
}

function sanitizeStore(
  value: Partial<TeamCreateDraftPointerStore> | Partial<TeamCreateDraftPointer> | null | undefined,
): TeamCreateDraftPointerStore {
  const legacyPointer = sanitizePointer(value as Partial<TeamCreateDraftPointer>);
  const drafts = Array.isArray((value as Partial<TeamCreateDraftPointerStore>)?.drafts)
    ? sanitizePointers(
        (value as Partial<TeamCreateDraftPointerStore>).drafts as Array<
          Partial<TeamCreateDraftPointer> | null | undefined
        >,
      )
    : legacyPointer.teamDraftWorkflowId
      ? [legacyPointer]
      : [];
  const selectedWorkflowId =
    typeof (value as Partial<TeamCreateDraftPointerStore>)?.selectedWorkflowId ===
    'string'
      ? (value as Partial<TeamCreateDraftPointerStore>).selectedWorkflowId?.trim() || ''
      : '';

  return {
    drafts,
    selectedWorkflowId:
      drafts.find((item) => item.teamDraftWorkflowId === selectedWorkflowId)
        ?.teamDraftWorkflowId || drafts[0]?.teamDraftWorkflowId || '',
  };
}

function loadStore(): TeamCreateDraftPointerStore {
  if (typeof window === 'undefined') {
    return defaultTeamCreateDraftPointerStore;
  }

  const raw = window.localStorage.getItem(STORAGE_KEY);
  if (!raw) {
    return defaultTeamCreateDraftPointerStore;
  }

  try {
    return sanitizeStore(
      JSON.parse(raw) as Partial<TeamCreateDraftPointerStore> | Partial<TeamCreateDraftPointer>,
    );
  } catch {
    return defaultTeamCreateDraftPointerStore;
  }
}

function saveStore(store: TeamCreateDraftPointerStore): TeamCreateDraftPointerStore {
  const next = sanitizeStore(store);
  if (typeof window !== 'undefined') {
    if (next.drafts.length === 0) {
      window.localStorage.removeItem(STORAGE_KEY);
    } else {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    }
  }

  return next;
}

export function hasTeamCreateDraftPointer(
  value: Partial<TeamCreateDraftPointer> | null | undefined,
): boolean {
  return Boolean(sanitizePointer(value).teamDraftWorkflowId);
}

export function loadTeamCreateDraftPointers(scopeId?: string): TeamCreateDraftPointer[] {
  return filterPointersByScope(loadStore().drafts, scopeId);
}

export function loadAllTeamCreateDraftPointers(): TeamCreateDraftPointer[] {
  return loadStore().drafts;
}

export function countTeamCreateDraftPointersOutsideScope(scopeId?: string): number {
  const normalizedScopeId = normalizeScopeId(scopeId);
  if (!normalizedScopeId) {
    return 0;
  }

  return loadStore().drafts.filter((item) => item.scopeId !== normalizedScopeId).length;
}

export function loadTeamCreateDraftPointer(scopeId?: string): TeamCreateDraftPointer {
  const scopedDrafts = loadTeamCreateDraftPointers(scopeId);
  const store = loadStore();
  return (
    scopedDrafts.find(
      (item) => item.teamDraftWorkflowId === store.selectedWorkflowId,
    ) || scopedDrafts[0] || defaultTeamCreateDraftPointer
  );
}

export function findTeamCreateDraftPointer(
  workflowId: string,
  scopeId?: string,
): TeamCreateDraftPointer | null {
  const normalizedWorkflowId = workflowId.trim();
  if (!normalizedWorkflowId) {
    return null;
  }

  return (
    loadTeamCreateDraftPointers(scopeId).find(
      (item) => item.teamDraftWorkflowId === normalizedWorkflowId,
    ) || null
  );
}

export function saveTeamCreateDraftPointer(
  value: Partial<TeamCreateDraftPointer>,
): TeamCreateDraftPointer {
  const nextPointer = sanitizePointer({
    ...value,
    updatedAt: new Date().toISOString(),
  });

  if (!hasTeamCreateDraftPointer(nextPointer)) {
    return defaultTeamCreateDraftPointer;
  }

  const currentStore = loadStore();
  const currentDrafts = currentStore.drafts.filter(
    (item) =>
      !(
        item.teamDraftWorkflowId === nextPointer.teamDraftWorkflowId &&
        item.scopeId === nextPointer.scopeId
      ),
  );
  const nextStore = saveStore({
    drafts: [nextPointer, ...currentDrafts],
    selectedWorkflowId: nextPointer.teamDraftWorkflowId,
  });

  return (
    nextStore.drafts.find(
      (item) => item.teamDraftWorkflowId === nextStore.selectedWorkflowId,
    ) || defaultTeamCreateDraftPointer
  );
}

export function selectTeamCreateDraftPointer(
  workflowId: string,
  scopeId?: string,
): TeamCreateDraftPointer {
  const normalizedWorkflowId = workflowId.trim();
  if (!normalizedWorkflowId) {
    return loadTeamCreateDraftPointer(scopeId);
  }

  const scopedDrafts = loadTeamCreateDraftPointers(scopeId);
  const nextStore = saveStore({
    drafts: loadStore().drafts,
    selectedWorkflowId:
      scopedDrafts.find((item) => item.teamDraftWorkflowId === normalizedWorkflowId)
        ?.teamDraftWorkflowId || '',
  });
  const nextScopedDrafts = filterPointersByScope(nextStore.drafts, scopeId);

  return (
    nextScopedDrafts.find(
      (item) => item.teamDraftWorkflowId === nextStore.selectedWorkflowId,
    ) || nextScopedDrafts[0] || defaultTeamCreateDraftPointer
  );
}

export function resetTeamCreateDraftPointer(
  workflowId?: string,
  scopeId?: string,
): TeamCreateDraftPointer {
  const normalizedWorkflowId = workflowId?.trim() ?? '';
  const normalizedScopeId = normalizeScopeId(scopeId);
  if (!normalizedWorkflowId) {
    if (!normalizedScopeId) {
      saveStore(defaultTeamCreateDraftPointerStore);
      return defaultTeamCreateDraftPointer;
    }

    const remainingDrafts = loadStore().drafts.filter(
      (item) => item.scopeId !== normalizedScopeId,
    );
    const nextStore = saveStore({
      drafts: remainingDrafts,
      selectedWorkflowId: loadStore().selectedWorkflowId,
    });
    const nextScopedDrafts = filterPointersByScope(nextStore.drafts, normalizedScopeId);
    return nextScopedDrafts[0] || defaultTeamCreateDraftPointer;
  }

  const currentStore = loadStore();
  const remainingDrafts = currentStore.drafts.filter(
    (item) =>
      !(
        item.teamDraftWorkflowId === normalizedWorkflowId &&
        (!normalizedScopeId || item.scopeId === normalizedScopeId)
      ),
  );
  const nextStore = saveStore({
    drafts: remainingDrafts,
    selectedWorkflowId:
      currentStore.selectedWorkflowId === normalizedWorkflowId
        ? remainingDrafts[0]?.teamDraftWorkflowId || ''
        : currentStore.selectedWorkflowId,
  });
  const nextScopedDrafts = filterPointersByScope(nextStore.drafts, scopeId);

  return (
    nextScopedDrafts.find(
      (item) => item.teamDraftWorkflowId === nextStore.selectedWorkflowId,
    ) || nextScopedDrafts[0] || defaultTeamCreateDraftPointer
  );
}
