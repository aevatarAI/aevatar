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

async function requestSkillData(path: string, signal?: AbortSignal) {
  const config = getNyxIDRuntimeConfig();
  if (config.configurationError || !config.baseUrl)
    throw new Error('NyxID is unavailable.');
  const session = await ensureActiveAuthSession();
  if (!session) throw new ChannelApiError(401);
  const url = new URL(
    `${config.baseUrl}/api/v1/proxy/s/ornn-api/api/v1/${path}`,
  );
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
  const body = expectRecord(await response.json(), 'Skill response');
  if (body.error != null) throw new Error('Could not load skill data.');
  return expectRecord(body.data, 'Skill data');
}

function readSkill(value: unknown): ChannelSkillChoice {
  const row = expectRecord(value, 'Skill');
  const id = readString(row, 'guid', 'Skill ID');
  const name = readString(row, 'name', 'Skill name');
  if (!id.trim() || !name.trim()) throw new Error('Missing skill identity.');
  return {
    id,
    name,
    description:
      readOptionalString(row, 'description', 'Skill description') || '',
  };
}

export async function getChannelSkillById(id: string, signal?: AbortSignal) {
  if (!id.trim() || id.length > 256 || id === '.' || id === '..')
    throw new Error('Invalid skill ID.');
  const skill = readSkill(
    await requestSkillData(`skills/${encodeURIComponent(id)}`, signal),
  );
  // Ornn also resolves names on this endpoint. A link must resolve its exact ID.
  if (skill.id !== id || skill.name.trim().length > 128)
    throw new Error('Invalid skill identity.');
  return skill;
}

export async function searchChannelSkills(
  search: string,
  cursor?: string,
  signal?: AbortSignal,
) {
  const params = new URLSearchParams({
    scope: 'private',
    mode: 'keyword',
    q: search,
    limit: '50',
    ...(cursor ? { cursor } : {}),
  });
  const data = await requestSkillData(`skill-search?${params}`, signal);
  const meta = expectRecord(data.meta, 'Skill pagination');
  const hasMore = expectBoolean(meta.hasMore, 'More skills');
  const nextCursor =
    readOptionalString(meta, 'nextCursor', 'Skill cursor') || undefined;
  if (hasMore && (!nextCursor || nextCursor === cursor))
    throw new Error('Invalid skill pagination.');
  const items = expectArray(data.items, 'Skills', readSkill);
  return { items, nextCursor: hasMore ? nextCursor : undefined };
}
