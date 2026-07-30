import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

type AdminObservatoryApi = {
  mapObsDetail: (
    detail: unknown,
    graph: unknown,
  ) => {
    id: string;
    events: Array<{ stepId: string; kind: string; text: string }>;
    graph: {
      rootNodeId: string;
      nodes: Array<{
        nodeId: string;
        st: string;
        incoming: number;
        outgoing: number;
      }>;
      edges: Array<{ fromNodeId: string; toNodeId: string; edgeType: string }>;
    };
  };
  obsBindGraph: ((root: HTMLElement) => void) | null;
  obsGraphView:
    | ((detail: ReturnType<AdminObservatoryApi['mapObsDetail']>) => string)
    | null;
  obsOpenGraphNode: ((nodeId: string) => void) | null;
  bindResize: ((root: HTMLElement) => void) | null;
  setFixture: (detail: ReturnType<AdminObservatoryApi['mapObsDetail']>) => void;
};

function loadAdminObservatoryApi(): AdminObservatoryApi {
  document.body.innerHTML =
    '<div id="drawer-root"></div><div id="toast-root"></div>';
  const html = fs.readFileSync(
    path.resolve(
      __dirname,
      '../../../src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html',
    ),
    'utf8',
  );
  const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(
    (match) => match[1],
  );
  const source = scripts
    .join('\n')
    .replace(
      /__BACKEND_CONSOLE_CONFIG__/g,
      JSON.stringify({
        authority: '',
        clientId: '',
        scope: '',
        resources: [],
        nyxidApi: '',
        storageKey: 'admin-observatory-test',
      }),
    )
    .replace(/\(function boot\(\)\{[\s\S]*?\}\)\(\);/, '')
    .concat(`
      window.__adminObservatoryApi = {
        mapObsDetail: mapObsDetail,
        obsBindGraph: typeof obsBindGraph === 'function' ? obsBindGraph : null,
        obsGraphView: typeof obsGraphView === 'function' ? obsGraphView : null,
        obsOpenGraphNode: typeof obsOpenGraphNode === 'function' ? obsOpenGraphNode : null,
        bindResize: function(root) {
          window.addEventListener('resize', function() {
            obsHandleGraphResize(root, function() {
              root.innerHTML = obsGraphView(OBS_DETAIL[OBS_STATE.selectedId].detail);
              obsBindGraph(root);
            });
          });
        },
        setFixture: function(detail) {
          OBS_STATE.selectedId = detail.id;
          OBS_RUNS = [detail];
          OBS_DETAIL[detail.id] = { loading:false, err:null, detail:detail };
        }
      };
    `);

  const load = new Function(
    'window',
    'document',
    'localStorage',
    'sessionStorage',
    `${source}; return window.__adminObservatoryApi;`,
  ) as (
    window: Window,
    document: Document,
    localStorage: Storage,
    sessionStorage: Storage,
  ) => AdminObservatoryApi;
  return load(window, document, localStorage, sessionStorage);
}

describe('admin observatory graph', () => {
  beforeEach(() => {
    jest.useFakeTimers();
    Object.defineProperty(window, 'innerWidth', {
      configurable: true,
      value: 1024,
    });
  });

  afterEach(() => {
    jest.clearAllTimers();
    jest.useRealTimers();
    delete (
      window as typeof window & { __adminObservatoryApi?: AdminObservatoryApi }
    ).__adminObservatoryApi;
    localStorage.clear();
    sessionStorage.clear();
  });

  it('preserves real edges and derives topology status from the authoritative run', () => {
    const api = loadAdminObservatoryApi();
    const detail = {
      summary: {
        runId: 'run-alpha',
        workflowName: 'branched-workflow',
        status: 'completed',
      },
      steps: [
        {
          stepId: 'answer',
          stepType: 'llm_call',
          success: true,
          requestedAtUtc: '2026-07-29T08:00:00Z',
          completedAtUtc: '2026-07-29T08:00:01Z',
        },
      ],
      timeline: [],
    };
    const graph = {
      rootNodeId: 'actor-root',
      nodes: [
        { nodeId: 'actor-root', nodeType: 'Actor' },
        { nodeId: 'run-alpha', nodeType: 'WorkflowRun' },
        { nodeId: 'step-answer', nodeType: 'WorkflowStep', stepId: 'answer' },
        { nodeId: 'actor-child', nodeType: 'Actor' },
      ],
      edges: [
        { fromNodeId: 'actor-root', toNodeId: 'run-alpha', edgeType: 'OWNS' },
        {
          fromNodeId: 'run-alpha',
          toNodeId: 'step-answer',
          edgeType: 'CONTAINS_STEP',
        },
        {
          fromNodeId: 'step-answer',
          toNodeId: 'actor-child',
          edgeType: 'CHILD_OF',
        },
      ],
    };

    const mapped = api.mapObsDetail(detail, graph).graph;

    expect(mapped.rootNodeId).toBe('actor-root');
    expect(mapped.edges).toEqual(graph.edges);
    expect(mapped.nodes).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          nodeId: 'actor-root',
          st: 'success',
          incoming: 0,
          outgoing: 1,
        }),
        expect.objectContaining({
          nodeId: 'step-answer',
          st: 'success',
          incoming: 1,
          outgoing: 1,
        }),
      ]),
    );
  });

  it('lays out branches and supports zoom, pan, and node inspection', () => {
    const api = loadAdminObservatoryApi();
    const graphView = api.obsGraphView;
    const bindGraph = api.obsBindGraph;
    const bindResize = api.bindResize;
    expect(graphView).toEqual(expect.any(Function));
    expect(bindGraph).toEqual(expect.any(Function));
    expect(api.obsOpenGraphNode).toEqual(expect.any(Function));
    expect(bindResize).toEqual(expect.any(Function));
    assert(graphView);
    assert(bindGraph);
    assert(bindResize);

    const detail = api.mapObsDetail(
      {
        summary: {
          runId: 'run-branch',
          workflowName: 'branch-workflow',
          status: 'running',
        },
        steps: [
          {
            stepId: 'plan',
            stepType: 'conditional',
            success: true,
            requestedAtUtc: '2026-07-29T08:00:00Z',
            completedAtUtc: '2026-07-29T08:00:01Z',
          },
          {
            stepId: 'success',
            stepType: 'llm_call',
            requestedAtUtc: '2026-07-29T08:00:02Z',
          },
        ],
        timeline: [
          {
            kind: 'StepStarted',
            stage: 'step.started',
            stepId: 'plan',
            timestampUtc: '2026-07-29T08:00:00Z',
            message: 'Evaluating branch',
          },
        ],
      },
      {
        rootNodeId: 'run-branch',
        nodes: [
          { nodeId: 'run-branch', nodeType: 'WorkflowRun' },
          { nodeId: 'step-plan', nodeType: 'WorkflowStep', stepId: 'plan' },
          {
            nodeId: 'step-success',
            nodeType: 'WorkflowStep',
            stepId: 'success',
          },
          { nodeId: 'step-error', nodeType: 'WorkflowStep', stepId: 'error' },
        ],
        edges: [
          {
            edgeId: 'contains-plan',
            fromNodeId: 'run-branch',
            toNodeId: 'step-plan',
            edgeType: 'CONTAINS_STEP',
          },
          {
            edgeId: 'next-success',
            fromNodeId: 'step-plan',
            toNodeId: 'step-success',
            edgeType: 'NEXT',
            branchKey: 'success',
          },
          {
            edgeId: 'next-error',
            fromNodeId: 'step-plan',
            toNodeId: 'step-error',
            edgeType: 'NEXT',
            branchKey: 'error',
          },
        ],
      },
    );
    api.setFixture(detail);

    const root = document.createElement('div');
    root.innerHTML = graphView(detail);
    document.body.appendChild(root);
    const host = root.querySelector<HTMLElement>('.obs-graph');
    const viewport = root.querySelector<HTMLElement>('.obs-graph-viewport');
    expect(host).not.toBeNull();
    expect(viewport).not.toBeNull();
    assert(host);
    assert(viewport);
    Object.defineProperties(host, {
      clientWidth: { configurable: true, value: 900 },
      clientHeight: { configurable: true, value: 520 },
    });

    bindGraph(root);
    jest.runOnlyPendingTimers();

    const successNode = root.querySelector<HTMLElement>(
      '[data-obs-node="step-success"]',
    );
    const errorNode = root.querySelector<HTMLElement>(
      '[data-obs-node="step-error"]',
    );
    expect(successNode?.style.left).toBe(errorNode?.style.left);
    expect(successNode?.style.top).not.toBe(errorNode?.style.top);
    expect(root.querySelectorAll('path[data-edge-type="NEXT"]')).toHaveLength(
      2,
    );

    const fittedTransform = viewport.style.transform;
    const zoomIn = root.querySelector<HTMLButtonElement>(
      '[data-obs-graph-act="zoom-in"]',
    );
    assert(zoomIn);
    zoomIn.click();
    expect(viewport.style.transform).not.toBe(fittedTransform);

    const zoomedTransform = viewport.style.transform;
    host.dispatchEvent(
      new MouseEvent('pointerdown', {
        bubbles: true,
        button: 0,
        clientX: 100,
        clientY: 100,
      }),
    );
    host.dispatchEvent(
      new MouseEvent('pointermove', {
        bubbles: true,
        clientX: 150,
        clientY: 125,
      }),
    );
    host.dispatchEvent(new MouseEvent('pointerup', { bubbles: true }));
    expect(viewport.style.transform).not.toBe(zoomedTransform);
    const transformed = viewport.style.transform;

    root.innerHTML = graphView(detail);
    const refreshedHost = root.querySelector<HTMLElement>('.obs-graph');
    assert(refreshedHost);
    Object.defineProperties(refreshedHost, {
      clientWidth: { configurable: true, value: 900 },
      clientHeight: { configurable: true, value: 520 },
    });
    bindGraph(root);
    const refreshedViewport = root.querySelector<HTMLElement>(
      '.obs-graph-viewport',
    );
    assert(refreshedViewport);
    expect(refreshedViewport.style.transform).toBe(transformed);

    const planNode = root.querySelector<HTMLButtonElement>(
      '[data-obs-node="step-plan"]',
    );
    assert(planNode);
    planNode.click();
    const drawer = document.querySelector('#drawer-root .drawer');
    expect(drawer).toHaveTextContent('step-plan');
    expect(drawer).toHaveTextContent('入边');
    expect(drawer).toHaveTextContent('出边');
    expect(drawer).toHaveTextContent('Evaluating branch');

    Object.defineProperty(window, 'innerWidth', {
      configurable: true,
      value: 600,
    });
    const narrowRoot = document.createElement('div');
    narrowRoot.innerHTML = graphView(detail);
    const narrowSuccess = narrowRoot.querySelector<HTMLElement>(
      '[data-obs-node="step-success"]',
    );
    const narrowError = narrowRoot.querySelector<HTMLElement>(
      '[data-obs-node="step-error"]',
    );
    expect(narrowSuccess?.style.top).toBe(narrowError?.style.top);
    expect(narrowSuccess?.style.left).not.toBe(narrowError?.style.left);

    Object.defineProperty(window, 'innerWidth', {
      configurable: true,
      value: 1024,
    });
    const responsiveRoot = document.createElement('div');
    responsiveRoot.innerHTML = graphView(detail);
    bindResize(responsiveRoot);
    Object.defineProperty(window, 'innerWidth', {
      configurable: true,
      value: 600,
    });
    window.dispatchEvent(new Event('resize'));
    jest.runOnlyPendingTimers();
    const resizedSuccess = responsiveRoot.querySelector<HTMLElement>(
      '[data-obs-node="step-success"]',
    );
    const resizedError = responsiveRoot.querySelector<HTMLElement>(
      '[data-obs-node="step-error"]',
    );
    expect(resizedSuccess?.style.top).toBe(resizedError?.style.top);
    expect(resizedSuccess?.style.left).not.toBe(resizedError?.style.left);
  });
});
