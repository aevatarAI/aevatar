import { Input } from 'antd';
import * as React from 'react';
import { t } from '@/shared/i18n/messages';

export default function ChannelSkillField({
  value,
  onChange,
  disabled,
  placeholder,
  error,
}: {
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly disabled: boolean;
  readonly placeholder?: string;
  readonly error?: string;
}) {
  const id = React.useId();
  return (
    <div className="channels__field">
      <div className="channels__field-heading">
        <label htmlFor={id}>
          {t('channels.connect.skillName', 'Skill name')}{' '}
          <span>{t('channels.connect.optional', '(optional)')}</span>
        </label>
      </div>
      <Input
        id={id}
        value={value}
        disabled={disabled}
        placeholder={placeholder}
        aria-invalid={Boolean(error)}
        aria-describedby={error ? `${id}-error` : undefined}
        onChange={(event) => onChange(event.target.value)}
      />
      {error ? (
        <p id={`${id}-error`} className="channels__form-error" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}
