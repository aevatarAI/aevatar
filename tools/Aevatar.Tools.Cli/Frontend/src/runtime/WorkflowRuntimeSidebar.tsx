import {
  AlertTriangle,
  CheckCircle2,
  ChevronLeft,
  ChevronRight,
  CircleDot,
  Clock3,
  LoaderCircle,
  PauseCircle,
  Workflow,
} from 'lucide-react';

import type { ActiveRunState, ActiveRunStep } from './runState';

type Props = {
  run: ActiveRunState | null;
  open: boolean;
  onToggle: () => void;
};

function shortId(value?: string) {
  const normalized = String(value || '').trim();
  if (!normalized) {
    return 'Pending';
  }

  if (normalized.length <= 28) {
    return normalized;
  }

  return `${normalized.slice(0, 12)}...${normalized.slice(-8)}`;
}

function formatTimestamp(timestamp?: number) {
  if (!timestamp) {
    return 'Pending';
  }

  return new Date(timestamp).toLocaleTimeString();
}

function getStatusLabel(run: ActiveRunState) {
  if (run.status === 'waiting' && run.waitingKind === 'human-input') {
    return 'Waiting for input';
  }

  if (run.status === 'waiting' && run.waitingKind === 'signal') {
    return 'Waiting for signal';
  }

  if (run.status === 'waiting' && run.waitingKind === 'tool-approval') {
    return 'Waiting for approval';
  }

  return run.status;
}

function StepStatusIcon({ step }: { step: ActiveRunStep }) {
  if (step.status === 'completed') {
    return <CheckCircle2 size={15} className="text-emerald-600" />;
  }

  if (step.status === 'error') {
    return <AlertTriangle size={15} className="text-rose-600" />;
  }

  if (step.status === 'waiting') {
    return <PauseCircle size={15} className="text-amber-600" />;
  }

  if (step.status === 'active') {
    return <LoaderCircle size={15} className="animate-spin text-violet-600" />;
  }

  return <CircleDot size={15} className="text-stone-300" />;
}

function StepRow({ step, isCurrent }: { step: ActiveRunStep; isCurrent: boolean }) {
  return (
    <div className={`rounded-2xl border px-3 py-3 ${
      isCurrent
        ? 'border-violet-200 bg-violet-50/80'
        : step.status === 'completed'
        ? 'border-emerald-100 bg-emerald-50/50'
        : step.status === 'waiting'
        ? 'border-amber-100 bg-amber-50/50'
        : step.status === 'error'
        ? 'border-rose-100 bg-rose-50/50'
        : 'border-stone-200 bg-white/90'
    }`}>
      <div className="flex items-start gap-2.5">
        <div className="mt-0.5">
          <StepStatusIcon step={step} />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <div className="truncate text-[13px] font-semibold text-stone-800">{step.label}</div>
            {isCurrent && (
              <span className="rounded-full bg-violet-100 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.12em] text-violet-700">
                Current
              </span>
            )}
          </div>
          <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 text-[11px] text-stone-500">
            {step.stepType && <span>Type: {step.stepType}</span>}
            {step.targetRole && <span>Role: {step.targetRole}</span>}
            <span>Status: {step.status}</span>
          </div>
          {step.output && (
            <div className="mt-2 rounded-xl bg-white/90 px-2.5 py-2 text-[12px] text-stone-600">
              {step.output.length > 160 ? `${step.output.slice(0, 160)}...` : step.output}
            </div>
          )}
          {step.error && (
            <div className="mt-2 rounded-xl bg-white/90 px-2.5 py-2 text-[12px] text-rose-600">
              {step.error}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export default function WorkflowRuntimeSidebar({ run, open, onToggle }: Props) {
  return (
    <aside className={`border-l border-[#E6E3DE] bg-[#FCFBF8] transition-all duration-200 ${open ? 'w-[360px]' : 'w-[52px]'}`}>
      <div className="flex h-full min-h-0 flex-col">
        <div className="flex items-center justify-between border-b border-[#E6E3DE] px-3 py-3">
          {open ? (
            <div className="flex items-center gap-2">
              <div className="flex h-8 w-8 items-center justify-center rounded-2xl bg-stone-900 text-white">
                <Workflow size={16} />
              </div>
              <div>
                <div className="text-[12px] font-semibold text-stone-800">Run Visibility</div>
                <div className="text-[10px] uppercase tracking-[0.14em] text-stone-400">Overview · Steps · Timeline</div>
              </div>
            </div>
          ) : (
            <div className="mx-auto flex h-8 w-8 items-center justify-center rounded-2xl bg-stone-900 text-white">
              <Workflow size={16} />
            </div>
          )}

          <button
            type="button"
            onClick={onToggle}
            className="rounded-xl border border-stone-200 bg-white px-2 py-2 text-stone-500 transition-colors hover:text-stone-800"
            title={open ? 'Collapse run panel' : 'Expand run panel'}
          >
            {open ? <ChevronRight size={16} /> : <ChevronLeft size={16} />}
          </button>
        </div>

        {!open ? null : (
          <div className="min-h-0 flex-1 overflow-auto px-3 py-3">
            {!run ? (
              <div className="rounded-[24px] border border-dashed border-stone-300 bg-white/80 px-4 py-5 text-[13px] text-stone-500">
                No workflow run is being tracked yet. Once a workflow starts, this panel will show the current node,
                execution timeline, waiting state, and run context.
              </div>
            ) : (
              <div className="space-y-4">
                <section className="rounded-[24px] border border-stone-200 bg-white/90 p-4 shadow-sm shadow-stone-200/60">
                  <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-stone-400">Overview</div>
                  <div className="mt-2 text-[16px] font-semibold text-stone-900">{run.workflowName || 'Workflow run'}</div>
                  <div className="mt-1 text-[12px] text-stone-500">
                    {run.currentStepLabel ? `Current step: ${run.currentStepLabel}` : 'Waiting for the first step event.'}
                  </div>

                  <div className="mt-3 grid grid-cols-2 gap-2">
                    <div className="rounded-2xl bg-stone-50 px-3 py-2">
                      <div className="text-[10px] uppercase tracking-[0.12em] text-stone-400">Status</div>
                      <div className="mt-1 text-[12px] font-semibold capitalize text-stone-800">{getStatusLabel(run)}</div>
                    </div>
                    <div className="rounded-2xl bg-stone-50 px-3 py-2">
                      <div className="text-[10px] uppercase tracking-[0.12em] text-stone-400">Last Event</div>
                      <div className="mt-1 text-[12px] font-semibold text-stone-800">{formatTimestamp(run.lastEventAt)}</div>
                    </div>
                  </div>

                  {(run.waitingPrompt || run.error) && (
                    <div className={`mt-3 rounded-2xl px-3 py-2 text-[12px] ${
                      run.error
                        ? 'bg-rose-50 text-rose-700'
                        : 'bg-amber-50 text-amber-800'
                    }`}>
                      {run.error || run.waitingPrompt}
                    </div>
                  )}
                </section>

                <section className="rounded-[24px] border border-stone-200 bg-white/90 p-4 shadow-sm shadow-stone-200/60">
                  <div className="flex items-center justify-between">
                    <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-stone-400">Steps</div>
                    <div className="text-[11px] text-stone-400">{run.steps.length} total</div>
                  </div>
                  <div className="mt-3 space-y-2.5">
                    {run.steps.length === 0 ? (
                      <div className="rounded-2xl bg-stone-50 px-3 py-3 text-[12px] text-stone-500">
                        Waiting for the workflow to publish step-level events.
                      </div>
                    ) : run.steps.map(step => (
                      <StepRow
                        key={step.id}
                        step={step}
                        isCurrent={step.id === run.currentStepId}
                      />
                    ))}
                  </div>
                </section>

                <section className="rounded-[24px] border border-stone-200 bg-white/90 p-4 shadow-sm shadow-stone-200/60">
                  <div className="flex items-center gap-2">
                    <Clock3 size={14} className="text-stone-400" />
                    <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-stone-400">Timeline</div>
                  </div>
                  <div className="mt-3 space-y-2.5">
                    {run.timeline.length === 0 ? (
                      <div className="rounded-2xl bg-stone-50 px-3 py-3 text-[12px] text-stone-500">
                        Timeline is waiting for the first event.
                      </div>
                    ) : run.timeline.slice().reverse().map(item => (
                      <div key={item.id} className="rounded-2xl bg-stone-50 px-3 py-2.5">
                        <div className="flex items-center justify-between gap-3">
                          <div className="text-[12px] font-semibold text-stone-800">{item.title}</div>
                          <div className="text-[10px] text-stone-400">{formatTimestamp(item.timestamp)}</div>
                        </div>
                        {item.detail && (
                          <div className={`mt-1 text-[11px] ${
                            item.tone === 'error'
                              ? 'text-rose-600'
                              : item.tone === 'warning'
                              ? 'text-amber-700'
                              : item.tone === 'success'
                              ? 'text-emerald-700'
                              : 'text-stone-500'
                          }`}>
                            {item.detail}
                          </div>
                        )}
                      </div>
                    ))}
                  </div>
                </section>

                <section className="rounded-[24px] border border-stone-200 bg-white/90 p-4 shadow-sm shadow-stone-200/60">
                  <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-stone-400">Context</div>
                  <div className="mt-3 space-y-2">
                    <div className="rounded-2xl bg-stone-50 px-3 py-2">
                      <div className="text-[10px] uppercase tracking-[0.12em] text-stone-400">Actor</div>
                      <div className="mt-1 break-all font-mono text-[12px] text-stone-700">{shortId(run.actorId)}</div>
                    </div>
                    <div className="rounded-2xl bg-stone-50 px-3 py-2">
                      <div className="text-[10px] uppercase tracking-[0.12em] text-stone-400">Run</div>
                      <div className="mt-1 break-all font-mono text-[12px] text-stone-700">{shortId(run.runId)}</div>
                    </div>
                    <div className="rounded-2xl bg-stone-50 px-3 py-2">
                      <div className="text-[10px] uppercase tracking-[0.12em] text-stone-400">Command</div>
                      <div className="mt-1 break-all font-mono text-[12px] text-stone-700">{shortId(run.commandId)}</div>
                    </div>
                    {run.waitingSignalName && (
                      <div className="rounded-2xl bg-amber-50 px-3 py-2">
                        <div className="text-[10px] uppercase tracking-[0.12em] text-amber-500">Signal</div>
                        <div className="mt-1 text-[12px] font-medium text-amber-800">{run.waitingSignalName}</div>
                      </div>
                    )}
                  </div>
                </section>
              </div>
            )}
          </div>
        )}
      </div>
    </aside>
  );
}

