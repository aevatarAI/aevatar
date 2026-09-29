import { loadRestorableAuthSession } from '@/shared/auth/session';

export type ChannelAccessDraftTarget =
  | { readonly kind: 'bind'; readonly botId: string }
  | { readonly kind: 'edit'; readonly registrationId: string };

export interface ChannelAccessDraft {
  readonly label: string;
  readonly skillName: string;
  readonly serviceIds: readonly string[];
}

function storageKey(scopeId: string, target: ChannelAccessDraftTarget): string {
  const subject = loadRestorableAuthSession()?.user.sub;
  if (!subject) throw new Error('A signed-in account is required.');
  const id = target.kind === 'bind' ? target.botId : target.registrationId;
  return `aevatar:channel-access-draft:${JSON.stringify([subject, scopeId, target.kind, id])}`;
}

export function saveChannelAccessDraft(
  scopeId: string,
  target: ChannelAccessDraftTarget,
  draft: ChannelAccessDraft,
): void {
  window.sessionStorage.setItem(
    storageKey(scopeId, target),
    JSON.stringify({ ...draft, expiresAt: Date.now() + 60 * 60 * 1000 }),
  );
}

export function clearChannelAccessDraft(
  scopeId: string,
  target: ChannelAccessDraftTarget,
): void {
  try {
    window.sessionStorage.removeItem(storageKey(scopeId, target));
  } catch {
    // A blocked browser store must not prevent leaving or saving the editor.
  }
}

export function readChannelAccessDraft(
  scopeId: string,
  target: ChannelAccessDraftTarget,
): ChannelAccessDraft | null {
  try {
    const raw = window.sessionStorage.getItem(storageKey(scopeId, target));
    if (!raw) return null;
    const draft: unknown = JSON.parse(raw);
    if (!draft || typeof draft !== 'object') return null;
    if (
      !('expiresAt' in draft) ||
      typeof draft.expiresAt !== 'number' ||
      draft.expiresAt <= Date.now()
    ) {
      clearChannelAccessDraft(scopeId, target);
      return null;
    }
    if (
      !('label' in draft) ||
      typeof draft.label !== 'string' ||
      !('skillName' in draft) ||
      typeof draft.skillName !== 'string' ||
      !('serviceIds' in draft) ||
      !Array.isArray(draft.serviceIds) ||
      !draft.serviceIds.every((id): id is string => typeof id === 'string')
    )
      return null;
    return {
      label: draft.label,
      skillName: draft.skillName,
      serviceIds: draft.serviceIds,
    };
  } catch {
    return null;
  }
}
