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

export interface ChannelSkillServiceContext {
  readonly name: string;
  readonly description: string;
  readonly instructions: string;
  readonly linkedServiceSlug: string | null;
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

// Registrations store a skill name. Resolve that name through Ornn, then read
// its package by the returned GUID; service associations are discovery hints,
// never UserService identities or grants.
export async function getChannelSkillServiceContext(
  name: string,
  signal?: AbortSignal,
): Promise<ChannelSkillServiceContext> {
  if (!name.trim() || name.length > 128 || name === '.' || name === '..')
    throw new Error('Invalid skill name.');
  const detail = await requestSkillData(
    `skills/${encodeURIComponent(name)}`,
    signal,
  );
  const skill = readSkill(detail);
  if (skill.name !== name || skill.id === '.' || skill.id === '..')
    throw new Error('Invalid skill identity.');
  const data = await requestSkillData(
    `skills/${encodeURIComponent(skill.id)}/json`,
    signal,
  );
  if (readString(data, 'name', 'Skill name') !== name)
    throw new Error('Skill identity changed.');
  const files = expectRecord(data.files, 'Skill files');
  const instructions = readString(files, 'SKILL.md', 'Skill instructions');
  if (instructions.length > 200_000)
    throw new Error('Skill instructions exceed the discovery limit.');
  return {
    name,
    description: skill.description,
    instructions,
    linkedServiceSlug:
      readOptionalString(
        detail,
        'nyxidServiceSlug',
        'Linked service slug',
      )?.trim() || null,
  };
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
