import { ensureActiveAuthSession } from '@/shared/auth/client';
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

export interface ChannelSkillChoice {
  readonly id: string;
  readonly name: string;
  readonly description: string;
}

export async function searchChannelSkills(
  search: string,
  cursor?: string,
  signal?: AbortSignal,
) {
  const config = getNyxIDRuntimeConfig();
  if (config.configurationError || !config.baseUrl)
    throw new Error('NyxID is unavailable.');
  const session = await ensureActiveAuthSession();
  if (!session) throw new ChannelApiError(401);
  const url = new URL(
    `${config.baseUrl}/api/v1/proxy/s/ornn-api/api/v1/skill-search`,
  );
  url.search = new URLSearchParams({
    scope: 'private',
    mode: 'keyword',
    q: search,
    limit: '50',
    ...(cursor ? { cursor } : {}),
  }).toString();
  const response = await authFetch(url.href, {
    signal,
    credentials: 'omit',
    cache: 'no-store',
    headers: {
      Accept: 'application/json',
      Authorization: `Bearer ${session.tokens.accessToken}`,
    },
  });
  if (!response.ok) throw new ChannelApiError(response.status);
  const body = expectRecord(await response.json(), 'Skill search');
  if (body.error != null) throw new Error('Skill search failed.');
  const data = expectRecord(body.data, 'Skill results');
  const meta = expectRecord(data.meta, 'Skill pagination');
  const hasMore = expectBoolean(meta.hasMore, 'More skills');
  const nextCursor =
    readOptionalString(meta, 'nextCursor', 'Skill cursor') || undefined;
  if (hasMore && (!nextCursor || nextCursor === cursor))
    throw new Error('Invalid skill pagination.');
  const items = expectArray(
    data.items,
    'Skills',
    (value): ChannelSkillChoice => {
      const row = expectRecord(value, 'Skill');
      const id = readString(row, 'guid', 'Skill ID');
      const name = readString(row, 'name', 'Skill name');
      if (!id.trim() || !name.trim())
        throw new Error('Missing skill identity.');
      return {
        id,
        name,
        description:
          readOptionalString(row, 'description', 'Skill description') || '',
      };
    },
  );
  return { items, nextCursor: hasMore ? nextCursor : undefined };
}
