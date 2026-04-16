import { Button, Input, InputNumber, Typography, theme } from 'antd';
import React from 'react';
import type { ServiceQueryDraft } from './serviceQuery';

type ServiceQueryCardProps = {
  draft: ServiceQueryDraft;
  onChange: (draft: ServiceQueryDraft) => void;
  onLoad: () => void;
  onReset?: () => void;
  loadLabel?: string;
  variant?: 'card' | 'inline';
};

const ServiceQueryCard: React.FC<ServiceQueryCardProps> = ({
  draft,
  onChange,
  onLoad,
  onReset,
  loadLabel = '筛选服务',
  variant = 'card',
}) => {
  const { token } = theme.useToken();
  const inline = variant === 'inline';
  const fieldLabelStyle: React.CSSProperties = {
    color: token.colorTextSecondary,
    fontSize: 11,
    fontWeight: 700,
    letterSpacing: 0.24,
    textTransform: 'uppercase',
  };
  const controlStyle: React.CSSProperties = {
    borderColor: token.colorBorderSecondary,
    borderRadius: 14,
    boxShadow: 'none',
  };

  return (
    <div
      style={{
        background: inline
          ? 'transparent'
          : `linear-gradient(145deg, ${token.colorBgElevated} 0%, ${token.colorFillAlter} 100%)`,
        border: inline ? 'none' : `1px solid ${token.colorBorderSecondary}`,
        borderRadius: inline ? 0 : 20,
        boxShadow: inline ? 'none' : token.boxShadowTertiary,
        padding: inline ? 0 : 16,
      }}
    >
      <div
        style={{
          display: 'grid',
          gap: 14,
          gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))',
        }}
      >
        {[
          {
            key: 'tenantId',
            label: 'Team / Tenant',
            placeholder: '团队 ID',
            value: draft.tenantId,
          },
          {
            key: 'appId',
            label: 'App',
            placeholder: '应用 ID',
            value: draft.appId,
          },
          {
            key: 'namespace',
            label: 'Namespace',
            placeholder: '命名空间',
            value: draft.namespace,
          },
        ].map((field) => (
          <label
            key={field.key}
            style={{
              display: 'flex',
              flexDirection: 'column',
              gap: 6,
              minWidth: 0,
            }}
          >
            <Typography.Text style={fieldLabelStyle}>{field.label}</Typography.Text>
            <Input
              size="large"
              placeholder={field.placeholder}
              style={controlStyle}
              value={field.value}
              onChange={(event) =>
                onChange({
                  ...draft,
                  [field.key]: event.target.value,
                })
              }
            />
          </label>
        ))}

        <label
          style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 6,
            justifySelf: inline ? 'stretch' : 'end',
            maxWidth: inline ? '100%' : 180,
            minWidth: 0,
            width: '100%',
          }}
        >
          <Typography.Text style={fieldLabelStyle}>Result window</Typography.Text>
          <InputNumber
            controls={false}
            min={1}
            max={500}
            size="large"
            style={{ ...controlStyle, width: '100%' }}
            value={draft.take}
            onChange={(value) =>
              onChange({
                ...draft,
                take: Number(value) || 200,
              })
            }
          />
        </label>

        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            flexWrap: 'wrap',
            gap: 8,
            gridColumn: '1 / -1',
            justifyContent: 'flex-end',
            marginTop: 2,
          }}
        >
          <Button onClick={onLoad} type="primary">
            {loadLabel}
          </Button>
          {onReset ? <Button onClick={onReset}>重置</Button> : null}
        </div>
      </div>
    </div>
  );
};

export default ServiceQueryCard;
