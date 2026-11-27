// Avgiftsregler och Medlemstyper och förbund (Ekonomi, rälsposterna efter Medlemsavgifter). Se Partials/ClubFeeRules.cshtml.
// Samma endpoints som listan (MembershipFeeAdminController); inga egna regler: exempelräkningen och "träffar N av M" räknas av servern.
(function () {
    if (window.__clubFeeRulesInit) return;
    window.__clubFeeRulesInit = true;

    var F = '/umbraco/surface/MembershipFeeAdmin/';
    function $(id) { return document.getElementById(id); }
    function esc(s) {
        return (s == null ? '' : String(s)).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function kr(n) { return (Math.round((n || 0) * 100) / 100).toLocaleString('sv-SE', { maximumFractionDigits: 2 }) + ' kr'; }
    function signed(n) { return (n > 0 ? '+' : n < 0 ? '−' : '') + kr(Math.abs(n)); }
    function show(el, on) { if (el) el.classList.toggle('d-none', !on); }
    function plural(n, one, many) { return n + ' ' + (n === 1 ? one : many); }
    async function token() {
        var el = document.querySelector('input[name="__RequestVerificationToken"]');
        if (el && el.value) return el.value;
        return (await (await fetch('/umbraco/surface/Club/GetFormToken', { cache: 'no-store' })).json()).token;
    }
    async function post(action, fields) {
        var fd = new FormData();
        Object.keys(fields).forEach(function (k) {
            var v = fields[k];
            if (Array.isArray(v)) v.forEach(function (x) { fd.append(k, x); }); else fd.append(k, v);
        });
        fd.append('__RequestVerificationToken', await token());
        var r = await fetch(F + action, { method: 'POST', body: fd });
        try { return await r.json(); } catch (e) { return { success: false, message: 'Något gick fel (' + r.status + ').' }; }
    }
    async function get(action, query) {
        var r = await fetch(F + action + '?' + query, { cache: 'no-store' });
        try { return await r.json(); } catch (e) { return { success: false, message: 'Något gick fel (' + r.status + ').' }; }
    }

    // ── Gemensamt: typer och förbund ───────────────────────────────────────────────────────
    var NEVER_SELECT = { 'familj': 1, 'hedersmedlem': 1, 'ständig': 1, 'stödjande': 1 };
    function sameKey(a, b) { return (a || '').toLowerCase() === (b || '').toLowerCase(); }

    function menu(attr, id, items) {
        return '<div class="dropdown"><button type="button" class="btn btn-sm btn-outline-secondary dropdown-toggle" data-bs-toggle="dropdown" '
            + 'data-bs-popper-config=\'{"strategy":"fixed"}\'>Åtgärder</button><ul class="dropdown-menu dropdown-menu-end">'
            + items.map(function (i) {
                return '<li><button type="button" class="dropdown-item' + (i[2] ? ' text-danger' : '') + '" data-' + attr + '="' + i[0] + '" data-fid="' + id + '">' + i[1] + '</button></li>';
            }).join('') + '</ul></div>';
    }

    // Följderna av en ändring ("7 oskickade avgifter räknas om. 3 skickade ändras inte …"). Servern bär dem i varje skrivsvar.
    function consequenceText(c) {
        if (!c) return '';
        var parts = [];
        parts.push(c.unsentCleared > 0 ? plural(c.unsentCleared, 'oskickad avgift räknas', 'oskickade avgifter räknas') + ' om för ' + c.year + '.'
                                       : 'Inga oskickade avgifter för ' + c.year + ' påverkas.');
        if (c.sentDiverging) parts.push(plural(c.sentDiverging, 'skickad avgift ändras', 'skickade avgifter ändras') + ' inte och avviker nu'
            + (c.divergingSum != null ? ' med ' + signed(c.divergingSum) : '') + '.');
        else if (c.paidDiverging) parts.push(plural(c.paidDiverging, 'betald avgift ändras', 'betalda avgifter ändras') + ' inte.');
        return parts.join(' ');
    }
    function refreshList() { if (window.hpskRefreshClubFees) window.hpskRefreshClubFees(); }

    // ═════════════════════════ AVGIFTSREGLER ═════════════════════════
    var fr = $('frRoot');
    if (fr) (function () {
        var CLUB_ID = parseInt(fr.getAttribute('data-club-id'), 10) || 0;
        var READONLY = fr.getAttribute('data-readonly') === '1';
        var data = null;                       // GetOverview: categories
        var fs = { parts: [], options: [], federations: [], memberTotal: null };
        var types = [];
        var loadSeq = 0;
        var modalCfg = null;

        function year() { return parseInt($('frYear').value, 10); }
        function typeOf(key) { return types.find(function (t) { return sameKey(t.key, key); }); }
        function typeName(key) { if (!key) return ''; var t = typeOf(key); return t ? t.name : key; }
        function optionOf(id) { return (fs.options || []).find(function (o) { return o.id === id; }); }
        function categoryOf(key) { return ((data && data.categories) || []).find(function (c) { return sameKey(c.membershipType, key); }); }

        function note(ok, text, kind) {
            $('frOk').className = 'alert ' + (kind === 'warn' ? 'alert-warning' : 'alert-success');
            show($('frOk'), ok && !!text); show($('frError'), !ok && !!text);
            $(ok ? 'frOk' : 'frError').textContent = text || '';
        }
        // Svar på en skrivning: fel visas i modalen (anroparen), annars följderna på sidan.
        function afterWrite(d) { note(true, consequenceText(d && d.consequences)); refreshList(); }

        // ── Läsning ──
        async function load() {
            var mine = ++loadSeq, y = year();
            var res = await Promise.all([
                get('GetOverview', 'clubId=' + CLUB_ID + '&year=' + y),
                get('GetMemberTypes', 'clubId=' + CLUB_ID).catch(function () { return null; }),
                get('GetFeeSettings', 'clubId=' + CLUB_ID + '&year=' + y).catch(function () { return null; })
            ]);
            if (mine !== loadSeq) return;       // ett svar efter ett årsbyte gäller fel år
            var ov = res[0], t = res[1], s = res[2];
            if (!ov.success) { note(false, ov.message || 'Avgiftsreglerna gick inte att läsa.'); return; }
            data = ov;
            if (t && t.success) types = t.types || [];
            fs = (s && s.success) ? s : { parts: [], options: [], federations: [], memberTotal: null, failed: true };
            if (fs.failed) note(false, 'Tilläggen kunde inte läsas just nu. Ladda om sidan.');
            render();
            runExample();
        }
        window.hpskLoadClubFeeRules = load;

        // ── Villkoret som en mening ──
        function whoText(p) {
            var base = {
                Always: 'alla medlemmar',
                PrimaryClub: 'medlemmar som har klubben som huvudklubb',
                NotPrimaryClub: 'medlemmar som har en annan klubb som huvudklubb'
            }[p.condition];
            if (p.condition === 'Option') { var o = optionOf(p.optionId); base = 'medlemmar som är med i förbundet ' + (o ? o.name : '?'); }
            var ts = (p.typeKeys || []).map(typeName);
            return (base || 'alla medlemmar') + (ts.length ? ' av typen ' + ts.join(', ') : '');
        }
        function sentence(p) {
            return (p.amount < 0 ? 'Drar av ' : 'Lägger på ') + kr(Math.abs(p.amount)) + ' för ' + whoText(p) + '.';
        }
        // Hedersmedlem/ständig med grundavgift 0 kr: en VARNING, ingen spärr (Stefan 2026-11-26).
        function zeroTypeWarning(p) {
            if ((p.typeKeys || []).length || p.amount <= 0) return '';
            var zero = ((data && data.categories) || []).filter(function (c) { return c.amount === 0; }).map(function (c) { return typeName(c.membershipType); });
            if (!zero.length) return '';
            return '<div class="small text-warning-emphasis">Träffar också ' + esc(zero.join(', ')) + ' (grundavgift 0 kr). Hen kanske ändå ska betala förbundsavgift; välj i så fall bara de medlemstyper det gäller.</div>';
        }

        function render() {
            var y = year(), cats = (data && data.categories) || [];
            $('frYearBadge').textContent = 'Gäller bara ' + y;
            $('frBaseTitle').textContent = 'Grundavgift ' + y;
            $('frPartsTitle').textContent = 'Tillägg och avdrag ' + y;

            var active = types.filter(function (t) { return t.isActive; });
            var withFee = active.filter(function (t) { return !!categoryOf(t.key); }).length;
            $('frBaseStatus').innerHTML = active.length ? (withFee === active.length ? '<strong class="text-success">✓</strong> ' : '') + withFee + ' av ' + active.length + ' typer har avgift' : '';

            // Typer med avgift först (ordning som typlistan), därefter typer som saknar avgift med en tydlig knapp.
            var rows = '';
            var listed = {};
            active.forEach(function (t) {
                var c = categoryOf(t.key); listed[(t.key || '').toLowerCase()] = 1;
                if (c) {
                    rows += '<tr><td>' + esc(t.name) + '</td><td class="text-end text-nowrap">' + kr(c.amount) + '</td>'
                        + '<td class="text-center" data-fr-cell="sel">' + (READONLY ? (c.memberSelectable ? 'Ja' : 'Nej')
                            : '<div class="form-check form-switch d-inline-block m-0"><input class="form-check-input" type="checkbox" role="switch" data-fr-sel="' + c.id + '"'
                              + (c.memberSelectable ? ' checked' : '') + ' aria-label="Medlemmen får välja ' + esc(t.name) + ' själv"></div>') + '</td>'
                        + '<td class="text-end">' + (READONLY ? '' : menu('fr-base', c.id, [['edit', 'Ändra belopp…'], ['delete', 'Ta bort…', true]])) + '</td></tr>';
                } else {
                    rows += '<tr><td>' + esc(t.name) + '</td><td class="text-end"><span class="badge bg-danger-subtle text-danger-emphasis border">Ingen avgift</span></td><td></td>'
                        + '<td class="text-end">' + (READONLY ? '' : '<button type="button" class="btn btn-sm btn-outline-primary" data-fr-base="add" data-fid="' + esc(t.key) + '">Ange avgift…</button>') + '</td></tr>';
                }
            });
            // En avgift för en typ som inte längre är aktiv står kvar så att den inte försvinner tyst.
            cats.filter(function (c) { return !listed[(c.membershipType || '').toLowerCase()]; }).forEach(function (c) {
                rows += '<tr><td>' + esc(typeName(c.membershipType)) + ' <span class="badge bg-light text-dark border">Dold typ</span></td><td class="text-end text-nowrap">' + kr(c.amount) + '</td><td></td>'
                    + '<td class="text-end">' + (READONLY ? '' : menu('fr-base', c.id, [['edit', 'Ändra belopp…'], ['delete', 'Ta bort…', true]])) + '</td></tr>';
            });
            $('frBaseRows').innerHTML = rows || '<tr><td colspan="4" class="text-muted small">Inga medlemstyper ännu. Lägg till dem under Medlemstyper och förbund.</td></tr>';

            var ps = fs.parts || [], total = fs.memberTotal;
            $('frPartsStatus').textContent = ps.length ? plural(ps.length, 'del', 'delar') : '';
            $('frParts').innerHTML = ps.map(function (p) {
                var hit = (p.appliesTo != null && total != null) ? '<div class="small text-primary">Träffar ' + p.appliesTo + ' av ' + plural(total, 'medlem', 'medlemmar') + '</div>' : '';
                return '<div class="d-flex justify-content-between align-items-start gap-2 py-2 border-bottom" data-fr-part="' + p.id + '">'
                    + '<div><div class="fw-semibold">' + esc(p.label) + '</div><div class="small text-muted">' + esc(sentence(p)) + '</div>' + hit + zeroTypeWarning(p) + '</div>'
                    + '<div class="text-end text-nowrap"><span class="fw-bold d-block mb-1 ' + (p.amount < 0 ? 'text-danger' : 'text-success') + '">' + signed(p.amount) + '</span>'
                    + (READONLY ? '' : menu('fr-part', p.id, [['edit', 'Ändra…'], ['delete', 'Ta bort…', true]])) + '</div></div>';
            }).join('') || '<div class="text-muted small py-2">Inga tillägg eller avdrag ännu. Avgiften är grundavgiften.</div>';

            renderExampleInputs();
        }

        // ── Exempelräkningen ──
        function renderExampleInputs() {
            var sel = $('frExType'), keep = sel.value;
            sel.innerHTML = types.filter(function (t) { return t.isActive; }).map(function (t) { return '<option value="' + esc(t.key) + '">' + esc(t.name) + '</option>'; }).join('');
            if (keep) sel.value = keep;
            var checked = {};
            Array.prototype.forEach.call($('frExOpts').querySelectorAll('input:checked'), function (b) { checked[b.value] = 1; });
            var opts = (fs.options || []).filter(function (o) { return o.isActive; });
            $('frExOpts').innerHTML = opts.map(function (o) {
                return '<div class="form-check form-check-inline"><input class="form-check-input" type="checkbox" value="' + o.id + '" id="frExO' + o.id + '"' + (checked[o.id] ? ' checked' : '') + '>'
                    + '<label class="form-check-label" for="frExO' + o.id + '">' + esc(o.name) + '</label></div>';
            }).join('') || '<span class="text-muted">Inga förbund än.</span>';

            // Medlemsväljaren: klubbens medlemmar ur samma översikt som listan (namn + typ). Valet behålls över omladdningar.
            var msel = $('frExMember'), keepM = msel.value;
            msel.innerHTML = '<option value="">Välj en medlem…</option>' + ((data && data.rows) || []).map(function (r) {
                return '<option value="' + r.memberId + '">' + esc(r.name) + (r.membershipType ? ' (' + esc(typeName(r.membershipType)) + ')' : '') + '</option>';
            }).join('');
            if (keepM) msel.value = keepM;
            syncExampleMode();
        }
        // En vald medlem ersätter de tre handvalen: de gråas så att ingen tror att de styr räkningen.
        function syncExampleMode() {
            var real = !!$('frExMember').value;
            $('frExType').disabled = real; $('frExHome').disabled = real;
            Array.prototype.forEach.call($('frExOpts').querySelectorAll('input'), function (b) { b.disabled = real; });
        }
        var exSeq = 0;
        async function runExample() {
            var box = $('frExResult'), skipBox = $('frExSkipped');
            show(skipBox, false);
            var memberId = $('frExMember').value;
            var type = $('frExType').value;
            if (!memberId && !type) { box.innerHTML = '<span class="text-muted small">Välj en medlemstyp för att räkna.</span>'; return; }
            var ids = Array.prototype.map.call($('frExOpts').querySelectorAll('input:checked'), function (b) { return b.value; }).join(',');
            var mine = ++exSeq;
            var q = 'clubId=' + CLUB_ID + '&year=' + year() + (memberId ? '&memberId=' + encodeURIComponent(memberId)
                : '&type=' + encodeURIComponent(type) + '&home=' + encodeURIComponent($('frExHome').value) + '&options=' + encodeURIComponent(ids));
            var d = await get('PreviewFee', q);
            if (mine !== exSeq) return;
            if (!d.success) { box.innerHTML = '<span class="text-danger small">' + esc(d.message || 'Räkningen gick inte att göra.') + '</span>'; return; }
            // Tilläggen som inte träffar den här personen, med skälet ("Gäller inte den här medlemmen: …").
            if (d.skipped && d.skipped.length) {
                skipBox.innerHTML = 'Gäller inte ' + (memberId ? esc((d.person && d.person.name) || 'den här medlemmen') : 'den här medlemmen') + ': '
                    + d.skipped.map(function (s) { return esc(s.label) + ' (' + esc(s.reason) + ')'; }).join(', ') + '.';
                show(skipBox, true);
            }
            if (d.kind !== 'Individual' && d.kind !== 'HouseholdPrimary') {
                box.innerHTML = '<span class="text-muted small">' + (d.kind === 'NoCategory' ? 'Ingen grundavgift är bestämd för ' + esc(typeName(type)) + ' ' + year() + ', så ingen avgift tas ut.' : 'Ingen avgift för den här kombinationen.') + '</span>';
                return;
            }
            box.innerHTML = d.lines.map(function (l) {
                return '<div class="d-flex justify-content-between"><span>' + esc(l.description) + '</span><span class="text-nowrap">' + (l.kind === 'fee-base' ? kr(l.amount) : signed(l.amount)) + '</span></div>';
            }).join('') + '<div class="d-flex justify-content-between fw-bold border-top mt-1 pt-1"><span>Att betala</span><span>' + kr(d.total) + '</span></div>';
        }
        $('frExMember').addEventListener('change', function () { syncExampleMode(); runExample(); });
        $('frExType').addEventListener('change', runExample);
        $('frExHome').addEventListener('change', runExample);
        $('frExOpts').addEventListener('change', runExample);

        // ── EN formulärmodal ──
        // cfg: { title, body, ok, danger, onOk() → felmeddelande eller null }
        function openModal(cfg) {
            modalCfg = cfg;
            $('frModalTitle').textContent = cfg.title;
            $('frModalBody').innerHTML = cfg.body;
            $('frModalOk').textContent = cfg.ok || 'Spara';
            $('frModalOk').className = 'btn ' + (cfg.danger ? 'btn-danger' : 'btn-primary');
            $('frModalOk').disabled = false;
            show($('frModalError'), false);
            bootstrap.Modal.getOrCreateInstance($('frModal')).show();
            if (cfg.after) cfg.after();
        }
        $('frModalOk').addEventListener('click', async function () {
            if (!modalCfg) return;
            var btn = this; btn.disabled = true;
            var err = null;
            try { err = await modalCfg.onOk(); } catch (e) { err = 'Något gick fel. Försök igen.'; }
            btn.disabled = false;
            if (err) { $('frModalError').textContent = err; show($('frModalError'), true); return; }
            bootstrap.Modal.getOrCreateInstance($('frModal')).hide();
        });
        $('frModalBody').addEventListener('keydown', function (ev) { if (ev.key === 'Enter' && ev.target.tagName === 'INPUT') { ev.preventDefault(); $('frModalOk').click(); } });
        $('frModal').addEventListener('shown.bs.modal', function () { var i = $('frModalBody').querySelector('input:not([type=checkbox]),select'); if (i) i.focus(); });

        // ── Grundavgift ──
        function baseForm(typeKey, c) {
            var avail = types.filter(function (t) { return t.isActive && (!categoryOf(t.key) || (c && sameKey(c.membershipType, t.key))); });
            var fixed = c || typeKey;
            return '<label class="form-label" for="frBType">Medlemstyp</label>'
                + (fixed ? '<input class="form-control mb-3" id="frBType" value="' + esc(typeName(c ? c.membershipType : typeKey)) + '" disabled data-key="' + esc(c ? c.membershipType : typeKey) + '">'
                    : '<select id="frBType" class="form-select mb-3">' + avail.map(function (t) { return '<option value="' + esc(t.key) + '">' + esc(t.name) + '</option>'; }).join('') + '</select>')
                + '<label class="form-label" for="frBAmount">Grundavgift ' + year() + ', kr</label>'
                + '<input id="frBAmount" type="number" min="0" step="1" class="form-control" autocomplete="off" value="' + (c ? c.amount : '') + '">'
                + '<p class="text-muted small mt-2 mb-0">Avgifter som redan skickats ändras inte. Oskickade räknas om.</p>';
        }
        function openBase(typeKey, c) {
            if (!c && !typeKey && !types.some(function (t) { return t.isActive && !categoryOf(t.key); })) { note(false, 'Alla medlemstyper har redan en grundavgift. Ändra en av dem i listan.'); return; }
            openModal({ title: c ? 'Ändra grundavgift ' + typeName(c.membershipType) : 'Ny grundavgift ' + year(), ok: c ? 'Spara' : 'Lägg till', body: baseForm(typeKey, c),
                onOk: async function () {
                    var amount = parseFloat($('frBAmount').value);
                    if (isNaN(amount) || amount < 0) return 'Fyll i en avgift.';
                    var el = $('frBType'), key = el.getAttribute('data-key') || el.value;
                    var d = await post('SaveCategory', { id: c ? c.id : 0, clubId: CLUB_ID, year: year(), membershipType: key, label: typeName(key), amount: amount,
                                                         memberSelectable: c && c.memberSelectable ? '1' : '0' });
                    if (!d.success) return d.message || 'Avgiften kunde inte sparas.';
                    afterWrite(d); await load();
                    return null;
                } });
        }
        $('frBaseRows').addEventListener('click', function (ev) {
            var add = ev.target.closest('button[data-fr-base="add"]');
            if (add) openBase(add.getAttribute('data-fid'), null);
        });
        // Menyraderna bär data-fr-base="edit|delete" + data-fid = kategorins id.
        $('frBaseRows').addEventListener('click', function (ev) {
            var b = ev.target.closest('.dropdown-item[data-fr-base]'); if (!b) return;
            var id = parseInt(b.getAttribute('data-fid'), 10), c = ((data && data.categories) || []).find(function (x) { return x.id === id; });
            if (!c) return;
            if (b.getAttribute('data-fr-base') === 'edit') { openBase(null, c); return; }
            openModal({ title: 'Ta bort grundavgift ' + typeName(c.membershipType), ok: 'Ta bort', danger: true,
                body: '<p class="mb-0">Ta bort grundavgiften ' + kr(c.amount) + ' för ' + esc(typeName(c.membershipType)) + ' ' + year() + '? Medlemmar av typen får då ingen avgift. Oskickade avgifter räknas om. Avgifter som redan skickats ändras inte.</p>',
                onOk: async function () {
                    var d = await post('DeleteCategory', { id: c.id, clubId: CLUB_ID });
                    if (!d.success) return d.message || 'Avgiften kunde inte tas bort.';
                    afterWrite(d); await load();
                    return null;
                } });
        });
        // "Medlemmen får välja typen själv" sparas direkt.
        $('frBaseRows').addEventListener('change', async function (ev) {
            var cb = ev.target.closest('[data-fr-sel]'); if (!cb) return;
            var id = parseInt(cb.getAttribute('data-fr-sel'), 10), c = ((data && data.categories) || []).find(function (x) { return x.id === id; });
            if (!c) return;
            cb.disabled = true;
            var d = await post('SaveCategory', { id: c.id, clubId: CLUB_ID, year: year(), membershipType: c.membershipType, label: typeName(c.membershipType),
                                                 amount: c.amount, memberSelectable: cb.checked ? '1' : '0' });
            if (!d.success) { note(false, d.message || 'Inställningen kunde inte sparas.'); cb.checked = !cb.checked; cb.disabled = false; return; }
            afterWrite(d); await load();
        });

        // ── Tillägg och avdrag ──
        var TEMPLATES = [
            ['own', 'Egen (börja från noll)', {}],
            ['home', 'Förbundsavgift för medlemmar med oss som huvudklubb', { label: 'Förbundsavgift', sign: 'plus', cond: 'PrimaryClub' }],
            ['nothome', 'Avdrag när en annan klubb är huvudklubb', { label: 'Avdrag annan huvudklubb', sign: 'minus', cond: 'NotPrimaryClub' }],
            ['fed', 'Avgift för ett förbund', { label: 'Förbundsavgift', sign: 'plus', cond: 'Option' }],
            ['type', 'Rabatt för en medlemstyp', { label: 'Rabatt', sign: 'minus', cond: 'Always' }]
        ];
        function partForm(p) {
            var opts = (fs.options || []).filter(function (o) { return o.isActive || (p && p.optionId === o.id); });
            var free = (fs.federations || []).filter(function (f) { return !(fs.options || []).some(function (o) { return sameKey(o.systemKey || o.name, f); }); });
            var cond = p ? p.condition : 'Always', sign = p && p.amount < 0 ? 'minus' : 'plus';
            var chosen = p ? (p.typeKeys || []).map(function (k) { return k.toLowerCase(); }) : [];
            return (p ? '' : '<label class="form-label" for="frPTemplate">Börja från en mall</label><select id="frPTemplate" class="form-select mb-3">'
                    + TEMPLATES.map(function (t) { return '<option value="' + t[0] + '">' + esc(t[1]) + '</option>'; }).join('') + '</select>')
                + '<label class="form-label" for="frPLabel">Namn</label>'
                + '<input id="frPLabel" class="form-control mb-3" maxlength="100" autocomplete="off" placeholder="till exempel Förbundsavgift" value="' + esc(p ? p.label : '') + '">'
                + '<div class="row g-2 mb-3"><div class="col-6"><label class="form-label" for="frPSign">Är det ett</label>'
                + '<select id="frPSign" class="form-select"><option value="plus"' + (sign === 'plus' ? ' selected' : '') + '>Tillägg</option><option value="minus"' + (sign === 'minus' ? ' selected' : '') + '>Avdrag</option></select></div>'
                + '<div class="col-6"><label class="form-label" for="frPAmount">Belopp, kr</label><input id="frPAmount" type="number" min="0" step="1" class="form-control" autocomplete="off" value="' + (p ? Math.abs(p.amount) : '') + '"></div></div>'
                + '<label class="form-label" for="frPCond">Gäller medlemmar som</label>'
                + '<select id="frPCond" class="form-select mb-2">'
                + [['Always', 'alla medlemmar'], ['PrimaryClub', 'har klubben som huvudklubb'], ['NotPrimaryClub', 'har en annan klubb som huvudklubb'], ['Option', 'är med i förbundet …']]
                    .map(function (c) { return '<option value="' + c[0] + '"' + (cond === c[0] ? ' selected' : '') + '>' + c[1] + '</option>'; }).join('')
                + '</select>'
                + '<div id="frPOptWrap" class="mb-2' + (cond === 'Option' ? '' : ' d-none') + '">'
                + '<label class="form-label small mb-0" for="frPOpt">Förbund</label>'
                + '<select id="frPOpt" class="form-select">' + opts.map(function (o) { return '<option value="' + o.id + '"' + (p && p.optionId === o.id ? ' selected' : '') + '>' + esc(o.name) + (o.isActive ? '' : ' (dolt)') + '</option>'; }).join('')
                + '<option value="new">＋ Nytt förbund…</option></select>'
                + '<div id="frPNewWrap" class="mt-2 d-none">'
                + (free.length ? '<select id="frPFed" class="form-select mb-2"><option value="">Annat förbund (skriv eget namn)</option>' + free.map(function (f) { return '<option>' + esc(f) + '</option>'; }).join('') + '</select>' : '')
                + '<input id="frPNewName" class="form-control" maxlength="100" autocomplete="off" placeholder="Förbundets namn"></div></div>'
                + '<div class="small text-muted mb-3">Medlemmar utan huvudklubb träffas varken av ”har klubben som huvudklubb” eller ”har en annan klubb som huvudklubb”.</div>'
                + '<div class="form-label">Medlemstyper</div>'
                + '<div class="form-check"><input class="form-check-input" type="checkbox" id="frPAllTypes"' + (chosen.length ? '' : ' checked') + '><label class="form-check-label" for="frPAllTypes">Alla typer</label></div>'
                + '<div id="frPTypes" class="ms-3' + (chosen.length ? '' : ' opacity-50') + '">'
                + types.filter(function (t) { return t.isActive || chosen.indexOf((t.key || '').toLowerCase()) >= 0; }).map(function (t) {
                    return '<div class="form-check"><input class="form-check-input" type="checkbox" data-fr-ptype="' + esc(t.key) + '" id="frPT_' + esc(t.key) + '"'
                        + (chosen.indexOf((t.key || '').toLowerCase()) >= 0 ? ' checked' : '') + '><label class="form-check-label" for="frPT_' + esc(t.key) + '">' + esc(t.name) + '</label></div>';
                }).join('') + '</div>'
                // Fast i modalens botten: typlistan är lång och blocket under den hamnade utom synhåll medan villkoret ändras högre upp.
                + '<div class="border rounded p-2 bg-body-tertiary small mt-3 shadow-sm" id="frPLive" style="position:sticky;bottom:0" aria-live="polite"><strong>Så här blir det</strong> <span class="text-muted">(räknas av servern)</span>'
                + '<div id="frPLiveBody" class="mt-1"></div></div>'
                + '<p class="text-muted small mt-3 mb-0">Avgifter som redan skickats ändras inte. Oskickade räknas om när du sparar.</p>';
        }
        function wirePartForm(editing) {
            var cond = $('frPCond'), optSel = $('frPOpt');
            // ── "Så här blir det": träffar, ett exempel och en medlem som inte träffas. Ritas om medan formuläret fylls i. ──
            var liveSeq = 0, liveTimer = null;
            function collect() {
                var amt = parseFloat($('frPAmount').value);
                var q = new URLSearchParams();
                q.append('clubId', CLUB_ID); q.append('year', year()); q.append('partId', editing ? editing.id : 0);
                q.append('amount', isNaN(amt) ? 0 : ($('frPSign').value === 'minus' ? -amt : amt));
                q.append('condition', cond.value);
                if (cond.value === 'Option' && optSel.value !== 'new') q.append('optionId', optSel.value);
                if (!$('frPAllTypes').checked) Array.prototype.forEach.call($('frPTypes').querySelectorAll('input:checked'), function (b) { q.append('typeKeys', b.getAttribute('data-fr-ptype')); });
                return q;
            }
            async function live() {
                var body = $('frPLiveBody'); if (!body) return;
                if (cond.value === 'Option' && (optSel.value === 'new' || !optSel.value)) {
                    body.innerHTML = '<span class="text-muted">Förbundet skapas när du sparar. Träffarna räknas då.</span>'; return;
                }
                var mine = ++liveSeq;
                var d = await get('PreviewPart', collect().toString());
                if (mine !== liveSeq || !$('frPLiveBody')) return;
                if (!d.success) { body.innerHTML = '<span class="text-muted">' + esc(d.message || 'Träffarna kunde inte räknas.') + '</span>'; return; }
                var h = '<div>Träffar <strong>' + d.applies + '</strong> av ' + plural(d.total, 'medlem', 'medlemmar') + '</div>';
                if (d.example) h += '<div>Exempel: ' + esc(d.example.name || 'en medlem') + (d.example.type ? ' (' + esc(typeName(d.example.type)) + ')' : '') + ' <span class="text-nowrap">'
                    + kr(d.example.before) + ' → ' + kr(d.example.after) + '</span></div>';
                else if (d.applies > 0) h += '<div class="text-muted">Ingen av de träffade har en avgift att visa som exempel än.</div>';
                if (d.notApplying) h += '<div class="text-muted">Gäller inte: ' + esc(d.notApplying.name || 'en medlem') + (d.notApplying.reason ? ' (' + esc(d.notApplying.reason) + ')' : '') + '.</div>';
                body.innerHTML = h;
            }
            function liveSoon() { clearTimeout(liveTimer); liveTimer = setTimeout(live, 250); }
            ['frPAmount', 'frPSign', 'frPCond', 'frPOpt'].forEach(function (id) { $(id).addEventListener('input', liveSoon); $(id).addEventListener('change', liveSoon); });
            $('frPAllTypes').addEventListener('change', liveSoon); $('frPTypes').addEventListener('change', liveSoon);
            if ($('frPTemplate')) $('frPTemplate').addEventListener('change', liveSoon);
            function sync() {
                show($('frPOptWrap'), cond.value === 'Option');
                show($('frPNewWrap'), cond.value === 'Option' && optSel.value === 'new');
                liveSoon();
            }
            cond.addEventListener('change', function () {
                // Inget förbund än: vi går direkt till "Nytt förbund…" så att kassören inte tappar det hon skrivit.
                if (cond.value === 'Option' && !optSel.querySelector('option:not([value=new])')) optSel.value = 'new';
                sync();
            });
            optSel.addEventListener('change', sync);
            if ($('frPFed')) $('frPFed').addEventListener('change', function () { if (this.value) $('frPNewName').value = this.value; });
            // "Alla typer" och enskilda typer utesluter varandra.
            var all = $('frPAllTypes');
            all.addEventListener('change', function () {
                if (all.checked) Array.prototype.forEach.call($('frPTypes').querySelectorAll('input'), function (b) { b.checked = false; });
                $('frPTypes').classList.toggle('opacity-50', all.checked);
            });
            $('frPTypes').addEventListener('change', function () {
                var any = $('frPTypes').querySelector('input:checked');
                all.checked = !any; $('frPTypes').classList.toggle('opacity-50', !any);
            });
            var tpl = $('frPTemplate');
            if (tpl) tpl.addEventListener('change', function () {
                var t = TEMPLATES.find(function (x) { return x[0] === tpl.value; });
                if (!t || !t[2].cond) return;
                if (!$('frPLabel').value) $('frPLabel').value = t[2].label;
                $('frPSign').value = t[2].sign; cond.value = t[2].cond;
                if (t[2].cond === 'Option' && !optSel.querySelector('option:not([value=new])')) optSel.value = 'new';
                sync();
            });
            sync();
        }
        function openPart(p) {
            openModal({ title: p ? 'Ändra ' + p.label : 'Nytt tillägg eller avdrag', ok: p ? 'Spara' : 'Lägg till', body: partForm(p),
                after: function () { wirePartForm(p); },
                onOk: async function () {
                    var amt = parseFloat($('frPAmount').value);
                    if (isNaN(amt) || amt <= 0) return 'Fyll i ett belopp över 0 kr. Välj Avdrag om det ska dras av.';
                    var cond = $('frPCond').value, optionId = null;
                    if (cond === 'Option') {
                        var v = $('frPOpt').value;
                        if (v === 'new') {
                            var name = (($('frPNewName').value || '').trim());
                            if (!name) return 'Skriv förbundets namn.';
                            var fed = $('frPFed') ? $('frPFed').value : '';
                            var o = await post('SaveFeeOption', { clubId: CLUB_ID, year: year(), id: 0, name: name, systemKey: fed && name === fed ? fed : '', isActive: '1' });
                            if (!o.success) return o.message || 'Förbundet kunde inte sparas.';
                            optionId = o.data && o.data.id;
                        } else optionId = parseInt(v, 10);
                        if (!optionId) return 'Välj ett förbund.';
                    }
                    var keys = $('frPAllTypes').checked ? [] : Array.prototype.map.call($('frPTypes').querySelectorAll('input:checked'), function (b) { return b.getAttribute('data-fr-ptype'); });
                    var f = { clubId: CLUB_ID, year: year(), id: p ? p.id : 0, label: $('frPLabel').value, amount: $('frPSign').value === 'minus' ? -amt : amt, condition: cond, typeKeys: keys };
                    if (optionId) f.optionId = optionId;
                    var d = await post('SaveFeePart', f);
                    if (!d.success) return d.message || 'Kunde inte spara.';
                    afterWrite(d); await load();
                    return null;
                } });
        }
        $('frParts').addEventListener('click', function (ev) {
            var b = ev.target.closest('.dropdown-item[data-fr-part]'); if (!b) return;
            var id = parseInt(b.getAttribute('data-fid'), 10), p = (fs.parts || []).find(function (x) { return x.id === id; });
            if (!p) return;
            if (b.getAttribute('data-fr-part') === 'edit') { openPart(p); return; }
            openModal({ title: 'Ta bort ' + p.label, ok: 'Ta bort', danger: true,
                body: '<p class="mb-0">Ta bort ”' + esc(p.label) + '” (' + signed(p.amount) + ')? Oskickade avgifter räknas om utan det. Avgifter som redan skickats ändras inte.</p>',
                onOk: async function () {
                    var d = await post('DeleteFeePart', { clubId: CLUB_ID, year: year(), id: p.id });
                    if (!d.success) return d.message || 'Kunde inte ta bort.';
                    afterWrite(d); await load();
                    return null;
                } });
        });
        if ($('frNewBase')) $('frNewBase').addEventListener('click', function () { openBase(null, null); });
        if ($('frNewPart')) $('frNewPart').addEventListener('click', function () { openPart(null); });

        // ── År ──
        var now = new Date().getFullYear();
        for (var y = now + 1; y >= now - 3; y--) {
            var o = document.createElement('option');
            o.value = y; o.textContent = y; if (y === now) o.selected = true;
            $('frYear').appendChild(o);
        }
        $('frYear').addEventListener('change', function () { note(true, ''); load(); });
    })();

    // ═════════════════════════ MEDLEMSTYPER OCH FÖRBUND ═════════════════════════
    var ft = $('ftRoot');
    if (ft) (function () {
        var CLUB_ID = parseInt(ft.getAttribute('data-club-id'), 10) || 0;
        var READONLY = ft.getAttribute('data-readonly') === '1';
        var types = [], untyped = 0, ageRules = { enabled: false }, fs = { parts: [], options: [], federations: [] };
        var modalCfg = null, loadSeq = 0;
        // Förbundens användning gäller tillägg för ETT år: innevarande. Sidan har ingen årsväljare (den gäller alla år).
        var YEAR = new Date().getFullYear();

        function note(ok, text) {
            show($('ftOk'), ok && !!text); show($('ftError'), !ok && !!text);
            $(ok ? 'ftOk' : 'ftError').textContent = text || '';
        }

        async function load() {
            var mine = ++loadSeq;
            var res = await Promise.all([
                get('GetMemberTypes', 'clubId=' + CLUB_ID),
                get('GetFeeSettings', 'clubId=' + CLUB_ID + '&year=' + YEAR).catch(function () { return null; })
            ]);
            if (mine !== loadSeq) return;
            var t = res[0], s = res[1];
            if (!t.success) { note(false, t.message || 'Medlemstyperna gick inte att läsa.'); return; }
            types = t.types || []; untyped = t.untyped || 0; ageRules = t.ageRules || { enabled: false };
            fs = (s && s.success) ? s : { parts: [], options: [], federations: [], failed: true };
            render();
        }
        window.hpskLoadClubFeeTypes = load;

        function mtOpts() {
            return { clubId: CLUB_ID, year: YEAR, canSeeFees: false, readOnly: READONLY, clubUrl: '',
                     onChanged: function () { load(); refreshList(); }, onMessage: function (t) { note(true, t); } };
        }
        // Typtabellen ritas av den delade komponenten direkt på sidan (mountTypes), inte i en dialog.
        var typesMount = null, mounting = false;
        function mountTypeTable() {
            if (typesMount || mounting) return;
            mounting = true;
            window.hpskMemberTypes.mountTypes($('ftTypesMount'), mtOpts()).then(function (m) { typesMount = m; mounting = false; }, function () { mounting = false; });
        }
        function openTypes() { if (typesMount) typesMount.newType(); }

        function render() {
            var active = types.filter(function (t) { return t.isActive; });
            $('ftTypesStatus').textContent = plural(types.length, 'typ', 'typer')
                + (ageRules.enabled ? ' · ' + types.filter(function (t) { return t.ageGroup; }).map(function (t) { return t.name + ' ' + t.ageGroup; }).join(' · ') : '');
            mountTypeTable();
            var bits = [];
            if (untyped > 0) bits.push(plural(untyped, 'medlem saknar', 'medlemmar saknar') + ' medlemstyp → <a href="#" data-view="arsavgift">Medlemsavgifter</a>');
            $('ftTypesNote').innerHTML = bits.join(' · ');

            var opts = fs.options || [];
            $('ftFedStatus').textContent = opts.length ? plural(opts.length, 'förbund', 'förbund') : '';
            $('ftFedRows').innerHTML = opts.map(function (o) {
                var used = (fs.parts || []).filter(function (p) { return p.optionId === o.id; }).length;
                return '<tr><td>' + esc(o.name) + (o.isActive ? '' : ' <span class="badge bg-light text-dark border">Dolt</span>') + '</td>'
                    + '<td class="small text-muted">' + (used ? plural(used, 'tillägg', 'tillägg') + ' (' + YEAR + ')' : '–') + '</td>'
                    + '<td class="text-end">' + (o.members != null ? o.members : '') + '</td>'
                    + '<td class="text-end">' + (READONLY ? '' : menu('ft-fed', o.id, [['rename', 'Byt namn…'], [o.isActive ? 'hide' : 'show', o.isActive ? 'Dölj' : 'Visa'], ['delete', 'Ta bort…', true]])) + '</td></tr>';
            }).join('') || '<tr><td colspan="4" class="text-muted small">Inga förbund ännu.</td></tr>';
            if (fs.failed) note(false, 'Förbunden kunde inte läsas just nu. Ladda om sidan.');
        }

        function openModal(cfg) {
            modalCfg = cfg;
            $('ftModalTitle').textContent = cfg.title; $('ftModalBody').innerHTML = cfg.body;
            $('ftModalOk').textContent = cfg.ok || 'Spara';
            $('ftModalOk').className = 'btn ' + (cfg.danger ? 'btn-danger' : 'btn-primary'); $('ftModalOk').disabled = false;
            show($('ftModalError'), false);
            bootstrap.Modal.getOrCreateInstance($('ftModal')).show();
            if (cfg.after) cfg.after();
        }
        $('ftModalOk').addEventListener('click', async function () {
            if (!modalCfg) return;
            var btn = this; btn.disabled = true; var err = null;
            try { err = await modalCfg.onOk(); } catch (e) { err = 'Något gick fel. Försök igen.'; }
            btn.disabled = false;
            if (err) { $('ftModalError').textContent = err; show($('ftModalError'), true); return; }
            bootstrap.Modal.getOrCreateInstance($('ftModal')).hide();
        });
        $('ftModalBody').addEventListener('keydown', function (ev) { if (ev.key === 'Enter' && ev.target.tagName === 'INPUT') { ev.preventDefault(); $('ftModalOk').click(); } });
        $('ftModal').addEventListener('shown.bs.modal', function () { var i = $('ftModalBody').querySelector('input,select'); if (i) i.focus(); });

        function optionOf(id) { return (fs.options || []).find(function (o) { return o.id === id; }); }
        async function saveOption(o, name, systemKey, active) {
            var d = await post('SaveFeeOption', { clubId: CLUB_ID, year: YEAR, id: o ? o.id : 0, name: name, systemKey: systemKey || '', isActive: active ? '1' : '0' });
            if (!d.success) return d.message || 'Förbundet kunde inte sparas.';
            note(true, consequenceText(d.consequences)); refreshList(); await load();
            return null;
        }
        function newFed() {
            var taken = (fs.options || []).map(function (o) { return (o.systemKey || o.name).toLowerCase(); });
            var free = (fs.federations || []).filter(function (f) { return taken.indexOf(f.toLowerCase()) < 0; });
            openModal({ title: 'Nytt förbund', ok: 'Lägg till',
                body: (free.length ? '<label class="form-label" for="ftFed">Förbund</label><select id="ftFed" class="form-select mb-3"><option value="">Annat förbund (skriv eget namn)</option>'
                        + free.map(function (f) { return '<option>' + esc(f) + '</option>'; }).join('') + '</select>' : '')
                    + '<label class="form-label" for="ftName">Namn</label><input id="ftName" class="form-control" maxlength="100" autocomplete="off" placeholder="till exempel Sportskytteförbundet">'
                    + '<p class="text-muted small mt-2 mb-0">Du sätter sedan förbundet på medlemmar (Åtgärder → Medlemskap på raden) och lägger en avgift för det som tillägg under Avgiftsregler.</p>',
                after: function () { if ($('ftFed')) $('ftFed').addEventListener('change', function () { if (this.value) $('ftName').value = this.value; }); },
                onOk: function () {
                    var fed = $('ftFed') ? $('ftFed').value : '', name = ($('ftName').value || '').trim() || fed;
                    return saveOption(null, name, fed && name === fed ? fed : '', true);
                } });
        }
        if ($('ftNewFed')) $('ftNewFed').addEventListener('click', newFed);
        if ($('ftNewType')) $('ftNewType').addEventListener('click', openTypes);
        // Länken är ritad av skriptet efter att Ekonomi bundit rälsen: den klickar på rälsposten.
        $('ftTypesNote').addEventListener('click', function (ev) {
            var a = ev.target.closest('[data-view]'); if (!a) return;
            ev.preventDefault();
            var nav = document.querySelector('.ek-nav[data-view="' + a.getAttribute('data-view') + '"]'); if (nav) nav.click();
        });
        $('ftFedRows').addEventListener('click', function (ev) {
            var b = ev.target.closest('.dropdown-item[data-ft-fed]'); if (!b) return;
            var id = parseInt(b.getAttribute('data-fid'), 10), o = optionOf(id), act = b.getAttribute('data-ft-fed');
            if (!o) return;
            var uses = (fs.parts || []).filter(function (p) { return p.optionId === o.id; });
            if (act === 'hide' || act === 'show') {
                var go = function () { return saveOption(o, o.name, o.systemKey, act === 'show'); };
                if (act === 'show' || !uses.length) { go().then(function (e) { if (e) note(false, e); }); return; }
                openModal({ title: 'Dölj ' + o.name, ok: 'Dölj',
                    body: '<p class="mb-0">Tillägget ' + uses.map(function (p) { return '”' + esc(p.label) + '”'; }).join(', ') + ' (' + YEAR + ') gäller fortfarande medlemmar som redan är med. Förbundet går inte längre att välja för nya.</p>',
                    onOk: go });
            } else if (act === 'rename')
                openModal({ title: 'Byt namn på ' + o.name, ok: 'Spara', body: '<label class="form-label" for="ftName">Namn</label><input id="ftName" class="form-control" maxlength="100" autocomplete="off" value="' + esc(o.name) + '">',
                    onOk: function () { return saveOption(o, $('ftName').value, o.systemKey, o.isActive); } });
            else
                openModal({ title: 'Ta bort ' + o.name, ok: 'Ta bort', danger: true,
                    body: '<p class="mb-0">Ta bort förbundet ' + esc(o.name) + '? Det går bara när ingen medlem är med i det och inget tillägg använder det'
                        + (uses.length ? ' (just nu: ' + uses.map(function (p) { return esc(p.label); }).join(', ') + ')' : '') + '. Annars kan du dölja det.</p>',
                    onOk: async function () {
                        var d = await post('DeleteFeeOption', { clubId: CLUB_ID, year: YEAR, id: o.id });
                        if (!d.success) return d.message || 'Förbundet kunde inte tas bort.';
                        refreshList(); await load();
                        return null;
                    } });
        });
    })();
})();
