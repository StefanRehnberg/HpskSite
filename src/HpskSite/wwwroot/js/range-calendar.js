/*
 * Banans kalender — delen Kalender på banans sida, /skjutbana/{id}#vy=kalender (Views/RangePage.cshtml).
 * Den fristående sidan /skjutbana/kalender?r= leder hit sedan 2026-10-11.
 *
 * Sidans blå Åtgärder (range-page.js) anropar window.hpskRangeCalendar: prenumerera, lägga in en
 * bokning för andra. Kalendern läser in sig själv när delen visas första gången (start()).
 *
 * Läser RangeCalendarApi/GetCalendar för det synliga fönstret och ritar månadsrutnät eller lista
 * med kretskalenderns stil (.kc-*). EN färg per klubb, så att det syns vems tid det är; sorten
 * syns på ikonen. Klubbarnas fasta tider är streckade — de är en vana, inte en bokning.
 *
 * ⚠️ Inga användartexter i onclick: knappar bär id:n i data-attribut och läses av EN delegerad lyssnare.
 */
(function () {
    var app = document.getElementById('rcApp');
    if (!app) return;
    var rangeId = parseInt(app.getAttribute('data-range-id'), 10) || 0;

    var KINDS = {
        competition: { label: 'Tävling', icon: 'bi-trophy' },
        training: { label: 'Träning', icon: 'bi-bullseye' },
        event: { label: 'Händelse', icon: 'bi-calendar-event' },
        allocation: { label: 'Fast tid', icon: 'bi-clock' },
        external: { label: 'Bokning', icon: 'bi-person-badge' }
    };
    var PALETTE = ['#0d6efd', '#198754', '#d63384', '#fd7e14', '#6f42c1', '#20c997', '#dc3545', '#0aa2c0', '#8d6e63', '#b58900'];
    var GREY = '#6c757d';
    var MONTHS = ['januari', 'februari', 'mars', 'april', 'maj', 'juni', 'juli', 'augusti', 'september', 'oktober', 'november', 'december'];
    var DOW = ['Mån', 'Tis', 'Ons', 'Tor', 'Fre', 'Lör', 'Sön'];
    var MAX_CHIPS = 4;

    var esc = window.hpskEsc || function (s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[c];
        });
    };
    var pad = function (n) { return n < 10 ? '0' + n : '' + n; };
    var ymd = function (d) { return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); };
    var parse = function (s) { var p = s.split('-'); return new Date(+p[0], +p[1] - 1, +p[2]); };

    var state = {
        view: 'month',
        month: new Date(new Date().getFullYear(), new Date().getMonth(), 1),
        data: null,
        selected: null,
        colors: {}
    };
    var loadSeq = 0;

    function token() {
        // Banans sida har EN token, utanför kalenderns del.
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        return el ? el.value : '';
    }
    function post(url, body) {
        return fetch(url, {
            method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
            body: JSON.stringify(body)
        }).then(function (r) { return r.json(); });
    }
    function msg(html, kind) {
        document.getElementById('rcMsg').innerHTML = html ? '<div class="alert alert-' + (kind || 'warning') + ' py-2">' + html + '</div>' : '';
    }

    // ── Fönstret ──────────────────────────────────────────────────────────
    function windowFor() {
        if (state.view === 'list') {
            var t = new Date(); t.setHours(0, 0, 0, 0);
            var to = new Date(t); to.setMonth(to.getMonth() + 6);
            return { from: t, to: to };
        }
        var first = new Date(state.month);
        var start = new Date(first); start.setDate(1 - ((first.getDay() + 6) % 7));
        var last = new Date(first.getFullYear(), first.getMonth() + 1, 0);
        var end = new Date(last); end.setDate(last.getDate() + (6 - ((last.getDay() + 6) % 7)));
        return { from: start, to: end };
    }

    function load() {
        var w = windowFor();
        var seq = ++loadSeq;
        document.getElementById('rcMonthLabel').textContent = state.view === 'list'
            ? 'Kommande sex månader'
            : MONTHS[state.month.getMonth()].replace(/^./, function (c) { return c.toUpperCase(); }) + ' ' + state.month.getFullYear();
        fetch('/umbraco/surface/RangeCalendarApi/GetCalendar?rangeId=' + rangeId + '&from=' + ymd(w.from) + '&to=' + ymd(w.to), { credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (d) {
                if (seq !== loadSeq) return;
                if (!d.success) {
                    document.getElementById('rcBody').innerHTML = '<div class="alert alert-warning">' + esc(d.message || 'Kalendern kunde inte läsas.') + '</div>';
                    return;
                }
                state.data = d;
                head(d);
                render();
            });
    }

    function head(d) {
        document.querySelectorAll('[data-rc-manage]').forEach(function (el) { el.classList.toggle('d-none', !d.canManage); });
        document.getElementById('rcMineWrap').classList.toggle('d-none', !(d.myClubIds || []).length);

        var secWrap = document.getElementById('rcSectionWrap');
        var sec = document.getElementById('rcSection');
        if ((d.sections || []).length) {
            var keep = sec.value;
            sec.innerHTML = '<option value="">Alla banor/vallar</option>' + d.sections.map(function (s) {
                return '<option value="' + s.id + '">' + esc(s.label) + '</option>';
            }).join('');
            sec.value = keep && d.sections.some(function (s) { return String(s.id) === keep; }) ? keep : '';
            secWrap.classList.remove('d-none');
        } else secWrap.classList.add('d-none');
    }

    // ── Färger: en per klubb, stabil inom sidan ───────────────────────────
    function assignColors(items) {
        var ids = [];
        items.forEach(function (i) { if (i.clubId > 0 && ids.indexOf(i.clubId) < 0) ids.push(i.clubId); });
        ids.sort(function (a, b) { return a - b; });
        ids.forEach(function (id, n) { if (!state.colors[id]) state.colors[id] = PALETTE[Object.keys(state.colors).length % PALETTE.length]; });
    }
    function colorOf(i) { return i.clubId > 0 ? (state.colors[i.clubId] || GREY) : GREY; }

    function visible() {
        var d = state.data;
        if (!d) return [];
        var mine = document.getElementById('rcMine').checked;
        var sec = parseInt(document.getElementById('rcSection').value, 10) || 0;
        return (d.items || []).filter(function (i) {
            var k = document.getElementById('rcK_' + i.kind);
            if (k && !k.checked) return false;
            if (mine && (d.myClubIds || []).indexOf(i.clubId) < 0) return false;
            // En post för hela banan berör varje vall.
            if (sec && i.sectionId && i.sectionId !== sec) return false;
            return true;
        });
    }

    function timeText(i) {
        if (!i.start) return 'hela dagen';
        return i.start + (i.end ? '–' + i.end : '');
    }
    function chipText(i) {
        var k = KINDS[i.kind] || {};
        var who = i.ownerName ? i.ownerName : '';
        var what = i.kind === 'allocation' ? 'fast tid' : i.title;
        return (i.start ? i.start + ' ' : '') + who + (who && what ? ' · ' : '') + what;
    }
    function chip(i) {
        var k = KINDS[i.kind] || {};
        var cls = 'kc-chip rc-chip' + (i.kind === 'allocation' ? ' rc-alloc' : '') + (i.isCancelled ? ' kc-cancelled' : '');
        var title = (k.label || '') + ': ' + i.title + (i.ownerName ? ' (' + i.ownerName + ')' : '') + ', ' + timeText(i) +
            (i.sectionLabel ? ', ' + i.sectionLabel : '') + (i.isCancelled ? ' — inställd' : '');
        return '<span class="' + cls + '" style="--rc-c:' + colorOf(i) + '" title="' + esc(title) + '"><i class="bi ' + (k.icon || '') + '"></i>' + esc(chipText(i)) + '</span>';
    }

    function legend(items) {
        var seen = {}, html = '';
        items.forEach(function (i) {
            var key = i.clubId > 0 ? 'c' + i.clubId : (i.kind === 'external' ? 'x' : 'o' + i.ownerName);
            if (seen[key] || !i.ownerName) return;
            seen[key] = true;
            html += '<span><i class="rc-sw" style="--rc-c:' + colorOf(i) + '"></i>' + esc(i.ownerName) + '</span>';
        });
        document.getElementById('rcLegend').innerHTML = html;
    }

    function render() {
        var all = (state.data && state.data.items) || [];
        assignColors(all);
        var items = visible();
        legend(items);
        if (state.view === 'list') renderList(items); else renderMonth(items);
        renderDay();
    }

    function byDate(items) {
        var map = {};
        items.forEach(function (i) { (map[i.date] = map[i.date] || []).push(i); });
        return map;
    }

    function renderMonth(items) {
        var w = windowFor();
        var map = byDate(items);
        var today = ymd(new Date());
        var hol = {};
        if (window.hpskSwedishHolidays) {
            [w.from.getFullYear(), w.to.getFullYear()].forEach(function (y) {
                var h = window.hpskSwedishHolidays(y); for (var k in h) hol[k] = h[k];
            });
        }
        var html = '<div class="kc-grid">' + DOW.map(function (d) { return '<div class="kc-dow">' + d + '</div>'; }).join('');
        for (var d = new Date(w.from); d <= w.to; d.setDate(d.getDate() + 1)) {
            var key = ymd(d);
            var list = map[key] || [];
            var other = d.getMonth() !== state.month.getMonth();
            var cls = 'kc-day' + (other ? ' kc-other' : '') + (key === today ? ' kc-today' : '') + (hol[key] ? ' kc-holiday' : '') + (state.selected === key ? ' kc-selected' : '');
            var label = d.getDate() + ' ' + MONTHS[d.getMonth()] + (list.length ? ', ' + list.length + (list.length === 1 ? ' post' : ' poster') : ', inget inlagt');
            html += '<div class="' + cls + '" role="button" tabindex="0" data-rc-day="' + key + '" aria-label="' + esc(label) + '">' +
                '<span class="kc-num">' + d.getDate() + '</span>' + (hol[key] ? '<span class="kc-hol">' + esc(hol[key]) + '</span>' : '') +
                list.slice(0, MAX_CHIPS).map(chip).join('') +
                (list.length > MAX_CHIPS ? '<div class="rc-more">+' + (list.length - MAX_CHIPS) + ' till</div>' : '') + '</div>';
        }
        document.getElementById('rcBody').innerHTML = html + '</div>';
    }

    function renderList(items) {
        if (!items.length) {
            document.getElementById('rcBody').innerHTML = '<div class="text-center text-muted py-4 border rounded">Inget inlagt på banan de kommande sex månaderna' +
                (document.getElementById('rcK_allocation').checked ? '' : ' (fasta tider är dolda)') + '.</div>';
            return;
        }
        var map = byDate(items), html = '', month = '';
        Object.keys(map).sort().forEach(function (key) {
            var d = parse(key);
            var m = MONTHS[d.getMonth()] + ' ' + d.getFullYear();
            if (m !== month) { month = m; html += '<div class="kc-list-month">' + esc(m.replace(/^./, function (c) { return c.toUpperCase(); })) + '</div>'; }
            html += '<div class="kc-list-row"><div class="kc-list-date">' + esc(d.toLocaleDateString('sv-SE', { weekday: 'short', day: 'numeric', month: 'short' })) + '</div>' +
                '<div class="flex-grow-1">' + map[key].map(dayLine).join('') + '</div></div>';
        });
        document.getElementById('rcBody').innerHTML = html;
    }

    function dayLine(i) {
        var k = KINDS[i.kind] || {};
        var title = i.url ? '<a href="' + esc(i.url) + '" target="_blank" rel="noopener">' + esc(i.title) + '</a>' : esc(i.title);
        if (i.kind === 'allocation') title = 'Fast tid' + (i.note ? ' <span class="text-muted">(' + esc(i.note) + ')</span>' : '');
        var del = i.kind === 'external' && state.data && state.data.canManage
            ? ' <button type="button" class="btn btn-link btn-sm p-0 ms-2 text-danger" data-rc="external-delete" data-id="' + i.sourceId + '">Ta bort</button>' +
              ' <button type="button" class="btn btn-link btn-sm p-0 ms-2" data-rc="external-edit" data-id="' + i.sourceId + '" data-date="' + esc(i.date) + '">Ändra</button>'
            : '';
        return '<div class="rc-day-row' + (i.isCancelled ? ' rc-cancel' : '') + '">' +
            '<div class="rc-day-time">' + esc(timeText(i)) + '</div>' +
            '<i class="rc-sw" style="--rc-c:' + colorOf(i) + '"></i>' +
            '<div class="flex-grow-1"><div><i class="bi ' + (k.icon || '') + ' me-1 text-muted" title="' + esc(k.label || '') + '"></i>' + title +
            (i.isCancelled ? ' <span class="badge bg-danger">Inställd</span>' : '') + del + '</div>' +
            // Sorten står redan i titeln för en fast tid och i ägaren för en extern bokning.
            '<div class="small text-muted">' + esc([i.ownerName, i.kind === 'allocation' || i.kind === 'external' ? '' : k.label, i.sectionLabel].filter(Boolean).join(' · ')) +
            (i.kind === 'external' && i.note ? ' · ' + esc(i.note) : '') + '</div></div></div>';
    }

    function renderDay() {
        var box = document.getElementById('rcDay');
        if (state.view !== 'month' || !state.selected) { box.innerHTML = ''; return; }
        var items = visible().filter(function (i) { return i.date === state.selected; });
        var d = parse(state.selected);
        var head = '<h4 class="h6 mb-2">' + esc(d.toLocaleDateString('sv-SE', { weekday: 'long', day: 'numeric', month: 'long' })) + '</h4>';
        box.innerHTML = head + (items.length ? items.map(dayLine).join('')
            : '<div class="text-muted small">Inget inlagt på banan den dagen.</div>') +
            (state.data && state.data.canManage
                ? '<div class="mt-2"><button type="button" class="btn btn-outline-primary btn-sm" data-rc="external-new" data-date="' + state.selected + '"><i class="bi bi-plus-circle me-1"></i>Lägg in en bokning för andra den här dagen</button></div>'
                : '');
    }

    // ── Prenumeration ─────────────────────────────────────────────────────
    function openIcs() {
        var body = document.getElementById('rcIcsBody');
        body.innerHTML = '<div class="text-center py-3"><div class="spinner-border spinner-border-sm" role="status"></div></div>';
        bootstrap.Modal.getOrCreateInstance(document.getElementById('rcIcsModal')).show();
        fetch('/umbraco/surface/RangeCalendarApi/GetIcsLink?rangeId=' + rangeId, { credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (d) {
                if (!d.success) { body.innerHTML = '<div class="alert alert-warning py-2">' + esc(d.message || 'Länken kunde inte skapas.') + '</div>'; return; }
                body.innerHTML = '<label class="form-label small" for="rcIcsUrl">Prenumerationslänk</label>' +
                    '<div class="input-group mb-2"><input class="form-control form-control-sm" id="rcIcsUrl" readonly value="' + esc(d.url) + '">' +
                    '<button class="btn btn-outline-secondary btn-sm" type="button" data-rc="ics-copy">Kopiera</button></div>' +
                    '<a class="btn btn-primary btn-sm" href="' + esc(d.webcal) + '"><i class="bi bi-calendar-plus me-1"></i>Öppna i kalenderappen</a>' +
                    '<div class="form-text mt-2">Fungerar inte knappen: kopiera länken och lägg till den som en prenumererad kalender (”Lägg till kalender från webbadress”).</div>';
            });
    }

    // ── Bokning för andra ─────────────────────────────────────────────────
    function openExternal(date, item) {
        var d = state.data || {};
        document.getElementById('rcExtTitle').textContent = item ? 'Ändra bokningen' : 'Lägg in en bokning för andra';
        document.getElementById('rcExtId').value = item ? item.sourceId : '';
        document.getElementById('rcExtWho').value = item ? item.title : '';
        document.getElementById('rcExtStart').value = item && item.start ? item.start : '';
        document.getElementById('rcExtEnd').value = item && item.end ? item.end : '';
        document.getElementById('rcExtNote').value = item && item.note ? item.note : '';
        document.getElementById('rcExtErr').textContent = '';
        var el = document.getElementById('rcExtDate');
        if (window.flatpickr && !el._flatpickr) flatpickr(el, { locale: 'sv', dateFormat: 'Y-m-d', allowInput: true });
        var v = item ? item.date : (date || '');
        if (el._flatpickr) { if (v) el._flatpickr.setDate(v, false); else el._flatpickr.clear(); } else el.value = v;
        var secWrap = document.getElementById('rcExtSectionWrap');
        var sec = document.getElementById('rcExtSection');
        if ((d.sections || []).length) {
            sec.innerHTML = '<option value="">Hela banan</option>' + d.sections.map(function (s) {
                return '<option value="' + s.id + '">' + esc(s.label) + '</option>';
            }).join('');
            sec.value = item && item.sectionId ? String(item.sectionId) : '';
            secWrap.classList.remove('d-none');
        } else secWrap.classList.add('d-none');
        bootstrap.Modal.getOrCreateInstance(document.getElementById('rcExtModal')).show();
    }
    function saveExternal() {
        var btn = document.getElementById('rcExtSave');
        btn.disabled = true;
        post('/umbraco/surface/RangeCalendarApi/SaveExternal', {
            id: parseInt(document.getElementById('rcExtId').value, 10) || 0,
            rangeId: rangeId,
            rangeSectionId: parseInt(document.getElementById('rcExtSection').value, 10) || null,
            date: document.getElementById('rcExtDate').value,
            startTime: document.getElementById('rcExtStart').value,
            endTime: document.getElementById('rcExtEnd').value,
            title: document.getElementById('rcExtWho').value,
            note: document.getElementById('rcExtNote').value
        }).then(function (r) {
            btn.disabled = false;
            if (!r.success) { document.getElementById('rcExtErr').textContent = r.message || 'Bokningen kunde inte sparas.'; return; }
            bootstrap.Modal.getInstance(document.getElementById('rcExtModal')).hide();
            state.selected = document.getElementById('rcExtDate').value || state.selected;
            load();
        }).catch(function () { btn.disabled = false; document.getElementById('rcExtErr').textContent = 'Bokningen kunde inte sparas.'; });
    }
    function deleteExternal(id) {
        var item = ((state.data && state.data.items) || []).find(function (i) { return i.kind === 'external' && i.sourceId === id; });
        var text = 'Bokningen' + (item ? ' för ' + item.title + ' ' + item.date : '') + ' tas bort ur banans kalender.';
        // Sidans egen fråga (range-page.js) — ingen confirm(), som har rapporterats som att sidan frös.
        var ask = window.hpskRangeConfirm ? window.hpskRangeConfirm('Ta bort bokningen?', text, 'Ta bort', true) : Promise.resolve(confirm(text));
        ask.then(function (ok) {
            if (!ok) return;
            post('/umbraco/surface/RangeCalendarApi/DeleteExternal', { id: id }).then(function (r) {
                if (!r.success) { msg(esc(r.message || 'Bokningen kunde inte tas bort.')); return; }
                msg('');
                load();
            });
        });
    }

    // ── Lyssnare ──────────────────────────────────────────────────────────
    document.addEventListener('click', function (e) {
        var day = e.target.closest('[data-rc-day]');
        if (day && app.contains(day)) {
            state.selected = day.getAttribute('data-rc-day');
            document.querySelectorAll('#rcBody .kc-selected').forEach(function (x) { x.classList.remove('kc-selected'); });
            day.classList.add('kc-selected');
            renderDay();
            var box = document.getElementById('rcDay');
            if (box.getBoundingClientRect().top > window.innerHeight - 80) box.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            return;
        }
        var b = e.target.closest('[data-rc]');
        if (!b) return;
        var act = b.getAttribute('data-rc');
        if (act === 'prev' || act === 'next') {
            state.month = new Date(state.month.getFullYear(), state.month.getMonth() + (act === 'next' ? 1 : -1), 1);
            state.selected = null; load();
        } else if (act === 'today') {
            var t = new Date(); state.month = new Date(t.getFullYear(), t.getMonth(), 1);
            state.selected = ymd(t); load();
        } else if (act === 'subscribe') openIcs();
        else if (act === 'external-new') openExternal(b.getAttribute('data-date') || state.selected || ymd(new Date()));
        else if (act === 'external-edit') {
            var id = parseInt(b.getAttribute('data-id'), 10);
            var it = ((state.data && state.data.items) || []).find(function (i) { return i.kind === 'external' && i.sourceId === id; });
            if (it) openExternal(null, it);
        }
        else if (act === 'external-delete') deleteExternal(parseInt(b.getAttribute('data-id'), 10));
        else if (act === 'ics-copy') {
            var input = document.getElementById('rcIcsUrl');
            input.select();
            (navigator.clipboard ? navigator.clipboard.writeText(input.value) : Promise.reject()).then(function () {
                b.textContent = 'Kopierad';
            }).catch(function () { document.execCommand('copy'); b.textContent = 'Kopierad'; });
        }
    });
    document.addEventListener('keydown', function (e) {
        var day = e.target.closest && e.target.closest('[data-rc-day]');
        if (day && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); day.click(); }
    });
    document.getElementById('rcExtSave').addEventListener('click', saveExternal);
    document.querySelectorAll('input[name="rcView"]').forEach(function (r) {
        r.addEventListener('change', function () {
            state.view = r.value;
            try { localStorage.setItem('hpsk_rc_view', state.view); } catch (e) { }
            document.querySelectorAll('[data-rc="prev"],[data-rc="next"],[data-rc="today"]').forEach(function (x) { x.classList.toggle('d-none', state.view === 'list'); });
            load();
        });
    });
    document.getElementById('rcFilters').addEventListener('change', function () {
        try {
            var f = {};
            document.querySelectorAll('#rcFilters input[type=checkbox]').forEach(function (c) { f[c.id] = c.checked; });
            localStorage.setItem('hpsk_rc_filters', JSON.stringify(f));
        } catch (e) { }
        render();
    });

    // Filtren och vyn minns per webbläsare. Tomt eller blockerat lagring = standard.
    try {
        var saved = JSON.parse(localStorage.getItem('hpsk_rc_filters') || 'null');
        if (saved) Object.keys(saved).forEach(function (id) { var c = document.getElementById(id); if (c) c.checked = !!saved[id]; });
        if (localStorage.getItem('hpsk_rc_view') === 'list') {
            state.view = 'list';
            document.getElementById('rcViewList').checked = true;
            document.querySelectorAll('[data-rc="prev"],[data-rc="next"],[data-rc="today"]').forEach(function (x) { x.classList.add('d-none'); });
        }
    } catch (e) { }

    state.selected = ymd(new Date());
    var started = false;
    window.hpskRangeCalendar = {
        /** Första gången delen visas — kalendern hämtas inte för den som aldrig öppnar den. */
        start: function () { if (!started) { started = true; load(); } },
        reload: load,
        openIcs: openIcs,
        openExternal: function () { openExternal(state.selected || ymd(new Date())); }
    };
})();
