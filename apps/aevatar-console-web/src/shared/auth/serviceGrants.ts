import {
  expectArray,
  expectBoolean,
  expectRecord,
  expectString,
} from '@/shared/api/http/decoders';

export interface NyxIDServiceGrants {
  readonly allowAllServices: boolean;
  readonly allowedServiceIds: readonly string[];
}

// Display filtering only: this does not verify a JWT signature or authorize an
// operation. NyxID validates the same bearer on the inventory request, and the
// registration endpoint remains responsible for enforcing delegation limits.
export function readAccessTokenServiceGrants(
  accessToken: string,
  expectedSubject: string,
): NyxIDServiceGrants {
  try {
    const parts = accessToken.split('.');
    if (parts.length !== 3 || parts.some((part) => !part)) throw new Error();
    const payload = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const bytes = Uint8Array.from(atob(payload), (character) =>
      character.charCodeAt(0),
    );
    const claims = expectRecord(
      JSON.parse(new TextDecoder().decode(bytes)),
      'Access claims',
    );
    if (
      claims.token_type !== 'access' ||
      claims.relay === true ||
      !expectedSubject ||
      claims.sub !== expectedSubject ||
      typeof claims.exp !== 'number' ||
      !Number.isFinite(claims.exp) ||
      claims.exp * 1000 <= Date.now()
    )
      throw new Error();
    const allowAllServices = expectBoolean(
      claims.allow_all_services,
      'Service grant mode',
    );
    const allowedServiceIds = expectArray(
      claims.allowed_service_ids,
      'Service grants',
      (value) => {
        const id = expectString(value, 'User service ID');
        if (!id.trim()) throw new Error();
        return id;
      },
    );
    return { allowAllServices, allowedServiceIds };
  } catch {
    // JSON/parser errors must never expose any part of the bearer payload.
    throw new Error('Could not read the current NyxID service authorization.');
  }
}
