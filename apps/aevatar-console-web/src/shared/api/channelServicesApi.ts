import { ensureActiveAuthSession } from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { authFetch } from '@/shared/auth/fetch';
import { readAccessTokenServiceGrants } from '@/shared/auth/serviceGrants';
import { ChannelApiError } from './channelsApi';
import {
  expectArray,
  expectBoolean,
  expectRecord,
  readOptionalString,
  readString,
} from './http/decoders';

export interface ChannelServiceChoice {
  readonly id: string;
  readonly slug: string;
  readonly label: string;
  readonly active: boolean;
  readonly allowed: boolean;
  readonly source: 'personal' | 'organization' | 'unknown';
  readonly organizationName: string | null;
}

export type ChannelServiceIdentity = Pick<
  ChannelServiceChoice,
  'id' | 'slug' | 'label'
>;

function decodeService(value: unknown): ChannelServiceChoice {
  const row = expectRecord(value, 'User service');
  const source = expectRecord(row.credential_source, 'Credential source');
  const id = readString(row, 'id', 'User service ID');
  const slug = readString(row, 'slug', 'Service slug');
  if (!id.trim() || !slug.trim()) throw new Error('Missing service identity.');
  const personal = source.type === 'personal';
  const organization = source.type === 'org';
  // This is account-level availability only. Current bearer access is checked
  // separately against the current access token before returning choices.
  return {
    id,
    slug,
    label:
      readOptionalString(row, 'label', 'Service label')?.trim() ||
      readOptionalString(row, 'catalog_service_name', 'Catalog name')?.trim() ||
      slug,
    active: expectBoolean(row.is_active, 'Service activity'),
    allowed: personal || (organization && source.allowed === true),
    source: personal ? 'personal' : organization ? 'organization' : 'unknown',
    organizationName: organization
      ? readOptionalString(source, 'org_name', 'Organization name') || null
      : null,
  };
}

async function readChannelServiceInventory(
  accessToken: string,
  signal?: AbortSignal,
): Promise<ChannelServiceChoice[]> {
  const config = getNyxIDRuntimeConfig();
  if (config.configurationError || !config.baseUrl)
    throw new Error('NyxID is unavailable.');
  const response = await authFetch(`${config.baseUrl}/api/v1/user-services`, {
    signal,
    credentials: 'omit',
    cache: 'no-store',
    headers: {
      Accept: 'application/json',
      Authorization: `Bearer ${accessToken}`,
    },
  });
  if (!response.ok) throw new ChannelApiError(response.status);
  const body = expectRecord(await response.json(), 'User services');
  const services = expectArray(body.services, 'User services', decodeService);
  if (new Set(services.map((service) => service.id)).size !== services.length)
    throw new Error('Ambiguous user service identity.');
  return services;
}

// Display names do not establish a channel's authorization. The registration's
// saved service IDs remain authoritative even if this session's grants changed.
export async function listChannelServiceIdentities(
  signal?: AbortSignal,
): Promise<ChannelServiceIdentity[]> {
  const session = await ensureActiveAuthSession();
  if (!session) throw new ChannelApiError(401);
  const services = await readChannelServiceInventory(
    session.tokens.accessToken,
    signal,
  );
  return services.map(({ id, slug, label }) => ({ id, slug, label }));
}

export async function listChannelServices(
  signal?: AbortSignal,
): Promise<ChannelServiceChoice[]> {
  const session = await ensureActiveAuthSession();
  if (!session) throw new ChannelApiError(401);
  const grants = readAccessTokenServiceGrants(
    session.tokens.accessToken,
    session.user.sub,
  );
  // Pin inventory and selectable choices to the same authenticated bearer.
  const services = await readChannelServiceInventory(
    session.tokens.accessToken,
    signal,
  );
  const authorizedIds = new Set(grants.allowedServiceIds);
  return services.filter(
    (service) =>
      service.active &&
      service.allowed &&
      (grants.allowAllServices || authorizedIds.has(service.id)),
  );
}
