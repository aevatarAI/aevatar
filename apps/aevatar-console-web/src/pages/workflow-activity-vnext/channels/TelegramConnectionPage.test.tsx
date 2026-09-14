import { focusManager, onlineManager } from '@tanstack/react-query';
import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import { authFetch } from '@/shared/auth/fetch';
import { history } from '@/shared/navigation/history';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextPage from '../index';
import TelegramConnectionPage from './TelegramConnectionPage';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({ baseUrl: 'https://nyx.example.test' }),
}));
jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn().mockResolvedValue({ authenticated: false }),
  },
}));
const fetchMock = jest.mocked(authFetch);
const originalFetch = global.fetch;
const telegramFetch = jest.fn();
const telegramToken = '123456:TEST_ONLY_TOKEN';
const response = (value: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => value,
  }) as Response;
const service = {
  id: 'user-service-github',
  slug: 'api-github',
  label: 'GitHub work',
  is_active: true,
  credential_source: { type: 'personal' },
};
const servicePath = 'https://nyx.example.test/api/v1/user-services';
const catalogPath = 'https://nyx.example.test/api/v1/mcp/config';
const catalog = {
  contract_version: '1.0',
  services: [
    { service_id: 'user-service-github', is_user_service: true },
    { service_id: 'user-service-personal', is_user_service: true },
    { service_id: 'user-service-slack', is_user_service: true },
  ],
};
const listPath = '/api/channels/registrations';
function enterNames() {
  fireEvent.change(screen.getByLabelText(/^Bot token/), {
    target: { value: 'TEST_ONLY_TOKEN' },
  });
  fireEvent.change(screen.getByLabelText(/^Label/), {
    target: { value: 'My team bot' },
  });
  fireEvent.change(screen.getByLabelText(/^Skill name/), {
    target: { value: 'review.skill' },
  });
}
beforeEach(() => {
  fetchMock.mockReset();
  telegramFetch.mockReset();
  global.fetch = telegramFetch;
  jest.spyOn(history, 'push').mockImplementation(() => {});
  jest.spyOn(history, 'replace').mockImplementation(() => {});
  fetchMock.mockImplementation(async (input) =>
    input === catalogPath
      ? response(catalog)
      : input === servicePath
        ? response({ services: [service] })
        : response([]),
  );
});
afterEach(() => {
  global.fetch = originalFetch;
});

it('reads registrations only after acceptance or Check again, never while idle or after failure', async () => {
  jest.useFakeTimers();
  try {
    let attempts = 0;
    fetchMock.mockImplementation(async (input, init) => {
      if (input === catalogPath) return response(catalog);
      if (init?.method === 'POST') {
        attempts += 1;
        return attempts === 1
          ? response({}, 504)
          : response(
              { status: 'accepted', registration_id: 'registration-new' },
              202,
            );
      }
      return input === servicePath ? response({ services: [] }) : response([]);
    });
    const listReads = () =>
      fetchMock.mock.calls.filter(
        ([input, init]) => input === listPath && init?.method !== 'POST',
      ).length;
    const idleAndReturn = async () => {
      await act(async () => {
        await jest.advanceTimersByTimeAsync(60_000);
        focusManager.setFocused(false);
        focusManager.setFocused(true);
        onlineManager.setOnline(false);
        onlineManager.setOnline(true);
        await jest.advanceTimersByTimeAsync(1);
      });
    };
    const view = renderWithQueryClient(
      <TelegramConnectionPage scopeId="scope-alpha" />,
    );
    await screen.findByText(/No services are available/);
    await idleAndReturn();
    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      servicePath,
      catalogPath,
    ]);
    enterNames();
    fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
    await screen.findByText(
      'Could not confirm the connection. Please try again.',
    );
    await idleAndReturn();
    expect(listReads()).toBe(0);
    expect(attempts).toBe(1);
    fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
    await screen.findByRole('button', { name: 'Check again' });
    expect(listReads()).toBe(1);
    await idleAndReturn();
    expect(listReads()).toBe(1);
    expect(history.replace).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
    await waitFor(() => expect(listReads()).toBe(2));
    expect(attempts).toBe(2);
    view.unmount();
  } finally {
    focusManager.setFocused(undefined);
    onlineManager.setOnline(true);
    await act(async () => {
      await jest.runOnlyPendingTimersAsync();
    });
    jest.useRealTimers();
  }
});

it('searches services, enforces permissions, submits selected IDs once and waits for the new registration', async () => {
  let resolvePost!: (value: Response) => void;
  const post = new Promise<Response>((resolve) => {
    resolvePost = resolve;
  });
  let observed = false;
  fetchMock.mockImplementation(async (input, init) => {
    if (input === catalogPath) return response(catalog);
    if (init?.method === 'POST') return post;
    if (input === servicePath)
      return response({
        services: [
          service,
          {
            ...service,
            id: 'user-service-ungranted',
            label: 'GitHub not authorized',
          },
          {
            ...service,
            id: 'user-service-org',
            slug: 'slack',
            label: 'Team Slack',
            credential_source: { type: 'org', allowed: false },
          },
          {
            ...service,
            id: 'user-service-inactive',
            slug: 'drive',
            label: 'Drive',
            is_active: false,
          },
        ],
      });
    return response(
      observed
        ? [
            {
              id: 'registration-created',
              platform: 'telegram',
              scope_id: 'scope-alpha',
              owned: true,
            },
          ]
        : [],
    );
  });
  window.history.replaceState(
    {},
    '',
    '/scopes/scope-alpha/workflow-activity-vnext/channels/connect/telegram',
  );
  const view = renderWithQueryClient(<WorkflowActivityVNextPage />);
  expect(
    await screen.findByRole('checkbox', { name: /GitHub work/ }),
  ).not.toBeChecked();
  expect(
    screen.queryByRole('checkbox', { name: /Team Slack/ }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole('checkbox', { name: /Drive/ }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole('checkbox', { name: /GitHub not authorized/ }),
  ).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('checkbox', { name: 'Select all' }));
  fireEvent.change(
    screen.getByRole('textbox', { name: 'Search services by name or slug' }),
    { target: { value: 'slack' } },
  );
  expect(
    screen.queryByRole('checkbox', { name: /GitHub work/ }),
  ).not.toBeInTheDocument();
  expect(screen.getByText('1 selected')).toBeInTheDocument();
  enterNames();
  fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
  fireEvent.submit(screen.getByRole('form', { name: 'Connect Telegram' }));
  expect(
    screen.getByRole('checkbox', { name: 'Select all results' }),
  ).toBeDisabled();
  const posts = fetchMock.mock.calls.filter(
    ([, init]) => init?.method === 'POST',
  );
  expect(posts).toHaveLength(1);
  expect(posts[0][0]).toBe(listPath);
  expect(JSON.parse(String(posts[0][1]?.body))).toEqual({
    platform: 'telegram',
    bot_token: 'TEST_ONLY_TOKEN',
    label: 'My team bot',
    default_skill_name: 'review.skill',
    authorization_mode: 'explicit_service_allowlist',
    service_ids: ['user-service-github'],
    webhook_base_url: 'https://aevatar-console-backend-api.aevatar.ai',
  });
  await act(async () =>
    resolvePost(
      response(
        {
          status: 'accepted',
          registration_id: 'registration-created',
          webhook_url: 'TEST_ONLY_SECRET',
        },
        202,
      ),
    ),
  );
  expect(
    await screen.findByText(/Telegram setup was submitted/),
  ).toBeInTheDocument();
  expect(screen.getByLabelText(/^Bot token/)).toHaveValue('');
  expect(
    screen.queryByText(
      'Telegram added. Send your bot a message to get started.',
    ),
  ).not.toBeInTheDocument();
  expect(history.replace).not.toHaveBeenCalled();
  expect(
    JSON.stringify(
      view.queryClient
        .getQueryCache()
        .getAll()
        .map((query) => query.state.data),
    ),
  ).not.toContain('TEST_ONLY');
  expect(view.queryClient.getMutationCache().getAll()).toHaveLength(0);
  observed = true;
  fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
  await waitFor(() =>
    expect(history.replace).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/channels/registration-created',
    ),
  );
  expect(
    await screen.findByText(
      'Telegram added. Send your bot a message to get started.',
    ),
  ).toBeInTheDocument();
});

it('selects and clears available results while preserving selections outside the search', async () => {
  fetchMock.mockImplementation(async (input) =>
    input === catalogPath
      ? response(catalog)
      : response({
          services: [
            service,
            {
              ...service,
              id: 'user-service-personal',
              label: 'GitHub personal',
            },
            {
              ...service,
              id: 'user-service-slack',
              label: 'Slack',
              slug: 'slack',
            },
            { ...service, id: 'inactive', label: 'Inactive', is_active: false },
            {
              ...service,
              id: 'denied',
              label: 'Denied',
              credential_source: { type: 'org', allowed: false },
            },
          ],
        }),
  );
  renderWithQueryClient(<TelegramConnectionPage scopeId="scope-alpha" />);
  const all = await screen.findByRole('checkbox', { name: 'Select all' });
  expect(all).not.toBeChecked();
  fireEvent.click(screen.getByRole('checkbox', { name: /^Slack/ }));
  expect(all).toBePartiallyChecked();
  fireEvent.click(all);
  expect(all).toBeChecked();
  expect(screen.getByText('3 selected')).toBeInTheDocument();
  for (const name of [/Inactive/, /Denied/]) {
    expect(screen.queryByRole('checkbox', { name })).not.toBeInTheDocument();
  }
  fireEvent.click(all);
  expect(screen.getByText('0 selected')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('checkbox', { name: /^Slack/ }));

  const search = screen.getByRole('textbox', {
    name: 'Search services by name or slug',
  });
  fireEvent.change(search, { target: { value: 'GitHub' } });
  const results = screen.getByRole('checkbox', { name: 'Select all results' });
  expect(results).not.toBeChecked();
  fireEvent.click(results);
  expect(screen.getByText('3 selected')).toBeInTheDocument();
  expect(results).toBeChecked();
  fireEvent.click(results);
  expect(screen.getByText('1 selected')).toBeInTheDocument();
  fireEvent.change(search, { target: { value: 'no-matching-service' } });
  expect(results).toBeDisabled();
  fireEvent.change(search, { target: { value: '' } });
  expect(screen.getByRole('checkbox', { name: /^Slack/ })).toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: 'Select all' }),
  ).toBePartiallyChecked();
});

it('recovers caller-catalog failure without exposing account services and defaults optional names with no access', async () => {
  let catalogReads = 0;
  telegramFetch.mockResolvedValue(
    response({
      ok: true,
      result: {
        is_bot: true,
        first_name: ' My Telegram Bot ',
        username: 'different_username',
      },
    }),
  );
  fetchMock.mockImplementation(async (input, init) => {
    if (input === catalogPath)
      return ++catalogReads === 1
        ? response({}, 503)
        : response({ contract_version: '1.0', services: [] });
    if (input === servicePath) return response({ services: [service] });
    if (init?.method === 'POST')
      return response(
        { error: 'missing_bot_token', note: 'TEST_ONLY_SECRET' },
        400,
      );
    return response([]);
  });
  renderWithQueryClient(<TelegramConnectionPage scopeId="scope-alpha" />);
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Could not load your services',
  );
  expect(
    screen.getByRole('button', { name: 'Connect Telegram' }),
  ).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
  expect(
    await screen.findByText(/No services are available/),
  ).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
  expect(
    screen.getByText('Enter the bot token from BotFather.'),
  ).toBeInTheDocument();
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(
    false,
  );
  expect(screen.getByLabelText('Label (optional)')).not.toBeRequired();
  expect(screen.getByLabelText('Skill name (optional)')).not.toBeRequired();
  fireEvent.change(screen.getByLabelText(/^Bot token/), {
    target: { value: telegramToken },
  });
  fireEvent.change(screen.getByLabelText('Label (optional)'), {
    target: { value: '   ' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
  expect(
    await screen.findByText(
      'Check the bot token from BotFather and try again.',
    ),
  ).toBeInTheDocument();
  expect(
    JSON.parse(
      String(
        fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')?.[1]
          ?.body,
      ),
    ),
  ).toMatchObject({
    label: 'My Telegram Bot',
    default_skill_name: 'My Telegram Bot',
    service_ids: [],
  });
  expect(screen.getByLabelText(/^Bot token/)).toHaveValue(telegramToken);
  expect(document.body).not.toHaveTextContent('TEST_ONLY_SECRET');
  expect(
    await screen.findByRole('button', { name: 'Connect Telegram' }),
  ).toBeEnabled();
});

it('shows a name-lookup toast and retries with the current token and custom field value', async () => {
  telegramFetch.mockRejectedValueOnce(
    new Error(`Failed https://api.telegram.org/bot${telegramToken}/getMe`),
  );
  fetchMock.mockImplementation(async (input, init) => {
    if (input === catalogPath) return response(catalog);
    if (init?.method === 'POST')
      return response(
        { status: 'accepted', registration_id: 'registration-lookup' },
        202,
      );
    return input === servicePath ? response({ services: [] }) : response([]);
  });
  renderWithQueryClient(<TelegramConnectionPage scopeId="scope-alpha" />);
  await screen.findByText(/No services are available/);
  fireEvent.change(screen.getByLabelText(/^Bot token/), {
    target: { value: telegramToken },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Could not read the Telegram bot name. Please try again.',
  );
  expect(document.body).not.toHaveTextContent(telegramToken);
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(
    false,
  );
  expect(
    screen.getByRole('button', { name: 'Connect Telegram' }),
  ).toBeEnabled();
  const updatedToken = '789012:TEST_ONLY_NEW_TOKEN';
  fireEvent.change(screen.getByLabelText(/^Bot token/), {
    target: { value: updatedToken },
  });
  fireEvent.change(screen.getByLabelText(/^Label/), {
    target: { value: 'Custom label' },
  });
  telegramFetch.mockResolvedValueOnce(
    response({ ok: true, result: { is_bot: true, first_name: 'Current Bot' } }),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
  await screen.findByText(/Telegram setup was submitted/);
  expect(telegramFetch.mock.calls.map(([url]) => url)).toEqual([
    `https://api.telegram.org/bot${telegramToken}/getMe`,
    `https://api.telegram.org/bot${updatedToken}/getMe`,
  ]);
  const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST');
  expect(JSON.parse(String(post?.[1]?.body))).toMatchObject({
    bot_token: updatedToken,
    label: 'Custom label',
    default_skill_name: 'Current Bot',
  });
});

it.each([
  'gateway timeout',
  'network failure',
])('shows a toast for %s, preserves inputs and allows a successful manual retry', async (failure) => {
  let attempts = 0;
  fetchMock.mockImplementation(async (input, init) => {
    if (input === catalogPath) return response(catalog);
    if (init?.method === 'POST') {
      attempts += 1;
      if (attempts === 1) {
        if (failure === 'network failure') throw new Error('TEST_ONLY_SECRET');
        return response({ note: 'TEST_ONLY_SECRET' }, 504);
      }
      return response(
        { status: 'accepted', registration_id: 'registration-retried' },
        202,
      );
    }
    if (input === servicePath) return response({ services: [service] });
    return response(
      attempts === 2
        ? [
            {
              id: 'registration-retried',
              platform: 'telegram',
              scope_id: 'scope-alpha',
              owned: true,
            },
          ]
        : [],
    );
  });
  renderWithQueryClient(<TelegramConnectionPage scopeId="scope-alpha" />);
  fireEvent.click(await screen.findByRole('checkbox', { name: 'Select all' }));
  enterNames();
  fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Could not confirm the connection. Please try again.',
  );
  const form = screen.getByRole('form', { name: 'Connect Telegram' });
  expect(within(form).queryByRole('alert')).not.toBeInTheDocument();
  expect(screen.getByLabelText(/^Bot token/)).toHaveValue('TEST_ONLY_TOKEN');
  expect(screen.getByLabelText(/^Label/)).toHaveValue('My team bot');
  expect(screen.getByLabelText(/^Skill name/)).toHaveValue('review.skill');
  expect(screen.getByRole('checkbox', { name: 'Select all' })).toBeChecked();
  expect(screen.getByRole('checkbox', { name: 'Select all' })).toBeEnabled();
  expect(
    screen.getByRole('button', { name: 'Connect Telegram' }),
  ).toBeEnabled();
  expect(attempts).toBe(1);
  expect(document.body).not.toHaveTextContent('TEST_ONLY_SECRET');
  fireEvent.click(screen.getByRole('button', { name: 'Close' }));
  fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
  await waitFor(() =>
    expect(history.replace).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/channels/registration-retried',
    ),
  );
  expect(attempts).toBe(2);
  expect(screen.getByLabelText(/^Bot token/)).toHaveValue('');
  expect(
    await screen.findByText(
      'Telegram added. Send your bot a message to get started.',
    ),
  ).toBeInTheDocument();
});

it('removes revoked selections after a rejected submission and allows a retry with the remaining grants', async () => {
  let attempted = false;
  let resolveCatalog!: (value: Response) => void;
  const updatedCatalog = new Promise<Response>((resolve) => {
    resolveCatalog = resolve;
  });
  fetchMock.mockImplementation(async (input, init) => {
    if (input === servicePath) return response({ services: [service] });
    if (input === catalogPath)
      return attempted ? updatedCatalog : response(catalog);
    if (init?.method === 'POST') {
      attempted = true;
      return response({ error: 'invalid_service_ids' }, 400);
    }
    return response([]);
  });
  renderWithQueryClient(<TelegramConnectionPage scopeId="scope-alpha" />);
  fireEvent.click(await screen.findByRole('checkbox', { name: /GitHub work/ }));
  enterNames();
  fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
  await screen.findByText(
    'The selected services are no longer available. Review your selection and try again.',
  );
  expect(
    screen.getByRole('button', { name: 'Connect Telegram' }),
  ).toBeDisabled();
  // An old selection cannot be submitted while its replacement is pending.
  fireEvent.submit(screen.getByRole('form', { name: 'Connect Telegram' }));
  expect(
    fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST'),
  ).toHaveLength(1);
  await act(async () =>
    resolveCatalog(response({ contract_version: '1.0', services: [] })),
  );
  await screen.findByText(/No services are available/);
  expect(
    screen.queryByRole('checkbox', { name: /GitHub work/ }),
  ).not.toBeInTheDocument();
  expect(screen.getByText('0 selected')).toBeInTheDocument();
  expect(screen.getByLabelText(/^Bot token/)).toHaveValue('TEST_ONLY_TOKEN');
  expect(
    screen.getByRole('button', { name: 'Connect Telegram' }),
  ).toBeEnabled();
  fireEvent.click(screen.getByRole('button', { name: 'Connect Telegram' }));
  await waitFor(() =>
    expect(
      fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST'),
    ).toHaveLength(2),
  );
  const posts = fetchMock.mock.calls.filter(
    ([, init]) => init?.method === 'POST',
  );
  expect(JSON.parse(String(posts[1][1]?.body)).service_ids).toEqual([]);
});

it('confirms discarding an edited setup without issuing a registration request', async () => {
  renderWithQueryClient(<TelegramConnectionPage scopeId="scope-alpha" />);
  await screen.findByRole('checkbox', { name: /GitHub work/ });
  enterNames();
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  const dialog = await screen.findByRole('dialog', {
    name: 'Discard this connection setup?',
  });
  expect(history.push).not.toHaveBeenCalled();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Stay' }));
  expect(screen.getByLabelText(/^Label/)).toHaveValue('My team bot');
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  fireEvent.click(
    within(await screen.findByRole('dialog')).getByRole('button', {
      name: 'Discard',
    }),
  );
  expect(history.push).toHaveBeenCalledWith(
    '/scopes/scope-alpha/workflow-activity-vnext/channels',
  );
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(
    false,
  );
});
