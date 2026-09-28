import type {
  ChannelServiceCatalogEntry,
  ChannelServiceChoice,
} from '@/shared/api/channelServicesApi';
import type { ChannelSkillServiceContext } from '@/shared/api/channelSkillsApi';

export interface SkillServiceRecommendation {
  readonly slug: string;
  readonly label: string;
  readonly evidence: 'linked' | 'catalog' | 'mention';
  readonly instances: readonly ChannelServiceChoice[];
}

function mentions(text: string, term: string) {
  // Match whole names/slugs, including those in URLs, but not substrings of
  // other hyphenated names. Free text is always advisory.
  if (term.trim().length < 3) return false;
  const escaped = term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(
    `(?<![\\p{L}\\p{N}_-])${escaped}(?![\\p{L}\\p{N}_-])`,
    'iu',
  ).test(text);
}

export function recommendSkillServices(
  skill: ChannelSkillServiceContext,
  catalog: readonly ChannelServiceCatalogEntry[],
  inventory: readonly ChannelServiceChoice[],
): SkillServiceRecommendation[] {
  const candidates = new Map(catalog.map((entry) => [entry.slug, entry]));
  for (const service of inventory) {
    if (!candidates.has(service.slug))
      candidates.set(service.slug, {
        slug: service.slug,
        name: service.label,
        recommendedSkills: [],
      });
  }
  if (skill.linkedServiceSlug && !candidates.has(skill.linkedServiceSlug))
    candidates.set(skill.linkedServiceSlug, {
      slug: skill.linkedServiceSlug,
      name: skill.linkedServiceSlug,
      recommendedSkills: [],
    });
  const text = `${skill.description}\n${skill.instructions}`;
  return [...candidates.values()].flatMap<SkillServiceRecommendation>(
    (entry) => {
      const evidence =
        entry.slug === skill.linkedServiceSlug
          ? 'linked'
          : entry.recommendedSkills.includes(skill.name)
            ? 'catalog'
            : mentions(text, entry.slug) || mentions(text, entry.name)
              ? 'mention'
              : null;
      return evidence
        ? [
            {
              slug: entry.slug,
              label: entry.name,
              evidence,
              instances: inventory.filter(
                (service) => service.slug === entry.slug,
              ),
            },
          ]
        : [];
    },
  );
}
