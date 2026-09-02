import type { NyxIDAuthSession } from '@/shared/auth/session';
import type {
  StudioAuthProfile,
  StudioAuthSession,
} from '@/shared/studio/models';

export type WorkflowActivityAccountPrincipal = {
  readonly authenticated: boolean;
  readonly displayName: string;
  readonly picture: string | null;
};

export type ResolvedWorkflowActivityAccount = {
  readonly auth: StudioAuthSession | undefined;
  readonly principal: WorkflowActivityAccountPrincipal | null;
};

function normalize(value: string | null | undefined): string | undefined {
  const normalized = value?.trim();
  return normalized || undefined;
}

function firstValue(
  ...values: readonly (string | null | undefined)[]
): string | undefined {
  return values.map(normalize).find(Boolean);
}

function storedPrincipal(
  storedSession: NyxIDAuthSession | null,
): WorkflowActivityAccountPrincipal | null {
  if (!storedSession) return null;
  return {
    authenticated: true,
    displayName:
      firstValue(
        storedSession.user.name,
        storedSession.user.email,
        storedSession.user.sub,
      ) || '',
    picture: normalize(storedSession.user.picture) || null,
  };
}

function resolveBackendProfile(
  auth: StudioAuthSession,
  subject: string | undefined,
): StudioAuthProfile | null | undefined {
  const profile = auth.profile;
  if (!profile) return profile;

  return {
    subject: subject || null,
    name: firstValue(profile.name, auth.name) || null,
    email: firstValue(profile.email, auth.email) || null,
    emailVerified: profile.emailVerified ?? null,
    picture: firstValue(profile.picture, auth.picture) || null,
    roles: profile.roles,
    groups: profile.groups,
  };
}

export function resolveWorkflowActivityAccount(
  auth: StudioAuthSession | undefined,
  storedSession: NyxIDAuthSession | null,
): ResolvedWorkflowActivityAccount {
  if (!auth) {
    return { auth: undefined, principal: storedPrincipal(storedSession) };
  }

  const subject = firstValue(auth.profile?.subject, auth.subject);
  const profile = resolveBackendProfile(auth, subject);
  const resolvedAuth: StudioAuthSession = {
    ...auth,
    subject: subject || null,
    name: firstValue(profile?.name, auth.name),
    email: firstValue(profile?.email, auth.email),
    picture: firstValue(profile?.picture, auth.picture),
    profile,
  };
  const authenticated =
    resolvedAuth.authenticated && resolvedAuth.session?.authenticated !== false;
  const browserPrincipal = storedPrincipal(storedSession);

  return {
    auth: resolvedAuth,
    principal: authenticated
      ? {
          authenticated: true,
          displayName:
            firstValue(
              resolvedAuth.profile?.name,
              resolvedAuth.name,
              resolvedAuth.profile?.email,
              resolvedAuth.email,
            ) || '',
          picture:
            firstValue(resolvedAuth.profile?.picture, resolvedAuth.picture) ||
            null,
        }
      : browserPrincipal,
  };
}
