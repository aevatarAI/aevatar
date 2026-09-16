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
const service = {
  id: 'user-service-github',
  slug: 'api-github',
  label: 'GitHub work',
  is_active: true,
  credential_source: { type: 'personal' },
};
const receipt = {
  status: 'accepted',
  registration_id: 'registration-alpha',
  command_id: 'command-save',
};
const posts = () =>
  fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST');
const postBody = () => JSON.parse(String(posts().at(-1)?.[1]?.body));
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
      : input === servicePath
        ? response({ services: [service] })
        : response(fixture),
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
    if (input === servicePath) return response({ services: [service] });
    if (input === '/api/channels/registrations')
      return response([
        {
          id: 'registration-alpha',
          scope_id: 'scope-alpha',
          platform: 'telegram',
          owned: true,
        },
      ]);
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
  expect(
    await screen.findByRole('checkbox', { name: /GitHub work/ }),
  ).toBeChecked();
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
    authorization_mode: 'explicit_service_allowlist',
    service_ids: ['user-service-github'],
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
  fireEvent.click(await screen.findByRole('checkbox', { name: /GitHub work/ }));
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
    authorization_mode: 'explicit_service_allowlist',
    service_ids: [],
    runtime_config: {
      ...fixture.runtime_config,
      default_skill: { name: '', version: '' },
      nyxid_service_selectors: [],
    },
  });
});

it('keeps unavailable saved services visible until explicitly deselected and handles rejected fields without leaking diagnostics', async () => {
  let rejected = false;
  fetchMock.mockImplementation(async (input, init) => {
    if (input === servicePath) return response({ services: [] });
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
  expect(
    await screen.findByRole('checkbox', { name: /Unavailable service/ }),
  ).toBeChecked();
  editSkill('changed-helper');
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  fireEvent.click(
    screen.getByRole('checkbox', { name: /Unavailable service/ }),
  );
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
    if (input === servicePath) return response({ services: [] });
    if (init?.method === 'POST') {
      submitted = true;
      return response(receipt, 202);
    }
    if (submitted) return response({}, 503);
    return response(legacy);
  });
  renderEditor();
  expect(
    await screen.findByRole('checkbox', { name: 'Use NyxID defaults' }),
  ).toBeChecked();
  editSkill('updated');
  save();
  await screen.findByText(
    'Changes submitted, but the latest configuration could not be loaded. Refresh the channel details to view it.',
  );
  expect(postBody()).toMatchObject({
    authorization_mode: 'nyxid_default',
    service_ids: [],
  });
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
    if (input === servicePath) return response({ services: [service] });
    if (init?.method === 'POST') return response(receipt, 202);
    return posts().length ? pendingRead : response(fixture);
  });
  const view = renderEditor();
  await screen.findByRole('checkbox', { name: /GitHub work/ });
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
      : input === servicePath
        ? response({ services: [] })
        : response({
            ...fixture,
            service_ids: [],
            runtime_config: {
              ...fixture.runtime_config,
              nyxid_service_selectors: [],
            },
          }),
  );
  const view = renderEditor();
  await screen.findByText(
    'No services are available with your current authorization.',
  );
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
