const RING_CAPACITY_SAMPLES = 24000 * 4;
const PREROLL_SAMPLES = 1200;
const REARM_EMPTY_RUNS = 40;

class SpeakerDecoder extends AudioWorkletProcessor {
  constructor() {
    super();
    this.ring = new Float32Array(RING_CAPACITY_SAMPLES);
    this.readIndex = 0;
    this.writeIndex = 0;
    this.size = 0;
    this.playing = false;
    this.emptyRuns = 0;

    this.port.onmessage = event => {
      const pcm16 = new Int16Array(event.data);
      this.enqueue(pcm16);
    };
  }

  enqueue(pcm16) {
    for (let index = 0; index < pcm16.length; index += 1) {
      if (this.size >= RING_CAPACITY_SAMPLES) {
        this.readIndex = (this.readIndex + 1) % RING_CAPACITY_SAMPLES;
        this.size -= 1;
      }

      this.ring[this.writeIndex] = pcm16[index] / 0x8000;
      this.writeIndex = (this.writeIndex + 1) % RING_CAPACITY_SAMPLES;
      this.size += 1;
    }
  }

  process(_, outputs) {
    const output = outputs[0]?.[0];
    if (!output) {
      return true;
    }

    if (!this.playing) {
      if (this.size >= PREROLL_SAMPLES) {
        this.playing = true;
        this.emptyRuns = 0;
      } else {
        output.fill(0);
        return true;
      }
    }

    let drained = 0;
    for (let index = 0; index < output.length; index += 1) {
      if (this.size > 0) {
        output[index] = this.ring[this.readIndex];
        this.readIndex = (this.readIndex + 1) % RING_CAPACITY_SAMPLES;
        this.size -= 1;
        drained += 1;
      } else {
        output[index] = 0;
      }
    }

    if (drained === 0) {
      this.emptyRuns += 1;
      if (this.emptyRuns >= REARM_EMPTY_RUNS) {
        this.playing = false;
      }
    } else {
      this.emptyRuns = 0;
      this.port.postMessage({ type: 'drained', count: drained });
    }

    return true;
  }
}

registerProcessor('speaker-decoder', SpeakerDecoder);
