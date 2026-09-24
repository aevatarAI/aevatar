import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import type { ChannelRegistration } from '@/shared/api/channelsApi';
import { ChannelIdentity } from './presentation';

it('reveals the complete NyxID bot identity even before a local registration exists', async () => {
  const fullId = 'bot-alpha-1234567890-1234567890-complete';
  const registration: ChannelRegistration = {
    id: null,
    botId: fullId,
    botOwnerScopeId: null,
    botOwnerScopeName: null,
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
    owned: true,
    serviceAuthorization: { kind: 'unavailable' },
  };
  render(
    <ChannelIdentity
      registration={registration}
      label={registration.label}
      pending={false}
    />,
  );
  const identifier = screen.getByRole('button', { name: 'Show full ID' });
  expect(identifier).not.toHaveTextContent(fullId);
  fireEvent.focus(identifier);
  expect(await screen.findByRole('tooltip')).toHaveTextContent(fullId);
});
