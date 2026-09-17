import type { StudioAuthSession } from '@/shared/studio/models';

export type AccountSessionState =
  | 'active'
  | 'expiring_soon'
  | 'expired'
  | 'invalid';

export type AccountField =
  | { readonly kind: 'value'; readonly value: string }
  | { readonly kind: 'not_provided' };

export type AccountIdentity = {
  readonly displayName: AccountField;
  readonly email: AccountField;
  readonly emailVerified: boolean | null;
  readonly expiry: AccountField;
  readonly picture: string | null;
  readonly provider: AccountField;
  readonly scope: AccountField;
  readonly sessionState: AccountSessionState;
  readonly support: {
    readonly groups: readonly string[];
    readonly roles: readonly string[];
    readonly subject: string | null;
  };
};

const EXPIRING_SOON_WINDOW_MS = 24 * 60 * 60 * 1000;

function valueOrMissing(value: string | null | undefined): AccountField {
  const normalized = value?.trim();
  return normalized
    ? { kind: 'value', value: normalized }
    : { kind: 'not_provided' };
}

function formatRelativeExpiry(
  expiryMs: number,
  nowMs: number,
  locale: string,
): string {
  const differenceMs = expiryMs - nowMs;
  const absoluteDifference = Math.abs(differenceMs);
  const [divisor, unit] =
    absoluteDifference >= 24 * 60 * 60 * 1000
      ? [24 * 60 * 60 * 1000, 'day' as const]
      : absoluteDifference >= 60 * 60 * 1000
        ? [60 * 60 * 1000, 'hour' as const]
        : [60 * 1000, 'minute' as const];
  const amount = Math.round(differenceMs / divisor);
  return new Intl.RelativeTimeFormat(locale, { numeric: 'auto' }).format(
    amount,
    unit,
  );
}

function formatExpiry(
  value: string | null | undefined,
  nowMs: number,
  locale: string,
): AccountField {
  if (!value?.trim()) return { kind: 'not_provided' };
  const expiryMs = Date.parse(value);
  if (Number.isNaN(expiryMs)) return { kind: 'not_provided' };
  const localTime = new Intl.DateTimeFormat(locale, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
    timeZoneName: 'short',
  }).format(expiryMs);
  return {
    kind: 'value',
    value: `${localTime} · ${formatRelativeExpiry(expiryMs, nowMs, locale)}`,
  };
}

function resolveSessionState(
  auth: StudioAuthSession,
  nowMs: number,
): AccountSessionState {
  const expiryValue = auth.session?.expiresAtUtc;
  const expiryMs = expiryValue ? Date.parse(expiryValue) : Number.NaN;
  if (!Number.isNaN(expiryMs) && expiryMs <= nowMs) return 'expired';
  if (!auth.authenticated || auth.session?.authenticated === false)
    return 'invalid';
  if (!Number.isNaN(expiryMs) && expiryMs - nowMs <= EXPIRING_SOON_WINDOW_MS)
    return 'expiring_soon';
  return 'active';
}

export function buildAccountIdentity(
  auth: StudioAuthSession,
  nowMs: number,
  locale: string,
): AccountIdentity {
  const profileName = auth.profile?.name || auth.name;
  const profileEmail = auth.profile?.email || auth.email;

  return {
    displayName: valueOrMissing(profileName),
    email: valueOrMissing(profileEmail),
    emailVerified: auth.profile?.emailVerified ?? null,
    expiry: formatExpiry(auth.session?.expiresAtUtc, nowMs, locale),
    picture: auth.profile?.picture || auth.picture || null,
    provider: valueOrMissing(
      auth.session?.providerDisplayName || auth.providerDisplayName,
    ),
    scope: valueOrMissing(auth.session?.scopeId || auth.scopeId),
    sessionState: resolveSessionState(auth, nowMs),
    support: {
      groups: auth.profile?.groups ?? [],
      roles: auth.profile?.roles ?? [],
      subject: auth.profile?.subject?.trim() || auth.subject?.trim() || null,
    },
  };
}
