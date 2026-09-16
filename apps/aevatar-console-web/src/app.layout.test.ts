import { act, render, screen, waitFor } from '@testing-library/react';
import { setLocale, useIntl } from '@umijs/max';
import React from 'react';
import defaultSettings from '../config/defaultSettings';
import { createNyxIDServiceSession } from '../tests/fixtures/nyxidServiceSession';
import { layout } from './app';
import { persistAuthSession } from './shared/auth/session';
import { history } from './shared/navigation/history';

function runtimeLayout() {
  return layout({
    initialState: { auth: {} as never, settings: defaultSettings },
  });
}

function renderRuntime(content: React.ReactNode) {
  const renderChildren = runtimeLayout().childrenRender as (
    node: React.ReactNode,
  ) => React.ReactNode;
  return render(
    React.createElement(React.Fragment, null, renderChildren(content)),
  );
}

beforeEach(() => {
  setLocale('en-US', false);
  window.history.replaceState({}, '', '/scopes/scope-alpha/workflows');
});

it('uses the resource shell without a second global menu or header', () => {
  for (const pathname of [
    '/workflows',
    '/scopes/scope-alpha/workflows/wf-alpha',
    '/scopes/scope-alpha/activity/run-alpha',
    '/scopes/scope-alpha/channels',
    '/scopes/scope-alpha/settings',
  ]) {
    window.history.replaceState({}, '', pathname);
    const config = runtimeLayout();
    expect(config.headerRender).toBe(false);
    expect((config.menuRender as () => boolean)()).toBe(false);
    expect((config.actionsRender as () => unknown[])()).toEqual([]);
    expect(config.contentStyle).toMatchObject({
      position: 'fixed',
      inset: 0,
      padding: 0,
      overflow: 'hidden',
    });
  }
});

it('protects canonical deep links and preserves their query and fragment through login', async () => {
  window.history.replaceState(
    {},
    '',
    '/scopes/scope-alpha/activity/run-alpha?view=steps#output',
  );
  const replace = jest.spyOn(history, 'replace').mockImplementation(() => {});
  renderRuntime(React.createElement('p', null, 'Protected content'));
  expect(screen.queryByText('Protected content')).not.toBeInTheDocument();
  await waitFor(() =>
    expect(replace).toHaveBeenCalledWith(
      '/login?redirect=%2Fscopes%2Fscope-alpha%2Factivity%2Frun-alpha%3Fview%3Dsteps%23output',
    ),
  );
});

it('leaves login and callback public', () => {
  for (const pathname of ['/login', '/auth/callback']) {
    window.history.replaceState({}, '', pathname);
    const view = renderRuntime(
      React.createElement('p', null, 'Public content'),
    );
    expect(screen.getByText('Public content')).toBeInTheDocument();
    view.unmount();
  }
});

it('keeps authenticated page locale changes reactive without restoring legacy layout', async () => {
  persistAuthSession(createNyxIDServiceSession());
  renderRuntime(React.createElement(LocalizedRuntimeProbe));
  expect(screen.getByText('Channels')).toBeInTheDocument();
  act(() => {
    setLocale('zh-CN', false);
  });
  await waitFor(() =>
    expect(screen.queryByText('Channels')).not.toBeInTheDocument(),
  );
  expect(screen.getByText('渠道')).toBeInTheDocument();
});

function LocalizedRuntimeProbe() {
  const intl = useIntl();
  return React.createElement(
    'p',
    null,
    intl.formatMessage({ id: 'workflowActivityVNext.nav.channels' }),
  );
}
