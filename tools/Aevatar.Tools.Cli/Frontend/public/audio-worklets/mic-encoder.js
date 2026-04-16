const TARGET_FRAME_SAMPLES = 480; // 20 ms at 24 kHz

class MicEncoder extends AudioWorkletProcessor {
  constructor() {
    super();
    this.buffer = new Int16Array(TARGET_FRAME_SAMPLES);
    this.fill = 0;
  }

  process(inputs) {
    const channel = inputs[0]?.[0];
    if (!channel) {
      return true;
    }

    for (let index = 0; index < channel.length; index += 1) {
      const sample = Math.max(-1, Math.min(1, channel[index]));
      this.buffer[this.fill] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
      this.fill += 1;

      if (this.fill === TARGET_FRAME_SAMPLES) {
        const frame = new Int16Array(this.buffer);
        this.port.postMessage(frame.buffer, [frame.buffer]);
        this.fill = 0;
      }
    }

    return true;
  }
}

registerProcessor('mic-encoder', MicEncoder);
