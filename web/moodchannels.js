/* Mood channels for MIKU: the channel page (#/moods), the player bar's channel band, the now-playing page's channel
   wheel, the song radio (menu items, its band on the player bar), and a settings block (設定 → 曲庫: analysis on/off, threads, progress; the advanced AI: on/off, model
   download, segments, GPU, progress). */
(() => {
  const MC = { api: null, ui: null, status: null, block: null, aiBlock: null, manageBox: null, methodBox: null, basicBody: null, aiBody: null, aiField: null, aiSwitch: null, failBox: null, failOpen: false, manageOpen: false, counts: null };
  const ART = new URL('art/', document.currentScript.src).href;   // web/art/<index>.svg (tools/gen_art.py)

  const fmtEta = m => m == null ? '' : m < 60 ? `約 ${m} 分鐘` : `約 ${Math.floor(m / 60)} 小時 ${m % 60} 分鐘`;

  const mb = n => (n / 1e6).toFixed(0);
  const num = n => (n || 0).toLocaleString();

  /** The advanced AI's errors in words (the service gives them in English). */
  function aiError(e) {
    if (!e) return '';
    if (/moodwave-clap not found/.test(e)) return '找不到 AI 分析程式（moodwave-clap），請重新安裝 MoodWave';
    if (/not the one listed/.test(e)) return '下載的模型檔案驗證失敗（檔案不完整或已損毀），請重試';
    if (/no download address/.test(e)) return '目前還沒有模型的下載位置';
    if (/keeps ending/.test(e)) return 'AI 分析程式一再意外結束，已停用。請重新開啟進階 AI 再試一次';
    return e;
  }

  /** One row of the progress card: name, bar, numbers (and what goes after them). */
  const row = (name, frac, ...text) => h('div', { class: 'mc-row' },
    h('span', { class: 'mc-row-name' }, name),
    h('div', { class: 'mc-bar' }, h('i', { style: { width: Math.min(100, Math.max(0, frac * 100)) + '%' } })),
    h('span', { class: 'mc-row-text' }, ...text));

  /** A card's state line: what runs, its rate and the time left, and its own pause. */
  const stateLine = (text, pauseRpc, paused) => h('div', { class: 'mc-state' },
    h('span', { class: 'mc-line mc-dim' }, text),
    pauseRpc ? h('button', { class: 'btn small ghost', onclick: () => MC.api.rpc(paused ? pauseRpc.resume : pauseRpc.pause).then(set) }, paused ? '繼續' : '暫停') : null);

  /**
   * The basic card's progress: the analysis (with the loudness, measured with each track: a track counts once it
   * has both) and its own pause. Redrawn on every status event while on screen.
   */
  function drawBasic() {
    const box = MC.block;
    const s = MC.status;
    if (!box || !box.isConnected || !s) return;
    box.textContent = '';
    if (s.engineError) { box.append(h('div', { class: 'mc-err' }, '分析引擎無法啟動：' + s.engineError)); return; }
    if (!s.engine) { box.append(h('div', { class: 'mc-line' }, '正在準備分析引擎…')); return; }
    const lp = s.loudness || { measured: 0, pending: 0 };
    // tracks analysed before loudness was measured get it afterwards: lp.pending beyond s.pending
    const done = Math.max(0, s.analysed - Math.max(0, lp.pending - s.pending));
    box.append(h('div', { class: 'mc-rows' }, row('分析進度', s.total ? (done + s.failed) / s.total : 0,
      h('b', null, `${num(done)} / ${num(s.total)}`),
      s.failed ? h('button', { class: 'mc-link', onclick: () => { MC.manageOpen = MC.failOpen = true; drawManage(); drawFailed(); } }, `· ${num(s.failed)} 首無法分析`) : null)));
    const busy = s.pending || lp.pending;
    const text = s.paused ? '已暫停' : !busy ? '已全部分析完成'
      : '分析中' + (s.pending && s.perHour ? ` · 每小時約 ${num(s.perHour)} 首` : '') + (s.pending && s.etaMinutes != null ? ` · 剩餘${fmtEta(s.etaMinutes)}` : '');
    box.append(stateLine(text, busy ? { pause: 'pause', resume: 'resume' } : null, s.paused));
  }

  /** The advanced AI card's progress: the model's download, then the analysis, and its own pause. */
  function drawAi() {
    const box = MC.aiBlock;
    const s = MC.status;
    if (!box || !box.isConnected || !s) return;
    box.textContent = '';
    const ai = s.ai || {}, dl = ai.download || {};
    const rows = h('div', { class: 'mc-rows' });
    box.append(rows);
    if (dl.downloading) {
      rows.append(row('模型', dl.bytes ? dl.done / dl.bytes : 0, `${dl.local ? '複製' : '下載'}中 ${mb(dl.done)} / ${mb(dl.bytes)} MB`,
        h('button', { class: 'mc-link', onclick: () => MC.api.rpc('ai.cancel').then(set) }, '· 取消')));
      return;
    }
    if (!dl.ready) {
      rows.append(row('模型', 0, dl.error ? '下載失敗' : '等待下載',
        dl.error ? h('button', { class: 'mc-link', onclick: () => MC.api.rpc('ai.download').then(set) }, '· 重試') : null));
      if (dl.error) box.append(h('div', { class: 'mc-err' }, aiError(dl.error)));
      return;
    }
    const device = ai.device ? (ai.device === 'CPU' ? '使用 CPU' : '使用 GPU') : '';
    rows.append(row('分析進度', ai.total ? ai.done / ai.total : 0, h('b', null, `${num(ai.done)} / ${num(ai.total)}`),
      ' · ' + [device, `${ai.segments} 段`].filter(Boolean).join(' · ')));
    if (ai.error) { box.append(h('div', { class: 'mc-err' }, aiError(ai.error))); return; }
    const waiting = ai.pending && (ai.waitingForDsp || (s.pending && !s.paused));
    const text = ai.paused ? '已暫停' : !ai.pending ? '已全部分析完成' : waiting ? '等基本分析完成後開始'
      : '分析中' + (ai.perHour ? ` · 每小時約 ${num(ai.perHour)} 首` : '') + (ai.etaMinutes != null ? ` · 剩餘${fmtEta(ai.etaMinutes)}` : '');
    box.append(stateLine(text, ai.pending ? { pause: 'ai.pause', resume: 'ai.resume' } : null, ai.paused));
  }

  /** What deletes or redoes analysis, under both cards, folded away (rarely needed, and not to be clicked by mistake). */
  function drawManage() {
    const box = MC.manageBox;
    const s = MC.status;
    if (!box || !box.isConnected || !s) return;
    box.textContent = '';
    const ai = s.ai || {}, dl = ai.download || {};
    box.append(h('button', { class: 'mc-link mc-fold', onclick: () => { MC.manageOpen = !MC.manageOpen; if (!MC.manageOpen) MC.failOpen = false; drawManage(); drawFailed(); } },
      (MC.manageOpen ? '▾' : '▸') + ' 管理分析資料'));
    if (!MC.manageOpen) return;
    const acts = h('div', { class: 'mc-actions' },
      s.failed ? h('button', { class: 'btn small ghost', onclick: toggleFailed }, MC.failOpen ? '隱藏無法分析的曲目' : `顯示無法分析的曲目（${num(s.failed)}）`) : null,
      s.failed ? h('button', { class: 'btn small ghost', onclick: () => MC.api.rpc('reanalyze').then(refresh) }, '重新分析失敗的曲目') : null,
      h('button', {
        class: 'btn small ghost',
        onclick: () => { if (confirm('要清除所有基本分析結果，從頭分析整個曲庫嗎？（你的投票會保留）')) MC.api.rpc('reanalyze', { all: true }).then(refresh); },
      }, '全部重新分析'));
    if (dl.ready || ai.total || ai.done) {
      const size = `模型約 ${mb(dl.bytes || 285e6)} MB、分析結果約 ${Math.max(1, Math.round((ai.done || 0) * 1.1 / 1024))} MB`;
      acts.append(h('button', {
        class: 'btn small ghost',
        onclick: () => { if (confirm('要刪除 AI 模型與所有 AI 分析結果嗎？進階 AI 會一併關閉，之後開啟需要重新下載與分析。')) MC.api.rpc('ai.delete').then(set); },
      }, '刪除 AI 資料'), h('span', { class: 'mc-line mc-dim mc-size' }, size));
    }
    box.append(acts);
  }

  const drawProgress = () => { drawBasic(); drawAi(); drawManage(); };

  /** The advanced AI's options (in its card, while it is on): the GPU, its threads on the processor, the segments. */
  function drawMethod() {
    const box = MC.methodBox, ui = MC.ui;
    if (!box || !box.isConnected || !ui) return;
    const s = MC.status || {}, ai = s.ai || {};
    const cpus = s.cpus || 1;
    box.textContent = '';
    if (ai.gpuCapable) box.append(ui.field('使用 GPU', '用顯示卡分析，比 CPU 快很多；顯示卡不支援時會自動改用 CPU',
      ui.sw(ai.gpu !== false, v => MC.api.rpc('set', { aiGpu: v }).then(set))));
    // the model's threads matter only on the processor: no GPU here, the GPU turned off, or the GPU not usable
    if (!ai.gpuCapable || ai.gpu === false || ai.device === 'CPU') {
      const half = Math.max(1, Math.floor(cpus / 2));
      box.append(ui.field('CPU 執行緒', `用 CPU 分析時的執行緒，越多越快，但會佔用較多 CPU（這台電腦有 ${cpus} 個）`,
        ui.select([[0, `自動（${half} 條）`], ...Array.from({ length: cpus }, (_, i) => [i + 1, `${i + 1} 條`])], ai.threadSetting || 0,
          v => MC.api.rpc('set', { aiThreads: +v }).then(set))));
    }
    const auto = !ai.segmentSetting && ai.segments ? ai.segments : ai.gpuCapable && ai.gpu !== false ? 12 : 6;   // the service's own pick once it knows the device
    box.append(ui.field('每首分析段數', '每首取幾段 10 秒的片段來分析。越多越準確，但也越慢；之後調高時只補算缺少的段落',
      ui.select([[0, `自動（${auto} 段）`], [6, '6 段（最快）'], [9, '9 段'], [12, '12 段（最準確）']], ai.segmentSetting || 0,
        v => MC.api.rpc('set', { aiSegments: +v }).then(set))));
  }

  /** The sidebar's MoodWave link: only while MoodWave is on (leaving its page when it is turned off there). */
  function showNav() {
    if (!MC.nav) return;
    const on = !MC.status || MC.status.enabled !== false;
    const display = on ? '' : 'none';
    if (MC.nav.style.display === display) return;
    MC.nav.style.display = display;
    // the links below moved: MIKU's highlight behind the current one follows them at once (it measures only on page changes)
    if (typeof NavPill !== 'undefined') requestAnimationFrame(() => NavPill.move(true));
    if (!on && location.hash.startsWith('#/moods')) location.hash = '#/home';
  }

  /** A card folded to its switch while its feature is off; the AI's switch greyed out while MoodWave is off. */
  function foldCards() {
    const s = MC.status || {}, ai = s.ai || {};
    const on = s.enabled !== false;
    if (MC.basicBody) MC.basicBody.hidden = !on;
    if (MC.aiBody) MC.aiBody.hidden = !(on && ai.enabled);
    if (MC.aiField) MC.aiField.classList.toggle('mc-off', !on);
  }

  /** The list of failed tracks under the management (its own box, so the progress redraws do not close it). */
  async function drawFailed() {
    const box = MC.failBox;
    if (!box) return;
    box.textContent = '';
    if (!MC.failOpen || !box.isConnected) return;
    const list = await MC.api.rpc('failed');
    if (!box.isConnected || !MC.failOpen) return;
    box.textContent = '';
    if (!list.length) { box.append(h('div', { class: 'mc-line mc-dim' }, '沒有無法分析的曲目')); return; }
    for (const f of list) {
      const cut = Math.max(f.path.lastIndexOf('/'), f.path.lastIndexOf(String.fromCharCode(92)));
      box.append(h('div', { class: 'mc-fail' },
        h('div', { class: 'mc-fail-text', title: f.path },
          h('div', { class: 'mc-fail-name' }, f.path.slice(cut + 1)),
          h('div', { class: 'mc-fail-dir' }, f.path.slice(0, cut + 1)),
          h('div', { class: 'mc-fail-why' }, f.reason)),
        h('button', { class: 'icon-btn', title: '在檔案總管中顯示', html: icon('folder'), onclick: () => Host.call('reveal', { id: f.id }) })));
    }
  }
  function toggleFailed() { MC.failOpen = !MC.failOpen; drawManage(); drawFailed(); }

  // what decides which analysis options show: the AI on, its GPU setting, where it runs
  const methodKey = st => { const ai = (st && st.ai) || {}; return [!!ai.enabled, ai.gpu !== false, ai.device || ''].join('|'); };
  const set = st => {
    const was = methodKey(MC.status);
    MC.status = st;
    if (MC.aiSwitch) MC.aiSwitch.classList.toggle('on', !!(st && st.ai && st.ai.enabled));
    if (methodKey(st) !== was) drawMethod();         // the GPU and segments only while the AI is on; its threads on the CPU
    foldCards();
    showNav();
    drawProgress();
  };
  const refresh = () => MC.api.rpc('status').then(set);


  /* ── channel page (#/moods) ── */
  // the channels in their native order (0–4 time of day, 5–12 moods), then all of them; hue pairs: the card colours
  const INFO = [
    { name: 'Morning', desc: '早晨 · 5:00–9:59', hue: [38, 22] },
    { name: 'Daytime', desc: '白天 · 10:00–15:59', hue: [200, 180] },
    { name: 'Evening', desc: '傍晚 · 16:00–18:59', hue: [18, 330] },
    { name: 'Night', desc: '夜晚 · 19:00–23:59', hue: [250, 220] },
    { name: 'Midnight', desc: '深夜 · 0:00–4:59', hue: [265, 235] },
    { name: 'Energetic', desc: '充滿活力、節奏強烈', hue: [8, 340] },
    { name: 'Relax', desc: '平靜、放鬆', hue: [160, 190] },
    { name: 'Mellow', desc: '柔和、溫暖', hue: [215, 250] },
    { name: 'Upbeat', desc: '明快、愉悅', hue: [45, 15] },
    { name: 'Emotional', desc: '抒情、感性', hue: [330, 290] },
    { name: 'Lounge', desc: '爵士與輕鬆氛圍', hue: [28, 5] },
    { name: 'Dance', desc: '律動、適合跳舞', hue: [285, 320] },
    { name: 'Extreme', desc: '強烈、激昂', hue: [355, 15] },
    { name: 'Shuffle All', desc: '所有已分類的曲目', hue: [280, 200] },
  ];
  const SHUFFLE_ALL = 13;
  // the channel's picture for cards and the wheel: pre-rendered (tools/gen_backdrops.py), as an SVG it stuttered the page
  const art = i => `url("${ART}card/${i}.webp?v=${BG_VERSION}")`;
  const BG_VERSION = 7;                                      // bump when tools/gen_backdrops.py makes new backdrops (cache)

  /**
   * Play a channel: shuffled, starting at a random track (picked by the module; `at` is where that track starts,
   * 0 for now), and marked as this module's so the player bar shows the channel band.
   */
  async function playChannel(i, still = () => true) {
    const r = await MC.api.rpc('start', { channel: i });
    if (!still()) return true;                               // another channel was asked for meanwhile: that one plays
    if (!r || !r.ids.length) { toast(`${INFO[i].name} 沒有曲目`); return false; }
    await MC.api.play(r.ids, true, r.start, { at: r.at, source: { channel: i } });
    return true;
  }

  /**
   * The channels ▲▼ steps through, in this order: Shuffle All, the time channel of now, the eight moods; only
   * channels with tracks. A time channel that is playing but is not the one of now keeps its place too.
   */
  function cycle(playing) {
    const zone = nowZone();
    const time = playing < 5 && playing !== zone ? playing : zone;
    const all = [SHUFFLE_ALL, time, 5, 6, 7, 8, 9, 10, 11, 12];
    const counts = MC.counts;
    const list = all.filter(i => i === playing || !counts || counts[i] > 0);
    return list.length > 1 ? list : all;                      // never a list of one: ▲▼ must go somewhere
  }

  /**
   * Track counts per channel, for skipping empty channels. Kept for a minute (they grow while the analysis runs);
   * a reply with nothing analysed (the engine is still starting) is not kept, so it is asked again next time.
   */
  async function loadCounts() {
    const data = await MC.api.rpc('channels', { sample: 40 });   // counts and a spread of 40 tracks each (covers)
    if (data.analysed > 0) { MC.counts = data.channels.map(c => c.count); MC.countsAt = Date.now(); }
    else MC.counts = null;
    return data;
  }

  /** The ▲▼ list around a channel, with fresh counts. */
  async function nav(i) {
    if (!MC.counts || Date.now() - MC.countsAt > 60000) await loadCounts().catch(() => {});
    const list = cycle(i);
    return { list };
  }

  /*
   * Switching channels turns like an iOS picker wheel: the bar's channel name rolls out up or down while the next
   * one rolls in from the other side, and the now-playing page's channel wheel turns by one. Both move the moment
   * ▲▼ is pressed (the new queue arrives a little later and finds them already there); if the channel turns out to
   * be empty they turn back. dir: +1 = next (▼, the wheel moves up), -1 = previous (▲).
   */
  const ROLL = { duration: 420, easing: 'cubic-bezier(.22,.9,.25,1)' };

  /**
   * ▲▼ turn the wheel at once, but the channel only starts once the presses stop (PLAY_AFTER ms): skipping through
   * channels quickly does not start a song (and its cover, lyrics lookup …) for every channel passed.
   */
  const PLAY_AFTER = 350;
  function go(to, dir) {
    if (MC.playTimer == null) MC.burstFrom = MC.showing;
    MC.pendingTo = to;                                   // queues of the channels passed on the way are not shown
    MC.pendingAt = Date.now();
    show(to, dir);
    clearTimeout(MC.playTimer);
    MC.playTimer = setTimeout(async () => {
      MC.playTimer = null;
      const from = MC.burstFrom;
      if (MC.pendingTo !== to) return;
      if (!(await playChannel(to, () => MC.pendingTo === to)) && MC.pendingTo === to) {
        MC.pendingTo = null;
        if (from != null) show(from, -dir);                  // the channel is empty: turn back
      }
    }, PLAY_AFTER);
  }

  /** A new queue of channel i arrived: show it, unless the wheel is on its way to another one or already there. */
  function sync(i) {
    // still on its way to another channel (but a switch that never arrived is not waited for longer than 4 s)
    if (MC.pendingTo != null && i !== MC.pendingTo && Date.now() - MC.pendingAt < PLAY_AFTER + 4000) return;
    MC.pendingTo = null;
    if (MC.showing !== i || band().roll.dataset.ch !== String(i) || (MC.rail && !MC.rail.items.size)) show(i, 0);
  }

  /** ▲▼: from the channel shown (which is ahead of the queue while a switch is on its way), d steps round the list. */
  function step(d) {
    const cur = MC.showing;
    if (cur == null) return;
    const list = cycle(cur), at = list.indexOf(cur);
    go(list[(at + d + list.length) % list.length], d);
  }
  const neighbour = (i, d) => { const list = cycle(i), at = list.indexOf(i); return list[(at + d + list.length) % list.length]; };

  /** Bring both the bar's name and the page's wheel to channel i (dir 0 = at once, no animation). */
  function show(i, dir) {
    MC.showing = i;
    rollName(i, dir);
    backdrops(i);
    if (MC.rail) turnWheel(i, cycle(i), dir !== 0);
    refreshVotes();
    for (const x of [MC.band, MC.rail]) if (x) {
      x.up.title = '上一個頻道：' + INFO[neighbour(i, -1)].name;
      x.down.title = '下一個頻道：' + INFO[neighbour(i, 1)].name;
    }
  }

  /**
   * The channel's pictures: the now-playing backdrop, and the bar's background, which fades over the one before
   * (two layers under the bar's content; the newer one fades in on top, the older goes once it is covered).
   * Entering channel mode it fades over the theme's own bar; leaving, the layers fade out (moodchannels.css).
   */
  function backdrops(i) {
    document.documentElement.style.setProperty('--mc-art', art(i));
    MC.fades = MC.fades || {
      bar: { parent: () => document.getElementById('bar'), cls: 'mc-barbg', ms: 700 },
      np: { parent: () => document.querySelector('#np .np-bg'), cls: 'mc-npbg', ms: 900 },
    };
    fadeTo(MC.fades.bar, `${ART}bar/${i}.webp?v=${BG_VERSION}`);
    fadeTo(MC.fades.np, `${ART}bg/${i}.webp?v=${BG_VERSION}`);    // the now-playing backdrop (tools/gen_backdrops.py)
  }

  /** Two layers in turn: the picture is loaded, then fades in on top of the other, which goes once it is covered. */
  function fadeTo(f, url) {
    if (!f.layers) {
      const parent = f.parent();
      if (!parent) return;
      f.layers = [h('div', { class: f.cls }), h('div', { class: f.cls })];
      if (f.cls === 'mc-barbg') parent.prepend(...f.layers); else parent.append(...f.layers);
    }
    if (f.url === url) return;
    f.url = url;
    const img = new Image();
    img.onload = () => {
      if (f.url !== url) return;                             // switched on again meanwhile: that one fades in instead
      const [a, b] = f.layers;
      const front = a.classList.contains('top') ? a : b, back = front === a ? b : a;
      back.style.backgroundImage = `url("${url}")`;
      front.classList.remove('top');
      back.classList.add('top', 'on');
      clearTimeout(f.timer);
      f.timer = setTimeout(() => front.classList.remove('on'), f.ms + 50);
    };
    img.src = url;
  }

  /** The now-playing page (the cover, the lyrics and MoodWave's wheel or label), from the bar's band or radio label. */
  function openNowPlaying() {
    if (!NowPlaying.open) NowPlaying.show();
  }

  /** The bar's channel band. One element for the session: MIKU empties the slot on every new queue and it is put back. */
  function band() {
    if (MC.band) return MC.band;
    const up = h('button', { class: 'mc-band-step', html: icon('down'), onclick: () => step(-1) });
    const down = h('button', { class: 'mc-band-step down', html: icon('down'), onclick: () => step(1) });
    const roll = h('span', { class: 'mc-roll' });             // the mark and the name roll as one
    const v = voteButtons();                                 // 👍 / 👎 right of the name
    // a click on the band (not on ▲▼ or 👍 / 👎) opens the now-playing page, as the radio's label does
    const name = h('span', { class: 'mc-band-name', role: 'link', tabindex: 0, title: '打開播放畫面',
      onkeydown: e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openNowPlaying(); } } }, roll);
    MC.band = h('div', { class: 'mc-band', onclick: e => { if (!e.target.closest('button')) openNowPlaying(); } }, up, name, down, v.votes);
    Object.assign(MC.band, { up, down, roll }, v);
    return MC.band;
  }

  /*
   * Entering and leaving channel mode, the band and what follows it move as one: the band's width (and the gap after
   * it) opens or closes while its content slides by exactly that much, with the same timing, so the content stays
   * against the cover and is uncovered or tucked away at the band's left edge.
   */
  const PUSH = { ms: 460, ease: 'cubic-bezier(.4,0,.2,1)', gap: 14 };

  function pushIn() { const b = band(); pushInto(b, bandWidth(b), fitBand); }

  /** A MIKU that swaps the bar's left part as a whole when the slot changes (BarSwap): the band then makes no entrance or exit of its own. */
  const barSwaps = () => typeof MikuExt !== 'undefined' && !!MikuExt.swapsBar;

  /** The channel band at its width at once (no glide), for MIKU's swap. */
  function settleBand() {
    const b = band();
    b.style.transition = 'none';
    fitBand();
    void b.offsetWidth;
    b.style.transition = '';
  }

  /**
   * One of MoodWave's bands takes the place of another (a radio after a radio or a channel, a channel after a radio):
   * MIKU has emptied the slot, so pushing in from nothing would snap the cover back and slide it out again. Instead
   * the width glides from the band that was there to this one's and its content fades in.
   */
  function morphInto(b, from, to, then) {
    b.getAnimations({ subtree: true }).forEach(a => a.cancel());
    b.classList.add('pushing');
    b.style.transition = 'none';
    b.style.width = from + 'px';
    void b.offsetWidth;
    b.style.transition = `width ${PUSH.ms}ms ${PUSH.ease}`;
    b.style.width = to + 'px';
    for (const c of b.children) c.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 300, delay: 90, easing: 'ease-out', fill: 'backwards' });
    setTimeout(() => {
      b.classList.remove('pushing');
      b.style.transition = '';
      MC.slotW = to;
      if (then) then();
    }, PUSH.ms + 20);
  }

  /** Opens <paramref name="b"/> (the channel band, or a radio's) to width w; then() once it is in place. */
  function pushInto(b, w, then) {
    const slot = b.parentElement;                           // the slot (#b-ext) holds the gap to the cover
    b.getAnimations({ subtree: true }).forEach(a => a.cancel());   // also a way out still running
    const t = `${PUSH.ms}ms ${PUSH.ease}`;
    b.classList.add('pushing');
    b.style.transition = slot.style.transition = 'none';
    b.style.width = '0px';
    slot.style.marginRight = -PUSH.gap + 'px';
    void b.offsetWidth;
    b.style.transition = `width ${t}`;
    slot.style.transition = `margin-right ${t}`;
    b.style.width = w + 'px';
    slot.style.marginRight = '0px';
    slideContent(b, -(w + PUSH.gap), 0);
    setTimeout(() => {
      b.classList.remove('pushing');
      b.style.transition = slot.style.transition = '';
      for (const c of b.children) c.getAnimations().forEach(x => x.cancel());   // in place: drop the slide
      MC.slotW = w;
      if (then) then();                                      // what changed meanwhile (the vote buttons shown)
    }, PUSH.ms + 20);
  }

  /* ── song radio (the advanced AI) ── */

  const RADIO_KIND = { track: '歌曲電台', album: '專輯電台', artist: '歌手電台', smart: 'MoodWave' };
  /** The Smart Playlist's label: where the queue goes on with MoodWave's picks (MIKU's slot source { smart: true }). */
  const SMART = { kind: 'smart', name: 'Smart Playlist' };

  /** The player bar while a radio plays: what kind of radio and what it started from, left of the cover. */
  function drawRadioBand(el, radio) {
    MC.inMode = false;
    const b = MC.radioBand = radioLabel(radio, 'mc-radio-band', 'mc-band-logo');
    el.append(b);
    fitRadio(b);
    // in as the channel band comes in: its width opens from nothing, the cover and the title following it
    const w = b.offsetWidth;
    const done = () => { b.style.width = ''; MC.slotW = b.offsetWidth; };
    if (barSwaps()) { MC.slotW = w; return; }              // MIKU brings the bar's left part in as a whole
    if (w > 0 && MC.slotW > 0) morphInto(b, MC.slotW, w, done);   // after another band of ours
    else if (w > 0) pushInto(b, w, done);
  }

  /**
   * What radio plays: the mark, the kind (歌曲電台 / 專輯電台 / 歌手電台) and what it started from, then 👍 / 👎 on the
   * track playing; on the bar and the now-playing page.
   */
  function radioLabel(radio, cls, logoCls) {
    const v = voteButtons(radioVote);
    MC.radioVotes = (MC.radioVotes || []).filter(x => x.votes.isConnected).concat([v]);
    // on the bar the label opens the now-playing page, as the channel band does (👍 / 👎 stay votes)
    const open = cls === 'mc-radio-band';
    const el = h('div', { class: cls + (open ? ' mc-radio-open' : ''), role: open && 'link', tabindex: open && 0,
      title: radio.kind === 'smart' ? 'MoodWave Smart Playlist' : `MoodWave ${RADIO_KIND[radio.kind] || '電台'}：${radio.name || ''}`,
      onclick: open && openNowPlaying, onkeydown: open && (e => { if (e.target === el && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openNowPlaying(); } }) },
      h('span', { class: 'mc-radio-mark', html: logo(logoCls) }),
      h('span', { class: 'mc-radio-text' }, h('small', null, RADIO_KIND[radio.kind] || '電台'), h('b', null, radio.name || '')),
      v.votes);
    refreshRadioVotes();
    return el;
  }

  /*
   * 👍 / 👎 in a radio, kept for radios from the same start (the same track, album or artist): 👍 makes the track a
   * seed of them (more like it), 👎 keeps it out of them and makes the ones very like it rarer; 👎 also skips it now.
   * Pressing the lit one again takes the vote back.
   */
  const radioSource = () => {
    const q = App.queue || {}, src = q.slotSource || q.source;
    return src && src.ext === 'moodchannels' && (src.radio || src.smart) ? src : null;
  };

  function setRadioVotes(id, r) {
    MC.radioVote = r && r.known ? { id, v: r.vote || 0 } : null;
    for (const x of MC.radioVotes || []) {
      if (!x.votes.isConnected) continue;
      const was = x.votes.hidden;
      x.votes.hidden = !MC.radioVote;
      if (was !== x.votes.hidden) fitRadio();
      if (!MC.radioVote) continue;
      const v = MC.radioVote.v;
      x.yes.setAttribute('aria-pressed', String(v > 0));
      x.no.setAttribute('aria-pressed', String(v < 0));
      const where = radioSource() && radioSource().smart ? 'Smart Playlist' : '這個電台';
      x.yes.title = v > 0 ? `取消（${where}多來點這種）` : `${where}多來點這種`;
      x.no.title = v < 0 ? `取消（${where}不要這首）` : `${where}不要這首`;
    }
  }

  async function refreshRadioVotes() {
    const src = radioSource(), id = App.state && App.state.trackId;
    if (!src || !id) { setRadioVotes(null, null); return; }
    const key = id + '|' + JSON.stringify(src);
    MC.radioVoteKey = key;
    const r = await MC.api.rpc('radio.vote.get', { id, source: src }).catch(() => null);
    if (MC.radioVoteKey === key) setRadioVotes(id, r);
  }

  async function radioVote(v) {
    const src = radioSource(), id = App.state && App.state.trackId;
    if (!src || !id) return;
    const cur = MC.radioVote && MC.radioVote.id === id ? MC.radioVote.v : 0;
    const next = cur === v ? 0 : v;
    const r = await MC.api.rpc('radio.vote', { id, source: src, vote: next }).catch(() => null);
    if (!r || !r.known) { toast('沒有記下來，請再按一次'); return; }
    setRadioVotes(id, r);
    if (next < 0) skipOut(id);
  }

  /**
   * A narrow window: the bar keeps at least TITLE_MIN of the track's title next to the cover. The label's 👍 / 👎 leave
   * the bar first (marked mc-tight; the now-playing page's label has them), then its name is cut short, and with no
   * room for that either the label leaves the bar (mc-gone).
   */
  function fitRadio(b = MC.radioBand) {
    const mini = b && b.isConnected && b.closest('.np-mini');
    if (!mini) return;
    b.classList.remove('mc-tight', 'mc-gone');
    b.style.maxWidth = '';
    const art = mini.querySelector('.art');
    const avail = mini.clientWidth - (art ? art.offsetWidth : 0) - 2 * 14 - TITLE_MIN;   // .np-mini gap: 14 px
    if (b.offsetWidth > avail) b.classList.add('mc-tight');
    if (b.offsetWidth <= avail) return;
    // no room even for its name: the label leaves the bar (the now-playing page's label stays), the title keeps it
    if (avail < 90) b.classList.add('mc-gone');
    else b.style.maxWidth = avail + 'px';
  }
  addEventListener('resize', () => setTimeout(fitRadio, 160));

  /** The radio can start: the advanced AI on, its model there, and tracks with their AI analysis. */
  const radioReady = () => {
    const ai = MC.status && MC.status.ai;
    return !!(ai && ai.enabled && ai.download && ai.download.ready && ai.done > 0);
  };

  async function startRadio(args) {
    const kind = RADIO_KIND[args.kind];
    let r = null;
    try { r = await MC.api.rpc('radio.start', args); } catch (e) { console.error('[moodchannels] radio', e); }
    if (!r || !r.ok) {
      toast(r && r.reason === 'embedding' ? '這首歌還沒完成 AI 分析，已排到最前面，請稍後再試一次' : `目前無法開始${kind}`, { error: true });
      return;
    }
    await MC.api.play(r.ids, false, 0, { source: r.source });
    toast(`已開始${kind}：${args.name}` + (r.waiting ? '（部分曲目還在 AI 分析，接下來的選曲會越來越準）' : ''));
  }

  /** Leaving channel mode: the band closes to the left, the cover and the title following it. */
  function pushOut() {
    if (barSwaps()) { MC.inMode = false; MC.slotW = 0; return null; }   // MIKU takes the bar's left part away as a whole
    const radio = document.documentElement.classList.contains('mc-radio');
    const b = radio ? MC.radioBand : band();
    if (!b || !b.isConnected) return null;
    const slot = b.parentElement;
    MC.inMode = false;
    MC.slotW = 0;
    const w = b.offsetWidth, t = `${PUSH.ms}ms ${PUSH.ease}`;
    b.classList.add('pushing');
    b.style.transition = `width ${t}`;
    slot.style.transition = `margin-right ${t}`;
    b.style.width = '0px';
    slot.style.marginRight = -PUSH.gap + 'px';
    slideContent(b, 0, -(w + PUSH.gap));
    return new Promise(r => setTimeout(() => {
      b.classList.remove('pushing');
      b.style.transition = slot.style.transition = slot.style.marginRight = '';   // MIKU hides the slot now
      r();
    }, PUSH.ms + 20));
  }

  function slideContent(b, from, to) {
    for (const c of b.children)
      c.animate([{ transform: `translateX(${from}px)` }, { transform: `translateX(${to}px)` }], { duration: PUSH.ms, easing: PUSH.ease, fill: 'forwards' });
  }

  /**
   * The band is as wide as the name shown and the vote buttons after it, so the cover sits right after them (the
   * width glides as names roll).
   */
  function bandWidth(b) {
    const item = b.roll.lastElementChild;
    const name = item ? item.offsetWidth : 0;               // the layout width: not changed by the roll's transforms
    return name + (b.votes.hidden || tight(b, name) ? 0 : VOTE_GAP + VOTES_W);
  }
  const VOTE_GAP = 14;                                       // moodchannels.css .mc-band > .mc-votes margin-left
  const VOTES_W = 62;                                        // the two 28 px buttons and the 6 px between them
  const TITLE_MIN = 90;                                      // the title next to the cover keeps at least this much

  /**
   * In a narrow window the bar's left part has no room for the buttons as well (the title would shrink to nothing):
   * they leave the bar then (marked mc-tight), the now-playing wheel still has them.
   */
  function tight(b, name) {
    const mini = b.closest('.np-mini');
    if (!mini) return false;
    const art = mini.querySelector('.art');
    const room = mini.clientWidth - name - (art ? art.offsetWidth : 0) - 2 * 14;   // .np-mini gap: 14 px
    const t = room - VOTE_GAP - VOTES_W < TITLE_MIN;
    b.classList.toggle('mc-tight', t);
    return t;
  }

  function fitBand() {
    const b = MC.band;
    if (!b || !b.isConnected || b.classList.contains('pushing')) return;
    const w = bandWidth(b);
    if (w > 0) { b.style.width = w + 'px'; MC.slotW = w; }
  }
  let fitSoon = 0;
  addEventListener('resize', () => { clearTimeout(fitSoon); fitSoon = setTimeout(fitBand, 150); });

  function rollName(i, dir) {
    const b = band(), roll = b.roll;
    if (roll.dataset.ch === String(i)) return;
    roll.dataset.ch = i;
    b.title = `${NAME} · ${INFO[i].name}`;
    const old = [...roll.children];
    const next = h('span', { class: 'mc-roll-item', html: logo('mc-band-logo') }, h('span', null, INFO[i].name));
    roll.append(next);
    fitBand();
    if (!dir || !old.length || !roll.isConnected) { old.forEach(o => o.remove()); return; }
    // the old name turns away over the top (▼) or the bottom (▲) of the wheel, the next comes round from the other side
    for (const o of old) {
      o.getAnimations().forEach(a => a.finish());
      o.animate([{ transform: 'none', opacity: 1 }, { transform: `translateY(${-dir * 90}%) rotateX(${dir * 75}deg)`, opacity: 0 }], ROLL).onfinish = () => o.remove();
    }
    next.animate([{ transform: `translateY(${dir * 90}%) rotateX(${-dir * 75}deg)`, opacity: 0 }, { transform: 'none', opacity: 1 }], ROLL);
  }

  /**
   * The player bar while a channel plays: the channel's picture behind the whole bar (moodchannels.css), and on the
   * left the channel band: ▲ previous channel, the channel, ▼ next channel, printed on the bar.
   */
  async function drawBand(el, src) {
    const radio = src.radio || (src.smart && SMART);
    document.documentElement.classList.toggle('mc-radio', !!radio);
    if (radio) { drawRadioBand(el, radio); return; }
    const i = src.channel;
    if (!INFO[i]) return;
    const entering = !MC.inMode;                         // from another kind of list (not a channel switch)
    MC.inMode = true;
    el.append(band());                                       // at once (before any await), so it never blinks
    sync(i);
    refreshVotes();                                          // the buttons in place first: they come in with the band
    if (entering && barSwaps()) settleBand();               // MIKU brings the bar's left part in as a whole
    else if (entering && MC.slotW > 0) { const b = band(); morphInto(b, MC.slotW, bandWidth(b), fitBand); }   // after a radio
    else if (entering) pushIn(); else fitBand();
    await nav(i);                                            // fresh counts: empty channels drop out of the wheel
    if (MC.showing === i && MC.rail) turnWheel(i, cycle(i), true);
  }

  /** The now-playing page's channel wheel (one element for the session, like the band). */
  function rail() {
    if (MC.rail) return MC.rail;
    const up = h('button', { class: 'mc-rail-step', html: icon('down'), onclick: () => step(-1) });
    const down = h('button', { class: 'mc-rail-step down', html: icon('down'), onclick: () => step(1) });
    const wheel = h('div', { class: 'mc-wheel' });
    // 👍 / 👎 on the track playing, in the channel playing: right of the wheel's middle frame
    const v = voteButtons();
    wheel.append(v.votes);
    MC.rail = h('div', { class: 'mc-rail' },
      h('a', { class: 'mc-rail-head', href: '#/moods', html: logo('mc-rail-logo'), onclick: () => NowPlaying.hide() }, brand()),
      up, wheel, down);
    Object.assign(MC.rail, { up, down, wheel, items: new Map() }, v);
    MC.rail.addEventListener('wheel', railScroll, { passive: false });
    return MC.rail;
  }

  /*
   * Votes: the listener says whether the track playing belongs in the channel playing (👍) or not (👎); pressing the
   * same one again takes the vote back. A 👎 takes the track out of that channel at once (the next time the channel
   * starts); once a channel has enough of both, the module learns from them which of its other tracks do not belong
   * (Personalization in the module). Not for Shuffle All, which is every track.
   */
  const THUMB = '<svg class="i" viewBox="0 0 24 24"><path d="M7 11v9H4.2a.7.7 0 0 1-.7-.7v-7.6a.7.7 0 0 1 .7-.7H7zm0 0 4-7.2a1.9 1.9 0 0 1 3.5 1.3L13.9 9h5a2 2 0 0 1 2 2.4l-1.3 6.5A2.6 2.6 0 0 1 17 20H7"/></svg>';

  function voteTarget() {
    const id = App.state && App.state.trackId, ch = MC.showing;
    return id && ch != null && ch !== SHUFFLE_ALL && INFO[ch] ? { id, ch } : null;
  }

  /**
   * The mouse wheel or the trackpad turns the wheel by one channel per gesture: the scroll adds up until it is worth a
   * step, then the rest of the gesture (a trackpad's glide included) is ignored until the scrolling pauses.
   */
  function railScroll(e) {
    if (Math.abs(e.deltaY) < Math.abs(e.deltaX)) return;
    e.preventDefault();
    const r = MC.rail, now = performance.now();
    if (now - (r.wheelAt || 0) > 150) { r.wheelSum = 0; r.wheelDone = false; }   // a pause: a new gesture
    r.wheelAt = now;
    if (r.wheelDone) return;
    r.wheelSum = (r.wheelSum || 0) + e.deltaY * (e.deltaMode === 1 ? 33 : e.deltaMode === 2 ? 400 : 1);
    if (Math.abs(r.wheelSum) < 30) return;
    r.wheelDone = true;
    step(Math.sign(r.wheelSum));
  }

  /** The 👍 / 👎 pair: one on the bar's band, one on the now-playing wheel, both showing the same vote. */
  function voteButtons(fn = vote) {
    const yes = h('button', { class: 'mc-vote yes', 'aria-pressed': 'false', html: THUMB, onclick: e => { e.stopPropagation(); fn(1); } });
    const no = h('button', { class: 'mc-vote no', 'aria-pressed': 'false', html: THUMB, onclick: e => { e.stopPropagation(); fn(-1); } });
    return { votes: h('div', { class: 'mc-votes', hidden: true }, yes, no), yes, no };
  }

  /**
   * Shows the vote r on both pairs; r undefined = not known yet (shown, nothing pressed: the band's width, and so the
   * cover after it, is right at once), r null or not known to the module = hidden.
   */
  function setVotes(t, r) {
    const v = t && r !== null && !(r && r.known === false) ? (r ? r.vote || 0 : 0) : null;
    MC.vote = v === null ? null : { id: t.id, ch: t.ch, v };
    for (const x of [MC.band, MC.rail]) {
      if (!x) continue;
      const hide = MC.vote === null;
      if (x.votes.hidden !== hide) { x.votes.hidden = hide; if (x === MC.band) fitBand(); }
      if (hide) continue;
      const name = INFO[t.ch].name;
      x.yes.setAttribute('aria-pressed', String(v > 0));
      x.no.setAttribute('aria-pressed', String(v < 0));
      x.yes.title = v > 0 ? '取消（這首屬於 ' + name + '）' : '這首屬於 ' + name;
      x.no.title = v < 0 ? '取消（這首不屬於 ' + name + '）' : '這首不屬於 ' + name;
    }
  }

  /**
   * 👎: the track is not wanted now either: out of the queue playing, on to the next one. Taking the current item out
   * of the queue makes the player go on with the one after it; the last one goes to the next first (the player then
   * stops or starts over, as its repeat says).
   */
  async function skipOut(id) {
    const q = App.queue || {};
    const ids = q.ids || [];
    const i = ids[q.index] === id ? q.index : ids.indexOf(id);
    if (i < 0) { Host.call('next'); return; }
    if (i === ids.length - 1) await Host.call('next');
    Host.call('queue.remove', { i });
  }

  /** The buttons' state for the track and channel playing now (asked again when either changes). */
  async function refreshVotes() {
    const t = voteTarget();
    if (!MC.rail && !MC.band) return;
    if (!t) { MC.voteKey = null; setVotes(null); return; }
    const key = t.id + '|' + t.ch;
    if (MC.voteKey !== key) setVotes(t, undefined);         // at once, before the answer: shown, nothing pressed
    MC.voteKey = key;
    const r = await MC.api.rpc('vote.get', { id: t.id, channel: t.ch }).catch(() => null);
    if (MC.voteKey === key) setVotes(t, r);
  }

  async function vote(v) {
    const t = voteTarget();
    if (!t) return;
    const cur = MC.vote && MC.vote.id === t.id && MC.vote.ch === t.ch ? MC.vote.v : 0;
    const next = cur === v ? 0 : v;
    const r = await MC.api.rpc('vote', { id: t.id, channel: t.ch, vote: next }).catch(() => null);
    if (!r) { toast('沒有記下來，請再按一次'); return; }
    setVotes(t, r);
    MC.counts = null;                                      // the channels' sizes may have changed
    if (MC.pageView && MC.pageView.isConnected) renderPage(MC.pageView);   // the channel page open: its counts at once
    if (next < 0) skipOut(t.id);                         // the lit button is the feedback: no message
  }

  /**
   * Turn the wheel so channel i is in the middle: every channel sits at its distance from i (wrapping round, as the
   * list is a loop), tilted and faded with the distance. A channel that moves one place slides there; one that wraps
   * from one end to the other jumps unseen.
   */
  function turnWheel(i, list, animate) {
    const r = rail();
    const at = list.indexOf(i), L = list.length;
    for (const [x, el] of r.items) if (!list.includes(x)) { el.remove(); r.items.delete(x); }
    list.forEach((x, k) => {
      let o = k - at;
      if (o > L / 2) o -= L;
      if (o < -Math.floor((L - 1) / 2)) o += L;
      let el = r.items.get(x);
      if (!el) {
        el = h('button', { class: 'mc-rail-item' },
          h('i', { class: 'mc-rail-art' }),
          h('span', { class: 'mc-rail-text' }, h('b', null, INFO[x].name), h('small', null, INFO[x].desc)));
        el.style.setProperty('--art', art(x));
        // the channel's picture comes alive on hover, as on the channel page's cards
        el.addEventListener('mouseenter', () => live(el, x));
        el.addEventListener('mouseleave', () => still(el));
        r.items.set(x, el);
        r.wheel.append(el);
      }
      const prev = el.dataset.o == null ? null : +el.dataset.o;
      const jump = !animate || prev == null || Math.abs(prev - o) > 1;
      el.classList.toggle('jump', jump);
      if (jump) void el.offsetWidth;                         // take the new place without the transition
      el.dataset.o = o;
      el.style.setProperty('--o', o);
      el.style.setProperty('--a', Math.abs(o));
      el.style.setProperty('--sc', o === 0 ? 1 : 0.8);
      el.classList.toggle('on', x === i);
      // the channel playing keeps its picture alive; the others come alive only while hovered
      el.keepLive = x === i;
      if (el.keepLive) live(el, x); else if (!el.matches(':hover')) still(el);
      el.classList.toggle('far', Math.abs(o) > 3);
      el.title = x === i ? '' : '切換到 ' + INFO[x].name;
      el.onclick = () => { if (x !== MC.showing) go(x, Math.sign(o)); };
      if (jump) requestAnimationFrame(() => requestAnimationFrame(() => el.classList.remove('jump')));
    });
  }

  /**
   * The now-playing page while a channel plays: the channel's picture as the backdrop (moodchannels.css) and, on the
   * left, the channel list as a picker wheel: the channel playing in the middle, ▲▼ or a click to turn it.
   */
  async function drawRail(el, src) {
    const radio = src.radio || (src.smart && SMART);
    document.documentElement.classList.toggle('mc-radio', !!radio);
    if (radio) { el.append(radioLabel(radio, 'mc-radio-rail', 'mc-rail-logo')); return; }   // a radio (or the Smart Playlist): its label, no wheel
    const i = src.channel;
    if (!INFO[i]) return;
    const r = rail();
    el.append(r);
    sync(i);
    refreshVotes();
    await nav(i);
    if (MC.showing === i) turnWheel(i, cycle(i), true);
  }

  const nowZone = (hr = new Date().getHours()) => hr >= 5 && hr < 10 ? 0 : hr < 16 && hr >= 10 ? 1 : hr >= 16 && hr < 19 ? 2 : hr >= 19 ? 3 : 4;

  /** Up to four different album covers from a channel, spread over the list so they are not all from one album. */
  function mosaic(ids, size) {
    const box = h('div', { class: 'mc-mosaic' });
    const seen = new Set();
    const step = Math.max(1, Math.floor(ids.length / 9));
    for (let i = 0; i < ids.length && seen.size < 4; i += step) {
      const t = Lib.trackById.get(ids[i]);
      if (!t) continue;
      const loose = t.album && t.album.loose;
      const key = loose ? 't:' + t.id : 'a:' + t.albumId;
      if (seen.has(key)) continue;
      seen.add(key);
      box.append(artBox('mc-tile', loose ? 't' : 'a', loose ? t.id : t.albumId, size / 2, t.album ? t.album.title : t.title));
    }
    box.dataset.n = seen.size;
    return box;
  }

  // the covers asked for at about the size they show (a mood card's mosaic spans the card, tiles up to ~150 px):
  // smaller ones are replaced by sharper ones once loaded, which shows the first time the page opens
  const coverSize = cls => cls === 'mc-hero' ? 220 : cls === 'mc-mood' ? 320 : 120;

  function card(ch, cls) {
    const info = INFO[ch.index];
    const el = h('div', {
      class: 'mc-card ' + cls,
      onclick: () => { if (el.count) playChannel(ch.index); },
      onmouseenter: () => { if (el.count) live(el, ch.index); },
      onmouseleave: () => still(el),
    },
      h('div', { class: 'mc-art' }),                      // the channel's picture (comes alive on hover)
      h('div', { class: 'mc-shade' }),                    // darkening for the white type, and the fine edge
      h('div', { class: 'mc-mosaic' }),
      h('div', { class: 'mc-meta' },
        cls === 'mc-hero' ? h('div', { class: 'mc-now' }, '現在') : null,
        h('div', { class: 'mc-name' }, info.name),
        h('div', { class: 'mc-desc' }, info.desc),
        h('div', { class: 'mc-count num' })));
    el.cls = cls;
    el.index = ch.index;
    el.style.setProperty('--art', art(ch.index));
    el.style.setProperty('--h1', info.hue[0]);        // the channel's colours (custom properties need setProperty)
    el.style.setProperty('--h2', info.hue[1]);
    fillCard(el, ch);
    return el;
  }

  /**
   * A card's tracks: its count, its covers (see below) and its play
   * button. The card itself stays: redrawing whole cards while the page is shown broke their pictures.
   */
  function fillCard(el, ch) {
    const n = ch.count, key = ch.ids.join(',');
    if (el.count === n && el.coversOf === key) return;          // nothing changed: leave the card untouched
    el.count = n;
    el.classList.toggle('empty', !n);
    const text = n ? `${n.toLocaleString()} 首` : '—', count = el.querySelector('.mc-count');
    if (count.textContent !== text) count.textContent = text;
    // the covers stay once there are four (the sample shifts as the channel grows: new covers every redraw would
    // keep the page moving); until then they follow the tracks found
    const full = el.querySelector('.mc-mosaic').dataset.n === '4';
    if (el.coversOf !== key && !(full && n)) {
      el.coversOf = key;
      el.querySelector('.mc-mosaic').replaceWith(n ? mosaic(ch.ids, coverSize(el.cls)) : h('div', { class: 'mc-mosaic' }));
    }
    const play = el.querySelector('.mc-play');
    if (n && !play) el.append(h('div', { class: 'mc-play', html: icon('play', true) }));
    else if (!n && play) play.remove();
  }

  /*
   * On hover the card's picture comes alive: the drawing itself (web/art/<i>.svg, whose elements are marked for it by
   * tools/gen_art.py) fades in over the still image and moves (stars twinkle, lights float, clouds drift …, see
   * moodchannels.css), slightly blurred. The still image stays the rest of the time: 14 live drawings stutter the page.
   */
  const svgs = new Map();
  let liveSeq = 0;
  const svgText = i => {
    if (!svgs.has(i)) svgs.set(i, fetch(`${ART}${i}.svg?v=${BG_VERSION}`).then(r => r.text()).catch(() => null));
    return svgs.get(i);
  };

  const PICTURE = '.mc-art, .mc-rail-art';                 // a card's picture, or a channel's on the now-playing wheel
  async function live(card, i) {
    card.liveOn = true;
    clearTimeout(card.liveOff);
    const box = card.querySelector(PICTURE);
    let svg = box.querySelector('svg');
    if (!svg) {
      const text = await svgText(i);
      if (!text || !card.liveOn || box.querySelector('svg')) return;
      // its own ids: the drawings all call their gradients "g", "b1" …, and several may be on the page
      const p = `mcl${++liveSeq}_`;
      box.insertAdjacentHTML('beforeend', text.replace(/<!--[\s\S]*?-->/g, '')
        .replace(/id="([^"]+)"/g, `id="${p}$1"`).replace(/url\(#([^)]+)\)/g, `url(#${p}$1)`));
      svg = box.lastElementChild;
      svg.classList.add('mc-live', 'mc-ch' + i);              // per channel, for its own sizes (moodchannels.css)
      void svg.getBoundingClientRect();
    }
    svg.classList.add('on');
  }

  function still(card) {
    if (card.keepLive) return;                               // the wheel's channel playing stays alive
    card.liveOn = false;
    const svg = card.querySelector(`:is(${PICTURE}) > svg`);   // :is(): a bare list would also match the picture itself
    if (!svg) return;
    svg.classList.remove('on');
    card.liveOff = setTimeout(() => { if (!card.liveOn) svg.remove(); }, 450);   // after its fade: the still image again
  }

  /**
   * Opening the page, the cards fade in one after another from the top left to the bottom right, as the album and
   * artist grids do: each waits by its distance from the top-left corner (the cards here are not a regular grid).
   */
  function sweepIn(body) {
    if (typeof Motion !== 'undefined' && Motion.quiet) return;
    const cards = [...body.querySelectorAll('.mc-card')];
    if (!cards.length) return;
    const at = cards.map(c => c.getBoundingClientRect());
    const x0 = Math.min(...at.map(r => r.left)), y0 = Math.min(...at.map(r => r.top));
    const d = at.map(r => (r.left - x0) + (r.top - y0));
    const far = Math.max(1, ...d);
    cards.forEach((c, k) => {
      c.style.setProperty('--d', Math.round(d[k] / far * 320) + 'ms');
      c.classList.add('mc-pop');
      c.addEventListener('animationend', e => { if (e.animationName === 'mc-pop') c.classList.remove('mc-pop'); });
    });
  }

  /**
   * The channel page. While the analysis runs it is asked again now and then: the cards on screen are then filled
   * anew in place (fillCard) rather than built again; the page is built again only when it opens, when the time
   * channel of now changed (another big card), or when there was nothing to show before.
   */
  async function renderPage(view, opening) {
    const data = await loadCounts();
    if (!view.isConnected) return;
    const s = MC.status || {};
    const sub = `${data.analysed.toLocaleString()} / ${data.total.toLocaleString()} 首已分析${s.pending ? ' · 背景分析中' : ''}`;
    const page = view.mcPage;
    if (!opening && page && page.zone === nowZone() && data.analysed) {
      if (page.sub && page.sub.textContent !== sub) page.sub.textContent = sub;
      for (const ch of data.channels) { const el = page.cards.get(ch.index); if (el) fillCard(el, ch); }
      return;
    }
    view.textContent = '';
    view.mcPage = null;
    const head = pageHead(h('span', { class: 'mc-title', html: logo('mc-title-logo') }, brand()), sub, null);
    view.append(head);
    const body = h('div', { class: 'mc-body' });         // the page's side margins, as under .page-head
    view.append(body);
    if (!data.analysed) {
      body.append(h('div', { class: 'mc-empty' }, `還沒有分析完成的曲目。分析在背景進行，可以在「設定 → 曲庫 → ${NAME}」查看進度與調整執行緒。`));
      return;
    }
    const zone = nowZone();
    const time = data.channels.filter(c => c.time);
    body.append(h('div', { class: 'mc-sect' }, '時段'));
    body.append(h('div', { class: 'mc-time' }, card(time[zone], 'mc-hero'),
      h('div', { class: 'mc-others' }, ...time.filter(c => c.index !== zone).map(c => card(c, 'mc-small')))));
    body.append(h('div', { class: 'mc-sect' }, '心情'));
    // Shuffle All first, then the eight moods
    const moods = data.channels.filter(c => !c.time);
    body.append(h('div', { class: 'mc-grid' }, ...[moods.find(c => c.index === SHUFFLE_ALL), ...moods.filter(c => c.index !== SHUFFLE_ALL)].map(c => card(c, 'mc-mood'))));
    view.mcPage = { zone, sub: head.querySelector('.sub'), cards: new Map([...body.querySelectorAll('.mc-card')].map(el => [el.index, el])) };
    if (opening) sweepIn(body);
  }

  /*
   * The mark: a heart with a heartbeat trace through it (icon/heart-wave-*.svg), in the theme's colours: the heart in
   * its pink with a fine line, the trace in its text colour edged with its background (white on the bar and the
   * wheel; moodchannels.css). The heart's shape is drawn once (defs()) and every copy of the mark uses it. While a
   * channel plays, the bar's and the wheel's marks beat (html.mc-beating): the heart pulses lub-dub and the trace runs
   * through it (moodchannels.css).
   */
  // the trace: a P bump, one QRS spike in the middle (where the heart beats as the trace crosses it) and a T bump;
  // fewer turns than the drawing in icon/, so it stays clear drawn thick at the size of an icon
  const WAVE = 'M4 104 L30 104 C38 92 50 92 58 104 L82 104 L90 116 L104 46 L122 154 L132 104 L148 104 C158 88 172 88 182 104 L196 104';
  const HEART = 'M100 172 C100 172 28 124 28 76 C28 50 46 32 70 32 C84 32 95 40 100 52 C105 40 116 32 130 32 C154 32 172 50 172 76 C172 124 100 172 100 172 Z';
  function defs() {
    if (document.getElementById('mc-defs')) return;
    document.body.insertAdjacentHTML('beforeend', `<svg id="mc-defs" width="0" height="0" style="position:absolute" aria-hidden="true"><defs>
      <path id="mc-heart" d="${HEART}"/></defs>
      <symbol id="i-mc-radio" viewBox="0 0 24 24"><circle cx="12" cy="12" r="1.8"/><path d="M8.6 8.6a4.8 4.8 0 0 0 0 6.8M15.4 8.6a4.8 4.8 0 0 1 0 6.8M5.7 5.7a8.9 8.9 0 0 0 0 12.6M18.3 5.7a8.9 8.9 0 0 1 0 12.6"/></symbol></svg>`);
  }
  const logo = (cls = '') => `<svg class="mc-logo ${cls}" viewBox="0 0 200 200" aria-hidden="true">`
    + `<g class="mc-pulse"><g transform="rotate(-12 100 102)"><use href="#mc-heart" class="mc-heart"/>`
    + `<ellipse class="mc-shine" cx="66" cy="62" rx="22" ry="14" transform="rotate(-38 66 62)"/></g></g>`
    + `<path class="mc-edge" d="${WAVE}"/><path class="mc-wave" d="${WAVE}"/><path class="mc-track" d="${WAVE}"/>`
    + `<path class="mc-run" d="${WAVE}" pathLength="100"/></svg>`;
  const NAV_ICON = logo('i');

  /** The name: plain text (NAME), and as a node for titles (brand()). */
  const NAME = 'MoodWave';
  const brand = () => h('span', { class: 'mc-brand' }, NAME);

  /** The mark in the player bar beats like a resting heart (60 BPM, lub-dub) while a channel plays; still while paused. */
  function beat() {
    const q = App.queue || {};
    const src = q.slotSource || q.source;
    const on = !!(src && src.ext === 'moodchannels' && App.state && App.state.playing);
    document.documentElement.classList.toggle('mc-beating', on);
  }


  MikuExt.register({
    id: 'moodchannels',
    async init(api) {
      MC.api = api;
      defs();
      // the cards' hover lift in whole screen pixels at this display scaling (moodchannels.css)
      const lift = () => document.documentElement.style.setProperty('--mc-lift', Math.round(6 * devicePixelRatio) / devicePixelRatio + 'px');
      lift();
      window.addEventListener('resize', lift);
      api.on('progress', set);
      const nav = MC.nav = api.addNav({ route: 'moods', label: NAME, icon: NAV_ICON, after: 'favorites' });
      nav.querySelector('span').replaceChildren(brand());
      showNav();
      // one channel after another (▲▼, the wheel): the band rolls to it as before, MIKU does not swap the bar's left part
      api.playerSlot({ bar: drawBand, nowPlaying: drawRail, leaveBar: pushOut,
        swap: (prev, next) => !(prev && next && prev.channel != null && next.channel != null) });
      // the mark before 「MoodWave Smart Playlist 已開啟」 under MIKU's 佇列播完後
      if (api.continueMark) api.continueMark(() => { const t = document.createElement('template'); t.innerHTML = logo('mc-cont-logo'); return t.content.firstChild; });
      // the queue drawer's header while MoodWave chooses what plays: a radio, or what comes after the list (auto-continue)
      if (api.queueHead) api.queueHead((el, src) => {
        const radio = src && src.ext === 'moodchannels' && src.radio;
        const after = !radio && radioReady() && App.settings.autoContinue === 'similar' && ((App.state && App.state.repeat) || 'off') === 'off';
        if (!radio && !after) return;
        el.append(h('div', { class: 'mc-qhead' },
          h('span', { class: 'mc-qhead-mark', html: logo('mc-qhead-logo') }),
          h('span', { class: 'mc-qhead-text' }, h('b', null, 'MoodWave Smart Playlist'),
            h('small', null, radio ? `${RADIO_KIND[radio.kind] || '電台'} · ${radio.name || ''}` : '播完後接著播放相似的歌'))));
      });
      // the song radio from a track, its artist or an album (only with the advanced AI and some of it analysed)
      if (api.addMenu) {
        api.addMenu('track', t => radioReady() ? [
          { label: '開始歌曲電台', icon: 'mc-radio', run: () => startRadio({ kind: 'track', ids: [t.id], name: t.title }) },
          t.artist ? { label: `開始歌手電台：${t.artist}`, icon: 'mc-radio', run: () => startRadio({ kind: 'artist', artist: t.artist, name: t.artist }) } : null,
        ] : null);
        api.addMenu('album', al => radioReady() ? [
          { label: '開始專輯電台', icon: 'mc-radio', run: () => startRadio({ kind: 'album', ids: al.tracks.map(t => t.id), name: al.title }) },
        ] : null);
      }
      Host.on('queue', beat);
      // leaving channel mode (another list plays): the next channel enters again with the push
      Host.on('queue', q => {
        if (q && q.source && q.source.ext === 'moodchannels') return;
        MC.inMode = false;
        if (MC.fades) for (const f of Object.values(MC.fades)) f.url = null;   // entering again fades in anew
      });
      Host.on('state', beat);
      // another track: the vote buttons show its votes (state comes often; only a new track asks)
      Host.on('state', s => { if (s && s.trackId !== MC.votedTrack) { MC.votedTrack = s.trackId; refreshVotes(); refreshRadioVotes(); } });
      beat();
      api.addRoute('moods', view => {
        MC.pageView = view;
        renderPage(view, true);
        // while the analysis runs, the channels grow: redraw now and then (not on every track)
        const timer = setInterval(() => { if (MC.status && MC.status.pending && !document.hidden) renderPage(view); }, 20000);
        return () => { clearInterval(timer); if (MC.pageView === view) MC.pageView = null; };
      });
      await refresh();
      MC.tuning = await api.rpc('tuning').catch(() => null);
      // 關於: the mark large on the left, the name, a line on what it is for, and its models' versions
      api.addSettings('about', host => {
        const st = MC.status || {}, dl = (st.ai && st.ai.download) || {};
        // each model's version in a light capsule, as Daphne's commit
        const ver = v => h('code', { class: 'mc-commit' }, String(v).slice(0, 6));
        const models = h('small', { class: 'mc-about-models' },
          st.engine ? h('span', null, '頻道模型', ver(st.engine)) : null,
          dl.ready && dl.version ? h('span', null, 'AI 模型', ver(dl.version)) : h('span', null, 'AI 模型未下載'));
        host.append(h('div', { class: 'field mc-about', html: logo('mc-about-logo') },
          h('div', { class: 'lbl' }, h('b', null, brand()), h('small', { class: 'mc-about-line' }, 'Your library, in the right mood.'),
            models)));
      });
      api.addSettings('library', (root, ui) => {
        MC.ui = ui;
        const s = MC.status || {}, ai = s.ai || {};
        const card = (title, ...kids) => h('div', { class: 'mc-sc' }, h('div', { class: 'mc-sc-title' }, title), ...kids);
        const sect = ui.section(h('span', { class: 'mc-title mc-sect-title', html: logo('mc-sect-logo') }, brand()),
          '分析每首歌的節奏、音色與調性，自動分成 5 個時段與 8 種心情頻道。開啟進階 AI 後，還能找出聽起來相似的歌，用在歌曲電台與 Smart Playlist。分析在背景進行，只有第一次比較久。');

        /* ── 基本功能: the channels ── */
        const cpus = s.cpus || 1;
        MC.basicBody = h('div', { class: 'mc-sc-body' });
        // models\basic\tuning.json: channel tuning learnt from votes, shipped with the module; the listener's own votes come
        // first. Asked again each time the block is drawn: just after MIKU starts the module may still be loading it
        const tuningBox = h('div', { class: 'mc-tuning' });
        const drawTuning = t => {
          tuningBox.textContent = '';
          if (!t || !t.channels || !t.channels.length) return;
          tuningBox.append(ui.field('使用內建的頻道調整', '依內建的投票學習結果，把不符合頻道感覺的歌移出；你自己投的讚與倒讚永遠優先',
            ui.sw(t.builtin !== false, v => api.rpc('set', { builtin: v }).then(r => { MC.tuning.builtin = v; MC.counts = null; set(r); }))));
        };
        drawTuning(MC.tuning);
        api.rpc('tuning').then(t => { if (!t) return; MC.tuning = t; if (tuningBox.isConnected) drawTuning(t); }).catch(() => { });
        MC.basicBody.append(tuningBox);
        MC.basicBody.append(ui.field('CPU 執行緒', `越多越快，但會佔用較多 CPU；播放時建議保留餘裕（這台電腦有 ${cpus} 個）`,
          ui.select(Array.from({ length: cpus }, (_, i) => [i + 1, `${i + 1} 條`]), s.threads || 1, v => api.rpc('set', { threads: +v }).then(set))));
        MC.block = h('div', { class: 'mc-progress' });
        MC.basicBody.append(h('div', { class: 'field mc-field' }, MC.block));
        sect.append(card('基本功能',
          ui.field(h('span', { class: 'mc-enable-title' }, '啟用', h('span', { class: 'mc-smart-title', html: logo('mc-smart-logo') }, 'MoodWave')), '關閉時不分析，也不顯示 MoodWave', ui.sw(s.enabled !== false, v => api.rpc('set', { enabled: v }).then(r => {
            set(r);
            // MoodWave off: the Smart Playlist cannot run either, so MIKU's own 佇列播完後 comes back
            if (!v && canContinue && smartOn()) setSmart(false);
          }))),
          MC.basicBody));

        /* ── 進階 AI: radio, Smart Playlist ── */
        // MoodWave Smart Playlist: MIKU's 「佇列播完後」 handed to this module ("similar"); MIKU's own choice is greyed out
        // meanwhile and comes back when this is turned off (kept as the ui pref autoContinuePrev). A MIKU without it: to come
        const canContinue = !!App.continuation;
        const smartOn = () => App.settings.autoContinue === 'similar';
        const setSmart = on => {
          if (on === smartOn()) return;
          if (on) setUiPref('autoContinuePrev', App.settings.autoContinue || 'off');
          Settings.set({ autoContinue: on ? 'similar' : uiPref('autoContinuePrev', 'off') });
          contSwitch.classList.toggle('on', on);
        };
        const contSwitch = ui.sw(canContinue && smartOn(), v => setSmart(v));
        const cont = ui.field(h('span', { class: 'mc-smart-title', html: logo('mc-smart-logo') }, 'MoodWave Smart Playlist',
          canContinue ? null : h('span', { class: 'mc-soon' }, '需要新版 MIKU')),
          '佇列播完後，接著播放風格相近的歌。開啟時「音訊 → 播放 → 佇列播完後」會暫停使用，關閉後恢復原本的選擇', contSwitch);
        if (!canContinue) cont.classList.add('mc-off');
        MC.aiSwitch = ui.sw(!!ai.enabled, v => api.rpc('set', { ai: v }).then(r => {
          set(r);
          if (!canContinue) return;
          // on: the smart playlist with it, unless the listener chose another way to go on (random albums / songs);
          // off: MIKU's own choice back (the smart playlist cannot run without the advanced AI)
          if (v && (App.settings.autoContinue || 'off') === 'off') setSmart(true);
          if (!v && smartOn()) setSmart(false);
        }));
        MC.aiField = ui.field('啟用進階 AI', `歌曲電台與 Smart Playlist。第一次開啟會下載約 ${mb((ai.download && ai.download.bytes) || 285e6)} MB 的模型（需要先啟用 MoodWave）`, MC.aiSwitch);
        MC.methodBox = h('div', { class: 'mc-method' });
        MC.aiBlock = h('div', { class: 'mc-progress' });
        MC.aiBody = h('div', { class: 'mc-sc-body' }, cont, MC.methodBox, h('div', { class: 'field mc-field' }, MC.aiBlock));
        sect.append(card('進階 AI', MC.aiField, MC.aiBody));

        // Progress redraws only the controls: keep the scrollable failure list mounted beside them.
        MC.manageBox = h('div', { class: 'mc-manage-controls' });
        MC.failBox = h('div', { class: 'mc-fails' });
        sect.append(h('div', { class: 'mc-manage' }, MC.manageBox, MC.failBox));
        drawMethod();
        foldCards();
        drawProgress();
        drawFailed();
      });
    },
  });
})();
