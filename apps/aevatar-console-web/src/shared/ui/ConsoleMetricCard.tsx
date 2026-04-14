import { Typography } from 'antd';
import React from 'react';

type ConsoleMetricCardProps = {
  readonly label: string;
  readonly tone?: 'default' | 'green' | 'purple';
  readonly value: React.ReactNode;
};

const valueColorByTone = {
  default: '#1d2129',
  green: '#52c41a',
  purple: '#6c5ce7',
} as const;

const ConsoleMetricCard: React.FC<ConsoleMetricCardProps> = ({
  label,
  tone = 'default',
  value,
}) => (
  <div
    style={{
      background: '#ffffff',
      border: '1px solid #e8e8e8',
      borderRadius: 10,
      display: 'flex',
      flexDirection: 'column',
      justifyContent: 'center',
      minHeight: 112,
      padding: 16,
      textAlign: 'center',
    }}
  >
    <Typography.Text
      style={{
        color: valueColorByTone[tone],
        fontSize: 28,
        fontWeight: 700,
        lineHeight: 1.1,
      }}
    >
      {value}
    </Typography.Text>
    <Typography.Text
      style={{
        color: '#8c8c8c',
        fontSize: 11,
        marginTop: 2,
      }}
    >
      {label}
    </Typography.Text>
  </div>
);

export default ConsoleMetricCard;
