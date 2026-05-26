import type { ProColumns } from '@ant-design/pro-components';
import React from 'react';
import type {
  ServiceBindingSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicySnapshot,
} from '@/shared/models/governance';
import { AevatarCompactText } from '@/shared/ui/compactText';

export const bindingColumns: ProColumns<ServiceBindingSnapshot>[] = [
  {
    title: 'Binding',
    dataIndex: 'bindingId',
    render: (_, record) => (
      <AevatarCompactText monospace value={record.bindingId} />
    ),
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
    render: (_, record) =>
      record.policyIds.length > 0 ? (
        <AevatarCompactText
          maxChars={24}
          mode="tail"
          monospace
          value={record.policyIds.join(', ')}
        />
      ) : (
        'n/a'
      ),
  },
  {
    title: 'Target',
    render: (_, record) => {
      if (record.serviceRef) {
        return (
          <AevatarCompactText
            monospace
            value={`${record.serviceRef.identity.serviceId}:${
              record.serviceRef.endpointId || '*'
            }`}
          />
        );
      }
      if (record.connectorRef) {
        return (
          <AevatarCompactText
            monospace
            value={`${record.connectorRef.connectorType}:${record.connectorRef.connectorId}`}
          />
        );
      }
      if (record.secretRef) {
        return <AevatarCompactText value={record.secretRef.secretName} />;
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
    render: (_, record) => (
      <AevatarCompactText monospace value={record.policyId} />
    ),
  },
  {
    title: 'Display name',
    dataIndex: 'displayName',
  },
  {
    title: 'Activation bindings',
    render: (_, record) =>
      record.activationRequiredBindingIds.length > 0 ? (
        <AevatarCompactText
          maxChars={24}
          mode="tail"
          monospace
          value={record.activationRequiredBindingIds.join(', ')}
        />
      ) : (
        'n/a'
      ),
  },
  {
    title: 'Allowed callers',
    render: (_, record) =>
      record.invokeAllowedCallerServiceKeys.length > 0 ? (
        <AevatarCompactText
          maxChars={24}
          mode="tail"
          monospace
          value={record.invokeAllowedCallerServiceKeys.join(', ')}
        />
      ) : (
        'n/a'
      ),
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
    render: (_, record) => (
      <AevatarCompactText monospace value={record.endpointId} />
    ),
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
      <AevatarCompactText
        copyable
        maxChars={28}
        mode="tail"
        monospace
        value={record.requestTypeUrl}
      />
    ),
  },
  {
    title: 'Policies',
    render: (_, record) =>
      record.policyIds.length > 0 ? (
        <AevatarCompactText
          maxChars={24}
          mode="tail"
          monospace
          value={record.policyIds.join(', ')}
        />
      ) : (
        'n/a'
      ),
  },
];
