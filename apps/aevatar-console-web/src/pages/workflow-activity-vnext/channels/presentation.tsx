import {
  ApiOutlined,
  LinkOutlined,
  MessageOutlined,
  SendOutlined,
  WhatsAppOutlined,
} from '@ant-design/icons';
import { Button } from 'antd';
import * as React from 'react';
import {
  ChannelApiError,
  type ChannelRegistration,
} from '@/shared/api/channelsApi';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { getOrnnRuntimeConfig } from '@/shared/studio/ornnConfig';

export function ChannelLink({
  href,
  children,
  className,
  ...rest
}: React.AnchorHTMLAttributes<HTMLAnchorElement> & { href: string }) {
  return (
    <a
      {...rest}
      className={className}
      href={href}
      onClick={(event) => {
        if (
          event.button ||
          event.metaKey ||
          event.ctrlKey ||
          event.shiftKey ||
          event.altKey
        )
          return;
        event.preventDefault();
        history.push(href);
      }}
    >
      {children}
    </a>
  );
}

export function platformName(platform: string): string {
  switch (platform.toLowerCase()) {
    case 'lark':
      return 'Lark';
    case 'feishu':
      return t('channels.platform.feishu', 'Feishu');
    case 'telegram':
      return 'Telegram';
    case 'whatsapp':
      return 'WhatsApp';
    case 'discord':
      return 'Discord';
    case 'slack':
      return 'Slack';
    default:
      return platform;
  }
}

export function ChannelIcon({ platform }: { readonly platform: string }) {
  return (
    <span
      aria-hidden="true"
      className={`channels__icon channels__icon--${platform === 'telegram' ? 'telegram' : 'chat'}`}
    >
      {platform === 'telegram' ? (
        <SendOutlined />
      ) : platform === 'whatsapp' ? (
        <WhatsAppOutlined />
      ) : ['lark', 'feishu', 'discord', 'slack'].includes(platform) ? (
        <MessageOutlined />
      ) : (
        <ApiOutlined />
      )}
    </span>
  );
}

export function compactChannelIdentifier(value: string): string {
  return value.length > 24 ? `${value.slice(0, 8)}…${value.slice(-6)}` : value;
}

export function ChannelIdentity({
  registration,
  label,
  pending,
}: {
  readonly registration: ChannelRegistration;
  readonly label: string | null;
  readonly pending: boolean;
}) {
  const identifier = registration.botId ?? registration.id;
  return (
    <div className="channels__identity">
      <ChannelIcon platform={registration.platform} />
      <div className="channels__identity-copy">
        <strong>
          {label ??
            (pending
              ? t('channels.name.loading', 'Loading name…')
              : t('channels.name.unavailable', 'Name unavailable'))}
        </strong>
        <span className="channels__identifier" title={identifier}>
          {compactChannelIdentifier(identifier)}
        </span>
      </div>
    </div>
  );
}

export function ChannelSkill({
  skill,
}: {
  readonly skill: ChannelRegistration['skill'];
}) {
  if (!skill)
    return (
      <span className="channels__muted">
        {t('channels.skill.notSet', 'Not set')}
      </span>
    );
  const config = getOrnnRuntimeConfig();
  // Ornn's published web route accepts an exact ID or name. The channel API
  // supplies the name; do not derive a GUID or substitute a sample skill.
  const href = config.configurationError
    ? null
    : `${config.baseUrl}/skills/${encodeURIComponent(skill.name)}`;
  return (
    <div className="channels__skill">
      {href ? (
        <a
          href={href}
          target="_blank"
          rel="noopener noreferrer"
          aria-label={t('channels.skill.open', 'Open {name} in Ornn', {
            name: skill.name,
          })}
        >
          {skill.name} <LinkOutlined aria-hidden="true" />
        </a>
      ) : (
        <span>{skill.name}</span>
      )}
      {skill.version ? (
        <span className="channels__muted">
          {t('channels.skill.version', 'Version {version}', {
            version: skill.version,
          })}
        </span>
      ) : null}
    </div>
  );
}

type Tone = 'success' | 'warning' | 'danger' | 'neutral';
export function ChannelBadge({
  children,
  tone = 'neutral',
}: {
  readonly children: React.ReactNode;
  readonly tone?: Tone;
}) {
  return (
    <span className={`channels__badge channels__badge--${tone}`}>
      <span aria-hidden="true" className="channels__dot" />
      {children}
    </span>
  );
}

export function InboundStatus({
  value,
  pending = false,
}: {
  readonly value?: string;
  readonly pending?: boolean;
}) {
  if (pending)
    return (
      <ChannelBadge>{t('channels.status.checking', 'Checking…')}</ChannelBadge>
    );
  switch (value?.toLowerCase()) {
    case 'active':
      return (
        <ChannelBadge tone="success">
          {t('channels.status.active', 'Active')}
        </ChannelBadge>
      );
    case 'pending':
    case 'pending_webhook':
      return (
        <ChannelBadge tone="warning">
          {t('channels.status.pending', 'Waiting for message')}
        </ChannelBadge>
      );
    case 'error':
    case 'failed':
      return (
        <ChannelBadge tone="danger">
          {t('channels.status.error', 'Needs attention')}
        </ChannelBadge>
      );
    default:
      return (
        <ChannelBadge>{t('channels.status.unknown', 'Unknown')}</ChannelBadge>
      );
  }
}

export function DeliveryStatus({
  value,
  platform,
}: {
  readonly value: string | null;
  readonly platform: string;
}) {
  switch (value) {
    case 'enabled':
      return (
        <ChannelBadge tone="success">
          {t('channels.delivery.ready', 'Ready')}
        </ChannelBadge>
      );
    case 'repair_failed':
      return (
        <ChannelBadge tone="danger">
          {t('channels.delivery.failed', 'Repair failed')}
        </ChannelBadge>
      );
    case 'repairing':
      return (
        <ChannelBadge tone="warning">
          {t('channels.delivery.repairing', 'Repairing')}
        </ChannelBadge>
      );
    case 'repair_required':
      return platform === 'lark' ? (
        <ChannelBadge tone="warning">
          {t('channels.delivery.repairRequired', 'Needs repair')}
        </ChannelBadge>
      ) : (
        <ChannelBadge>
          {t('channels.delivery.notEnabled', 'Not enabled')}
        </ChannelBadge>
      );
    case 'missing':
    case 'not_enabled':
      return (
        <ChannelBadge>
          {t('channels.delivery.notEnabled', 'Not enabled')}
        </ChannelBadge>
      );
    default:
      return (
        <ChannelBadge>{t('channels.status.unknown', 'Unknown')}</ChannelBadge>
      );
  }
}

export function ChannelLoadError({
  error,
  retry,
  pending,
}: {
  readonly error: unknown;
  readonly retry: () => void;
  readonly pending: boolean;
}) {
  const unavailable =
    error instanceof ChannelApiError && [403, 404].includes(error.status);
  return (
    <div className="channels__state" role="alert">
      <p>
        {unavailable
          ? t(
              'channels.error.unavailable',
              'This channel is unavailable or you do not have access.',
            )
          : t(
              'channels.error.load',
              'Could not load channels. Please try again.',
            )}
      </p>
      <Button loading={pending} onClick={retry}>
        {t('channels.retry', 'Try again')}
      </Button>
    </div>
  );
}
