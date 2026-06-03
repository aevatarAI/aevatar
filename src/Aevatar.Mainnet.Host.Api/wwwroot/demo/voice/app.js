const TARGET_SAMPLE_RATE = 24000;
const FRAME_SAMPLES = 480;
const TOKEN_STORAGE_KEYS = [
  "aevatar.nyxid.access_token",
  "nyxid.access_token",
  "nyxid_access_token",
  "access_token",
];
const nyxidAccessToken = resolveNyxIdAccessToken();

const els = {
  endpointMode: document.getElementById("endpointMode"),
  actorField: document.getElementById("actorField"),
  actorId: document.getElementById("actorId"),
  moduleName: document.getElementById("moduleName"),
  channelName: document.getElementById("channelName"),
  sampleRate: document.getElementById("sampleRate"),
  wsUrl: document.getElementById("wsUrl"),
  connectBtn: document.getElementById("connectBtn"),
  muteBtn: document.getElementById("muteBtn"),
  disconnectBtn: document.getElementById("disconnectBtn"),
  connectionState: document.getElementById("connectionState"),
  micState: document.getElementById("micState"),
  sampleRateLabel: document.getElementById("sampleRateLabel"),
  inputMeter: document.getElementById("inputMeter"),
  sentFrames: document.getElementById("sentFrames"),
  receivedFrames: document.getElementById("receivedFrames"),
  log: document.getElementById("log"),
};

const state = {
  ws: null,
  audioContext: null,
  micStream: null,
  micSource: null,
  micNode: null,
  scriptNode: null,
  mutedGain: null,
  isMuted: false,
  nextPlayTime: 0,
  sentFrames: 0,
  receivedFrames: 0,
  inferredResponseId: 0,
  playoutSequence: 0,
  lastAudioAt: 0,
  drainAckTimer: 0,
};

function log(message) {
  const ts = new Date().toISOString().split("T")[1].slice(0, 12);
  const line = `[${ts}] ${message}`;
  els.log.textContent = els.log.textContent === "等待连接..." ? line : `${els.log.textContent}\n${line}`;
  els.log.scrollTop = els.log.scrollHeight;
}

function setPill(el, text, tone = "") {
  el.textContent = text;
  el.className = `pill ${tone}`.trim();
}

function buildWsUrl(options = {}) {
  const params = new URLSearchParams();
  params.set("codec", "pcm16");
  params.set("sample_rate_hz", normalizeSampleRate().toString());
  params.set("mode", "full_duplex");
  params.set("vad_mode", "server");

  const moduleName = els.moduleName.value.trim();
  if (moduleName) params.set("module", moduleName);

  const channel = els.channelName.value.trim();
  if (channel) params.set("channel", channel);
  if (options.includeAccessToken && nyxidAccessToken) {
    params.set("access_token", nyxidAccessToken);
  }

  const scheme = location.protocol === "https:" ? "wss" : "ws";
  if (els.endpointMode.value === "bypass") {
    const actorId = els.actorId.value.trim();
    const encoded = actorId ? encodeURIComponent(actorId) : "{actorId}";
    return `${scheme}://${location.host}/ws/voice/${encoded}?${params}`;
  }

  return `${scheme}://${location.host}/ws/voice?${params}`;
}

function normalizeSampleRate() {
  const parsed = Number.parseInt(els.sampleRate.value, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : TARGET_SAMPLE_RATE;
}

function refreshUrl() {
  els.actorField.classList.toggle("is-hidden", els.endpointMode.value !== "bypass");
  els.wsUrl.value = buildWsUrl();
}

function resolveNyxIdAccessToken() {
  const fromUrl = readTokenFromParams(new URLSearchParams(location.search)) ||
    readTokenFromParams(new URLSearchParams(location.hash.replace(/^#/, "")));
  if (fromUrl) {
    try {
      sessionStorage.setItem(TOKEN_STORAGE_KEYS[0], fromUrl);
    } catch {}
    return fromUrl;
  }

  for (const storage of [sessionStorage, localStorage]) {
    for (const key of TOKEN_STORAGE_KEYS) {
      try {
        const value = storage.getItem(key);
        if (value?.trim()) return value.trim();
      } catch {}
    }
  }

  return "";
}

function readTokenFromParams(params) {
  for (const key of ["access_token", "nyxid_access_token", "token"]) {
    const value = params.get(key);
    if (value?.trim()) return value.trim();
  }

  return "";
}

function redactAccessToken(url) {
  return url.replace(/([?&]access_token=)[^&]+/i, "$1<hidden>");
}

async function connect() {
  if (state.ws) return;

  if (els.endpointMode.value === "bypass" && !els.actorId.value.trim()) {
    log("直连调试模式需要填写 Actor ID。");
    return;
  }

  els.connectBtn.disabled = true;
  setPill(els.connectionState, "连接中", "pill-warn");

  try {
    if (els.endpointMode.value === "policy") {
      await bootstrapVoiceDemo();
    }
    await startAudio();
    openSocket();
  } catch (error) {
    log(`启动失败: ${error.message}`);
    setPill(els.connectionState, "启动失败", "pill-bad");
    await cleanup();
  }
}

async function bootstrapVoiceDemo() {
  log("正在准备 voice demo agent 和路由策略...");
  const headers = {
    "accept": "application/json",
  };
  if (nyxidAccessToken) {
    headers.authorization = `Bearer ${nyxidAccessToken}`;
  }

  const response = await fetch("/api/demo/voice/bootstrap", {
    method: "POST",
    headers,
    credentials: "include",
  });

  let payload = null;
  try {
    payload = await response.json();
  } catch {}

  if (!response.ok) {
    const detail = payload?.detail || payload?.error || response.statusText || "unknown error";
    throw new Error(`voice demo bootstrap 失败: HTTP ${response.status} ${detail}`);
  }

  const actorId = payload?.actor_id || "(unknown actor)";
  const moduleName = payload?.voice_module_name || els.moduleName.value.trim() || "voice_presence_openai";
  if (els.moduleName.value.trim() !== moduleName) {
    els.moduleName.value = moduleName;
    refreshUrl();
  }
  log(`voice demo 已准备好：actor=${actorId} module=${moduleName}`);
}

async function startAudio() {
  log("请求麦克风权限...");
  state.micStream = await navigator.mediaDevices.getUserMedia({
    audio: {
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
      channelCount: 1,
    },
  });

  state.audioContext = new AudioContext({ sampleRate: normalizeSampleRate() });
  state.nextPlayTime = state.audioContext.currentTime + 0.08;
  setPill(els.micState, "麦克风已启动", "pill-ok");
  setPill(
    els.sampleRateLabel,
    `AudioContext ${state.audioContext.sampleRate} Hz`,
    state.audioContext.sampleRate === TARGET_SAMPLE_RATE ? "pill-ok" : "pill-warn");
  log(`AudioContext sampleRate=${state.audioContext.sampleRate} Hz，发送前会转成 PCM16 24kHz。`);

  state.micSource = state.audioContext.createMediaStreamSource(state.micStream);
  state.mutedGain = state.audioContext.createGain();
  state.mutedGain.gain.value = 0;

  if (state.audioContext.audioWorklet) {
    await state.audioContext.audioWorklet.addModule("/demo/voice/audio-worklets/mic-encoder.js");
    state.micNode = new AudioWorkletNode(state.audioContext, "aevatar-mic-encoder");
    state.micNode.port.onmessage = event => {
      if (event.data?.type !== "audio") return;
      els.inputMeter.value = Math.min(1, event.data.level * 8);
      sendAudioFrame(event.data.buffer);
    };
    state.micSource.connect(state.micNode);
    state.micNode.connect(state.mutedGain).connect(state.audioContext.destination);
    log("麦克风 AudioWorklet 已加载，20ms 一帧发送。");
    return;
  }

  startScriptProcessorFallback();
}

function startScriptProcessorFallback() {
  const processor = state.audioContext.createScriptProcessor(2048, 1, 1);
  const resampler = createBlockResampler(state.audioContext.sampleRate, TARGET_SAMPLE_RATE);
  let pending = [];
  let pendingSamples = 0;

  processor.onaudioprocess = event => {
    const input = event.inputBuffer.getChannelData(0);
    const converted = resampler(input);
    pending.push(converted);
    pendingSamples += converted.length;

    const level = converted.reduce((sum, sample) => sum + Math.abs(sample / 0x8000), 0) / Math.max(1, converted.length);
    els.inputMeter.value = Math.min(1, level * 8);

    while (pendingSamples >= FRAME_SAMPLES) {
      const frame = new Int16Array(FRAME_SAMPLES);
      let offset = 0;
      while (offset < FRAME_SAMPLES && pending.length > 0) {
        const head = pending[0];
        const take = Math.min(FRAME_SAMPLES - offset, head.length);
        frame.set(head.subarray(0, take), offset);
        offset += take;
        if (take === head.length) {
          pending.shift();
        } else {
          pending[0] = head.subarray(take);
        }
      }
      pendingSamples -= FRAME_SAMPLES;
      sendAudioFrame(frame.buffer);
    }
  };

  state.scriptNode = processor;
  state.micSource.connect(processor);
  processor.connect(state.mutedGain).connect(state.audioContext.destination);
  log("当前浏览器不支持 AudioWorklet，已启用 ScriptProcessor fallback。");
}

function createBlockResampler(fromRate, toRate) {
  let readOffset = 0;
  const ratio = fromRate / toRate;
  return input => {
    const out = [];
    while (readOffset < input.length) {
      const index = Math.floor(readOffset);
      const frac = readOffset - index;
      const a = input[index] ?? 0;
      const b = input[index + 1] ?? a;
      const sample = Math.max(-1, Math.min(1, a + (b - a) * frac));
      out.push(sample < 0 ? sample * 0x8000 : sample * 0x7fff);
      readOffset += ratio;
    }
    readOffset -= input.length;
    return Int16Array.from(out);
  };
}

function openSocket() {
  const url = buildWsUrl({ includeAccessToken: true });
  log(`连接 ${redactAccessToken(url)}`);
  const ws = new WebSocket(url);
  ws.binaryType = "arraybuffer";
  state.ws = ws;

  ws.onopen = () => {
    setPill(els.connectionState, "已连接", "pill-ok");
    els.connectBtn.disabled = true;
    els.disconnectBtn.disabled = false;
    els.muteBtn.disabled = false;
    log("WebSocket 已连接。现在可以直接说话。");
  };

  ws.onmessage = event => {
    if (typeof event.data === "string") {
      log(`control: ${event.data}`);
      return;
    }

    state.receivedFrames += 1;
    els.receivedFrames.textContent = state.receivedFrames.toString();
    playPcm16(event.data);
  };

  ws.onerror = () => {
    setPill(els.connectionState, "连接错误", "pill-bad");
    log("WebSocket error。常见原因：未登录、路由策略没配、目标 agent 没有 voice module、NyxID proxy 授权未就绪。");
  };

  ws.onclose = event => {
    const reason = event.reason ? ` reason=${event.reason}` : "";
    log(`WebSocket 已关闭 code=${event.code}${reason}`);
    setPill(els.connectionState, "已关闭", event.code === 1000 ? "" : "pill-warn");
    void cleanup({ keepLog: true });
  };
}

function sendAudioFrame(buffer) {
  if (state.isMuted) return;
  if (state.ws?.readyState !== WebSocket.OPEN) return;

  state.ws.send(buffer);
  state.sentFrames += 1;
  els.sentFrames.textContent = state.sentFrames.toString();
}

function playPcm16(arrayBuffer) {
  if (!state.audioContext || arrayBuffer.byteLength === 0) return;

  const nowMs = performance.now();
  if (nowMs - state.lastAudioAt > 900) {
    state.inferredResponseId += 1;
    state.playoutSequence = 0;
    log(`检测到新回复音频，推断 responseId=${state.inferredResponseId}`);
  }
  state.lastAudioAt = nowMs;

  const pcm16 = new Int16Array(arrayBuffer);
  const float32 = new Float32Array(pcm16.length);
  for (let i = 0; i < pcm16.length; i += 1) {
    float32[i] = Math.max(-1, Math.min(1, pcm16[i] / 0x8000));
  }

  const buffer = state.audioContext.createBuffer(1, float32.length, TARGET_SAMPLE_RATE);
  buffer.copyToChannel(float32, 0);

  const source = state.audioContext.createBufferSource();
  source.buffer = buffer;
  source.connect(state.audioContext.destination);

  const now = state.audioContext.currentTime;
  if (state.nextPlayTime < now + 0.02) {
    state.nextPlayTime = now + 0.05;
  }

  source.start(state.nextPlayTime);
  state.nextPlayTime += buffer.duration;
  state.playoutSequence += pcm16.length;

  window.clearTimeout(state.drainAckTimer);
  state.drainAckTimer = window.setTimeout(sendDrainAck, 450);
}

function sendDrainAck() {
  if (state.ws?.readyState !== WebSocket.OPEN) return;
  if (state.inferredResponseId <= 0) return;

  const payload = {
    drainAcknowledged: {
      responseId: state.inferredResponseId,
      playoutSequence: state.playoutSequence,
    },
  };
  state.ws.send(JSON.stringify(payload));
  log(`发送 drain ack responseId=${state.inferredResponseId} playoutSequence=${state.playoutSequence}`);
}

function toggleMute() {
  state.isMuted = !state.isMuted;
  els.muteBtn.textContent = state.isMuted ? "取消静音" : "静音";
  setPill(els.micState, state.isMuted ? "麦克风已静音" : "麦克风已启动", state.isMuted ? "pill-warn" : "pill-ok");
}

async function disconnect() {
  log("手动断开。");
  if (state.ws?.readyState === WebSocket.OPEN) {
    state.ws.close(1000, "client disconnect");
  }
  await cleanup({ keepLog: true });
}

async function cleanup() {
  window.clearTimeout(state.drainAckTimer);

  if (state.micNode) {
    try { state.micNode.disconnect(); } catch {}
    state.micNode = null;
  }
  if (state.scriptNode) {
    try { state.scriptNode.disconnect(); } catch {}
    state.scriptNode = null;
  }
  if (state.micSource) {
    try { state.micSource.disconnect(); } catch {}
    state.micSource = null;
  }
  if (state.mutedGain) {
    try { state.mutedGain.disconnect(); } catch {}
    state.mutedGain = null;
  }
  if (state.micStream) {
    state.micStream.getTracks().forEach(track => track.stop());
    state.micStream = null;
  }
  if (state.audioContext) {
    try { await state.audioContext.close(); } catch {}
    state.audioContext = null;
  }

  state.ws = null;
  state.isMuted = false;
  state.nextPlayTime = 0;
  state.playoutSequence = 0;
  state.lastAudioAt = 0;

  els.connectBtn.disabled = false;
  els.disconnectBtn.disabled = true;
  els.muteBtn.disabled = true;
  els.muteBtn.textContent = "静音";
  els.inputMeter.value = 0;
  setPill(els.micState, "麦克风未启动");
}

els.endpointMode.addEventListener("change", refreshUrl);
els.actorId.addEventListener("input", refreshUrl);
els.moduleName.addEventListener("input", refreshUrl);
els.channelName.addEventListener("input", refreshUrl);
els.sampleRate.addEventListener("input", refreshUrl);
els.connectBtn.addEventListener("click", connect);
els.disconnectBtn.addEventListener("click", disconnect);
els.muteBtn.addEventListener("click", toggleMute);

refreshUrl();
