import { SearchOutlined } from '@ant-design/icons';
import { Button, Checkbox, Input } from 'antd';
import * as React from 'react';
import type { ChannelServiceChoice } from '@/shared/api/channelServicesApi';
import { t } from '@/shared/i18n/messages';
import { AevatarContentSkeleton } from '@/shared/ui/AevatarContentSkeleton';

export default function ChannelServicePicker({
  services,
  selectedIds,
  onChange,
  loading,
  failed,
  refreshing,
  disabled,
  retry,
}: {
  readonly services: readonly ChannelServiceChoice[];
  readonly selectedIds: readonly string[];
  readonly onChange: (ids: string[]) => void;
  readonly loading: boolean;
  readonly failed: boolean;
  readonly refreshing: boolean;
  readonly disabled: boolean;
  readonly retry: () => void;
}) {
  const [search, setSearch] = React.useState('');
  const term = search.trim().toLocaleLowerCase();
  const visible = services.filter((service) =>
    `${service.label} ${service.slug}`.toLocaleLowerCase().includes(term),
  );
  const selectableIds = visible
    .filter((service) => service.active && service.allowed)
    .map((service) => service.id);
  const selectedCount = selectableIds.filter((id) =>
    selectedIds.includes(id),
  ).length;
  const allSelected =
    selectableIds.length > 0 && selectedCount === selectableIds.length;
  return (
    <section
      className="channels__services"
      aria-labelledby="channel-services-title"
    >
      <div className="channels__services-heading">
        <h2 id="channel-services-title">
          {t('channels.connect.services', 'Services')}
        </h2>
        <span aria-live="polite">
          {t('channels.connect.selected', '{count} selected', {
            count: selectedIds.length,
          })}
        </span>
      </div>
      <p className="channels__form-help">
        {t(
          'channels.connect.servicesHelp',
          'Only services available through your current NyxID authorization are shown.',
        )}
      </p>
      {loading ? (
        <AevatarContentSkeleton
          ariaLabel={t('channels.connect.servicesLoading', 'Loading services')}
          variant="list"
          rows={3}
        />
      ) : failed ? (
        <div className="channels__service-state" role="alert">
          <p>
            {t(
              'channels.connect.servicesError',
              'Could not load your services. Retry before connecting.',
            )}
          </p>
          <Button loading={refreshing} onClick={retry} disabled={disabled}>
            {t('channels.retry', 'Try again')}
          </Button>
        </div>
      ) : !services.length ? (
        <div className="channels__service-state" role="status">
          {t(
            'channels.connect.servicesEmpty',
            'No services are available with your current NyxID authorization. You can connect this bot without service access.',
          )}
        </div>
      ) : (
        <div className="channels__service-picker">
          <Input
            aria-label={t(
              'channels.connect.searchServices',
              'Search services by name or slug',
            )}
            placeholder={t(
              'channels.connect.searchServices',
              'Search services by name or slug',
            )}
            prefix={<SearchOutlined aria-hidden="true" />}
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            disabled={disabled}
          />
          <div className="channels__service-bulk">
            <Checkbox
              checked={allSelected}
              indeterminate={selectedCount > 0 && !allSelected}
              disabled={disabled || !selectableIds.length}
              onChange={(event) =>
                onChange(
                  event.target.checked
                    ? [...new Set([...selectedIds, ...selectableIds])]
                    : selectedIds.filter((id) => !selectableIds.includes(id)),
                )
              }
            >
              {term
                ? t('channels.connect.selectAllResults', 'Select all results')
                : t('channels.connect.selectAll', 'Select all')}
            </Checkbox>
          </div>
          <div className="channels__service-options">
            {visible.length ? (
              visible.map((service) => {
                const selected = selectedIds.includes(service.id);
                const unavailable = !service.active || !service.allowed;
                return (
                  <div
                    className={`channels__service-option${selected ? ' channels__service-option--selected' : ''}`}
                    key={service.id}
                  >
                    <Checkbox
                      checked={selected}
                      disabled={disabled || (unavailable && !selected)}
                      onChange={(event) =>
                        onChange(
                          event.target.checked
                            ? [...selectedIds, service.id]
                            : selectedIds.filter((id) => id !== service.id),
                        )
                      }
                    >
                      <span className="channels__service-name">
                        {service.label}
                      </span>
                      <span className="channels__service-slug">
                        {service.slug}
                      </span>
                    </Checkbox>
                    <span className="channels__service-source">
                      {unavailable
                        ? t(
                            'channels.connect.serviceUnavailable',
                            'Unavailable',
                          )
                        : service.source === 'personal'
                          ? t('channels.connect.personal', 'Personal')
                          : service.organizationName ||
                            t('channels.connect.organization', 'Organization')}
                    </span>
                  </div>
                );
              })
            ) : (
              <p className="channels__service-state" role="status">
                {t(
                  'channels.connect.noMatches',
                  'No services match your search.',
                )}
              </p>
            )}
          </div>
        </div>
      )}
      <p className="channels__form-help">
        {t(
          'channels.connect.selectionHelp',
          'Only selected services will be available to this bot.',
        )}
      </p>
    </section>
  );
}
