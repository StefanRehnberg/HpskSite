// Dialogen Medlemskap (2026-11-26): en medlems typ, status och tillhörighet på ETT ställe.
// Delas av Medlemmar (klubbadmin) och Ekonomi → Medlemsavgifter (kassören).
//   window.hpskMembership.open({ clubId, memberId, year?, onSaved? })
// Läser ClubMembership/GetCard, sparar med ClubMembership/SaveCard (allt i ett anrop). Inga belopp för klubbadmin.
(function () {
    'use strict';
    var S = null; // { o, d, initial, dirty }

    function el(id) { return document.getElementById(id); }
    function esc(s) { return (s == null ? '' : String(s)).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function token() { var t = document.querySelector('#hmModal input[name="__RequestVerificationToken"]') || document.querySelector('input[name="__RequestVerificationToken"]'); return t ? t.value : ''; }
    function datum(iso) {
        if (!iso) return '';
        var p = iso.split('-'); if (p.length !== 3) return iso;
        var m = ['januari', 'februari', 'mars', 'april', 'maj', 'juni', 'juli', 'augusti', 'september', 'oktober', 'november', 'december'];
        return parseInt(p[2], 10) + ' ' + m[parseInt(p[1], 10) - 1] + ' ' + p[0];
    }
    function today() { var d = new Date(); return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0'); }
    function onEkonomi() { return /^\/ekonomi/i.test(location.pathname); }

    function showError(msg) {
        var box = el('hmError');
        if (!box) { box = document.createElement('div'); box.id = 'hmError'; box.className = 'alert alert-danger'; box.setAttribute('role', 'alert'); el('hmBody').insertBefore(box, el('hmBody').firstChild); }
        box.textContent = msg; box.hidden = false;
        box.scrollIntoView({ block: 'nearest' });
    }
    function clearError() { var b = el('hmError'); if (b) b.hidden = true; }

    function toast(msg) {
        var old = el('hmToast'); if (old) old.remove();
        var t = document.createElement('div');
        t.id = 'hmToast'; t.className = 'hm-toast'; t.setAttribute('role', 'status');
        t.innerHTML = '<span>' + esc(msg) + '</span><button type="button" aria-label="Stäng">×</button>';
        t.querySelector('button').addEventListener('click', function () { t.remove(); });
        document.body.appendChild(t);
        setTimeout(function () { if (t.parentNode) t.remove(); }, 10000);
    }

    // ── Ritning ────────────────────────────────────────────────────────────────────
    function typesCard(d) {
        if (d.typeLocked) {
            var h = d.household;
            var txt = h.isPrimary
                ? esc(d.firstName) + ' är huvudmedlem i en familj och betalar familjeavgiften. Typen är Familj.'
                : esc(d.firstName) + ' ingår i familjen med ' + esc(h.mainName || 'huvudmedlemmen') + '. Familjeavgiften betalas av ' + esc(h.mainName || 'huvudmedlemmen') + '.';
            return '<div class="hm-card"><div class="hm-h">Hur är ' + esc(d.firstName) + ' medlem?</div><p class="mb-0">' + txt + '</p></div>';
        }
        var cur = S.cur.type;
        var h = '<div class="hm-card"><div class="hm-h">Hur är ' + esc(d.firstName) + ' medlem?</div>';
        if (d.suggestion) h += '<div class="hm-sug" id="hmSug"><span>' + esc(d.firstName) + ' är ' + d.suggestion.age + ' år. ' + esc(d.suggestion.name) + ' passar bättre.</span>'
            + '<button type="button" class="btn btn-sm btn-outline-secondary" data-hm-pick="' + esc(d.suggestion.key) + '">Välj ' + esc(d.suggestion.name) + '</button></div>';
        h += '<div id="hmTypes">';
        d.types.forEach(function (t) {
            var on = (t.key || '').toLowerCase() === (cur || '').toLowerCase();
            h += '<label class="hm-opt"><input type="radio" name="hm_type" value="' + esc(t.key) + '"' + (on ? ' checked' : '') + '><span><b>' + esc(t.name) + '</b>'
               + (t.help ? '<span>' + esc(t.help) + '</span>' : '') + '</span></label>';
        });
        h += '<label class="hm-opt"><input type="radio" name="hm_type" value=""' + (cur ? '' : ' checked') + '><span><b>Ingen typ vald</b><span>Medlemmen väljer själv vid betalningen, om klubben tillåtit det</span></span></label>';
        return h + '</div></div>';
    }

    function statusCard(d) {
        var c = S.cur;
        var h = '<div class="hm-card"><div class="hm-h">Medlemskapet</div>';
        [['Medlem', 'Medlem', 'Är med i klubben'], ['Vilande', 'Vilande', 'Är kvar i registret men räknas inte som aktiv'], ['Slut', 'Har slutat', 'Försvinner ur klubbens listor']].forEach(function (o) {
            h += '<label class="hm-opt"><input type="radio" name="hm_status" value="' + o[0] + '"' + (c.status === o[0] ? ' checked' : '') + '><span><b>' + o[1] + '</b><span>' + o[2] + '</span></span></label>';
        });
        h += '<div id="hmSlut"' + (c.status === 'Slut' ? '' : ' hidden') + '>'
           + '<div class="hm-field"><label for="hm_until">Slutade den</label><input type="text" id="hm_until" class="form-control" autocomplete="off" max="' + today() + '" value="' + esc(c.until || today()) + '"></div>'
           + '<div class="hm-field"><label for="hm_reason">Varför</label><select id="hm_reason" class="form-select">';
        d.reasons.forEach(function (r) { h += '<option' + (c.reason === r ? ' selected' : '') + '>' + esc(r) + '</option>'; });
        h += '</select></div><div class="hm-field" id="hmReasonTextWrap"' + (c.reason === 'Annat' ? '' : ' hidden') + '><label for="hm_reasonText">Skriv orsaken</label><input type="text" id="hm_reasonText" class="form-control" maxlength="200" value="' + esc(c.reasonText || '') + '"></div>'
           + '<p class="hm-hint">När du sparar tas avgifter som inte är skickade bort för ' + esc(d.firstName) + '.</p></div>';
        h += '<div class="hm-since" id="hmSince">';
        if (c.since) h += '<span>Medlem sedan ' + esc(datum(c.since)) + '</span><button type="button" class="btn btn-link p-0" data-hm-act="since">Ändra</button>';
        else h += '<span>Medlem sedan: inte angivet</span><button type="button" class="btn btn-link p-0" data-hm-act="since">Ange datum</button>';
        h += '</div><div class="hm-field" id="hmSinceField" hidden><label for="hm_since">Medlem sedan</label><input type="text" id="hm_since" class="form-control" autocomplete="off" max="' + today() + '" value="' + esc(c.since || '') + '"></div></div>';
        return h;
    }

    function optionsCard(d) {
        if (!d.options.length && !d.legacyFederations) return '';
        var c = S.cur, names = d.options.filter(function (o) { return c.options.indexOf(o.id) >= 0; }).map(function (o) { return o.name; });
        var h = '<details class="hm-card" id="hmOptionsCard"' + (names.length ? ' open' : '') + '><summary>Är ' + esc(d.firstName) + ' även med i något förbund?<small id="hmOptionsSum">' + (names.length ? esc(names.join(', ')) : 'Inget valt') + '</small></summary>';
        if (!d.options.length) h += '<p class="hm-hint mb-0">Klubben har inga förbund inlagda än. Kassören lägger in dem under Medlemsavgifter → Åtgärder.</p>';
        d.options.forEach(function (o) {
            h += '<label class="hm-opt"><input type="checkbox" name="hm_opt" value="' + o.id + '"' + (c.options.indexOf(o.id) >= 0 ? ' checked' : '') + '><span><b>' + esc(o.name) + '</b></span></label>';
        });
        if (d.legacyFederations) h += '<p class="hm-hint">Tidigare ifyllt: ' + esc(d.legacyFederations) + (d.options.length ? '. Kryssa rätt ruta ovan.' : '.') + '</p>';
        return h + '</details>';
    }

    function brickHtml(list, kind) {
        return list.map(function (b) { return '<span class="hm-brick" data-hm-brick="' + kind + ':' + b.id + '">' + esc(b.name) + '<button type="button" aria-label="Ta bort ' + esc(b.name) + '" data-hm-del="' + kind + ':' + b.id + '">×</button></span>'; }).join('');
    }

    function familyCard(d) {
        var h = d.household, c = S.cur;
        if (h.inHousehold && !h.isPrimary) {
            return '<details class="hm-card"><summary>Familj<small>Ingår i ' + esc(h.mainName || 'en familj') + 's familj</small></summary><p class="mb-0">' + esc(d.firstName) + ' ingår i familjen med ' + esc(h.mainName || 'huvudmedlemmen') + '. Ändra familjen hos ' + esc(h.mainName || 'huvudmedlemmen') + '.</p></details>';
        }
        var sum = c.family.length ? esc(d.firstName) + ' + ' + c.family.length + ' till' : 'Ingen familj';
        var x = '<details class="hm-card" id="hmFamilyCard"' + (c.family.length ? ' open' : '') + '><summary>Familj<small id="hmFamilySum">' + sum + '</small></summary>'
              + '<div id="hmFamilyBricks">' + brickHtml(c.family, 'f') + '</div>'
              + '<div class="hm-field"><label for="hm_famAdd">Lägg till familjemedlem</label><input type="text" id="hm_famAdd" class="form-control" list="hm_famList" placeholder="Skriv ett namn…" autocomplete="off"><datalist id="hm_famList"></datalist></div>'
              + '<p class="hm-hint">Den som betalar familjeavgiften blir huvudmedlem. De andra får typen Familj. Tar du bort alla upplöses familjen.</p></details>';
        return x;
    }

    function clubsCard(d) {
        if (!d.clubs) return '';
        var c = S.cur, name = function (id) { var k = d.clubs.all.filter(function (x) { return x.id === id; })[0]; return k ? k.name : ('Klubb ' + id); };
        var h = '<details class="hm-card" id="hmClubsCard"><summary>Klubbar<small id="hmClubsSum">' + (c.home ? 'Hemmaklubb: ' + esc(name(c.home)) : 'Ingen hemmaklubb') + '</small></summary>'
              + '<div class="hm-field mt-0"><label for="hm_home">Hemmaklubb</label><select id="hm_home" class="form-select"><option value="">Ingen</option>';
        d.clubs.all.forEach(function (k) { h += '<option value="' + k.id + '"' + (k.id === c.home ? ' selected' : '') + '>' + esc(k.name) + '</option>'; });
        h += '</select></div><div class="hm-field"><label for="hm_clubAdd">Är också medlem i</label><div id="hmClubBricks">' + brickHtml(c.extra.map(function (id) { return { id: id, name: name(id) }; }), 'c') + '</div>'
           + '<input type="text" id="hm_clubAdd" class="form-control" list="hm_clubList" placeholder="Skriv klubbens namn…" autocomplete="off"><datalist id="hm_clubList"></datalist></div></details>';
        return h;
    }

    function otherCard(d) {
        var c = S.cur;
        return '<details class="hm-card"><summary>Övrigt<small>Belastningsregister, anteckningar</small></summary>'
            + '<label class="hm-opt"><input type="checkbox" id="hm_bg"' + (c.bg ? ' checked' : '') + '><span><b>Kontroll mot belastningsregistret är godkänd</b></span></label>'
            + '<div class="hm-field"><label for="hm_bgDate">Datum för kontrollen</label><input type="text" id="hm_bgDate" class="form-control" autocomplete="off" max="' + today() + '" value="' + esc(c.bgDate || '') + '"></div>'
            + '<label class="hm-opt mt-3"><input type="checkbox" id="hm_map"' + (c.map ? ' checked' : '') + '><span><b>Finns i MAP</b><span>MAP är förbundets medlemsregister</span></span></label>'
            + '<div class="hm-field"><label for="hm_notes">Anteckningar (syns bara för klubben)</label><textarea id="hm_notes" class="form-control" rows="2" maxlength="4000">' + esc(c.notes || '') + '</textarea></div></details>';
    }

    function kr(n) { return (Math.round(n * 100) / 100).toString().replace('.', ',') + ' kr'; }

    // Förslaget (ingen avgift skapad än): samma tal som Medlemsavgifter visar. Beloppet ändras inte av ett osparat val i dialogen.
    function proposalText(p) {
        if (!p) return 'ingen avgift skapad än';
        switch (p.kind) {
            case 'Individual': case 'HouseholdPrimary': return 'förslag ' + kr(p.amount) + ', ännu inte skapad';
            case 'Covered': return 'ingår i familjeavgiften' + (p.coveredBy ? ' som ' + p.coveredBy + ' betalar' : '');
            case 'NoCategory': return 'klubben har ingen avgift för medlemstypen i år';
            case 'ChooseType': return 'medlemmen väljer typ själv på betalsidan';
            default: return 'ingen avgift skapad än, medlemmen saknar medlemstyp';
        }
    }

    function proposalLines(p) {
        if (!p || !p.lines || p.lines.length < 2 || (p.kind !== 'Individual' && p.kind !== 'HouseholdPrimary')) return '';
        return '<ul class="hm-hint mb-0 mt-1 ps-3">' + p.lines.map(function (l) {
            return '<li>' + esc(l.text) + ': ' + esc(kr(l.amount)) + '</li>';
        }).join('') + '</ul>';
    }

    function feeNote(d) {
        if (d.canSeeFees) {
            var t = d.charge && d.charge.text ? d.charge.text : proposalText(d.charge && d.charge.proposal);
            return '<div class="hm-fee">Avgift ' + d.year + ': ' + esc(t) + '.' + proposalLines(d.charge && d.charge.proposal) + (onEkonomi() ? '' : ' <a href="/ekonomi?type=0&id=' + d.clubId + '&vy=arsavgift">Öppna Medlemsavgifter</a>') + '</div>';
        }
        return '<p class="hm-hint mb-0">När du sparar ser kassören ändringen under Medlemsavgifter.</p>';
    }

    function render() {
        var d = S.d;
        el('hmTitle').textContent = 'Medlemskap – ' + d.name;
        el('hmBody').innerHTML = '<div id="hmError" class="alert alert-danger" role="alert" hidden></div>'
            + typesCard(d) + statusCard(d) + optionsCard(d) + familyCard(d) + clubsCard(d) + otherCard(d) + feeNote(d);
        fillLists();
        initDates();
        S.initial = JSON.stringify(collect());
        el('hmSave').disabled = true;
    }

    // Husets datumväljare: flatpickr, svenska, ÅÅÅÅ-MM-DD. Fältets .value förblir ISO, så läsning och sparande är oförändrade.
    // static: kalendern ligger i dialogens flöde (annars klipps den på telefon). allowInput: datumet går att skriva.
    function initDates() {
        if (!window.flatpickr) return;
        ['hm_until', 'hm_since', 'hm_bgDate'].forEach(function (id) {
            var f = el(id); if (!f) return;
            flatpickr(f, { locale: 'sv', dateFormat: 'Y-m-d', maxDate: 'today', allowInput: true, static: true, disableMobile: true });
        });
    }

    function fillLists() {
        var d = S.d, fl = el('hm_famList');
        if (fl) {
            var taken = S.cur.family.map(function (b) { return b.id; });
            S.famMap = {};
            var names = {};
            d.household.candidates.forEach(function (c) { names[c.name] = (names[c.name] || 0) + 1; });
            fl.innerHTML = d.household.candidates.filter(function (c) { return taken.indexOf(c.id) < 0; }).map(function (c) {
                var label = names[c.name] > 1 ? c.name + ' (nr ' + c.id + ')' : c.name;
                S.famMap[label.toLowerCase()] = c; return '<option value="' + esc(label) + '">';
            }).join('');
        }
        var cl = el('hm_clubList');
        if (cl && d.clubs) {
            var have = S.cur.extra.concat([S.cur.home]);
            S.clubMap = {};
            cl.innerHTML = d.clubs.all.filter(function (k) { return have.indexOf(k.id) < 0; }).map(function (k) { S.clubMap[k.name.toLowerCase()] = k; return '<option value="' + esc(k.name) + '">'; }).join('');
        }
    }

    // ── Läsa av dialogen ──────────────────────────────────────────────────────────
    function collect() {
        var d = S.d, c = S.cur;
        var type = d.typeLocked ? c.type : ((document.querySelector('input[name="hm_type"]:checked') || {}).value || '');
        var status = (document.querySelector('input[name="hm_status"]:checked') || {}).value || c.status;
        var g = function (id) { var e = el(id); return e ? e.value : ''; };
        return {
            type: type, status: status,
            until: g('hm_until'), reason: g('hm_reason'), reasonText: g('hm_reasonText'),
            since: el('hmSinceField') && !el('hmSinceField').hidden ? g('hm_since') : (c.since || ''),
            options: Array.prototype.slice.call(document.querySelectorAll('input[name="hm_opt"]:checked')).map(function (e) { return parseInt(e.value, 10); }).sort(),
            family: c.family.map(function (b) { return b.id; }).sort(),
            home: el('hm_home') ? (parseInt(g('hm_home'), 10) || 0) : c.home,
            extra: c.extra.slice().sort(),
            bg: !!(el('hm_bg') && el('hm_bg').checked), bgDate: g('hm_bgDate'),
            map: !!(el('hm_map') && el('hm_map').checked), notes: g('hm_notes')
        };
    }

    function changed(keys) { var a = JSON.parse(S.initial), b = collect(); return keys.some(function (k) { return JSON.stringify(a[k]) !== JSON.stringify(b[k]); }); }

    function updateSummaries() {
        var d = S.d, v = collect();
        var s = el('hmOptionsSum');
        if (s) { var n = d.options.filter(function (o) { return v.options.indexOf(o.id) >= 0; }).map(function (o) { return o.name; }); s.textContent = n.length ? n.join(', ') : 'Inget valt'; }
        var f = el('hmFamilySum'); if (f) f.textContent = v.family.length ? d.firstName + ' + ' + v.family.length + ' till' : 'Ingen familj';
        var k = el('hmClubsSum');
        if (k && d.clubs) { var h = d.clubs.all.filter(function (x) { return x.id === v.home; })[0]; k.textContent = h ? 'Hemmaklubb: ' + h.name : 'Ingen hemmaklubb'; }
        el('hmSave').disabled = JSON.stringify(v) === S.initial;
        var rt = el('hmReasonTextWrap'); if (rt) rt.hidden = v.reason !== 'Annat';
        var sl = el('hmSlut'); if (sl) sl.hidden = v.status !== 'Slut';
    }

    // ── Spara ─────────────────────────────────────────────────────────────────────
    function needsConfirm() {
        var d = S.d, v = collect();
        if (!d.charge || (d.charge.state !== 'sent' && d.charge.state !== 'paid')) return null;
        var typeChanged = !d.typeLocked && changed(['type']);
        var left = v.status === 'Slut' && changed(['status']);
        if (!typeChanged && !left) return null;
        var what = d.charge.state === 'paid' ? 'betald' : 'skickad';
        return 'Avgiften för ' + d.year + ' är redan ' + what + ' till ' + d.name + '. Det som är ' + what + ' ändras inte. ' + (d.canSeeFees ? '' : 'Kassören får se att uppgifterna ändrats. ') + 'Vill du ändra ändå?';
    }

    function doSave() {
        clearError();
        var d = S.d, v = collect(), fd = new FormData();
        fd.append('__RequestVerificationToken', token());
        fd.append('memberId', d.memberId); fd.append('clubId', d.clubId); fd.append('year', d.year);
        fd.append('membershipType', v.type); fd.append('status', v.status);
        fd.append('reason', v.reason); fd.append('reasonText', v.reasonText);
        fd.append('memberSince', v.since); fd.append('memberUntil', v.status === 'Slut' ? v.until : '');
        fd.append('backgroundCheckApproved', v.bg); fd.append('backgroundCheckDate', v.bgDate);
        fd.append('registeredInMap', v.map); fd.append('memberNotes', v.notes);
        if (changed(['options'])) { fd.append('optionsTouched', 'true'); v.options.forEach(function (i) { fd.append('optionIds', i); }); }
        if (changed(['family'])) { fd.append('familyTouched', 'true'); v.family.forEach(function (i) { fd.append('familyMemberIds', i); }); }
        if (d.clubs && changed(['home', 'extra'])) { fd.append('clubsTouched', 'true'); fd.append('homeClubId', v.home || ''); fd.append('additionalClubIds', v.extra.join(',')); }
        var btn = el('hmSave'); btn.disabled = true; btn.textContent = 'Sparar…';
        fetch('/umbraco/surface/ClubMembership/SaveCard', { method: 'POST', body: fd, credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (res) {
                btn.textContent = 'Spara';
                if (!res.success) { btn.disabled = false; showError(res.message || 'Det gick inte att spara. Försök igen.'); return; }
                var s = res.summary || {};
                var msg = (s.name || d.name) + ': ' + (s.typeName || 'ingen typ vald') + ', ' + (s.statusText || 'medlem') + '.'
                    + (d.canSeeFees ? (res.chargesCleared > 0 ? ' Avgiften räknas om.' : '') : ' Kassören ser ändringen under Medlemsavgifter.');
                bootstrap.Modal.getOrCreateInstance(el('hmModal')).hide();
                toast(msg);
                if (S.o && typeof S.o.onSaved === 'function') S.o.onSaved(res);
            })
            .catch(function () { btn.textContent = 'Spara'; btn.disabled = false; showError('Det gick inte att spara. Kontrollera uppkopplingen och försök igen.'); });
    }

    function save() {
        var warn = needsConfirm();
        if (!warn) { doSave(); return; }
        el('hmConfirmText').textContent = warn;
        el('hmFooter').hidden = true; el('hmConfirm').hidden = false;
    }

    // ── Händelser ─────────────────────────────────────────────────────────────────
    function onBodyClick(e) {
        var t = e.target.closest ? e.target : e.target.parentNode;
        var pick = t.closest('[data-hm-pick]');
        if (pick) { var r = document.querySelector('input[name="hm_type"][value="' + pick.getAttribute('data-hm-pick') + '"]'); if (r) { r.checked = true; r.dispatchEvent(new Event('change', { bubbles: true })); } var s = el('hmSug'); if (s) s.remove(); return; }
        var act = t.closest('[data-hm-act]');
        if (act && act.getAttribute('data-hm-act') === 'since') { el('hmSince').hidden = true; el('hmSinceField').hidden = false; el('hm_since').focus(); updateSummaries(); return; }
        var del = t.closest('[data-hm-del]');
        if (del) {
            var p = del.getAttribute('data-hm-del').split(':'), id = parseInt(p[1], 10);
            if (p[0] === 'f') { S.cur.family = S.cur.family.filter(function (b) { return b.id !== id; }); el('hmFamilyBricks').innerHTML = brickHtml(S.cur.family, 'f'); }
            else { S.cur.extra = S.cur.extra.filter(function (x) { return x !== id; }); el('hmClubBricks').innerHTML = brickHtml(S.cur.extra.map(function (i) { return { id: i, name: clubName(i) }; }), 'c'); }
            fillLists(); updateSummaries();
        }
    }
    function clubName(id) { var k = S.d.clubs.all.filter(function (x) { return x.id === id; })[0]; return k ? k.name : ('Klubb ' + id); }

    function onBodyChange(e) {
        var t = e.target;
        if (t.id === 'hm_famAdd') {
            var c = S.famMap && S.famMap[t.value.trim().toLowerCase()];
            if (c) { S.cur.family.push({ id: c.id, name: c.name }); t.value = ''; el('hmFamilyBricks').innerHTML = brickHtml(S.cur.family, 'f'); fillLists(); }
        } else if (t.id === 'hm_clubAdd') {
            var k = S.clubMap && S.clubMap[t.value.trim().toLowerCase()];
            if (k) { S.cur.extra.push(k.id); t.value = ''; el('hmClubBricks').innerHTML = brickHtml(S.cur.extra.map(function (i) { return { id: i, name: clubName(i) }; }), 'c'); fillLists(); }
        }
        updateSummaries();
    }

    function wireOnce() {
        var m = el('hmModal'); if (!m || m.getAttribute('data-hm-wired')) return;
        m.setAttribute('data-hm-wired', '1');
        el('hmBody').addEventListener('click', onBodyClick);
        el('hmBody').addEventListener('change', onBodyChange);
        el('hmBody').addEventListener('input', function (e) { if (e.target.id === 'hm_notes' || e.target.id === 'hm_reasonText' || e.target.id === 'hm_bgDate' || e.target.id === 'hm_until' || e.target.id === 'hm_since') updateSummaries(); });
        el('hmSave').addEventListener('click', save);
        el('hmConfirmNo').addEventListener('click', function () { el('hmConfirm').hidden = true; el('hmFooter').hidden = false; });
        el('hmConfirmYes').addEventListener('click', function () { el('hmConfirm').hidden = true; el('hmFooter').hidden = false; doSave(); });
    }

    function open(o) {
        wireOnce();
        S = { o: o, d: null, cur: null, initial: '', famMap: {}, clubMap: {} };
        el('hmTitle').textContent = 'Medlemskap';
        el('hmBody').innerHTML = '<div class="text-center py-4"><div class="spinner-border" role="status"><span class="visually-hidden">Laddar…</span></div></div>';
        el('hmSave').disabled = true; el('hmConfirm').hidden = true; el('hmFooter').hidden = false;
        bootstrap.Modal.getOrCreateInstance(el('hmModal')).show();
        var qs = 'memberId=' + o.memberId + '&clubId=' + o.clubId + (o.year ? '&year=' + o.year : '');
        fetch('/umbraco/surface/ClubMembership/GetCard?' + qs, { credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (res) {
                if (!res.success) { el('hmBody').innerHTML = '<div class="alert alert-danger" role="alert">' + esc(res.message || 'Det gick inte att öppna medlemskapet.') + '</div>'; return; }
                var d = res.data; S.d = d;
                S.cur = {
                    type: d.type || '', status: d.status, until: d.memberUntil, reason: d.reason || d.reasons[0], reasonText: d.reasonText, since: d.memberSince,
                    options: (d.chosenOptionIds || []).slice(), family: (d.household.members || []).map(function (m) { return { id: m.id, name: m.name }; }),
                    home: d.clubs ? (d.clubs.homeClubId || 0) : 0, extra: d.clubs ? d.clubs.additionalClubIds.slice() : [],
                    bg: d.backgroundCheckApproved, bgDate: d.backgroundCheckDate, map: d.registeredInMap, notes: d.memberNotes
                };
                render();
            })
            .catch(function () { el('hmBody').innerHTML = '<div class="alert alert-danger" role="alert">Det gick inte att öppna medlemskapet. Kontrollera uppkopplingen och försök igen.</div>'; });
    }

    window.hpskMembership = { open: open, toast: toast };
})();
