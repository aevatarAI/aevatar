import type {
  ProColumns,
  ProDescriptionsItemProps,
  ProFormInstance,
} from '@ant-design/pro-components';
import {
  PageContainer,
  ProCard,
  ProDescriptions,
  ProForm,
  ProFormCheckbox,
  ProFormDigit,
  ProFormSelect,
  ProFormText,
  ProTable,
} from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Col,
  Drawer,
  Empty,
  Row,
  Space,
  Statistic,
  Tag,
  Typography,
} from 'antd';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { consoleApi } from '@/shared/api/consoleApi';
import type {
  WorkflowActorGraphEdge,
  WorkflowActorGraphNode,
  WorkflowActorGraphSubgraph,
  WorkflowActorSnapshot,
} from '@/shared/api/models';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { buildActorGraphElements } from '@/shared/graphs/buildGraphElements';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import {
  type ActorGraphDirection,
  loadConsolePreferences,
} from '@/shared/preferences/consolePreferences';
import {
  cardStackStyle,
  compactTableCardProps,
  embeddedPanelStyle,
  fillCardStyle,
  moduleCardProps,
  stretchColumnStyle,
} from '@/shared/ui/proComponents';
import {
  type ActorTimelineFilters,
  type ActorTimelineRow,
  buildTimelineRows,
  deriveSubgraphFromEdges,
  filterTimelineRows,
} from './actorPresentation';

type ActorGraphViewMode = 'enriched' | 'subgraph' | 'edges';

type ActorPageState = {
  actorId: string;
  timelineTake: number;
  graphDepth: number;
  graphTake: number;
  graphDirection: ActorGraphDirection;
  edgeTypes: string[];
};

type ActorSnapshotRecord = WorkflowActorSnapshot & {
  executionStatus: 'success' | 'error' | 'default';
  completionRate: number;
};

type ActorGraphSummaryRecord = {
  mode: ActorGraphViewMode;
  direction: ActorGraphDirection;
  depth: number;
  take: number;
  edgeTypes: string;
  rootNodeId: string;
  nodeCount: number;
  edgeCount: number;
};

type ActorNodeDetailRecord = WorkflowActorGraphNode & {
  propertyCount: number;
  primaryLabel: string;
  isRoot: boolean;
};

type ActorEdgeDetailRecord = WorkflowActorGraphEdge & {
  propertyCount: number;
};

type GraphControlValues = {
  graphViewMode: ActorGraphViewMode;
};

const defaultTimelineFilters: ActorTimelineFilters = {
  stages: [],
  eventTypes: [],
  stepTypes: [],
  query: '',
  errorsOnly: false,
};

const graphViewOptions: Array<{ label: string; value: ActorGraphViewMode }> = [
  { label: 'Enriched', value: 'enriched' },
  { label: 'Subgraph', value: 'subgraph' },
  { label: 'Edges only', value: 'edges' },
];

const executionValueEnum = {
  success: { text: 'Healthy', status: 'Success' },
  error: { text: 'Error', status: 'Error' },
  default: { text: 'Unknown', status: 'Default' },
} as const;

const timelineStatusValueEnum = {
  processing: { text: 'Processing', status: 'Processing' },
  success: { text: 'Completed', status: 'Success' },
  error: { text: 'Error', status: 'Error' },
  default: { text: 'Observed', status: 'Default' },
} as const;

const graphViewLabels: Record<ActorGraphViewMode, string> = {
  enriched: 'Snapshot + subgraph',
  subgraph: 'Subgraph',
  edges: 'Edges only',
};

function renderPropertyList(properties: Record<string, string>) {
  const entries = Object.entries(properties);
  if (entries.length === 0) {
    return 'n/a';
  }

  return (
    <Space direction="vertical" size={4} style={{ width: '100%' }}>
      {entries.map(([key, value]) => (
        <Typography.Text key={key}>
          <Typography.Text type="secondary">{key}</Typography.Text>:{' '}
          {value || 'n/a'}
        </Typography.Text>
      ))}
    </Space>
  );
}

const snapshotColumns: ProDescriptionsItemProps<ActorSnapshotRecord>[] = [
  {
    title: 'ActorId',
    dataIndex: 'actorId',
    render: (_, record) => (
      <Typography.Text copyable>{record.actorId}</Typography.Text>
    ),
  },
  {
    title: 'Workflow',
    dataIndex: 'workflowName',
  },
  {
    title: 'Execution',
    dataIndex: 'executionStatus',
    valueType: 'status' as any,
    valueEnum: executionValueEnum,
  },
  {
    title: 'Completion',
    dataIndex: 'completionRate',
    valueType: 'percent',
  },
  {
    title: 'State version',
    dataIndex: 'stateVersion',
    valueType: 'digit',
  },
  {
    title: 'Role replies',
    dataIndex: 'roleReplyCount',
    valueType: 'digit',
  },
  {
    title: 'Last command',
    dataIndex: 'lastCommandId',
    render: (_, record) =>
      record.lastCommandId ? (
        <Typography.Text copyable>{record.lastCommandId}</Typography.Text>
      ) : (
        'n/a'
      ),
  },
  {
    title: 'Last updated',
    dataIndex: 'lastUpdatedAt',
    valueType: 'dateTime',
    render: (_, record) => formatDateTime(record.lastUpdatedAt),
  },
  {
    title: 'Last output',
    dataIndex: 'lastOutput',
    render: (_, record) => record.lastOutput || 'n/a',
  },
  {
    title: 'Last error',
    dataIndex: 'lastError',
    render: (_, record) => record.lastError || 'n/a',
  },
];

const timelineColumns: ProColumns<ActorTimelineRow>[] = [
  {
    title: 'Timestamp',
    dataIndex: 'timestamp',
    valueType: 'dateTime',
    width: 220,
    render: (_, record) => formatDateTime(record.timestamp),
  },
  {
    title: 'Status',
    dataIndex: 'timelineStatus',
    valueType: 'status' as any,
    valueEnum: timelineStatusValueEnum,
    width: 120,
  },
  {
    title: 'Stage',
    dataIndex: 'stage',
    width: 180,
  },
  {
    title: 'Event type',
    dataIndex: 'eventType',
    width: 220,
    render: (_, record) => record.eventType || 'n/a',
  },
  {
    title: 'Message',
    dataIndex: 'message',
    ellipsis: true,
  },
  {
    title: 'Step',
    dataIndex: 'stepId',
    width: 180,
    render: (_, record) => record.stepId || 'n/a',
  },
  {
    title: 'Step type',
    dataIndex: 'stepType',
    width: 160,
    render: (_, record) => record.stepType || 'n/a',
  },
  {
    title: 'Actor',
    dataIndex: 'agentId',
    width: 200,
    render: (_, record) => record.agentId || 'n/a',
  },
  {
    title: 'Data',
    dataIndex: 'dataSummary',
    ellipsis: true,
    render: (_, record) =>
      record.dataCount > 0
        ? `${record.dataCount} field${record.dataCount === 1 ? '' : 's'} · ${record.dataSummary}`
        : 'n/a',
  },
];

const graphSummaryColumns: ProDescriptionsItemProps<ActorGraphSummaryRecord>[] =
  [
    {
      title: 'View',
      dataIndex: 'mode',
      render: (_, record) => graphViewLabels[record.mode],
    },
    {
      title: 'Direction',
      dataIndex: 'direction',
    },
    {
      title: 'Depth',
      dataIndex: 'depth',
      valueType: 'digit',
    },
    {
      title: 'Take',
      dataIndex: 'take',
      valueType: 'digit',
    },
    {
      title: 'Edge types',
      dataIndex: 'edgeTypes',
    },
    {
      title: 'Root node',
      dataIndex: 'rootNodeId',
      render: (_, record) => (
        <Typography.Text copyable>{record.rootNodeId}</Typography.Text>
      ),
    },
    {
      title: 'Nodes',
      dataIndex: 'nodeCount',
      valueType: 'digit',
    },
    {
      title: 'Edges',
      dataIndex: 'edgeCount',
      valueType: 'digit',
    },
  ];

const nodeDetailColumns: ProDescriptionsItemProps<ActorNodeDetailRecord>[] = [
  {
    title: 'NodeId',
    dataIndex: 'nodeId',
    render: (_, record) => (
      <Typography.Text copyable>{record.nodeId}</Typography.Text>
    ),
  },
  {
    title: 'Primary label',
    dataIndex: 'primaryLabel',
  },
  {
    title: 'Node type',
    dataIndex: 'nodeType',
  },
  {
    title: 'Role',
    dataIndex: ['properties', 'role'],
    render: (_, record) => record.properties.role || 'n/a',
  },
  {
    title: 'Updated at',
    dataIndex: 'updatedAt',
    render: (_, record) => formatDateTime(record.updatedAt),
  },
  {
    title: 'Root node',
    dataIndex: 'isRoot',
    render: (_, record) => (record.isRoot ? 'Yes' : 'No'),
  },
  {
    title: 'Property count',
    dataIndex: 'propertyCount',
    valueType: 'digit',
  },
  {
    title: 'Properties',
    dataIndex: 'properties',
    span: 2,
    render: (_, record) => renderPropertyList(record.properties),
  },
];

const edgeDetailColumns: ProDescriptionsItemProps<ActorEdgeDetailRecord>[] = [
  {
    title: 'EdgeId',
    dataIndex: 'edgeId',
    render: (_, record) => (
      <Typography.Text copyable>{record.edgeId}</Typography.Text>
    ),
  },
  {
    title: 'Type',
    dataIndex: 'edgeType',
  },
  {
    title: 'From',
    dataIndex: 'fromNodeId',
    render: (_, record) => (
      <Typography.Text copyable>{record.fromNodeId}</Typography.Text>
    ),
  },
  {
    title: 'To',
    dataIndex: 'toNodeId',
    render: (_, record) => (
      <Typography.Text copyable>{record.toNodeId}</Typography.Text>
    ),
  },
  {
    title: 'Updated at',
    dataIndex: 'updatedAt',
    render: (_, record) => formatDateTime(record.updatedAt),
  },
  {
    title: 'Property count',
    dataIndex: 'propertyCount',
    valueType: 'digit',
  },
  {
    title: 'Properties',
    dataIndex: 'properties',
    span: 2,
    render: (_, record) => renderPropertyList(record.properties),
  },
];

function parsePositiveInt(value: string | null, fallback: number): number {
  if (!value) {
    return fallback;
  }

  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed <= 0) {
    return fallback;
  }

  return Math.floor(parsed);
}

function parseDirection(
  value: string | null,
  fallback: ActorGraphDirection,
): ActorGraphDirection {
  if (value === 'Both' || value === 'Outbound' || value === 'Inbound') {
    return value;
  }

  return fallback;
}

function parseGraphViewMode(value: string | null): ActorGraphViewMode {
  if (value === 'subgraph' || value === 'edges' || value === 'enriched') {
    return value;
  }

  return 'enriched';
}

function readStateFromUrl(): ActorPageState {
  const preferences = loadConsolePreferences();
  if (typeof window === 'undefined') {
    return {
      actorId: '',
      timelineTake: preferences.actorTimelineTake,
      graphDepth: preferences.actorGraphDepth,
      graphTake: preferences.actorGraphTake,
      graphDirection: preferences.actorGraphDirection,
      edgeTypes: [],
    };
  }

  const params = new URLSearchParams(window.location.search);
  return {
    actorId: params.get('actorId') ?? '',
    timelineTake: parsePositiveInt(
      params.get('timelineTake'),
      preferences.actorTimelineTake,
    ),
    graphDepth: parsePositiveInt(
      params.get('graphDepth'),
      preferences.actorGraphDepth,
    ),
    graphTake: parsePositiveInt(
      params.get('graphTake'),
      preferences.actorGraphTake,
    ),
    graphDirection: parseDirection(
      params.get('graphDirection'),
      preferences.actorGraphDirection,
    ),
    edgeTypes: params
      .getAll('edgeTypes')
      .map((value) => value.trim())
      .filter(Boolean),
  };
}

function readGraphViewModeFromUrl(): ActorGraphViewMode {
  if (typeof window === 'undefined') {
    return 'enriched';
  }

  return parseGraphViewMode(
    new URLSearchParams(window.location.search).get('graphView'),
  );
}

const ActorsPage: React.FC = () => {
  const initialState = useMemo(() => readStateFromUrl(), []);
  const initialGraphViewMode = useMemo(() => readGraphViewModeFromUrl(), []);
  const formRef = useRef<ProFormInstance<ActorPageState> | undefined>(
    undefined,
  );
  const timelineFormRef = useRef<
    ProFormInstance<ActorTimelineFilters> | undefined
  >(undefined);
  const graphControlFormRef = useRef<
    ProFormInstance<GraphControlValues> | undefined
  >(undefined);
  const [filters, setFilters] = useState<ActorPageState>(initialState);
  const [graphViewMode, setGraphViewMode] =
    useState<ActorGraphViewMode>(initialGraphViewMode);
  const [timelineFilters, setTimelineFilters] = useState<ActorTimelineFilters>(
    defaultTimelineFilters,
  );
  const [selectedNodeId, setSelectedNodeId] = useState<string>('');
  const [selectedEdgeId, setSelectedEdgeId] = useState<string>('');
  const [selectedTimelineKey, setSelectedTimelineKey] = useState<string>('');

  const snapshotQuery = useQuery({
    queryKey: ['actor-snapshot', filters.actorId],
    enabled: Boolean(filters.actorId),
    queryFn: () => consoleApi.getActorSnapshot(filters.actorId),
  });

  const timelineQuery = useQuery({
    queryKey: ['actor-timeline', filters.actorId, filters.timelineTake],
    enabled: Boolean(filters.actorId),
    queryFn: () =>
      consoleApi.getActorTimeline(filters.actorId, {
        take: filters.timelineTake,
      }),
  });

  const graphSubgraphQuery = useQuery({
    queryKey: [
      'actor-graph-subgraph',
      filters.actorId,
      filters.graphDepth,
      filters.graphTake,
      filters.graphDirection,
      [...filters.edgeTypes].sort().join(','),
    ],
    enabled: Boolean(filters.actorId) && graphViewMode !== 'edges',
    queryFn: () =>
      consoleApi.getActorGraphSubgraph(filters.actorId, {
        depth: filters.graphDepth,
        take: filters.graphTake,
        direction: filters.graphDirection,
        edgeTypes: filters.edgeTypes,
      }),
  });

  const graphEdgesQuery = useQuery({
    queryKey: [
      'actor-graph-edges',
      filters.actorId,
      filters.graphTake,
      filters.graphDirection,
      [...filters.edgeTypes].sort().join(','),
    ],
    enabled: Boolean(filters.actorId) && graphViewMode === 'edges',
    queryFn: () =>
      consoleApi.getActorGraphEdges(filters.actorId, {
        take: filters.graphTake,
        direction: filters.graphDirection,
        edgeTypes: filters.edgeTypes,
      }),
  });

  const snapshotRecord = useMemo<ActorSnapshotRecord | undefined>(() => {
    if (!snapshotQuery.data) {
      return undefined;
    }

    return {
      ...snapshotQuery.data,
      executionStatus:
        snapshotQuery.data.lastSuccess === null
          ? 'default'
          : snapshotQuery.data.lastSuccess
            ? 'success'
            : 'error',
      completionRate:
        snapshotQuery.data.totalSteps > 0
          ? snapshotQuery.data.completedSteps / snapshotQuery.data.totalSteps
          : 0,
    };
  }, [snapshotQuery.data]);

  const timelineRows = useMemo<ActorTimelineRow[]>(
    () => buildTimelineRows(timelineQuery.data ?? []),
    [timelineQuery.data],
  );

  const filteredTimelineRows = useMemo(
    () => filterTimelineRows(timelineRows, timelineFilters),
    [timelineRows, timelineFilters],
  );

  const selectedTimelineRecord = useMemo<ActorTimelineRow | undefined>(
    () => timelineRows.find((row) => row.key === selectedTimelineKey),
    [selectedTimelineKey, timelineRows],
  );

  const timelineStageOptions = useMemo(
    () =>
      Array.from(new Set(timelineRows.map((row) => row.stage).filter(Boolean)))
        .sort((left, right) => left.localeCompare(right))
        .map((value) => ({ label: value, value })),
    [timelineRows],
  );

  const timelineEventTypeOptions = useMemo(
    () =>
      Array.from(
        new Set(timelineRows.map((row) => row.eventType).filter(Boolean)),
      )
        .sort((left, right) => left.localeCompare(right))
        .map((value) => ({ label: value, value })),
    [timelineRows],
  );

  const timelineStepTypeOptions = useMemo(
    () =>
      Array.from(
        new Set(timelineRows.map((row) => row.stepType).filter(Boolean)),
      )
        .sort((left, right) => left.localeCompare(right))
        .map((value) => ({ label: value, value })),
    [timelineRows],
  );

  const currentGraph = useMemo<WorkflowActorGraphSubgraph | undefined>(() => {
    if (!filters.actorId) {
      return undefined;
    }

    if (graphViewMode === 'subgraph') {
      return graphSubgraphQuery.data;
    }

    if (graphViewMode === 'edges') {
      return graphEdgesQuery.data
        ? deriveSubgraphFromEdges(graphEdgesQuery.data, filters.actorId)
        : undefined;
    }

    return graphSubgraphQuery.data;
  }, [
    filters.actorId,
    graphEdgesQuery.data,
    graphSubgraphQuery.data,
    graphViewMode,
  ]);

  const currentGraphLoading =
    graphViewMode === 'edges'
      ? graphEdgesQuery.isLoading
      : graphSubgraphQuery.isLoading;

  const currentGraphError =
    graphViewMode === 'edges'
      ? graphEdgesQuery.error
      : graphSubgraphQuery.error;

  const graphElements = useMemo(() => {
    if (!currentGraph) {
      return { nodes: [], edges: [] };
    }

    return buildActorGraphElements(
      currentGraph.nodes,
      currentGraph.edges,
      currentGraph.rootNodeId || filters.actorId,
    );
  }, [currentGraph, filters.actorId]);

  const availableEdgeTypes = useMemo(
    () =>
      Array.from(
        new Set(
          [
            ...filters.edgeTypes,
            ...(graphSubgraphQuery.data?.edges ?? []).map(
              (edge) => edge.edgeType,
            ),
            ...(graphEdgesQuery.data ?? []).map((edge) => edge.edgeType),
          ]
            .map((value) => value.trim())
            .filter(Boolean),
        ),
      ).sort((left, right) => left.localeCompare(right)),
    [
      filters.edgeTypes,
      graphEdgesQuery.data,
      graphSubgraphQuery.data?.edges,
    ],
  );

  const graphSummary = useMemo<ActorGraphSummaryRecord | undefined>(() => {
    if (!currentGraph) {
      return undefined;
    }

    return {
      mode: graphViewMode,
      direction: filters.graphDirection,
      depth: filters.graphDepth,
      take: filters.graphTake,
      edgeTypes:
        filters.edgeTypes.length > 0 ? filters.edgeTypes.join(', ') : 'All',
      rootNodeId: currentGraph.rootNodeId || filters.actorId,
      nodeCount: currentGraph.nodes.length,
      edgeCount: currentGraph.edges.length,
    };
  }, [
    currentGraph,
    filters.actorId,
    filters.edgeTypes,
    filters.graphDepth,
    filters.graphDirection,
    filters.graphTake,
    graphViewMode,
  ]);

  const selectedNodeRecord = useMemo<ActorNodeDetailRecord | undefined>(() => {
    const node = currentGraph?.nodes.find(
      (item) => item.nodeId === selectedNodeId,
    );
    if (!node) {
      return undefined;
    }

    return {
      ...node,
      propertyCount: Object.keys(node.properties).length,
      primaryLabel:
        node.properties.stepId || node.properties.workflowName || node.nodeId,
      isRoot: node.nodeId === currentGraph?.rootNodeId,
    };
  }, [currentGraph, selectedNodeId]);

  const selectedEdgeRecord = useMemo<ActorEdgeDetailRecord | undefined>(() => {
    const edge = currentGraph?.edges.find(
      (item) => item.edgeId === selectedEdgeId,
    );
    if (!edge) {
      return undefined;
    }

    return {
      ...edge,
      propertyCount: Object.keys(edge.properties).length,
    };
  }, [currentGraph, selectedEdgeId]);

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    const url = new URL(window.location.href);
    if (filters.actorId) {
      url.searchParams.set('actorId', filters.actorId);
    } else {
      url.searchParams.delete('actorId');
    }
    url.searchParams.set('timelineTake', String(filters.timelineTake));
    url.searchParams.set('graphDepth', String(filters.graphDepth));
    url.searchParams.set('graphTake', String(filters.graphTake));
    url.searchParams.set('graphDirection', filters.graphDirection);
    url.searchParams.set('graphView', graphViewMode);
    url.searchParams.delete('edgeTypes');
    for (const edgeType of filters.edgeTypes) {
      url.searchParams.append('edgeTypes', edgeType);
    }
    window.history.replaceState(null, '', `${url.pathname}${url.search}`);
  }, [filters, graphViewMode]);

  useEffect(() => {
    timelineFormRef.current?.setFieldsValue(defaultTimelineFilters);
    setTimelineFilters(defaultTimelineFilters);
    setSelectedTimelineKey('');
  }, [filters.actorId]);

  useEffect(() => {
    if (!selectedTimelineKey) {
      return;
    }

    if (!timelineRows.some((row) => row.key === selectedTimelineKey)) {
      setSelectedTimelineKey('');
    }
  }, [selectedTimelineKey, timelineRows]);

  useEffect(() => {
    if (!currentGraph) {
      setSelectedNodeId('');
      setSelectedEdgeId('');
      return;
    }

    if (!currentGraph.nodes.some((node) => node.nodeId === selectedNodeId)) {
      setSelectedNodeId(
        currentGraph.rootNodeId || currentGraph.nodes[0]?.nodeId || '',
      );
    }

    if (!currentGraph.edges.some((edge) => edge.edgeId === selectedEdgeId)) {
      setSelectedEdgeId('');
    }
  }, [currentGraph, selectedEdgeId, selectedNodeId]);

  return (
    <PageContainer
      title="Actors"
      content="Inspect actor snapshots, filter execution history, and switch across enriched, subgraph, and edges-only topology views."
    >
      <ProCard title="Actor query" {...moduleCardProps}>
        <ProForm<ActorPageState>
          formRef={formRef}
          layout="vertical"
          initialValues={initialState}
          onFinish={async (values) => {
            setFilters({
              actorId: (values.actorId ?? '').trim(),
              timelineTake: values.timelineTake,
              graphDepth: values.graphDepth,
              graphTake: values.graphTake,
              graphDirection: values.graphDirection,
              edgeTypes: values.edgeTypes ?? [],
            });
            return true;
          }}
          submitter={{
            render: (props) => (
              <Space wrap>
                <Button type="primary" onClick={() => props.form?.submit?.()}>
                  Load actor
                </Button>
                <Button
                  onClick={() => {
                    formRef.current?.setFieldsValue(initialState);
                    timelineFormRef.current?.setFieldsValue(
                      defaultTimelineFilters,
                    );
                    graphControlFormRef.current?.setFieldsValue({
                      graphViewMode: initialGraphViewMode,
                    });
                    setFilters(initialState);
                    setGraphViewMode(initialGraphViewMode);
                    setTimelineFilters(defaultTimelineFilters);
                  }}
                >
                  Reset filters
                </Button>
                {filters.actorId ? (
                  <Tag color="processing">{filters.actorId}</Tag>
                ) : null}
              </Space>
            ),
          }}
        >
          <Row gutter={[16, 16]}>
            <Col xs={24} lg={10}>
              <ProFormText
                name="actorId"
                label="ActorId"
                placeholder="Workflow:19fe1b04"
              />
            </Col>
            <Col xs={24} md={8} lg={4}>
              <ProFormDigit
                name="timelineTake"
                label="Timeline take"
                min={10}
                max={500}
                fieldProps={{ precision: 0 }}
              />
            </Col>
            <Col xs={24} md={8} lg={4}>
              <ProFormDigit
                name="graphDepth"
                label="Graph depth"
                min={1}
                max={8}
                fieldProps={{ precision: 0 }}
              />
            </Col>
            <Col xs={24} md={8} lg={4}>
              <ProFormDigit
                name="graphTake"
                label="Graph take"
                min={10}
                max={500}
                fieldProps={{ precision: 0 }}
              />
            </Col>
            <Col xs={24} md={12} lg={6}>
              <ProFormSelect<ActorGraphDirection>
                name="graphDirection"
                label="Graph direction"
                options={[
                  { label: 'Both', value: 'Both' },
                  { label: 'Outbound', value: 'Outbound' },
                  { label: 'Inbound', value: 'Inbound' },
                ]}
              />
            </Col>
            <Col xs={24} md={12} lg={10}>
              <ProFormSelect<string[]>
                name="edgeTypes"
                label="Edge types"
                options={availableEdgeTypes.map((edgeType) => ({
                  label: edgeType,
                  value: edgeType,
                }))}
                fieldProps={{
                  mode: 'multiple',
                  allowClear: true,
                  placeholder: 'Filter graph edge types',
                }}
              />
            </Col>
          </Row>
        </ProForm>
      </ProCard>

      {!filters.actorId ? (
        <ProCard style={{ marginTop: 16 }} {...moduleCardProps}>
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="Provide an actorId to load actor data."
          />
        </ProCard>
      ) : null}

      {filters.actorId ? (
        <>
          {snapshotRecord ? (
            <Row gutter={[16, 16]} style={{ marginTop: 16 }}>
              <Col xs={24} md={8} style={stretchColumnStyle}>
                <ProCard {...moduleCardProps} style={fillCardStyle}>
                  <Statistic
                    title="Completed steps"
                    value={`${snapshotRecord.completedSteps}/${snapshotRecord.totalSteps}`}
                  />
                </ProCard>
              </Col>
              <Col xs={24} md={8} style={stretchColumnStyle}>
                <ProCard {...moduleCardProps} style={fillCardStyle}>
                  <Statistic
                    title="Role replies"
                    value={snapshotRecord.roleReplyCount}
                  />
                </ProCard>
              </Col>
              <Col xs={24} md={8} style={stretchColumnStyle}>
                <ProCard {...moduleCardProps} style={fillCardStyle}>
                  <Statistic
                    title="State version"
                    value={snapshotRecord.stateVersion}
                  />
                </ProCard>
              </Col>
            </Row>
          ) : null}

          <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
            <Col xs={24} xl={8} style={stretchColumnStyle}>
              <ProCard
                title="Snapshot"
                {...moduleCardProps}
                style={fillCardStyle}
                loading={snapshotQuery.isLoading}
              >
                {snapshotQuery.isError ? (
                  <Alert
                    showIcon
                    type="error"
                    message="Failed to load actor"
                    description={String(snapshotQuery.error)}
                  />
                ) : (
                  <ProDescriptions<ActorSnapshotRecord>
                    column={1}
                    dataSource={snapshotRecord}
                    columns={snapshotColumns}
                    emptyText={
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description="No actor snapshot available."
                      />
                    }
                  />
                )}
              </ProCard>
            </Col>

            <Col xs={24} xl={16} style={stretchColumnStyle}>
              <ProCard
                title="Timeline"
                {...moduleCardProps}
                style={fillCardStyle}
              >
                <div style={cardStackStyle}>
                  <ProForm<ActorTimelineFilters>
                    formRef={timelineFormRef}
                    layout="vertical"
                    initialValues={defaultTimelineFilters}
                    submitter={false}
                    onValuesChange={(_, values) => {
                      setTimelineFilters({
                        stages: values.stages ?? [],
                        eventTypes: values.eventTypes ?? [],
                        stepTypes: values.stepTypes ?? [],
                        query: values.query ?? '',
                        errorsOnly: Boolean(values.errorsOnly),
                      });
                    }}
                  >
                    <Row gutter={[16, 16]}>
                      <Col xs={24} md={12} xl={8}>
                        <ProFormText
                          name="query"
                          label="Search"
                          placeholder="Search message, event type, step or payload"
                        />
                      </Col>
                      <Col xs={24} md={12} xl={5}>
                        <ProFormSelect<string[]>
                          name="stages"
                          label="Stages"
                          options={timelineStageOptions}
                          fieldProps={{
                            mode: 'multiple',
                            allowClear: true,
                            placeholder: 'All stages',
                          }}
                        />
                      </Col>
                      <Col xs={24} md={12} xl={5}>
                        <ProFormSelect<string[]>
                          name="eventTypes"
                          label="Event types"
                          options={timelineEventTypeOptions}
                          fieldProps={{
                            mode: 'multiple',
                            allowClear: true,
                            placeholder: 'All event types',
                          }}
                        />
                      </Col>
                      <Col xs={24} md={12} xl={4}>
                        <ProFormSelect<string[]>
                          name="stepTypes"
                          label="Step types"
                          options={timelineStepTypeOptions}
                          fieldProps={{
                            mode: 'multiple',
                            allowClear: true,
                            placeholder: 'All step types',
                          }}
                        />
                      </Col>
                      <Col xs={24} md={12} xl={2}>
                        <ProFormCheckbox
                          name="errorsOnly"
                          label=" "
                          tooltip="Only show error rows"
                        >
                          Errors only
                        </ProFormCheckbox>
                      </Col>
                    </Row>
                  </ProForm>

                  {timelineQuery.isError ? (
                    <Alert
                      showIcon
                      type="error"
                      message="Failed to load timeline"
                      description={String(timelineQuery.error)}
                    />
                  ) : null}

                  <ProTable<ActorTimelineRow>
                    rowKey="key"
                    search={false}
                    options={false}
                    columns={timelineColumns}
                    dataSource={filteredTimelineRows}
                    loading={timelineQuery.isLoading}
                    pagination={{ pageSize: 8, showSizeChanger: false }}
                    cardProps={compactTableCardProps}
                    scroll={{ x: 1460, y: 460 }}
                    onRow={(record) => ({
                      onClick: () => setSelectedTimelineKey(record.key),
                    })}
                    rowClassName={(record) =>
                      record.key === selectedTimelineKey ? 'ant-table-row-selected' : ''
                    }
                    locale={{
                      emptyText: (
                        <Empty
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                          description="No timeline rows match the current filters."
                        />
                      ),
                    }}
                  />
                  <Drawer
                    title="Timeline detail"
                    width={560}
                    open={Boolean(selectedTimelineRecord)}
                    onClose={() => setSelectedTimelineKey('')}
                    destroyOnClose
                  >
                    {selectedTimelineRecord ? (
                      <Space direction="vertical" size={16} style={{ width: '100%' }}>
                        <ProDescriptions<ActorTimelineRow>
                          column={1}
                          dataSource={selectedTimelineRecord}
                          columns={[
                            {
                              title: 'Timestamp',
                              dataIndex: 'timestamp',
                              render: (_, record) => formatDateTime(record.timestamp),
                            },
                            {
                              title: 'Stage',
                              dataIndex: 'stage',
                              render: (_, record) => record.stage || 'n/a',
                            },
                            {
                              title: 'Event type',
                              dataIndex: 'eventType',
                              render: (_, record) => record.eventType || 'n/a',
                            },
                            {
                              title: 'Message',
                              dataIndex: 'message',
                              render: (_, record) => record.message || 'n/a',
                            },
                            {
                              title: 'Step',
                              dataIndex: 'stepId',
                              render: (_, record) => record.stepId || 'n/a',
                            },
                            {
                              title: 'Step type',
                              dataIndex: 'stepType',
                              render: (_, record) => record.stepType || 'n/a',
                            },
                            {
                              title: 'Actor',
                              dataIndex: 'agentId',
                              render: (_, record) => record.agentId || 'n/a',
                            },
                          ]}
                        />

                        <div>
                          <Typography.Text strong>Structured data</Typography.Text>
                          <div style={{ marginTop: 12 }}>
                            {selectedTimelineRecord.dataCount > 0 ? (
                              <Space
                                direction="vertical"
                                size={8}
                                style={{ width: '100%' }}
                              >
                                {Object.entries(selectedTimelineRecord.data).map(
                                  ([key, value]) => (
                                    <Typography.Text key={key}>
                                      <Typography.Text type="secondary">
                                        {key}
                                      </Typography.Text>
                                      : {value || 'n/a'}
                                    </Typography.Text>
                                  ),
                                )}
                              </Space>
                            ) : (
                              <Typography.Text type="secondary">
                                No structured data was attached to this timeline entry.
                              </Typography.Text>
                            )}
                          </div>
                        </div>

                        {selectedTimelineRecord.dataCount > 0 ? (
                          <div>
                            <Typography.Text strong>Raw JSON</Typography.Text>
                            <pre
                              style={{
                                marginTop: 12,
                                whiteSpace: 'pre-wrap',
                                wordBreak: 'break-word',
                              }}
                            >
                              {JSON.stringify(selectedTimelineRecord.data, null, 2)}
                            </pre>
                          </div>
                        ) : null}
                      </Space>
                    ) : null}
                  </Drawer>
                </div>
              </ProCard>
            </Col>
          </Row>

          <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
            <Col xs={24} lg={8} style={stretchColumnStyle}>
              <ProCard
                title="Graph controls"
                {...moduleCardProps}
                style={fillCardStyle}
              >
                <div style={cardStackStyle}>
                  <ProForm<GraphControlValues>
                    formRef={graphControlFormRef}
                    layout="vertical"
                    initialValues={{ graphViewMode }}
                    submitter={false}
                    onValuesChange={(_, values) => {
                      if (values.graphViewMode) {
                        setGraphViewMode(values.graphViewMode);
                      }
                    }}
                  >
                    <ProFormSelect<ActorGraphViewMode>
                      name="graphViewMode"
                      label="Graph view"
                      options={graphViewOptions}
                    />
                  </ProForm>

                  {graphSummary ? (
                    <ProDescriptions<ActorGraphSummaryRecord>
                      column={1}
                      dataSource={graphSummary}
                      columns={graphSummaryColumns}
                    />
                  ) : (
                    <Empty
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                      description="No graph summary available yet."
                    />
                  )}
                </div>
              </ProCard>
            </Col>

            <Col xs={24} lg={16} style={stretchColumnStyle}>
              <ProCard
                title="Selection details"
                {...moduleCardProps}
                style={fillCardStyle}
                loading={currentGraphLoading}
              >
                {currentGraphError ? (
                  <Alert
                    showIcon
                    type="error"
                    message="Failed to load graph view"
                    description={String(currentGraphError)}
                  />
                ) : selectedNodeRecord || selectedEdgeRecord ? (
                  <div style={cardStackStyle}>
                    {selectedNodeRecord ? (
                      <div style={embeddedPanelStyle}>
                        <Space wrap style={{ marginBottom: 12 }}>
                          <Tag color="processing">Node</Tag>
                          <Typography.Text strong>
                            {selectedNodeRecord.primaryLabel}
                          </Typography.Text>
                        </Space>
                        <ProDescriptions<ActorNodeDetailRecord>
                          column={2}
                          dataSource={selectedNodeRecord}
                          columns={nodeDetailColumns}
                        />
                      </div>
                    ) : null}

                    {selectedEdgeRecord ? (
                      <div style={embeddedPanelStyle}>
                        <Space wrap style={{ marginBottom: 12 }}>
                          <Tag color="purple">Edge</Tag>
                          <Typography.Text strong>
                            {selectedEdgeRecord.edgeType}
                          </Typography.Text>
                        </Space>
                        <ProDescriptions<ActorEdgeDetailRecord>
                          column={2}
                          dataSource={selectedEdgeRecord}
                          columns={edgeDetailColumns}
                        />
                      </div>
                    ) : null}
                  </div>
                ) : (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="Select a node or edge from the graph to inspect its details."
                  />
                )}
              </ProCard>
            </Col>
          </Row>

          <ProCard
            title="Graph explorer"
            style={{ marginTop: 16 }}
            {...moduleCardProps}
            loading={currentGraphLoading}
          >
            {currentGraphError ? (
              <Alert
                showIcon
                type="error"
                message="Failed to load graph topology"
                description={String(currentGraphError)}
              />
            ) : currentGraph && currentGraph.nodes.length > 0 ? (
              <GraphCanvas
                nodes={graphElements.nodes}
                edges={graphElements.edges}
                selectedNodeId={selectedNodeId}
                selectedEdgeId={selectedEdgeId}
                onNodeSelect={(nodeId) => setSelectedNodeId(nodeId)}
                onEdgeSelect={(edgeId) => setSelectedEdgeId(edgeId)}
                height={560}
              />
            ) : (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="No graph topology returned for this actor."
              />
            )}
          </ProCard>
        </>
      ) : null}
    </PageContainer>
  );
};

export default ActorsPage;
