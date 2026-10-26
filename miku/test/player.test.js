'use strict';
// MIKU macOS's queue with a module's continuation, read from the MIKU checkout itself (mac/app/main/player.js) and run
// against a stand-in audio engine and library: what plays when the queue runs out (random songs or albums, the
// module's picks when 「佇列播完後」 is handed to it, a radio of the module's going on by itself), where the Smart
// Playlist's part of the queue starts and how it follows adding, removing, moving, clearing, a library change and a
// restart, and the 'played' reports a module gets (left, skipped, stopped, with the queue's source of the time).
//
//   node miku/test/player.test.js --miku <MIKU checkout>
const fs = require('fs');
const path = require('path');
const Module = require('module');
const { EventEmitter } = require('events');
const T = require('./lib');
const { check, group, sleep } = T;

const o = T.options({ miku: path.join(require('os').homedir(), 'daphne-test', 'miku') });
const main = path.join(path.resolve(o.miku), 'mac', 'app', 'main');
if (!fs.existsSync(path.join(main, 'player.js'))) { console.error(`no MIKU checkout at ${o.miku} (--miku)`); process.exit(2); }

const errors = [];
const stubs = { './common': { Log: { info() { }, warn() { }, error: (...a) => errors.push(a.join(' ')) } } };
const load0 = Module._load;
Module._load = function (req, parent, ...rest) {
  if (parent && parent.filename && parent.filename.startsWith(main) && stubs[req]) return stubs[req];
  return load0.call(this, req, parent, ...rest);
};
const { Player } = require(path.join(main, 'player.js'));

// the library: 4 albums of 5 tracks, plus the live stream
const lib = {
  tracks: new Map(),
  albums: [],
  getTrack(id) { return this.tracks.get(id) || null; },
  albumList() { return this.albums; },
};
for (let a = 1; a <= 4; a++) {
  const al = { id: 'al' + a, tracks: [] };
  for (let k = 1; k <= 5; k++) {
    const t = { id: `a${a}t${k}`, title: `A${a} T${k}`, albumId: al.id, duration: 200 };
    lib.tracks.set(t.id, t); al.tracks.push(t);
  }
  lib.albums.push(al);
}
lib.tracks.set('yt-live', { id: 'yt-live', title: 'live', duration: 0 });
const ids = n => [...lib.tracks.keys()].filter(id => id !== 'yt-live').slice(0, n);

/** What the player needs of the audio engine. */
class Engine extends EventEmitter {
  constructor() { super(); this.isLoaded = false; this.isPlaying = false; this.position = 0; this.track = null; }
  async load(t, pos, play) { this.track = t; this.isLoaded = true; this.isPlaying = !!play; this.position = pos || 0; }
  invalidateNext() { }
  stop() { this.isPlaying = false; }
}

function make(s = {}) {
  const engine = new Engine();
  const p = new Player(engine, lib, { repeat: 'off', autoContinue: 'tracks', ...s });
  const played = [];
  p.on('played', (t, seconds, skipped, stopped, source) => played.push({ id: t.id, seconds, skipped, stopped, source }));
  return { p, engine, played };
}

/** A module's continuation as main.js hands it to the player (extensions.js continuation()), recording each ask. */
function module(answer) {
  const asks = [];
  return { asks, c: { id: 'moodchannels', name: 'MoodWave Smart Playlist', ask: (played, source, chosen) => { asks.push({ played, source, chosen }); return answer(played, source, chosen); } } };
}
const tick = () => sleep(20);

(async () => {
  console.log(`player  ${path.join(main, 'player.js')}`);

  await group('the queue runs out, no module', async () => {
    const { p } = make({ autoContinue: 'tracks' });
    await p.playList(ids(3), 2, false);
    check('random songs added after the last one', p.queue.length === 8, p.queue.length);
    check('none of the queue again', p.queue.slice(3).every(id => !ids(3).includes(id)), p.queue.slice(3));
    const b = make({ autoContinue: 'albums' });
    await b.p.playList(['a1t1'], 0, false);
    check('albums: a whole other album', b.p.queue.length === 6 && new Set(b.p.queue.slice(1).map(id => lib.getTrack(id).albumId)).size === 1 &&
      lib.getTrack(b.p.queue[1]).albumId !== 'al1', b.p.queue);
    const off = make({ autoContinue: 'off' });
    await off.p.playList(ids(2), 1, false);
    check('off: nothing added', off.p.queue.length === 2, off.p.queue.length);
    const rep = make({ autoContinue: 'tracks', repeat: 'all' });
    await rep.p.playList(ids(2), 1, false);
    check('repeat on: nothing added', rep.p.queue.length === 2, rep.p.queue.length);
  });

  await group('「佇列播完後」 handed to the module (the Smart Playlist)', async () => {
    const m = module(() => Promise.resolve(['a4t1', 'a4t2', 'nope', 'a4t3']));
    const { p } = make({ autoContinue: 'similar' });
    p.continuation = m.c;
    await p.playList(['a1t1', 'a1t2', 'a2t1'], 0, false);
    check('not asked before the last track', m.asks.length === 0, m.asks.length);
    await p.next(); await p.next(); await tick();
    check('asked once at the last track', m.asks.length === 1, m.asks.length);
    check('with what played, the source and chosen', m.asks[0] && m.asks[0].chosen === true && m.asks[0].played.join() === 'a1t1,a1t2,a2t1' && m.asks[0].source === null, m.asks[0]);
    check('its picks added, unknown ids left out', p.queue.slice(3).join() === 'a4t1,a4t2,a4t3', p.queue);
    check('its part starts after the list', p.continuedFrom === 3, p.continuedFrom);
    check('kept with the queue (settings)', p.s.queueContinuedFrom === 3, p.s.queueContinuedFrom);
    // the next asks go on in the Smart Playlist: its start stays where it was
    await p.next(); await p.next(); await p.next(); await tick();
    check('asked again at its own end; the start stays', m.asks.length === 2 && p.continuedFrom === 3 && p.queue.length === 9, { asks: m.asks.length, from: p.continuedFrom, n: p.queue.length });
    check('it is told the last 50 at most', m.asks[1].played.length <= 50 && m.asks[1].played[m.asks[1].played.length - 1] === p.queue[5], m.asks[1].played.length);
  });

  await group('the module has nothing, fails or is slow', async () => {
    let m = module(() => Promise.resolve([]));
    let r = make({ autoContinue: 'similar' }); r.p.continuation = m.c;
    await r.p.playList(['a1t1'], 0, false); await tick();
    check('nothing: random songs instead, no Smart Playlist part', r.p.queue.length === 6 && r.p.continuedFrom === -1, { n: r.p.queue.length, from: r.p.continuedFrom });
    m = module(() => Promise.reject(new Error('boom')));
    r = make({ autoContinue: 'similar' }); r.p.continuation = m.c;
    const n = errors.length;
    await r.p.playList(['a1t1'], 0, false); await tick();
    check('an error: logged, random songs instead', r.p.queue.length === 6 && errors.length === n + 1, { n: r.p.queue.length, logged: errors.slice(n) });
    m = module(() => new Promise(res => setTimeout(() => res(['a4t1']), 200)));
    r = make({ autoContinue: 'similar' }); r.p.continuation = m.c;
    await r.p.playList(['a1t1', 'a1t2'], 1, false);
    r.p.add(['a3t3']);                                     // the queue changes while the module thinks
    await sleep(300);
    check('the queue changed meanwhile: its answer dropped', r.p.queue.join() === 'a1t1,a1t2,a3t3' && !r.p.asking, r.p.queue);
    m = module(() => new Promise(() => { }));
    r = make({ autoContinue: 'similar' }); r.p.continuation = m.c;
    await r.p.playList(['a1t1'], 0, false);
    r.p.ensureAutoNext(); r.p.ensureAutoNext();
    check('one ask at a time', m.asks.length === 1, m.asks.length);
  });

  await group('a radio of the module goes on by itself', async () => {
    const m = module(() => Promise.resolve(['a3t1', 'a3t2']));
    const src = { ext: 'moodchannels', radio: { kind: 'track', name: 'x', seeds: ['a1t1'] } };
    const { p } = make({ autoContinue: 'off' }); p.continuation = m.c;
    await p.playList(['a1t1', 'a2t2'], 1, false, 0, src); await tick();
    check('asked though 「佇列播完後」 is off, chosen false, with the radio as source', m.asks.length === 1 && m.asks[0].chosen === false && m.asks[0].source === src, m.asks[0]);
    check('added, but no Smart Playlist part (it is all the radio)', p.queue.length === 4 && p.continuedFrom === -1, { n: p.queue.length, from: p.continuedFrom });
    const other = make({ autoContinue: 'off' }); other.p.continuation = m.c;
    await other.p.playList(['a1t1'], 0, false, 0, { ext: 'someone-else' }); await tick();
    check('another module\'s list: not asked', m.asks.length === 1 && other.p.queue.length === 1, m.asks.length);
  });

  await group('a pick taken out (a 👎 in the queue): asked again at once', async () => {
    let n = 0;
    const m = module(() => Promise.resolve([['a3t1'], ['a3t2'], ['a3t3']][n++] || []));
    const src = { ext: 'moodchannels', radio: { kind: 'track', name: 'x', seeds: ['a1t1'] } };
    const { p } = make({ autoContinue: 'off' }); p.continuation = m.c;
    await p.playList(['a1t1', 'a2t2'], 1, false, 0, src); await tick();
    check('one pick after the track playing', p.queue.join() === 'a1t1,a2t2,a3t1' && m.asks.length === 1, p.queue);
    p.remove(2); await tick();
    check('taken out: asked again while the track plays, the new pick added', m.asks.length === 2 && p.queue.join() === 'a1t1,a2t2,a3t2', { asks: m.asks.length, q: p.queue });
    p.add(['a4t4']);
    p.remove(3); await tick();
    check('a track taken out that leaves another after the one playing: not asked', m.asks.length === 2, m.asks.length);
    // paused: the pick comes back at once too (not only when the track is over after the listener plays on)
    p.engine.isPlaying = false;
    p.remove(2); await tick();
    check('taken out while paused: asked again, the new pick added', m.asks.length === 3 && p.queue.join() === 'a1t1,a2t2,a3t3', { asks: m.asks.length, q: p.queue });
    p.engine.isPlaying = true;
    // random songs (「佇列播完後」 random) do not come back at once when the listener takes the last one out
    const r = make({ autoContinue: 'tracks' });
    await r.p.playList(['a1t1', 'a1t2'], 0, false);
    r.p.remove(1);
    check('no module: nothing added on taking the last one out', r.p.queue.join() === 'a1t1', r.p.queue);
    // the queue changed while the module thinks, still ending with the track playing: its answer (for the queue as it
    // was) dropped, and asked again
    let k = 0;
    const slow = module(() => new Promise(res => setTimeout(() => res([k++ ? 'a4t2' : 'a4t1']), 100)));
    const s = make({ autoContinue: 'off' }); s.p.continuation = slow.c;
    await s.p.playList(['a1t1', 'a1t2', 'a1t3'], 2, false, 0, src);
    s.p.remove(0);
    await sleep(350);
    check('asked again after its answer for the queue as it was', slow.asks.length === 2 && slow.asks[1].played.join() === 'a1t2,a1t3' && s.p.queue.join() === 'a1t2,a1t3,a4t2',
      { asks: slow.asks.map(a => a.played.join()), q: s.p.queue });
  });

  await group('where the Smart Playlist starts, as the queue changes', async () => {
    const m = module(() => Promise.resolve(['a4t1', 'a4t2', 'a4t3']));
    const { p } = make({ autoContinue: 'similar' }); p.continuation = m.c;
    await p.playList(['a1t1', 'a1t2', 'a1t3'], 2, false); await tick();
    check('starts at 3', p.continuedFrom === 3, p.continuedFrom);
    p.add(['a2t1', 'a2t2'], true);                        // play next: before it
    check('play next: moves on by 2', p.continuedFrom === 5 && p.queue[5] === 'a4t1', { from: p.continuedFrom, q: p.queue });
    p.add(['a2t3']);                                      // to the end: after it
    check('add to the end: stays', p.continuedFrom === 5, p.continuedFrom);
    p.remove(0);
    check('a track before it removed: back by 1', p.continuedFrom === 4 && p.queue[4] === 'a4t1', p.continuedFrom);
    p.remove(6);
    check('a track in it removed: stays', p.continuedFrom === 4, p.continuedFrom);
    p.move(1, 5);                                         // from before it into it
    check('moved into it: back by 1', p.continuedFrom === 3 && p.queue[3] === 'a4t1', { from: p.continuedFrom, q: p.queue });
    p.move(5, 0);                                         // from it to before it
    check('moved out of it: on by 1', p.continuedFrom === 4 && p.queue[4] === 'a4t1', { from: p.continuedFrom, q: p.queue });
    // a library change (a tag edit, a rescan): the entries left keep their places
    const keep = lib.tracks.get('a2t1');
    const before = p.queue.slice(0, p.continuedFrom).filter(id => id !== 'a2t1').length;
    lib.tracks.delete('a2t1');
    p.validate();
    lib.tracks.set('a2t1', keep);
    check('library change: the part starts at the same track', p.queue[p.continuedFrom] === 'a4t1' && p.continuedFrom === before, { from: p.continuedFrom, q: p.queue });
    p.validate();
    check('library change with nothing gone: unchanged', p.queue[p.continuedFrom] === 'a4t1', p.continuedFrom);
    // a restart: from the saved settings
    const again = new Player(new Engine(), lib, p.s);
    check('restart: the part starts where it did', again.continuedFrom === p.continuedFrom && again.queue[again.continuedFrom] === 'a4t1', again.continuedFrom);
    const bad = new Player(new Engine(), lib, { ...p.s, queueContinuedFrom: 999 });
    check('restart with a start beyond the queue: none', bad.continuedFrom === -1, bad.continuedFrom);
    p.clearUpcoming();
    check('upcoming cleared (the part was after the track playing): none', p.continuedFrom === -1, p.continuedFrom);
    const q = make({ autoContinue: 'similar' }); q.p.continuation = m.c;
    await q.p.playList(['a1t1'], 0, false); await tick();
    await q.p.playList(['a2t1', 'a2t2'], 0, false);
    check('a new list played: none', q.p.continuedFrom === -1, q.p.continuedFrom);
    const w = make({ autoContinue: 'similar' }); w.p.continuation = m.c;
    await w.p.playList(['a1t1'], 0, false); await tick();
    for (const id of w.p.queue.slice(1)) lib.tracks.delete(id);
    w.p.validate();
    check('all of its tracks gone from the library: none', w.p.continuedFrom === -1, w.p.continuedFrom);
    for (const al of lib.albums) for (const t of al.tracks) lib.tracks.set(t.id, t);
  });

  await group('a click on a track further on in the queue: the ones jumped over leave it', async () => {
    const { p } = make({ autoContinue: 'off' });
    const ten = ids(10), kept = [ten[0], ten[9]].join();
    await p.playList(ten, 0, false);
    await p.jumpTo(9);
    check('the 8 between leave; the one that played stays, before the one chosen', p.queue.join() === kept && p.index === 1 && p.current.id === ten[9], { q: p.queue, i: p.index });
    await p.jumpTo(0);
    check('back into what was played: nothing leaves', p.queue.join() === kept && p.index === 0, { q: p.queue, i: p.index });
    await p.jumpTo(1);
    check('the next one: nothing to leave', p.queue.join() === kept && p.index === 1, { q: p.queue, i: p.index });
    // shuffle on: the jumped-over tracks leave the order to go back to as well
    const s = make({ autoContinue: 'off', shuffle: false });
    await s.p.playList(ids(5), 0, false);
    s.p.setShuffle(true);
    const over = s.p.queue.slice(1, 3);
    await s.p.jumpTo(3);
    s.p.setShuffle(false);
    check('shuffle off again: the jumped-over tracks do not come back', s.p.queue.length === 3 && !over.some(id => s.p.queue.includes(id)), { q: s.p.queue, over });
    // the Smart Playlist's part: jumped into, it starts at the track chosen; past it, it moves up
    const m = module(() => Promise.resolve(['a4t1', 'a4t2', 'a4t3']));
    const c = make({ autoContinue: 'similar' }); c.p.continuation = m.c;
    await c.p.playList(['a1t1', 'a1t2', 'a1t3'], 2, false); await tick();
    check('(its part after the list)', c.p.continuedFrom === 3 && c.p.queue.length === 6, { from: c.p.continuedFrom, q: c.p.queue });
    await c.p.jumpTo(4);
    check('jumped into it: a4t1 leaves, the part starts at a4t2', c.p.queue.join() === 'a1t1,a1t2,a1t3,a4t2,a4t3' && c.p.continuedFrom === 3, { q: c.p.queue, from: c.p.continuedFrom });
    const d = make({ autoContinue: 'similar' }); d.p.continuation = m.c;
    await d.p.playList(['a1t1', 'a1t2', 'a1t3'], 2, false); await tick();
    await d.p.jumpTo(0); await d.p.jumpTo(2);
    check('a jump before it, over list tracks: the part moves up with it', d.p.queue.join() === 'a1t1,a1t3,a4t1,a4t2,a4t3' && d.p.continuedFrom === 2, { q: d.p.queue, from: d.p.continuedFrom });
  });

  await group('\'played\': what the module is told', async () => {
    const { p, engine, played } = make({ autoContinue: 'off' });
    const src = { ext: 'moodchannels', channel: 3 };
    await p.playList(['a1t1', 'a1t2', 'a1t3'], 0, false, 0, src);
    check('nothing yet', played.length === 0, played);
    engine.position = 42;
    await p.jumpTo(1);
    check('another track chosen: the first reported as skipped, at 42 s, with its source', played.length === 1 && played[0].id === 'a1t1' &&
      played[0].seconds === 42 && played[0].skipped && !played[0].stopped && played[0].source === src, played[0]);
    p.onGapless(lib.getTrack('a1t3'));
    check('gapless on: reported in full, not skipped', played.length === 2 && played[1].id === 'a1t2' && played[1].seconds === 200 && !played[1].skipped, played[1]);
    p.stopped();
    check('stopped: reported as stopped', played.length === 3 && played[2].id === 'a1t3' && played[2].stopped && !played[2].skipped, played[2]);
    p.stopped();
    check('only once', played.length === 3, played.length);
    await p.playList(['a2t1'], 0, false, 0, null);
    engine.position = 200;
    p.onEnded();
    check('ended: reported, source of its own time (none)', played.length === 4 && played[3].id === 'a2t1' && !played[3].skipped && played[3].source === null, played[3]);
    await p.playList(['yt-live', 'a2t2'], 0, false);
    await p.jumpTo(1);
    check('the live stream is never reported', !played.some(x => x.id === 'yt-live'), played.map(x => x.id));
    await p.jumpTo(1);
    check('the same track again (a reload): not reported', !played.slice(4).some(x => x.id === 'a2t2'), played.slice(4));
  });

  check('nothing logged as an error but the one asked for', errors.length === 1, errors);
  T.summary();
})();
