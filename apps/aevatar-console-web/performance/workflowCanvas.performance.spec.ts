import { mkdir, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { expect, type Page, test } from '@playwright/test';
import {
  assertWorkflowCanvasBenchmarkResult,
  createWorkflowCanvasBenchmarkProgress,
  WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES,
  WORKFLOW_CANVAS_BENCHMARK_SCENARIOS,
  type WorkflowCanvasBenchmarkPolicy,
  type WorkflowCanvasBenchmarkReactCommit,
  type WorkflowCanvasBenchmarkResult,
  type WorkflowCanvasBenchmarkScenario,
} from '../src/pages/workflow-canvas-benchmark/benchmarkGraph';

type PolicyCase = {
  readonly id: string;
  readonly policy: WorkflowCanvasBenchmarkPolicy;
};

type ReactProfileArtifact = {
  readonly commits: readonly WorkflowCanvasBenchmarkReactCommit[];
  readonly graphNodes: number;
  readonly policy: WorkflowCanvasBenchmarkPolicy;
  readonly scenario: WorkflowCanvasBenchmarkScenario;
};

type InteractableNodeTarget = {
  readonly id: string;
  readonly point: {
    readonly x: number;
    readonly y: number;
  };
};

type InteractableNodePair = readonly [
  InteractableNodeTarget,
  InteractableNodeTarget,
];

const BENCHMARK_POLICIES: readonly PolicyCase[] = [
  {
    id: 'full-graph-with-minimap',
    policy: { minimap: true, visibleElementsOnly: false },
  },
  {
    id: 'full-graph-without-minimap',
    policy: { minimap: false, visibleElementsOnly: false },
  },
  {
    id: 'visible-elements-with-minimap',
    policy: { minimap: true, visibleElementsOnly: true },
  },
  {
    id: 'visible-elements-without-minimap',
    policy: { minimap: false, visibleElementsOnly: true },
  },
];
const EXPECTED_RESULT_COUNT =
  WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES.length *
  BENCHMARK_POLICIES.length *
  WORKFLOW_CANVAS_BENCHMARK_SCENARIOS.length;
const ARTIFACT_DIRECTORY = path.resolve(
  __dirname,
  '../artifacts/workflow-canvas-benchmark',
);
const COMPACT_ZOOM_THRESHOLD = 0.48;
const CONTROL_ZOOM_FACTOR = 1.2;
const POSITION_CHANGE_TOLERANCE = 1;
const ZOOM_CHANGE_TOLERANCE = 0.0001;
const EXPECTED_CHANGED_NODE_REFERENCES: Readonly<
  Record<WorkflowCanvasBenchmarkScenario, (graphNodes: number) => number>
> = {
  drag: () => 1,
  'initial-load': (graphNodes) => graphNodes,
  pan: () => 0,
  selection: () => 2,
  'status-update': () => 1,
  'topology-add': () => 2,
  'zoom-same-band': () => 0,
  'zoom-threshold': () => 0,
};

async function waitForAnimationFrames(page: Page, frameCount = 2) {
  await page.evaluate(async (frames) => {
    for (let index = 0; index < frames; index += 1) {
      await new Promise<void>((resolve) =>
        requestAnimationFrame(() => resolve()),
      );
    }
  }, frameCount);
}

async function readViewport(page: Page) {
  return page.locator('.react-flow__viewport').evaluate((element) => {
    const matrix = new DOMMatrix(getComputedStyle(element).transform);
    return { x: matrix.e, y: matrix.f, zoom: matrix.a };
  });
}

async function waitForInitialFit(page: Page) {
  await page.locator('[data-benchmark-ready="true"]').waitFor();
  await page.waitForFunction(async () => {
    if (
      !window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__ ||
      !document.querySelector('.react-flow__node')
    ) {
      return false;
    }

    const transforms: string[] = [];
    for (let index = 0; index < 4; index += 1) {
      await new Promise<void>((resolve) =>
        requestAnimationFrame(() => resolve()),
      );
      const viewport = document.querySelector<HTMLElement>(
        '.react-flow__viewport',
      );
      if (!viewport) {
        return false;
      }
      transforms.push(getComputedStyle(viewport).transform);
    }

    const matrix = new DOMMatrix(transforms.at(-1));
    return (
      Math.abs(matrix.a - 1) > 0.001 &&
      transforms.slice(-3).every((transform) => transform === transforms.at(-1))
    );
  });
}

async function findPanePoint(page: Page): Promise<{ x: number; y: number }> {
  return page.evaluate(() => {
    const pane = document.querySelector<HTMLElement>('.react-flow__pane');
    if (!pane) {
      throw new Error('React Flow pane is unavailable');
    }
    const bounds = pane.getBoundingClientRect();
    const candidates = [
      [0.75, 0.75],
      [0.5, 0.75],
      [0.75, 0.5],
      [0.25, 0.75],
      [0.5, 0.5],
    ] as const;
    for (const [horizontal, vertical] of candidates) {
      const x = bounds.left + bounds.width * horizontal;
      const y = bounds.top + bounds.height * vertical;
      if (
        document.elementFromPoint(x, y)?.classList.contains('react-flow__pane')
      ) {
        return { x, y };
      }
    }
    throw new Error('No unobstructed React Flow pane point is available');
  });
}

async function findInteractableNodePair(
  page: Page,
): Promise<InteractableNodePair> {
  return page.evaluate(() => {
    const pane = document.querySelector<HTMLElement>('.react-flow__pane');
    if (!pane) {
      throw new Error('React Flow pane is unavailable');
    }

    const paneBounds = pane.getBoundingClientRect();
    const seenIds = new Set<string>();
    const targets: InteractableNodeTarget[] = [];
    const nodes = document.querySelectorAll<HTMLElement>('.react-flow__node');
    for (const node of nodes) {
      const id = node.dataset.id;
      if (!id || seenIds.has(id) || !pane.contains(node)) {
        continue;
      }

      const nodeBounds = node.getBoundingClientRect();
      const intersection = {
        bottom: Math.min(nodeBounds.bottom, paneBounds.bottom),
        left: Math.max(nodeBounds.left, paneBounds.left),
        right: Math.min(nodeBounds.right, paneBounds.right),
        top: Math.max(nodeBounds.top, paneBounds.top),
      };
      if (
        intersection.right <= intersection.left ||
        intersection.bottom <= intersection.top
      ) {
        continue;
      }

      const point = {
        x: (intersection.left + intersection.right) / 2,
        y: (intersection.top + intersection.bottom) / 2,
      };
      const hitTarget = document.elementFromPoint(point.x, point.y);
      if (!hitTarget || !node.contains(hitTarget)) {
        continue;
      }

      seenIds.add(id);
      targets.push({ id, point });
      if (targets.length === 2) {
        return [targets[0], targets[1]] as InteractableNodePair;
      }
    }

    throw new Error(
      `Expected two distinct interactable workflow nodes, found ${targets.length}`,
    );
  });
}

async function readNodeScreenPosition(page: Page, nodeId: string) {
  return page.evaluate((expectedNodeId) => {
    const node = Array.from(
      document.querySelectorAll<HTMLElement>('.react-flow__node'),
    ).find((candidate) => candidate.dataset.id === expectedNodeId);
    if (!node) {
      throw new Error(`Workflow node ${expectedNodeId} is unavailable`);
    }
    const bounds = node.getBoundingClientRect();
    return { x: bounds.left, y: bounds.top };
  }, nodeId);
}

async function readNodeSelected(page: Page, nodeId: string) {
  return page.evaluate((expectedNodeId) => {
    const node = Array.from(
      document.querySelectorAll<HTMLElement>('.react-flow__node'),
    ).find((candidate) => candidate.dataset.id === expectedNodeId);
    if (!node) {
      throw new Error(`Workflow node ${expectedNodeId} is unavailable`);
    }
    return (
      node.classList.contains('selected') ||
      node.getAttribute('aria-selected') === 'true'
    );
  }, nodeId);
}

async function waitForSelectionState(
  page: Page,
  selectedNodeId: string,
  clearedNodeIds: readonly string[] = [],
) {
  await page.waitForFunction(
    ({ clearedIds, selectedId }) => {
      const nodes = Array.from(
        document.querySelectorAll<HTMLElement>('.react-flow__node'),
      );
      const isSelected = (nodeId: string) => {
        const node = nodes.find((candidate) => candidate.dataset.id === nodeId);
        return Boolean(
          node &&
            (node.classList.contains('selected') ||
              node.getAttribute('aria-selected') === 'true'),
        );
      };
      return (
        isSelected(selectedId) && clearedIds.every((id) => !isSelected(id))
      );
    },
    { clearedIds: clearedNodeIds, selectedId: selectedNodeId },
  );
  expect(await readNodeSelected(page, selectedNodeId)).toBe(true);
  for (const clearedNodeId of clearedNodeIds) {
    expect(await readNodeSelected(page, clearedNodeId)).toBe(false);
  }
}

async function prepareSelectionScenario(
  page: Page,
): Promise<InteractableNodePair> {
  const targets = await findInteractableNodePair(page);
  const [initialTarget] = targets;
  await page.mouse.click(initialTarget.point.x, initialTarget.point.y);
  await waitForSelectionState(page, initialTarget.id);
  return targets;
}

async function runSelection(page: Page, targets: InteractableNodePair) {
  const [initialTarget, nextTarget] = targets;
  await page.mouse.click(nextTarget.point.x, nextTarget.point.y);
  await waitForSelectionState(page, nextTarget.id, [initialTarget.id]);
}

async function runDrag(page: Page) {
  const [target] = await findInteractableNodePair(page);
  const before = await readNodeScreenPosition(page, target.id);
  await page.mouse.move(target.point.x, target.point.y);
  await page.mouse.down();
  await page.mouse.move(target.point.x + 48, target.point.y + 32, { steps: 8 });
  await page.mouse.up();
  await page.waitForFunction(
    ({ nodeId, position, tolerance }) => {
      const node = Array.from(
        document.querySelectorAll<HTMLElement>('.react-flow__node'),
      ).find((candidate) => candidate.dataset.id === nodeId);
      if (!node) {
        return false;
      }
      const bounds = node.getBoundingClientRect();
      return (
        Math.abs(bounds.left - position.x) > tolerance ||
        Math.abs(bounds.top - position.y) > tolerance
      );
    },
    {
      nodeId: target.id,
      position: before,
      tolerance: POSITION_CHANGE_TOLERANCE,
    },
  );
  const after = await readNodeScreenPosition(page, target.id);
  expect(
    Math.max(Math.abs(after.x - before.x), Math.abs(after.y - before.y)),
  ).toBeGreaterThan(POSITION_CHANGE_TOLERANCE);
}

async function runPan(page: Page) {
  const before = await readViewport(page);
  const start = await findPanePoint(page);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(start.x + 72, start.y + 48, { steps: 8 });
  await page.mouse.up();
  await waitForAnimationFrames(page);
  const after = await readViewport(page);
  expect(
    Math.max(Math.abs(after.x - before.x), Math.abs(after.y - before.y)),
  ).toBeGreaterThan(POSITION_CHANGE_TOLERANCE);
  expect(Math.abs(after.zoom - before.zoom)).toBeLessThanOrEqual(
    ZOOM_CHANGE_TOLERANCE,
  );
}

async function clickZoomControl(page: Page, direction: 'in' | 'out') {
  const before = await readViewport(page);
  await page.locator(`.react-flow__controls-zoom${direction}`).click();
  await page.waitForFunction(
    ({ direction: expectedDirection, zoom }) => {
      const viewport = document.querySelector<HTMLElement>(
        '.react-flow__viewport',
      );
      if (!viewport) {
        return false;
      }
      const currentZoom = new DOMMatrix(getComputedStyle(viewport).transform).a;
      return expectedDirection === 'in'
        ? currentZoom > zoom + 0.0001
        : currentZoom < zoom - 0.0001;
    },
    { direction, zoom: before.zoom },
  );
  await waitForAnimationFrames(page);
}

async function runZoomSameBand(page: Page) {
  const before = await readViewport(page);
  const candidates = [
    { direction: 'in', projectedZoom: before.zoom * CONTROL_ZOOM_FACTOR },
    { direction: 'out', projectedZoom: before.zoom / CONTROL_ZOOM_FACTOR },
  ] as const;
  const candidate = candidates.find(
    ({ projectedZoom }) =>
      projectedZoom > before.zoom + ZOOM_CHANGE_TOLERANCE &&
      projectedZoom < COMPACT_ZOOM_THRESHOLD ===
        before.zoom < COMPACT_ZOOM_THRESHOLD,
  );
  if (!candidate) {
    throw new Error(
      `No non-expanding control zoom remains in the current compact band from ${before.zoom}`,
    );
  }
  await clickZoomControl(page, candidate.direction);
  const after = await readViewport(page);
  expect(after.zoom).toBeGreaterThan(before.zoom + ZOOM_CHANGE_TOLERANCE);
  expect(after.zoom < COMPACT_ZOOM_THRESHOLD).toBe(
    before.zoom < COMPACT_ZOOM_THRESHOLD,
  );
}

async function runZoomThreshold(page: Page) {
  const before = await readViewport(page);
  const direction = before.zoom < COMPACT_ZOOM_THRESHOLD ? 'in' : 'out';
  for (let attempt = 0; attempt < 12; attempt += 1) {
    const current = await readViewport(page);
    if (
      current.zoom < COMPACT_ZOOM_THRESHOLD !==
      before.zoom < COMPACT_ZOOM_THRESHOLD
    ) {
      expect(current.zoom < COMPACT_ZOOM_THRESHOLD).not.toBe(
        before.zoom < COMPACT_ZOOM_THRESHOLD,
      );
      return;
    }
    await clickZoomControl(page, direction);
  }
  throw new Error('Zoom did not cross the Studio compact threshold');
}

async function runScenarioAction(
  page: Page,
  scenario: Exclude<WorkflowCanvasBenchmarkScenario, 'initial-load'>,
  selectionTargets?: InteractableNodePair,
) {
  switch (scenario) {
    case 'selection': {
      if (!selectionTargets) {
        throw new Error('Selection scenario targets are unavailable');
      }
      await runSelection(page, selectionTargets);
      break;
    }
    case 'drag':
      await runDrag(page);
      break;
    case 'pan':
      await runPan(page);
      break;
    case 'zoom-same-band':
      await runZoomSameBand(page);
      break;
    case 'zoom-threshold':
      await runZoomThreshold(page);
      break;
    case 'status-update':
    case 'topology-add':
      await page.evaluate(
        (stateScenario) =>
          window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__?.runStateScenario(
            stateScenario,
          ),
        scenario,
      );
      break;
  }
  await waitForAnimationFrames(page);
}

function assertSemanticResult(result: WorkflowCanvasBenchmarkResult) {
  expect(result.changedNodeReferences).toBe(
    EXPECTED_CHANGED_NODE_REFERENCES[result.scenario](result.graph.nodes),
  );
  if (result.scenario === 'initial-load') {
    expect(result.renderedNodeCount).toBeGreaterThan(0);
    if (!result.policy.visibleElementsOnly) {
      expect(result.renderedNodeCount).toBeGreaterThanOrEqual(
        result.graph.nodes,
      );
    }
  }
  if (result.scenario === 'zoom-same-band') {
    expect(result.renderedNodeCount).toBe(0);
  }
  if (result.scenario === 'zoom-threshold') {
    expect(result.renderedNodeCount).toBeGreaterThan(0);
  }
}

async function appendResult(
  page: Page,
  scenario: WorkflowCanvasBenchmarkScenario,
  startedAt: number,
): Promise<{
  readonly profile?: ReactProfileArtifact;
  readonly result: WorkflowCanvasBenchmarkResult;
}> {
  const captured = await page.evaluate(() => {
    const api = window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__;
    if (!api) {
      throw new Error('Workflow canvas benchmark API is unavailable');
    }
    return {
      config: api.config,
      endedAt: performance.now(),
      measurement: api.captureMeasurement(),
    };
  });
  const result = assertWorkflowCanvasBenchmarkResult({
    browser: captured.config.browser,
    buildMode: 'production',
    changedNodeReferences: captured.measurement.changedNodeReferences,
    durationMs: captured.endedAt - startedAt,
    graph: captured.config.graph,
    longTasks: captured.measurement.longTasks,
    policy: captured.config.policy,
    renderedNodeCount: captured.measurement.renderedNodeCount,
    scenario,
    usedHeapBytes: captured.measurement.usedHeapBytes,
  });
  assertSemanticResult(result);
  const storedResult = await page.evaluate((value) => {
    const api = window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__;
    if (!api) {
      throw new Error('Workflow canvas benchmark API is unavailable');
    }
    return api.appendResult(value);
  }, result);
  expect(storedResult).toEqual(result);

  return {
    profile: captured.measurement.reactCommits?.length
      ? {
          commits: captured.measurement.reactCommits,
          graphNodes: result.graph.nodes,
          policy: result.policy,
          scenario,
        }
      : undefined,
    result,
  };
}

function formatHeap(value: number | null): string {
  return value === null ? 'unavailable' : String(value);
}

function policyId(policy: WorkflowCanvasBenchmarkPolicy): string {
  return (
    BENCHMARK_POLICIES.find(
      (candidate) =>
        candidate.policy.minimap === policy.minimap &&
        candidate.policy.visibleElementsOnly === policy.visibleElementsOnly,
    )?.id ?? 'unknown'
  );
}

async function writeArtifacts({
  browserVersion,
  profiles,
  results,
  userAgent,
}: {
  readonly browserVersion: string;
  readonly profiles: readonly ReactProfileArtifact[];
  readonly results: readonly WorkflowCanvasBenchmarkResult[];
  readonly userAgent: string | null;
}) {
  const cpus = os.cpus();
  const progress = createWorkflowCanvasBenchmarkProgress(
    results.length,
    EXPECTED_RESULT_COUNT,
  );
  const envelope = {
    build: {
      commit: process.env.GITHUB_SHA || null,
      mode: 'production',
    },
    browser: {
      channel: 'chrome',
      userAgent,
      version: browserVersion,
    },
    complete: progress.complete,
    generatedAt: new Date().toISOString(),
    profiles: profiles.length ? profiles : undefined,
    results,
    runner: {
      architecture: os.arch(),
      ci: process.env.CI === 'true',
      cpuModel: cpus[0]?.model ?? null,
      logicalCpuCount: cpus.length,
      operatingSystem: `${os.platform()} ${os.release()}`,
      totalMemoryBytes: os.totalmem(),
    },
    schemaVersion: 1,
  };
  const profileSummary = profiles.length
    ? `${profiles.reduce((sum, profile) => sum + profile.commits.length, 0)} React commits were exposed by the runtime.`
    : 'React commit profiling was unavailable in this production runtime.';
  const markdown = [
    '# Workflow Canvas Benchmark',
    '',
    `- Build: production${envelope.build.commit ? ` (${envelope.build.commit})` : ''}`,
    ...progress.markdownLines,
    `- Browser: system Chrome ${browserVersion}`,
    `- User agent: ${userAgent ?? 'unavailable'}`,
    `- Runner: ${envelope.runner.operatingSystem}, ${envelope.runner.architecture}, ${envelope.runner.logicalCpuCount} logical CPUs, ${envelope.runner.totalMemoryBytes} bytes memory`,
    `- React profiling: ${profileSummary}`,
    '',
    '| Nodes | Policy | Scenario | Duration (ms) | Long tasks | Rendered nodes | Changed refs | Heap (bytes) |',
    '| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |',
    ...results.map(
      (result) =>
        `| ${result.graph.nodes} | ${policyId(result.policy)} | ${result.scenario} | ${result.durationMs.toFixed(2)} | ${result.longTasks} | ${result.renderedNodeCount} | ${result.changedNodeReferences} | ${formatHeap(result.usedHeapBytes)} |`,
    ),
    '',
    'Timing and heap values are observational. CI gates only the schema and semantic assertions.',
    '',
  ].join('\n');

  await mkdir(ARTIFACT_DIRECTORY, { recursive: true });
  await Promise.all([
    writeFile(
      path.join(ARTIFACT_DIRECTORY, 'results.json'),
      `${JSON.stringify(envelope, null, 2)}\n`,
      'utf8',
    ),
    writeFile(path.join(ARTIFACT_DIRECTORY, 'summary.md'), markdown, 'utf8'),
  ]);
}

test('records every workflow canvas scale scenario and policy', async ({
  browser,
  page,
}) => {
  const results: WorkflowCanvasBenchmarkResult[] = [];
  const profiles: ReactProfileArtifact[] = [];
  let userAgent: string | null = null;

  try {
    for (const graphNodes of WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES) {
      for (const policyCase of BENCHMARK_POLICIES) {
        const query = new URLSearchParams({
          minimap: policyCase.policy.minimap ? '1' : '0',
          nodes: String(graphNodes),
          visible: policyCase.policy.visibleElementsOnly ? '1' : '0',
        });
        await page.goto(`/workflow-canvas-benchmark?${query.toString()}`, {
          waitUntil: 'domcontentloaded',
        });
        await waitForInitialFit(page);

        const initial = await appendResult(page, 'initial-load', 0);
        results.push(initial.result);
        if (initial.profile) profiles.push(initial.profile);
        userAgent ??= initial.result.browser;

        for (const scenario of WORKFLOW_CANVAS_BENCHMARK_SCENARIOS) {
          if (scenario === 'initial-load') {
            continue;
          }
          const selectionTargets =
            scenario === 'selection'
              ? await prepareSelectionScenario(page)
              : undefined;
          if (selectionTargets) {
            await waitForAnimationFrames(page);
          }
          const startedAt = await page.evaluate((nextScenario) => {
            const api = window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__;
            if (!api) {
              throw new Error('Workflow canvas benchmark API is unavailable');
            }
            api.beginScenario(nextScenario);
            return performance.now();
          }, scenario);
          await runScenarioAction(page, scenario, selectionTargets);
          const captured = await appendResult(page, scenario, startedAt);
          results.push(captured.result);
          if (captured.profile) profiles.push(captured.profile);
        }

        const pageResults = await page.evaluate(
          () =>
            window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__?.getResults() ?? [],
        );
        expect(pageResults).toEqual(results.slice(-8));
      }
    }

    expect(results).toHaveLength(EXPECTED_RESULT_COUNT);
  } finally {
    await writeArtifacts({
      browserVersion: browser.version(),
      profiles,
      results,
      userAgent,
    });
  }
});
