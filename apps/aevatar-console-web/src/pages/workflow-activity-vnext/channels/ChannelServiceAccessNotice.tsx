import { LockOutlined } from '@ant-design/icons';
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
  const draftNotice = restored ? (
    <p className="channels__form-help" role="status">
      {t(
        'channels.access.restored',
        'Your changes have been kept. Review and save.',
      )}
    </p>
  ) : null;
  if (!missing.length) return draftNotice;
  return (
    <>
      {draftNotice}
      <div
        className="channels__access-notice channels__access-notice--needed"
        role="status"
      >
        <div className="channels__access-notice-heading">
          <LockOutlined aria-hidden="true" />
          <strong>
            {t('channels.access.needed', 'Service access needed')}
          </strong>
        </div>
        <p>
          {t(
            'channels.access.instructions',
            'In NyxID, choose Customize under Service access. Keep the services you still use selected and add the services below, then choose Allow.',
          )}
        </p>
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
      </div>
    </>
  );
}
