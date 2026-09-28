import { useQuery } from '@tanstack/react-query';
import { Button } from 'antd';
import * as React from 'react';
import type { ChannelServiceChoice } from '@/shared/api/channelServicesApi';
import { getChannelSkillServices } from '@/shared/api/channelSkillServicesApi';
import { t } from '@/shared/i18n/messages';
import { AevatarLoadingDots } from '@/shared/ui/AevatarLoading';
import { buildWorkflowActivitySettingsHref } from '../navigation';
import { channelKeys } from './queries';

export default function ChannelSkillServices({
  scopeId,
  skillName,
  services,
  selectedIds,
  checkingAccess,
  accessFailed,
  disabled,
  onSelect,
  refreshAccess,
}: {
  readonly scopeId: string;
  readonly skillName: string;
  readonly services: readonly ChannelServiceChoice[];
  readonly selectedIds: readonly string[];
  readonly checkingAccess: boolean;
  readonly accessFailed: boolean;
  readonly disabled: boolean;
  readonly onSelect: (id: string) => void;
  readonly refreshAccess: () => void;
}) {
  const titleId = React.useId();
  const query = useQuery({
    queryKey: channelKeys.skillServices(scopeId, skillName),
    queryFn: ({ signal }) => getChannelSkillServices(skillName, signal),
    enabled: Boolean(scopeId && skillName),
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  if (!skillName) return null;
  const authorizedIds = new Set(services.map((service) => service.id));
  const refreshing = query.isFetching || checkingAccess;
  return (
    <section className="channels__skill-services" aria-labelledby={titleId}>
      <div className="channels__services-heading">
        <h3 id={titleId}>
          {t('channels.suggestions.title', 'Suggested for {skill}', {
            skill: skillName,
          })}
        </h3>
        <Button
          size="small"
          loading={refreshing}
          disabled={disabled}
          onClick={() => {
            void query.refetch();
            refreshAccess();
          }}
        >
          {t('channels.suggestions.refresh', 'Refresh suggestions')}
        </Button>
      </div>
      {query.isPending || query.isFetching ? (
        <p role="status" className="channels__form-help">
          <AevatarLoadingDots />
          {t('channels.suggestions.loading', 'Finding related services...')}
        </p>
      ) : query.isError ? (
        <p role="status" className="channels__form-help">
          {t(
            'channels.suggestions.error',
            'Could not identify related services. Refresh to retry, or select services manually below.',
          )}
        </p>
      ) : (
        <>
          <p className="channels__form-help">
            {t(
              'channels.suggestions.help',
              'Suggestions may be incomplete and do not confirm required dependencies. Select the services your task needs; each addition is saved with the channel.',
            )}
          </p>
          {query.data.length ? (
            <ul className="channels__suggestion-list">
              {query.data.map((recommendation) => (
                <li className="channels__suggestion" key={recommendation.slug}>
                  <strong>{recommendation.label}</strong>
                  <span className="channels__service-slug">
                    {recommendation.slug}
                  </span>
                  <p className="channels__form-help">
                    {recommendation.evidence === 'linked'
                      ? t(
                          'channels.suggestions.linked',
                          'Linked to this skill in Ornn.',
                        )
                      : recommendation.evidence === 'catalog'
                        ? t(
                            'channels.suggestions.catalog',
                            'The service catalog recommends this skill.',
                          )
                        : t(
                            'channels.suggestions.mention',
                            'Mentioned in the skill description or instructions; may be needed for some tasks.',
                          )}
                  </p>
                  {recommendation.instances.length ? (
                    recommendation.instances.map((instance) => {
                      const selected = selectedIds.includes(instance.id);
                      const selectable = authorizedIds.has(instance.id);
                      return (
                        <div
                          className="channels__suggestion-instance"
                          key={instance.id}
                        >
                          <div>
                            <span className="channels__service-name">
                              {instance.label}
                            </span>
                            <span className="channels__service-slug">
                              {instance.source === 'personal'
                                ? t('channels.connect.personal', 'Personal')
                                : instance.source === 'organization'
                                  ? instance.organizationName ||
                                    t(
                                      'channels.connect.organization',
                                      'Organization',
                                    )
                                  : t(
                                      'channels.suggestions.unknownSource',
                                      'Unknown source',
                                    )}
                            </span>
                            <span className="channels__service-slug">
                              {checkingAccess
                                ? t(
                                    'channels.suggestions.checking',
                                    'Checking access...',
                                  )
                                : accessFailed
                                  ? t(
                                      'channels.suggestions.accessError',
                                      'Could not check access. Refresh to retry.',
                                    )
                                  : !instance.active
                                    ? t(
                                        'channels.suggestions.inactive',
                                        'Inactive — manage this service in NyxID.',
                                      )
                                    : !instance.allowed
                                      ? t(
                                          'channels.suggestions.unavailable',
                                          'Access unavailable — check with the service owner.',
                                        )
                                      : !selectable
                                        ? t(
                                            'channels.suggestions.unauthorized',
                                            'Not authorized for this session — review service access.',
                                          )
                                        : !selected
                                          ? t(
                                              'channels.suggestions.notSelected',
                                              'Not selected',
                                            )
                                          : null}
                            </span>
                          </div>
                          {selected ? (
                            <span className="channels__suggestion-selected">
                              {t('channels.suggestions.selected', 'Selected')}
                            </span>
                          ) : selectable &&
                            instance.active &&
                            instance.allowed ? (
                            <Button
                              size="small"
                              disabled={
                                disabled || checkingAccess || accessFailed
                              }
                              onClick={() => onSelect(instance.id)}
                              aria-label={t(
                                'channels.suggestions.selectNamed',
                                'Select {service}',
                                { service: instance.label },
                              )}
                            >
                              {t('channels.suggestions.select', 'Select')}
                            </Button>
                          ) : null}
                        </div>
                      );
                    })
                  ) : (
                    <p className="channels__form-help">
                      {t(
                        'channels.suggestions.notConnected',
                        'No connection found in your account. Add this service in NyxID.',
                      )}
                    </p>
                  )}
                </li>
              ))}
            </ul>
          ) : (
            <p role="status" className="channels__form-help">
              {t(
                'channels.suggestions.empty',
                'No related services identified. You can still select services manually below.',
              )}
            </p>
          )}
        </>
      )}
      <div className="channels__suggestion-links">
        <a
          href="https://nyx.chrono-ai.fun/services"
          target="_blank"
          rel="noreferrer"
        >
          {t('channels.suggestions.manage', 'Manage connections in NyxID ↗')}
        </a>
        <a
          href={buildWorkflowActivitySettingsHref(scopeId, 'account')}
          target="_blank"
          rel="noreferrer"
        >
          {t('channels.suggestions.reviewAccess', 'Review service access ↗')}
        </a>
      </div>
    </section>
  );
}
