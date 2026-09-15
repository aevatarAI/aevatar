import { focusManager, onlineManager } from '@tanstack/react-query';
import { act, fireEvent, screen } from '@testing-library/react';
import * as React from 'react';
import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { createNyxIDServiceSession } from '../../../../tests/fixtures/nyxidServiceSession';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import ChannelAuthorizedServices from './ChannelAuthorizedServices';
import ChannelDetailsPage from './ChannelDetailsPage';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({
    baseUrl: 'https://nyx.example.test',
    enabled: true,
  }),
}));
const fetchMock = jest.mocked(authFetch);
const listPath = '/api/channels/registrations';
const statusPath = `${listPath}/reg-selected/status`;
const inventoryPath = 'https://nyx.example.test/api/v1/user-services';
const registration = {
  id: 'reg-selected',
  platform: 'telegram',
  scope_id: 'scope-alpha',
  owned: true,
  authorization_mode: 'explicit_service_allowlist',
  service_ids: ['us-work', 'us-model', 'us-deleted'],
  default_skill_name: 'review-skill',
};
const service = {
  id: 'us-work',
  slug: 'api-github',
  label: 'GitHub work',
  is_active: false,
  credential_source: { type: 'personal' },
  default_request_headers: [{ value: 'TEST_ONLY_SECRET' }],
};
const inventory = {
  services: [
    service,
    {
      ...service,
      id: 'us-model',
      label: 'Chrono Public',
      slug: 'chrono-llm-public',
      is_active: true,
    },
    {
      ...service,
      id: 'us-not-selected',
      label: 'Not authorized for this channel',
      is_active: true,
    },
  ],
};
const response = (value: unknown, status = 200) =>
  ({ ok: status === 200, status, json: async () => value }) as Response;

beforeEach(() => {
  fetchMock.mockReset();
  persistAuthSession(
    createNyxIDServiceSession({ allowed_service_ids: ['us-not-selected'] }),
  );
  fetchMock.mockImplementation(async (input) => {
    if (input === '/api/auth/me') return response({ authenticated: false });
    if (input === listPath) return response([registration]);
    if (input === statusPath)
      return response({ registration_id: 'reg-selected', status: 'active' });
    if (input === inventoryPath) return response(inventory);
    throw new Error('Unexpected test request');
  });
});

it('shows saved authorizations by exact ID, including services outside current grants, without automatic refresh', async () => {
  jest.useFakeTimers();
  try {
    const view = renderWithQueryClient(
      <ChannelDetailsPage
        scopeId="scope-alpha"
        registrationId="reg-selected"
      />,
    );
    expect(await screen.findByText('Authorized services')).toBeInTheDocument();
    expect(await screen.findByText('GitHub work')).toBeInTheDocument();
    expect(screen.getByText('Chrono Public')).toBeInTheDocument();
    expect(screen.getByText('chrono-llm-public')).toBeInTheDocument();
    expect(screen.getByText('us-deleted')).toBeInTheDocument();
    expect(
      screen.queryByText('Not authorized for this channel'),
    ).not.toBeInTheDocument();
    expect(
      JSON.stringify(
        view.queryClient
          .getQueryCache()
          .getAll()
          .map((query) => query.state.data),
      ),
    ).not.toContain('TEST_ONLY_SECRET');
    // The shared account header has its own reconnect behavior. Check only
    // channel-owned requests, keeping registrations, status and names covered.
    const channelReads = () =>
      fetchMock.mock.calls
        .map(([input]) => input)
        .filter((input) => input !== '/api/auth/me')
        .sort();
    const expectedReads = [listPath, statusPath, inventoryPath].sort();
    expect(channelReads()).toEqual(expectedReads);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(60_000);
      focusManager.setFocused(false);
      focusManager.setFocused(true);
      onlineManager.setOnline(false);
      onlineManager.setOnline(true);
    });
    expect(channelReads()).toEqual(expectedReads);
    view.unmount();
  } finally {
    focusManager.setFocused(undefined);
    onlineManager.setOnline(true);
    jest.useRealTimers();
  }
});

it('keeps saved IDs and other details on name lookup failure, then retries only names', async () => {
  let inventoryReads = 0;
  fetchMock.mockImplementation(async (input) => {
    if (input === '/api/auth/me') return response({ authenticated: false });
    if (input === listPath) return response([registration]);
    if (input === statusPath)
      return response({ registration_id: 'reg-selected', status: 'active' });
    if (input === inventoryPath)
      return ++inventoryReads === 1
        ? response({ error: 'TEST_ONLY_SECRET' }, 503)
        : response(inventory);
    throw new Error('Unexpected test request');
  });
  renderWithQueryClient(
    <ChannelDetailsPage scopeId="scope-alpha" registrationId="reg-selected" />,
  );
  expect(
    await screen.findByText('Could not load service names. Please try again.'),
  ).toBeInTheDocument();
  expect(screen.getByText('us-work')).toBeInTheDocument();
  expect(screen.getByText('review-skill')).toBeInTheDocument();
  expect(document.body).not.toHaveTextContent('TEST_ONLY_SECRET');
  fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
  expect(await screen.findByText('GitHub work')).toBeInTheDocument();
  expect(inventoryReads).toBe(2);
  expect(
    fetchMock.mock.calls.filter(([input]) => input === listPath),
  ).toHaveLength(1);
  expect(
    fetchMock.mock.calls.filter(([input]) => input === statusPath),
  ).toHaveLength(1);
});

it('keeps empty, default and unavailable authorization distinct without querying current service choices', () => {
  renderWithQueryClient(
    <>
      <ChannelAuthorizedServices
        scopeId="scope-alpha"
        authorization={{ kind: 'explicit', serviceIds: [] }}
      />
      <ChannelAuthorizedServices
        scopeId="scope-alpha"
        authorization={{ kind: 'nyxidDefault' }}
      />
      <ChannelAuthorizedServices
        scopeId="scope-alpha"
        authorization={{ kind: 'unavailable' }}
      />
    </>,
  );
  expect(screen.getByText('No services authorized.')).toBeInTheDocument();
  expect(
    screen.getByText(
      'Uses NyxID default authorization; individual services are not listed.',
    ),
  ).toBeInTheDocument();
  expect(
    screen.getByText('Authorization details are unavailable for this channel.'),
  ).toBeInTheDocument();
  expect(fetchMock).not.toHaveBeenCalled();
});
