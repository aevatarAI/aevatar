import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { createNyxIDServiceSession } from '../../../../tests/fixtures/nyxidServiceSession';
import {
  createTestQueryClient,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextPage from '../index';
import { channelKeys } from './queries';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({
    baseUrl: 'https://nyx.example.test',
    enabled: true,
  }),
}));
jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn().mockResolvedValue({ authenticated: false }),
  },
}));
const mockToast = { success: jest.fn(), error: jest.fn() };
jest.mock('@/shared/ui/ConsoleToast', () => ({
  ...jest.requireActual('@/shared/ui/ConsoleToast'),
  useConsoleToast: () => mockToast,
}));

const fetchMock = jest.mocked(authFetch);
const baseIds = ['us-ornn', 'us-llm', 'us-github'];
const service = (id: string, slug: string, label: string) => ({
  id,
  slug,
  label,
  is_active: true,
  credential_source: { type: 'personal' },
});
const inventory = [
  service('us-ornn', 'ornn-api', 'Ornn'),
  service('us-llm', 'chrono-llm-public', 'Chrono LLM'),
  service('us-github', 'api-github', 'GitHub'),
  service('us-firecrawl', 'api-firecrawl', 'Firecrawl'),
  service('us-lark', 'api-lark-bot', 'Lark Bot API'),
];
const unbound = {
  id: null,
  nyx_channel_bot_id: 'bot-alpha',
  platform: 'discord',
  label: 'Support bot',
  owned: true,
  binding_status: 'unbound',
  availability_status: 'available',
  nyx_status: 'active',
  skill_name: '',
  authorization_mode: null,
  service_ids: [],
};
const bound = {
  ...unbound,
  id: 'reg-alpha',
  nyx_channel_bot_id: 'bot-saved',
  binding_status: 'bound',
  skill_name: 'saved-skill',
  authorization_mode: 'explicit_service_allowlist',
  service_ids: ['us-ornn', 'us-llm'],
  state_version: 1,
};
const skillId = '76ca33e8-0807-43f7-919d-de67e7428217';
const bindHref = (botId = 'bot-alpha') =>
  `/scopes/scope-alpha/channels/bind/${botId}?skillId=${skillId}&requiredServiceId=us-firecrawl&requiredServiceId=us-lark#services`;
const response = (value: unknown) =>
  ({ ok: true, status: 200, json: async () => value }) as Response;
const writes = () =>
  fetchMock.mock.calls.filter(([, init]) =>
    ['POST', 'PATCH'].includes(init?.method ?? ''),
  );
function grant(ids = baseIds) {
  persistAuthSession(createNyxIDServiceSession({ allowed_service_ids: ids }));
}
function mount(href = bindHref()) {
  window.history.replaceState({}, '', href);
  return renderWithQueryClient(<WorkflowActivityVNextPage />);
}
async function chooseSupport() {
  fireEvent.mouseDown(await screen.findByRole('combobox'));
  fireEvent.click(
    await screen.findByText('support', {
      selector: '.channels__skill-option strong',
    }),
  );
}

beforeEach(() => {
  grant();
  window.sessionStorage.clear();
  fetchMock.mockReset();
  mockToast.success.mockReset();
  mockToast.error.mockReset();
  fetchMock.mockImplementation(async (input, init) => {
    const url = String(input);
    if (init?.method === 'POST')
      return {
        ...response({
          status: 'accepted',
          registration_id: 'reg-alpha',
          command_id: 'cmd-alpha',
        }),
        status: 202,
      };
    if (url.endsWith('/user-services'))
      return response({ services: inventory });
    if (url.includes('/skill-search'))
      return response({
        data: {
          items: [{ guid: 'skill-support', name: 'support', description: '' }],
          meta: { hasMore: false },
        },
        error: null,
      });
    if (url.endsWith(`/skills/${skillId}`))
      return response({
        data: { guid: skillId, name: 'linked-default', description: '' },
        error: null,
      });
    if (url.endsWith('?scope=all'))
      return response([
        unbound,
        { ...unbound, nyx_channel_bot_id: 'bot-beta' },
        // Deliberately collides with an edit registration ID to test identity isolation.
        { ...unbound, nyx_channel_bot_id: 'reg-alpha' },
        bound,
      ]);
    if (url.endsWith('/registrations/reg-alpha')) return response(bound);
    throw new Error(`Unexpected request: ${url}`);
  });
});

it('preselects requested active services outside login consent and binds only after confirmation', async () => {
  grant([]);
  const { queryClient } = mount();
  const firecrawl = await screen.findByRole('checkbox', { name: /Firecrawl/ });
  expect(firecrawl).toBeEnabled();
  expect(firecrawl).toBeChecked();
  expect(screen.getByRole('checkbox', { name: /Lark Bot API/ })).toBeEnabled();
  expect(
    screen.queryByRole('button', { name: /Manage service access/ }),
  ).not.toBeInTheDocument();
  expect(screen.queryByText(/Missing a service/)).not.toBeInTheDocument();
  await screen.findByText('linked-default');
  await chooseSupport();
  fireEvent.click(screen.getByRole('checkbox', { name: /GitHub/ }));
  fireEvent.click(screen.getByRole('checkbox', { name: /Lark Bot API/ }));
  expect(writes()).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Bind bot' }));
  await screen.findByText('Confirming your changes...');
  expect(writes()).toHaveLength(1);
  expect(writes()[0][0]).toBe('/api/channels/registrations');
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({
    nyx_channel_bot_id: 'bot-alpha',
    skill_name: 'support',
    authorization_mode: 'explicit_service_allowlist',
    service_ids: ['us-firecrawl', 'us-github', 'us-llm', 'us-ornn'],
  });
  expect(mockToast.success).not.toHaveBeenCalled();
  const normalFetch = fetchMock.getMockImplementation();
  if (!normalFetch) throw new Error('Missing request fixture');
  fetchMock.mockImplementation((input, init) =>
    String(input).endsWith('?scope=all')
      ? Promise.resolve(
          response([
            {
              ...unbound,
              id: 'reg-alpha',
              binding_status: 'bound',
              skill_name: 'support',
              authorization_mode: 'explicit_service_allowlist',
              service_ids: ['us-firecrawl', 'us-github', 'us-llm', 'us-ornn'],
              state_version: 1,
            },
          ]),
        )
      : normalFetch(input, init),
  );
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: ['channels', 'scope-alpha', 'confirmation', 'cmd-alpha'],
    });
  });
  await waitFor(() =>
    expect(mockToast.success).toHaveBeenCalledWith('Bot bound successfully.'),
  );
  expect(window.location.pathname).toBe(
    '/scopes/scope-alpha/channels/reg-alpha',
  );
});

it('preserves deselection across inventory refreshes and URL changes in the same form', async () => {
  const { queryClient } = mount();
  const firecrawl = await screen.findByRole('checkbox', { name: /Firecrawl/ });
  expect(firecrawl).toBeChecked();
  fireEvent.click(firecrawl);
  await act(async () => {
    // A new URL hint must not select an additional service after entry.
    window.history.replaceState(
      {},
      '',
      `${bindHref().split('#')[0]}&requiredServiceId=us-github`,
    );
    window.dispatchEvent(new PopStateEvent('popstate'));
    await queryClient.invalidateQueries({
      queryKey: channelKeys.services('scope-alpha'),
    });
  });
  expect(screen.getByRole('checkbox', { name: /Firecrawl/ })).not.toBeChecked();
  expect(screen.getByRole('checkbox', { name: /GitHub/ })).not.toBeChecked();
  expect(screen.getByRole('checkbox', { name: /Lark Bot API/ })).toBeChecked();
  expect(writes()).toHaveLength(0);
});

it('initializes a newly entered bot independently of the previous bot choices', async () => {
  mount();
  const firecrawl = await screen.findByRole('checkbox', { name: /Firecrawl/ });
  fireEvent.click(firecrawl);
  expect(firecrawl).not.toBeChecked();
  await act(async () => {
    window.history.replaceState({}, '', bindHref('bot-beta'));
    window.dispatchEvent(new PopStateEvent('popstate'));
  });
  await waitFor(() =>
    expect(screen.getByRole('checkbox', { name: /Firecrawl/ })).toBeChecked(),
  );
  expect(writes()).toHaveLength(0);
});

it('keeps manual choices made from cached inventory while the first fresh response is pending', async () => {
  const normalFetch = fetchMock.getMockImplementation();
  if (!normalFetch) throw new Error('Missing request fixture');
  let complete!: (value: Response) => void;
  const pending = new Promise<Response>((resolve) => {
    complete = resolve;
  });
  fetchMock.mockImplementation((input, init) =>
    String(input).endsWith('/user-services')
      ? pending
      : normalFetch(input, init),
  );
  const queryClient = createTestQueryClient();
  queryClient.setQueryData(
    channelKeys.services('scope-alpha'),
    inventory.map((item) => ({
      id: item.id,
      slug: item.slug,
      label: item.label,
      active: true,
      allowed: true,
      source: 'personal',
      organizationName: null,
    })),
  );
  window.history.replaceState({}, '', bindHref());
  renderWithQueryClient(<WorkflowActivityVNextPage />, queryClient);
  fireEvent.click(await screen.findByRole('checkbox', { name: /GitHub/ }));
  await act(async () => {
    complete(response({ services: inventory }));
  });
  expect(screen.getByRole('checkbox', { name: /GitHub/ })).toBeChecked();
  expect(screen.getByRole('checkbox', { name: /Firecrawl/ })).not.toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: /Lark Bot API/ }),
  ).not.toBeChecked();
  expect(writes()).toHaveLength(0);
});
