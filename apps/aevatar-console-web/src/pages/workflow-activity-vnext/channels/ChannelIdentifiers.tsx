import {
  CloseOutlined,
  CopyOutlined,
  InfoCircleOutlined,
} from '@ant-design/icons';
import { Popover } from 'antd';
import * as React from 'react';
import type { ChannelRegistration } from '@/shared/api/channelsApi';
import { t } from '@/shared/i18n/messages';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';

export default function ChannelIdentifiers({
  registration,
  name,
}: {
  readonly registration: ChannelRegistration;
  readonly name: string;
}) {
  const [open, setOpen] = React.useState(false);
  const [copying, setCopying] = React.useState<string | null>(null);
  const copyInFlight = React.useRef(false);
  const trigger = React.useRef<HTMLButtonElement>(null);
  const firstCopy = React.useRef<HTMLButtonElement>(null);
  const panelId = React.useId();
  const toast = useConsoleToast();
  const fields = [
    { label: t('channels.botId', 'Bot ID'), value: registration.botId },
    ...(registration.botOwnerScopeId
      ? [
          {
            label: t('channels.owner.id', 'Owner ID'),
            value: registration.botOwnerScopeId,
          },
        ]
      : []),
  ];

  function close() {
    setOpen(false);
    trigger.current?.focus();
  }

  async function copy(label: string, value: string) {
    if (copyInFlight.current) return;
    copyInFlight.current = true;
    setCopying(label);
    try {
      if (!navigator.clipboard?.writeText)
        throw new Error('Clipboard unavailable');
      await navigator.clipboard.writeText(value);
      toast.success(
        t('channels.identifier.copied', '{label} copied.', { label }),
      );
    } catch {
      toast.error(
        t(
          'channels.identifier.copyFailed',
          'Could not copy. Select the ID and copy it manually.',
        ),
      );
    } finally {
      copyInFlight.current = false;
      setCopying(null);
    }
  }

  return (
    <Popover
      trigger="click"
      placement="bottom"
      open={open}
      onOpenChange={setOpen}
      afterOpenChange={(visible) => {
        if (visible) queueMicrotask(() => firstCopy.current?.focus());
      }}
      destroyOnHidden
      classNames={{ root: 'channels__identifiers-popup' }}
      content={
        <div
          id={panelId}
          role="dialog"
          aria-label={t('channels.identifier.title', 'Bot identifiers')}
          onKeyDown={(event) => {
            if (event.key === 'Escape') {
              event.stopPropagation();
              close();
            }
          }}
        >
          <div className="channels__identifiers-heading">
            <strong>{t('channels.identifier.title', 'Bot identifiers')}</strong>
            <button
              type="button"
              className="channels__identifier-action"
              aria-label={t('channels.identifier.close', 'Close identifiers')}
              onClick={close}
            >
              <CloseOutlined aria-hidden="true" />
            </button>
          </div>
          <dl className="channels__identifiers-fields">
            {fields.map(({ label, value }, index) => (
              <div key={label}>
                <dt>{label}</dt>
                <dd>
                  <code translate="no">{value}</code>
                  <AevatarTooltip
                    title={t('channels.identifier.copy', 'Copy {label}', {
                      label,
                    })}
                  >
                    <button
                      ref={index === 0 ? firstCopy : undefined}
                      type="button"
                      className="channels__identifier-action"
                      aria-label={t(
                        'channels.identifier.copy',
                        'Copy {label}',
                        { label },
                      )}
                      aria-busy={copying === label}
                      disabled={copying !== null}
                      onClick={() => void copy(label, value)}
                    >
                      <CopyOutlined aria-hidden="true" />
                    </button>
                  </AevatarTooltip>
                </dd>
              </div>
            ))}
          </dl>
        </div>
      }
    >
      <button
        ref={trigger}
        type="button"
        className="channels__identifier-action"
        aria-label={t('channels.identifier.show', 'View IDs for {name}', {
          name,
        })}
        aria-expanded={open}
        aria-haspopup="dialog"
        aria-controls={open ? panelId : undefined}
        title={t('channels.identifier.title', 'Bot identifiers')}
        onKeyDown={(event) => {
          if (event.key === 'Escape') close();
        }}
      >
        <InfoCircleOutlined aria-hidden="true" />
      </button>
    </Popover>
  );
}
