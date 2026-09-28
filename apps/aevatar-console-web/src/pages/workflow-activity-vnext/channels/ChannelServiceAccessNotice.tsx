import { CheckCircleOutlined, LockOutlined } from '@ant-design/icons';
import * as React from 'react';
import type { ChannelServiceChoice } from '@/shared/api/channelServicesApi';
import { t } from '@/shared/i18n/messages';

export default function ChannelServiceAccessNotice({
  requestedIds,
  services,
  restored,
}: {
  readonly requestedIds: readonly string[];
  readonly services: readonly ChannelServiceChoice[];
  readonly restored: boolean;
}) {
  const missing = requestedIds.filter(
    (id) =>
      !services.some(
        (service) => service.id === id && service.active && service.allowed,
      ),
  );
  if (!requestedIds.length && !restored) return null;
  return (
    <div
      className={`channels__access-notice${missing.length ? ' channels__access-notice--needed' : ''}`}
      role="status"
    >
      <div className="channels__access-notice-heading">
        {missing.length ? (
          <LockOutlined aria-hidden="true" />
        ) : (
          <CheckCircleOutlined aria-hidden="true" />
        )}
        <strong>
          {missing.length
            ? t('channels.access.needed', 'Service access needed')
            : t('channels.access.reviewed', 'Service access checked')}
        </strong>
      </div>
      <p>
        {missing.length
          ? t(
              'channels.access.instructions',
              'In NyxID, choose Customize under Service access. Keep the services you still use selected and add the services below, then choose Allow.',
            )
          : t(
              'channels.access.available',
              'Choose the services this channel should use below, then save your changes.',
            )}
      </p>
      {missing.length ? (
        <ul className="channels__access-list">
          {missing.map((id) => {
            const service = services.find((item) => item.id === id);
            return (
              <li key={id}>
                <span>
                  <strong>
                    {service?.label ??
                      t('channels.access.unknown', 'Service not found')}
                  </strong>
                  {service ? (
                    <span className="channels__service-slug">
                      {service.slug}
                    </span>
                  ) : (
                    <details>
                      <summary>
                        {t(
                          'channels.access.requestedIdentity',
                          'Requested service ID',
                        )}
                      </summary>
                      <code>{id}</code>
                    </details>
                  )}
                </span>
                <span>
                  {!service || !service.active
                    ? t(
                        'channels.access.unavailable',
                        'Check availability in NyxID',
                      )
                    : t('channels.access.notAuthorized', 'Access needed')}
                </span>
              </li>
            );
          })}
        </ul>
      ) : null}
      {restored ? (
        <p>
          {t(
            'channels.access.restored',
            'Your unsaved changes have been restored. Review service access and your selections before saving.',
          )}
        </p>
      ) : null}
    </div>
  );
}
