import { useEffect, useMemo, useState } from 'react';
import { useVoiceSession, type VoiceProviderSelection } from './useVoiceSession';
import './voice.css';

const DEFAULT_SAMPLE_RATE_HZ = 24000;

function readInitialProvider(value: string | null): VoiceProviderSelection {
  if (value === 'openai' || value === 'minicpm') {
    return value;
  }

  return '';
}

function readInitialSampleRate(value: string | null) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0
    ? parsed
    : DEFAULT_SAMPLE_RATE_HZ;
}

function formatStatusLabel(status: ReturnType<typeof useVoiceSession>['status']) {
  switch (status) {
    case 'connecting':
      return 'Connecting';
    case 'requesting-mic':
      return 'Requesting Mic';
    case 'live':
      return 'Live';
    case 'stopped':
      return 'Stopped';
    case 'error':
      return 'Error';
    case 'idle':
    default:
      return 'Idle';
  }
}

function formatProviderLabel(provider: VoiceProviderSelection) {
  switch (provider) {
    case 'openai':
      return 'OpenAI';
    case 'minicpm':
      return 'MiniCPM';
    default:
      return 'Host Default';
  }
}

export default function VoicePage() {
  const initialQuery = useMemo(() => new URLSearchParams(window.location.search), []);
  const [agentId, setAgentId] = useState(initialQuery.get('agent')?.trim() ?? '');
  const [provider, setProvider] = useState<VoiceProviderSelection>(readInitialProvider(initialQuery.get('provider')));
  const [voiceHint, setVoiceHint] = useState(initialQuery.get('voice')?.trim() ?? '');
  const [requestedSampleRateHz, setRequestedSampleRateHz] = useState(readInitialSampleRate(initialQuery.get('sampleRateHz')));
  const [runtimeBaseUrl, setRuntimeBaseUrl] = useState('Loading...');

  const session = useVoiceSession({
    agentId,
    provider,
    requestedSampleRateHz,
  });

  const statusLabel = formatStatusLabel(session.status);
  const statusTone = session.status === 'live'
    ? 'ok'
    : session.status === 'error'
      ? 'error'
      : session.status === 'connecting' || session.status === 'requesting-mic'
        ? 'warning'
        : 'idle';
  const controlsLocked = session.status === 'connecting' || session.status === 'requesting-mic' || session.status === 'live';
  const canStart = agentId.trim().length > 0 && !controlsLocked;
  const providerLabel = formatProviderLabel(provider);

  useEffect(() => {
    const controller = new AbortController();

    fetch('/api/_proxy/runtime-url', { signal: controller.signal })
      .then(async response => {
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }

        const payload = await response.json() as { runtimeBaseUrl?: string };
        setRuntimeBaseUrl(payload.runtimeBaseUrl?.trim() || 'Unavailable');
      })
      .catch(() => setRuntimeBaseUrl('Unavailable'));

    return () => controller.abort();
  }, []);

  useEffect(() => {
    const url = new URL(window.location.href);
    if (agentId.trim()) {
      url.searchParams.set('agent', agentId.trim());
    } else {
      url.searchParams.delete('agent');
    }

    if (provider) {
      url.searchParams.set('provider', provider);
    } else {
      url.searchParams.delete('provider');
    }

    if (voiceHint.trim()) {
      url.searchParams.set('voice', voiceHint.trim());
    } else {
      url.searchParams.delete('voice');
    }

    url.searchParams.set('sampleRateHz', String(requestedSampleRateHz));
    window.history.replaceState(null, '', url.toString());
  }, [agentId, provider, requestedSampleRateHz, voiceHint]);

  return (
    <main className="voice-shell">
      <section className="voice-hero">
        <p className="voice-kicker">Aevatar Voice</p>
        <div className="voice-hero-row">
          <div>
            <h1>Browser voice session for actor-bound realtime audio.</h1>
            <p className="voice-summary">
              Phase A keeps audio in the browser, proxies WebSocket voice frames through the local app host,
              and lets the backend resolve the voice-enabled actor session.
            </p>
          </div>
          <div className={`voice-status-chip ${statusTone}`}>
            <span>{statusLabel}</span>
            <small>{providerLabel}</small>
          </div>
        </div>
      </section>

      <section className="voice-grid">
        <div className="voice-card voice-card-form">
          <div className="voice-card-head">
            <h2>Session</h2>
            <p>Launch a browser voice channel against a specific actor ID.</p>
          </div>

          <label className="voice-field">
            <span>Actor ID</span>
            <input
              type="text"
              value={agentId}
              disabled={controlsLocked}
              onChange={event => setAgentId(event.target.value)}
              placeholder="robot-dog-1"
            />
          </label>

          <div className="voice-field-row">
            <label className="voice-field">
              <span>Provider</span>
              <select
                value={provider}
                disabled={controlsLocked}
                onChange={event => setProvider(event.target.value as VoiceProviderSelection)}
              >
                <option value="">Host Default</option>
                <option value="openai">OpenAI</option>
                <option value="minicpm">MiniCPM</option>
              </select>
            </label>

            <label className="voice-field">
              <span>Requested Voice</span>
              <input
                type="text"
                value={voiceHint}
                disabled={controlsLocked}
                onChange={event => setVoiceHint(event.target.value)}
                placeholder="alloy"
              />
            </label>
          </div>

          <div className="voice-field-row">
            <label className="voice-field">
              <span>Requested Sample Rate</span>
              <input
                type="number"
                min={8000}
                step={1000}
                value={requestedSampleRateHz}
                disabled={controlsLocked}
                onChange={event => setRequestedSampleRateHz(readInitialSampleRate(event.target.value))}
              />
            </label>

            <div className="voice-facts">
              <span>Backend</span>
              <strong>{runtimeBaseUrl}</strong>
            </div>
          </div>

          <div className="voice-actions">
            <button type="button" className="voice-primary" onClick={() => void session.start()} disabled={!canStart}>
              Start Voice
            </button>
            <button type="button" className="voice-secondary" onClick={session.stop} disabled={!controlsLocked && session.status !== 'stopped' && session.status !== 'error'}>
              Stop
            </button>
          </div>

          <p className="voice-note">
            Browser microphone permissions are required. Provider selection maps to the backend voice module alias
            when one is specified.
          </p>
          {session.errorMessage ? <p className="voice-inline-error">{session.errorMessage}</p> : null}
        </div>

        <div className="voice-card voice-card-metrics">
          <div className="voice-card-head">
            <h2>Transport</h2>
            <p>Observe the browser audio context and proxy frame flow.</p>
          </div>

          <dl className="voice-metrics">
            <div>
              <dt>Actual Sample Rate</dt>
              <dd>{session.actualSampleRateHz ? `${session.actualSampleRateHz} Hz` : 'Not started'}</dd>
            </div>
            <div>
              <dt>Sent Frames</dt>
              <dd>{session.sentFrames}</dd>
            </div>
            <div>
              <dt>Received Frames</dt>
              <dd>{session.receivedFrames}</dd>
            </div>
            <div>
              <dt>Playout Sequence</dt>
              <dd>{session.playoutSequence}</dd>
            </div>
            <div>
              <dt>Response ID</dt>
              <dd>{session.currentResponseId || 'Not advertised'}</dd>
            </div>
            <div>
              <dt>Requested Voice</dt>
              <dd>{voiceHint.trim() || 'Host configured'}</dd>
            </div>
          </dl>
        </div>
      </section>

      <section className="voice-card voice-card-log">
        <div className="voice-card-head">
          <h2>Session Log</h2>
          <p>Newest entries are shown first so transport failures stay visible.</p>
        </div>

        <div className="voice-log-stream">
          {session.logs.length === 0 ? (
            <div className="voice-log-empty">Start a session to capture voice transport events.</div>
          ) : (
            session.logs.map(entry => (
              <div key={entry.id} className={`voice-log-entry ${entry.level}`}>
                <span className="voice-log-time">{entry.timestamp}</span>
                <span className="voice-log-message">{entry.message}</span>
              </div>
            ))
          )}
        </div>
      </section>
    </main>
  );
}
