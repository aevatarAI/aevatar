import {
  act,
  cleanup,
  fireEvent,
  screen,
  waitFor,
} from '@testing-library/react';
import * as React from 'react';
import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { history } from '@/shared/navigation/history';
import { createNyxIDServiceSession } from '../../../../tests/fixtures/nyxidServiceSession';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextPage from '../index';
import ChannelConfigurationPage from './ChannelConfigurationPage';

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
  service_ids: ['user-service-github'],
  state_version: 12,
};
const unbound = {
  ...row,
  id: null,
  binding_status: 'unbound',
  skill_name: '',
  authorization_mode: null,
  service_ids: [],
};
const receipt = {
  status: 'accepted',
  registration_id: 'reg-alpha',
  command_id: 'cmd-one',
};
const serviceCatalogue = [
  {
    id: 'user-service-github',
    slug: 'api-github',
    label: 'GitHub',
    is_active: true,
    credential_source: { type: 'personal' },
  },
  {
    id: 'user-service-ornn',
    slug: 'ornn-api',
    label: 'Ornn',
    is_active: true,
    credential_source: { type: 'personal' },
  },
  {
    id: 'user-service-llm',
    slug: 'chrono-llm-public',
    label: 'Chrono LLM',
    is_active: true,
    credential_source: { type: 'personal' },
  },
];
const selectedServiceIds = [
  'user-service-github',
  'user-service-llm',
  'user-service-ornn',
];
function catalogue(input: RequestInfo | URL, services = serviceCatalogue) {
  if (String(input).includes('/skill-search'))
    return response({
      data: {
        items: [
          {
            guid: 'skill-guid',
            name: 'support',
            description: 'Help customers with support requests.',
            myAccessReason: 'shared-via-org',
          },
          { guid: 'skill-guid-new', name: 'new-support', description: '' },
        ],
        meta: { hasMore: false },
      },
      error: null,
    });
  if (String(input).endsWith('/user-services')) return response({ services });
  return null;
}
const posts = () =>
  fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST');
beforeEach(() => {
  fetchMock.mockReset();
  Object.values(mockToast).forEach((mock) => {
    mock.mockReset();
  });
  persistAuthSession(
    createNyxIDServiceSession({ allowed_service_ids: selectedServiceIds }),
  );
  jest.spyOn(history, 'push').mockImplementation(() => {});
  jest.spyOn(history, 'replace').mockImplementation(() => {});
});
async function chooseSkill(name: string) {
  fireEvent.mouseDown(await screen.findByRole('combobox'));
  fireEvent.click(
    await screen.findByText(name, {
      selector: '.channels__skill-option strong',
    }),
  );
}

it('binds with shared Ornn skills, retains refresh selection and opens the registration detail only after bound inventory', async () => {
  jest.useFakeTimers();
  try {
    let committed = false;
    fetchMock.mockImplementation(async (input, init) => {
      if (init?.method === 'POST') return response(receipt, 202);
      return (
        catalogue(input) ??
        response([
          committed ? { ...row, service_ids: selectedServiceIds } : unbound,
        ])
      );
    });
    window.history.replaceState(
      {},
      '',
      '/scopes/scope-alpha/workflow-activity-vnext/channels/bind/bot-alpha',
    );
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    fireEvent.mouseDown(await screen.findByRole('combobox'));
    expect(
      await screen.findByText('Help customers with support requests.'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox', { name: 'Search skills' }), {
      target: { value: 'support' },
    });
    await act(async () => {
      await jest.advanceTimersByTimeAsync(250);
    });
    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(([url]) => String(url).includes('q=support')),
      ).toBe(true),
    );
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Search skills' }), {
      key: 'Escape',
    });
    await chooseSkill('support');
    expect(
      screen.queryByLabelText(/Bot token|Channel name|Label/),
    ).not.toBeInTheDocument();
    fireEvent.click(await screen.findByRole('checkbox', { name: /GitHub/ }));
    expect(
      screen.queryByRole('button', { name: 'Refresh skills' }),
    ).not.toBeInTheDocument();
    fireEvent.mouseDown(screen.getByRole('combobox'));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh skills' }));
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Refresh skills' }),
      ).toBeEnabled(),
    );
    expect(
      screen.getByText('support', { selector: '.ant-select-content' }),
    ).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByRole('combobox')).toHaveAttribute(
        'aria-expanded',
        'true',
      );
      expect(
        screen.getByRole('textbox', { name: 'Search skills' }),
      ).toBeVisible();
    });
    const create = await screen.findByRole('link', {
      name: /Create new skill/,
    });
    expect(create).toHaveAttribute(
      'href',
      'https://ornn.chrono-ai.fun/skills/new/generate',
    );
    expect(create).toHaveAttribute('target', '_blank');
    fireEvent.mouseEnter(create);
    expect(await screen.findByRole('tooltip')).toHaveTextContent(
      'return here and refresh',
    );
    fireEvent.keyDown(screen.getByRole('combobox'), { key: 'Escape' });
    fireEvent.click(screen.getByRole('button', { name: 'Bind bot' }));
    await screen.findByText('Confirming your changes...');
    expect(JSON.parse(String(posts()[0][1]?.body))).toEqual({
      nyx_channel_bot_id: 'bot-alpha',
      skill_name: 'support',
      authorization_mode: 'explicit_service_allowlist',
      service_ids: selectedServiceIds,
    });
    expect(mockToast.success).not.toHaveBeenCalled();
    expect(history.replace).not.toHaveBeenCalled();
    expect(
      screen.queryByRole('button', { name: /Check again/ }),
    ).not.toBeInTheDocument();
    committed = true;
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1500);
    });
    await waitFor(() =>
      expect(mockToast.success).toHaveBeenCalledWith('Bot bound successfully.'),
    );
    expect(history.replace).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/channels/reg-alpha',
    );
    expect(history.replace).toHaveBeenCalledTimes(1);
    expect(posts()).toHaveLength(1);
  } finally {
    cleanup();
    jest.useRealTimers();
  }
});

it('preserves Label editing and requires a newer matching configuration before reporting saved', async () => {
  jest.useFakeTimers();
  try {
    let committed = false;
    fetchMock.mockImplementation(async (input, init) => {
      if (init?.method === 'PATCH')
        return response({
          id: 'bot-alpha',
          platform: 'discord',
          label: 'Renamed bot',
        });
      if (init?.method === 'POST') return response(receipt, 202);
      if (input === '/api/channels/registrations') return response([row]);
      return (
        catalogue(input) ??
        response({
          ...row,
          label: 'reg-alpha',
          skill_name: committed ? 'new-support' : 'support',
          service_ids: committed ? selectedServiceIds : row.service_ids,
          state_version: committed ? 13 : 12,
        })
      );
    });
    window.history.replaceState(
      {},
      '',
      '/scopes/scope-alpha/workflow-activity-vnext/channels/reg-alpha/edit',
    );
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    expect(await screen.findByLabelText('Label')).toHaveValue('Support bot');
    fireEvent.change(await screen.findByLabelText('Label'), {
      target: { value: 'Renamed bot' },
    });
    await chooseSkill('new-support');
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Confirming your changes...');
    expect(posts()[0][0]).toBe('/api/channels/registrations/reg-alpha');
    expect(JSON.parse(String(posts()[0][1]?.body))).toEqual({
      skill_name: 'new-support',
      authorization_mode: 'explicit_service_allowlist',
      service_ids: selectedServiceIds,
    });
    expect(mockToast.success).not.toHaveBeenCalled();
    committed = true;
    await act(async () => {
      await jest.advanceTimersByTimeAsync(1500);
    });
    await waitFor(() =>
      expect(mockToast.success).toHaveBeenCalledWith('Channel changes saved.'),
    );
    expect(history.replace).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/channels/reg-alpha',
    );
    expect(posts()).toHaveLength(1);
  } finally {
    cleanup();
    jest.useRealTimers();
  }
});

it('attributes an insecure callback rejection to the server and retains the binding choices', async () => {
  jest.useFakeTimers();
  try {
    fetchMock.mockImplementation(async (input, init) => {
      if (init?.method === 'POST')
        return response({ error: 'insecure_webhook_base_url' }, 400);
      return catalogue(input) ?? response([unbound]);
    });
    renderWithQueryClient(
      <ChannelConfigurationPage scopeId="scope-alpha" botId="bot-alpha" />,
    );
    await chooseSkill('support');
    fireEvent.click(await screen.findByRole('checkbox', { name: /GitHub/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Bind bot' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'server callback address is not configured correctly',
    );
    expect(mockToast.error).toHaveBeenCalledWith(
      expect.stringContaining('server callback address'),
    );
    expect(
      screen.queryByText(/Check your skill, services/),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: /GitHub/ })).toBeChecked();
    expect(
      screen.getByText('support', { selector: '.ant-select-content' }),
    ).toBeInTheDocument();
    expect(JSON.parse(String(posts()[0][1]?.body))).toEqual({
      nyx_channel_bot_id: 'bot-alpha',
      skill_name: 'support',
      authorization_mode: 'explicit_service_allowlist',
      service_ids: selectedServiceIds,
    });
    expect(posts()).toHaveLength(1);
    expect(mockToast.success).not.toHaveBeenCalled();
    expect(history.replace).not.toHaveBeenCalled();
  } finally {
    cleanup();
    jest.useRealTimers();
  }
});

it('does not repeat an adoption after an uncertain transport result', async () => {
  jest.useFakeTimers();
  try {
    fetchMock.mockImplementation(async (input, init) => {
      if (init?.method === 'POST') throw new Error('TEST_SECRET');
      return catalogue(input) ?? response([unbound]);
    });
    renderWithQueryClient(
      <ChannelConfigurationPage scopeId="scope-alpha" botId="bot-alpha" />,
    );
    const bind = await screen.findByRole('button', { name: 'Bind bot' });
    await waitFor(() => expect(bind).toBeEnabled());
    fireEvent.click(bind);
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Return to Channels',
    );
    expect(bind).toBeDisabled();
    fireEvent.click(bind);
    expect(posts()).toHaveLength(1);
    expect(document.body).not.toHaveTextContent('TEST_SECRET');
  } finally {
    cleanup();
    jest.useRealTimers();
  }
});

it('locks required services through individual, bulk and filtered deselection and submits their real IDs', async () => {
  jest.useFakeTimers();
  try {
    fetchMock.mockImplementation(async (input, init) => {
      if (init?.method === 'POST') return response(receipt, 202);
      return catalogue(input) ?? response([unbound]);
    });
    renderWithQueryClient(
      <ChannelConfigurationPage scopeId="scope-alpha" botId="bot-alpha" />,
    );
    const ornn = await screen.findByRole('checkbox', { name: /ornn-api/ });
    const llm = screen.getByRole('checkbox', { name: /chrono-llm-public/ });
    for (const required of [ornn, llm]) {
      expect(required).toBeChecked();
      expect(required).toBeDisabled();
      fireEvent.click(required);
      expect(required).toBeChecked();
    }
    expect(screen.getByText('2 selected')).toBeInTheDocument();
    const github = screen.getByRole('checkbox', { name: /GitHub/ });
    expect(github).toBeEnabled();
    expect(github).not.toBeChecked();
    const selectAll = screen.getByRole('checkbox', { name: 'Select all' });
    fireEvent.click(selectAll);
    expect(github).toBeChecked();
    expect(screen.getByText('3 selected')).toBeInTheDocument();
    fireEvent.click(selectAll);
    expect(github).not.toBeChecked();
    expect(ornn).toBeChecked();
    expect(llm).toBeChecked();
    const search = screen.getByRole('textbox', {
      name: 'Search services by name or slug',
    });
    fireEvent.change(search, { target: { value: 'ornn-api' } });
    expect(
      screen.getByRole('checkbox', { name: 'Select all results' }),
    ).toBeDisabled();
    fireEvent.change(search, { target: { value: 'GitHub' } });
    fireEvent.click(
      screen.getByRole('checkbox', { name: 'Select all results' }),
    );
    expect(screen.getByRole('checkbox', { name: /GitHub/ })).toBeChecked();
    fireEvent.click(
      screen.getByRole('checkbox', { name: 'Select all results' }),
    );
    fireEvent.change(search, { target: { value: '' } });
    expect(screen.getByText('2 selected')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Bind bot' }));
    await screen.findByText('Confirming your changes...');
    expect(JSON.parse(String(posts()[0][1]?.body))).toEqual({
      nyx_channel_bot_id: 'bot-alpha',
      skill_name: '',
      authorization_mode: 'explicit_service_allowlist',
      service_ids: ['user-service-llm', 'user-service-ornn'],
    });
  } finally {
    cleanup();
    jest.useRealTimers();
  }
});

it.each([
  ['missing', 'ornn-api'],
  ['inactive', 'chrono-llm-public'],
  ['unauthorized', 'ornn-api'],
  ['similar slug', 'chrono-llm-public'],
])('blocks submission with a %s required service and recovers on retry', async (condition, slug) => {
  jest.useFakeTimers();
  try {
    let restored = false;
    const unavailableId = serviceCatalogue.find(
      (service) => service.slug === slug,
    )?.id;
    if (condition === 'unauthorized')
      persistAuthSession(
        createNyxIDServiceSession({
          allowed_service_ids: selectedServiceIds.filter(
            (id) => id !== unavailableId,
          ),
        }),
      );
    const unavailableServices = serviceCatalogue
      .filter((service) => condition !== 'missing' || service.slug !== slug)
      .map((service) =>
        service.slug !== slug
          ? service
          : {
              ...service,
              is_active: condition !== 'inactive',
              slug: condition === 'similar slug' ? `${slug}-other` : slug,
              label: slug,
            },
      );
    fetchMock.mockImplementation(
      async (input) =>
        catalogue(input, restored ? serviceCatalogue : unavailableServices) ??
        response([unbound]),
    );
    renderWithQueryClient(
      <ChannelConfigurationPage scopeId="scope-alpha" botId="bot-alpha" />,
    );
    expect(await screen.findByRole('alert')).toHaveTextContent(
      `Required services unavailable: ${slug}.`,
    );
    expect(screen.getByRole('button', { name: 'Bind bot' })).toBeDisabled();
    fireEvent.submit(screen.getByRole('form', { name: 'Bind bot' }));
    expect(posts()).toHaveLength(0);
    restored = true;
    persistAuthSession(
      createNyxIDServiceSession({ allowed_service_ids: selectedServiceIds }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() =>
      expect(screen.queryByRole('alert')).not.toBeInTheDocument(),
    );
    expect(screen.getByRole('button', { name: 'Bind bot' })).toBeEnabled();
    expect(screen.getByRole('checkbox', { name: /ornn-api/ })).toBeChecked();
    expect(
      screen.getByRole('checkbox', { name: /chrono-llm-public/ }),
    ).toBeChecked();
  } finally {
    cleanup();
    jest.useRealTimers();
  }
});

it('explains replacing legacy NyxID defaults and saves the required explicit allowlist', async () => {
  jest.useFakeTimers();
  try {
    const defaults = {
      ...row,
      authorization_mode: 'nyxid_default',
      service_ids: [],
    };
    fetchMock.mockImplementation(async (input, init) => {
      if (init?.method === 'POST') return response(receipt, 202);
      if (input === '/api/channels/registrations') return response([defaults]);
      return catalogue(input) ?? response(defaults);
    });
    renderWithQueryClient(
      <ChannelConfigurationPage
        scopeId="scope-alpha"
        registrationId="reg-alpha"
      />,
    );
    await screen.findByRole('checkbox', { name: /ornn-api/ });
    expect(
      screen.getByText(
        'Saving replaces NyxID default access with the selected services.',
      ),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Confirming your changes...');
    expect(JSON.parse(String(posts()[0][1]?.body))).toEqual({
      skill_name: 'support',
      authorization_mode: 'explicit_service_allowlist',
      service_ids: ['user-service-llm', 'user-service-ornn'],
    });
  } finally {
    cleanup();
    jest.useRealTimers();
  }
});
