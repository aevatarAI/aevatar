import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import type { ChannelRegistration } from '@/shared/api/channelsApi';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import { ChannelIdentity } from './presentation';

const registration = {
  id: null,
  botId: 'bot-alpha-1234567890-1234567890-complete',
  botOwnerScopeId: 'org-alpha-1234567890-complete',
  botOwnerScopeName: 'Support organization',
  label: 'Support',
  platform: 'discord',
  bindingStatus: 'unbound',
  availabilityStatus: 'available',
  nyxStatus: 'active',
  providerSlug: null,
  agentKeyId: null,
  skill: null,
  workflowDeliveryStatus: 'unbound',
  stateVersion: null,
  owned: false,
  serviceAuthorization: { kind: 'unavailable' },
} satisfies ChannelRegistration;
const clipboardDescriptor = Object.getOwnPropertyDescriptor(
  navigator,
  'clipboard',
);
const writeText = jest.fn();
beforeEach(() => {
  writeText.mockReset();
  Object.defineProperty(navigator, 'clipboard', {
    configurable: true,
    value: { writeText },
  });
});
afterEach(() => {
  if (clipboardDescriptor)
    Object.defineProperty(navigator, 'clipboard', clipboardDescriptor);
  else Reflect.deleteProperty(navigator, 'clipboard');
});

function renderIdentity(before?: React.ReactNode) {
  return renderWithQueryClient(
    <>
      {before}
      <ChannelIdentity
        registration={registration}
        label={registration.label}
        pending={false}
      />
    </>,
  );
}

it('previews IDs on hover without moving focus and stays open while the pointer enters the panel', async () => {
  jest.useFakeTimers();
  try {
    renderIdentity(<button type="button">Another action</button>);
    const otherAction = screen.getByRole('button', { name: 'Another action' });
    const trigger = screen.getByRole('button', {
      name: 'View IDs for Support',
    });
    act(() => otherAction.focus());
    fireEvent.mouseEnter(trigger);
    await act(async () => jest.advanceTimersByTime(300));
    const panel = screen.getByRole('dialog', { name: 'Bot identifiers' });
    expect(within(panel).getByText(registration.botId)).toBeVisible();
    expect(otherAction).toHaveFocus();

    fireEvent.mouseLeave(trigger);
    fireEvent.mouseEnter(panel);
    await act(async () => jest.advanceTimersByTime(300));
    expect(panel).toBeVisible();
    expect(trigger).toHaveAttribute('aria-expanded', 'true');

    writeText.mockResolvedValueOnce(undefined);
    fireEvent.click(within(panel).getByRole('button', { name: 'Copy Bot ID' }));
    await act(async () => jest.advanceTimersByTime(0));
    expect(writeText).toHaveBeenCalledWith(registration.botId);
    expect(screen.getByText('Bot ID copied.')).toBeInTheDocument();

    fireEvent.mouseLeave(panel);
    await act(async () => jest.advanceTimersByTime(300));
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    fireEvent.mouseEnter(trigger);
    await act(async () => jest.advanceTimersByTime(300));
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    fireEvent.keyDown(otherAction, { key: 'Escape' });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(otherAction).toHaveFocus();
  } finally {
    await act(async () => jest.runOnlyPendingTimers());
    jest.useRealTimers();
  }
});

it('reveals and copies exact bot and owner IDs on demand for an unbound non-owned bot', async () => {
  let finishCopy!: () => void;
  writeText.mockImplementationOnce(
    () =>
      new Promise<void>((resolve) => {
        finishCopy = resolve;
      }),
  );
  renderIdentity();
  expect(screen.queryByText(registration.botId)).not.toBeInTheDocument();
  expect(
    screen.queryByText(registration.botOwnerScopeId),
  ).not.toBeInTheDocument();
  const trigger = screen.getByRole('button', { name: 'View IDs for Support' });
  fireEvent.click(trigger);
  const panel = await screen.findByRole('dialog', { name: 'Bot identifiers' });
  expect(trigger).toHaveAttribute('aria-expanded', 'true');
  expect(within(panel).getByText(registration.botId)).toHaveAttribute(
    'translate',
    'no',
  );
  expect(
    within(panel).getByText(registration.botOwnerScopeId),
  ).toBeInTheDocument();
  const copyBot = within(panel).getByRole('button', { name: 'Copy Bot ID' });
  await waitFor(() => expect(copyBot).toHaveFocus());
  fireEvent.click(copyBot);
  expect(copyBot).toBeDisabled();
  expect(screen.queryByText('Bot ID copied.')).not.toBeInTheDocument();
  expect(writeText).toHaveBeenCalledWith(registration.botId);
  await act(async () => finishCopy());
  expect(await screen.findByText('Bot ID copied.')).toBeInTheDocument();
  writeText.mockResolvedValueOnce(undefined);
  fireEvent.click(within(panel).getByRole('button', { name: 'Copy Owner ID' }));
  expect(await screen.findByText('Owner ID copied.')).toBeInTheDocument();
  expect(writeText).toHaveBeenLastCalledWith(registration.botOwnerScopeId);
  fireEvent.keyDown(panel, { key: 'Escape' });
  await waitFor(() =>
    expect(trigger).toHaveAttribute('aria-expanded', 'false'),
  );
  expect(trigger).toHaveFocus();
});

it('preserves selectable IDs and reports a failed clipboard write without success feedback', async () => {
  writeText.mockRejectedValue(new Error('Clipboard denied'));
  renderIdentity();
  fireEvent.click(screen.getByRole('button', { name: 'View IDs for Support' }));
  const panel = await screen.findByRole('dialog', { name: 'Bot identifiers' });
  const copyBot = within(panel).getByRole('button', { name: 'Copy Bot ID' });
  fireEvent.click(copyBot);
  expect(
    await screen.findByText(
      'Could not copy. Select the ID and copy it manually.',
    ),
  ).toBeInTheDocument();
  expect(screen.queryByText('Bot ID copied.')).not.toBeInTheDocument();
  expect(within(panel).getByText(registration.botId)).toBeInTheDocument();
  expect(copyBot).toBeEnabled();
});
