import type { ProColumns } from '@ant-design/pro-components';
import React from 'react';
import type {
  ServiceBindingSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicySnapshot,
} from '@/shared/models/governance';
import {
  ConsoleMessage,
  t,
  type ConsoleMessageDescriptor,
} from '@/shared/i18n/messages';
import { getUserFacingIdentifierLabel } from '@/shared/ui/userFacingIdentifiers';

const bindingColumnMessages = {
  displayName: {
    id: 'pages.governance.columns.display.name',
    defaultMessage: 'Display name',
  },
} satisfies Record<string, ConsoleMessageDescriptor>;

const policyColumnMessages = {
  activeDeploymentRequired: {
    id: 'pages.governance.columns.active.deployment.required',
    defaultMessage: 'Active deployment required',
  },
  activationBindings: {
    id: 'pages.governance.columns.activation.bindings',
    defaultMessage: 'Activation bindings',
  },
  allowedCallers: {
    id: 'pages.governance.columns.allowed.callers',
    defaultMessage: 'Allowed callers',
  },
  displayName: {
    id: 'pages.governance.columns.display.name.3',
    defaultMessage: 'Display name',
  },
} satisfies Record<string, ConsoleMessageDescriptor>;

const endpointColumnMessages = {
  displayName: {
    id: 'pages.governance.columns.display.name.2',
    defaultMessage: 'Display name',
  },
  requestType: {
    id: 'pages.governance.columns.request.type',
    defaultMessage: 'Request type',
  },
} satisfies Record<string, ConsoleMessageDescriptor>;

export const bindingColumns: ProColumns<ServiceBindingSnapshot>[] = [
  {
    title: 'Binding',
    dataIndex: 'bindingId',
    render: (_, record) =>
      getUserFacingIdentifierLabel(
        record.displayName || record.bindingId,
        t("pages.governance.columns.binding", "Binding"),
      ),
  },
  {
    title: <ConsoleMessage descriptor={bindingColumnMessages.displayName} />,
    dataIndex: 'displayName',
  },
  {
    title: 'Kind',
    dataIndex: 'bindingKind',
  },
  {
    title: 'Policies',
    render: (_, record) =>
      record.policyIds.length > 0
        ? t("pages.governance.columns.policy.count", "{value1} policies", {
            value1: record.policyIds.length,
          })
        : 'n/a',
  },
  {
    title: 'Target',
    render: (_, record) => {
      if (record.serviceRef) {
        return record.serviceRef.endpointId
          ? t("pages.governance.columns.service.endpoint.target", "Service endpoint target")
          : t("pages.governance.columns.service.target", "Service target");
      }
      if (record.connectorRef) {
        return record.connectorRef.connectorType || t("pages.governance.columns.connector.target", "Connector target");
      }
      if (record.secretRef) {
        return record.secretRef.secretName || t("pages.governance.columns.secret.target", "Secret target");
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
    render: (_, record) =>
      getUserFacingIdentifierLabel(
        record.displayName || record.policyId,
        t("pages.governance.columns.policy", "Policy"),
      ),
  },
  {
    title: <ConsoleMessage descriptor={policyColumnMessages.displayName} />,
    dataIndex: 'displayName',
  },
  {
    title: (
      <ConsoleMessage descriptor={policyColumnMessages.activationBindings} />
    ),
    render: (_, record) =>
      record.activationRequiredBindingIds.length > 0
        ? t("pages.governance.columns.binding.count", "{value1} bindings", {
            value1: record.activationRequiredBindingIds.length,
          })
        : 'n/a',
  },
  {
    title: <ConsoleMessage descriptor={policyColumnMessages.allowedCallers} />,
    render: (_, record) =>
      record.invokeAllowedCallerServiceKeys.length > 0
        ? t("pages.governance.columns.caller.count", "{value1} callers", {
            value1: record.invokeAllowedCallerServiceKeys.length,
          })
        : 'n/a',
  },
  {
    title: (
      <ConsoleMessage
        descriptor={policyColumnMessages.activeDeploymentRequired}
      />
    ),
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
    render: (_, record) =>
      getUserFacingIdentifierLabel(
        record.displayName || record.endpointId,
        t("pages.governance.columns.endpoint", "Endpoint"),
      ),
  },
  {
    title: <ConsoleMessage descriptor={endpointColumnMessages.displayName} />,
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
    title: <ConsoleMessage descriptor={endpointColumnMessages.requestType} />,
    dataIndex: 'requestTypeUrl',
    render: (_, record) =>
      record.requestTypeUrl
        ? t("pages.governance.columns.request.contract.ready", "Request contract ready")
        : 'n/a',
  },
  {
    title: 'Policies',
    render: (_, record) =>
      record.policyIds.length > 0
        ? t("pages.governance.columns.policy.count", "{value1} policies", {
            value1: record.policyIds.length,
          })
        : 'n/a',
  },
];
