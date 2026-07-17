import {
  builtInTeamDetailTabIds,
  createTeamDetailTabRegistry,
  defineTeamDetailTab,
  type TeamDetailContext,
} from './teamDetailTabs';

type TestHostModel = Record<never, never>;

const emptyViewModule = async () => ({
  default: () => null,
});

const context: TeamDetailContext = {
  navigation: {
    buildTabHref: (tabId) => `/scopes/scope-alpha/teams/t-alpha?tab=${tabId}`,
  },
  refresh: async () => undefined,
  scopeId: 'scope-alpha',
  teamId: 't-alpha',
  teamSummary: {
    createdAt: '2026-07-16T00:00:00Z',
    description: 'Support operations',
    displayName: 'Support Team',
    lifecycleStage: 'active',
    memberCount: 2,
    scopeId: 'scope-alpha',
    teamId: 't-alpha',
    updatedAt: '2026-07-16T00:00:00Z',
  },
};

function createDefinition(
  id: string,
  isAvailable?: (value: TeamDetailContext) => boolean,
) {
  return defineTeamDetailTab<TestHostModel>({
    id,
    isAvailable,
    label: {
      defaultMessage: id,
      id: `teams.detail.tabs.${id}`,
    },
    load: emptyViewModule,
  });
}

describe('Team detail tab registry', () => {
  it('preserves declaration order and resolves only available definitions', () => {
    const registry = createTeamDetailTabRegistry<TestHostModel>({
      defaultTabId: builtInTeamDetailTabIds.overview,
      definitions: [
        createDefinition('overview'),
        createDefinition('activity'),
        createDefinition(
          'archived-log',
          (value) => value.teamSummary?.lifecycleStage === 'archived',
        ),
      ],
    });

    expect(registry.definitions.map((definition) => definition.id)).toEqual([
      'overview',
      'activity',
      'archived-log',
    ]);
    expect(
      registry.listAvailable(context).map((definition) => definition.id),
    ).toEqual(['overview', 'activity']);
    expect(registry.resolve('ACTIVITY', context).id).toBe('activity');
    expect(registry.findId('ACTIVITY')).toBe('activity');
    expect(registry.resolve('archived-log', context).id).toBe('overview');
    expect(registry.resolve('unknown', context).id).toBe('overview');
    expect(Object.isFrozen(registry)).toBe(true);
    expect(Object.isFrozen(registry.definitions)).toBe(true);
    expect(Object.isFrozen(registry.definitions[0])).toBe(true);
  });

  it('rejects invalid, duplicate, missing, and conditional default tabs', () => {
    expect(() =>
      createTeamDetailTabRegistry<TestHostModel>({
        defaultTabId: builtInTeamDetailTabIds.overview,
        definitions: [createDefinition('Invalid tab')],
      }),
    ).toThrow('Invalid Team detail tab id');
    expect(() =>
      createTeamDetailTabRegistry<TestHostModel>({
        defaultTabId: builtInTeamDetailTabIds.overview,
        definitions: [
          createDefinition('overview'),
          createDefinition('overview'),
        ],
      }),
    ).toThrow('Duplicate Team detail tab id "overview".');
    expect(() =>
      createTeamDetailTabRegistry<TestHostModel>({
        defaultTabId: builtInTeamDetailTabIds.overview,
        definitions: [createDefinition('activity')],
      }),
    ).toThrow('Default Team detail tab "overview" is not registered.');
    expect(() =>
      createTeamDetailTabRegistry<TestHostModel>({
        defaultTabId: builtInTeamDetailTabIds.overview,
        definitions: [createDefinition('overview', () => true)],
      }),
    ).toThrow('Default Team detail tab "overview" must always be available.');
  });
});
