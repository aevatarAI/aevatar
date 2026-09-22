import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { createNyxIDServiceSession } from '../../../../tests/fixtures/nyxidServiceSession';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextPage from '../index';
import { channelKeys } from './queries';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn().mockResolvedValue({ authenticated: false }),
  },
}));

const fetchMock = jest.mocked(authFetch);
const response = (value: unknown, status = 200) =>
  ({ ok: status === 200, status, json: async () => value }) as Response;
const serviceIds = ['service-ornn', 'service-llm'];
const bound = {
  id: 'reg-alpha',
  nyx_channel_bot_id: 'bot-alpha',
  platform: 'lark',
  label: 'Support bot',
  owned: true,
  binding_status: 'bound',
  availability_status: 'available',
  nyx_status: 'active',
  skill_name: 'saved-support',
  authorization_mode: 'explicit_service_allowlist',
  service_ids: serviceIds,
  state_version: 12,
};
const unbound = {
  ...bound,
  id: null,
  binding_status: 'unbound',
  skill_name: '',
  authorization_mode: null,
  service_ids: [],
};
const skills = {
  data: {
    items: [{ guid: 'skill-guid-support', name: 'support', description: '' }],
    meta: { hasMore: false },
  },
  error: null,
};
const writes = () =>
  fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST');

function openBind(search = '', botId = 'bot-alpha') {
  window.history.replaceState(
    {},
    '',
    `/scopes/scope-alpha/channels/bind/${botId}${search}`,
  );
}

function selectedSkill() {
  return screen.getByRole('combobox').closest('.ant-select');
}

beforeEach(() => {
  fetchMock.mockReset();
  persistAuthSession(
    createNyxIDServiceSession({ allowed_service_ids: serviceIds }),
  );
  fetchMock.mockImplementation(async (input, init) => {
    if (init?.method === 'POST')
      return response({ error: 'insecure_webhook_base_url' }, 400);
    if (String(input).includes('/skill-search')) return response(skills);
    if (String(input).endsWith('/user-services'))
      return response({
        services: [
          { id: serviceIds[0], slug: 'ornn-api', label: 'Ornn' },
          { id: serviceIds[1], slug: 'chrono-llm-public', label: 'LLM' },
        ].map((service) => ({
          ...service,
          is_active: true,
          credential_source: { type: 'personal' },
        })),
      });
    if (input === '/api/channels/registrations')
      return response([
        unbound,
        { ...unbound, nyx_channel_bot_id: 'bot-beta' },
      ]);
    throw new Error(`Unexpected request: ${input}`);
  });
});

it('prefills an encoded skill before the catalogue loads and submits only on explicit bind', async () => {
  const skillName = '预约+容量 & 100%20';
  const normalFetch = fetchMock.getMockImplementation();
  if (!normalFetch) throw new Error('Missing request fixture');
  let completeSkills!: (value: Response) => void;
  const pendingSkills = new Promise<Response>((resolve) => {
    completeSkills = resolve;
  });
  fetchMock.mockImplementation((input, init) =>
    String(input).includes('/skill-search')
      ? pendingSkills
      : normalFetch(input, init),
  );
  openBind(`?skill=${encodeURIComponent(`  ${skillName}  `)}`);
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await screen.findByRole('combobox');
  expect(selectedSkill()).toHaveTextContent(skillName);
  expect(writes()).toHaveLength(0);

  await act(async () => completeSkills(response(skills)));
  expect(selectedSkill()).toHaveTextContent(skillName);
  const bind = screen.getByRole('button', { name: 'Bind bot' });
  await waitFor(() => expect(bind).toBeEnabled());
  fireEvent.click(bind);
  await screen.findByRole('alert');
  expect(writes()).toHaveLength(1);
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({
    nyx_channel_bot_id: 'bot-alpha',
    skill_name: skillName,
    authorization_mode: 'explicit_service_allowlist',
    service_ids: ['service-llm', 'service-ornn'],
  });
});

it('keeps manual replacement and clearing through catalogue refresh and same-bot query changes', async () => {
  openBind('?skill=booking-capacity');
  const { queryClient } = renderWithQueryClient(<WorkflowActivityVNextPage />);
  fireEvent.mouseDown(await screen.findByRole('combobox'));
  fireEvent.click(
    await screen.findByText('support', {
      selector: '.channels__skill-option strong',
    }),
  );
  expect(selectedSkill()).toHaveTextContent('support');
  await act(async () => {
    openBind('?skill=another-default');
    window.dispatchEvent(new PopStateEvent('popstate'));
    await queryClient.invalidateQueries({
      queryKey: channelKeys.skills('scope-alpha', ''),
    });
  });
  expect(selectedSkill()).toHaveTextContent('support');
  fireEvent.mouseDown(screen.getByRole('img', { name: 'close-circle' }));
  await act(async () => {
    await queryClient.invalidateQueries({
      queryKey: channelKeys.skills('scope-alpha', ''),
    });
  });
  expect(selectedSkill()).toHaveTextContent('Select a skill');
  fireEvent.click(screen.getByRole('button', { name: 'Bind bot' }));
  await screen.findByRole('alert');
  expect(JSON.parse(String(writes()[0][1]?.body)).skill_name).toBe('');
});

it('starts another bot binding with its own URL default', async () => {
  openBind('?skill=alpha-skill');
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await screen.findByRole('combobox');
  expect(selectedSkill()).toHaveTextContent('alpha-skill');
  await act(async () => {
    openBind('?skill=beta-skill', 'bot-beta');
    window.dispatchEvent(new PopStateEvent('popstate'));
  });
  await screen.findByRole('combobox');
  expect(selectedSkill()).toHaveTextContent('beta-skill');
  expect(writes()).toHaveLength(0);
});

it('ignores the URL default when editing a saved registration', async () => {
  const normalFetch = fetchMock.getMockImplementation();
  if (!normalFetch) throw new Error('Missing request fixture');
  fetchMock.mockImplementation((input, init) => {
    if (input === '/api/channels/registrations')
      return Promise.resolve(response([bound]));
    if (input === '/api/channels/registrations/reg-alpha')
      return Promise.resolve(response(bound));
    return normalFetch(input, init);
  });
  window.history.replaceState(
    {},
    '',
    '/scopes/scope-alpha/channels/reg-alpha/edit?skill=booking-capacity',
  );
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await screen.findByRole('combobox');
  expect(selectedSkill()).toHaveTextContent('saved-support');
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  expect(writes()).toHaveLength(0);
});

it.each([
  '',
  '?skill=%20%20',
  `?skill=${'x'.repeat(129)}`,
])('leaves the skill optional for absent, blank or oversized defaults: %s', async (search) => {
  openBind(search);
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await screen.findByRole('combobox');
  expect(selectedSkill()).toHaveTextContent('Select a skill');
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Bind bot' })).toBeEnabled(),
  );
  expect(writes()).toHaveLength(0);
});
