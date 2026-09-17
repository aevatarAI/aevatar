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
import ChannelDetailsPage from './ChannelDetailsPage';
import ChannelsPage from './ChannelsPage';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn().mockResolvedValue({ authenticated: false }),
  },
}));
const mockToast = {
  success: jest.fn(),
  warning: jest.fn(),
  error: jest.fn(),
  info: jest.fn(),
};
jest.mock('@/shared/ui/ConsoleToast', () => ({
  ...jest.requireActual('@/shared/ui/ConsoleToast'),
  useConsoleToast: () => mockToast,
}));
const fetchMock = jest.mocked(authFetch);
const response = (value: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => value,
  }) as Response;
const row = {
  state_version: 12,
  id: 'reg-alpha',
  nyx_channel_bot_id: 'bot-alpha',
  platform: 'discord',
  label: 'Support',
  owned: true,
  binding_status: 'bound',
  availability_status: 'available',
  nyx_status: 'active',
  skill_name: 'support',
  authorization_mode: 'explicit_service_allowlist',
  service_ids: [],
  workflow_result_delivery_status: 'enabled',
};
beforeEach(() => {
  fetchMock.mockReset();
  Object.values(mockToast).forEach((mock) => {
    mock.mockReset();
  });
  jest.spyOn(history, 'push').mockImplementation(() => {});
  jest.spyOn(history, 'replace').mockImplementation(() => {});
});

it('renders static guidance and unbound bot actions, preserving rows during manual refresh', async () => {
  fetchMock.mockResolvedValue(
    response([
      row,
      {
        ...row,
        id: null,
        nyx_channel_bot_id: 'bot:two/a',
        label: 'New bot',
        binding_status: 'unbound',
        skill_name: '',
        workflow_result_delivery_status: 'unbound',
      },
    ]),
  );
  renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
  const table = await screen.findByRole('table');
  const guide = screen.getByRole('region', { name: 'How to connect a bot' });
  const guideHtml = guide.innerHTML;
  expect(within(guide).getAllByRole('listitem')).toHaveLength(3);
  expect(guide.querySelector('[aria-current]')).toBeNull();
  expect(
    within(guide).getByRole('link', { name: /Add bot in NyxID/ }),
  ).toHaveAttribute('href', 'https://nyx.chrono-ai.fun/channel-bots');
  expect(within(table).getByRole('link', { name: 'Bind' })).toHaveAttribute(
    'href',
    '/scopes/scope-alpha/workflow-activity-vnext/channels/bind/bot%3Atwo%2Fa',
  );
  expect(within(table).getAllByRole('link', { name: /Manage/ })).toHaveLength(
    1,
  );
  let resolve!: (value: Response) => void;
  fetchMock.mockImplementation(
    () =>
      new Promise((done) => {
        resolve = done;
      }),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Refresh channels' }));
  expect(table).toHaveAttribute('inert');
  expect(screen.getByText('New bot')).toBeInTheDocument();
  await act(async () => resolve(response([row])));
  await waitFor(() =>
    expect(
      screen.getByRole('button', { name: 'Refresh channels' }),
    ).toBeEnabled(),
  );
  expect(guide.innerHTML).toBe(guideHtml);
  expect(screen.queryByText('New bot')).not.toBeInTheDocument();
  expect(fetchMock).toHaveBeenCalledTimes(2);
});

it('keeps inventory failure distinct from an empty list and unavailable bots cannot be bound', async () => {
  fetchMock
    .mockResolvedValueOnce(response({}, 502))
    .mockResolvedValueOnce(response([]))
    .mockResolvedValue(
      response([
        {
          ...row,
          id: null,
          binding_status: 'unbound',
          availability_status: 'unavailable',
        },
      ]),
    );
  renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Could not load channels',
  );
  expect(screen.queryByText('No bots yet')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
  await screen.findByText('No bots yet');
  fireEvent.click(screen.getByRole('button', { name: 'Refresh channels' }));
  await screen.findByText('Unavailable');
  expect(screen.queryByRole('link', { name: 'Bind' })).not.toBeInTheDocument();
});

it('loads exact detail and confirms removal when the bot remains as unbound inventory', async () => {
  let removed = false;
  fetchMock.mockImplementation(async (input, init) => {
    if (init?.method === 'DELETE') {
      removed = true;
      return response({ status: 'deleted', warnings: ['TEST_SECRET'] });
    }
    if (input === '/api/channels/registrations/reg-alpha')
      return response({
        ...row,
        label: 'reg-alpha',
        nyx_agent_api_key_id: 'key:one/a',
        webhook_url: 'TEST_SECRET',
      });
    return response([
      {
        ...row,
        id: removed ? null : row.id,
        binding_status: removed ? 'unbound' : 'bound',
      },
    ]);
  });
  renderWithQueryClient(
    <ChannelDetailsPage scopeId="scope-alpha" registrationId="reg-alpha" />,
  );
  expect(
    await screen.findByRole('link', { name: /Open Agent key ID/ }),
  ).toHaveAttribute(
    'href',
    'https://nyx.chrono-ai.fun/keys/api-key/key%3Aone%2Fa',
  );
  expect(document.body).not.toHaveTextContent('TEST_SECRET');
  expect(screen.getByRole('heading', { name: 'Support' })).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
  fireEvent.click(
    within(await screen.findByRole('dialog')).getByRole('button', {
      name: 'Remove',
    }),
  );
  await waitFor(() =>
    expect(history.replace).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/channels',
    ),
  );
  expect(mockToast.warning).toHaveBeenCalled();
  expect(
    fetchMock.mock.calls.filter(([, init]) => init?.method === 'DELETE'),
  ).toHaveLength(1);
});

it('identifies an older server response without showing an empty inventory or allowing binding', async () => {
  fetchMock.mockResolvedValue(
    response([
      { ...row, binding_status: undefined, default_skill_name: 'support' },
    ]),
  );
  renderWithQueryClient(<ChannelsPage scopeId="scope-alpha" />);
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Bot binding is not available yet. Please try again later.',
  );
  expect(screen.queryByText('No bots yet')).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Bind' })).not.toBeInTheDocument();
  expect(
    fetchMock.mock.calls.every(
      ([, init]) => !init?.method || init.method === 'GET',
    ),
  ).toBe(true);
});
