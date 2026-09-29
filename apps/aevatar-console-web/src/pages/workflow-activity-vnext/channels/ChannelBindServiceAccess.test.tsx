import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { createNyxIDServiceSession } from '../../../../tests/fixtures/nyxidServiceSession';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextPage from '../index';

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
const detailReads = () =>
  fetchMock.mock.calls.filter(([input]) =>
    String(input).endsWith(`/skills/${skillId}`),
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

it('restores an unbound bot draft and fresh requested access after consent, then binds only on explicit submission and confirms the observed result', async () => {
  const review = jest
    .spyOn(NyxIDAuthClient.prototype, 'loginWithRedirect')
    .mockResolvedValue();
  const first = mount();
  await screen.findByText('Service access needed');
  expect(screen.getByText('Firecrawl')).toBeInTheDocument();
  expect(screen.getByText('Lark Bot API')).toBeInTheDocument();
  expect(
    screen.queryByRole('checkbox', { name: /Firecrawl/ }),
  ).not.toBeInTheDocument();
  await chooseSupport();
  fireEvent.click(screen.getByRole('checkbox', { name: /GitHub/ }));
  fireEvent.click(
    screen.getByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() =>
    expect(review).toHaveBeenCalledWith({
      flow: 'serviceAccessReview',
      returnTo: bindHref(),
    }),
  );
  expect(writes()).toHaveLength(0);
  first.unmount();

  grant([...baseIds, 'us-firecrawl']);
  const second = mount();
  await screen.findByText(/Your changes have been kept/);
  expect(screen.getByText('support', { exact: true })).toBeInTheDocument();
  expect(screen.queryByText('linked-default')).not.toBeInTheDocument();
  expect(detailReads()).toHaveLength(1);
  expect(screen.getByRole('checkbox', { name: /GitHub/ })).toBeChecked();
  expect(screen.getByText('Lark Bot API')).toBeInTheDocument();
  const firecrawl = screen.getByRole('checkbox', { name: /Firecrawl/ });
  expect(firecrawl).not.toBeChecked();
  expect(writes()).toHaveLength(0);
  fireEvent.click(firecrawl);
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
    await second.queryClient.invalidateQueries({
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

it('preserves an explicitly cleared default skill after cancelled consent without binding', async () => {
  const review = jest
    .spyOn(NyxIDAuthClient.prototype, 'loginWithRedirect')
    .mockResolvedValue();
  const first = mount();
  await screen.findByText('linked-default');
  fireEvent.mouseDown(screen.getByRole('img', { name: 'close-circle' }));
  fireEvent.click(
    screen.getByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() => expect(review).toHaveBeenCalledTimes(1));
  first.unmount();
  mount();
  await screen.findByText(/Your changes have been kept/);
  expect(screen.getByText('Select a skill')).toBeInTheDocument();
  expect(detailReads()).toHaveLength(1);
  expect(screen.getByText('Service access needed')).toBeInTheDocument();
  expect(writes()).toHaveLength(0);
  expect(screen.getByRole('button', { name: 'Bind bot' })).toBeEnabled();
});

it('isolates bind drafts by bot and from edit registrations even when their raw IDs coincide', async () => {
  const review = jest
    .spyOn(NyxIDAuthClient.prototype, 'loginWithRedirect')
    .mockResolvedValue();
  const first = mount(bindHref('reg-alpha'));
  await screen.findByText('Service access needed');
  await chooseSupport();
  fireEvent.click(screen.getByRole('checkbox', { name: /GitHub/ }));
  fireEvent.click(
    screen.getByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() => expect(review).toHaveBeenCalledTimes(1));
  first.unmount();
  const other = mount(bindHref('bot-beta'));
  await screen.findByText('linked-default');
  expect(
    screen.queryByText(/Your changes have been kept/),
  ).not.toBeInTheDocument();
  expect(
    await screen.findByRole('checkbox', { name: /GitHub/ }),
  ).not.toBeChecked();
  other.unmount();
  const edit = mount('/scopes/scope-alpha/channels/reg-alpha/edit');
  await screen.findByText('saved-skill');
  expect(
    screen.queryByText(/Your changes have been kept/),
  ).not.toBeInTheDocument();
  expect(
    await screen.findByRole('checkbox', { name: /GitHub/ }),
  ).not.toBeChecked();
  edit.unmount();
  mount(bindHref('reg-alpha'));
  await screen.findByText(/Your changes have been kept/);
  expect(screen.getByText('support', { exact: true })).toBeInTheDocument();
  expect(screen.getByRole('checkbox', { name: /GitHub/ })).toBeChecked();
  expect(writes()).toHaveLength(0);
});
