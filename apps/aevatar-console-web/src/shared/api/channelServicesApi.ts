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
  // This is account-level availability only. Current bearer access is checked
  // separately against NyxID's caller-scoped catalog before returning choices.
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
  const [body, catalog] = await Promise.all(
    ['/api/v1/user-services', '/api/v1/mcp/config'].map(async (path) => {
      const response = await authFetch(`${config.baseUrl}${path}`, {
        signal,
        credentials: 'omit',
        cache: 'no-store',
        headers: { Accept: 'application/json' },
      });
      if (!response.ok) throw new ChannelApiError(response.status);
      return expectRecord(await response.json(), 'NyxID service inventory');
    }),
  );
  // The account inventory does not enforce OAuth service grants. The v1 MCP
  // catalog does, and only is_user_service rows carry exact UserService IDs.
  if (catalog.contract_version !== '1.0')
    throw new Error('Unsupported NyxID service catalog.');
  const catalogServices = expectArray(
    catalog.services,
    'Service catalog',
    (value) => {
      const row = expectRecord(value, 'Catalog service');
      const id = readString(row, 'service_id', 'Catalog service ID');
      if (!id.trim()) throw new Error('Missing catalog service identity.');
      return {
        id,
        userService: expectBoolean(
          row.is_user_service,
          'User service identity',
        ),
      };
    },
  );
  if (
    new Set(catalogServices.map((service) => service.id)).size !==
    catalogServices.length
  )
    throw new Error('Ambiguous catalog service identity.');
  const authorizedIds = new Set(
    catalogServices
      .filter((service) => service.userService)
      .map((service) => service.id),
  );
  const services = expectArray(body.services, 'User services', decodeService);
  if (new Set(services.map((service) => service.id)).size !== services.length)
    throw new Error('Ambiguous user service identity.');
  return services.filter(
    (service) =>
      service.active && service.allowed && authorizedIds.has(service.id),
  );
}
