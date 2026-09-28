import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { createNyxIDServiceSession } from '../../../../tests/fixtures/nyxidServiceSession';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import ChannelConfigurationPage from './ChannelConfigurationPage';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn().mockResolvedValue({ authenticated: false }),
  },
}));

const fetchMock = jest.mocked(authFetch);
const response = (value: unknown, status = 200) =>
  ({ ok: status === 200, status, json: async () => value }) as Response;
const personal = (id: string, slug: string, label = slug) => ({
  id,
  slug,
  label,
  is_active: true,
  credential_source: { type: 'personal' },
});
const inventory = [
  personal('us-ornn', 'ornn-api', 'Ornn'),
  personal('us-llm', 'chrono-llm-public', 'LLM'),
  personal('us-manual', 'manual-only', 'Manual service'),
  personal('us-personal', 'api-github', 'Personal GitHub'),
  {
    ...personal('us-org', 'api-github', 'Team GitHub'),
    credential_source: { type: 'org', org_name: 'Acme', allowed: true },
  },
  personal('us-linear', 'linear', 'Linear'),
  { ...personal('us-inactive', 'inactive-service'), is_active: false },
  {
    ...personal('us-owner', 'owner-service'),
    credential_source: { type: 'org', org_name: 'Other team', allowed: false },
  },
];
const allowedIds = ['us-ornn', 'us-llm', 'us-manual', 'us-personal', 'us-org'];
const registration = {
  id: 'reg-alpha',
  nyx_channel_bot_id: 'bot-alpha',
  platform: 'lark',
  label: 'Support bot',
  owned: true,
  binding_status: 'bound',
  availability_status: 'available',
  nyx_status: 'active',
  skill_name: 'support',
  authorization_mode: 'explicit_service_allowlist',
  service_ids: ['us-manual', 'us-ornn', 'us-llm'],
  state_version: 12,
};
const catalog = [
  { slug: 'api-github', name: 'GitHub' },
  { slug: 'slack', name: 'Slack' },
  { slug: 'linear', name: 'Linear' },
  { slug: 'api-drive', name: 'Drive', recommended_skills: ['support'] },
  { slug: 'inactive-service', name: 'Inactive service' },
  { slug: 'owner-service', name: 'Owner service' },
  { slug: 'mail', name: 'Mail' },
];

async function serve(input: RequestInfo | URL, init?: RequestInit) {
  const url = String(input);
  if (init?.method === 'POST')
    return response({ error: 'insecure_webhook_base_url' }, 400);
  if (url.endsWith('/user-services')) return response({ services: inventory });
  if (url.endsWith('/catalog?include_all=true'))
    return response({ entries: catalog });
  if (url.includes('/skill-search'))
    return response({
      data: {
        items: ['support', 'slow-skill', 'no-services'].map((name) => ({
          guid: `guid-${name}`,
          name,
        })),
        meta: { hasMore: false },
      },
    });
  const name = url.match(/\/skills\/([^/]+)$/)?.[1];
  if (name)
    return response({
      data: {
        guid: `guid-${name}`,
        name,
        description: 'Task guide',
        nyxidServiceSlug: name === 'support' ? 'api-github' : null,
        nyxidServiceId: 'catalog-id-is-not-authorization',
      },
    });
  const packageName = url.match(/\/skills\/guid-([^/]+)\/json$/)?.[1];
  if (packageName)
    return response({
      data: {
        name: packageName,
        files: {
          'SKILL.md':
            packageName === 'no-services'
              ? 'No integrations.'
              : 'Use Slack, Linear, inactive-service and owner-service. gmail and mail-helper are unrelated. TEST_ONLY_PRIVATE',
        },
      },
    });
  if (url === '/api/channels/registrations?scope=all')
    return response([registration]);
  if (url === '/api/channels/registrations/reg-alpha')
    return response(registration);
  throw new Error(`Unexpected request: ${url}`);
}

beforeEach(() => {
  fetchMock.mockReset().mockImplementation(serve);
  persistAuthSession(
    createNyxIDServiceSession({ allowed_service_ids: allowedIds }),
  );
});

function renderForm() {
  return renderWithQueryClient(
    <ChannelConfigurationPage
      scopeId="scope-alpha"
      registrationId="reg-alpha"
    />,
  );
}
function suggestions(skill = 'support') {
  return screen.getByRole('region', { name: `Suggested for ${skill}` });
}
async function chooseSkill(name: string) {
  fireEvent.mouseDown(await screen.findByRole('combobox'));
  fireEvent.click(
    await screen.findByText(name, {
      selector: '.channels__skill-option strong',
    }),
  );
}
const writes = () =>
  fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST');

it('discovers evidence and access gaps without granting access, then saves only the explicitly selected UserService instance', async () => {
  renderForm();
  await screen.findByRole('button', { name: 'Select Team GitHub' });
  const related = within(suggestions());
  expect(
    related.getByText('Linked to this skill in Ornn.'),
  ).toBeInTheDocument();
  expect(
    related.getByText('The service catalog recommends this skill.'),
  ).toBeInTheDocument();
  expect(related.getByText('Slack')).toBeInTheDocument();
  expect(related.getAllByText(/may be needed for some tasks/)).not.toHaveLength(
    0,
  );
  expect(related.queryByText('Mail')).not.toBeInTheDocument();
  expect(
    related.getByText(/Not authorized for this session/),
  ).toBeInTheDocument();
  expect(related.getByText(/Inactive —/)).toBeInTheDocument();
  expect(related.getByText(/Access unavailable —/)).toBeInTheDocument();
  expect(related.getAllByText(/No connection found/)).toHaveLength(2);
  expect(
    related.queryByRole('button', { name: 'Select Linear' }),
  ).not.toBeInTheDocument();
  expect(
    related.getByRole('link', { name: /Manage connections/ }),
  ).toHaveAttribute('href', 'https://nyx.chrono-ai.fun/services');
  expect(
    related.getByRole('link', { name: /Review service access/ }),
  ).toHaveAttribute('href', '/scopes/scope-alpha/settings?section=account');
  expect(document.body).not.toHaveTextContent('TEST_ONLY_PRIVATE');
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  expect(writes()).toHaveLength(0);
  fireEvent.click(related.getByRole('button', { name: 'Select Team GitHub' }));
  expect(related.getByText('Selected')).toBeInTheDocument();
  expect(
    screen.getByRole('checkbox', { name: /Personal GitHub/ }),
  ).not.toBeChecked();
  expect(screen.getByRole('checkbox', { name: /Team GitHub/ })).toBeChecked();
  fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  await screen.findByRole('alert');
  expect(writes()).toHaveLength(1);
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({
    skill_name: 'support',
    authorization_mode: 'explicit_service_allowlist',
    service_ids: ['us-llm', 'us-manual', 'us-org', 'us-ornn'],
  });
});

it('updates suggestions on skill changes and ignores late results while retaining manual choices after clearing', async () => {
  let complete!: (value: Response) => void;
  const pending = new Promise<Response>((resolve) => {
    complete = resolve;
  });
  let pendingSignal: AbortSignal | null | undefined;
  fetchMock.mockImplementation((input, init) => {
    if (String(input).endsWith('/skills/guid-slow-skill/json')) {
      pendingSignal = init?.signal;
      return pending;
    }
    return serve(input, init);
  });
  renderForm();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Select Personal GitHub' }),
  );
  await chooseSkill('slow-skill');
  await waitFor(() => expect(pendingSignal).toBeTruthy());
  expect(
    within(suggestions('slow-skill')).getByText('Finding related services...'),
  ).toBeInTheDocument();
  expect(
    within(suggestions('slow-skill')).queryByText('GitHub'),
  ).not.toBeInTheDocument();
  await chooseSkill('no-services');
  await screen.findByText(/No related services identified/);
  expect(pendingSignal?.aborted).toBe(true);
  await act(async () =>
    complete(
      response({
        data: { name: 'slow-skill', files: { 'SKILL.md': 'Slack' } },
      }),
    ),
  );
  expect(
    within(suggestions('no-services')).queryByText('Slack'),
  ).not.toBeInTheDocument();
  fireEvent.mouseDown(screen.getByRole('img', { name: 'close-circle' }));
  expect(
    screen.queryByRole('region', { name: /Suggested for/ }),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole('checkbox', { name: /Personal GitHub/ }),
  ).toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: /Manual service/ }),
  ).toBeChecked();
  expect(writes()).toHaveLength(0);
});

it('keeps manual selection usable on discovery failure and refreshes connections and authorization on retry', async () => {
  let recovered = false;
  fetchMock.mockImplementation((input, init) => {
    if (!recovered && String(input).includes('/catalog?'))
      return Promise.resolve(
        response({ message: 'PRIVATE_SERVER_DETAIL' }, 503),
      );
    if (recovered && String(input).endsWith('/user-services'))
      return Promise.resolve(
        response({
          services: [
            ...inventory,
            personal('us-slack', 'slack', 'Connected Slack'),
          ],
        }),
      );
    return serve(input, init);
  });
  renderForm();
  await screen.findByText(/Could not identify related services/);
  expect(document.body).not.toHaveTextContent('PRIVATE_SERVER_DETAIL');
  fireEvent.click(screen.getByRole('checkbox', { name: /Personal GitHub/ }));
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled();
  recovered = true;
  persistAuthSession(
    createNyxIDServiceSession({
      allowed_service_ids: [...allowedIds, 'us-slack', 'us-linear'],
    }),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Refresh suggestions' }));
  await screen.findByRole('button', { name: 'Select Connected Slack' });
  expect(screen.getByRole('button', { name: 'Select Linear' })).toBeEnabled();
  expect(
    screen.queryByText(/Could not identify related services/),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole('checkbox', { name: /Personal GitHub/ }),
  ).toBeChecked();
  expect(
    screen.getByRole('checkbox', { name: /Connected Slack/ }),
  ).not.toBeChecked();
  expect(writes()).toHaveLength(0);
});
