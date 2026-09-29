import { act, cleanup, render, screen } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import WorkflowCanvasBenchmarkPage from './index';

beforeEach(() => {
  window.history.replaceState(
    {},
    '',
    '/workflow-canvas-benchmark?nodes=100&minimap=0&visible=0',
  );
});

afterEach(() => {
  cleanup();
  setLocale('en-US');
  window.history.replaceState({}, '', '/');
});

it.each([
  ['en-US', 'Workflow canvas benchmark'],
  ['zh-CN', '工作流画布基准测试'],
])('localizes the page name and renders stable node identities in %s', async (locale, title) => {
  setLocale(locale);
  render(<WorkflowCanvasBenchmarkPage />);

  expect(screen.getByRole('main', { name: title })).toBeInTheDocument();
  expect(screen.getByText('benchmark-node-0001')).toBeInTheDocument();
  expect(screen.getByText('benchmark-node-0100')).toBeInTheDocument();

  const benchmark = window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__;
  if (!benchmark) throw new Error('Benchmark page API was not initialized');
  await act(async () => {
    await benchmark.runStateScenario('topology-add');
  });

  expect(screen.getByText('benchmark-node-0101')).toBeInTheDocument();
});

it('updates the accessible name without resetting topology on a runtime locale change', async () => {
  render(<WorkflowCanvasBenchmarkPage />);
  expect(
    screen.getByRole('main', { name: 'Workflow canvas benchmark' }),
  ).toBeInTheDocument();

  const benchmark = window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__;
  if (!benchmark) throw new Error('Benchmark page API was not initialized');
  await act(async () => {
    await benchmark.runStateScenario('topology-add');
  });
  expect(screen.getByText('benchmark-node-0101')).toBeInTheDocument();

  act(() => setLocale('zh-CN'));

  expect(
    screen.getByRole('main', { name: '工作流画布基准测试' }),
  ).toBeInTheDocument();
  expect(screen.getByText('benchmark-node-0001')).toBeInTheDocument();
  expect(screen.getByText('benchmark-node-0101')).toBeInTheDocument();
  expect(window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__).toBe(benchmark);
});

it('localizes the invalid-configuration alert label', () => {
  setLocale('zh-CN');
  window.history.replaceState({}, '', '/workflow-canvas-benchmark?nodes=99');

  render(<WorkflowCanvasBenchmarkPage />);

  expect(
    screen.getByRole('alert', { name: '工作流画布基准测试' }),
  ).toHaveTextContent('Unsupported workflow canvas benchmark graph size');
  expect(window.__AEVATAR_WORKFLOW_CANVAS_BENCHMARK__).toBeUndefined();
});
