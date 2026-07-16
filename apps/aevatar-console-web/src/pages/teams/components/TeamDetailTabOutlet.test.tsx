import { act, render, screen } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import {
  createTeamDetailTabRegistry,
  defineTeamDetailTab,
  type TeamDetailContext,
  type TeamDetailTabModule,
  type TeamDetailTabViewProps,
} from '@/shared/teams/teamDetailTabs';
import { TeamDetailTabOutlet } from './TeamDetailTabOutlet';

type TestHostModel = {
  readonly message: string;
};

type TestViewProps = {
  readonly message: string;
};

const context: TeamDetailContext = {
  navigation: {
    buildTabHref: (tabId) => `/scopes/scope-alpha/teams/t-alpha?tab=${tabId}`,
  },
  refresh: async () => undefined,
  scopeId: 'scope-alpha',
  teamId: 't-alpha',
  teamSummary: null,
};

const TestView: React.FC<TeamDetailTabViewProps & TestViewProps> = ({
  context: teamContext,
  message,
}) => <div>{`${message}: ${teamContext.scopeId}/${teamContext.teamId}`}</div>;

describe('TeamDetailTabOutlet', () => {
  beforeEach(() => {
    setLocale('en-US', false);
  });

  it('lazy-loads a registered view with validated Team context', async () => {
    let resolveModule: (module: TeamDetailTabModule<TestViewProps>) => void =
      () => undefined;
    const modulePromise = new Promise<TeamDetailTabModule<TestViewProps>>(
      (resolve) => {
        resolveModule = resolve;
      },
    );
    const definition = defineTeamDetailTab<TestHostModel, TestViewProps>({
      id: 'activity',
      label: {
        defaultMessage: 'Activity',
        id: 'teams.detail.tabs.activity',
      },
      load: () => modulePromise,
      selectHostProps: (hostModel) => ({ message: hostModel.message }),
    });
    const registry = createTeamDetailTabRegistry({
      defaultTabId: 'activity',
      definitions: [definition],
    });

    render(
      <TeamDetailTabOutlet
        context={context}
        definition={registry.resolve('activity', context)}
        hostModel={{ message: 'Team activity' }}
        label="Activity"
      />,
    );

    expect(
      screen.getByRole('status', { name: 'Loading team detail...' }),
    ).toBeTruthy();
    await act(async () => {
      resolveModule({ default: TestView });
      await modulePromise;
    });
    expect(
      await screen.findByText('Team activity: scope-alpha/t-alpha'),
    ).toBeTruthy();
    expect(screen.getByRole('tabpanel')).toHaveAttribute(
      'aria-labelledby',
      'team-detail-tab-activity',
    );
  });

  it('isolates a module load failure from surrounding Team content', async () => {
    const consoleError = jest
      .spyOn(console, 'error')
      .mockImplementation(() => undefined);
    const definition = defineTeamDetailTab<TestHostModel>({
      id: 'topology',
      label: {
        defaultMessage: 'Topology',
        id: 'teams.detail.tabs.topology',
      },
      load: async () => {
        throw new Error('chunk unavailable');
      },
    });
    const registry = createTeamDetailTabRegistry({
      defaultTabId: 'topology',
      definitions: [definition],
    });

    render(
      <>
        <h1>Support Team</h1>
        <TeamDetailTabOutlet
          context={context}
          definition={registry.resolve('topology', context)}
          hostModel={{ message: 'unused' }}
          label="Topology"
        />
      </>,
    );

    expect(await screen.findByText('Topology could not load')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Support Team' })).toBeTruthy();
    consoleError.mockRestore();
  });
});
