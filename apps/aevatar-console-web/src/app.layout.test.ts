import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { getLocale, setLocale } from '@umijs/max';
import React from 'react';
import defaultSettings from '../config/defaultSettings';
import { layout } from './app';

describe('layout menu collapse behavior', () => {
  beforeEach(() => {
    setLocale('en-US', false);
    window.history.replaceState({}, '', '/scopes');
  });

  it('keeps grouped navigation titles hidden in collapsed mode', () => {
    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    expect(runtimeLayout.menu).toMatchObject({
      collapsedWidth: 40,
      collapsedShowGroupTitle: false,
      collapsedShowTitle: false,
      type: 'group',
    });
  });

  it('collapses the global menu for Studio create-member intent', () => {
    window.history.replaceState(
      {},
      '',
      '/studio?tab=studio&intent=create-member',
    );

    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    expect(runtimeLayout.defaultCollapsed).toBe(true);
    expect(runtimeLayout.collapsed).toBe(true);
  });

  it('leaves the global menu uncontrolled for ordinary Studio entry', () => {
    window.history.replaceState({}, '', '/studio?tab=studio');

    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    expect(runtimeLayout.defaultCollapsed).toBe(false);
    expect(runtimeLayout.collapsed).toBeUndefined();
  });

  it('hides console chrome for the fullscreen Mission Wall route', () => {
    window.history.replaceState({}, '', '/runtime/mission-wall');

    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });
    const menuRender = runtimeLayout.menuRender as
      | ((props: unknown, defaultDom: unknown) => React.ReactNode)
      | undefined;
    const actionsRender = runtimeLayout.actionsRender as
      | ((props: unknown, dom: unknown) => React.ReactNode[])
      | undefined;

    expect(runtimeLayout.headerRender).toBe(false);
    expect(menuRender?.({}, React.createElement('nav'))).toBe(false);
    expect(actionsRender?.({}, {})).toEqual([]);
    expect(runtimeLayout.contentStyle).toMatchObject({
      background: '#09110f',
      height: '100vh',
      overflow: 'hidden',
      padding: 0,
    });
  });

  it('renders Workflow Activity vNext without the global console chrome', () => {
    window.history.replaceState(
      {},
      '',
      '/scopes/scope-a/workflow-activity-vnext/workflows/wf-a',
    );

    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });
    const menuRender = runtimeLayout.menuRender as
      | ((props: unknown, defaultDom: unknown) => React.ReactNode)
      | undefined;
    const actionsRender = runtimeLayout.actionsRender as
      | ((props: unknown, dom: unknown) => React.ReactNode[])
      | undefined;

    expect(runtimeLayout.headerRender).toBe(false);
    expect(menuRender?.({}, React.createElement('nav'))).toBe(false);
    expect(actionsRender?.({}, {})).toEqual([]);
    expect(runtimeLayout.contentStyle).toMatchObject({
      background: '#ffffff',
      height: 'auto',
      inset: 0,
      overflow: 'hidden',
      padding: 0,
      position: 'fixed',
    });
  });

  it('updates the controlled global menu collapse state after SPA route changes', () => {
    window.history.replaceState({}, '', '/scopes/scope-a/teams');
    const teamsLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    window.history.pushState({}, '', '/studio?tab=studio&intent=create-member');
    const studioLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });

    expect(teamsLayout.collapsed).toBeUndefined();
    expect(studioLayout.collapsed).toBe(true);
  });

  it('renders a global language switch in the layout actions', async () => {
    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });
    const actionsRender = runtimeLayout.actionsRender as
      | ((props: unknown, dom: unknown) => React.ReactNode[])
      | undefined;

    render(React.createElement(React.Fragment, null, actionsRender?.({}, {})));

    fireEvent.click(screen.getByRole('button', { name: 'Switch language' }));
    fireEvent.click(await screen.findByText('中文'));

    await waitFor(() => {
      expect(getLocale()).toBe('zh-CN');
    });
  });

  it('keeps page content in sync when the locale changes without a reload', async () => {
    window.history.replaceState({}, '', '/studio');
    const runtimeLayout = layout({
      initialState: {
        auth: {} as never,
        settings: defaultSettings,
      },
    });
    const childrenRender = runtimeLayout.childrenRender as
      | ((children: React.ReactNode) => React.ReactNode)
      | undefined;

    render(
      React.createElement(
        React.Fragment,
        null,
        childrenRender?.(React.createElement(LocalizedRuntimeProbe)),
      ),
    );

    expect(screen.getByText('My AI teams')).toBeTruthy();

    act(() => {
      setLocale('zh-CN', false);
    });

    await waitFor(() => {
      expect(screen.getByText('我的 AI 团队')).toBeTruthy();
    });
    expect(screen.queryByText('My AI teams')).toBeNull();
  });
});

const LocalizedRuntimeProbe: React.FC = () => {
  const { useIntl } = require('@umijs/max') as typeof import('@umijs/max');
  const intl = useIntl();

  return React.createElement(
    'div',
    null,
    intl.formatMessage({
      id: 'teams.home.title',
    }),
  );
};
