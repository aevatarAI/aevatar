import { authFetch } from '@/shared/auth/fetch';
import type { ChannelServiceChoice } from './channelServicesApi';
import { ChannelApiError } from './channelsApi';
import {
  expectArray,
  expectBoolean,
  expectRecord,
  readString,
} from './http/decoders';

export interface SkillServiceRecommendation {
  readonly slug: string;
  readonly label: string;
  readonly evidence: 'linked' | 'catalog' | 'mention';
  readonly instances: readonly ChannelServiceChoice[];
}

export async function getChannelSkillServices(
  skillName: string,
  signal?: AbortSignal,
): Promise<SkillServiceRecommendation[]> {
  const params = new URLSearchParams({ skillName });
  const response = await authFetch(
    `/api/skills/service-recommendations?${params}`,
    {
      signal,
      cache: 'no-store',
    },
  );
  if (!response.ok) throw new ChannelApiError(response.status);
  const body = expectRecord(
    await response.json(),
    'Skill service recommendations',
  );
  if (body.skillName !== skillName)
    throw new Error('Mismatched skill recommendations.');
  const suggestions = expectArray<SkillServiceRecommendation>(
    body.suggestions,
    'Service suggestions',
    (value) => {
      const item = expectRecord(value, 'Service suggestion');
      const slug = readString(item, 'slug', 'Service slug');
      const label = readString(item, 'label', 'Service label');
      const evidence = item.evidence;
      if (
        !slug.trim() ||
        !label.trim() ||
        (evidence !== 'linked' &&
          evidence !== 'catalog' &&
          evidence !== 'mention')
      )
        throw new Error('Invalid service suggestion.');
      const instances = expectArray(
        item.instances,
        'Service instances',
        (entry): ChannelServiceChoice => {
          const instance = expectRecord(entry, 'Service instance');
          const id = readString(instance, 'id', 'UserService ID');
          const source = instance.source;
          if (
            !id.trim() ||
            instance.slug !== slug ||
            (source !== 'personal' &&
              source !== 'organization' &&
              source !== 'unknown')
          )
            throw new Error('Invalid service instance.');
          return {
            id,
            slug,
            source,
            label: readString(instance, 'label', 'Instance label'),
            active: expectBoolean(instance.active, 'Service activity'),
            allowed: expectBoolean(instance.allowed, 'Account access'),
            organizationName:
              readString(instance, 'organizationName', 'Organization name') ||
              null,
          };
        },
      );
      if (
        new Set(instances.map((instance) => instance.id)).size !==
        instances.length
      )
        throw new Error('Ambiguous service instances.');
      return { slug, label, evidence, instances };
    },
  );
  if (new Set(suggestions.map((item) => item.slug)).size !== suggestions.length)
    throw new Error('Ambiguous service suggestions.');
  return suggestions;
}
