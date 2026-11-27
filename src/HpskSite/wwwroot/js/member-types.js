// Medlemstyper, åldersgränser, Åldersförslag och "Sätt typ på alla" (2026-11-26).
// EN komponent som delas av Medlemmar (klubbadmin och kassör) och Ekonomi → Medlemsavgifter (kassören).
//   window.hpskMemberTypes.openTypes(o)      Medlemstyper och åldersgränser
//   window.hpskMemberTypes.openAge(o)        Åldersförslag
//   window.hpskMemberTypes.openSetUntyped(o) Sätt typ på alla som saknar
//   window.hpskMemberTypes.summary(o)        Promise<{ untyped, ageEnabled, ageProposals }> (för menyraderna)
// o = { clubId, year?, canSeeFees?, readOnly?, clubUrl?, onChanged? }. Dialogerna finns i Partials/_MemberTypesDialogs.cshtml.
// Servern (MembershipFeeAdminController) prövar varje anrop: klubbadmin OCH kassör får ändra, belopp läser bara den som får läsa avgifter.
(function () {
    'use strict';
    if (window.hpskMemberTypes) return;

    var F = '/umbraco/surface/MembershipFeeAdmin/';
    var NEVER_AGE = { 'familj': 1, 'hedersmedlem': 1, 'ständig': 1, 'stödjande': 1 };
    var CAP = 15;
    var O = null;
    var types = [], untyped = 0, ageRules = { enabled: false, error: null };
    var ageEdit = {}, ageCur = null, ageAll = {}, ageResult = null, ageNames = {};
    // Åldersförslagen ritas i dialogen (id-prefix "mt") eller inbäddade på sidan (prefix "mtE", mountAge). AP = prefixet för
    // den yta som just nu används; varje ingång (öppna, klick, godkänn) sätter det först. Tillståndet delas — bara en yta används åt gången.
    var AP = 'mt';
    var subCfg = null;

    function $(id) { return document.getElementById(id); }
    function esc(s) { return (s == null ? '' : String(s)).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function kr(n) { return (Math.round((n || 0) * 100) / 100).toLocaleString('sv-SE', { maximumFractionDigits: 2 }) + ' kr'; }
    function show(el, on) { if (el) el.classList.toggle('d-none', !on); }
    function year() { return (O && O.year) || new Date().getFullYear(); }
    function typeOf(key) { var k = (key || '').toLowerCase(); return types.find(function (t) { return (t.key || '').toLowerCase() === k; }); }
    function typeName(key) { if (!key) return ''; var t = typeOf(key); return t ? t.name : key; }
    function neverAge(key) { return !!NEVER_AGE[(key || '').toLowerCase()]; }
    // Bekräftelse när sidan inte har en egen (stilen hm-toast kommer med dialogen Medlemskap).
    function toast(msg) {
        var old = $('mtToast'); if (old) old.remove();
        var t = document.createElement('div');
        t.id = 'mtToast'; t.className = 'hm-toast'; t.setAttribute('role', 'status');
        t.innerHTML = '<span>' + esc(msg) + '</span><button type="button" aria-label="Stäng">×</button>';
        t.querySelector('button').addEventListener('click', function () { t.remove(); });
        document.body.appendChild(t);
        setTimeout(function () { if (t.parentNode) t.remove(); }, 10000);
    }
    function changed() { if (O && O.onChanged) { try { O.onChanged(); } catch (e) { } } }

    async function token() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        if (el && el.value) return el.value;
        return (await (await fetch('/umbraco/surface/Club/GetFormToken', { cache: 'no-store' })).json()).token;
    }
    async function form(action, fields) {
        var fd = new FormData();
        Object.keys(fields).forEach(function (k) {
            var v = fields[k];
            if (Array.isArray(v)) v.forEach(function (x) { fd.append(k, x); }); else fd.append(k, v);
        });
        fd.append('__RequestVerificationToken', await token());
        var r = await fetch(F + action, { method: 'POST', body: fd });
        try { return await r.json(); } catch (e) { return { success: false, message: 'Något gick fel (' + r.status + ').' }; }
    }
    async function loadTypes() {
        try {
            var t = await (await fetch(F + 'GetMemberTypes?clubId=' + O.clubId, { cache: 'no-store' })).json();
            if (t && t.success) { types = t.types || []; untyped = t.untyped || 0; ageRules = t.ageRules || { enabled: false, error: null }; return true; }
        } catch (e) { }
        return false;
    }

    // ── Dialoger ────────────────────────────────────────────────────────────────────────
    // showModal(till, från): har man en dialog öppen stängs den först — Bootstrap staplar inte dialoger.
    function showModal(to, from) {
        var t = bootstrap.Modal.getOrCreateInstance($(to));
        var f = from ? $(from) : null;
        if (!f || !f.classList.contains('show')) { t.show(); return; }
        f.addEventListener('hidden.bs.modal', function once() { f.removeEventListener('hidden.bs.modal', once); t.show(); });
        bootstrap.Modal.getOrCreateInstance(f).hide();
    }
    // EN dialog för bekräftelser och namnfrågor. onOk returnerar ett felmeddelande, eller null när det gick.
    function openSub(cfg) {
        subCfg = cfg;
        cfg.back = cfg.back || cfg.from;
        $('mtSubTitle').textContent = cfg.title;
        $('mtSubBody').innerHTML = cfg.body;
        $('mtSubOk').textContent = cfg.ok || 'Spara';
        $('mtSubOk').className = 'btn ' + (cfg.danger ? 'btn-danger' : 'btn-primary');
        $('mtSubOk').disabled = false;
        show($('mtSubError'), false);
        showModal('mtSubModal', cfg.from);
    }

    // ── Medlemstyper och åldersgränser ──────────────────────────────────────────────────
    function savedAge(t) { return t.fromAge == null ? '' : String(t.fromAge); }
    // Typtabellen ritas på upp till två ställen: dialogen (id-prefix "mt") och den inbäddade ytan (prefix "mtE", mountTypes).
    // Samma tillstånd (types, ageEdit) och samma ritning; bara id:na skiljer, så de två kan aldrig krocka.
    var SET_PREFIXES = ['mt', 'mtE'];
    function eachSet(fn) { SET_PREFIXES.forEach(function (p) { if ($(p + 'TypeRows')) fn(p, p === 'mtE'); }); }
    var EMBED_O = null;
    function typesNote(ok, text) {
        eachSet(function (p) {
            var el = $(p + 'TypesError');
            if (!el) return;
            el.className = 'alert ' + (ok ? 'alert-success' : 'alert-danger') + ' mt-3 mb-0' + (text ? '' : ' d-none');
            el.textContent = text || '';
        });
    }
    function editedSteps() {
        var out = [];
        types.forEach(function (t) {
            if (!t.isActive || neverAge(t.key)) return;
            var v = (ageEdit[t.key] || '').trim(); if (v === '') return;
            var n = parseInt(v, 10); if (isNaN(n)) return;
            out.push({ key: t.key, name: t.name, from: n });
        });
        out.sort(function (a, b) { return a.from - b.from; });
        return out;
    }
    function ageDirty() {
        return types.some(function (t) { return !neverAge(t.key) && ((ageEdit[t.key] || '').trim()) !== savedAge(t); });
    }
    function renderAgeSummary() {
        eachSet(function (p) { renderAgeSummaryIn(p); });
    }
    function renderAgeSummaryIn(p) {
        var steps = editedSteps(), el = $(p + 'AgeSummary');
        if (!steps.length) {
            el.className = 'small text-muted';
            el.textContent = 'Ingen åldersgräns är satt. Då görs inga åldersförslag, och medlemmen väljer fritt på betalsidan.';
        } else {
            el.className = 'fw-semibold small';
            el.innerHTML = 'Åldersgrupper: ' + steps.map(function (s, i) {
                var next = steps[i + 1];
                return esc(s.name) + ' ' + (next ? s.from + '–' + (next.from - 1) : s.from + '+');
            }).join(' · ')
            + (steps[0].from > 0 ? '<div class="text-danger fw-normal">Den yngsta gruppen måste börja på 0 år.</div>' : '');
        }
        $(p + 'AgeSave').disabled = !ageDirty() || !!(O && O.readOnly);
        show($(p + 'AgeSuggest'), !ageRules.enabled && !steps.length && !(O && O.readOnly));
    }
    function typeMenu(t) {
        var key = esc(t.key), its = [];
        if (O && O.readOnly) return '';
        if (!/^familj$/i.test(t.key)) {
            its.push('<li><button type="button" class="dropdown-item" data-mt-tact="rename" data-mt-tkey="' + key + '">Byt namn…</button></li>');
            its.push('<li><button type="button" class="dropdown-item" data-mt-tact="' + (t.isActive ? 'hide' : 'unhide') + '" data-mt-tkey="' + key + '">'
                   + (t.isActive ? 'Dölj' : 'Visa') + '</button></li>');
        }
        if (t.isCustom) its.push('<li><button type="button" class="dropdown-item text-danger" data-mt-tact="delete" data-mt-tkey="' + key + '">Ta bort…</button></li>');
        if (!its.length) return '';
        return '<div class="dropdown"><button type="button" class="btn btn-sm btn-outline-secondary dropdown-toggle px-2" data-bs-toggle="dropdown" '
             + 'data-bs-popper-config=\'{"strategy":"fixed"}\'>Åtgärder</button><ul class="dropdown-menu dropdown-menu-end">' + its.join('') + '</ul></div>';
    }
    function renderTypes() {
        var html = typeRowsHtml();
        eachSet(function (p) { $(p + 'TypeRows').innerHTML = html; });
        renderAgeSummary();
    }
    function typeRowsHtml() {
        return types.map(function (t) {
            var never = neverAge(t.key);
            var nameCell = esc(t.name) + (t.isActive ? '' : ' <span class="badge bg-light text-dark border">Dold</span>')
                + (!t.isCustom && t.name !== t.key ? '<div class="small text-muted">Standardtyp: ' + esc(t.key) + '</div>' : '')
                + '<div class="small text-muted d-sm-none">' + (t.members || 0) + (t.members === 1 ? ' medlem' : ' medlemmar') + '</div>';
            var from = (never || !t.isActive) ? '<span class="text-muted small">—</span>'
                : '<input type="number" min="0" max="120" step="1" class="form-control form-control-sm px-1" style="width:3.5rem" autocomplete="off" '
                + 'data-mt-from="' + esc(t.key) + '" aria-label="Från ålder, ' + esc(t.name) + '" value="' + esc(ageEdit[t.key] || '') + '"'
                + (O && O.readOnly ? ' disabled' : '') + '>';
            return '<tr><td>' + nameCell + '</td><td class="text-end d-none d-sm-table-cell">' + (t.members || 0) + '</td><td>' + from + '</td><td class="text-end">' + typeMenu(t) + '</td></tr>';
        }).join('') || '<tr><td colspan="4" class="text-muted small">Inga medlemstyper.</td></tr>';
    }
    async function openTypes(o, from) {
        O = o;
        await loadTypes();
        ageEdit = {};
        types.forEach(function (t) { ageEdit[t.key] = savedAge(t); });
        typesNote(false, '');
        renderTypes();
        showModal('mtTypesModal', typeof from === 'string' ? from : null);
    }
    // Efter varje ändring: läs om (namnen står överallt) men behåll det som skrivits i åldersfälten.
    async function reloadTypes() { await loadTypes(); renderTypes(); changed(); }
    function nameField(value) {
        return '<label class="form-label" for="mtTypeNameInput">Namn</label>'
             + '<input id="mtTypeNameInput" class="form-control" maxlength="50" autocomplete="off" value="' + esc(value || '') + '">'
             + '<p class="text-muted small mt-2 mb-0">Namnet visas i listan, på betalsidan och i mejlen.</p>';
    }
    async function saveType(key, name, active) {
        var d = await form('SaveMemberType', { clubId: O.clubId, year: year(), key: key, name: name, isActive: active ? '1' : '0' });
        if (!d.success) return d.message || 'Medlemstypen kunde inte sparas.';
        await reloadTypes();
        return null;
    }

    // ── Sätt typ på alla som saknar ─────────────────────────────────────────────────────
    async function openSetUntyped(o) {
        O = o;
        await loadTypes();
        var opts = types.filter(function (t) { return t.isActive && !/^familj$/i.test(t.key); });
        openSub({ title: 'Sätt medlemstyp på alla som saknar', ok: 'Sätt typ',
            body: '<p class="mb-2">' + untyped + (untyped === 1 ? ' medlem saknar' : ' medlemmar saknar') + ' medlemstyp. De får den typ du väljer; du kan ändra enskilda medlemmar efteråt.</p>'
                + '<label class="form-label" for="mtUntypedSel">Medlemstyp</label><select id="mtUntypedSel" class="form-select">'
                + opts.map(function (t) { return '<option value="' + esc(t.key) + '"' + (/^senior$/i.test(t.key) ? ' selected' : '') + '>' + esc(t.name) + '</option>'; }).join('')
                + '</select>',
            onOk: async function () {
                var to = $('mtUntypedSel').value;
                var d = await form('SetMembersType', { clubId: O.clubId, year: year(), toKey: to, fromUntyped: '1' });
                if (!d.success) return d.message || 'Medlemstypen kunde inte sättas.';
                var n = d.data && d.data.changed;
                changed();
                var text = (n === 1 ? '1 medlem fick' : n + ' medlemmar fick') + ' medlemstypen ' + typeName(to) + '.';
                if (O.onMessage) O.onMessage(text); else toast(text);
                return null;
            } });
    }

    // ── Åldersförslag ───────────────────────────────────────────────────────────────────
    async function fetchAge() {
        try {
            var d = await (await fetch(F + 'GetAgeProposals?clubId=' + O.clubId + '&year=' + year(), { cache: 'no-store' })).json();
            return d && d.success ? d : null;
        } catch (e) { return null; }
    }
    function ageProposalCount(d) { return (d.rows || []).filter(function (r) { return (r.kind === 'Change' || r.kind === 'Untyped') && r.toKey; }).length; }
    async function summary(o) {
        O = o;
        var out = { untyped: 0, ageEnabled: false, ageProposals: 0 };
        if (!(await loadTypes())) return out;
        out.untyped = untyped;
        if (!ageRules.enabled) return out;
        var d = await fetchAge();
        if (d && d.enabled) { out.ageEnabled = true; out.ageProposals = ageProposalCount(d); }
        return out;
    }
    async function openAge(o) {
        O = o; AP = 'mt';
        await loadTypes();
        ageResult = null; ageAll = {};
        $('mtAgeTitle').textContent = 'Åldersförslag ' + year();
        $('mtAgeBody').innerHTML = '<p class="text-muted mb-0">Räknar…</p>';
        show($('mtAgeApply'), !O.readOnly);
        $('mtAgeApply').disabled = true; $('mtAgeApply').textContent = 'Godkänn valda';
        showModal('mtAgeModal');
        await reloadAge();
    }
    async function reloadAge() { ageCur = await fetchAge(); renderAge(); }

    function feeDelta(r) {
        if (r.noCategory) return '<span class="text-danger">' + (O.canSeeFees === false
            ? 'Klubben har ingen avgift för ' + esc(r.toName) + ' ' + year() + ' — kassören behöver lägga till den först.'
            : 'Klubben har ingen avgift för ' + esc(r.toName) + ' ' + year() + ' — lägg till den först.') + '</span>';
        if (O.canSeeFees === false || r.feeBefore == null || r.feeAfter == null) return '';
        var d = r.feeAfter - r.feeBefore;
        if (Math.abs(d) < 0.005) return 'Avgiften blir oförändrad (' + kr(r.feeAfter) + ')';
        return (d > 0 ? '↑ +' : '↓ −') + kr(Math.abs(d)) + ' (' + kr(r.feeBefore) + ' → ' + kr(r.feeAfter) + ')';
    }
    function ageMenu(act, label, id) {
        return '<div class="dropdown"><button type="button" class="btn btn-sm btn-outline-secondary dropdown-toggle" data-bs-toggle="dropdown" '
             + 'data-bs-popper-config=\'{"strategy":"fixed"}\'>Åtgärder</button><ul class="dropdown-menu dropdown-menu-end"><li><button type="button" class="dropdown-item" '
             + 'data-mt-age-act="' + act + '" data-mt-age-id="' + id + '">' + label + '</button></li></ul></div>';
    }
    function section(kind, title, hint, rows, build) {
        if (!rows.length) return '';
        var shown = ageAll[kind] ? rows : rows.slice(0, CAP);
        return '<h6 class="mt-3 mb-1">' + title + ' <span class="text-muted fw-normal">(' + rows.length + ')</span></h6>'
            + (hint ? '<p class="small text-muted mb-2">' + hint + '</p>' : '')
            + '<div class="table-responsive"><table class="table table-sm align-middle mb-1"><tbody>' + shown.map(build).join('') + '</tbody></table></div>'
            + (rows.length > shown.length ? '<button type="button" class="btn btn-link btn-sm p-0" data-mt-age-all="' + kind + '">Visa alla ' + rows.length + '</button>' : '');
    }
    function resultHtml() {
        var r = ageResult;
        if (r.error) return '<div class="alert alert-danger">' + esc(r.error) + '</div>';
        var h = '';
        if (r.undone != null) h += '<div class="alert alert-secondary">Ångrade ' + r.undone + (r.undone === 1 ? ' byte.' : ' byten.') + '</div>';
        else {
            h += '<div class="alert alert-success"><div>' + r.applied + (r.applied === 1 ? ' medlem fick ny medlemstyp.' : ' medlemmar fick ny medlemstyp.')
               + (r.cleared ? ' ' + r.cleared + (r.cleared === 1 ? ' oskickad avgift räknas om' : ' oskickade avgifter räknas om') + ' vid utskicket.' : '') + '</div>'
               + '<div class="small mt-1">Redan skickade eller betalda avgifter behåller sin typ och sitt belopp — bytet gäller först nästa avgift.</div>'
               + (r.ids.length && !O.readOnly ? '<button type="button" class="btn btn-sm btn-outline-secondary mt-2" data-mt-age-undo="1">Ångra</button>' : '') + '</div>';
        }
        if ((r.skipped || []).length)
            h += '<div class="alert alert-warning small"><strong>Hoppades över:</strong><ul class="mb-0">' + r.skipped.map(function (s) {
                return '<li>' + esc(ageNames[s.memberId] || ('Medlem ' + s.memberId)) + ' — ' + esc(s.reason) + '</li>'; }).join('') + '</ul></div>';
        return h;
    }
    function renderAge() {
        var h = ageResult ? resultHtml() : '';
        var READONLY = !!O.readOnly;
        if (!ageCur) h += '<div class="alert alert-danger mb-0">Förslagslistan kunde inte räknas fram. Försök igen om en stund.</div>';
        else if (!ageCur.enabled) {
            h += '<p class="mb-0">Ingen åldersgräns är satt, så det finns inga förslag. '
               + (READONLY ? '' : '<button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-mt-open-types="1">Ange åldersgränser</button>') + '</p>';
        } else {
            var rows = ageCur.rows || [];
            rows.forEach(function (r) { ageNames[r.memberId] = r.name; });
            var can = !READONLY && ageCur.canApply;
            h += '<p class="small text-muted">Åldern räknas som avgiftsåret ' + ageCur.year + ' minus födelseåret. Inget byts förrän ' + (READONLY ? 'kassören godkänner' : 'du godkänner')
               + ' — och typen ändras aldrig av sig själv.'
               + (ageCur.canApply ? '' : ' Avgiftsår före innevarande år går att läsa men inte godkänna.') + '</p>';
            var by = function (k) { return rows.filter(function (r) { return r.kind === k; }); };
            var change = by('Change'), untypedRows = by('Untyped'), cannot = by('CannotPlace'), house = by('Household'), kept = by('Kept');
            var rowCheck = function (r, checked) {
                var ok = can && !r.noCategory && r.toKey;
                return '<td style="width:2rem">' + (can ? '<input class="form-check-input" type="checkbox" data-mt-age-sel="' + r.memberId + ':' + esc(r.fromKey) + '"'
                    + (checked && ok ? ' checked' : '') + (ok ? '' : ' disabled') + ' aria-label="Välj ' + esc(r.name) + '">' : '') + '</td>';
            };
            var line = function (r) {
                return '<td>' + esc(r.name) + '<div class="small text-muted">' + esc(r.fromName || 'ingen typ') + ' → <strong>' + esc(r.toName) + '</strong>'
                     + (r.age != null ? ' · ' + r.age + ' år' : '') + '</div><div class="small text-muted">' + feeDelta(r) + '</div></td>';
            };
            h += section('Change', 'Byter medlemstyp', 'Förbockade rader byts när du godkänner.', change, function (r) {
                return '<tr>' + rowCheck(r, true) + line(r) + '<td class="text-end">' + (can ? ageMenu('keep', 'Behåll typen', r.memberId) : '') + '</td></tr>'; });
            h += section('Untyped', 'Saknar medlemstyp — förslag efter ålder', 'En gissning efter ålder, därför inte förbockad.', untypedRows, function (r) {
                return '<tr>' + rowCheck(r, false) + line(r) + '<td></td></tr>'; });
            h += section('CannotPlace', 'Kan inte placeras', 'Inget byts för dessa. ' + (O.clubUrl ? '<a href="' + esc(O.clubUrl) + '#clubAdmin/members">Komplettera personnummer under Medlemmar</a> eller importera födelsedatum.' : 'Komplettera personnummer under Medlemmar.'),
                cannot, function (r) { return '<tr><td>' + esc(r.name) + '<div class="small text-muted">' + esc(r.reason || '') + '</div></td></tr>'; });
            h += section('Household', 'Ingår i ett familjemedlemskap', 'Rörs aldrig här — bara information.', house, function (r) {
                return '<tr><td>' + esc(r.name) + (r.reason ? '<div class="small text-muted">' + esc(r.reason) + '</div>' : '') + '</td></tr>'; });
            if (kept.length) {
                h += '<details class="mt-3 text-muted"><summary class="small">Behåller sin typ (' + kept.length + ')</summary>'
                   + '<div class="table-responsive"><table class="table table-sm align-middle mb-0"><tbody>' + kept.map(function (r) {
                        return '<tr><td>' + esc(r.name) + '<div class="small">Behåller ' + esc(r.fromName || 'ingen typ') + '</div></td><td class="text-end">'
                             + (can ? ageMenu('unlock', 'Lås upp', r.memberId) : '') + '</td></tr>'; }).join('') + '</tbody></table></div></details>';
            }
            if (!change.length && !untypedRows.length && !cannot.length && !house.length && !kept.length)
                h += '<p class="mb-0">Alla medlemmar har redan rätt medlemstyp för sin ålder.</p>';
        }
        $(AP + 'AgeBody').innerHTML = h;
        updateApply();
    }
    function updateApply() {
        var b = $(AP + 'AgeApply'); if (!b) return;
        // Inbäddat: knappen visas bara när listan har något att godkänna (annars står en avstängd knapp under "inga förslag").
        if (AP === 'mtE' && b.parentElement && !(O && O.readOnly)) b.parentElement.classList.toggle('d-none', !$(AP + 'AgeBody').querySelector('[data-mt-age-sel]'));
        var n = $(AP + 'AgeBody').querySelectorAll('[data-mt-age-sel]:checked').length;
        b.disabled = n === 0;
        b.textContent = n ? 'Godkänn valda (' + n + ')' : 'Godkänn valda';
    }

    // Kopplar typtabellens knappar och rader (dialogen eller den inbäddade ytan). p = id-prefix.
    function wireTypesUi(p, embedded) {
        var fromId = embedded ? null : 'mtTypesModal';
        function use() { if (embedded && EMBED_O) O = EMBED_O; }
        $(p + 'NewTypeBtn').addEventListener('click', function () { use();
            openSub({ title: 'Ny medlemstyp', ok: 'Lägg till', from: fromId, body: nameField(''),
                      onOk: function () { return saveType('', $('mtTypeNameInput').value, true); } });
        });
        $(p + 'TypeRows').addEventListener('input', function (ev) { use();
            var inp = ev.target.closest('[data-mt-from]'); if (!inp) return;
            ageEdit[inp.getAttribute('data-mt-from')] = inp.value;
            renderAgeSummary();
        });
        $(p + 'TypeRows').addEventListener('click', function (ev) { use();
            var b = ev.target.closest('[data-mt-tact]'); if (!b) return;
            var key = b.getAttribute('data-mt-tkey'), act = b.getAttribute('data-mt-tact'), t = typeOf(key);
            if (!t) return;
            typesNote(false, '');
            if (act === 'rename') {
                openSub({ title: 'Byt namn på ' + t.name, ok: 'Spara', from: fromId, body: nameField(t.name),
                          onOk: function () { return saveType(t.key, $('mtTypeNameInput').value, t.isActive); } });
            } else if (act === 'unhide') {
                saveType(t.key, t.name, true).then(function (err) { if (err) typesNote(false, err); });
            } else if (act === 'hide') {
                var others = types.filter(function (x) { return x.isActive && x.key !== t.key && !/^familj$/i.test(x.key); });
                var move = t.members > 0
                    ? '<p class="mb-2">' + t.members + (t.members === 1 ? ' medlem har' : ' medlemmar har') + ' typen ' + esc(t.name) + '. Flytta dem till en annan typ, annars står de kvar på en dold typ.</p>'
                      + '<label class="form-label" for="mtMoveTo">Flytta medlemmarna till</label><select id="mtMoveTo" class="form-select">'
                      + '<option value="">Flytta inte</option>' + others.map(function (x) { return '<option value="' + esc(x.key) + '">' + esc(x.name) + '</option>'; }).join('') + '</select>'
                    : '<p class="mb-0">Typen ' + esc(t.name) + ' har inga medlemmar. Den kan visas igen senare.</p>';
                openSub({ title: 'Dölj ' + t.name, ok: 'Dölj', from: fromId, body: move,
                    onOk: async function () {
                        var to = $('mtMoveTo') ? $('mtMoveTo').value : '';
                        if (to) {
                            var m = await form('SetMembersType', { clubId: O.clubId, year: year(), toKey: to, fromKey: t.key });
                            if (!m.success) return m.message || 'Medlemmarna kunde inte flyttas.';
                        }
                        return await saveType(t.key, t.name, false);
                    } });
            } else if (act === 'delete') {
                openSub({ title: 'Ta bort ' + t.name, ok: 'Ta bort', danger: true, from: fromId,
                    body: '<p class="mb-0">Ta bort medlemstypen ' + esc(t.name) + '? Det går bara när ingen medlem, avgift eller åldersgräns använder den.</p>',
                    onOk: async function () {
                        var d = await form('DeleteMemberType', { clubId: O.clubId, key: t.key });
                        if (!d.success) return d.message || 'Typen kunde inte tas bort.';
                        await reloadTypes();
                        return null;
                    } });
            }
        });
        $(p + 'AgeFill').addEventListener('click', function () { use();
            // Förslag, inte sanning: SHB:s åldrar gäller tävlingsklass. Fylls bara i i fälten — sparas med knappen.
            var found = [['Junior', 0], ['Senior', 21], ['Veteran', 60]].filter(function (c) { var t = typeOf(c[0]); return t && t.isActive; });
            if (!found.length) { typesNote(false, 'Klubben har varken Junior, Senior eller Veteran bland sina aktiva typer.'); return; }
            types.forEach(function (t) { if (!neverAge(t.key)) ageEdit[t.key] = ''; });
            found.forEach(function (c) { ageEdit[typeOf(c[0]).key] = String(c[1]); });
            ageEdit[typeOf(found[0][0]).key] = '0'; // den yngsta gruppen börjar alltid på 0
            typesNote(false, '');
            renderTypes();
        });
        $(p + 'AgeSave').addEventListener('click', async function () { use();
            var steps = editedSteps(), btn = this;
            btn.disabled = true;
            var d = await form('SaveAgeLimits', { clubId: O.clubId, typeKeys: steps.map(function (s) { return s.key; }),
                                                  fromAges: steps.map(function (s) { return String(s.from); }) });
            if (!d.success) { typesNote(false, d.message || 'Åldersgränserna kunde inte sparas.'); btn.disabled = false; return; }
            await loadTypes();
            ageEdit = {}; types.forEach(function (t) { ageEdit[t.key] = savedAge(t); });
            renderTypes();
            changed();
            typesNote(true, steps.length ? 'Åldersgränserna är sparade.' : 'Åldersgränserna är borttagna.');
        });

    }

    // ── Kopplingar (en gång, när dialogerna finns på sidan) ────────────────────────────
    function wire() {
        if (!$('mtTypesModal') || $('mtTypesModal').getAttribute('data-wired')) return;
        $('mtTypesModal').setAttribute('data-wired', '1');

        $('mtSubOk').addEventListener('click', async function () {
            if (!subCfg) return;
            var btn = this; btn.disabled = true;
            var err = null;
            try { err = await subCfg.onOk(); } catch (e) { err = 'Något gick fel. Försök igen.'; }
            btn.disabled = false;
            if (err) { $('mtSubError').textContent = err; show($('mtSubError'), true); return; }
            bootstrap.Modal.getOrCreateInstance($('mtSubModal')).hide();
        });
        $('mtSubBody').addEventListener('keydown', function (ev) { if (ev.key === 'Enter' && ev.target.tagName === 'INPUT') { ev.preventDefault(); $('mtSubOk').click(); } });
        $('mtSubModal').addEventListener('shown.bs.modal', function () { var i = $('mtSubBody').querySelector('input,select'); if (i) i.focus(); });
        $('mtSubModal').addEventListener('hidden.bs.modal', function () {
            var c = subCfg; subCfg = null;
            if (!c) return;
            if (c.after) c.after(); else if (c.back) bootstrap.Modal.getOrCreateInstance($(c.back)).show();
        });

        wireTypesUi('mt', false);
        wireAgeUi($('mtAgeModal'), 'mt');
    }

    // Kopplar åldersförslagens klick (Visa alla, Behåll typen, Lås upp, Ångra) och Godkänn. Dialogen: root = mtAgeModal, p = "mt";
    // den inbäddade ytan: root = hennes eget element, p = "mtE". Hanterarna sätter AP (och O) först, så att renderingen hamnar rätt.
    function wireAgeUi(root, p) {
        var embedded = p === 'mtE';
        function use() { AP = p; if (embedded && EMBED_O) O = EMBED_O; }
        root.addEventListener('change', function (ev) { use(); if (ev.target.closest('[data-mt-age-sel]')) updateApply(); });
        root.addEventListener('click', async function (ev) {
            use();
            var t;
            if ((t = ev.target.closest('[data-mt-age-all]'))) { ageAll[t.getAttribute('data-mt-age-all')] = true; renderAge(); return; }
            if (ev.target.closest('[data-mt-open-types]')) {
                // Inbäddat ligger typtabellen på samma sida: rulla dit i stället för att öppna en dialog.
                if (embedded) { var tr = $('mtETypeRows'); if (tr) tr.scrollIntoView({ behavior: 'smooth', block: 'center' }); }
                else openTypes(O, 'mtAgeModal');
                return;
            }
            if ((t = ev.target.closest('[data-mt-age-act]'))) {
                var keep = t.getAttribute('data-mt-age-act') === 'keep';
                var d = await form('SetAgeRuleOptOut', { clubId: O.clubId, year: year(), memberId: t.getAttribute('data-mt-age-id'), keep: keep ? '1' : '0' });
                ageResult = d.success ? null : { error: d.message || 'Det gick inte att spara.' };
                use(); await reloadAge(); changed();
                return;
            }
            if (ev.target.closest('[data-mt-age-undo]')) {
                var ids = (ageResult && ageResult.ids) || [];
                var u = await form('UndoAgeProposals', { clubId: O.clubId, year: year(), memberIds: ids.map(String) });
                ageResult = u.success ? { undone: u.undone, skipped: u.skipped } : { error: u.message || 'Det gick inte att ångra.' };
                use(); await reloadAge(); changed();
                return;
            }
        });
        $(p + 'AgeApply').addEventListener('click', async function () {
            use();
            var items = Array.prototype.map.call($(p + 'AgeBody').querySelectorAll('[data-mt-age-sel]:checked'), function (b) { return b.getAttribute('data-mt-age-sel'); });
            if (!items.length) return;
            this.disabled = true;
            var d = await form('ApplyAgeProposals', { clubId: O.clubId, year: year(), items: items });
            if (!d.success) ageResult = { error: d.message || 'Typbytena kunde inte godkännas.' };
            else {
                var skipped = d.skipped || [], skippedIds = skipped.map(function (s) { return s.memberId; });
                var ids = items.map(function (i) { return parseInt(i.split(':')[0], 10); }).filter(function (id) { return skippedIds.indexOf(id) < 0; });
                ageResult = { applied: d.applied, skipped: skipped, ids: ids, cleared: d.chargesCleared };
            }
            use(); await reloadAge(); changed();
        });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wire); else wire();

    // ── Inbäddad typtabell (Ekonomi → Medlemstyper och förbund) ─────────────────────────
    // Ritar typtabellen, åldersgränserna och "Ny medlemstyp" direkt i root, med id-prefixet "mtE" så att
    // dialogens id:n (prefix "mt") aldrig krockar. Namn- och bekräftelsefrågorna går fortfarande genom den delade
    // mtSubModal (utan "tillbaka till föregående dialog"). Anropa en gång; ett andra anrop läser bara om.
    async function mountTypes(root, o) {
        EMBED_O = o; O = o;
        if (!root.getAttribute('data-mt-mounted')) {
            root.setAttribute('data-mt-mounted', '1');
            root.innerHTML =
                '<div class="table-responsive"><table class="table table-sm align-middle mb-2" style="font-size:.9rem">'
                + '<thead><tr><th>Medlemstyp</th><th class="text-end d-none d-sm-table-cell">Medlemmar</th><th>Från ålder</th><th><span class="visually-hidden">Åtgärder</span></th></tr></thead>'
                + '<tbody id="mtETypeRows"></tbody></table></div>'
                + '<div class="mb-3' + (o.readOnly ? ' d-none' : '') + '"><button type="button" class="btn btn-sm btn-outline-secondary" id="mtENewTypeBtn"><i class="bi bi-plus-lg me-1"></i>Ny medlemstyp…</button></div>'
                + '<div class="fw-semibold small" id="mtEAgeSummary"></div>'
                + '<div class="small text-muted d-none mt-1" id="mtEAgeSuggest">'
                + '<span>Förslag efter SHB: Junior till 20 år, Senior från 21, Veteran från 60. Klubben bestämmer själv.</span>'
                + '<button type="button" class="btn btn-sm btn-outline-secondary ms-1" id="mtEAgeFill">Fyll i förslaget</button></div>'
                + '<div class="d-flex justify-content-end mt-3' + (o.readOnly ? ' d-none' : '') + '"><button type="button" class="btn btn-primary" id="mtEAgeSave" disabled>Spara åldersgränser</button></div>'
                + '<div class="alert alert-danger d-none mt-3 mb-0" id="mtETypesError"></div>';
            wireTypesUi('mtE', true);
        }
        await loadTypes();
        ageEdit = {};
        types.forEach(function (t) { ageEdit[t.key] = savedAge(t); });
        typesNote(false, '');
        renderTypes();
        return { newType: function () { var b = $('mtENewTypeBtn'); if (b) b.click(); }, reload: function () { EMBED_O = o; O = o; return reloadTypes(); } };
    }

    // ── Inbäddade åldersförslag (Ekonomi → Medlemstyper och förbund) ────────────────────
    // Samma lista och samma knappar som dialogen, ritade direkt i root med id-prefixet "mtE". Ett andra anrop läser bara om.
    async function mountAge(root, o) {
        EMBED_O = o; O = o; AP = 'mtE';
        if (!root.getAttribute('data-mt-age-mounted')) {
            root.setAttribute('data-mt-age-mounted', '1');
            root.innerHTML = '<div id="mtEAgeBody"><p class="text-muted mb-0">Räknar…</p></div>'
                + '<div class="d-flex justify-content-end mt-3' + (o.readOnly ? ' d-none' : '') + '"><button type="button" class="btn btn-primary" id="mtEAgeApply" disabled>Godkänn valda</button></div>';
            wireAgeUi(root, 'mtE');
        }
        await loadTypes();
        ageResult = null; ageAll = {};
        await reload();
        async function reload() { EMBED_O = o; O = o; AP = 'mtE'; await loadTypes(); ageCur = await fetchAge(); renderAge(); }
        return { reload: function () { return reload(); } };
    }

    window.hpskMemberTypes = {
        mountAge: function (root, o) { wire(); return mountAge(root, o); },
        mountTypes: function (root, o) { wire(); return mountTypes(root, o); },
        openTypes: function (o, from) { wire(); return openTypes(o, from); },
        openAge: function (o) { wire(); return openAge(o); },
        openSetUntyped: function (o) { wire(); return openSetUntyped(o); },
        summary: summary
    };
})();
