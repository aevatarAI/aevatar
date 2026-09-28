import {
  act,
  cleanup,
  fireEvent,
  screen,
  waitFor,
} from '@testing-library/react';
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

it('shows exact missing services from the link without selecting or granting them, even when another account has the same slug', async () => {
  grant([...baseIds, 'us-firecrawl-other']);
  mount(
    `${editHref}&requiredServiceId=us-firecrawl&requiredServiceId=unknown-service`,
  );
  await screen.findByText('Service access needed');
  expect(screen.getAllByText('Firecrawl', { exact: true })).toHaveLength(1);
  expect(screen.getByText('Lark Bot API')).toBeInTheDocument();
  expect(screen.getByText('Service not found')).toBeInTheDocument();
  expect(
    screen.getByText(/choose Customize under Service access/),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('checkbox', { name: /^Firecrawl/ }),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole('checkbox', { name: /Other Firecrawl account/ }),
  ).not.toBeChecked();
  expect(screen.getByText('3 selected')).toBeInTheDocument();
  expect(writes()).toHaveLength(0);
});

it('restores the draft after full consent, checks fresh grants and requires selecting the newly available service before saving', async () => {
  const review = jest
    .spyOn(NyxIDAuthClient.prototype, 'loginWithRedirect')
    .mockResolvedValue();
  const first = mount();
  fireEvent.change(await screen.findByLabelText('Label'), {
    target: { value: 'Edited label' },
  });
  fireEvent.click(await screen.findByRole('checkbox', { name: /GitHub/ }));
  fireEvent.click(
    screen.getByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() =>
    expect(review).toHaveBeenCalledWith({
      flow: 'serviceAccessReview',
      returnTo: editHref,
    }),
  );
  expect(writes()).toHaveLength(0);
  const leaving = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(leaving);
  expect(leaving.defaultPrevented).toBe(false);
  first.unmount();
  grant([...baseIds, 'us-firecrawl']);
  mount();
  expect(await screen.findByLabelText('Label')).toHaveValue('Edited label');
  expect(
    await screen.findByText(/Your unsaved changes have been restored/),
  ).toBeInTheDocument();
  expect(screen.getByText('Lark Bot API')).toBeInTheDocument();
  const firecrawl = screen.getByRole('checkbox', { name: /Firecrawl/ });
  expect(firecrawl).not.toBeChecked();
  expect(screen.getByRole('checkbox', { name: /GitHub/ })).not.toBeChecked();
  fireEvent.click(firecrawl);
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByText('Confirming your changes...');
  const post = writes().find(([, init]) => init?.method === 'POST');
  expect(JSON.parse(String(post?.[1]?.body))).toMatchObject({
    service_ids: ['us-firecrawl', 'us-llm', 'us-ornn'],
  });
  expect(mockToast.success).not.toHaveBeenCalled();
});

it('retains missing access and draft after cancellation, and recovers from a failed review launch', async () => {
  const review = jest
    .spyOn(NyxIDAuthClient.prototype, 'loginWithRedirect')
    .mockResolvedValue();
  const first = mount();
  fireEvent.change(await screen.findByLabelText('Label'), {
    target: { value: 'Edited label' },
  });
  fireEvent.click(
    await screen.findByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() => expect(review).toHaveBeenCalledTimes(1));
  first.unmount();
  mount();
  expect(
    await screen.findByText(/Your unsaved changes have been restored/),
  ).toBeInTheDocument();
  expect(screen.getByText('Service access needed')).toBeInTheDocument();
  expect(screen.getByLabelText('Label')).toHaveValue('Edited label');
  review.mockRejectedValueOnce(new Error('TEST_PRIVATE_ERROR'));
  fireEvent.click(
    screen.getByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() =>
    expect(mockToast.error).toHaveBeenCalledWith(
      'Could not open NyxID. Your changes are still here. Try again.',
    ),
  );
  expect(
    screen.getByRole('button', { name: /Manage service access/ }),
  ).toBeEnabled();
  expect(screen.getByLabelText('Label')).toHaveValue('Edited label');
  const leaving = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(leaving);
  expect(leaving.defaultPrevented).toBe(true);
  fireEvent.click(
    screen.getByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() => expect(review).toHaveBeenCalledTimes(3));
  expect(writes()).toHaveLength(0);
});

it('keeps the editor open when draft storage is unavailable and does not restore a draft for another account', async () => {
  const review = jest
    .spyOn(NyxIDAuthClient.prototype, 'loginWithRedirect')
    .mockResolvedValue();
  const first = mount();
  fireEvent.change(await screen.findByLabelText('Label'), {
    target: { value: 'Edited label' },
  });
  const storage = jest
    .spyOn(Storage.prototype, 'setItem')
    .mockImplementation(() => {
      throw new Error('Storage blocked');
    });
  fireEvent.click(
    await screen.findByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() => expect(mockToast.error).toHaveBeenCalled());
  expect(review).not.toHaveBeenCalled();
  expect(screen.getByLabelText('Label')).toHaveValue('Edited label');
  storage.mockRestore();
  fireEvent.click(
    screen.getByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() => expect(review).toHaveBeenCalledTimes(1));
  first.unmount();
  const other = createNyxIDServiceSession({
    sub: 'user-other',
    allowed_service_ids: baseIds,
  });
  persistAuthSession({ ...other, user: { sub: 'user-other' } });
  mount();
  expect(await screen.findByLabelText('Label')).toHaveValue('Support bot');
  await screen.findByText('Service access needed');
  expect(
    screen.queryByText(/Your unsaved changes have been restored/),
  ).not.toBeInTheDocument();
});

it('refreshes grants on browser history restoration and requires resolving revoked selections before saving', async () => {
  const review = jest
    .spyOn(NyxIDAuthClient.prototype, 'loginWithRedirect')
    .mockResolvedValue();
  mount();
  fireEvent.change(await screen.findByLabelText('Label'), {
    target: { value: 'Edited label' },
  });
  await screen.findByText('Service access needed');
  fireEvent.click(
    screen.getByRole('button', { name: /Manage service access/ }),
  );
  await waitFor(() => expect(review).toHaveBeenCalledTimes(1));
  const draftKeys = () =>
    Object.keys(window.sessionStorage).filter((key) =>
      key.startsWith('aevatar:channel-access-draft:'),
    );
  expect(draftKeys()).toHaveLength(1);
  grant(['us-ornn', 'us-llm', 'us-firecrawl']);
  act(() => {
    const restored = new Event('pageshow');
    Object.defineProperty(restored, 'persisted', { value: true });
    window.dispatchEvent(restored);
  });
  await screen.findByText(/Some selected services are unavailable/);
  expect(draftKeys()).toHaveLength(0);
  expect(screen.getByLabelText('Label')).toHaveValue('Edited label');
  expect(screen.getByRole('checkbox', { name: /Firecrawl/ })).not.toBeChecked();
  expect(
    screen.getByRole('button', { name: /Manage service access/ }),
  ).toBeEnabled();
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  fireEvent.click(screen.getByRole('checkbox', { name: /GitHub/ }));
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled();
  const leaving = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(leaving);
  expect(leaving.defaultPrevented).toBe(true);
  expect(writes()).toHaveLength(0);
});

afterEach(cleanup);
