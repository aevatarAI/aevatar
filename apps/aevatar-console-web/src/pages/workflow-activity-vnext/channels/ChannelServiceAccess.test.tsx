import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { createNyxIDServiceSession } from '../../../../tests/fixtures/nyxidServiceSession';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
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
  service('us-firecrawl-other', 'api-firecrawl', 'Other Firecrawl account'),
];
const row = {
  id: 'reg-alpha',
  nyx_channel_bot_id: 'bot-alpha',
  platform: 'discord',
  label: 'Support bot',
  owned: true,
  binding_status: 'bound',
  availability_status: 'available',
  nyx_status: 'active',
  skill_name: 'support',
  authorization_mode: 'explicit_service_allowlist',
  service_ids: baseIds,
  state_version: 12,
};
const editHref =
  '/scopes/scope-alpha/channels/reg-alpha/edit?requiredServiceId=us-firecrawl&requiredServiceId=us-lark';
const response = (value: unknown) =>
  ({ ok: true, status: 200, json: async () => value }) as Response;
const writes = () =>
  fetchMock.mock.calls.filter(([, init]) =>
    ['POST', 'PATCH'].includes(init?.method ?? ''),
  );
function grant(ids = baseIds) {
  persistAuthSession(createNyxIDServiceSession({ allowed_service_ids: ids }));
}
function mount(href = editHref) {
  window.history.replaceState({}, '', href);
  return renderWithQueryClient(<WorkflowActivityVNextPage />);
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
          registration_id: row.id,
          command_id: 'cmd-alpha',
        }),
        status: 202,
      };
    if (init?.method === 'PATCH')
      return response({
        id: row.nyx_channel_bot_id,
        platform: row.platform,
        label: 'Edited label',
      });
    if (url.endsWith('/user-services'))
      return response({ services: inventory });
    if (url.includes('/skill-search'))
      return response({
        data: {
          items: [{ guid: 'skill-alpha', name: 'support', description: '' }],
          meta: { hasMore: false },
        },
        error: null,
      });
    if (url.endsWith('?scope=all')) return response([row]);
    return response(row);
  });
});

it('preselects requested exact IDs on Edit without changing saved services until Save', async () => {
  grant([]);
  mount(
    `${editHref}&requiredServiceId=us-firecrawl&requiredServiceId=unknown-service`,
  );
  const firecrawl = await screen.findByRole('checkbox', { name: /^Firecrawl/ });
  expect(firecrawl).toBeEnabled();
  expect(firecrawl).toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: /Other Firecrawl account/ }),
  ).not.toBeChecked();
  expect(screen.getByRole('checkbox', { name: /GitHub/ })).toBeChecked();
  expect(screen.getByText('5 selected')).toBeInTheDocument();
  expect(screen.getByText('Service not found')).toBeInTheDocument();
  expect(
    screen.queryByRole('button', { name: /Manage service access/ }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByText(/choose Customize under Service access/),
  ).not.toBeInTheDocument();
  expect(writes()).toHaveLength(0);
  fireEvent.click(screen.getByRole('checkbox', { name: /Lark Bot API/ }));
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByText('Confirming your changes...');
  expect(writes()).toHaveLength(1);
  expect(writes()[0][0]).toBe('/api/channels/registrations/reg-alpha');
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({
    skill_name: 'support',
    authorization_mode: 'explicit_service_allowlist',
    service_ids: ['us-firecrawl', 'us-github', 'us-llm', 'us-ornn'],
  });
  expect(mockToast.success).not.toHaveBeenCalled();
});

it('preserves edits during failed inventory refresh, then removes inactive choices without reselecting them on reactivation', async () => {
  const { queryClient } = mount(editHref.split('?')[0]);
  fireEvent.change(await screen.findByLabelText('Label'), {
    target: { value: 'Edited label' },
  });
  await screen.findByRole('checkbox', { name: /GitHub/ });
  const normalFetch = fetchMock.getMockImplementation();
  if (!normalFetch) throw new Error('Missing request fixture');
  fetchMock.mockImplementation((input, init) => {
    if (String(input).endsWith('/user-services'))
      return Promise.resolve({ ok: false, status: 503 } as Response);
    return normalFetch(input, init);
  });
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: channelKeys.services('scope-alpha'),
    });
  });
  await screen.findByText(
    'Could not load your services. Try again before saving.',
  );
  expect(screen.getByText('3 selected')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  fetchMock.mockImplementation((input, init) =>
    String(input).endsWith('/user-services')
      ? Promise.resolve(
          response({
            services: inventory.map((item) =>
              item.id === 'us-github' ? { ...item, is_active: false } : item,
            ),
          }),
        )
      : normalFetch(input, init),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
  await screen.findByText('2 selected');
  expect(
    screen.queryByRole('checkbox', { name: /GitHub/ }),
  ).not.toBeInTheDocument();
  expect(screen.getByLabelText('Label')).toHaveValue('Edited label');
  fetchMock.mockImplementation(normalFetch);
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: channelKeys.services('scope-alpha'),
    });
  });
  expect(
    await screen.findByRole('checkbox', { name: /GitHub/ }),
  ).not.toBeChecked();
  expect(screen.getByText('2 selected')).toBeInTheDocument();
  const leaving = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(leaving);
  expect(leaving.defaultPrevented).toBe(true);
  expect(writes()).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByText('Confirming your changes...');
  const post = writes().find(([, init]) => init?.method === 'POST');
  expect(JSON.parse(String(post?.[1]?.body)).service_ids).toEqual([
    'us-llm',
    'us-ornn',
  ]);
});

it('removes saved missing and inactive services while preserving active selections outside login consent', async () => {
  grant([]);
  const normalFetch = fetchMock.getMockImplementation();
  if (!normalFetch) throw new Error('Missing request fixture');
  const saved = {
    ...row,
    service_ids: [...baseIds, 'us-firecrawl', 'us-deleted'],
  };
  fetchMock.mockImplementation((input, init) => {
    const url = String(input);
    if (url.endsWith('/user-services'))
      return Promise.resolve(
        response({
          services: inventory.map((item) =>
            item.id === 'us-firecrawl' ? { ...item, is_active: false } : item,
          ),
        }),
      );
    if (url.endsWith('?scope=all')) return Promise.resolve(response([saved]));
    if (url.endsWith('/registrations/reg-alpha') && init?.method !== 'POST')
      return Promise.resolve(response(saved));
    return normalFetch(input, init);
  });
  mount(`${editHref.split('?')[0]}?requiredServiceId=us-firecrawl`);
  await screen.findByText('Requested services unavailable');
  expect(screen.getByText('Firecrawl', { exact: true })).toBeInTheDocument();
  expect(
    screen.queryByRole('checkbox', { name: /^Firecrawl|us-deleted/ }),
  ).not.toBeInTheDocument();
  expect(screen.getByRole('checkbox', { name: /GitHub/ })).toBeChecked();
  expect(screen.getByText('3 selected')).toBeInTheDocument();
  expect(writes()).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByText('Confirming your changes...');
  expect(JSON.parse(String(writes()[0][1]?.body)).service_ids).toEqual([
    'us-github',
    'us-llm',
    'us-ornn',
  ]);
});

it('waits for the first successful inventory and only preselects active account-available exact IDs', async () => {
  const normalFetch = fetchMock.getMockImplementation();
  if (!normalFetch) throw new Error('Missing request fixture');
  let loaded = false;
  fetchMock.mockImplementation((input, init) => {
    if (String(input).endsWith('/user-services')) {
      if (!loaded)
        return Promise.resolve({ ok: false, status: 503 } as Response);
      return Promise.resolve(
        response({
          services: inventory.map((item) =>
            item.id === 'us-firecrawl'
              ? { ...item, is_active: false }
              : item.id === 'us-lark'
                ? {
                    ...item,
                    credential_source: {
                      type: 'org',
                      allowed: false,
                      org_name: 'Example',
                    },
                  }
                : item,
          ),
        }),
      );
    }
    return normalFetch(input, init);
  });
  const { queryClient } = mount(
    `${editHref}&requiredServiceId=us-firecrawl-other&requiredServiceId=unknown`,
  );
  await screen.findByText(
    'Could not load your services. Try again before saving.',
  );
  expect(screen.getByText('3 selected')).toBeInTheDocument();
  loaded = true;
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
  const other = await screen.findByRole('checkbox', {
    name: /Other Firecrawl account/,
  });
  await waitFor(() => expect(other).toBeChecked());
  expect(
    screen.queryByRole('checkbox', { name: /^Firecrawl/ }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole('checkbox', { name: /Lark Bot API/ }),
  ).not.toBeInTheDocument();
  expect(screen.getByText('4 selected')).toBeInTheDocument();
  expect(
    screen.getByText('Requested services unavailable'),
  ).toBeInTheDocument();
  fetchMock.mockImplementation(normalFetch);
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: channelKeys.services('scope-alpha'),
    });
  });
  expect(
    await screen.findByRole('checkbox', { name: /^Firecrawl/ }),
  ).not.toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: /Lark Bot API/ }),
  ).not.toBeChecked();
  expect(other).toBeChecked();
  expect(writes()).toHaveLength(0);
});
