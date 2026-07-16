import {
  createTeamDetailTabRegistry,
  defineTeamDetailTab,
  type TeamDetailTabDefinition,
  type TeamDetailTabDefinitionInput,
} from '@/shared/teams/teamDetailTabs';
import type { TeamAutomationsTabProps } from './tabs/TeamAutomationsTab';
import type { TeamMembersTabProps } from './tabs/TeamMembersTab';
import type { TeamOverviewTabProps } from './tabs/TeamOverviewTab';

export type TeamDetailTabHostModel = {
  readonly automations: TeamAutomationsTabProps;
  readonly members: TeamMembersTabProps;
  readonly overview: TeamOverviewTabProps;
};

export const teamDetailTabIds = Object.freeze({
  automations: 'automations',
  members: 'members',
  overview: 'overview',
});

export function defineTeamDetailFeatureTab<
  TViewProps extends object = Record<never, never>,
>(
  definition: TeamDetailTabDefinitionInput<
    TeamDetailTabHostModel,
    TViewProps
  >,
): TeamDetailTabDefinition<TeamDetailTabHostModel> {
  return defineTeamDetailTab(definition);
}

export const builtInTeamDetailTabDefinitions = Object.freeze([
  defineTeamDetailTab<TeamDetailTabHostModel, TeamOverviewTabProps>({
    id: teamDetailTabIds.overview,
    label: {
      defaultMessage: 'Overview',
      id: 'teams.detail.tabs.overview',
    },
    load: () => import('./tabs/TeamOverviewTab'),
    selectHostProps: (hostModel) => hostModel.overview,
  }),
  defineTeamDetailTab<TeamDetailTabHostModel, TeamAutomationsTabProps>({
    id: teamDetailTabIds.automations,
    label: {
      defaultMessage: 'Automations',
      id: 'teams.detail.tabs.automations',
    },
    load: () => import('./tabs/TeamAutomationsTab'),
    selectHostProps: (hostModel) => hostModel.automations,
  }),
  defineTeamDetailTab<TeamDetailTabHostModel, TeamMembersTabProps>({
    id: teamDetailTabIds.members,
    label: {
      defaultMessage: 'Team members',
      id: 'teams.detail.tabs.members',
    },
    load: () => import('./tabs/TeamMembersTab'),
    selectHostProps: (hostModel) => hostModel.members,
  }),
] satisfies readonly TeamDetailTabDefinition<TeamDetailTabHostModel>[]);

export const teamDetailTabRegistry = createTeamDetailTabRegistry({
  defaultTabId: teamDetailTabIds.overview,
  definitions: builtInTeamDetailTabDefinitions,
});
