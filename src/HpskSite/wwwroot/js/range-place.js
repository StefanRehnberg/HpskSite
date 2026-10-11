/*
 * Skjutbana + bana/vall i tränings- och händelsedialogerna (banans kalender, 2026-10-11).
 * Markupen: Views/Partials/_RangePlaceField.cshtml. Läses av:
 *   Admin → Träningar (prefix "ctr"), klubbens händelsedialog ("clubEvent"),
 *   kretsens händelsedialog ("regionEvent") och händelsesidans redigering ("editDetails").
 *
 * API (window.hpskRangePlace):
 *   mount(prefix, opts)  opts: { clubId, venueId, kind: 'training'|'event', read(): {date, start, end},
 *                                watchIds: [...] } — kopplar lyssnare EN gång per prefix.
 *   set(prefix, { rangeId, sectionId, isNew, excludeId }) → Promise. Ny post utan bana får klubbens förval.
 *   value(prefix) → { rangeId, sectionId }  (0 = ingen bana, null = hela banan)
 *   check(prefix) — läs om dagens läge på banan (krockvarningen).
 *
 * ⚠️ Inga användartexter i onclick: sökträffarna bär ett index i data-attribut.
 */
(function () {
    if (window.hpskRangePlace) return;

    var esc = function (s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[c];
        });
    };
    var states = {};
    var optionCache = {};   // clubId → Promise<{ ranges, defaultRangeId }>
    var infoCache = {};     // rangeId → Promise<{ range, sections }>

    function $(prefix, id) { return document.getElementById(prefix + id); }

    function loadOptions(clubId) {
        var key = String(clubId || 0);
        if (!optionCache[key]) {
            optionCache[key] = fetch('/umbraco/surface/RangeCalendarApi/GetPlaceOptions?clubId=' + (clubId || 0), { credentials: 'same-origin' })
                .then(function (r) { return r.json(); })
                .then(function (d) { return d && d.success ? d : { ranges: [], defaultRangeId: 0 }; })
                .catch(function () { delete optionCache[key]; return { ranges: [], defaultRangeId: 0 }; });
        }
        return optionCache[key];
    }
    function loadInfo(rangeId) {
        if (!infoCache[rangeId]) {
            infoCache[rangeId] = fetch('/umbraco/surface/RangeCalendarApi/GetRangeInfo?rangeId=' + rangeId, { credentials: 'same-origin' })
                .then(function (r) { return r.json(); })
                .then(function (d) { return d && d.success ? d : null; })
                .catch(function () { delete infoCache[rangeId]; return null; });
        }
        return infoCache[rangeId];
    }

    function rangeById(st, id) {
        var all = st.ranges.concat(st.extra);
        for (var i = 0; i < all.length; i++) if (all[i].id === id) return all[i];
        return null;
    }

    function renderSelect(st) {
        var sel = $(st.prefix, 'rpRange');
        var html = '<option value="0">Ingen skjutbana (annan plats)</option>';
        if (st.ranges.length) {
            html += '<optgroup label="Klubbens banor">' + st.ranges.map(function (r) {
                return '<option value="' + r.id + '">' + esc(r.name) + (r.city ? ' · ' + esc(r.city) : '') + '</option>';
            }).join('') + '</optgroup>';
        }
        if (st.extra.length) {
            html += '<optgroup label="Annan skjutbana">' + st.extra.map(function (r) {
                return '<option value="' + r.id + '">' + esc(r.name) + (r.city ? ' · ' + esc(r.city) : '') + '</option>';
            }).join('') + '</optgroup>';
        }
        html += '<option value="search">Sök en annan skjutbana…</option>';
        sel.innerHTML = html;
        sel.value = String(st.rangeId || 0);
    }

    function renderSections(st) {
        var wrap = $(st.prefix, 'rpSectionWrap');
        var sel = $(st.prefix, 'rpSection');
        var r = rangeById(st, st.rangeId);
        var sections = r ? (r.sections || []) : [];
        if (!sections.length) { wrap.classList.add('d-none'); sel.innerHTML = ''; st.sectionId = null; return; }
        sel.innerHTML = '<option value="">Hela banan</option>' + sections.map(function (s) {
            return '<option value="' + s.id + '">' + esc(s.label) + '</option>';
        }).join('');
        if (st.sectionId && !sections.some(function (s) { return s.id === st.sectionId; })) st.sectionId = null;
        sel.value = st.sectionId ? String(st.sectionId) : '';
        wrap.classList.remove('d-none');
    }

    function renderHint(st) {
        var hint = $(st.prefix, 'rpHint');
        var what = st.kind === 'training' ? 'träningen' : 'händelsen';
        if (st.rangeId > 0) {
            hint.innerHTML = 'Syns i banans kalender, där klubbarna som delar banan ser vad som händer. ' +
                '<a href="/skjutbana/kalender?r=' + st.rangeId + '" target="_blank" rel="noopener">Öppna banans kalender</a>';
        } else {
            hint.textContent = 'Välj en skjutbana så syns ' + what + ' i banans kalender. Skriv platsen nedan om den inte är en skjutbana.';
        }
    }

    function syncHidden(st) {
        var h = $(st.prefix, 'rangeId'); if (h) h.value = String(st.rangeId || 0);
        var hs = $(st.prefix, 'rangeSectionId'); if (hs) hs.value = st.sectionId ? String(st.sectionId) : '';
    }

    // Platsens fritext fylls med banans namn när den är tom — eller när den fortfarande bär namnet
    // den själv fyllde i senast, så ett byte av bana inte lämnar den gamla banans namn kvar.
    function fillVenue(st) {
        if (!st.venueId) return;
        var v = document.getElementById(st.venueId);
        if (!v) return;
        var r = rangeById(st, st.rangeId);
        var cur = (v.value || '').trim();
        if (r && (!cur || cur === st.autoVenue)) { v.value = r.name; st.autoVenue = r.name; }
        else if (!r && cur && cur === st.autoVenue) { v.value = ''; st.autoVenue = ''; }
    }

    function choose(st, rangeId, fromUser) {
        st.rangeId = rangeId || 0;
        if (!rangeById(st, st.rangeId)) st.sectionId = null;
        renderSelect(st);
        renderSections(st);
        renderHint(st);
        syncHidden(st);
        if (fromUser) fillVenue(st);
        check(st.prefix);
    }

    function wireSearch(st) {
        var input = $(st.prefix, 'rpSearch');
        var box = $(st.prefix, 'rpResults');
        var t = null, hits = [];
        input.addEventListener('input', function () {
            clearTimeout(t);
            var q = (input.value || '').trim();
            if (q.length < 2) { box.classList.add('d-none'); return; }
            t = setTimeout(function () {
                fetch('/umbraco/surface/ShootingRange/GetRangePicker?query=' + encodeURIComponent(q), { credentials: 'same-origin' })
                    .then(function (r) { return r.json(); })
                    .then(function (d) {
                        hits = (d && d.ranges) || [];
                        box.innerHTML = hits.length ? hits.map(function (x, i) {
                            return '<button type="button" class="list-group-item list-group-item-action py-1" data-rp-hit="' + i + '">' +
                                esc(x.name) + '<small class="text-muted">' + (x.city ? ' · ' + esc(x.city) : '') +
                                (x.municipality ? ' · ' + esc(x.municipality) : '') + '</small></button>';
                        }).join('') : '<div class="list-group-item py-1 text-muted small">Inga träffar.</div>';
                        box.classList.remove('d-none');
                    })
                    .catch(function () { box.classList.add('d-none'); });
            }, 250);
        });
        box.addEventListener('click', function (e) {
            var b = e.target.closest('[data-rp-hit]');
            if (!b) return;
            var hit = hits[parseInt(b.getAttribute('data-rp-hit'), 10)];
            if (!hit) return;
            box.classList.add('d-none');
            input.value = '';
            $(st.prefix, 'rpSearchWrap').classList.add('d-none');
            addExtra(st, hit.id).then(function () { choose(st, hit.id, true); });
        });
    }

    function addExtra(st, rangeId) {
        if (!rangeId || rangeById(st, rangeId)) return Promise.resolve();
        return loadInfo(rangeId).then(function (d) {
            if (!d || !d.range) return;
            st.extra.push({ id: d.range.id, name: d.range.name, city: d.range.city, sections: d.sections || [] });
        });
    }

    function check(prefix) {
        var st = states[prefix];
        if (!st) return;
        var box = $(prefix, 'rpDay');
        clearTimeout(st.checkTimer);
        if (!(st.rangeId > 0) || typeof st.read !== 'function') { box.innerHTML = ''; return; }
        var w = st.read() || {};
        if (!w.date) { box.innerHTML = ''; return; }
        st.checkTimer = setTimeout(function () {
            var seq = ++st.checkSeq;
            var q = 'rangeId=' + st.rangeId + '&date=' + encodeURIComponent(w.date) +
                (st.sectionId ? '&sectionId=' + st.sectionId : '') +
                (w.start ? '&start=' + encodeURIComponent(w.start) : '') +
                (w.end ? '&end=' + encodeURIComponent(w.end) : '') +
                (st.excludeId ? '&excludeKind=' + st.kind + '&excludeId=' + st.excludeId : '') +
                '&clubId=' + (st.clubId || 0);
            fetch('/umbraco/surface/RangeCalendarApi/GetDay?' + q, { credentials: 'same-origin' })
                .then(function (r) { return r.json(); })
                .then(function (d) {
                    if (seq !== st.checkSeq) return;
                    renderDay(st, (d && d.success && d.items) || []);
                })
                .catch(function () { if (seq === st.checkSeq) box.innerHTML = ''; });
        }, 300);
    }

    function line(i) {
        var time = i.start ? (i.start + (i.end ? '–' + i.end : '')) : 'hela dagen';
        var kind = { competition: 'Tävling', training: 'Träning', event: 'Händelse', allocation: 'Fast tid', external: 'Extern bokning' }[i.kind] || '';
        var title = i.kind === 'allocation' ? kind : (esc(i.title) + (kind && i.kind !== 'external' ? ' <span class="text-muted">(' + kind.toLowerCase() + ')</span>' : ''));
        return '<li>' + (i.ownerName ? '<strong>' + esc(i.ownerName) + '</strong> · ' : '') + title + ' · ' + esc(time) +
            (i.sectionLabel ? ' · ' + esc(i.sectionLabel) : '') + '</li>';
    }

    function renderDay(st, items) {
        var box = $(st.prefix, 'rpDay');
        // Egen klubbs fasta tid är ingen krock — den är beskedet att banan är er.
        var own = items.filter(function (i) { return i.ownClub && i.kind === 'allocation' && i.relation !== 'otherSection'; });
        var clash = items.filter(function (i) { return i.relation === 'overlap' && !(i.ownClub && i.kind === 'allocation'); });
        var rest = items.filter(function (i) { return clash.indexOf(i) < 0 && own.indexOf(i) < 0; });
        var html = '';
        if (clash.length) {
            html += '<div class="alert alert-warning py-2 mb-2 small"><i class="bi bi-exclamation-triangle me-1"></i>' +
                '<strong>Samtidigt på banan:</strong><ul class="mb-1 ps-3">' + clash.map(line).join('') + '</ul>' +
                'Det stoppar inte sparandet — men prata gärna med dem innan.</div>';
        }
        if (own.length) {
            html += '<div class="small text-success-emphasis mb-1"><i class="bi bi-check-circle me-1"></i>Er fasta tid: ' +
                own.map(function (i) { return esc(i.start + '–' + i.end) + (i.sectionLabel ? ' (' + esc(i.sectionLabel) + ')' : ''); }).join(', ') + '</div>';
        }
        if (rest.length) {
            html += '<details class="small text-muted"><summary>Också på banan den dagen (' + rest.length + ')</summary><ul class="mb-0 ps-3">' +
                rest.map(line).join('') + '</ul></details>';
        }
        if (!html && st.rangeId > 0) html = '<div class="small text-muted"><i class="bi bi-calendar-check me-1"></i>Inget annat inlagt på banan den dagen.</div>';
        box.innerHTML = html;
    }

    window.hpskRangePlace = {
        mount: function (prefix, opts) {
            opts = opts || {};
            var st = states[prefix];
            if (!st) {
                st = states[prefix] = { prefix: prefix, ranges: [], extra: [], rangeId: 0, sectionId: null, autoVenue: '', checkSeq: 0 };
                var sel = $(prefix, 'rpRange');
                if (!sel) return;
                sel.addEventListener('change', function () {
                    if (sel.value === 'search') {
                        sel.value = String(st.rangeId || 0);
                        $(prefix, 'rpSearchWrap').classList.remove('d-none');
                        $(prefix, 'rpSearch').focus();
                        return;
                    }
                    choose(st, parseInt(sel.value, 10) || 0, true);
                });
                $(prefix, 'rpSection').addEventListener('change', function () {
                    st.sectionId = parseInt($(prefix, 'rpSection').value, 10) || null;
                    syncHidden(st);
                    check(prefix);
                });
                wireSearch(st);
            }
            st.clubId = opts.clubId || 0;
            st.venueId = opts.venueId || null;
            st.kind = opts.kind || 'event';
            st.read = opts.read;
            (opts.watchIds || []).forEach(function (id) {
                var el = document.getElementById(id);
                if (!el || el['_rpWatch_' + prefix]) return;
                el['_rpWatch_' + prefix] = true;
                el.addEventListener('change', function () { check(prefix); });
                el.addEventListener('input', function () { check(prefix); });
            });
        },

        set: function (prefix, v) {
            var st = states[prefix];
            if (!st) return Promise.resolve();
            v = v || {};
            // ⚠️ Tills valet är inläst svarar value() null ("rör inte") och de dolda fälten är tomma —
            // en sparning i mellantiden får aldrig nolla en lagrad bana.
            st.ready = false;
            $(prefix, 'rangeId').value = '';
            $(prefix, 'rangeSectionId').value = '';
            st.excludeId = v.excludeId || 0;
            st.autoVenue = '';
            $(prefix, 'rpSearchWrap').classList.add('d-none');
            $(prefix, 'rpDay').innerHTML = '';
            return loadOptions(st.clubId).then(function (d) {
                st.ranges = (d.ranges || []).map(function (r) {
                    return { id: r.id, name: r.name, city: r.city, sections: r.sections || [] };
                });
                st.extra = [];
                var target = v.rangeId > 0 ? v.rangeId : (v.isNew ? (d.defaultRangeId || 0) : 0);
                st.sectionId = v.rangeId > 0 && v.sectionId ? v.sectionId : null;
                return addExtra(st, target).then(function () {
                    st.ready = true;
                    choose(st, target, false);
                    // Ett förval på en NY post fyller platsen, precis som ett eget val hade gjort.
                    if (v.isNew && !(v.rangeId > 0)) fillVenue(st);
                });
            });
        },

        value: function (prefix) {
            var st = states[prefix];
            return st && st.ready ? { rangeId: st.rangeId || 0, sectionId: st.sectionId || null } : { rangeId: null, sectionId: null };
        },

        check: check
    };
})();
