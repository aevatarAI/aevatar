import { useState } from 'react';
import * as api from '../api';

type ServiceItem = {
  serviceId: string;
  displayName: string;
  deploymentStatus: string;
  endpoints: { endpointId: string; kind: string; displayName: string }[];
};

type BindingItem = {
  bindingId: string;
  displayName: string;
  bindingKind: string;
  serviceRef?: any;
  connectorRef?: any;
  secretRef?: any;
};

function readStr(obj: any, ...keys: string[]): string {
  for (const k of keys) {
    if (typeof obj?.[k] === 'string') return obj[k];
  }
  return '';
}

function parseService(raw: any): ServiceItem {
  return {
    serviceId: readStr(raw, 'serviceId', 'ServiceId'),
    displayName: readStr(raw, 'displayName', 'DisplayName'),
    deploymentStatus: readStr(raw, 'deploymentStatus', 'DeploymentStatus'),
    endpoints: (raw?.endpoints ?? raw?.Endpoints ?? []).map((ep: any) => ({
      endpointId: readStr(ep, 'endpointId', 'EndpointId'),
      kind: readStr(ep, 'kind', 'Kind'),
      displayName: readStr(ep, 'displayName', 'DisplayName'),
    })),
  };
}

function parseBinding(raw: any): BindingItem {
  return {
    bindingId: readStr(raw, 'bindingId', 'BindingId'),
    displayName: readStr(raw, 'displayName', 'DisplayName'),
    bindingKind: readStr(raw, 'bindingKind', 'BindingKind'),
    serviceRef: raw?.serviceRef ?? raw?.ServiceRef,
    connectorRef: raw?.connectorRef ?? raw?.ConnectorRef,
    secretRef: raw?.secretRef ?? raw?.SecretRef,
  };
}

function bindingTarget(b: BindingItem): string {
  if (b.serviceRef) {
    const sid = readStr(b.serviceRef?.identity ?? b.serviceRef?.Identity, 'serviceId', 'ServiceId');
    const eid = readStr(b.serviceRef, 'endpointId', 'EndpointId');
    return sid ? `${sid}${eid ? '/' + eid : ''}` : eid;
  }
  if (b.connectorRef) {
    return readStr(b.connectorRef, 'connectorType', 'ConnectorType') + '/' + readStr(b.connectorRef, 'connectorId', 'ConnectorId');
  }
  if (b.secretRef) {
    return readStr(b.secretRef, 'secretName', 'SecretName');
  }
  return '';
}

export default function ServicesTab(props: { scopeId: string }) {
  const [services, setServices] = useState<ServiceItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [selectedService, setSelectedService] = useState<string | null>(null);
  const [bindings, setBindings] = useState<BindingItem[]>([]);
  const [bindingsLoading, setBindingsLoading] = useState(false);

  async function handleLoad() {
    if (!props.scopeId.trim()) return;
    setLoading(true);
    setError('');
    try {
      const data = await api.runtime.listServices(props.scopeId);
      setServices((data ?? []).map(parseService));
    } catch (e: any) {
      setError(e?.message || String(e));
    } finally {
      setLoading(false);
    }
  }

  async function handleSelectService(serviceId: string) {
    setSelectedService(serviceId);
    setBindingsLoading(true);
    try {
      const data = await api.runtime.getBinding(props.scopeId);
      const list = data?.bindings ?? data?.Bindings ?? [];
      setBindings(Array.isArray(list) ? list.map(parseBinding) : []);
    } catch {
      setBindings([]);
    } finally {
      setBindingsLoading(false);
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex gap-2 items-end">
        <button
          onClick={handleLoad}
          disabled={loading || !props.scopeId.trim()}
          className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-40"
        >
          {loading ? 'Loading...' : 'Load Services'}
        </button>
      </div>

      {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-[13px] text-red-700">{error}</div>}

      {services.length > 0 && (
        <div className="overflow-auto rounded-lg border border-[#E6E3DE]">
          <table className="w-full text-[13px]">
            <thead className="bg-[#F7F5F2]">
              <tr>
                <th className="px-4 py-2.5 text-left font-semibold text-gray-600">Service ID</th>
                <th className="px-4 py-2.5 text-left font-semibold text-gray-600">Display Name</th>
                <th className="px-4 py-2.5 text-left font-semibold text-gray-600">Status</th>
                <th className="px-4 py-2.5 text-left font-semibold text-gray-600">Endpoints</th>
              </tr>
            </thead>
            <tbody className="bg-white divide-y divide-[#E6E3DE]">
              {services.map(s => (
                <tr
                  key={s.serviceId}
                  onClick={() => handleSelectService(s.serviceId)}
                  className={`cursor-pointer hover:bg-[#F7F5F2] ${selectedService === s.serviceId ? 'bg-blue-50' : ''}`}
                >
                  <td className="px-4 py-2.5 font-mono">{s.serviceId}</td>
                  <td className="px-4 py-2.5">{s.displayName}</td>
                  <td className="px-4 py-2.5">
                    <span className="rounded-full bg-[#F0EDE8] px-2 py-0.5 text-[11px]">{s.deploymentStatus}</span>
                  </td>
                  <td className="px-4 py-2.5">{s.endpoints.map(ep => ep.kind || ep.endpointId).join(', ')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {services.length === 0 && !loading && !error && (
        <div className="text-[13px] text-gray-400">No services loaded. Click "Load Services" to fetch.</div>
      )}

      {selectedService && (
        <div className="space-y-2">
          <div className="text-[12px] font-semibold text-gray-500 uppercase tracking-wider">
            Bindings for {selectedService}
            {bindingsLoading && <span className="ml-2 text-gray-400">loading...</span>}
          </div>
          {bindings.length > 0 ? (
            <div className="overflow-auto rounded-lg border border-[#E6E3DE]">
              <table className="w-full text-[13px]">
                <thead className="bg-[#F7F5F2]">
                  <tr>
                    <th className="px-4 py-2.5 text-left font-semibold text-gray-600">Binding ID</th>
                    <th className="px-4 py-2.5 text-left font-semibold text-gray-600">Name</th>
                    <th className="px-4 py-2.5 text-left font-semibold text-gray-600">Kind</th>
                    <th className="px-4 py-2.5 text-left font-semibold text-gray-600">Target</th>
                  </tr>
                </thead>
                <tbody className="bg-white divide-y divide-[#E6E3DE]">
                  {bindings.map(b => (
                    <tr key={b.bindingId}>
                      <td className="px-4 py-2.5 font-mono">{b.bindingId}</td>
                      <td className="px-4 py-2.5">{b.displayName}</td>
                      <td className="px-4 py-2.5">
                        <span className="rounded-full bg-[#F0EDE8] px-2 py-0.5 text-[11px]">{b.bindingKind}</span>
                      </td>
                      <td className="px-4 py-2.5 font-mono text-[12px]">{bindingTarget(b)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : !bindingsLoading ? (
            <div className="text-[13px] text-gray-400">No bindings found.</div>
          ) : null}
        </div>
      )}
    </div>
  );
}
