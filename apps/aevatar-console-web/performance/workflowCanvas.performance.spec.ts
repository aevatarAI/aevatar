import { mkdir, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { expect, type Page, test } from '@playwright/test';
import {
  assertWorkflowCanvasBenchmarkResult,
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
const ARTIFACT_DIRECTORY = path.resolve(
  __dirname,
  '../artifacts/workflow-canvas-benchmark',
);
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

async function runSelection(page: Page) {
  await page.locator('.react-flow__node').nth(1).click();
}

async function runDrag(page: Page) {
  const bounds = await page.locator('.react-flow__node').first().boundingBox();
  if (!bounds) {
    throw new Error('No rendered workflow node is available to drag');
  }
  const start = {
    x: bounds.x + bounds.width / 2,
    y: bounds.y + bounds.height / 2,
  };
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(start.x + 48, start.y + 32, { steps: 8 });
  await page.mouse.up();
}

async function runPan(page: Page) {
  const start = await findPanePoint(page);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(start.x + 72, start.y + 48, { steps: 8 });
  await page.mouse.up();
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
  await clickZoomControl(page, before.zoom < 0.48 ? 'out' : 'in');
  const after = await readViewport(page);
  expect(after.zoom < 0.48).toBe(before.zoom < 0.48);
}

async function runZoomThreshold(page: Page) {
  const before = await readViewport(page);
  const direction = before.zoom < 0.48 ? 'in' : 'out';
  for (let attempt = 0; attempt < 12; attempt += 1) {
    const current = await readViewport(page);
    if (current.zoom < 0.48 !== before.zoom < 0.48) {
      expect(current.zoom < 0.48).not.toBe(before.zoom < 0.48);
      return;
    }
    await clickZoomControl(page, direction);
  }
  throw new Error('Zoom did not cross the Studio compact threshold');
}

async function runScenarioAction(
  page: Page,
  scenario: Exclude<WorkflowCanvasBenchmarkScenario, 'initial-load'>,
) {
  switch (scenario) {
    case 'selection':
      await runSelection(page);
      break;
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
    complete:
      results.length ===
      WORKFLOW_CANVAS_BENCHMARK_GRAPH_SIZES.length *
        BENCHMARK_POLICIES.length *
        WORKFLOW_CANVAS_BENCHMARK_SCENARIOS.length,
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
          if (scenario === 'selection') {
            await page.locator('.react-flow__node').first().click();
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
          await runScenarioAction(page, scenario);
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

    expect(results).toHaveLength(96);
  } finally {
    await writeArtifacts({
      browserVersion: browser.version(),
      profiles,
      results,
      userAgent,
    });
  }
});
