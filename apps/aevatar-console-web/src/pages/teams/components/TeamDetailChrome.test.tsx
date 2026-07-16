import { fireEvent, render, screen } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { builtInTeamDetailTabIds } from '@/shared/teams/teamDetailTabs';
import { TeamTabBar } from './TeamDetailChrome';

const tabOptions = [
  { label: 'Overview', value: builtInTeamDetailTabIds.overview },
  { label: 'Automations', value: builtInTeamDetailTabIds.automations },
  { label: 'Team members', value: builtInTeamDetailTabIds.members },
] as const;

describe('TeamTabBar', () => {
  beforeEach(() => {
    setLocale('en-US', false);
  });

  it('uses WAI-ARIA tab semantics and roving keyboard focus', () => {
    const onSelectTab = jest.fn();
    render(
      <TeamTabBar
        activeTab={builtInTeamDetailTabIds.overview}
        onSelectTab={onSelectTab}
        tabOptions={tabOptions}
      />,
    );

    const overviewTab = screen.getByRole('tab', { name: 'Overview' });
    const automationsTab = screen.getByRole('tab', { name: 'Automations' });
    const membersTab = screen.getByRole('tab', { name: 'Team members' });

    expect(overviewTab).toHaveAttribute('aria-selected', 'true');
    expect(overviewTab).toHaveAttribute('tabindex', '0');
    expect(automationsTab).toHaveAttribute('aria-selected', 'false');
    expect(automationsTab).toHaveAttribute('tabindex', '-1');

    overviewTab.focus();
    fireEvent.keyDown(overviewTab, { key: 'ArrowRight' });
    expect(automationsTab).toHaveFocus();
    expect(onSelectTab).toHaveBeenLastCalledWith('automations');

    fireEvent.keyDown(automationsTab, { key: 'End' });
    expect(membersTab).toHaveFocus();
    expect(onSelectTab).toHaveBeenLastCalledWith('members');

    fireEvent.keyDown(membersTab, { key: 'ArrowRight' });
    expect(overviewTab).toHaveFocus();
    expect(onSelectTab).toHaveBeenLastCalledWith('overview');
  });

  it('keeps long tab lists in a stable horizontal overflow surface', () => {
    render(
      <TeamTabBar
        activeTab={builtInTeamDetailTabIds.overview}
        onSelectTab={() => undefined}
        tabOptions={tabOptions}
      />,
    );

    const tabList = screen.getByRole('tablist', { name: 'Team detail tabs' });
    expect(tabList).toHaveStyle({
      flexWrap: 'nowrap',
      overflowX: 'auto',
    });
    expect(screen.getByRole('tab', { name: 'Overview' }).style.transition).toBe(
      '',
    );
  });
});
