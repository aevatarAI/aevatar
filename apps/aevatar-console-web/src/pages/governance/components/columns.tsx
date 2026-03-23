import type { ProColumns } from '@ant-design/pro-components';
import { Typography } from 'antd';
import React from 'react';
import type {
  ServiceBindingSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicySnapshot,
} from '@/shared/models/governance';

export const bindingColumns: ProColumns<ServiceBindingSnapshot>[] = [
  {
    title: 'Binding',
    dataIndex: 'bindingId',
  },
  {
    title: 'Display name',
    dataIndex: 'displayName',
  },
  {
    title: 'Kind',
    dataIndex: 'bindingKind',
  },
  {
    title: 'Policies',
    render: (_, record) => record.policyIds.join(', ') || 'n/a',
  },
  {
    title: 'Target',
    render: (_, record) => {
      if (record.serviceRef) {
        return `${record.serviceRef.identity.serviceId}:${
          record.serviceRef.endpointId || '*'
        }`;
      }
      if (record.connectorRef) {
        return `${record.connectorRef.connectorType}:${record.connectorRef.connectorId}`;
      }
      if (record.secretRef) {
        return record.secretRef.secretName;
      }
      return 'n/a';
    },
  },
  {
    title: 'Retired',
    render: (_, record) => (record.retired ? 'yes' : 'no'),
  },
];

export const policyColumns: ProColumns<ServicePolicySnapshot>[] = [
  {
    title: 'Policy',
    dataIndex: 'policyId',
  },
  {
    title: 'Display name',
    dataIndex: 'displayName',
  },
  {
    title: 'Activation bindings',
    render: (_, record) =>
      record.activationRequiredBindingIds.join(', ') || 'n/a',
  },
  {
    title: 'Allowed callers',
    render: (_, record) =>
      record.invokeAllowedCallerServiceKeys.join(', ') || 'n/a',
  },
  {
    title: 'Active deployment required',
    render: (_, record) =>
      record.invokeRequiresActiveDeployment ? 'yes' : 'no',
  },
  {
    title: 'Retired',
    render: (_, record) => (record.retired ? 'yes' : 'no'),
  },
];

export const endpointColumns: ProColumns<ServiceEndpointExposureSnapshot>[] = [
  {
    title: 'Endpoint',
    dataIndex: 'endpointId',
  },
  {
    title: 'Display name',
    dataIndex: 'displayName',
  },
  {
    title: 'Kind',
    dataIndex: 'kind',
  },
  {
    title: 'Exposure',
    dataIndex: 'exposureKind',
  },
  {
    title: 'Request type',
    dataIndex: 'requestTypeUrl',
    render: (_, record) => (
      <Typography.Text copyable>{record.requestTypeUrl}</Typography.Text>
    ),
  },
  {
    title: 'Policies',
    render: (_, record) => record.policyIds.join(', ') || 'n/a',
  },
];
