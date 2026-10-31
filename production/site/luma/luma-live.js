/**
 * Isolated pet-only Live session. No permanent key, Jarvis instance, native audio, actions,
 * billing owner or arbitrary Settings context is accepted here. Tokens are single-use,
 * constrained and minted by the protected surface adapter. All retries require another click.
 */
export const LUMA_LIVE_SOCKET = 'wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContentConstrained';
const MODELS = new Set(['gemini-3.8-live', 'gemini-3.1-flash-live-preview']);
const MAX_MESSAGE_BYTES = 2 * 1024 * 1024;
const MAX_AUDIO_CHUNK_BYTES = 256 * 1024;
const CAPTION_LIMIT = 2000;

function abortable(promise, signal) {
  return new Promise((resolve, reject) => {
    const operation = Promise.resolve(promise);
    const aborted = () => reject(new DOMException('Luma voice stopped.', 'AbortError'));
    if (signal.aborted) { operation.catch(() => {}); aborted(); return; }
    signal.addEventListener('abort', aborted, { once: true });
    operation.then(value => {
      signal.removeEventListener('abort', aborted);
      if (!signal.aborted) resolve(value);
    }, error => { signal.removeEventListener('abort', aborted); reject(error); });
  });
}

export function validateLumaGrant(grant, now = Date.now()) {
  const expires = Date.parse(grant?.expiresAt);
  const newSessionExpires = Date.parse(grant?.newSessionExpiresAt);
  if (typeof grant?.token !== 'string' || !grant.token.startsWith('auth_tokens/')
      || grant.token.length < 16 || grant.token.length > 4096 || /\s/.test(grant.token)
      || !MODELS.has(grant.model) || grant.websocketUrl !== LUMA_LIVE_SOCKET
      || !Number.isFinite(expires) || expires <= now || expires > now + 301000
      || !Number.isFinite(newSessionExpires) || newSessionExpires <= now || newSessionExpires > expires
      || !Number.isInteger(grant.sessionSeconds) || grant.sessionSeconds < 1 || grant.sessionSeconds > 300)
    throw new Error('Luma voice is unavailable. The tour and text help still work.');
  return { ...grant, expires, newSessionExpires };
}

export function encodePcmBase64(buffer, environment = globalThis) {
  if (!(buffer instanceof ArrayBuffer) || buffer.byteLength > 6400 || buffer.byteLength % 2)
    throw new Error('Invalid pet microphone frame.');
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset++) binary += String.fromCharCode(bytes[offset]);
  return environment.btoa(binary);
}

export function createLumaVoiceAdapter({ requestToken, canStart = async () => true,
  maxSessionMs = 300000, environment = globalThis } = {}) {
  let owned = null;
  let sequence = 0;
  let currentState = 'idle';
  let permanentlyDisposed = false;
  const setState = (session, state) => {
    if (owned !== session) return;
    currentState = state;
    session.callbacks.onState?.(state);
  };
  const clearPlayback = session => {
    for (const source of session.sources) {
      source.onended = null;
      try { source.stop(); } catch { /* Already ended. */ }
      try { source.disconnect(); } catch { /* Already disconnected. */ }
    }
    session.sources.clear();
    session.nextAudio = 0;
  };
  const release = (session, notify = true) => {
    if (!session || session.closed) return;
    session.closed = true;
    session.rejectReady?.(new Error('Luma voice stopped.'));
    session.resolveReady = session.rejectReady = null;
    session.abort.abort();
    environment.clearTimeout(session.setupTimer);
    environment.clearTimeout(session.connectionTimer);
    environment.clearTimeout(session.expiryTimer);
    environment.clearInterval(session.healthTimer);
    clearPlayback(session);
    session.processor?.port?.postMessage({ muted: true });
    if (session.processor?.port) session.processor.port.onmessage = null;
    try { session.processor?.disconnect(); } catch { /* Already disconnected. */ }
    try { session.microphoneSource?.disconnect(); } catch { /* Already disconnected. */ }
    for (const track of session.stream?.getTracks?.() || []) track.stop();
    const socket = session.socket;
    if (socket) {
      socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
      try { socket.close(1000, 'Luma session ended'); } catch { /* Connecting/closed. */ }
    }
    session.stream = session.socket = session.processor = session.microphoneSource = null;
    session.audio?.close?.().catch?.(() => {});
    session.audio = null;
    if (owned === session) {
      owned = null;
      currentState = 'idle';
      if (notify) {
        session.callbacks.onState?.('idle');
        session.callbacks.onStop?.();
      }
    }
  };
  const fail = (session, message) => {
    if (owned !== session || session.closed) return;
    const callbacks = session.callbacks;
    release(session);
    callbacks.onError?.(message); // Safe application text, never provider body/token/close reason.
  };
  const send = (session, message) => {
    if (owned !== session || session.closed || session.socket?.readyState !== 1) return false;
    if (session.socket.bufferedAmount > 128 * 1024) {
      fail(session, 'Luma’s connection is too slow. Please try again.');
      return false;
    }
    try { session.socket.send(JSON.stringify(message)); return true; }
    catch { fail(session, 'Luma’s Live connection was interrupted. Please try again.'); return false; }
  };
  const play = (session, data, mimeType) => {
    if (typeof data !== 'string' || data.length > MAX_AUDIO_CHUNK_BYTES * 1.4
      || !/^audio\/pcm(?:;rate=24000)?$/.test(mimeType || ''))
      throw new Error('Unsupported pet audio.');
    const binary = environment.atob(data);
    if (!binary.length || binary.length > MAX_AUDIO_CHUNK_BYTES || binary.length % 2)
      throw new Error('Invalid pet audio.');
    if (session.sources.size >= 64 || session.nextAudio - session.audio.currentTime > 20)
      throw new Error('Pet audio queue is full.');
    const samples = binary.length / 2;
    const buffer = session.audio.createBuffer(1, samples, 24000);
    const output = buffer.getChannelData(0);
    for (let i = 0; i < samples; i++) {
      const value = binary.charCodeAt(i * 2) | (binary.charCodeAt(i * 2 + 1) << 8);
      output[i] = (value >= 32768 ? value - 65536 : value) / 32768;
    }
    const source = session.audio.createBufferSource();
    source.buffer = buffer;
    source.connect(session.gain);
    const startsAt = Math.max(session.audio.currentTime + .025, session.nextAudio);
    session.nextAudio = startsAt + buffer.duration;
    session.sources.add(source);
    source.onended = () => {
      source.disconnect();
      session.sources.delete(source);
      if (owned === session && !session.sources.size && session.turnComplete) setState(session, 'listening');
    };
    source.start(startsAt);
    setState(session, 'speaking');
  };
  const publishCaption = (session, text, role, source = 'transcription') => {
    if (!text) return;
    const key = role === 'user' ? 'userCaption' : 'assistantCaption';
    if (role === 'assistant' && session.captionSource && session.captionSource !== source) return;
    if (role === 'assistant') session.captionSource = source;
    session[key] = (session[key] + text).slice(0, CAPTION_LIMIT);
    session.callbacks.onCaption?.(session[key], role);
  };
  const receive = async (session, event) => {
    if (owned !== session || session.closed) return;
    try {
      const text = typeof event.data === 'string' ? event.data
        : event.data instanceof ArrayBuffer ? new TextDecoder().decode(event.data)
        : event.data?.size <= MAX_MESSAGE_BYTES ? await event.data.text() : null;
      if (owned !== session || session.closed) return;
      if (typeof text !== 'string' || text.length > MAX_MESSAGE_BYTES) throw new Error('Pet response too large.');
      const message = JSON.parse(text);
      if (!message || typeof message !== 'object' || Array.isArray(message)) throw new Error('Invalid pet response.');
      if (message.error || message.toolCall) {
        fail(session, 'Luma voice cannot complete that request. The tour and text help still work.');
        return;
      }
      if (message.goAway) {
        fail(session, 'Luma’s short Live session is ending. Click Voice to reconnect.');
        return;
      }
      if (message.setupComplete && !session.ready) {
        session.ready = true;
        environment.clearTimeout(session.setupTimer);
        environment.clearTimeout(session.connectionTimer);
        session.lastUseful = Date.now();
        setState(session, 'listening');
        // Exactly one greeting, only after setup. Never replay after close/interruption.
        send(session, { clientContent: { turns: [{ role: 'user', parts: [{
          text: 'Greet me briefly, introduce yourself as my keyboard.wtf guide, and invite a product question.',
        }] }], turnComplete: true } });
        session.turnPending = true;
        session.lastSpeech = Date.now();
        session.resolveReady?.();
        session.resolveReady = session.rejectReady = null;
      }
      const content = message.serverContent;
      if (!content || typeof content !== 'object') return;
      if (content.interrupted) {
        clearPlayback(session);
        session.userCaption = session.assistantCaption = session.captionSource = '';
        session.captionTurnEnded = true;
        session.turnPending = false;
        session.turnComplete = true;
        setState(session, 'listening');
        // Never enqueue audio from the interrupted generation after clearing its queue.
        return;
      }
      if (typeof content.inputTranscription?.text === 'string') {
        if (session.captionTurnEnded) {
          session.userCaption = session.assistantCaption = session.captionSource = '';
          session.captionTurnEnded = false;
        }
        publishCaption(session, content.inputTranscription.text, 'user');
        session.turnPending = true;
        session.turnComplete = false;
        session.lastSpeech = Date.now();
        if (!session.sources.size) setState(session, 'thinking');
      }
      if (typeof content.outputTranscription?.text === 'string') {
        if (session.captionTurnEnded) {
          session.userCaption = session.assistantCaption = session.captionSource = '';
          session.captionTurnEnded = false;
        }
        publishCaption(session, content.outputTranscription.text, 'assistant');
        session.lastUseful = Date.now();
      }
      const parts = content.modelTurn?.parts;
      if (parts && (!Array.isArray(parts) || parts.length > 64)) throw new Error('Invalid pet response.');
      for (const part of parts || []) {
        if (typeof part?.text === 'string') {
          publishCaption(session, part.text, 'assistant', 'text');
          session.lastUseful = Date.now();
        }
        if (part?.inlineData) {
          play(session, part.inlineData.data, part.inlineData.mimeType);
          session.lastUseful = Date.now();
        }
      }
      if (content.turnComplete) {
        session.captionTurnEnded = true;
        session.turnComplete = true;
        session.turnPending = false;
        if (!session.sources.size) setState(session, 'listening');
      }
    } catch {
      fail(session, 'Luma voice received an incomplete response. Please try again.');
    }
  };
  const adapter = {
    async start(callbacks = {}) {
      if (permanentlyDisposed) throw new Error('Luma voice has been closed.');
      if (owned) return; // No duplicate session/token/microphone on repeated click.
      const session = {
        id: ++sequence, callbacks, abort: new AbortController(), closed: false, muted: false,
        sources: new Set(), nextAudio: 0, ready: false, turnComplete: false,
        turnPending: false, lastSpeech: 0, lastUseful: Date.now(), checking: false,
        userCaption: '', assistantCaption: '', captionSource: '', captionTurnEnded: false,
      };
      owned = session;
      setState(session, 'connecting');
      session.connectionTimer = environment.setTimeout(() => fail(session,
        'Luma voice could not finish connecting. Please try again.'), 30000);
      try {
        if (typeof requestToken !== 'function')
          throw new Error('Luma voice is not configured yet. The tour and text help still work.');
        if (environment.document?.hidden)
          throw new Error('Return to this page before starting Luma voice.');
        const AudioContext = environment.AudioContext || environment.webkitAudioContext;
        if (!AudioContext || !environment.AudioWorkletNode || !environment.navigator?.mediaDevices?.getUserMedia)
          throw new Error('This browser cannot start Live voice. The tour and text help still work.');
        session.audio = new AudioContext();
        // Activate output on the click itself, before the availability check can
        // cross a network turn and lose browser user activation. This acquires no
        // microphone or token; a denied check closes this silent context.
        const outputReady = abortable(session.audio.resume(), session.abort.signal);
        const [permitted] = await Promise.all([
          abortable(Promise.resolve().then(() => canStart({ signal: session.abort.signal, phase: 'start' })), session.abort.signal), outputReady,
        ]);
        if (!permitted) throw new Error('Finish the active voice task before talking with Luma.');
        if (owned !== session) return;
        if (session.audio.state !== 'running')
          throw new Error('Luma audio could not start. Click Voice to try again.');
        const capture = environment.navigator.mediaDevices.getUserMedia({
          audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }, video: false,
        });
        // Permission prompts cannot be cancelled by Web APIs; late approval must never leave a mic open.
        capture.then(lateStream => {
          if (owned !== session || session.closed) lateStream.getTracks().forEach(track => track.stop());
        }, () => {});
        const stream = await abortable(capture, session.abort.signal);
        if (owned !== session || session.closed) { stream.getTracks().forEach(track => track.stop()); return; }
        session.stream = stream;
        await abortable(session.audio.audioWorklet.addModule(new URL('./luma-live-worklet.js', import.meta.url)), session.abort.signal);
        if (owned !== session) return;
        if (!await abortable(canStart({ signal: session.abort.signal, phase: 'start' }), session.abort.signal))
          throw new Error('Another voice task is active. Luma did not take it over.');
        const grant = validateLumaGrant(await abortable(requestToken({ signal: session.abort.signal }), session.abort.signal));
        if (owned !== session || session.closed) return;
        session.gain = session.audio.createGain();
        session.gain.gain.value = 1; // Full pet output; never change device/system volume.
        session.gain.connect(session.audio.destination);
        session.microphoneSource = session.audio.createMediaStreamSource(stream);
        session.processor = new environment.AudioWorkletNode(session.audio, 'luma-pcm-capture');
        session.microphoneSource.connect(session.processor);
        session.processor.connect(session.audio.destination);
        session.processor.port.onmessage = ({ data }) => {
          if (owned !== session || session.closed || session.muted || !session.ready) return;
          try {
            if (data.peak > .025) {
              session.lastSpeech = Date.now();
              session.turnPending = true;
            }
            send(session, { realtimeInput: { audio: { mimeType: 'audio/pcm;rate=16000',
              data: encodePcmBase64(data.pcm, environment) } } });
          } catch {
            fail(session, 'Luma voice could not read its microphone stream. Please try again.');
          }
        };
        const socket = new environment.WebSocket(LUMA_LIVE_SOCKET + '?access_token=' + encodeURIComponent(grant.token));
        session.socket = socket;
        session.expiryTimer = environment.setTimeout(() => fail(session,
          'Luma’s short voice session ended. Click Voice to talk again.'),
          Math.min(maxSessionMs, grant.expires - Date.now(), grant.sessionSeconds * 1000));
        const ready = new Promise((resolve, reject) => { session.resolveReady = resolve; session.rejectReady = reject; });
        session.setupTimer = environment.setTimeout(() => {
          session.rejectReady?.(new Error('Luma voice could not connect. Please try again.'));
          fail(session, 'Luma voice could not connect. Please try again.');
        }, 10000);
        socket.onopen = () => send(session, { setup: { model: 'models/' + grant.model,
          generationConfig: { responseModalities: ['AUDIO'] }, outputAudioTranscription: {} } });
        // Promise chain preserves order even when Blob decoding is asynchronous.
        session.receiveChain = Promise.resolve();
        socket.onmessage = event => { session.receiveChain = session.receiveChain.then(() => receive(session, event)); };
        socket.onerror = () => {
          session.rejectReady?.(new Error('Luma’s Live connection was interrupted. Please try again.'));
          fail(session, 'Luma’s Live connection was interrupted. Please try again.');
        };
        socket.onclose = () => {
          session.rejectReady?.(new Error('Luma’s Live connection was interrupted. Please try again.'));
          fail(session, 'Luma’s Live connection was interrupted. Please try again.');
        };
        session.healthTimer = environment.setInterval(async () => {
          if (owned !== session || session.closed || session.checking) return;
          if (session.turnPending && !session.sources.size && Date.now() - Math.max(session.lastUseful, session.lastSpeech) > 30000) {
            fail(session, 'Luma didn’t receive a response. Please try again.'); return;
          }
          session.checking = true;
          try {
            if (!await canStart({ signal: session.abort.signal, phase: 'health' }))
              fail(session, 'Luma paused so your active voice task can use the microphone.');
          } catch {
            if (owned === session) fail(session, 'Luma voice could not verify its session. Please try again.');
          } finally { session.checking = false; }
        }, 1000);
        await ready;
      } catch (error) {
        if (owned !== session || session.closed) return;
        const message = error?.name === 'NotAllowedError'
          ? 'Microphone permission was not granted. The tour and text help still work.'
          : error?.name === 'NotFoundError' ? 'No microphone was found. The tour and text help still work.'
          : typeof error?.message === 'string' && /^Luma|^Finish|^Another|^This browser|^Return/.test(error.message)
            ? error.message : 'Luma voice is temporarily unavailable. The tour and text help still work.';
        fail(session, message);
      }
    },
    stop() {
      const session = owned;
      session?.rejectReady?.(new Error('Luma voice stopped.'));
      release(session);
    },
    setMuted(muted) {
      const session = owned;
      if (!session || session.closed) return;
      session.muted = Boolean(muted);
      for (const track of session.stream?.getAudioTracks?.() || []) track.enabled = !session.muted;
      session.processor?.port?.postMessage({ muted: session.muted });
      if (session.gain) session.gain.gain.value = session.muted ? 0 : 1;
      if (session.muted && session.ready) send(session, { realtimeInput: { audioStreamEnd: true } });
    },
    getState() { return currentState; },
    dispose() {
      adapter.stop();
      permanentlyDisposed = true;
      environment.document?.removeEventListener('visibilitychange', visibility);
      environment.removeEventListener?.('pagehide', pagehide);
    },
  };
  const visibility = () => { if (environment.document?.hidden) adapter.stop(); };
  const pagehide = () => adapter.stop();
  environment.document?.addEventListener('visibilitychange', visibility);
  environment.addEventListener?.('pagehide', pagehide);
  return adapter;
}
