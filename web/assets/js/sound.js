/* =============================================================================
   sound.js — every sound is synthesised with WebAudio, so the app ships with zero
   audio files and still feels alive. Short, soft, tuned to the brand accent's
   mood: a rising third for "sent", a falling fifth for "received", nothing harsh.
   ============================================================================= */

const Sound = (() => {
  let ctx = null;
  let enabled = true;
  let volume = 0.22;

  function ensureContext() {
    if (ctx) return ctx;
    const Ctor = window.AudioContext || window.webkitAudioContext;
    if (!Ctor) return null;
    try {
      ctx = new Ctor();
    } catch {
      ctx = null;
    }
    return ctx;
  }

  /** Browsers require a gesture before audio may start. */
  function unlock() {
    const c = ensureContext();
    if (c && c.state === 'suspended') c.resume().catch(() => {});
  }

  function setEnabled(value) {
    enabled = !!value;
    if (enabled) unlock();
  }

  function isEnabled() {
    return enabled;
  }

  /**
   * One shaped note. `partials` gives the timbre: a sine plus a quiet second
   * harmonic reads as "soft chime" rather than "beep".
   */
  function note(freq, startAt, duration, gain, type = 'sine', partials = 1) {
    const c = ensureContext();
    if (!c) return;

    const osc = c.createOscillator();
    const amp = c.createGain();

    osc.type = type;
    osc.frequency.setValueAtTime(freq, startAt);

    // A gentle glide keeps the attack from clicking.
    osc.frequency.exponentialRampToValueAtTime(freq * partials, startAt + duration);

    amp.gain.setValueAtTime(0.0001, startAt);
    amp.gain.exponentialRampToValueAtTime(gain, startAt + 0.012);
    amp.gain.exponentialRampToValueAtTime(0.0001, startAt + duration);

    osc.connect(amp).connect(c.destination);
    osc.start(startAt);
    osc.stop(startAt + duration + 0.02);
  }

  function play(name) {
    if (!enabled) return;
    const c = ensureContext();
    if (!c) return;
    if (c.state === 'suspended') c.resume().catch(() => {});

    const t = c.currentTime + 0.01;
    const v = volume;

    switch (name) {
      // two-note rise: something left the house
      case 'sent':
        note(523.25, t, 0.09, v * 0.5);
        note(783.99, t + 0.055, 0.13, v * 0.45);
        break;

      // two-note fall: something arrived
      case 'received':
        note(783.99, t, 0.10, v * 0.55);
        note(523.25, t + 0.075, 0.18, v * 0.5);
        break;

      case 'mention':
        note(659.25, t, 0.09, v * 0.5);
        note(880.00, t + 0.07, 0.09, v * 0.5);
        note(1174.66, t + 0.14, 0.20, v * 0.42);
        break;

      case 'open':
        note(392.00, t, 0.07, v * 0.28);
        break;

      case 'pop':
        note(880.00, t, 0.05, v * 0.25, 'triangle');
        break;

      case 'error':
        note(220.00, t, 0.10, v * 0.4, 'sine', 0.94);
        note(196.00, t + 0.10, 0.16, v * 0.35, 'sine', 0.94);
        break;

      case 'send':
        note(1174.66, t, 0.045, v * 0.2, 'triangle');
        break;

      default:
        note(523.25, t, 0.06, v * 0.25);
        break;
    }
  }

  // A very quiet tick while typing a message would be maddening, so instead the
  // recorder gets a soft metronome-free start cue.
  function recordStart() {
    if (!enabled) return;
    const c = ensureContext();
    if (!c) return;
    const t = c.currentTime + 0.01;
    note(659.25, t, 0.06, volume * 0.3, 'sine');
  }

  function recordStop() {
    if (!enabled) return;
    const c = ensureContext();
    if (!c) return;
    const t = c.currentTime + 0.01;
    note(440.00, t, 0.07, volume * 0.3, 'sine');
  }

  return { unlock, setEnabled, isEnabled, play, recordStart, recordStop, setVolume: (v) => { volume = v; } };
})();
