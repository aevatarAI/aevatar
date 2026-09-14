import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { authFetch } from '@/shared/auth/fetch';
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

function decodeService(value: unknown): ChannelServiceChoice {
  const row = expectRecord(value, 'User service');
  const source = expectRecord(row.credential_source, 'Credential source');
  const id = readString(row, 'id', 'User service ID');
  const slug = readString(row, 'slug', 'Service slug');
  if (!id.trim() || !slug.trim()) throw new Error('Missing service identity.');
  const personal = source.type === 'personal';
  const organization = source.type === 'org';
  // Only the documented personal variant has implicit permission. Unknown
  // variants and organization rows without allowed=true cannot be selected.
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

export async function listChannelServices(
  signal?: AbortSignal,
): Promise<ChannelServiceChoice[]> {
  const config = getNyxIDRuntimeConfig();
  if (config.configurationError || !config.baseUrl)
    throw new Error('NyxID is unavailable.');
  const response = await authFetch(`${config.baseUrl}/api/v1/user-services`, {
    signal,
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) throw new ChannelApiError(response.status);
  const body = expectRecord(await response.json(), 'User services');
  const services = expectArray(body.services, 'User services', decodeService);
  if (new Set(services.map((service) => service.id)).size !== services.length)
    throw new Error('Ambiguous user service identity.');
  return services;
}
