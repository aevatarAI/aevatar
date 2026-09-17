import {
  ExportOutlined,
  PlusOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { useInfiniteQuery } from '@tanstack/react-query';
import { Button, Select } from 'antd';
import * as React from 'react';
import { searchChannelSkills } from '@/shared/api/channelSkillsApi';
import { t } from '@/shared/i18n/messages';
import { getOrnnRuntimeConfig } from '@/shared/studio/ornnConfig';
import { AevatarLoadingDots } from '@/shared/ui/AevatarLoading';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { channelKeys } from './queries';

export default function ChannelSkillField({
  scopeId,
  value,
  onChange,
  disabled,
  error,
}: {
  readonly scopeId: string;
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly disabled: boolean;
  readonly error?: string;
}) {
  const id = React.useId();
  const [search, setSearch] = React.useState('');
  const [term, setTerm] = React.useState('');
  React.useEffect(() => {
    const timer = window.setTimeout(() => setTerm(search.trim()), 250);
    return () => window.clearTimeout(timer);
  }, [search]);
  const skills = useInfiniteQuery({
    queryKey: channelKeys.skills(scopeId, term),
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) =>
      searchChannelSkills(term, pageParam, signal),
    getNextPageParam: (page) => page.nextCursor,
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    enabled: Boolean(scopeId),
  });
  // The registration contract accepts a name, not an Ornn GUID.
  const names = [
    ...new Set(
      skills.data?.pages.flatMap((page) =>
        page.items.map((skill) => skill.name),
      ) ?? [],
    ),
  ];
  const config = getOrnnRuntimeConfig();
  const createHref = config.configurationError
    ? null
    : `${config.baseUrl}/skills/new/generate`;
  return (
    <div className="channels__field">
      <div className="channels__field-heading">
        <label htmlFor={id}>
          {t('channels.connect.skillName', 'Skill')}{' '}
          <span>{t('channels.connect.optional', '(optional)')}</span>
        </label>
      </div>
      <div className="channels__skill-controls">
        <Select
          id={id}
          value={value || undefined}
          disabled={disabled}
          allowClear
          showSearch
          filterOption={false}
          onSearch={setSearch}
          onChange={(name) => {
            onChange(name ?? '');
            setSearch('');
          }}
          placeholder={t('channels.skills.placeholder', 'Select a skill')}
          aria-invalid={Boolean(error)}
          aria-describedby={error || skills.isError ? `${id}-error` : undefined}
          options={names.map((name) => ({ value: name, label: name }))}
          notFoundContent={
            skills.isFetching ? (
              <AevatarLoadingDots
                ariaLabel={t('channels.skills.loading', 'Loading skills')}
              />
            ) : skills.isError ? (
              t(
                'channels.skills.failed',
                'Could not load skills. Use Refresh to try again.',
              )
            ) : (
              t('channels.skills.empty', 'No skills found')
            )
          }
          popupRender={(menu) => (
            <>
              {menu}
              {skills.hasNextPage ? (
                <Button
                  type="text"
                  block
                  loading={skills.isFetchingNextPage}
                  disabled={skills.isFetching}
                  onClick={() => void skills.fetchNextPage()}
                >
                  {t('channels.skills.more', 'Load more skills')}
                </Button>
              ) : null}
              {createHref ? (
                <AevatarTooltip
                  title={t(
                    'channels.skills.createHelp',
                    'Opens Ornn in a new tab. Create your skill, then return here and refresh the list.',
                  )}
                >
                  <a
                    className="channels__skill-create"
                    href={createHref}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    <PlusOutlined />
                    {t('channels.skills.create', 'Create new skill')}
                    <ExportOutlined />
                  </a>
                </AevatarTooltip>
              ) : null}
            </>
          )}
        />
        <AevatarTooltip title={t('channels.skills.refresh', 'Refresh skills')}>
          <Button
            icon={<ReloadOutlined />}
            aria-label={t('channels.skills.refresh', 'Refresh skills')}
            disabled={disabled || skills.isFetching}
            loading={skills.isFetching && !skills.isFetchingNextPage}
            onClick={() => void skills.refetch()}
          />
        </AevatarTooltip>
      </div>
      {error || skills.isError ? (
        <p id={`${id}-error`} className="channels__form-error" role="alert">
          {error ||
            t(
              'channels.skills.failed',
              'Could not load skills. Use Refresh to try again.',
            )}
        </p>
      ) : null}
    </div>
  );
}
