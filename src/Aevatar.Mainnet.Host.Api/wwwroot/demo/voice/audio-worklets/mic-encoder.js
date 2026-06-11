const TARGET_SAMPLE_RATE = 24000;
const FRAME_SAMPLES = 480;

class MicEncoder extends AudioWorkletProcessor {
  constructor() {
    super();
    this._frame = new Int16Array(FRAME_SAMPLES);
    this._fill = 0;
    this._readOffset = 0;
    this._levelSum = 0;
    this._levelCount = 0;
  }

  process(inputs) {
    const channel = inputs[0]?.[0];
    if (!channel) return true;

    const ratio = sampleRate / TARGET_SAMPLE_RATE;
    while (this._readOffset < channel.length) {
      const index = Math.floor(this._readOffset);
      const frac = this._readOffset - index;
      const a = channel[index] ?? 0;
      const b = channel[index + 1] ?? a;
      const sample = a + (b - a) * frac;
      this._push(sample);
      this._readOffset += ratio;
    }
    this._readOffset -= channel.length;

    return true;
  }

  _push(sample) {
    const clamped = Math.max(-1, Math.min(1, sample));
    this._frame[this._fill++] = clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff;
    this._levelSum += Math.abs(clamped);
    this._levelCount += 1;

    if (this._fill !== FRAME_SAMPLES) return;

    const out = new Int16Array(this._frame);
    const level = this._levelCount === 0 ? 0 : this._levelSum / this._levelCount;
    this.port.postMessage({ type: "audio", buffer: out.buffer, level }, [out.buffer]);
    this._fill = 0;
    this._levelSum = 0;
    this._levelCount = 0;
  }
}

registerProcessor("aevatar-mic-encoder", MicEncoder);
