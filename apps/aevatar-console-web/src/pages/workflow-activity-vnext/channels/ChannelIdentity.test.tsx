import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import type { ChannelRegistration } from '@/shared/api/channelsApi';
import { ChannelIdentity } from './presentation';

describe('Channel identifier disclosure', () => {
  it.each([
    'bot',
    'registration',
  ] as const)('reveals the complete %s ID from the compact identifier', async (identityKind) => {
    const fullId =
      identityKind === 'bot'
        ? 'bot-alpha-1234567890-1234567890-complete'
        : 'registration-alpha-1234567890-1234567890-complete';
    const registration: ChannelRegistration = {
      id: identityKind === 'registration' ? fullId : 'registration-alpha',
      botId: identityKind === 'bot' ? fullId : null,
      platform: 'telegram',
      scopeId: 'scope-alpha',
      providerSlug: null,
      agentKeyId: null,
      skill: null,
      workflowDeliveryStatus: null,
      owned: true,
    };
    const identityProps = {
      registration,
      label: 'Channel for ID test',
      pending: false,
    };
    render(<ChannelIdentity {...identityProps} />);
    const identifier = screen.getByRole('button', { name: 'Show full ID' });
    expect(identifier).not.toHaveTextContent(fullId);
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();

    if (identityKind === 'bot') fireEvent.mouseEnter(identifier);
    else fireEvent.focus(identifier);

    expect(await screen.findByRole('tooltip')).toHaveTextContent(fullId);
  });
});
