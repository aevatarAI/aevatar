import ConnectorsCatalogEditor from '../config-explorer/editors/ConnectorsCatalogEditor';

type Props = {
  flash: (msg: string, type: 'success' | 'error' | 'info') => void;
  onSaved?: () => void;
};

export default function ConnectorsPage({ flash, onSaved }: Props) {
  const flashSuccessOrError = (msg: string, type: 'success' | 'error') => flash(msg, type);

  return (
    <>
      <header className="workspace-page-header h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Catalog</div>
          <div className="text-[18px] font-bold text-gray-800 mt-0.5">Connectors</div>
          <div className="text-[11px] text-gray-400 mt-0.5">
            HTTP / CLI / MCP endpoints persisted to this scope's connector-catalog actor.
          </div>
        </div>
      </header>

      <section className="flex-1 min-h-0 overflow-y-auto bg-[#F2F1EE] p-6">
        <ConnectorsCatalogEditor flash={flashSuccessOrError} onSaved={onSaved} />
      </section>
    </>
  );
}
