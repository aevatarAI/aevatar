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
const skillId = '76ca33e8-0807-43f7-919d-de67e7428217';
const secondSkillId = '29018a73-0fa5-475a-a05a-d158f7b0f392';
const linkedSkill = (id = skillId) => ({
  data: {
    guid: id,
    name: id === skillId ? 'booking-capacity-renamed' : 'beta-skill',
    description: '',
  },
  error: null,
});
const detailReads = () =>
  fetchMock.mock.calls.filter(([input]) =>
    String(input).includes('/api/v1/skills/'),
  );
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
    if (String(input).endsWith(`/skills/${skillId}`))
      return response(linkedSkill());
    if (String(input).endsWith(`/skills/${secondSkillId}`))
      return response(linkedSkill(secondSkillId));
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

function deferDefaultSkill() {
  const normalFetch = fetchMock.getMockImplementation();
  if (!normalFetch) throw new Error('Missing request fixture');
  let complete!: (value: Response) => void;
  const pending = new Promise<Response>((resolve) => {
    complete = resolve;
  });
  fetchMock.mockImplementation((input, init) =>
    String(input).endsWith(`/skills/${skillId}`)
      ? pending
      : normalFetch(input, init),
  );
  return complete;
}

it('resolves the exact ID outside the search results, then explicitly binds its current name', async () => {
  const complete = deferDefaultSkill();
  openBind(`?skillId=${encodeURIComponent(`  ${skillId}  `)}`);
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await screen.findByRole('combobox');
  const bind = screen.getByRole('button', { name: 'Bind bot' });
  expect(bind).toBeDisabled();
  expect(selectedSkill()).toHaveTextContent('Select a skill');
  expect(selectedSkill()).not.toHaveTextContent(skillId);
  await screen.findByText('Loading linked skill...');
  expect(writes()).toHaveLength(0);

  await act(async () => complete(response(linkedSkill())));
  await waitFor(() =>
    expect(selectedSkill()).toHaveTextContent('booking-capacity-renamed'),
  );
  await waitFor(() => expect(bind).toBeEnabled());
  expect(writes()).toHaveLength(0);
  fireEvent.click(bind);
  await screen.findByRole('alert');
  expect(writes()).toHaveLength(1);
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({
    nyx_channel_bot_id: 'bot-alpha',
    skill_name: 'booking-capacity-renamed',
    authorization_mode: 'explicit_service_allowlist',
    service_ids: ['service-llm', 'service-ornn'],
  });
});

it('preserves manual selection and clearing when a default arrives late or the URL and catalogue refresh', async () => {
  const complete = deferDefaultSkill();
  openBind(`?skillId=${skillId}`);
  const { queryClient } = renderWithQueryClient(<WorkflowActivityVNextPage />);
  fireEvent.mouseDown(await screen.findByRole('combobox'));
  fireEvent.click(
    await screen.findByText('support', {
      selector: '.channels__skill-option strong',
    }),
  );
  expect(selectedSkill()).toHaveTextContent('support');
  fireEvent.mouseDown(screen.getByRole('img', { name: 'close-circle' }));
  await act(async () => {
    complete(response(linkedSkill()));
    openBind(`?skillId=${secondSkillId}`);
    window.dispatchEvent(new PopStateEvent('popstate'));
    await queryClient.invalidateQueries({
      queryKey: channelKeys.skills('scope-alpha', ''),
    });
  });
  expect(selectedSkill()).toHaveTextContent('Select a skill');
  fireEvent.click(screen.getByRole('button', { name: 'Bind bot' }));
  await screen.findByRole('alert');
  expect(JSON.parse(String(writes()[0][1]?.body)).skill_name).toBe('');
});

it('isolates a new bot default from a previous bot lookup that finishes late', async () => {
  const complete = deferDefaultSkill();
  openBind(`?skillId=${skillId}`);
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await screen.findByText('Loading linked skill...');
  await act(async () => {
    openBind(`?skillId=${secondSkillId}`, 'bot-beta');
    window.dispatchEvent(new PopStateEvent('popstate'));
  });
  await waitFor(() => expect(selectedSkill()).toHaveTextContent('beta-skill'));
  await act(async () => complete(response(linkedSkill())));
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
    `/scopes/scope-alpha/channels/reg-alpha/edit?skillId=${skillId}`,
  );
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await screen.findByRole('combobox');
  expect(selectedSkill()).toHaveTextContent('saved-support');
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  expect(detailReads()).toHaveLength(0);
  expect(writes()).toHaveLength(0);
});

it('blocks binding an unavailable default and lets the user retry the exact ID', async () => {
  const complete = deferDefaultSkill();
  openBind(`?skillId=${skillId}`);
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await act(async () =>
    complete(response({ error: { message: 'PRIVATE_SERVER_DETAIL' } }, 404)),
  );
  await screen.findByRole('alert');
  expect(document.body).not.toHaveTextContent('PRIVATE_SERVER_DETAIL');
  expect(screen.getByRole('button', { name: 'Bind bot' })).toBeDisabled();
  expect(writes()).toHaveLength(0);
  const normalFetch = fetchMock.getMockImplementation();
  if (!normalFetch) throw new Error('Missing request fixture');
  fetchMock.mockImplementation((input, init) =>
    String(input).endsWith(`/skills/${skillId}`)
      ? Promise.resolve(response(linkedSkill()))
      : normalFetch(input, init),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Retry linked skill' }));
  await waitFor(() =>
    expect(selectedSkill()).toHaveTextContent('booking-capacity-renamed'),
  );
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Bind bot' })).toBeEnabled(),
  );
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  expect(writes()).toHaveLength(0);
});

it('allows an explicit opt-out of a failed default without silently binding an empty skill', async () => {
  const complete = deferDefaultSkill();
  openBind(`?skillId=${skillId}`);
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await act(async () =>
    complete(
      response({
        error: null,
        data: { guid: secondSkillId, name: 'wrong-skill' },
      }),
    ),
  );
  await screen.findByRole('alert');
  expect(selectedSkill()).not.toHaveTextContent('wrong-skill');
  expect(screen.getByRole('button', { name: 'Bind bot' })).toBeDisabled();
  fireEvent.click(
    screen.getByRole('button', { name: 'Continue without a skill' }),
  );
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Bind bot' })).toBeEnabled(),
  );
  expect(writes()).toHaveLength(0);
  fireEvent.click(screen.getByRole('button', { name: 'Bind bot' }));
  await screen.findByRole('alert');
  expect(JSON.parse(String(writes()[0][1]?.body)).skill_name).toBe('');
});

it('keeps a blank default optional and does not interpret the old name parameter', async () => {
  openBind('?skillId=%20%20&skill=booking-capacity');
  renderWithQueryClient(<WorkflowActivityVNextPage />);
  await screen.findByRole('combobox');
  expect(selectedSkill()).toHaveTextContent('Select a skill');
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Bind bot' })).toBeEnabled(),
  );
  expect(detailReads()).toHaveLength(0);
  expect(writes()).toHaveLength(0);
});
