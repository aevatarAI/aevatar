import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import { channelRuntimeConfigApi } from '@/shared/api/channelRuntimeConfigApi';
import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { history } from '@/shared/navigation/history';
import { channelRuntimeConfigFixture as fixture } from '../../../../tests/fixtures/channelRuntimeConfig';
import { createNyxIDServiceSession } from '../../../../tests/fixtures/nyxidServiceSession';
import {
  createTestQueryClient,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextPage from '../index';
import ChannelEditPage from './ChannelEditPage';
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
const fetchMock = jest.mocked(authFetch);
const path = '/api/channels/registrations/registration-alpha/runtime-config';
const servicePath = 'https://nyx.example.test/api/v1/user-services';
const response = (value: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => value,
  }) as Response;
const botsPath = 'https://nyx.example.test/api/v1/channel-bots';
const bot = {
  id: 'bot-nyx-alpha',
  platform: 'telegram',
  label: 'Team channel',
};
const registration = {
  id: 'registration-alpha',
  scope_id: 'scope-alpha',
  platform: 'telegram',
  nyx_channel_bot_id: bot.id,
  owned: true,
};
function identityResponse(input: unknown) {
  if (input === botsPath) return response({ bots: [bot] });
  if (input === '/api/channels/registrations') return response([registration]);
  return undefined;
}
const receipt = {
  status: 'accepted',
  registration_id: 'registration-alpha',
  command_id: 'command-save',
};
const posts = () =>
  fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST');
const postBody = () => JSON.parse(String(posts().at(-1)?.[1]?.body));
const patches = () =>
  fetchMock.mock.calls.filter(([, init]) => init?.method === 'PATCH');
const editLabel = (value: string) =>
  fireEvent.change(screen.getByLabelText('Label'), { target: { value } });
const editSkill = (value: string) =>
  fireEvent.change(screen.getByLabelText(/^Skill name/), { target: { value } });
const save = () =>
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
const renderEditor = () =>
  renderWithQueryClient(
    <ChannelEditPage
      scopeId="scope-alpha"
      registrationId="registration-alpha"
    />,
  );
beforeEach(() => {
  fetchMock.mockReset();
  persistAuthSession(createNyxIDServiceSession());
  jest.spyOn(history, 'push').mockImplementation(() => {});
  jest.spyOn(history, 'replace').mockImplementation(() => {});
  fetchMock.mockImplementation(async (input, init) =>
    init?.method === 'POST'
      ? response(receipt, 202)
      : (identityResponse(input) ?? response(fixture)),
  );
});

it('opens Edit from channel details and saves once, preserving hidden config and returning after one matching readback', async () => {
  let resolvePost!: (value: Response) => void;
  const pendingPost = new Promise<Response>((resolve) => {
    resolvePost = resolve;
  });
  let current = fixture;
  fetchMock.mockImplementation(async (input, init) => {
    if (init?.method === 'POST') return pendingPost;
    const identity = identityResponse(input);
    if (identity) return identity;
    if (String(input).endsWith('/status'))
      return response({
        registration_id: 'registration-alpha',
        status: 'active',
      });
    return response(current);
  });
  window.history.replaceState(
    {},
    '',
    '/scopes/scope-alpha/channels/registration-alpha',
  );
  const view = renderWithQueryClient(<WorkflowActivityVNextPage />);
  fireEvent.click(await screen.findByRole('button', { name: 'Edit' }));
  expect(history.push).toHaveBeenCalledWith(
    '/scopes/scope-alpha/channels/registration-alpha/edit',
  );
  act(() => {
    window.history.replaceState(
      {},
      '',
      '/scopes/scope-alpha/channels/registration-alpha/edit',
    );
    window.dispatchEvent(new PopStateEvent('popstate'));
  });
  expect(await screen.findByLabelText(/^Skill name/)).toHaveValue(
    'team-helper',
  );
  expect(await screen.findByLabelText('Label')).toHaveValue('Team channel');
  expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  expect(screen.queryByText('Services')).not.toBeInTheDocument();
  expect(fetchMock.mock.calls.some(([input]) => input === servicePath)).toBe(
    false,
  );
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  expect(screen.queryByText('Advanced settings')).not.toBeInTheDocument();
  expect(
    screen.queryByLabelText(
      /Skill version|Bot instructions|Bot token|Channel name|Introduction/,
    ),
  ).not.toBeInTheDocument();
  editSkill('  changed-helper  ');
  save();
  fireEvent.submit(screen.getByRole('form', { name: 'Edit Telegram' }));
  expect(posts()).toHaveLength(1);
  expect(posts()[0][0]).toBe(path);
  expect(postBody()).toEqual({
    runtime_config: {
      ...fixture.runtime_config,
      default_skill: { name: 'changed-helper', version: '2.1' },
    },
  });
  expect(screen.getByLabelText(/^Skill name/)).toBeDisabled();
  current = {
    ...fixture,
    state_version: 13,
    default_skill: { name: 'changed-helper', version: '2.1' },
    runtime_config: {
      ...fixture.runtime_config,
      default_skill: { name: 'changed-helper', version: '2.1' },
    },
  };
  await act(async () => resolvePost(response(receipt, 202)));
  await screen.findByText('Channel changes saved.');
  expect(
    screen.queryByRole('button', { name: 'Check again' }),
  ).not.toBeInTheDocument();
  expect(posts()).toHaveLength(1);
  expect(fetchMock.mock.calls.filter(([input]) => input === path)).toHaveLength(
    3,
  );
  expect(history.replace).toHaveBeenCalledWith(
    '/scopes/scope-alpha/channels/registration-alpha',
  );
  expect(view.queryClient.getMutationCache().getAll()).toHaveLength(0);
});

it('returns to details when an accepted change is still delayed, without requiring another confirmation or resubmitting', async () => {
  renderEditor();
  await screen.findByLabelText('Label');
  editSkill('');
  save();
  await screen.findByText(
    'Changes submitted. They may take a moment to appear in channel details.',
  );
  expect(history.replace).toHaveBeenCalledWith(
    '/scopes/scope-alpha/channels/registration-alpha',
  );
  expect(screen.queryByText('Channel changes saved.')).not.toBeInTheDocument();
  expect(
    screen.queryByRole('button', { name: 'Check again' }),
  ).not.toBeInTheDocument();
  fireEvent.submit(screen.getByRole('form', { name: 'Edit Telegram' }));
  expect(posts()).toHaveLength(1);
  expect(fetchMock.mock.calls.filter(([input]) => input === path)).toHaveLength(
    3,
  );
  expect(postBody()).toEqual({
    runtime_config: {
      ...fixture.runtime_config,
      default_skill: { name: '', version: '' },
    },
  });
});

it('does not fetch service choices and handles rejected skill fields without leaking diagnostics', async () => {
  let rejected = false;
  fetchMock.mockImplementation(async (input, init) => {
    const identity = identityResponse(input);
    if (identity) return identity;
    if (init?.method === 'POST') {
      rejected = true;
      return response(
        {
          error: 'invalid_runtime_config',
          field_errors: [
            {
              field: 'runtime_config.default_skill.name',
              code: 'too_long',
              message: 'TEST_ONLY_SECRET',
            },
          ],
        },
        400,
      );
    }
    return response(fixture);
  });
  renderEditor();
  await screen.findByLabelText('Label');
  expect(fetchMock.mock.calls.some(([input]) => input === servicePath)).toBe(
    false,
  );
  editSkill('changed-helper');
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled();
  save();
  await screen.findByText(
    'Check the skill name. Use no more than 128 characters.',
  );
  expect(rejected).toBe(true);
  expect(screen.getByLabelText(/^Skill name/)).toHaveAttribute(
    'aria-invalid',
    'true',
  );
  expect(screen.getByLabelText(/^Skill name/)).toHaveValue('changed-helper');
  expect(document.body).not.toHaveTextContent('TEST_ONLY_SECRET');
  expect(
    await screen.findByRole('button', { name: 'Save changes' }),
  ).toBeEnabled();
  expect(history.replace).not.toHaveBeenCalled();
  expect(
    await screen.findByText(
      'Could not save channel changes. Review your choices and try again.',
    ),
  ).toBeInTheDocument();
  editSkill('retry-helper');
  save();
  await waitFor(() => expect(posts()).toHaveLength(2));
  expect(postBody().runtime_config.default_skill.name).toBe('retry-helper');
});

it('revalidates a cached detail and retries an unavailable response without exposing a stale editable form', async () => {
  const queryClient = createTestQueryClient();
  queryClient.setQueryData(
    channelKeys.config('scope-alpha', 'registration-alpha'),
    await channelRuntimeConfigApi.get('registration-alpha', 'scope-alpha'),
  );
  fetchMock.mockClear();
  fetchMock.mockResolvedValueOnce(response({ error: 'TEST_ONLY_SECRET' }, 404));
  renderWithQueryClient(
    <ChannelEditPage
      scopeId="scope-alpha"
      registrationId="registration-alpha"
    />,
    queryClient,
  );
  expect(screen.queryByLabelText(/^Skill name/)).not.toBeInTheDocument();
  await screen.findByText(
    'This channel is unavailable or you do not have access.',
  );
  expect(
    screen.queryByRole('button', { name: 'Save changes' }),
  ).not.toBeInTheDocument();
  expect(document.body).not.toHaveTextContent('TEST_ONLY_SECRET');
  expect(fetchMock.mock.calls.filter(([input]) => input === path)).toHaveLength(
    1,
  );
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
  expect(await screen.findByLabelText(/^Skill name/)).toHaveValue(
    'team-helper',
  );
  expect(posts()).toHaveLength(0);
});

it('preserves legacy default authorization and returns to details with accurate feedback when readback fails', async () => {
  let submitted = false;
  const legacy = {
    ...fixture,
    authorization_mode: 'nyxid_default',
    service_ids: null,
    runtime_config: { ...fixture.runtime_config, nyxid_service_selectors: [] },
  };
  fetchMock.mockImplementation(async (input, init) => {
    const identity = identityResponse(input);
    if (identity) return identity;
    if (init?.method === 'POST') {
      submitted = true;
      return response(receipt, 202);
    }
    if (submitted) return response({}, 503);
    return response(legacy);
  });
  renderEditor();
  await screen.findByLabelText('Label');
  expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  editSkill('updated');
  save();
  await screen.findByText(
    'Changes submitted, but the latest configuration could not be loaded. Refresh the channel details to view it.',
  );
  expect(postBody()).not.toHaveProperty('authorization_mode');
  expect(postBody()).not.toHaveProperty('service_ids');
  expect(postBody().runtime_config.nyxid_service_selectors).toEqual([]);
  expect(history.replace).toHaveBeenCalledWith(
    '/scopes/scope-alpha/channels/registration-alpha',
  );
  expect(screen.queryByText('Channel changes saved.')).not.toBeInTheDocument();
  expect(
    screen.queryByRole('button', { name: 'Check again' }),
  ).not.toBeInTheDocument();
  expect(posts()).toHaveLength(1);
  expect(fetchMock.mock.calls.filter(([input]) => input === path)).toHaveLength(
    3,
  );
});

it('keeps Save pending through readback and ignores its result after leaving the editor', async () => {
  let resolveRead!: (value: Response) => void;
  const pendingRead = new Promise<Response>((resolve) => {
    resolveRead = resolve;
  });
  fetchMock.mockImplementation(async (input, init) => {
    const identity = identityResponse(input);
    if (identity) return identity;
    if (init?.method === 'POST') return response(receipt, 202);
    return posts().length ? pendingRead : response(fixture);
  });
  const view = renderEditor();
  await screen.findByLabelText('Label');
  editSkill('updated');
  save();
  await waitFor(() =>
    expect(
      fetchMock.mock.calls.filter(([input]) => input === path),
    ).toHaveLength(3),
  );
  expect(screen.getByRole('button', { name: /Save changes/ })).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
  expect(
    screen.queryByRole('button', { name: 'Check again' }),
  ).not.toBeInTheDocument();
  fireEvent.submit(screen.getByRole('form', { name: 'Edit Telegram' }));
  expect(posts()).toHaveLength(1);
  view.unmount();
  await act(async () =>
    resolveRead(
      response({
        ...fixture,
        state_version: 13,
        default_skill: { name: 'updated', version: '2.1' },
      }),
    ),
  );
  expect(history.replace).not.toHaveBeenCalled();
  expect(screen.queryByText('Channel changes saved.')).not.toBeInTheDocument();
});

it('protects unsaved navigation and ignores a response after the editor unmounts', async () => {
  let resolvePost!: (value: Response) => void;
  const deferred = new Promise<Response>((resolve) => {
    resolvePost = resolve;
  });
  fetchMock.mockImplementation(async (input, init) =>
    init?.method === 'POST'
      ? deferred
      : (identityResponse(input) ??
        response({
          ...fixture,
          service_ids: [],
          runtime_config: {
            ...fixture.runtime_config,
          },
        })),
  );
  const view = renderEditor();
  await screen.findByLabelText('Label');
  editSkill('unsaved');
  const unload = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(unload);
  expect(unload.defaultPrevented).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  const dialog = await screen.findByRole('dialog', {
    name: 'Discard your changes?',
  });
  expect(history.push).not.toHaveBeenCalled();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Stay' }));
  expect(screen.getByLabelText(/^Skill name/)).toHaveValue('unsaved');
  save();
  await waitFor(() => expect(posts()).toHaveLength(1));
  view.unmount();
  await act(async () => resolvePost(response(receipt, 202)));
  expect(history.replace).not.toHaveBeenCalled();
  expect(fetchMock.mock.calls.filter(([input]) => input === path)).toHaveLength(
    2,
  );
});

it('updates only the label using the exact NyxID bot ID and refreshes the safe identity cache', async () => {
  fetchMock.mockImplementation(async (input, init) => {
    if (init?.method === 'PATCH')
      return response({
        ...bot,
        label: 'New label',
        access_token: 'TEST_ONLY_SECRET',
      });
    return identityResponse(input) ?? response(fixture);
  });
  const view = renderEditor();
  await screen.findByLabelText('Label');
  editLabel('  New label  ');
  save();
  await screen.findByText('Channel changes saved.');
  expect(patches()).toHaveLength(1);
  expect(patches()[0][0]).toBe(`${botsPath}/bot-nyx-alpha`);
  expect(JSON.parse(String(patches()[0][1]?.body))).toEqual({
    label: 'New label',
  });
  expect(posts()).toHaveLength(0);
  expect(fetchMock.mock.calls.filter(([input]) => input === path)).toHaveLength(
    1,
  );
  expect(
    view.queryClient.getQueryData(channelKeys.bots('scope-alpha')),
  ).toEqual([{ ...bot, label: 'New label' }]);
  expect(
    JSON.stringify(
      view.queryClient.getQueryData(channelKeys.bots('scope-alpha')),
    ),
  ).not.toContain('TEST_ONLY_SECRET');
  expect(history.replace).toHaveBeenCalledWith(
    '/scopes/scope-alpha/channels/registration-alpha',
  );
});

it('reports a partial save and retries only the failed skill update without changing authorization', async () => {
  let attempts = 0;
  fetchMock.mockImplementation(async (input, init) => {
    if (init?.method === 'PATCH')
      return response({ ...bot, label: 'New label' });
    if (init?.method === 'POST')
      return ++attempts === 1 ? response({}, 503) : response(receipt, 202);
    return identityResponse(input) ?? response(fixture);
  });
  renderEditor();
  await screen.findByLabelText('Label');
  editLabel('New label');
  editSkill('new-skill');
  save();
  await screen.findByText(
    'Label saved, but the skill name could not be updated. Try saving again.',
  );
  expect(history.replace).not.toHaveBeenCalled();
  expect(screen.getByLabelText('Label')).toHaveValue('New label');
  expect(screen.getByLabelText(/^Skill name/)).toHaveValue('new-skill');
  save();
  await screen.findByText(
    'Changes submitted. They may take a moment to appear in channel details.',
  );
  expect(patches()).toHaveLength(1);
  expect(posts()).toHaveLength(2);
  expect(postBody()).toEqual({
    runtime_config: {
      ...fixture.runtime_config,
      default_skill: { name: 'new-skill', version: '2.1' },
    },
  });
});

it('validates the label before either write and keeps both values when the label request fails', async () => {
  fetchMock.mockImplementation(async (input, init) => {
    if (init?.method === 'PATCH')
      return response({ token: 'TEST_ONLY_SECRET' }, 503);
    return identityResponse(input) ?? response(fixture);
  });
  renderEditor();
  await screen.findByLabelText('Label');
  editSkill('new-skill');
  for (const value of [' ', '界'.repeat(43)]) {
    editLabel(value);
    save();
    expect(screen.getByLabelText('Label')).toHaveAttribute(
      'aria-invalid',
      'true',
    );
    expect(patches()).toHaveLength(0);
    expect(posts()).toHaveLength(0);
  }
  editLabel('Valid label');
  save();
  await screen.findByText('Could not save the label. Check it and try again.');
  expect(screen.getByLabelText('Label')).toHaveValue('Valid label');
  expect(screen.getByLabelText(/^Skill name/)).toHaveValue('new-skill');
  expect(posts()).toHaveLength(0);
  expect(history.replace).not.toHaveBeenCalled();
  expect(document.body).not.toHaveTextContent('TEST_ONLY_SECRET');
});

it.each([
  { ...registration, owned: false },
  { ...registration, scope_id: 'other-scope' },
  { ...registration, nyx_channel_bot_id: 'unrelated-bot' },
  { ...registration, platform: 'lark' },
])('does not expose an editor without an exact owned bot mapping: %j', async (row) => {
  fetchMock.mockImplementation(async (input) =>
    input === '/api/channels/registrations'
      ? response([row])
      : (identityResponse(input) ?? response(fixture)),
  );
  renderEditor();
  await screen.findByText(
    'This channel is unavailable or you do not have access.',
  );
  expect(screen.queryByRole('form')).not.toBeInTheDocument();
  expect(patches()).toHaveLength(0);
  expect(posts()).toHaveLength(0);
});
