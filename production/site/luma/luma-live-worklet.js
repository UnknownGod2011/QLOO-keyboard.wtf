// Pet-only microphone conversion. No dependency on the native Jarvis audio pipeline.
class LumaPcmCapture extends AudioWorkletProcessor {
  constructor() {
    super();
    this.ratio = sampleRate / 16000;
    this.phase = 0;
    this.sum = 0;
    this.count = 0;
    this.samples = new Int16Array(1600); // 100 ms, bounded; provider receives 16 kHz PCM.
    this.offset = 0;
    this.peak = 0;
    this.muted = false;
    this.port.onmessage = ({ data }) => {
      this.muted = Boolean(data?.muted);
      this.phase = this.sum = this.count = this.offset = 0;
      this.peak = 0;
    };
  }
  process(inputs, outputs) {
    // A zero output keeps the processor alive without monitoring the microphone locally.
    for (const channel of outputs[0] || []) channel.fill(0);
    const input = inputs[0]?.[0];
    if (!input || this.muted) return true;
    for (let i = 0; i < input.length; i++) {
      const value = Math.max(-1, Math.min(1, input[i]));
      this.peak = Math.max(this.peak, Math.abs(value));
      this.sum += value;
      this.count++;
      this.phase++;
      if (this.phase < this.ratio) continue;
      const averaged = this.sum / this.count;
      this.samples[this.offset++] = averaged < 0 ? averaged * 32768 : averaged * 32767;
      this.phase -= this.ratio;
      this.sum = this.count = 0;
      if (this.offset === this.samples.length) {
        const bytes = new ArrayBuffer(this.samples.length * 2);
        const view = new DataView(bytes);
        for (let sample = 0; sample < this.samples.length; sample++)
          view.setInt16(sample * 2, this.samples[sample], true);
        this.port.postMessage({ pcm: bytes, peak: this.peak }, [bytes]);
        this.offset = 0;
        this.peak = 0;
      }
    }
    return true;
  }
}
registerProcessor('luma-pcm-capture', LumaPcmCapture);
