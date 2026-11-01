// Patrullgeneratorn (avancerad) – /patrullgenerator/{id}. Ordning: EN statusrad, STATIONSBILDEN (standard, alltid synlig), inställningar, det som skaver, övrigt bakom ett klick.
// Ingen databas skrivs före "Använd förslaget". Inställningarna bor i adressens hash. Förhandsvisningen kommer från StaffingController.GetPatrolPlan (engine=true).
(function () {
  'use strict';
  const page = document.getElementById('pgPage');
  if (!page) return;
  const comp = parseInt(page.dataset.competitionId, 10);
  const date = page.dataset.date || '';
  // Övning: samma sida och samma kod, men underlaget är ett scenario (påhittade skyttar) i stället för en tävling. Ingen skrivväg finns: Tillämpa är dold och servern kräver ett tävlings-id för den.
  const practice = page.dataset.practice === '1';
  const $ = id => document.getElementById(id);
  const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const token = () => (document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || '';
  const phone = () => window.matchMedia('(max-width: 760px)').matches;
  const GROUPS = ['C', 'B', 'A', 'R'];
  const gcol = g => 'var(--g' + (GROUPS.includes(g) ? g : 'X') + ')';
  const dur = m => { m = Math.round(m); if (m < 60) return m + ' min'; return Math.floor(m / 60) + ' h ' + String(m % 60).padStart(2, '0') + ' min'; };
  const hm = m => { m = Math.round(m); const d2 = m >= 1440; if (d2) m -= 1440; return (d2 ? 'dag 2 ' : '') + String(Math.floor(m / 60)).padStart(2, '0') + ':' + String(m % 60).padStart(2, '0'); };
  const mainGroup = p => Object.entries(p.groups).sort((a, b) => b[1] - a[1])[0][0];

  const FIELDS = [
    ['pgSize', 'size', ''], ['pgInt', 'int', ''], ['pgFirst', 'first', ''], ['pgClose', 'close', ''], ['pgRest', 'rest', '15'], ['pgPrio', 'prio', '1'],
    ['pgWish', 'wish', ''], ['pgOrder', 'order', 'auto'], ['pgChamp', 'champ', ''], ['pgGlideN', 'gn', '0'], ['pgGlideM', 'gm', '0'], ['pgRound', 'round', '60'], ['pgRoundMin', 'rm', ''], ['pgPause', 'pb', ''], ['pgPauseExt', 'pe', ''], ['pgOpen2', 'o2', ''], ['pgClose2', 'c2', ''], ['pgBreak', 'db', '1'], ['pgPre', 'pre', '0'], ['pgPreStart', 'ps', ''],
  ];
  const DEFAULTS = Object.fromEntries(FIELDS.map(f => [f[1], f[2]])); DEFAULTS.keep = '1'; DEFAULTS.d2 = '0'; DEFAULTS.ar = '1'; DEFAULTS.sc = '';   // sc = övningsscenariot (JSON, bara siffror)
  let P = Object.assign({}, DEFAULTS);
  let R = null, seq = 0, ctl = null, timer = null, lastServer = null, busy = false, needForceMsg = null;
  let hi = null; // skytt som markeras i bilden från detaljvisningen (utan att patrullvalet byts)
  let fixes = { key: '', rest: undefined, int: undefined, order: undefined, running: false };
  let view = phone() ? 'list' : 'sta', sel = null /* {k:'patrol'|'person', id} */, lastChange = null;

  // ---------- inställningar <-> fält <-> adress
  function fromHash() { const q = new URLSearchParams(location.hash.replace(/^#/, '')); P = Object.assign({}, DEFAULTS); q.forEach((v, k) => { if (k in P) P[k] = v; }); }
  function toFields() { FIELDS.forEach(([id, k]) => { const el = $(id); if (el) el.value = P[k]; }); $('pgKeep').checked = P.keep === '1'; $('pgDay2').checked = P.d2 === '1'; $('pgDay2Box').hidden = P.d2 !== '1'; }
  function fromFields() { FIELDS.forEach(([id, k]) => { const el = $(id); if (el) P[k] = el.value.trim(); }); P.keep = $('pgKeep').checked ? '1' : '0'; P.d2 = $('pgDay2').checked ? '1' : '0'; $('pgDay2Box').hidden = P.d2 !== '1'; }
  function toHash() { const q = new URLSearchParams(); Object.keys(P).forEach(k => { if (P[k] !== DEFAULTS[k]) q.set(k, P[k]); }); history.replaceState(null, '', location.pathname + (q.toString() ? '#' + q.toString() : '')); }
  function query(p) {
    const q = { competitionId: comp, planningId: 0, engine: true };
    if (practice) { delete q.competitionId; q.scenario = p.sc; }
    if (p.size) q.patrolSize = p.size; if (p.int) q.intervalMinutes = p.int;
    if (p.first) q.firstStart = (date || new Date().toISOString().slice(0, 10)) + 'T' + p.first;
    if (p.close) q.close = p.close; if (p.rest !== '') q.minRest = p.rest; if (p.wish !== '') q.wishRest = p.wish;
    q.prio = p.prio; q.order = p.order; if (p.champ) q.champ = p.champ; q.glideEvery = p.gn; q.glideMin = p.gm; q.roundExtra = p.round; if (p.rm) q.roundMin = p.rm;
    if (p.pb !== '') q.pauseBlock = p.pb; if (p.pe !== '') q.pauseExt = p.pe; if (p.d2 === '1') { q.day2 = true; if (p.o2) q.open2 = p.o2; if (p.c2) q.close2 = p.c2; q.dayBreak = p.db; } if (p.ar !== '1') q.arTogether = false; if (+p.pre > 0) { q.pre = p.pre; if (p.ps) q.preStart = p.ps; }
    if (p.keep !== '1') q.keep = false;
    return q;
  }
  function body(p, extra) {
    const q = query(p);
    return Object.assign({ competitionId: comp, engine: true, patrolSize: +q.patrolSize || 0, intervalMinutes: +q.intervalMinutes || 0, firstStart: q.firstStart || null, keep: p.keep === '1',
      minRest: p.rest === '' ? -1 : +p.rest, wishRest: p.wish === '' ? -1 : +p.wish, close: q.close || null, prio: +p.prio, order: q.order, champ: q.champ || null, glideEvery: +p.gn, glideMin: +p.gm, roundExtra: +p.round, roundMin: p.rm ? +p.rm : -1, pauseBlock: p.pb === '' ? -1 : +p.pb, pauseExt: p.pe === '' ? -1 : +p.pe, day2: p.d2 === '1', open2: p.o2 || null, close2: p.c2 || null, dayBreak: +p.db, arTogether: p.ar === '1', pre: +p.pre || 0, preStart: p.ps || null }, extra || {});
  }
  async function getJson(url, init, signal) { const r = await fetch(url, Object.assign({ headers: { Accept: 'application/json' }, signal }, init || {})); if (!r.ok) throw new Error('Server ' + r.status); return r.json(); }

  // ---------- omräkning (äldre svar slängs; tidigare bild står kvar, dämpad)
  function schedule(delay) { clearTimeout(timer); busy = true; setBusy(true); timer = setTimeout(recalc, delay == null ? 400 : delay); }
  function setBusy(b) { ['pgViz'].forEach(id => $(id).classList.toggle('pg-busy', b)); if (b && R) $('pgStatus').setAttribute('aria-busy', 'true'); else $('pgStatus').removeAttribute('aria-busy'); if (b) { $('pgApply').disabled = true; $('pgApplyHint').textContent = 'Räknar om…'; } }
  async function recalc() {
    const my = ++seq; if (ctl) ctl.abort(); ctl = new AbortController();
    try {
      const d = await getJson('/umbraco/surface/Staffing/GetPatrolPlan?' + new URLSearchParams(query(P)).toString(), null, ctl.signal);
      if (my !== seq) return;
      busy = false; setBusy(false); $('pgApplyHint').textContent = '';
      if (!d || !d.success) { fail((d && d.message) || 'Kunde inte räkna ut en startlista.'); return; }
      R = d; needForceMsg = null; lastServer = d.settings; render();
    } catch (e) { if (e.name === 'AbortError') return; if (my === seq) { busy = false; setBusy(false); fail('Kunde inte räkna ut en startlista just nu. Försök igen.'); } }
  }
  function fail(msg) { $('pgStatus').className = 'pg-status bad'; $('pgStatus').innerHTML = '<div class="big">' + esc(msg) + '</div>'; $('pgApply').disabled = true; }

  // ---------- rendering
  const T = r => r && r.timeline;
  const noFit = () => !!(R && R.metrics && R.metrics.feasible === false);
  function render() {
    if (!R) return;
    const keep = { x: $('pgScroll').scrollLeft, y: $('pgScroll').scrollTop };
    renderStatus(); renderNotes(); probeFixes(); renderLegend(); renderPicture(); renderScrape(); renderMoves(); renderMore(); renderSel();
    { const t0 = T(R), est = t0 && t0.roundMin; if (est && P.rm && +P.rm < est - 0.5) { $('pgRoundHint').textContent = 'Stationerna tar tillsammans minst ' + Math.round(est) + ' min (' + t0.stations.length + ' stationer × tiden mellan patruller), så rundan räknas som ' + Math.round(est) + ' min och inte ' + P.rm + '. Öka tiden mellan patruller eller ange en längre rundtid för att ändra det.'; } else if (est) { $('pgRoundMin').placeholder = Math.round(est); $('pgRoundHint').textContent = P.rm ? 'Tid för en hel runda på banan (du har angett den).' : 'Tid för en hel runda på banan. Just nu uppskattad till ' + Math.round(est) + ' min (10 min per station). Ange den uppmätta tiden för ett säkrare resultat: den styr hur länge skyttar med flera starter måste vänta mellan klasserna.'; } }
    if (lastServer) { if (!P.size) $('pgSize').placeholder = lastServer.patrolSize; if (!P.int) $('pgInt').placeholder = lastServer.intervalMinutes; if (!P.first && lastServer.firstStart) $('pgFirst').value = lastServer.firstStart.slice(11, 16); }
    ['vSta', 'vTl', 'vList'].forEach(id => $(id).setAttribute('aria-pressed', String(({ vSta: 'sta', vTl: 'tl', vList: 'list' })[id] === view)));
    $('pgScroll').hidden = view === 'list'; $('pgListPane').hidden = view !== 'list';
    $('pgFlowBox').style.display = view === 'sta' ? 'flex' : 'none'; $('pgFlow').checked = flow;
    $('pgScroll').scrollLeft = keep.x; $('pgScroll').scrollTop = keep.y;
    updateApply();
  }
  function roundUp(h) { const [a, b] = h.split(':').map(Number); const t = Math.ceil((a * 60 + b) / 5) * 5; return String(Math.floor(t / 60) % 24).padStart(2, '0') + ':' + String(t % 60).padStart(2, '0'); }
  function problems() {
    const m = R.metrics, out = [];
    if (m.underMin) out.push(m.underMin + ' skyttar får kortare vila än minsta vila' + (m.underMinNames && m.underMinNames.length ? ' (' + m.underMinNames.slice(0, 4).map(esc).join(', ') + (m.underMinNames.length > 4 ? ' …' : '') + ')' : ''));
    if (m.collisions) out.push(m.collisions + ' gånger är två patruller på samma station samtidigt');
    if (m.overMinutes > 0.5) out.push('dagen blir ' + dur(m.overMinutes) + ' för lång');
    if (m.cap) out.push(m.cap + (m.cap === 1 ? ' skytt får' : ' skyttar får') + ' inte plats: patrullerna har fler skyttar än platser');
    if (m.staffBad) out.push(m.staffBad + (m.staffBad === 1 ? ' start krockar' : ' starter krockar') + ' med ett funktionärsuppdrag');
    if (m.link) out.push(m.link + (m.link === 1 ? ' skytt får' : ' skyttar får') + ' inte gå i samma patrull som den de valt');
    return out;
  }
  // Orsaken till ett uppehåll: den skytt vars andra start sitter tätast efter att den första rundan är slut plus vilan (tidigare start + rundtid + vila).
  function pauseCause(z) {
    const t = T(R), round = t && t.roundMin, rest = +P.rest || 0, mins = s => { const a = (s || '').split(':').map(Number); return a[0] * 60 + a[1]; };
    if (!round || !z.beforePatrol) return null;
    const starts = {}; R.patrols.forEach(p => (p.members || []).forEach(m => { (starts[m.memberId] = starts[m.memberId] || []).push({ n: p.number, t: mins(p.time), name: m.name }); }));
    let best = null;
    Object.values(starts).forEach(v => {
      v.sort((a, b) => a.t - b.t);
      for (let i = 1; i < v.length; i++) if (v[i].n >= z.beforePatrol && v[i - 1].n < z.beforePatrol) { const slack = v[i].t - (v[i - 1].t + round + rest); if (!best || slack < best.slack) best = { slack, a: v[i - 1], b: v[i] }; }
    });
    if (!best) return null;
    const f = x => hm(x);
    return esc(best.a.name) + ' startar i patrull ' + best.a.n + ' kl ' + f(best.a.t) + ', är klar ' + f(best.a.t + round) + ' (rundan är ' + Math.round(round) + ' min) och behöver ' + rest + ' min vila, tidigast ' + f(best.a.t + round + rest) + ' i patrull ' + best.b.n + ' kl ' + f(best.b.t) + '.';
  }
  function renderStatus() {
    const m = R.metrics, t = T(R), k = t && t.kpi, st = $('pgStatus');
    if (!m || !k) { st.className = 'pg-status wait'; st.innerHTML = '<div class="big">Inget förslag än.</div>'; return; }
    const ok = m.feasible;
    st.className = 'pg-status' + (ok ? '' : ' bad');
    let h = '<div class="big">' + (ok ? '✓ Det går ihop. ' : '⚠ Det går inte ihop. ') + '<span style="font-weight:400">' + (ok ? k.patrols + ' patruller, ' + k.starts + ' starter. Klart ca ' + esc(k.lastEnd || '–') + (P.close ? ' (tillåtet ' + esc(P.close) + ')' : '') + '.' : problems().join(' · ') + '.') + '</span></div>';
    h += '<div class="nums"><span>Patruller <b>' + k.patrols + '</b></span><span>Sista start <b>' + esc(k.lastStart || '–') + '</b></span>' + (ok ? '' : '<span>Klart ca <b>' + esc(k.lastEnd || '–') + '</b></span>') +
      '<span class="' + (k.underMin ? 'pg-bad' : '') + '">För kort vila <b>' + k.underMin + '</b></span><span class="' + (k.collisions ? 'pg-bad' : '') + '">Krockar <b>' + k.collisions + '</b></span></div>';
    t.pauses.filter(z => z.needMin > z.setMin + 0.5 && z.beforePatrol).slice(0, 2).forEach(z => {
      const why = pauseCause(z);
      h += '<div class="pg-hint mt-1">Uppehåll före patrull ' + z.beforePatrol + ': ' + dur(z.needMin) + ' mellan starterna i stället för ' + dur(z.setMin) + '. Skälet: ' + (why || 'skyttar som skjuter flera klasser behöver sin vila efter att rundan är slut.') + '</div>';
    });
    if (R.blocked) h +='<div class="mt-2 pg-bad"><b>' + esc(R.blocked) + '</b> Skyttarna kan ha planerat efter tiderna. Patrullnumren behålls och ingen skytt som har resultat flyttas.</div>';
    if (!ok) {
      const f = [];
      const fk = JSON.stringify(P), known = fixes.key === fk;
      if (m.underMin > 0 && +P.rest > 0 && known && fixes.rest != null) f.push('<button type="button" class="btn btn-outline-primary" data-fix="rest" data-v="' + fixes.rest + '">Sänk minsta vila till ' + fixes.rest + ' min</button>');
      if (m.overMinutes > 0.5 && m.lastEnd) { const t2 = roundUp(m.lastEnd); f.push('<button type="button" class="btn btn-outline-primary" data-fix="close" data-v="' + t2 + '">Förläng dagen till ' + t2 + '</button>'); }
      if (m.collisions > 0 && known && fixes.int != null) f.push('<button type="button" class="btn btn-outline-primary" data-fix="int" data-v="' + fixes.int + '">Öka tiden mellan patruller till ' + fixes.int + ' min</button>');
      if (m.underMin > 0 && known && fixes.order) f.push('<button type="button" class="btn btn-outline-primary" data-fix="order">Låt generatorn välja hur klasserna delas upp</button>');
      if (!known && (m.underMin > 0 || m.collisions > 0)) h += '<div class="pg-hint mt-1">Räknar ut vad som krävs…</div>';
      else if (known && m.underMin > 0 && fixes.rest == null && m.collisions === 0 && !(m.overMinutes > 0.5)) h += '<div class="pg-hint mt-1">Det räcker inte att sänka vilan. Prova att öka tiden mellan patruller, förlänga dagen eller minska antal skyttar per patrull.</div>';
      if (f.length) h += '<div class="pg-fixes">' + f.join('') + '</div><div class="pg-hint mt-1">Att sänka minsta vila är en regeländring: skyttarna får då kortare tid mellan sina starter.</div>';
      else if (!f.length) h += '<div class="pg-hint mt-1">Ändra något i inställningarna till vänster. Bilden nedan visar bästa försöket, med röda markeringar där det skaver.</div>';
    }
    if (lastChange) h += '<div class="pg-hint mt-1">' + esc(lastChange.text) + ' <button type="button" class="btn btn-link p-0 align-baseline" data-undofix="1">Ångra</button></div>';
    st.innerHTML = h;
  }
  // "Vad krävs?": prövar några värden mot motorn (samma förhandsvisning) och visar det minsta som gör att det går ihop. Inget skrivs.
  async function probeFixes() {
    if (!R || !R.metrics || R.metrics.feasible !== false) return;
    const key = JSON.stringify(P); if (fixes.key === key || fixes.running) return;
    fixes.running = true; const res = { key, rest: null, int: null }, base = Object.assign({}, P), m = R.metrics;
    const feasible = async patch => { try { const d = await getJson('/umbraco/surface/Staffing/GetPatrolPlan?' + new URLSearchParams(query(Object.assign({}, base, patch))).toString()); return !!(d && d.success && d.metrics && d.metrics.feasible); } catch (e) { return false; } };
    if (m.underMin > 0 && +base.rest > 0) { const cur = +base.rest, cands = [60, 45, 30, 25, 20, 15, 10, 5, 0].filter(v => v < cur); for (let i = cands.length - 1; i >= 0; i--) { /* minsta ändring först */ } for (const v of cands) { if (key !== JSON.stringify(P)) break; if (await feasible({ rest: String(v) })) { res.rest = v; break; } } }
    if (m.underMin > 0 && base.order !== 'auto') { if (key === JSON.stringify(P) && await feasible({ order: 'auto' })) res.order = true; }
    if (m.collisions > 0) { const cur = +base.int || (lastServer && lastServer.intervalMinutes) || 15; for (const v of [cur + 5, cur + 10, cur + 15, cur + 20, cur + 30]) { if (key !== JSON.stringify(P)) break; if (await feasible({ int: String(v) })) { res.int = v; break; } } }
    fixes.running = false;
    if (key === JSON.stringify(P)) { fixes = Object.assign({ running: false }, res); renderStatus(); } else probeFixes();
  }
  function renderLegend() {
    $('pgLegend').innerHTML = GROUPS.map(g => '<span><i style="background:' + gcol(g) + '"></i>Klass ' + g + '</span>').join('') + '<span><i style="background:var(--pg-bad)"></i>Problem: två patruller på samma station / för kort vila</span>';
  }
  function renderPicture() {
    const t = T(R); if (!t || !t.patrols.length) { $('pgScroll').innerHTML = '<p class="p-3 mb-0">Inga patruller.</p>'; $('pgListPane').innerHTML = ''; $('pgNote').textContent = ''; return; }
    if (view === 'tl') renderTimeline(t); else renderStations(t);
    renderTable(t);
  }
  function patrolAria(p) { return 'Patrull ' + p.number + ', ' + p.start + ' till ' + p.end + ', ' + Object.entries(p.groups).map(([g, n]) => n + ' ' + g).join(' och ') + (p.collStations && p.collStations.length ? ', två patruller samtidigt vid station ' + p.collStations.join(', ') : '') + (p.bad ? ', bryter en regel' : ''); }

  // "Före och efter": linjer i stationsbilden från den markerade patrullens skyttar till patrullerna där de skjuter härnäst (och kom ifrån). En linje per patrull, med antal skyttar och vilan.
  let flow = true; try { flow = localStorage.getItem('pgFlow') !== '0'; } catch (e) { /* utan lagring är den på */ }
  let flowNote = '';
  function flowLines(t, ps, sts, x, top, rowH) {
    flowNote = '';
    if (!flow || !sel || sel.k !== 'patrol') return '';
    const cur = ps.find(p => p.number === sel.id); if (!cur) return '';
    const last = sts.length - 1, cy = i => top + i * rowH + 3 + (rowH - 8) / 2;
    const endX = p => x(p.startMin + (p.arrive[last] || 0) + (p.occupy[last] || 0)), startX = p => x(p.startMin + (p.arrive[0] || 0));
    const groups = new Map(); let withOther = 0;
    membersOf(sel.id).forEach(m => {
      const per = t.persons.find(z => z.memberId === m.memberId); let any = false;
      if (per) per.pairs.forEach(pr => {
        const dir = pr.fromPatrol === sel.id ? 'ahead' : pr.toPatrol === sel.id ? 'behind' : null; if (!dir) return;
        const other = dir === 'ahead' ? pr.toPatrol : pr.fromPatrol, k = dir + ':' + other, g = groups.get(k) || { dir, other, n: 0, idle: [], slack: [] };
        g.n++; g.idle.push(pr.idleMin); g.slack.push(pr.slackMin); groups.set(k, g); any = true;
      });
      if (any) withOther++;
    });
    const total = membersOf(sel.id).length;
    flowNote = ' Linjerna visar var skyttarna i den markerade patrullen skjuter före och efter: siffran är antal skyttar, tiden är vilan mellan starterna. ' + (total - withOther) + ' av ' + total + ' skjuter bara här.';
    const items = [...groups.values()].map(g => {
      const q = ps.find(p => p.number === g.other); if (!q) return null;
      const from = g.dir === 'ahead' ? cur : q, to = g.dir === 'ahead' ? q : cur;
      return Object.assign(g, { x1: endX(from), y1: cy(last), x2: startX(to), y2: cy(0) });
    }).filter(Boolean).sort((a, b) => a.x2 - b.x2);
    let out = '';
    items.forEach(g => {
      const bad = Math.min(...g.slack) < 0, long = !bad && Math.max(...g.slack) > 60, dx = Math.max(24, Math.abs(g.x2 - g.x1) / 2), ahead = g.dir === 'ahead';
      const col = bad ? 'var(--pg-bad)' : 'var(--bs-body-color)', w = ahead ? 2.2 : 1.3, dash = bad ? '5 3' : long ? '1.5 4' : '';
      const d = 'M' + g.x1 + ',' + g.y1 + ' C' + (g.x1 + dx) + ',' + g.y1 + ' ' + (g.x2 - dx) + ',' + g.y2 + ' ' + g.x2 + ',' + g.y2;
      out += '<g class="pg-flow" data-dir="' + g.dir + '" data-to="' + g.other + '" data-n="' + g.n + '" data-bad="' + (bad ? 1 : 0) + '" style="pointer-events:none" opacity="' + (ahead ? 1 : .7) + '">' +
        '<path d="' + d + '" fill="none" stroke="var(--bs-body-bg)" stroke-width="' + (w + 3) + '" opacity=".8"/>' +
        '<path d="' + d + '" fill="none" stroke="' + col + '" stroke-width="' + w + '"' + (dash ? ' stroke-dasharray="' + dash + '"' : '') + '/>' +
        '<polygon points="' + g.x2 + ',' + g.y2 + ' ' + (g.x2 - 8) + ',' + (g.y2 - 4.5) + ' ' + (g.x2 - 8) + ',' + (g.y2 + 4.5) + '" fill="' + col + '"/></g>';
    });
    // Linjer som slutar nära varandra får EN gemensam etikett (antal skyttar och vilan, från kortaste till längsta); annars blir etiketterna en hög.
    const clusters = [];
    ['ahead', 'behind'].forEach(dir => {
      let c = null;
      items.filter(g => g.dir === dir).forEach(g => { if (c && g.x2 - c.x2 < 60) { c.n += g.n; c.idle.push(...g.idle); c.slack.push(...g.slack); c.x2 = g.x2; } else { c = { dir, n: g.n, idle: [...g.idle], slack: [...g.slack], x2: g.x2, y2: g.y2 }; clusters.push(c); } });
    });
    const laneEnd = [];
    clusters.sort((a, b) => a.x2 - b.x2).forEach(c => {
      const lo = Math.min(...c.idle), hi2 = Math.max(...c.idle), bad = Math.min(...c.slack) < 0, col = bad ? 'var(--pg-bad)' : 'var(--bs-body-color)';
      const label = (c.dir === 'ahead' ? '' : '← ') + c.n + ' · vila ' + dur(lo) + (hi2 - lo >= 1 ? '–' + dur(hi2) : '') + (bad ? ' !' : ''), lw = label.length * 6.2 + 6;
      let lane = 0; while (laneEnd[lane] != null && laneEnd[lane] > c.x2 - lw - 4 && lane < 4) lane++;
      if (lane >= 4) return; laneEnd[lane] = c.x2;
      out += '<text class="pg-flow-label" x="' + (c.x2 - 11) + '" y="' + (c.y2 + 4 + lane * 11) + '" text-anchor="end" style="pointer-events:none;fill:' + col + ';font-weight:600;paint-order:stroke;stroke:var(--bs-body-bg);stroke-width:3px">' + esc(label) + '</text>';
    });
    return out;
  }
  // STATIONSBILDEN: en rad per station, tid längs x, en ruta per patrull vid stationen (ankomst till avgång), färg per huvudgrupp, röd ruta när två patruller är där samtidigt.
  function renderStations(t) {
    const ps = t.patrols, sts = t.stations && t.stations.length ? t.stations : [{ index: 1, number: 1, name: null }];
    const all = ps;
    const tmin = Math.floor(Math.min(...all.map(p => p.startMin)) / 30) * 30, tmax = Math.ceil(Math.max(...all.map(p => p.endMin)) / 30) * 30;
    const W = 1100, L = 175, rowH = Math.max(28, Math.min(56, Math.floor(520 / sts.length))), top = 24, H = top + sts.length * rowH + 8, x = m => L + (m - tmin) / Math.max(1, tmax - tmin) * W;
    let s = '<svg id="pgSvg" viewBox="0 0 ' + (L + W + 20) + ' ' + H + '" role="img" aria-label="Stationsbild: en rad per station, en ruta per patrull" aria-describedby="pgNote">';
    for (let m = tmin; m <= tmax; m += 30) s += '<line x1="' + x(m) + '" x2="' + x(m) + '" y1="' + (top - 4) + '" y2="' + H + '" stroke="var(--bs-border-color)" ' + (m % 60 ? 'stroke-dasharray="2 4"' : '') + '/>' + (m % 60 === 0 ? '<text x="' + (x(m) + 3) + '" y="12">' + hm(m) + '</text>' : '');
    sts.forEach((st, i) => { const y = top + i * rowH; s += '<text x="4" y="' + (y + 17) + '" style="fill:var(--bs-body-color)">' + 'Station ' + st.number + (st.name ? ' ' + esc(String(st.name).length > 14 ? String(st.name).slice(0, 13) + '…' : String(st.name)) : '') + '<title>' + esc('Station ' + st.number + (st.name ? ' ' + st.name : '')) + '</title></text><line x1="' + L + '" x2="' + (L + W) + '" y1="' + (y + rowH) + '" y2="' + (y + rowH) + '" stroke="var(--bs-border-color)" opacity=".5"/>'; });
    ps.forEach(p => {
      const selP = sel && sel.k === 'patrol' && sel.id === p.number, hiId = sel && sel.k === 'person' ? sel.id : hi, personSel = hiId != null && (T(R).persons.find(z => z.memberId === hiId) || { starts: [] }).starts.some(z => z.patrol === p.number);
      sts.forEach((st, i) => {
        const a = p.startMin + (p.arrive[i] || 0), o = p.occupy[i] || 0, x0 = x(a), w = Math.max(2, x(a + o) - x0), y = top + i * rowH + 3, hit = p.collStations && p.collStations.includes(i + 1);
        s += '<g class="hit" data-patrol="' + p.number + '"><title>' + esc('Patrull ' + p.number + ' · station ' + st.number + ' · ' + hm(a) + '–' + hm(a + o) + ' · ' + Object.entries(p.groups).map(([g, n]) => g + ' ' + n).join(' + ') + (hit ? ' · TVÅ PATRULLER SAMTIDIGT' : '') + '\n' + membersOf(p.number).map(m => m.name + (m.club ? ' (' + m.club + ')' : '')).join(', ')) + '</title>' +
          '<rect class="f" x="' + x0 + '" y="' + y + '" width="' + w + '" height="' + (rowH - 8) + '" fill="' + (hit ? 'var(--pg-bad)' : gcol(mainGroup(p))) + '" opacity="' + (hit ? 1 : .9) + '" stroke="' + (selP || personSel ? 'var(--bs-body-color)' : hit ? '#fff' : 'none') + '" stroke-width="' + (selP || personSel ? 2.5 : 1) + '"' + (hit ? ' stroke-dasharray="3 2"' : '') + '/>' +
          (w >= 16 ? '<text x="' + (x0 + 3) + '" y="' + (y + 13) + '" style="fill:#fff;font-weight:600">' + p.number + (hit ? '!' : '') + '</text>' : '') + '</g>';
      });
    });
    s += flowLines(t, ps, sts, x, top, rowH);
    $('pgScroll').innerHTML = s + '</svg>';
    const hits = ps.filter(p => p.collStations && p.collStations.length).length;
    $('pgNote').textContent = 'Varje ruta är en patrull vid en station (ankomst till avgång). Rött = två patruller är vid samma station samtidigt (' + hits + ' patruller berörda). Klicka på en ruta för att se vilka som skjuter. ' + ps.length + ' patruller, ' + ps[0].start + '–' + ps[ps.length - 1].end + '. Tider är uppskattningar.' + flowNote;
  }
  // Patrulltidslinjen: en rad per patrull (hela patrullens tid), mörk del = extra tid när olika skjuttider blandas.
  function renderTimeline(t) {
    const ps = t.patrols, all = ps;
    const tmin = Math.floor(Math.min(...all.map(p => p.startMin)) / 60) * 60, tmax = Math.ceil(Math.max(...all.map(p => p.endMin)) / 60) * 60;
    const W = 1000, L = 150, rowH = 18, top = 24, H = top + ps.length * rowH + 8, x = m => L + (m - tmin) / Math.max(1, tmax - tmin) * W;
    let s = '<svg id="pgSvg" viewBox="0 0 ' + (L + W + 20) + ' ' + H + '" role="group" aria-label="Patruller över tid" aria-describedby="pgNote">';
    for (let m = tmin; m <= tmax; m += 60) s += '<line x1="' + x(m) + '" x2="' + x(m) + '" y1="' + (top - 4) + '" y2="' + H + '" stroke="var(--bs-border-color)"/><text x="' + (x(m) + 3) + '" y="12">' + hm(m) + '</text>';
    ps.forEach((p, i) => {
      const y = top + i * rowH, selP = sel && sel.k === 'patrol' && sel.id === p.number;
      const x0 = x(p.startMin), w = Math.max(3, x(p.endMin) - x0), tot = Object.values(p.groups).reduce((a, b) => a + b, 0) || 1, wExtra = p.extraMin > 0 ? Math.max(2, p.extraMin / Math.max(1, p.endMin - p.startMin) * w) : 0;
      s += '<g class="hit" tabindex="0" role="button" data-patrol="' + p.number + '" aria-label="' + esc(patrolAria(p)) + '"><rect class="f" x="0" y="' + y + '" width="' + (L + W + 20) + '" height="' + rowH + '" fill="transparent"/><text x="4" y="' + (y + 12) + '" style="fill:var(--bs-body-color)">' + String(p.number).padStart(2, ' ') + ' ' + p.start + ' ' + esc(Object.keys(p.groups).join('+')) + ' ' + tot + '</text>';
      let cx = x0; const bw = w - wExtra;
      Object.entries(p.groups).forEach(([g, n]) => { const gw = bw * n / tot; s += '<rect x="' + cx + '" y="' + (y + 2) + '" width="' + gw + '" height="' + (rowH - 5) + '" fill="' + gcol(g) + '" opacity=".92"/>'; cx += gw; });
      if (wExtra) s += '<rect x="' + (x0 + bw) + '" y="' + (y + 2) + '" width="' + wExtra + '" height="' + (rowH - 5) + '" fill="#000" opacity=".4"/>';
      s += '<rect x="' + x0 + '" y="' + (y + 2) + '" width="' + w + '" height="' + (rowH - 5) + '" fill="none" stroke="' + (selP ? 'var(--bs-body-color)' : p.bad ? 'var(--pg-bad)' : 'none') + '" stroke-width="' + (selP ? 2.5 : 2) + '"/></g>';
    });
    const hiT = sel && sel.k === 'person' ? sel.id : hi;
    if (hiT != null) { const per = t.persons.find(z => z.memberId === hiT);
      if (per) per.pairs.forEach(pr => { const a = ps.findIndex(p => p.number === pr.fromPatrol), b = ps.findIndex(p => p.number === pr.toPatrol); if (a < 0 || b < 0) return;
        s += '<line x1="' + x(ps[a].endMin) + '" y1="' + (top + a * rowH + 9) + '" x2="' + x(ps[b].startMin) + '" y2="' + (top + b * rowH + 9) + '" stroke="var(--bs-body-color)" stroke-width="1.5" stroke-dasharray="4 3"/><text x="' + (x(ps[b].startMin) + 4) + '" y="' + (top + b * rowH + 6) + '" style="fill:var(--bs-body-color)">' + (pr.slackMin >= 0 ? 'väntar ' + dur(pr.slackMin) + ' över vilan' : 'för tidigt ' + dur(-pr.slackMin)) + '</text>'; }); }
    $('pgScroll').innerHTML = s + '</svg>';
    $('pgNote').textContent = 'Varje rad är en patrull från start till uppskattat slut. Mörk del = extra tid när olika skjuttider blandas. Klicka på en rad för att se vilka som skjuter.';
    const svg = $('pgSvg'); svg.addEventListener('keydown', e => { const rows = [...svg.querySelectorAll('.hit')], i = rows.indexOf(document.activeElement); if (i < 0) return; if (e.key === 'ArrowDown') { e.preventDefault(); (rows[i + 1] || rows[i]).focus(); } else if (e.key === 'ArrowUp') { e.preventDefault(); (rows[i - 1] || rows[i]).focus(); } else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectPatrol(+rows[i].dataset.patrol); } else if (e.key === 'Escape') { sel = null; render(); } });
  }
  // Tabell/kort: tabellalternativet till bilden och standard på telefon.
  function renderTable(t) {
    const row = p => '<tr data-patrol="' + p.number + '" class="' + (sel && sel.k === 'patrol' && sel.id === p.number ? 'sel' : '') + '" style="cursor:pointer"><td><b>' + p.number + '</b></td><td>' + p.start + '–' + p.end + '</td><td>' + esc(Object.entries(p.groups).map(([g, n]) => g + ' ' + n).join(' + ')) + '</td><td>' + (p.collStations && p.collStations.length ? '<span class="pg-bad">⚠ två patruller samtidigt vid station ' + p.collStations.join(', ') + '</span>' : p.bad ? '<span class="pg-bad">⚠ för kort vila</span>' : '<span class="pg-ok">ok</span>') + '</td></tr>';
    const cards = t.patrols.map(p => { const tot = Object.values(p.groups).reduce((a, b) => a + b, 0) || 1;
      return '<button type="button" class="pg-card ' + (p.bad || (p.collStations && p.collStations.length) ? 'bad' : '') + '" data-patrol="' + p.number + '"><b>Patrull ' + p.number + '</b> · ' + p.start + '–' + p.end + ' <span class="pg-hint">' + esc(Object.keys(p.groups).join('+')) + ', ' + tot + ' skyttar</span><div class="bar">' + Object.entries(p.groups).map(([g, n]) => '<i style="width:' + (n / tot * 100) + '%;background:' + gcol(g) + '"></i>').join('') + '</div>' +
        (p.collStations && p.collStations.length ? '<span class="pg-bad">⚠ Två patruller samtidigt vid station ' + p.collStations.join(', ') + '</span>' : p.bad ? '<span class="pg-bad">⚠ För kort vila</span>' : '') + '</button>'; }).join('');
    $('pgListPane').innerHTML = '<div class="pg-cards">' + cards + '</div><div class="pg-tbl-wide" style="max-height:62vh;overflow:auto"><table><thead><tr><th>Nr</th><th>Tid</th><th>Klass</th><th>Status</th></tr></thead><tbody>' + t.patrols.map(row).join('') + '</tbody></table></div>';
  }
  function membersOf(num) { const p = (R.patrols || []).find(x => x.number === num); return p ? p.members : []; }
  // Detaljvisningen för en vald patrull: varje skytt med sina ANDRA starter (skyttens tidslinje): en liten remsa över dagen, vilka patruller, och vilan mellan starterna.
  function renderSel() {
    const t = T(R); let h = '';
    if (sel && sel.k === 'patrol') {
      const p = t.patrols.find(x => x.number === sel.id);
      if (p) {
        const all = t.patrols, tmin = Math.floor(Math.min(...all.map(x => x.startMin)) / 60) * 60, tmax = Math.ceil(Math.max(...all.map(x => x.endMin)) / 60) * 60, W = 360, x = m => (m - tmin) / Math.max(1, tmax - tmin) * W;
        const rows = membersOf(sel.id).map(m => {
          const per = t.persons.find(z => z.memberId === m.memberId);
          const strip = per ? '<svg viewBox="0 0 ' + W + ' 16" width="100%" height="16" style="max-width:420px;display:block" aria-hidden="true"><rect x="0" y="6" width="' + W + '" height="4" fill="var(--bs-secondary-bg)"/>' +
            per.starts.map(s => { const pp = all.find(q => q.number === s.patrol); if (!pp) return ''; const cur = s.patrol === sel.id; return '<rect x="' + x(pp.startMin) + '" y="' + (cur ? 1 : 3) + '" width="' + Math.max(3, x(pp.endMin) - x(pp.startMin)) + '" height="' + (cur ? 14 : 10) + '" rx="2" fill="' + gcol(s.group) + '" stroke="' + (cur ? 'var(--bs-body-color)' : 'none') + '" stroke-width="1.5"/>'; }).join('') + '</svg>' : '';
          const starts = per ? per.starts.map(s => '<button type="button" class="btn btn-link p-0 align-baseline" data-patrol="' + s.patrol + '"' + (s.patrol === sel.id ? ' style="font-weight:700;text-decoration:none"' : '') + '>' + esc(s.group) + ' ' + s.start + '–' + s.end + ' (patrull ' + s.patrol + ')</button>').join(' → ') : '';
          const rest = per ? per.pairs.map(pr => '<span class="' + (pr.slackMin < 0 ? 'pg-bad' : '') + '">' + (pr.slackMin < 0 ? '⚠ ' + dur(-pr.slackMin) + ' för kort vila' : 'vila ' + dur(pr.idleMin) + (pr.slackMin > 0 ? ' (' + dur(pr.slackMin) + ' över minsta)' : ' (minsta)')) + '</span>').join(', ') : '';
          return '<li class="py-2" style="border-top:1px solid var(--bs-border-color)"><div><button type="button" class="btn btn-link p-0 align-baseline fw-semibold" data-hi="' + m.memberId + '" aria-pressed="' + (hi === m.memberId) + '" title="Markera i bilden">' + esc(m.name) + '</button> <span class="pg-hint">' + esc(m.shootingClass) + (m.club ? ' · ' + esc(m.club) : '') + '</span></div>' +
            (per ? strip + '<div class="small">' + starts + '</div><div class="small">' + rest + '</div>' : '<div class="pg-hint small">Bara den här starten.</div>') + '</li>'; }).join('');
        h = '<div class="pg-panel"><div class="d-flex align-items-center gap-2"><b>Patrull ' + p.number + '</b> · ' + p.start + '–' + p.end + ' <button type="button" class="btn btn-link p-0 ms-auto" data-clearsel="1">Stäng</button></div><ul class="list-unstyled mb-0 mt-1">' + rows + '</ul></div>';
      }
    }
    $('pgSelDetail').innerHTML = h;
  }

  // ---------- det som skaver (skyttar med flera starter)
  function renderScrape() {
    const t = T(R); if (!t) return;
    const q = $('pgPFind').value.trim().toLowerCase(), by = $('pgPSort').value, showAll = $('pgPList').dataset.all === '1' || !!q;
    const scrape = t.persons.filter(z => z.minSlackMin < 0 || z.waitMin > 60);
    let ps = (showAll ? t.persons : scrape).filter(z => !q || z.name.toLowerCase().includes(q));
    ps = ps.slice().sort((a, b) => by === 'wait' ? b.waitMin - a.waitMin : by === 'name' ? a.name.localeCompare(b.name, 'sv') : a.minSlackMin - b.minSlackMin);
    $('pgScrapeSum').textContent = scrape.length ? 'Skyttar som skaver (' + scrape.length + ' av ' + t.persons.length + ' med flera starter)' : 'Skyttar med flera starter (' + t.persons.length + '), ingen skaver';
    $('pgPList').innerHTML = ps.slice(0, 80).map(z => '<li><button type="button" data-person="' + z.memberId + '" class="' + (sel && sel.k === 'person' && sel.id === z.memberId ? 'sel' : '') + '"><span><b>' + esc(z.name) + '</b></span><span class="' + (z.minSlackMin < 0 ? 'pg-bad' : '') + '">' + (z.minSlackMin < 0 ? '⚠ ' + dur(-z.minSlackMin) + ' för kort' : z.waitMin > 60 ? 'väntar ' + dur(z.waitMin) : 'ok') + '</span><span class="sub">' + esc(z.classes) + ' · ' + z.starts.map(s => s.start).join(' → ') + '</span></button></li>').join('') +
      (!showAll && t.persons.length > scrape.length ? '<li><button type="button" id="pgPAll">Visa alla ' + t.persons.length + ' med flera starter</button></li>' : '') + (ps.length === 0 && showAll ? '<li class="pg-hint p-2">Ingen träff.</li>' : '');
    const d = sel && sel.k === 'person' ? t.persons.find(z => z.memberId === sel.id) : null;
    $('pgPDetail').innerHTML = d ? '<div class="pg-panel"><b>' + esc(d.name) + '</b> <span class="pg-hint">' + esc(d.club) + '</span><ul class="mb-1 mt-1" style="padding-left:1.1rem">' + d.starts.map(s => '<li>' + esc(s.group) + ' kl ' + s.start + '–' + s.end + ' (patrull ' + s.patrol + ')</li>').join('') + '</ul>' +
      d.pairs.map(pr => '<div class="' + (pr.slackMin < 0 ? 'pg-bad' : '') + '">' + (pr.slackMin < 0 ? 'Startar ' + dur(-pr.slackMin) + ' för tidigt (under minsta vila).' : pr.slackMin === 0 ? 'Precis minsta vila.' : 'Väntar ' + dur(pr.slackMin) + ' över minsta vila' + (pr.cross ? ' (över en lång paus)' : '') + '.') + '</div>').join('') + '</div>' : '';
  }
  function renderMoves() {
    const r = R, moves = r.moves || [], nStarts = r.patrols.reduce((n, p) => n + p.members.length, 0), c = r.change;
    $('pgMovesSum').textContent = r.existing.patrols === 0 ? 'Ny startlista (' + nStarts + ' starter)' : 'Vem flyttas (' + c.moved + ' flyttas, ' + c.unchanged + ' ligger kvar, ' + c.added + ' nya)';
    const li = m => '<li><b>' + esc(m.name) + '</b> <span class="pg-hint">' + esc(m.shootingClass) + '</span><br>' + (m.kind === 'moved' ? 'patrull ' + m.fromPatrol + ' kl ca ' + esc(m.fromTime) + ' → <b>patrull ' + m.toPatrol + ' kl ca ' + esc(m.toTime) + '</b>' : m.kind === 'added' ? 'får <b>patrull ' + m.toPatrol + ' kl ca ' + esc(m.toTime) + '</b>' : 'tas bort (patrull ' + m.fromPatrol + ')') + '<br><span class="pg-hint">' + esc(m.why) + '</span></li>';
    let h = ''; if (r.existing.patrols === 0) h = '<p class="mb-0">Det finns ingen startlista än: alla ' + nStarts + ' starter får en plats.</p>';
    else if (!moves.length) h = '<p class="pg-ok mb-0"><b>Ingen behöver flyttas.</b></p>';
    else h = '<ul class="pg-moves">' + moves.slice(0, 15).map(li).join('') + '</ul>' + (moves.length > 15 ? '<details><summary style="min-height:44px">Visa de ' + (moves.length - 15) + ' övriga</summary><ul class="pg-moves">' + moves.slice(15, 300).map(li).join('') + '</ul></details>' : '');
    // De som får ny tid får inget mejl: arrangören ringer dem. Listan är namn, klass och ny tid, som text att klistra in eller skriva ut.
    const callList = moves.filter(m => m.kind !== 'removed');
    if (callList.length) h += '<p class="mt-2 mb-0"><button type="button" class="btn btn-outline-secondary btn-sm" id="pgCopyMoves" style="min-height:44px">Kopiera som lista att ringa efter (' + callList.length + ')</button> <span class="pg-hint" id="pgCopyMovesMsg" role="status"></span></p>';
    $('pgMovesPane').innerHTML = h;
    const cb = $('pgCopyMoves');
    if (cb) cb.addEventListener('click', async () => {
      const txt = callList.map(m => m.name + ' (' + m.shootingClass + '): patrull ' + m.toPatrol + ' kl ca ' + m.toTime).join('\n');
      try { await navigator.clipboard.writeText(txt); $('pgCopyMovesMsg').textContent = 'Listan över ' + callList.length + ' skyttar är kopierad.'; }
      catch (e) { $('pgCopyMovesMsg').textContent = 'Kunde inte kopiera. Markera och kopiera själv: ' + txt.replace(/\n/g, '; '); }
    });
  }
  function kpiRows(r) { const k = T(r) && T(r).kpi, m = r.metrics || {}; if (!k) return []; return [['Patruller', k.patrols], ['Starter', k.starts], ['Skyttar med flera starter', k.multi], ['Väntar mer än 1 timme', m.waitOverHour || 0], ['Längsta väntan över vilan', dur(k.maxWaitMin)], ['Under minsta vila', k.underMin], ['Under önskad vila', k.underWish], ['Patruller som kan behöva extra tid', k.needAdd], ['Krockar vid station', k.collisions], ['Sista start / klart ca', (k.lastStart || '–') + ' / ' + (k.lastEnd || '–')]]; }
  // Krav "gå i samma patrull som" (hårt) och önskemål om starttid (mjukt) ur anmälningarna: det som inte gick redovisas med namn, aldrig tyst.
  function renderNotes() {
    const box = $('pgNotes'), l = R.links || { total: 0, met: 0, issues: [] }, w = R.wishes || { total: 0, met: 0, missed: [] };
    let h = '';
    if (l.total > 0 || (l.issues || []).length) {
      const bad = (l.issues || []).length > 0;
      h += '<div class="alert ' + (bad ? 'alert-warning' : 'alert-success') + ' py-2 mb-2" id="pgLinks"><b>Gå i samma patrull som:</b> ' + l.met + ' av ' + l.total + ' krav håller.' +
        (bad ? '<ul class="mb-0 mt-1">' + l.issues.map(x => '<li>' + esc(x) + '</li>').join('') + '</ul>' : '') + '</div>';
    }
    if (w.total > 0) {
      h += '<div class="alert alert-light border py-2 mb-2" id="pgWishes"><b>Önskemål om starttid:</b> ' + w.met + ' av ' + w.total + ' uppfyllda. Det är bara önskemål: vilokraven och skyttar med flera starter går före.' +
        ((w.missed || []).length ? '<details class="mt-1"><summary>Vilka som inte fick sitt önskemål (' + w.missed.length + ')</summary><ul class="mb-0">' + w.missed.map(x => '<li>' + esc(x) + '</li>').join('') + '</ul></details>' : '') + '</div>';
    }
    box.innerHTML = h; box.hidden = !h;
  }
  function renderMore() {
    const r = R, t = T(r), tips = [], add = (c, w, text) => tips.push('<li class="' + c + '"><b>' + w + '</b> ' + text + '</li>');
    if (t) { const need = t.patrols.filter(p => p.addMin > 0);
      if (need.length) add('', 'Tips:', need.length + ' patruller blandar skjuttider och behöver lite extra tid före nästa (t.ex. patrull ' + need[0].number + ': ' + String(need[0].addMin).replace('.', ',') + ' min).');
      t.pauses.filter(z => z.needMin > z.setMin && z.beforePatrol).slice(0, 3).forEach(z => add('', 'Tips:', 'Förläng pausen före patrull ' + z.beforePatrol + ' från ' + dur(z.setMin) + ' till ' + dur(z.needMin) + ' så att skyttarna hinner vila.')); }
    if ((r.unavoidable || []).length) add('pg-bad', 'Fel:', r.unavoidable.length + ' skyttar får ett uppdrag som krockar med starten: ' + r.unavoidable.slice(0, 5).map(u => esc(u.name) + ' kl ' + esc(u.time)).join('; '));
    if ((r.ungrouped || []).length) add('pg-warn', 'Varning:', 'Skjuttid saknas för ' + r.ungrouped.map(esc).join(', ') + ' (tidtas med 20 s).');
    if ((r.lockedOverflow || []).length) add('pg-warn', 'Varning:', 'Fick ingen plats i sin patrull: ' + r.lockedOverflow.map(esc).join(', ') + '.');
    if ((r.reservesLeftOut || []).length) add('', 'Info:', 'Utan start (hoppar in vid lucka): ' + r.reservesLeftOut.map(esc).join(', ') + '.');
    $('pgMetricsMore').innerHTML = '<table><tbody>' + kpiRows(r).map(x => '<tr><td>' + esc(x[0]) + '</td><td><b>' + esc(x[1]) + '</b></td></tr>').join('') + '</tbody></table>' + (tips.length ? '<ul class="list-unstyled mt-2 mb-0" style="font-size:.9rem">' + tips.join('') + '</ul>' : '');
  }
  function selectPatrol(n) { sel = sel && sel.k === 'patrol' && sel.id === n ? null : { k: 'patrol', id: n }; render(); }
  function selectPerson(id) { sel = sel && sel.k === 'person' && sel.id === id ? null : { k: 'person', id }; if (view === 'list' && !phone()) view = 'sta'; render(); }

  // ---------- tillämpa / ångra (bekräftelser i en dialog, inga kryssrutor)
  function reasons() {
    const r = []; if (!R) return r;
    if (R.blocked) r.push(R.blocked + ' Skyttarna kan ha planerat efter tiderna. Patrullnumren behålls och ingen skytt som har resultat flyttas.');
    if (needForceMsg) r.push(needForceMsg + ' Befintligt resultat påverkas inte.');
    if (noFit()) r.push('Förslaget följer inte reglerna: ' + problems().join(', ') + '.');
    return r;
  }
  // Knappen får tre punkter när en bekräftelse kommer först (publicerad lista, resultat eller regelbrott). Ingen förklarande text: dialogen säger varför.
  function updateApply() { if (practice) { $('pgApply').disabled = true; return; } $('pgApply').disabled = busy || !R; $('pgApply').textContent = R && reasons().length ? 'Använd förslaget…' : 'Använd förslaget'; if (busy) $('pgApplyHint').textContent = 'Räknar om…'; else if (/^(Räknar|Sparar)/.test($('pgApplyHint').textContent)) $('pgApplyHint').textContent = ''; }
  function confirmDialog(list) {
    return new Promise(res => { const d = $('pgConfirm'); $('pgConfirmBody').innerHTML = list.map(x => '<p>' + esc(x) + '</p>').join('');
      const done = v => { d.close(); $('pgConfirmYes').onclick = $('pgConfirmNo').onclick = null; res(v); }; $('pgConfirmYes').onclick = () => done(true); $('pgConfirmNo').onclick = () => done(false); d.showModal(); });
  }
  async function apply() {
    if (practice) return;   // övning: det finns inget att tillämpa på
    const rs = reasons(); let force = false;
    if (rs.length) { if (!(await confirmDialog(rs))) return; force = !!(R.blocked || needForceMsg); }
    $('pgApply').disabled = true; $('pgApplyHint').textContent = 'Sparar…';
    try {
      const d = await getJson('/umbraco/surface/Staffing/ApplyPatrolPlan', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token(), Accept: 'application/json' }, body: JSON.stringify(body(P, { fingerprint: R.fingerprint, force })) });
      if (d.success) { done(d); return; }
      if (/resultat|publicerad|gått ut/i.test(d.message || '') && !force) { needForceMsg = d.message; $('pgApply').disabled = false; apply(); return; }
      $('pgApplyHint').textContent = d.message || 'Det gick inte att spara. Inget ändrades.'; $('pgApply').disabled = false;
    } catch (e) { $('pgApplyHint').textContent = 'Det gick inte att spara. Inget ändrades.'; $('pgApply').disabled = false; }
  }
  function done(d) {
    $('pgDone').innerHTML = '<div class="alert alert-success mt-3" role="status" tabindex="-1" id="pgDoneBox"><b>Klart.</b> ' + d.patrols + ' patruller med ' + d.shooters + ' starter sparades. <div class="d-flex gap-2 flex-wrap mt-2"><button type="button" class="btn btn-outline-secondary" id="pgUndo" data-backup="' + d.backupId + '">Ångra</button><a class="btn btn-primary" href="/competitionmanagement?competitionId=' + comp + '">Gå till startlistan</a></div></div>';
    $('pgApplyHint').textContent = ''; $('pgDoneBox').focus(); $('pgDoneBox').scrollIntoView({ block: 'center' });
  }
  async function undo(id) {
    const d = await getJson('/umbraco/surface/Staffing/UndoPatrolPlan', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token(), Accept: 'application/json' }, body: JSON.stringify({ competitionId: comp, backupId: id, force: false }) }).catch(() => null);
    if (d && d.success) { $('pgDone').innerHTML = '<div class="alert alert-info mt-3" role="status">Startlistan är återställd som den var före ombyggnaden.</div>'; schedule(0); return; }
    $('pgDone').insertAdjacentHTML('beforeend', '<div class="alert alert-warning mt-2" role="alert">' + esc((d && d.message) || 'Det gick inte att ångra.') + '</div>');
  }

  // ---------- händelser
  function changed() { fromFields(); toHash(); lastChange = null; schedule(); }
  FIELDS.forEach(([id]) => { const el = $(id); if (!el) return; el.addEventListener('input', changed); el.addEventListener('change', changed); });
  $('pgKeep').addEventListener('change', changed); $('pgDay2').addEventListener('change', changed);
  $('pgFlow').addEventListener('change', () => { flow = $('pgFlow').checked; try { localStorage.setItem('pgFlow', flow ? '1' : '0'); } catch (e) { /* valet gäller bara den här sidvisningen */ } render(); });
  $('pgReset').addEventListener('click', () => { const sc = P.sc; P = Object.assign({}, DEFAULTS); P.sc = sc; lastChange = null; toFields(); toHash(); start(); });
  $('pgCopy').addEventListener('click', async () => { try { await navigator.clipboard.writeText(location.href); $('pgCopy').textContent = 'Länken är kopierad'; setTimeout(() => $('pgCopy').textContent = 'Kopiera länk', 2500); } catch (e) { $('pgCopy').textContent = 'Kopiera adressen i fältet'; } });
  $('pgApply').addEventListener('click', () => apply());
  $('pgPSort').addEventListener('change', () => { if (R) renderScrape(); });
  $('pgPFind').addEventListener('input', () => { if (R) renderScrape(); });
  [['vSta', 'sta'], ['vTl', 'tl'], ['vList', 'list']].forEach(([id, k]) => $(id).addEventListener('click', () => { view = k; if (R) render(); }));
  page.addEventListener('click', e => {
    const t = e.target, f = t.closest && t.closest('[data-fix]');
    if (f) { const before = Object.assign({}, P);
      if (f.dataset.fix === 'rest') { P.rest = f.dataset.v; lastChange = { text: 'Minsta vila ändrades från ' + before.rest + ' till ' + P.rest + ' min.', undo: { rest: before.rest } }; }
      else if (f.dataset.fix === 'order') { P.order = 'auto'; lastChange = { text: 'Uppdelningen ändrades till automatiskt val.', undo: { order: before.order } }; }
      else if (f.dataset.fix === 'close') { P.close = f.dataset.v; lastChange = { text: 'Dagens slut ändrades till ' + P.close + '.', undo: { close: before.close } }; }
      else if (f.dataset.fix === 'int') { P.int = f.dataset.v; lastChange = { text: 'Tiden mellan patruller ändrades till ' + P.int + ' min.', undo: { int: before.int } }; }
      toFields(); toHash(); schedule(0); return; }
    if (t.closest && t.closest('[data-undofix]') && lastChange) { Object.assign(P, lastChange.undo); lastChange = null; toFields(); toHash(); schedule(0); return; }
    if (t.closest && t.closest('[data-clearsel]')) { sel = null; hi = null; render(); return; }
    const hb = t.closest && t.closest('[data-hi]'); if (hb) { const id = +hb.dataset.hi; hi = hi === id ? null : id; render(); return; }
    const row = t.closest && t.closest('[data-patrol]'); if (row) { selectPatrol(+row.dataset.patrol); return; }
    const pb = t.closest && t.closest('[data-person]'); if (pb) { selectPerson(+pb.dataset.person); return; }
    if (t.id === 'pgPAll') { $('pgPList').dataset.all = '1'; renderScrape(); return; }
    if (t.id === 'pgUndo') undo(+t.dataset.backup);
  });
  window.addEventListener('hashchange', () => { fromHash(); toFields(); start(); });
  window.matchMedia('(max-width: 760px)').addEventListener('change', () => { if (phone() && view !== 'list') view = 'list'; if (R) render(); });
  if (phone()) { $('pgParamsBox').open = false; }
  // Ett förslag visas aldrig på en halv konfiguration: saknas de centrala värdena i adressen räknas de fram en gång (utan att visas), fylls i fälten och adressen, och först då räknas förslaget.
  // ---------- övning: scenariot (påhittade skyttar). Allt här är siffror; namnen skapas på servern.
  // Dubbelstarter anges som OORDNADE par (C+B, C+A, ...) i fast ordning; riktningen betyder bara något internt i byggaren.
  const GR = ['C', 'B', 'A', 'R'];
  const PAIRS = [['C', 'B'], ['C', 'A'], ['C', 'R'], ['B', 'A'], ['B', 'R'], ['A', 'R']];
  const A2 = (f, t, n) => ({ From: f, To: t, N: n });
  const PRESETS = [
    // Förvalen bygger på verkliga startlistor (bara antal och inställningar, inga namn): personer per grupp, hur många som skjuter två, tre eller fyra grupper, patrullstorlek, tid mellan patruller, första start och rundtid.
    // I verkligheten skjuter ungefär hälften flera grupper (mest A och C); tre grupper är 9 % (Trollenäs 2026) till 28 % (Mårtenträffen); fyra grupper förekommer inte.
    // Mästerskap pågår flera dagar och planeras en dag i taget: SSM har C och B den ena dagen och A och R den andra, SM 2024 hade en grupp (och några dubbla) per dag.
    { id: 'trol26', name: 'Trollenäs 2026', sc: { C: 206, B: 4, A: 129, R: 41, Also: [A2('C', 'A', 71), A2('C', 'R', 14), A2('C', 'B', 2), A2('A', 'R', 3)], Multi: [{ Groups: 'CAR', N: 21 }, { Groups: 'CBA', N: 1 }, { Groups: 'CBR', N: 1 }] }, size: '7', int: '8', first: '07:45', rm: '120' },
    { id: 'trol25', name: 'Trollenäs 2025', sc: { C: 189, B: 27, A: 94, R: 34, Also: [A2('C', 'A', 56), A2('C', 'R', 17), A2('C', 'B', 11), A2('B', 'A', 2), A2('A', 'R', 1)], Multi: [{ Groups: 'CBA', N: 7 }, { Groups: 'CAR', N: 10 }, { Groups: 'CBR', N: 2 }] }, size: '7', int: '8', first: '08:00', rm: '90' },
    { id: 'skane', name: 'Skånemästerskap 2025', sc: { C: 168, B: 11, A: 83, R: 17, Also: [A2('C', 'A', 79), A2('C', 'R', 17), A2('C', 'B', 11)] }, size: '8', int: '10', first: '08:00', rm: '105' },
    { id: 'martens', name: 'Mårtenträffen (många skjuter tre klasser)', sc: { C: 78, B: 21, A: 37, R: 27, Also: [A2('C', 'R', 6), A2('C', 'A', 19), A2('C', 'B', 10)], Multi: [{ Groups: 'CBR', N: 7 }, { Groups: 'CBA', N: 4 }, { Groups: 'CAR', N: 12 }] }, size: '8', int: '8', first: '08:00', rm: '75' },
    { id: 'newbies', name: 'Nybörjare med kamrat och önskemål (Mårtenträffen + 10 nybörjare, 12 önskemål)', sc: { C: 78, B: 21, A: 37, R: 27, Also: [A2('C', 'R', 6), A2('C', 'A', 19), A2('C', 'B', 10)], Multi: [{ Groups: 'CBR', N: 7 }, { Groups: 'CBA', N: 4 }, { Groups: 'CAR', N: 12 }], Followers: 10, WishEarly: 8, WishLate: 4 }, size: '8', int: '8', first: '08:00', rm: '75' },
    { id: 'staff', name: 'Svårt, med funktionärer (Mårtenträffen + uppdrag)', sc: { C: 78, B: 21, A: 37, R: 27, Also: [A2('C', 'R', 6), A2('C', 'A', 19), A2('C', 'B', 10)], Multi: [{ Groups: 'CBR', N: 7 }, { Groups: 'CBA', N: 4 }, { Groups: 'CAR', N: 12 }], Jobs: [{ N: 12, From: '09:00', To: '12:00' }], Reserves: 3, Early: 4 }, size: '8', int: '8', first: '08:00', rm: '75' },
    { id: 'four', name: 'Stresstest: många skjuter alla fyra klasserna', sc: { C: 24, B: 16, A: 16, R: 8, Multi: [{ Groups: 'CBAR', N: 8 }, { Groups: 'CBA', N: 8 }], Stations: 6 } },
    { id: 'ssm26a', big: true, name: 'SSM 2026 dag 1 (C och B)', sc: { C: 153, B: 69, Also: [A2('C', 'B', 68)] }, size: '8', int: '8', first: '09:00', rm: '100' },
    { id: 'ssm26b', big: true, name: 'SSM 2026 dag 2 (A och R)', sc: { A: 87, R: 74, Also: [A2('A', 'R', 57)] }, size: '8', int: '8', first: '09:00', rm: '100' },
    { id: 'ssm25a', big: true, name: 'SSM 2025 dag 1 (C och B)', sc: { C: 104, B: 56, Also: [A2('C', 'B', 52)] }, size: '5', int: '8', first: '08:00', rm: '200' },
    { id: 'ssm25b', big: true, name: 'SSM 2025 dag 2 (A och R)', sc: { A: 38, R: 48, Also: [A2('A', 'R', 28)] }, size: '5', int: '10', first: '09:00', rm: '180' },
    { id: 'smthu', big: true, name: 'SM 2024 torsdag (A, 36 skjuter också C)', sc: { A: 266, C: 99, Also: [A2('C', 'A', 36)], Stations: 10 }, rest: '45', wish: '90', champ: 'A', size: '10', int: '11', first: '07:55', rm: '110' },
    { id: 'smfri', big: true, name: 'SM 2024 fredag (R, 14 skjuter också C)', sc: { R: 237, C: 66, Also: [A2('C', 'R', 14)], Stations: 10 }, rest: '45', wish: '90', champ: 'R', size: '10', int: '11', first: '07:55', rm: '110' },
    { id: 'smsat', big: true, name: 'SM 2024 lördag (C)', sc: { C: 333, Stations: 10 }, rest: '45', wish: '90', size: '10', int: '11', first: '07:55', rm: '110' },
    { id: 'smsun', big: true, name: 'SM 2024 söndag (B)', sc: { B: 199, Stations: 10 }, rest: '45', wish: '90', size: '10', int: '11', first: '07:55', rm: '110' },
  ];
  const fullSc = sc => Object.assign({ V: 1, Seed: 1, C: 0, B: 0, A: 0, R: 0, Also: [], Multi: [], Jobs: [], Reserves: 0, Early: 0, Stations: 8 }, sc);
  function currentSc() { try { return fullSc(JSON.parse(P.sc)); } catch (e) { return fullSc(PRESETS[0].sc); } }
  let lastExtra = null;   // förvalets egna inställningar (rundtid, vila, mästerskap) följer med så länge man bara justerar antalen
  function setScenario(sc, extra) {
    lastExtra = extra || null;
    P.sc = JSON.stringify(fullSc(sc)); P.close = ''; P.pb = '';   // det som hänger på skyttarna (dagens slut, paus) räknas fram igen
    P.rm = (extra && extra.rm) || ''; P.first = (extra && extra.first) || '';   // verkliga förval har sin egen rundtid och första start; annars föreslår servern
    P.rest = (extra && extra.rest) || DEFAULTS.rest; P.wish = (extra && extra.wish) || ''; P.champ = (extra && extra.champ) || '';
    P.size = (extra && extra.size) || ''; P.int = (extra && extra.int) || '';   // tomt = servern föreslår (6 per patrull, 15 minuter)
    R = null; toFields(); toHash();
  }
  // Modalen anger PERSONER PER KOMBINATION: varje person står på exakt en rad ("bara C", "C och A", "C, A och R"). Antalet starter per grupp räknas fram.
  // Det kan inte bli motsägelsefullt: inga tak som hänger på andra fält. Servern får Also = rader med två grupper, Multi = rader med tre eller fyra, och starterna per grupp som summa.
  const COMBOS = [].concat(GR, PAIRS.map(p => p.join('')), ['CBA', 'CBR', 'CAR', 'BAR', 'CBAR']);
  const keyOf = gs => GR.filter(g => gs.includes(g)).join('');
  const comboLabel = k => k.length === 1 ? 'Bara ' + k : k.split('').join(', ').replace(/, (\w)$/, ' och $1');
  /** Starter, personer som skjuter flera grupper och personer för ett scenario (samma räkning som servern: en person med flera grupper är flera starter men en person). */
  function sumOf(sc) {
    const pv = {}; (sc.Also || []).forEach(x => { const k = keyOf([x.From, x.To]); pv[k] = (pv[k] || 0) + (x.N || 0); });
    (sc.Multi || []).forEach(x => { const k = keyOf(x.Groups.split('')); pv[k] = (pv[k] || 0) + (x.N || 0); });
    const dub = Object.values(pv).reduce((t, v) => t + v, 0), extra = Object.entries(pv).reduce((t, [k, v]) => t + v * (k.length - 1), 0), total = sc.C + sc.B + sc.A + sc.R;
    return { total, dub, persons: total - extra, groups: GR.filter(g => sc[g] > 0).length };
  }
  const gv = id => Math.max(0, parseInt($(id).value, 10) || 0);
  let dirty = false, chosen = null;
  /** Ett scenario (antal per grupp + exakta kombinationer) som rader: kombination -> antal personer. De som bara skjuter en grupp är gruppens starter minus de som skjuter fler. */
  function rowsOf(sc) {
    const ex = {}; (sc.Also || []).forEach(x => { const k = keyOf([x.From, x.To]); ex[k] = (ex[k] || 0) + (x.N || 0); });
    (sc.Multi || []).forEach(x => { const k = keyOf(x.Groups.split('')); ex[k] = (ex[k] || 0) + (x.N || 0); });
    GR.forEach(g => { ex[g] = Math.max(0, (sc[g] || 0) - Object.entries(ex).reduce((t, [k, v]) => t + (k.length > 1 && k.includes(g) ? v : 0), 0)); });
    return COMBOS.filter(k => ex[k] > 0).map(k => ({ k, n: ex[k] }));
  }
  /** Raderna som scenario (det servern får). */
  function scOfRows(rows) {
    const s = { C: 0, B: 0, A: 0, R: 0, Also: [], Multi: [] };
    rows.forEach(r => { r.k.split('').forEach(g => { s[g] += r.n; }); if (r.k.length === 2) s.Also.push(A2(r.k[0], r.k[1], r.n)); else if (r.k.length > 2) s.Multi.push({ Groups: r.k, N: r.n }); });
    return s;
  }
  const rowsNow = () => { const m = {}; document.querySelectorAll('#scRows [data-row]').forEach(r => { const n = Math.max(0, parseInt(r.querySelector('input').value, 10) || 0); if (n > 0) m[r.querySelector('select').value] = (m[r.querySelector('select').value] || 0) + n; }); return COMBOS.filter(k => m[k]).map(k => ({ k, n: m[k] })); };
  function rowHtml(k, n) {
    return '<div class="sc-crow" data-row><select class="form-select" aria-label="Vilka klasser">' + COMBOS.map(c => '<option value="' + c + '"' + (c === k ? ' selected' : '') + '>' + comboLabel(c) + '</option>').join('') + '</select><input class="form-control" type="number" min="0" max="6000" inputmode="numeric" aria-label="Antal personer" value="' + (n || '') + '" placeholder="antal"><button type="button" class="btn btn-outline-secondary" data-delrow aria-label="Ta bort raden">×</button></div>';
  }
  const renderRows = rows => { $('scRows').innerHTML = rows.map(r => rowHtml(r.k, r.n)).join(''); };
  function setFields(sc) {
    const set = (id, v) => { $(id).value = v == null ? '' : v; };
    renderRows(rowsOf(sc));
    set('scSeed', sc.Seed); set('scSt', sc.Stations); set('scRes', sc.Reserves || ''); set('scEarly', sc.Early || '');
    const j = (sc.Jobs || [])[0]; set('scJobN', j ? j.N : ''); $('scJobMode').value = j && j.From === '12:00' ? 'pm' : 'am';   // en funktionär som är upptagen hela dagen hinner inte skjuta, så det finns bara förmiddag eller eftermiddag
    $('scStaffOn').checked = !!j || sc.Reserves > 0 || sc.Early > 0;
    set('scFollowers', sc.Followers || ''); set('scWishEarly', sc.WishEarly || ''); set('scWishLate', sc.WishLate || '');
    $('scWishOn').checked = sc.Followers > 0 || sc.WishEarly > 0 || sc.WishLate > 0;
  }
  function fieldsToSc() {
    const n = id => gv(id), m = $('scJobMode').value, base = scOfRows(rowsNow());
    const staff = $('scStaffOn').checked, jn = staff ? n('scJobN') : 0, wish = $('scWishOn').checked;
    base.Followers = wish ? n('scFollowers') : 0; base.WishEarly = wish ? n('scWishEarly') : 0; base.WishLate = wish ? n('scWishLate') : 0;
    return fullSc(Object.assign(base, {
      Seed: Math.max(1, n('scSeed')), Reserves: staff ? n('scRes') : 0, Early: staff ? n('scEarly') : 0, Stations: n('scSt') || 8,
      Jobs: jn > 0 ? [{ N: jn, From: m === 'am' ? '09:00' : '12:00', To: m === 'am' ? '12:00' : '15:00' }] : [],
    }));
  }
  function errAt(id, html) { const e = $(id + 'Err'); if (!e) return; setHtml(e, html || ''); e.hidden = !html; e.className = 'sc-err'; $(id).setAttribute('aria-invalid', html ? 'true' : 'false'); }
  // Knapparna i felraderna byggs bara om när texten ändras: byggs de om mellan musen ner och upp (när fältet tappar fokus) går klicket förlorat.
  const setHtml = (e, html) => { if (e._h !== html) { e.innerHTML = html; e._h = html; } };
  /** Räknar om det som visas: starter per grupp, summering, vilka kombinationer som redan är valda, funktionärsgränsen. */
  function refreshDialog() {
    const rows = rowsNow(), sc = scOfRows(rows), total = sc.C + sc.B + sc.A + sc.R, m = sumOf(fullSc(sc));
    // en kombination kan bara stå på en rad: de som redan används är avstängda i de andra raderna
    const used = new Set([...document.querySelectorAll('#scRows [data-row] select')].map(s => s.value));
    document.querySelectorAll('#scRows [data-row] select').forEach(s => [...s.options].forEach(o => { o.disabled = o.value !== s.value && used.has(o.value); }));
    $('scAddRow').disabled = used.size >= COMBOS.length;
    let errors = 0;
    const wishOn = $('scWishOn').checked; $('scWishBox').hidden = !wishOn;
    const wishNeed = wishOn ? gv('scFollowers') + gv('scWishEarly') + gv('scWishLate') : 0, wishBad = wishOn && wishNeed > m.persons;
    setHtml($('scWishErr'), wishBad ? 'Nybörjare och önskemål är ' + wishNeed + ' men det finns bara ' + m.persons + ' personer.' : ''); $('scWishErr').hidden = !wishBad; if (wishBad) errors++;
    const staffOn = $('scStaffOn').checked; $('scStaffBox').hidden = !staffOn;
    const jn = gv('scJobN'), rs = gv('scRes'), er = gv('scEarly'), need = jn + rs + er;
    if (staffOn && need > m.persons) { errors++; errAt('scStaff', 'Uppdrag, reserver och tidiga är ' + need + ' men det finns bara ' + m.persons + ' personer. <button type="button" class="btn btn-sm btn-outline-primary" data-staffmax="1">Sänk till ' + m.persons + '</button>'); } else errAt('scStaff', '');
    let tot = '';
    if (total < 1) tot = 'Ange hur många som skjuter.'; else if (total > 1500) tot = 'Högst 1 500 starter.';
    if (tot) errors++;
    $('scStarts').textContent = GR.filter(g => sc[g] > 0).map(g => g + ' ' + sc[g]).join(' · ') + (total ? ' starter' : '');
    $('scSum').textContent = tot ? tot : total + ' starter · ' + m.persons + ' personer · ' + m.dub + ' dubbla';
    $('scSum').classList.toggle('text-danger', !!tot);
    $('scHelp').textContent = '';
    $('scGo').disabled = errors > 0;
    document.querySelectorAll('#scCards [data-preset], #scCardsBig [data-preset]').forEach(b => b.setAttribute('aria-pressed', String(!dirty && chosen && b.dataset.preset === chosen.id)));
    $('scCustomCard').setAttribute('aria-pressed', String(dirty || !chosen));
  }
  function cardHtml(p) {
    const m = sumOf(fullSc(p.sc));
    return '<button type="button" class="sc-card" data-preset="' + p.id + '" aria-pressed="false"><strong>' + esc(p.name) + '</strong><span>' + m.total + ' starter' + (m.groups === 1 ? ', en klass' : '') + (m.dub ? ', ' + m.dub + ' dubbla' : '') + '</span></button>';
  }
  function initScenarioDialog() {
    const d = $('pgScenario'); if (!d) return;
    $('scCards').innerHTML = PRESETS.filter(p => !p.big).map(cardHtml).join('') + '<button type="button" class="sc-card" id="scCustomCard" aria-pressed="false"><strong>Egna värden</strong><span>ange antalen själv</span></button>';
    $('scCardsBig').innerHTML = PRESETS.filter(p => p.big).map(cardHtml).join('');
    $('pgScenarioBtn').addEventListener('click', () => {
      const cur = currentSc(), key = JSON.stringify(cur);
      chosen = PRESETS.find(p => JSON.stringify(fullSc(p.sc)) === key) || null; dirty = false;
      setFields(cur); if (!chosen) chosen = null;
      $('scCustom').open = !chosen; $('scBig').open = !!(chosen && chosen.big);
      refreshDialog(); d.showModal();
    });
    d.addEventListener('click', e => {
      const t = e.target, el = t.closest ? t.closest('button') : null; if (!el) return;
      if (el.dataset.preset) { chosen = PRESETS.find(p => p.id === el.dataset.preset); dirty = false; setFields(fullSc(chosen.sc)); refreshDialog(); return; }
      if (el.id === 'scCustomCard') { $('scCustom').open = true; dirty = true; refreshDialog(); const f = document.querySelector('#scRows input') || $('scAddRow'); f.focus(); return; }
      if (el.id === 'scAddRow') {
        const used = new Set([...document.querySelectorAll('#scRows [data-row] select')].map(s => s.value)), k = COMBOS.find(c => !used.has(c));
        if (k) { $('scRows').insertAdjacentHTML('beforeend', rowHtml(k, '')); dirty = true; refreshDialog(); $('scRows').lastElementChild.querySelector('input').focus(); }
        return;
      }
      if (el.dataset.delrow !== undefined) { el.closest('[data-row]').remove(); dirty = true; refreshDialog(); return; }
      if (el.dataset.staffmax) {
        const persons = sumOf(fieldsToSc()).persons; let over = gv('scJobN') + gv('scRes') + gv('scEarly') - persons;
        for (const id of ['scEarly', 'scRes', 'scJobN']) { const cut = Math.min(over, gv(id)); if (cut > 0) { $(id).value = gv(id) - cut || ''; over -= cut; } }
        dirty = true; refreshDialog(); return;
      }
      if (el.id === 'scNewSeed') { $('scSeed').value = Math.floor(Math.random() * 9000) + 1; dirty = true; refreshDialog(); }
    });
    d.addEventListener('input', e => { if (e.target.matches('input, select')) { dirty = true; refreshDialog(); } });
    $('scCancel').addEventListener('click', () => d.close());
    $('scGo').addEventListener('click', () => {
      if ($('scGo').disabled) return;
      const sc = !dirty && chosen ? fullSc(chosen.sc) : !dirty ? currentSc() : fieldsToSc();
      d.close();
      if (!dirty && JSON.stringify(sc) === JSON.stringify(currentSc())) return;   // oförändrat: behåll adressen och det som räknats fram
      setScenario(sc, chosen || lastExtra); start();
    });
  }

  const ESSENTIAL = ['size', 'int', 'first', 'close', 'wish', 'rm', 'pb'];
  async function start() {
    if (practice && !P.sc) { const p = PRESETS[0]; setScenario(p.sc, p); }
    if (!ESSENTIAL.some(k => !P[k])) { schedule(0); return; }
    busy = true; setBusy(true); $('pgStatus').className = 'pg-status wait'; $('pgStatus').innerHTML = '<div class="big">Fyller i inställningarna…</div>'; $('pgApply').disabled = true;
    const my = ++seq; if (ctl) ctl.abort(); ctl = new AbortController();
    try {
      const d = await getJson('/umbraco/surface/Staffing/GetPatrolPlan?' + new URLSearchParams(query(P)).toString(), null, ctl.signal);
      if (my !== seq) return;
      const s = d && d.settings, m = d && d.metrics, t = T(d);
      if (!d || !d.success || !s || !m || !t) { busy = false; setBusy(false); fail((d && d.message) || 'Kunde inte räkna ut en startlista.'); return; }
      if (!P.size) P.size = String(s.patrolSize);
      if (!P.int) P.int = String(s.intervalMinutes);
      if (!P.first && s.firstStart) P.first = s.firstStart.slice(11, 16);
      if (!P.close && m.lastEnd) { const c = roundUp(m.lastEnd); P.close = c <= (P.first || '00:00') ? '23:55' : c; }   // en dag som går förbi midnatt får sluttiden 23:55; statusen säger då hur mycket för lång den är
      if (!P.wish) P.wish = P.rest || '15';
      if (!P.rm && t.roundMin) P.rm = String(Math.round(t.roundMin));
      if (!P.pb) P.pb = P.int;
      toFields(); toHash(); schedule(0);
    } catch (e) { if (e.name === 'AbortError') return; if (my === seq) { busy = false; setBusy(false); fail('Kunde inte räkna ut en startlista just nu. Försök igen.'); } }
  }
  if (practice) { $('pgApply').hidden = true; initScenarioDialog(); }
  fromHash(); toFields(); start();
})();
