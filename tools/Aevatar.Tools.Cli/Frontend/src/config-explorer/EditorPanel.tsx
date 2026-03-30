import type { ConfigStore } from './useConfigStore';
import ConfigEditor from './editors/ConfigEditor';
import RolesEditor from './editors/RolesEditor';
import ConnectorsEditor from './editors/ConnectorsEditor';
import ActorsEditor from './editors/ActorsEditor';

type Props = {
  store: ConfigStore;
  flash: (msg: string, type: 'success' | 'error') => void;
};

export default function EditorPanel({ store, flash }: Props) {
  switch (store.selectedFile) {
    case 'config.json':
      return <ConfigEditor store={store} flash={flash} />;
    case 'roles.json':
      return <RolesEditor store={store} flash={flash} />;
    case 'connectors.json':
      return <ConnectorsEditor store={store} flash={flash} />;
    case 'actors.json':
      return <ActorsEditor store={store} flash={flash} />;
    default:
      return null;
  }
}
