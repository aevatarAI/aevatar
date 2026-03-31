import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as api from '../api';
import {
  type RoleState,
  type ConnectorState,
  toRoleState,
  toConnectorState,
  toRolePayload,
  toConnectorPayload,
  createRoleState,
  createEmptyConnector,
  createUniqueConnectorName,
} from '../studio';
import type { ConfigFile, GAgentType, ActorGroup, ProviderInfo, WorkflowEntry } from './types';
import type { ConversationMeta, StoredChatMessage } from '../runtime/chatTypes';

export type ConfigStore = ReturnType<typeof useConfigStore>;

export function useConfigStore(scopeId: string) {
  const [loading, setLoading] = useState(true);
  const [selectedFile, setSelectedFile] = useState<ConfigFile>('config.json');

  // ── config.json ──
  const [configJson, setConfigJson] = useState('');
  const [configSaving, setConfigSaving] = useState(false);
  const configSnap = useRef('');

  // ── roles.json ──
  const [roles, setRoles] = useState<RoleState[]>([]);
  const [rolesSaving, setRolesSaving] = useState(false);
  const rolesSnap = useRef('');

  // ── connectors.json ──
  const [connectors, setConnectors] = useState<ConnectorState[]>([]);
  const [connectorsSaving, setConnectorsSaving] = useState(false);
  const connectorsSnap = useRef('');

  // ── actors.json ──
  const [actorGroups, setActorGroups] = useState<ActorGroup[]>([]);
  const [actorTypes, setActorTypes] = useState<GAgentType[]>([]);

  // ── LLM models ──
  const [providers, setProviders] = useState<ProviderInfo[]>([]);
  const [supportedModels, setSupportedModels] = useState<string[]>([]);
  const [modelsLoading] = useState(false);

  // ── Workflows ──
  const [workflows, setWorkflows] = useState<WorkflowEntry[]>([]);
  const [selectedWorkflowYaml, setSelectedWorkflowYaml] = useState<string | null>(null);
  const [workflowLoading, setWorkflowLoading] = useState(false);

  // ── Chat history ──
  const [chatConversations, setChatConversations] = useState<ConversationMeta[]>([]);
  const [selectedConversationMessages, setSelectedConversationMessages] = useState<StoredChatMessage[]>([]);
  const [chatLoading, setChatLoading] = useState(false);

  // ── Dirty tracking ──
  const configDirty = useMemo(() => configJson !== configSnap.current, [configJson]);
  const rolesDirty = useMemo(() => JSON.stringify(roles) !== rolesSnap.current, [roles]);
  const connectorsDirty = useMemo(() => JSON.stringify(connectors) !== connectorsSnap.current, [connectors]);

  const anyDirty = configDirty || rolesDirty || connectorsDirty;

  function isDirty(file: ConfigFile): boolean {
    if (file === 'config.json') return configDirty;
    if (file === 'roles.json') return rolesDirty;
    if (file === 'connectors.json') return connectorsDirty;
    return false;
  }

  // ── Load all data ──
  const loadAll = useCallback(async () => {
    setLoading(true);
    const [configRes, rolesRes, connectorsRes, actorsRes, typesRes, modelsRes, chatRes, workflowsRes] =
      await Promise.allSettled([
        api.userConfig.get(),
        api.roles.getCatalog(),
        api.connectors.getCatalog(),
        scopeId ? api.gagent.listActors(scopeId) : Promise.resolve([]),
        api.gagent.listTypes(),
        api.userConfig.models(),
        scopeId ? api.chatHistory.getIndex(scopeId) : Promise.resolve({ conversations: [] }),
        api.workspace.listWorkflows(),
      ]);

    // config — store as raw JSON
    if (configRes.status === 'fulfilled' && configRes.value) {
      const json = JSON.stringify(configRes.value, null, 2);
      setConfigJson(json);
      configSnap.current = json;
    }

    // roles
    if (rolesRes.status === 'fulfilled' && rolesRes.value) {
      const list: RoleState[] = (rolesRes.value.roles || []).map((r: any, i: number) => toRoleState(r, i + 1));
      setRoles(list);
      rolesSnap.current = JSON.stringify(list);
    }

    // connectors
    if (connectorsRes.status === 'fulfilled' && connectorsRes.value) {
      const list: ConnectorState[] = (connectorsRes.value.connectors || []).map((c: any) => toConnectorState(c));
      setConnectors(list);
      connectorsSnap.current = JSON.stringify(list);
    }

    // actors
    if (actorsRes.status === 'fulfilled') {
      setActorGroups(actorsRes.value ?? []);
    }

    // types
    if (typesRes.status === 'fulfilled') {
      setActorTypes(typesRes.value ?? []);
    }

    // models
    if (modelsRes.status === 'fulfilled' && modelsRes.value) {
      setProviders(modelsRes.value.providers ?? []);
      setSupportedModels(modelsRes.value.supported_models ?? []);
    }

    // chat history index
    if (chatRes.status === 'fulfilled' && chatRes.value) {
      setChatConversations(chatRes.value.conversations ?? []);
    }

    // workflows
    if (workflowsRes.status === 'fulfilled' && workflowsRes.value) {
      setWorkflows((workflowsRes.value ?? []).map((w: any) => ({
        workflowId: w.workflowId || w.WorkflowId || '',
        name: w.name || w.Name || '',
        directoryLabel: w.directoryLabel || w.DirectoryLabel || '',
        stepCount: w.stepCount ?? w.StepCount ?? 0,
        updatedAtUtc: w.updatedAtUtc || w.UpdatedAtUtc || '',
        description: w.description || w.Description || '',
      })));
    }

    setLoading(false);
  }, [scopeId]);

  useEffect(() => { loadAll(); }, [loadAll]);

  // ── Load conversation messages when a chat-history file is selected ──
  useEffect(() => {
    if (!selectedFile.startsWith('chat-history:') || !scopeId) {
      setSelectedConversationMessages([]);
      return;
    }
    const convId = selectedFile.replace('chat-history:', '');
    setChatLoading(true);
    api.chatHistory.getConversation(scopeId, convId)
      .then(msgs => setSelectedConversationMessages((msgs ?? []) as StoredChatMessage[]))
      .catch(() => setSelectedConversationMessages([]))
      .finally(() => setChatLoading(false));
  }, [selectedFile, scopeId]);

  // ── Load workflow YAML when a workflow file is selected ──
  useEffect(() => {
    if (!selectedFile.startsWith('workflow:')) {
      setSelectedWorkflowYaml(null);
      return;
    }
    const wfId = selectedFile.replace('workflow:', '');
    setWorkflowLoading(true);
    api.workspace.getWorkflow(wfId)
      .then((res: any) => setSelectedWorkflowYaml(res?.yaml ?? res?.Yaml ?? ''))
      .catch(() => setSelectedWorkflowYaml(null))
      .finally(() => setWorkflowLoading(false));
  }, [selectedFile]);

  // ── Save functions ──
  async function saveConfig() {
    setConfigSaving(true);
    try {
      const parsed = JSON.parse(configJson);
      await api.userConfig.save(parsed);
      configSnap.current = configJson;
      return true;
    } finally {
      setConfigSaving(false);
    }
  }

  async function saveRoles() {
    setRolesSaving(true);
    try {
      await api.roles.saveCatalog({ roles: roles.map(toRolePayload) });
      rolesSnap.current = JSON.stringify(roles);
      return true;
    } finally {
      setRolesSaving(false);
    }
  }

  async function saveConnectors() {
    setConnectorsSaving(true);
    try {
      await api.connectors.saveCatalog({ connectors: connectors.map(toConnectorPayload) });
      connectorsSnap.current = JSON.stringify(connectors);
      return true;
    } finally {
      setConnectorsSaving(false);
    }
  }

  async function saveFile(file: ConfigFile) {
    if (file === 'config.json') return saveConfig();
    if (file === 'roles.json') return saveRoles();
    if (file === 'connectors.json') return saveConnectors();
    return false;
  }

  async function saveAll() {
    const tasks: Promise<boolean>[] = [];
    if (configDirty) tasks.push(saveConfig());
    if (rolesDirty) tasks.push(saveRoles());
    if (connectorsDirty) tasks.push(saveConnectors());
    await Promise.all(tasks);
  }

  // ── Role mutations ──
  function addRole() {
    setRoles(prev => [...prev, createRoleState(prev.length + 1)]);
  }

  function updateRole(key: string, patch: Partial<RoleState>) {
    setRoles(prev => prev.map(r => r.key === key ? { ...r, ...patch } : r));
  }

  function removeRole(key: string) {
    setRoles(prev => prev.filter(r => r.key !== key));
  }

  // ── Connector mutations ──
  function addConnector(type: ConnectorState['type'] = 'http') {
    setConnectors(prev => {
      const name = createUniqueConnectorName(prev, type);
      return [...prev, createEmptyConnector(type, name)];
    });
  }

  function updateConnector(key: string, patch: Partial<ConnectorState>) {
    setConnectors(prev => prev.map(c => c.key === key ? { ...c, ...patch } : c));
  }

  function removeConnector(key: string) {
    setConnectors(prev => prev.filter(c => c.key !== key));
  }

  // ── Actor mutations (immediate API, read + delete only) ──
  async function removeActor(gagentType: string, actorId: string) {
    if (!scopeId) return;
    await api.gagent.removeActor(scopeId, gagentType, actorId);
    const data = await api.gagent.listActors(scopeId);
    setActorGroups(data ?? []);
  }

  // ── Chat history mutations ──
  async function deleteChatConversation(convId: string) {
    if (!scopeId) return;
    await api.chatHistory.deleteConversation(scopeId, convId);
    setChatConversations(prev => prev.filter(c => c.id !== convId));
    if (selectedFile === `chat-history:${convId}`) {
      setSelectedFile('config.json');
    }
  }

  return {
    loading,
    selectedFile,
    setSelectedFile,
    scopeId,

    // config (raw JSON)
    configJson,
    setConfigJson,
    configDirty,
    configSaving,
    saveConfig,

    // roles
    roles,
    rolesDirty,
    rolesSaving,
    addRole,
    updateRole,
    removeRole,
    saveRoles,

    // connectors
    connectors,
    connectorsDirty,
    connectorsSaving,
    addConnector,
    updateConnector,
    removeConnector,
    saveConnectors,

    // actors
    actorGroups,
    actorTypes,
    removeActor,

    // models
    providers,
    supportedModels,
    modelsLoading,

    // workflows
    workflows,
    selectedWorkflowYaml,
    workflowLoading,

    // chat history
    chatConversations,
    selectedConversationMessages,
    chatLoading,
    deleteChatConversation,

    // global
    anyDirty,
    isDirty,
    saveFile,
    saveAll,
    loadAll,
  };
}
