import {
  ExportOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons';
import { useInfiniteQuery } from '@tanstack/react-query';
import { Button, Input, type InputRef, Select } from 'antd';
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
  const [open, setOpen] = React.useState(false);
  const selectRef = React.useRef<React.ComponentRef<typeof Select>>(null);
  const searchRef = React.useRef<InputRef>(null);
  React.useEffect(() => {
    if (!open) return;
    const frame = window.requestAnimationFrame(() =>
      searchRef.current?.focus(),
    );
    return () => window.cancelAnimationFrame(frame);
  }, [open]);
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
  const choices = [
    ...new Map(
      skills.data?.pages.flatMap((page) =>
        page.items.map((skill) => [skill.name, skill] as const),
      ) ?? [],
    ).values(),
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
          ref={selectRef}
          value={value || undefined}
          open={open && !disabled}
          onOpenChange={(nextOpen) => {
            setOpen(nextOpen);
            if (!nextOpen) setSearch('');
          }}
          disabled={disabled}
          allowClear
          showSearch={false}
          virtual={false}
          classNames={{ popup: { root: 'channels__skill-popup' } }}
          onChange={(name) => {
            onChange(name ?? '');
            setSearch('');
            setOpen(false);
            selectRef.current?.focus();
          }}
          placeholder={t('channels.skills.placeholder', 'Select a skill')}
          aria-invalid={Boolean(error)}
          aria-describedby={error || skills.isError ? `${id}-error` : undefined}
          options={choices.map((skill) => ({
            value: skill.name,
            label: skill.name,
            description: skill.description,
          }))}
          optionRender={(option) => (
            <div className="channels__skill-option">
              <strong>{option.data.label}</strong>
              {option.data.description ? (
                <span>{option.data.description}</span>
              ) : null}
            </div>
          )}
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
              <div className="channels__skill-search">
                <Input
                  ref={searchRef}
                  prefix={<SearchOutlined />}
                  value={search}
                  aria-label={t('channels.skills.search', 'Search skills')}
                  placeholder={t('channels.skills.search', 'Search skills')}
                  onChange={(event) => setSearch(event.target.value)}
                  onKeyDown={(event) => {
                    if (event.key === 'ArrowDown' || event.key === 'Escape') {
                      event.preventDefault();
                      selectRef.current?.focus();
                      if (event.key === 'Escape') setOpen(false);
                    }
                    event.stopPropagation();
                  }}
                />
                <div className="channels__skill-list-heading">
                  <p>{t('channels.skills.available', 'Available in Ornn')}</p>
                  <AevatarTooltip
                    title={t('channels.skills.refresh', 'Refresh skills')}
                  >
                    <Button
                      type="text"
                      icon={<ReloadOutlined />}
                      aria-label={t(
                        'channels.skills.refresh',
                        'Refresh skills',
                      )}
                      disabled={disabled || skills.isFetching}
                      loading={skills.isFetching && !skills.isFetchingNextPage}
                      onClick={() => void skills.refetch()}
                    />
                  </AevatarTooltip>
                </div>
              </div>
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
