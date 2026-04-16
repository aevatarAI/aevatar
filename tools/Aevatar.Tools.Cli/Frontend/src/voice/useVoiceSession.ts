import { useEffect, useRef, useState } from 'react';

export type VoiceProviderSelection = '' | 'openai' | 'minicpm';
export type VoiceSessionStatus = 'idle' | 'connecting' | 'requesting-mic' | 'live' | 'stopped' | 'error';

export type VoiceLogEntry = {
  id: string;
  level: 'info' | 'warn' | 'error';
  message: string;
  timestamp: string;
};

type UseVoiceSessionOptions = {
  agentId: string;
  provider: VoiceProviderSelection;
  requestedSampleRateHz: number;
};

type UseVoiceSessionResult = {
  status: VoiceSessionStatus;
  errorMessage: string;
  actualSampleRateHz: number | null;
  sentFrames: number;
  receivedFrames: number;
  playoutSequence: number;
  currentResponseId: number;
  logs: VoiceLogEntry[];
  start: () => Promise<void>;
  stop: () => void;
};

const MAX_LOG_ENTRIES = 60;

function buildVoiceWebSocketUrl(agentId: string, provider: VoiceProviderSelection) {
  const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  const url = new URL(`${protocol}//${window.location.host}/ws/voice/${encodeURIComponent(agentId)}`);
  const moduleName = provider === 'openai'
    ? 'voice_presence_openai'
    : provider === 'minicpm'
      ? 'voice_presence_minicpm'
      : '';

  if (moduleName) {
    url.searchParams.set('module', moduleName);
  }

  return url.toString();
}

function buildDrainAcknowledgedFrame(responseId: number, playoutSequence: number) {
  return JSON.stringify({
    drainAcknowledged: {
      responseId,
      playoutSequence,
    },
  });
}

function asErrorMessage(error: unknown) {
  if (error instanceof Error && error.message.trim()) {
    return error.message.trim();
  }

  return 'Voice session failed.';
}

export function useVoiceSession({
  agentId,
  provider,
  requestedSampleRateHz,
}: UseVoiceSessionOptions): UseVoiceSessionResult {
  const [status, setStatus] = useState<VoiceSessionStatus>('idle');
  const [errorMessage, setErrorMessage] = useState('');
  const [actualSampleRateHz, setActualSampleRateHz] = useState<number | null>(null);
  const [sentFrames, setSentFrames] = useState(0);
  const [receivedFrames, setReceivedFrames] = useState(0);
  const [playoutSequence, setPlayoutSequence] = useState(0);
  const [currentResponseId, setCurrentResponseId] = useState(0);
  const [logs, setLogs] = useState<VoiceLogEntry[]>([]);

  const wsRef = useRef<WebSocket | null>(null);
  const audioContextRef = useRef<AudioContext | null>(null);
  const micStreamRef = useRef<MediaStream | null>(null);
  const micEncoderNodeRef = useRef<AudioWorkletNode | null>(null);
  const speakerDecoderNodeRef = useRef<AudioWorkletNode | null>(null);
  const mutedGainRef = useRef<GainNode | null>(null);
  const manualCloseRef = useRef(false);
  const currentResponseIdRef = useRef(0);
  const playoutSequenceRef = useRef(0);

  function appendLog(message: string, level: VoiceLogEntry['level'] = 'info') {
    const timestamp = new Date().toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });

    setLogs(prev => [
      {
        id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
        level,
        message,
        timestamp,
      },
      ...prev,
    ].slice(0, MAX_LOG_ENTRIES));
  }

  function resetSessionCounters() {
    currentResponseIdRef.current = 0;
    playoutSequenceRef.current = 0;
    setCurrentResponseId(0);
    setPlayoutSequence(0);
    setSentFrames(0);
    setReceivedFrames(0);
  }

  function cleanupAudio() {
    micStreamRef.current?.getTracks().forEach(track => track.stop());
    micStreamRef.current = null;

    try {
      micEncoderNodeRef.current?.disconnect();
    } catch {
      // best effort cleanup
    }
    micEncoderNodeRef.current = null;

    try {
      mutedGainRef.current?.disconnect();
    } catch {
      // best effort cleanup
    }
    mutedGainRef.current = null;

    try {
      speakerDecoderNodeRef.current?.disconnect();
    } catch {
      // best effort cleanup
    }
    speakerDecoderNodeRef.current = null;

    const audioContext = audioContextRef.current;
    audioContextRef.current = null;
    if (audioContext) {
      void audioContext.close().catch(() => undefined);
    }

    setActualSampleRateHz(null);
  }

  function handleServerControlFrame(payload: string) {
    try {
      const parsed = JSON.parse(payload) as Record<string, unknown>;

      const responseStarted = parsed.responseStarted as { responseId?: number | string } | undefined;
      if (responseStarted?.responseId !== undefined) {
        const nextResponseId = Number(responseStarted.responseId);
        if (Number.isFinite(nextResponseId)) {
          currentResponseIdRef.current = nextResponseId;
          setCurrentResponseId(nextResponseId);
          appendLog(`Response ${nextResponseId} started.`);
          return;
        }
      }

      const responseDone = parsed.responseDone as { responseId?: number | string } | undefined;
      if (responseDone?.responseId !== undefined) {
        appendLog(`Response ${responseDone.responseId} completed.`);
        return;
      }

      const providerError = parsed.error as { errorMessage?: string; errorCode?: string } | undefined;
      if (providerError) {
        const message = providerError.errorMessage || providerError.errorCode || 'Voice provider error.';
        appendLog(message, 'error');
        return;
      }

      const legacyType = typeof parsed.type === 'string' ? parsed.type : '';
      if (legacyType) {
        if (legacyType === 'response_started' && typeof parsed.response_id !== 'undefined') {
          const nextResponseId = Number(parsed.response_id);
          if (Number.isFinite(nextResponseId)) {
            currentResponseIdRef.current = nextResponseId;
            setCurrentResponseId(nextResponseId);
          }
        }

        appendLog(`Control: ${legacyType}`);
        return;
      }

      appendLog(`Control: ${payload}`);
    } catch {
      appendLog(`Control: ${payload}`);
    }
  }

  async function start() {
    const normalizedAgentId = agentId.trim();
    if (!normalizedAgentId || status === 'connecting' || status === 'requesting-mic' || status === 'live') {
      return;
    }

    manualCloseRef.current = false;
    resetSessionCounters();
    setErrorMessage('');
    setStatus('connecting');
    appendLog(`Connecting to actor ${normalizedAgentId}...`);

    let ws: WebSocket | null = null;

    try {
      const wsUrl = buildVoiceWebSocketUrl(normalizedAgentId, provider);
      ws = new WebSocket(wsUrl);
      ws.binaryType = 'arraybuffer';
      wsRef.current = ws;

      ws.onmessage = event => {
        if (typeof event.data === 'string') {
          handleServerControlFrame(event.data);
          return;
        }

        speakerDecoderNodeRef.current?.port.postMessage(event.data, [event.data]);
        setReceivedFrames(prev => prev + 1);
      };

      ws.onclose = event => {
        cleanupAudio();
        wsRef.current = null;

        if (manualCloseRef.current || event.code === 1000) {
          setStatus('stopped');
          appendLog('Voice session closed.');
          return;
        }

        const reason = event.reason?.trim()
          ? `Voice session closed (${event.reason}).`
          : `Voice session closed with code ${event.code}.`;
        setErrorMessage(reason);
        setStatus('error');
        appendLog(reason, 'error');
      };

      ws.onerror = () => {
        appendLog('Voice WebSocket reported an error.', 'error');
      };

      await new Promise<void>((resolve, reject) => {
        const handleOpen = () => {
          ws?.removeEventListener('error', handleError);
          resolve();
        };

        const handleError = () => {
          ws?.removeEventListener('open', handleOpen);
          reject(new Error('Voice WebSocket could not connect to the configured backend.'));
        };

        ws?.addEventListener('open', handleOpen, { once: true });
        ws?.addEventListener('error', handleError, { once: true });
      });

      appendLog('Voice transport attached. Requesting microphone...');
      setStatus('requesting-mic');

      const micStream = await navigator.mediaDevices.getUserMedia({
        audio: {
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true,
        },
      });
      micStreamRef.current = micStream;

      const context = new AudioContext({ sampleRate: requestedSampleRateHz });
      audioContextRef.current = context;
      await context.resume();
      setActualSampleRateHz(context.sampleRate);
      if (context.sampleRate !== requestedSampleRateHz) {
        appendLog(
          `Browser kept sample rate ${context.sampleRate} Hz instead of requested ${requestedSampleRateHz} Hz.`,
          'warn',
        );
      } else {
        appendLog(`Audio context ready at ${context.sampleRate} Hz.`);
      }

      await context.audioWorklet.addModule('/audio-worklets/mic-encoder.js');
      await context.audioWorklet.addModule('/audio-worklets/speaker-decoder.js');

      const speakerDecoderNode = new AudioWorkletNode(context, 'speaker-decoder');
      speakerDecoderNode.connect(context.destination);
      speakerDecoderNode.port.onmessage = event => {
        const data = event.data as { type?: string; count?: number } | undefined;
        if (data?.type !== 'drained' || !Number.isFinite(data.count)) {
          return;
        }

        const nextSequence = playoutSequenceRef.current + Number(data.count);
        playoutSequenceRef.current = nextSequence;
        setPlayoutSequence(nextSequence);

        if (wsRef.current?.readyState === WebSocket.OPEN && currentResponseIdRef.current > 0) {
          wsRef.current.send(buildDrainAcknowledgedFrame(currentResponseIdRef.current, nextSequence));
        }
      };
      speakerDecoderNodeRef.current = speakerDecoderNode;

      const micSource = context.createMediaStreamSource(micStream);
      const micEncoderNode = new AudioWorkletNode(context, 'mic-encoder');
      const mutedGain = context.createGain();
      mutedGain.gain.value = 0;

      micSource.connect(micEncoderNode);
      micEncoderNode.connect(mutedGain).connect(context.destination);

      micEncoderNode.port.onmessage = event => {
        if (wsRef.current?.readyState !== WebSocket.OPEN) {
          return;
        }

        const chunk = event.data as ArrayBuffer;
        wsRef.current.send(chunk);
        setSentFrames(prev => prev + 1);
      };

      micEncoderNodeRef.current = micEncoderNode;
      mutedGainRef.current = mutedGain;
      setStatus('live');
      appendLog('Microphone connected. Voice session is live.');
    } catch (error) {
      const message = asErrorMessage(error);
      setErrorMessage(message);
      setStatus('error');
      appendLog(message, 'error');
      cleanupAudio();
      ws?.close();
      wsRef.current = null;
    }
  }

  function stop() {
    manualCloseRef.current = true;
    cleanupAudio();

    const socket = wsRef.current;
    wsRef.current = null;
    if (socket && socket.readyState < WebSocket.CLOSING) {
      socket.close(1000, 'voice session stopped');
    }

    setStatus('stopped');
    appendLog('Voice session stopped by user.');
  }

  useEffect(() => () => {
    manualCloseRef.current = true;
    cleanupAudio();

    const socket = wsRef.current;
    wsRef.current = null;
    if (socket && socket.readyState < WebSocket.CLOSING) {
      socket.close(1000, 'voice page unmounted');
    }
  }, []);

  return {
    status,
    errorMessage,
    actualSampleRateHz,
    sentFrames,
    receivedFrames,
    playoutSequence,
    currentResponseId,
    logs,
    start,
    stop,
  };
}
