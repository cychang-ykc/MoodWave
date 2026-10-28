'use strict';
// MoodWave as a macOS MIKU module. MIKU's extension loader (mac/app/main/extensions.js) finds it at
// <MIKU checkout>/Extensions/<name>/miku/ or, installed, at MIKU.app/Contents/Resources/ext/moodchannels/.
//
// The work is done by moodwave-helper (src/MoodChannels.Helper: the same MoodService as the Windows module, on .NET,
// self-contained), started here and spoken to in JSON lines (docs/macos.md). This file only passes things along:
// the library and what plays next to the helper; the page's calls, its answers and events, settings and log lines
// between the helper and MIKU; and the measured loudness, kept here so ReplayGain can ask for it at once.
const fs = require('fs');
const path = require('path');
const readline = require('readline');
const { spawn } = require('child_process');

const SETTINGS = ['enabled', 'threads', 'builtin', 'ai', 'aiSegments', 'aiGpu', 'aiThreads'];   // the module's settings the helper reads (MoodService)
const UPCOMING = 4;                                     // the track playing and the next three go first

let host = null;
let proc = null;
let nextId = 1;
const waiting = new Map();                              // rpc id → resolve
const loudness = new Map();                             // path → [lufs | null (silent), true peak]
let restarts = 0, stopping = false, libTimer = null, lastUpcoming = '';
let listening = false;                                  // MIKU's events subscribed to (once: MIKU has no way to unsubscribe)
let readyAt = 0;                                        // when the helper last said it was ready
const STABLE = 10 * 60 * 1000;                          // a helper that ran this long before ending: its restarts start over

/** The module's root: models/ and web/ are next to this folder in a checkout, inside it when installed. */
function moduleDir() {
  return fs.existsSync(path.join(__dirname, 'models')) ? __dirname : path.join(__dirname, '..');
}

/** The helper for this Mac: MOODWAVE_HELPER, else bin/<arch>/ (a checkout: built by miku/build-helper), else bin/. */
function helperPath() {
  const arch = process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64';
  const candidates = [process.env.MOODWAVE_HELPER, path.join(__dirname, 'bin', arch, 'moodwave-helper'), path.join(__dirname, 'bin', 'moodwave-helper')];
  return candidates.find(p => { if (!p) return false; try { fs.accessSync(p, fs.constants.X_OK); return true; } catch { return false; } }) || null;
}

function send(m) {
  if (proc && !proc.stdin.destroyed) proc.stdin.write(JSON.stringify(m) + '\n');
}

function sendTracks() {
  if (!host) return;
  // what MoodService needs of each track (the store tells an analysed file by its size and modification time)
  const tracks = (host.tracks || []).filter(t => t && t.path && t.id !== 'yt-live').map(t => ({
    id: t.id, path: t.path, album: t.album || '', duration: t.duration || 0, size: t.size || 0, mtime: t.mtime || 0,
    title: t.title || '', artist: t.artist || '',   // for the radio: an artist's tracks, another version of a song
  }));
  send({ op: 'tracks', tracks });
}

function sendUpcoming() {
  if (!host || typeof host.upcoming !== 'function') return;
  const ids = host.upcoming(UPCOMING) || [];
  const playing = !!host.isPlaying;                       // the analysis reads the disk more gently while it is
  const key = ids.join('|') + (playing ? '|>' : '|=');
  if (key === lastUpcoming) return;
  lastUpcoming = key;
  send({ op: 'upcoming', ids, playing });
}

function onLine(line) {
  let m;
  try { m = JSON.parse(line); } catch { host.log('helper: not JSON: ' + line.slice(0, 200)); return; }
  if (!m || typeof m !== 'object') { host.log('helper: not a message: ' + line.slice(0, 200)); return; }
  if (m.id != null && waiting.has(m.id)) {
    const done = waiting.get(m.id);
    waiting.delete(m.id);
    if (m.error != null) host.log(`rpc failed: ${m.error}`);
    done(m.error != null ? null : m.result);
  } else if (m.loudness) {
    for (const [p, lufs, peak] of m.loudness) loudness.set(p, [lufs, peak]);
  } else if (m.ev) host.post(m.ev, m.data);
  else if (m.setting) host.setSetting(m.setting, m.value);
  else if (m.log != null) host.log(m.log);
  else if (m.ready) { readyAt = Date.now(); sendUpcoming(); }
}

function startHelper() {
  const exe = helperPath();
  if (!exe) { host.log('moodwave-helper not found (miku/bin/<arch>/): MoodWave is off'); return; }
  const settings = {};
  for (const k of SETTINGS) { const v = host.getSetting(k); if (v != null) settings[k] = v; }
  const p = proc = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
  lastUpcoming = '';                                    // a new helper knows nothing yet: what plays next goes to it on ready
  p.stdin.on('error', () => { });
  readline.createInterface({ input: p.stdout }).on('line', line => { if (proc === p) onLine(line); });
  let partial = '';
  p.stderr.on('data', d => {
    const lines = (partial + String(d)).split('\n'); partial = lines.pop();
    for (const l of lines) if (l.trim()) host.log('helper: ' + l.slice(0, 2000));
  });
  const ended = why => {
    if (proc !== p) return;
    proc = null;
    for (const done of waiting.values()) done(null);
    waiting.clear();
    if (stopping) return;
    host.log(`moodwave-helper stopped (${why})`);
    // a crash: start again a few times, then leave MoodWave off for this session. One that ends again soon after it
    // started (the same file each time …) uses those up; one that had run a while starts over.
    if (readyAt && Date.now() - readyAt > STABLE) restarts = 0;
    readyAt = 0;
    if (restarts++ < 3) setTimeout(() => { if (!stopping && !proc) { startHelper(); sendTracks(); } }, 2000 * restarts);
  };
  p.on('error', e => ended(e.message));
  p.on('exit', (code, signal) => ended(`exit ${code}${signal ? ' ' + signal : ''}`));
  send({ op: 'start', dataDir: host.dataDir, moduleDir: moduleDir(), ffmpeg: host.ffmpegPath || null, settings });
}

/** A request to the helper answered by id (rpc, continue); null when it has stopped or does not answer in time. */
function ask(message, ms) {
  if (!proc) return Promise.resolve(null);
  const id = nextId++;
  return new Promise(resolve => {
    const timer = setTimeout(() => { if (waiting.delete(id)) resolve(null); }, ms);
    waiting.set(id, r => { clearTimeout(timer); resolve(r); });
    send({ ...message, id });
  });
}

module.exports = {
  id: 'moodchannels',

  start(h) {
    host = h;
    stopping = false;
    startHelper();
    sendTracks();
    // started again after a stop: the events subscribed to then still come (to the host passed now), not twice
    if (listening) return;
    listening = true;
    // a scan changes the library many times in a row: send it once things settle
    if (typeof h.onLibraryChanged === 'function')
      h.onLibraryChanged(() => { if (stopping) return; clearTimeout(libTimer); libTimer = setTimeout(sendTracks, 3000); });
    if (typeof h.onPlaybackChanged === 'function')
      h.onPlaybackChanged(() => { if (!stopping) setTimeout(sendUpcoming, 400); });
    // what played, for the radio and the Smart Playlist (plays, quick skips); an older MIKU has no onTrackPlayed
    if (typeof h.onTrackPlayed === 'function')
      h.onTrackPlayed(p => send({ op: 'played', id: p.id, seconds: p.seconds, duration: p.duration, skipped: !!p.skipped, stopped: !!p.stopped,
        source: p.source || null, back: !!p.back }));
  },

  stop() {
    stopping = true;
    clearTimeout(libTimer);
    const p = proc;
    if (!p) return;
    send({ op: 'stop' });
    setTimeout(() => { try { p.kill(); } catch { } }, 3000);
  },

  handleRpc(method, args) { return ask({ op: 'rpc', method, args: args || {} }, 120000); },

  /**
   * What plays when the queue runs out (the Windows module's IQueueContinuation): MIKU's 「佇列播完後」 handed to
   * MoodWave (the Smart Playlist), or a radio of its own going on. The helper picks (MoodService.Continue).
   */
  continuation: {
    name: 'MoodWave Smart Playlist',
    continue({ played, source, chosen }) {
      return ask({ op: 'continue', played: played || [], source: source || null, chosen: !!chosen }, 10000).then(ids => Array.isArray(ids) ? ids : []);
    },
  },

  /**
   * For MIKU's ReplayGain (the Windows module's ILoudnessProvider): the measured loudness of a file, or null when not
   * measured (yet). lufs is -Infinity for a file measured as silent (nothing to level).
   */
  loudness: {
    name: 'MoodWave',
    lookup(p) {
      const v = loudness.get(p);
      if (!v) return null;
      return v[0] == null ? { lufs: -Infinity, peak: 0 } : { lufs: v[0], peak: v[1] };
    },
  },
};
