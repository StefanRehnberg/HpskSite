(function () {
    if (window.__clubFeeInit) return;
    window.__clubFeeInit = true;

    var CFG = window.__clubFeeConfig || {};
    var CLUB_ID = CFG.clubId || 0;
    var READONLY = !!CFG.readOnly;
    var CLUB_URL = CFG.clubUrl || '';
    var F = '/umbraco/surface/MembershipFeeAdmin/';
    var data = null, dirty = false;
    var MONTHS = ['jan','feb','mar','apr','maj','jun','jul','aug','sep','okt','nov','dec'];

    function $(id) { return document.getElementById(id); }
    function esc(s) {
        return (s == null ? '' : String(s)).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function kr(n) { return (Math.round((n || 0) * 100) / 100).toLocaleString('sv-SE', { maximumFractionDigits: 2 }) + ' kr'; }
    function day(iso) { if (!iso) return ''; var p = iso.split('-'); return parseInt(p[2], 10) + ' ' + MONTHS[parseInt(p[1], 10) - 1]; }
    function year() { return parseInt($('cfYear').value, 10); }
    function show(el, on) { if (el) el.classList.toggle('d-none', !on); }
    function msg(ok, text, kind) {
        $('cfOk').className = 'alert ' + (kind === 'warn' ? 'alert-warning' : 'alert-success');
        show($('cfOk'), ok && !!text); show($('cfError'), !ok && !!text);
        $(ok ? 'cfOk' : 'cfError').textContent = text || '';
    }
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

    // ── Medlemstyper: nyckel + klubbens namn. Visa ALLTID namnet, aldrig nyckeln (en egen typ har nyckeln x-…). ──
    var types = [], untyped = 0, ageRules = { enabled: false, error: null };
    // Tillägg/avdrag och tillval för året (GetFeeSettings).
    var fs = { parts: [], options: [], federations: [] };
    function optionOf(id) { return (fs.options || []).find(function (o) { return o.id === id; }); }
    function signed(n) { return (n > 0 ? '+' : n < 0 ? '−' : '') + kr(Math.abs(n)); }
    // Villkoret i klartext — samma ord som i dialogen.
    function condText(p) {
        if (p.condition === 'PrimaryClub') return 'Klubben är huvudklubb';
        if (p.condition === 'NotPrimaryClub') return 'En annan klubb är huvudklubb';
        if (p.condition === 'Option') { var o = optionOf(p.optionId); return 'Har tillvalet ' + (o ? o.name : '?'); }
        return 'Alla';
    }
    function partScope(p) {
        var c = condText(p), t = (p.typeKeys || []).map(typeName).join(', ');
        return c + (t ? ' · ' + t : '');
    }
    function usesHomeClub() { return (fs.parts || []).some(function (p) { return p.condition === 'PrimaryClub' || p.condition === 'NotPrimaryClub'; }); }
    var filterVal = 'all';
    var NEVER_AGE = { 'familj': 1, 'hedersmedlem': 1, 'ständig': 1, 'stödjande': 1 };
    function typeOf(key) { var k = (key || '').toLowerCase(); return types.find(function (t) { return (t.key || '').toLowerCase() === k; }); }
    function typeName(key) { if (!key) return ''; var t = typeOf(key); return t ? t.name : key; }
    function neverAge(key) { return !!NEVER_AGE[(key || '').toLowerCase()]; }

    function rowOf(memberId) { return (data.rows || []).find(function (r) { return r.memberId === memberId; }); }
    var BILLABLE = { Individual: true, HouseholdPrimary: true };

    // ⚠️ Varje handling på en rad börjar här: avgiften skapas oskickad om den inte finns.
    async function chargeIdFor(memberId) {
        var r = rowOf(memberId);
        if (r && r.charge) return r.charge.id;
        var e = await form('EnsureClubCharge', { clubId: CLUB_ID, year: year(), memberId: memberId });
        if (!e.success) { msg(false, e.message); return null; }
        return e.chargeId;
    }

    function stateOf(r) {
        var c = r.charge;
        return {
            c: c,
            paid: !!c && c.status === 'Paid',
            sent: !!c && !!c.requestSentDate,
            claimed: !!c && !!c.paymentSentDate,
            covered: r.kind === 'Covered' || (!!c && c.covered),
            billable: !!c ? (!c.covered && c.amount > 0) : (BILLABLE[r.kind] && r.proposedAmount > 0),
            // Medlemmen saknar medlemstyp och väljer den på betalsidan — mejlet gäller valet, inte ett belopp.
            choose: !!c ? !!c.needsTypeChoice : r.kind === 'ChooseType',
            amount: c ? c.amount : r.proposedAmount
        };
    }

    // ⚠️ Status som på resten av sajten (Anmälningar, Fakturor): en BRICKA, datum intill i grått.
    function badge(cls, text, note) {
        return '<span class="badge ' + cls + '">' + text + '</span>' + (note ? ' <span class="small text-muted">' + note + '</span>' : '');
    }
    function statusCell(r, s) {
        if (s.covered) return badge('bg-light text-dark border', 'Ingår i familjen', r.coveredBy ? esc(r.coveredBy) + ' betalar' : '');
        if (s.paid) return badge('bg-success', 'Betald', s.c.paidDate ? day(s.c.paidDate) : '');
        if (s.claimed) return badge('bg-info text-dark', 'Uppgett betald', day(s.c.paymentSentDate));
        if (s.choose) return badge('bg-info text-dark', 'Väljer medlemstyp', s.sent ? 'skickad ' + day(s.c.requestSentDate) : 'på betalsidan');
        if (!s.c && r.kind === 'NoType') return badge('bg-light text-dark border', 'Ingen medlemstyp', 'ange den under Medlemmar');
        if (!s.c && r.kind === 'NoCategory') return badge('bg-light text-dark border', 'Ingen avgift', 'för ' + esc(typeName(r.membershipType)));
        if (s.amount <= 0) return badge('bg-light text-dark border', 'Ingen avgift');
        if (s.sent) return badge('bg-warning text-dark', 'Väntande', 'skickad ' + day(s.c.requestSentDate));
        return badge('bg-secondary', 'Inte skickad');
    }
    // Medlemstypen — och om MEDLEMMEN valt den själv. Kassören ska se det: ett förtroendesystem, inte ett osynligt.
    // Kolumnen visar bara typen. Förbund och tillägg står under beloppet (Att betala).
    // Typen är en bricka vars färg visar huvudklubben (förklaras av raden ovanför tabellen).
    var HOME = {
        Ours:    { cls: 'bg-primary-subtle text-primary-emphasis border', tip: 'Klubben är medlemmens huvudklubb' },
        Other:   { cls: 'bg-warning-subtle text-warning-emphasis border', tip: 'En annan klubb är medlemmens huvudklubb' },
        Unknown: { cls: 'bg-danger-subtle text-danger-emphasis border', tip: 'Medlemmen har ingen huvudklubb' }
    };
    function typeCell(r, s) {
        var h = HOME[r.homeClub] || HOME.Unknown;
        var t = '<span class="badge ' + h.cls + '" title="' + h.tip + '">' + esc(typeName(r.membershipType)) + '</span>';
        if (s.choose) return '<span class="text-muted">väljer själv</span>';
        if (s.c && s.c.memberChosenType)
            t += ' <span class="badge bg-light text-dark border" title="Medlemmen valde medlemstypen själv på betalsidan'
               + (s.c.memberChosenAt ? ' ' + day(s.c.memberChosenAt) : '') + '">vald av medlemmen</span>';
        return t;
    }
    // Adressen visas inte som kolumn (den finns under Åtgärder → E-post för avgiften…), men saknas den syns det på raden.
    function noEmailBadge(r) {
        return r.hasEmail ? '' : '<span class="badge bg-danger-subtle text-danger-emphasis border">Ingen e-post</span>';
    }

    function item(act, id, text, extra) {
        return '<li><button type="button" class="dropdown-item' + (extra || '') + '" data-cf-act="' + act
             + '" data-cf-id="' + id + '">' + text + '</button></li>';
    }
    function actionsCell(r, s) {
        if (READONLY) return '';
        var id = r.memberId, items = [];
        // Medlemskap (typ, status, förbund, familj, klubbar) är samma dialog som under Medlemmar. Den visas även för den som ingår i en familjs avgift.
        var memberItems = '<li><h6 class="dropdown-header">Medlem</h6></li>' + item('member', id, 'Medlemskap…');
        if (s.covered) return '<div class="dropdown"><button type="button" class="btn btn-sm btn-outline-secondary dropdown-toggle" '
             + 'data-bs-toggle="dropdown" data-bs-popper-config=\'{"strategy":"fixed"}\'>Åtgärder</button><ul class="dropdown-menu dropdown-menu-end">' + memberItems + '</ul></div>';
        items.push(memberItems);
        items.push('<li><hr class="dropdown-divider"></li>');
        if (s.paid) items.push(item('unpaid', id, 'Ångra betald'));
        else if (s.billable || s.choose) {
            // Betald ligger i menyn som allt annat på raden — samma form som resten av sajten.
            // En medlem som väljer medlemstyp har inget belopp än, och kan inte markeras betald.
            if (!s.choose) {
                items.push('<li><h6 class="dropdown-header">Betalning</h6></li>');
                items.push(item('paid', id, 'Markera som betald'));
                items.push('<li><hr class="dropdown-divider"></li>');
            }
            items.push('<li><h6 class="dropdown-header">Mejlet</h6></li>');
            items.push(item('preview', id, 'Visa mejlet'));
            if (r.hasEmail) items.push(item('send-one', id, s.sent ? 'Skicka mejlet igen' : 'Skicka mejlet nu'));
            items.push(item('link', id, 'Kopiera betallänk'));
        } else if (r.kind === 'NoCategory') items.push(item('rates', id, 'Grundavgift per medlemstyp…'));
        // E-postadressen går att sätta för alla — en förälder som betalar juniorens avgift.
        if (!s.paid) items.push(item('email', id, 'E-post för avgiften…'));
        return '<div class="dropdown"><button type="button" class="btn btn-sm btn-outline-secondary dropdown-toggle" '
             + 'data-bs-toggle="dropdown" data-bs-popper-config=\'{"strategy":"fixed"}\'>Åtgärder</button>'
             + '<ul class="dropdown-menu dropdown-menu-end">' + items.join('') + '</ul></div>';
    }

    // Uppdelningen under beloppet: "Grund 600 kr · Förbund +100 kr". En skickad eller betald avgift är fryst — då
    // visas bara att förslaget blivit ett annat.
    function breakdown(r, s) {
        var frozen = !!s.c && (s.sent || s.paid || s.claimed);
        if (frozen) {
            if (r.proposedAmount > 0 && Math.abs(r.proposedAmount - s.amount) >= 0.005)
                return '<div class="small text-muted fw-normal cf-bd">Förslaget är nu ' + kr(r.proposedAmount) + '</div>';
            return '';
        }
        var ls = r.lines || [];
        if (ls.length < 2) return '';
        return '<div class="small text-muted fw-normal cf-bd">' + ls.map(function (l) {
            return esc(l.text) + ' ' + (l.kind === 'fee-base' ? kr(l.amount) : signed(l.amount));
        }).join(' · ') + '</div>';
    }
    function renderFilter() {
        var opts = [['all', 'Alla medlemmar']];
        if (usesHomeClub()) { opts.push(['home-unknown', 'Huvudklubb saknas']); opts.push(['home-other', 'Annan huvudklubb']); }
        (fs.options || []).filter(function (o) { return o.isActive; }).forEach(function (o) { opts.push(['opt:' + o.id, 'Är med i förbundet ' + o.name]); });
        // Ingenting att filtrera på → ingen väljare.
        var wrap = $('cfFilterWrap'), sel = $('cfFilter');
        if (!wrap) return;
        if (opts.length < 2) { filterVal = 'all'; show(wrap, false); return; }
        if (!opts.some(function (o) { return o[0] === filterVal; })) filterVal = 'all';
        sel.innerHTML = opts.map(function (o) { return '<option value="' + esc(o[0]) + '">' + esc(o[1]) + '</option>'; }).join('');
        sel.value = filterVal;
        show(wrap, true);
    }
    function passesFilter(r) {
        if (filterVal === 'all') return true;
        if (filterVal === 'home-unknown') return r.homeClub === 'Unknown';
        if (filterVal === 'home-other') return r.homeClub === 'Other';
        if (filterVal.indexOf('opt:') === 0) return (r.optionIds || []).indexOf(parseInt(filterVal.slice(4), 10)) >= 0;
        return true;
    }

    function render() {
        var cats = data.categories || [];
        var hasRate = cats.length > 0;
        var yr = year();
        $('cfRateLine').textContent = hasRate
            ? 'Avgiften ' + yr + ': ' + cats.map(function (c) { return typeName(c.membershipType) + ' ' + kr(c.amount); }).join(' · ') : '';
        show($('cfNoRate'), !hasRate);
        $('cfNoRateText').textContent = 'Klubben har inte bestämt medlemsavgiften för ' + yr + ' än.';
        show($('cfListWrap'), hasRate);

        var rows = data.rows || [];
        renderFilter();
        var html = '', total = 0, billed = 0, paidCount = 0, paidSum = 0, toSend = 0, noMail = 0, toRemind = 0;
        rows.forEach(function (r) {
            var s = stateOf(r);
            if (s.billable) { total += s.amount; billed++; }
            if (s.paid && s.billable) { paidCount++; paidSum += s.amount; }
            if ((s.billable || s.choose) && !s.paid && !s.sent) { if (r.hasEmail) toSend++; else noMail++; }
            if (s.billable && !s.paid && s.sent) toRemind++;
            // Filtret påverkar bara vilka rader som visas — summor och utskick gäller alla.
            if (!passesFilter(r)) return;

            var amount = s.covered ? '<span class="text-muted">—</span>' : (s.billable || s.paid ? kr(s.amount) : '<span class="text-muted">—</span>')
                       + breakdown(r, s);
            var nameFlags = noEmailBadge(r);
            // Förbunden medlemmen är med i, som vanlig text under namnet (inte brickor). Syns även när tillvalet saknar pris.
            var fed = (r.optionIds || []).map(function (id) { var o = optionOf(id); return o ? o.name + (o.isActive ? '' : ' (dolt)') : ''; }).filter(Boolean);
            html += '<tr class="cf-main"><td>' + esc(r.name) + (nameFlags ? '<div class="small">' + nameFlags + '</div>' : '')
                  + (fed.length ? '<div class="small text-muted">Förbund: ' + esc(fed.join(', ')) + '</div>' : '')
                  + '<div class="small text-muted d-sm-none">' + typeCell(r, s) + '</div></td>'
                  + '<td class="d-none d-sm-table-cell">' + typeCell(r, s) + '</td>'
                  + '<td class="text-end cf-n cf-amt">' + amount + '</td>'
                  + '<td class="small d-none d-sm-table-cell">' + statusCell(r, s) + '</td>'
                  + '<td class="text-end cf-n cf-actions d-none d-sm-table-cell">' + actionsCell(r, s) + '</td></tr>'
                  + '<tr class="cf-mrow d-sm-none"><td colspan="2"><div class="d-flex flex-wrap justify-content-between align-items-center gap-2 small">'
                  + '<span>' + statusCell(r, s) + '</span>' + actionsCell(r, s) + '</div></td></tr>';
        });
        html += '<tr class="cf-total"><td>Totalt</td><td class="d-none d-sm-table-cell"></td><td class="text-end cf-n">' + kr(total)
              + '</td><td class="d-none d-sm-table-cell"></td><td class="d-none d-sm-table-cell"></td></tr>';
        $('cfRows').innerHTML = html;

        $('cfSummary').textContent = hasRate
            ? paidCount + ' av ' + billed + ' medlemmar har betalat · ' + kr(paidSum) + ' av ' + kr(total) : '';

        if ($('cfSend')) {
            $('cfSend').querySelector('span').textContent = 'Skicka avgiften till ' + toSend + (toSend === 1 ? ' medlem' : ' medlemmar');
            $('cfRemind').querySelector('span').textContent = 'Påminn ' + toRemind + (toRemind === 1 ? ' medlem' : ' medlemmar') + ' som inte betalat';
            show($('cfSend').parentElement, hasRate && toSend > 0);
            show($('cfRemind').parentElement, hasRate && toRemind > 0);
            show($('cfNothingToSend'), !(hasRate && (toSend > 0 || toRemind > 0)));
            $('cfNothingToSend').textContent = hasRate ? 'Alla medlemmar har fått avgiften.' : 'Bestäm avgiften först.';
        }

        renderUntyped();
    }

    // Gul varning: aktiva medlemmar utan typ får ingen avgift.
    function renderUntyped() {
        show($('cfUntyped'), untyped > 0);
        $('cfUntypedText').textContent = untyped === 1 ? '1 medlem saknar medlemstyp.' : untyped + ' medlemmar saknar medlemstyp.';
    }

    // ⚠️ load() rör INTE beskedet — det körs direkt efter Skicka.
    // ⚠️ Ett svar som kommer efter ett årsbyte gäller fel år och får INTE skriva över listan.
    var loadSeq = 0;
    async function load() {
        var mine = ++loadSeq;
        var both = await Promise.all([
            fetch(F + 'GetOverview?clubId=' + CLUB_ID + '&year=' + year(), { cache: 'no-store' }).then(function (r) { return r.json(); }),
            fetch(F + 'GetMemberTypes?clubId=' + CLUB_ID, { cache: 'no-store' }).then(function (r) { return r.json(); }).catch(function () { return null; }),
            fetch(F + 'GetFeeSettings?clubId=' + CLUB_ID + '&year=' + year(), { cache: 'no-store' }).then(function (r) { return r.json(); }).catch(function () { return null; })
        ]);
        var d = both[0], t = both[1], fsd = both[2];
        if (mine !== loadSeq) return;
        if (!d.success) { msg(false, d.message || 'Medlemsavgifterna gick inte att läsa.'); return; }
        // Går tilläggen inte att läsa visas listan ändå (summan kommer från GetOverview) — men dialogerna säger det.
        fs = (fsd && fsd.success) ? fsd : { parts: [], options: [], federations: [], failed: true };
        // Typerna är namnen på sidan. Går de inte att läsa visas nycklarna (standardtyperna är läsbara ändå) — aldrig en tom lista.
        if (t && t.success) { types = t.types || []; untyped = t.untyped || 0; ageRules = t.ageRules || { enabled: false, error: null }; }
        data = d;
        render();
        refreshAgeCount();
    }

    // ── E-post för avgiften ─────────────────────────────────────────────────────────────
    var emailFor = 0;
    $('cfEmailSave').addEventListener('click', async function () {
        var d = await form('SetFeeEmail', { issuerType: 0, issuerId: CLUB_ID, payerId: emailFor, email: $('cfEmailInput').value });
        // ⚠️ Dialogen stängs INTE vid fel — annars får man börja om för att läsa vad som gick snett.
        if (!d.success) { $('cfEmailError').textContent = d.message || 'Adressen kunde inte sparas.'; show($('cfEmailError'), true); return; }
        bootstrap.Modal.getOrCreateInstance($('cfEmailModal')).hide();
        msg(true, '');
        load();
    });

    // ── Klick i listan ──────────────────────────────────────────────────────────────────
    $('cfRoot').addEventListener('click', async function (ev) {
        var a = ev.target.closest('[data-cf-act]');
        if (!a) return;
        var act = a.getAttribute('data-cf-act'), memberId = parseInt(a.getAttribute('data-cf-id'), 10);
        var row = rowOf(memberId), r, id;

        // Avgiften per typ bor på sin egen sida (Avgiftsregler): klicka på rälsposten.
        if (act === 'rates') { var nav = document.querySelector('.ek-nav[data-view="avgiftsregler"]'); if (nav) nav.click(); return; }
        if (act === 'member') {
            window.hpskMembership.open({ clubId: CLUB_ID, memberId: memberId, year: year(), onSaved: function () { load(); } });
            return;
        }
        if (act === 'email') {
            emailFor = memberId;
            $('cfEmailTitle').textContent = 'E-post för avgiften — ' + row.name;
            $('cfEmailInput').value = row.emailIsOwn ? row.email : '';
            $('cfEmailHelp').textContent = (row.defaultEmail
                ? 'Lämna tomt för att använda medlemmens egen adress (' + row.defaultEmail + ').'
                : 'Medlemmen har ingen egen adress på pistol.nu.') + ' Medlemmens egen adress ändras inte.';
            show($('cfEmailError'), false);
            bootstrap.Modal.getOrCreateInstance($('cfEmailModal')).show();
            return;
        }
        if (act === 'preview') {
            // ⚠️ Fönstret öppnas FÖRE await — ett window.open efter en väntan blockeras.
            var w = window.open('about:blank', '_blank');
            id = await chargeIdFor(memberId);
            if (!id) { if (w) w.close(); return; }
            if (w) w.location = F + 'PreviewClubPaymentRequest?chargeId=' + id;
            if (!row.charge) load();
            return;
        }
        if (act === 'link') {
            id = await chargeIdFor(memberId); if (!id) return;
            r = await (await fetch(F + 'GetPaymentLink?chargeId=' + id, { cache: 'no-store' })).json();
            if (!r.success) { msg(false, r.message); return; }
            try { await navigator.clipboard.writeText(r.url); msg(true, 'Betallänken för ' + row.name + ' är kopierad.'); }
            catch (x) { msg(true, 'Betallänk för ' + row.name + ': ' + r.url); }
            load();
            return;
        }
        if (act === 'send-one') {
            id = await chargeIdFor(memberId); if (!id) return;
            report(await form('SendPaymentRequests', { clubId: CLUB_ID, year: year(), chargeIds: String(id) }));
            load();
            return;
        }

        a.disabled = true;
        if (act === 'paid') {
            id = await chargeIdFor(memberId);
            r = id ? await form('MarkPaid', { chargeId: id }) : null;
        } else if (act === 'unpaid') {
            r = await form('MarkUnpaid', { chargeId: row.charge.id });
        } else { a.disabled = false; return; }

        if (!r || !r.success) { if (r) msg(false, r.message); a.disabled = false; if (!row.charge) load(); return; }
        msg(true, '');
        load();
    });

    // ── Skicka och påminn ───────────────────────────────────────────────────────────────
    if ($('cfSend')) $('cfSend').addEventListener('click', async function () {
        var list = (data.rows || []).filter(function (r) { var s = stateOf(r); return (s.billable || s.choose) && !s.paid && !s.sent && r.hasEmail; });
        if (list.length === 0) return;
        var sum = list.reduce(function (a, r) { return a + stateOf(r).amount; }, 0);
        var choosing = list.filter(function (r) { return stateOf(r).choose; }).length;        openSub({
            title: 'Skicka medlemsavgiften ' + year(), ok: 'Skicka',
            body: '<p class="mb-2">Mejla medlemsavgiften ' + year() + ' till <strong>' + list.length + (list.length === 1 ? ' medlem' : ' medlemmar')
                + '</strong>, totalt <strong>' + kr(sum) + '</strong>.</p>'
                + (choosing ? '<p class="small text-muted mb-2">' + choosing + ' saknar medlemstyp och får välja den själva på betalsidan.</p>' : '')
                + '<div id="cfSendWarn"></div>',
            onOk: async function () {
                // Medlemmar som ingen rört har ingen avgift än — den skapas nu, ur samma regel som listan visar.
                var g = await form('GenerateCharges', { clubId: CLUB_ID, year: year() });
                if (!g.success) return g.message || 'Avgifterna kunde inte skapas.';
                report(await form('SendPaymentRequests', { clubId: CLUB_ID, year: year(), mode: 'unsent' }));
                load();
                return null;
            }
        });
        // Gul varning (spärrar inte): förslag efter ålder som ingen godkänt. Går de inte att räkna visas ingenting.
        var pending = await window.hpskMemberTypes.summary(mtOpts());
        var box = $('cfSendWarn');
        if (box && subCfg && pending && pending.ageProposals > 0) {
            var n = pending.ageProposals;
            box.innerHTML = '<div class="alert alert-warning mb-0 small">' + n + (n === 1 ? ' medlem har' : ' medlemmar har')
                + ' ett förslag på ny medlemstyp efter ålder som ingen godkänt. Avgiften skickas med den typ de har nu. '
                + '<button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-cf-open-age="1">Granska förslagen</button></div>';
        }
    });
    if ($('cfRemind')) $('cfRemind').addEventListener('click', function () {
        var n = (data.rows || []).filter(function (r) { var s = stateOf(r); return s.billable && !s.paid && s.sent; }).length;
        openSub({
            title: 'Påminn medlemmarna', ok: 'Skicka påminnelse',
            body: '<p class="mb-0">Mejla en påminnelse till <strong>' + n + (n === 1 ? ' medlem' : ' medlemmar') + '</strong> som inte betalat.</p>',
            onOk: async function () {
                report(await form('SendPaymentRequests', { clubId: CLUB_ID, year: year(), mode: 'reminder' }));
                load();
                return null;
            }
        });
    });

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
    // cfg: { title, body (html), ok, danger, from (dialogen som öppnade, öppnas igen vid stängning), after (körs vid stängning), onOk }
    var subCfg = null;
    function openSub(cfg) {
        subCfg = cfg;
        cfg.back = cfg.back || cfg.from;
        $('cfSubTitle').textContent = cfg.title;
        $('cfSubBody').innerHTML = cfg.body;
        $('cfSubOk').textContent = cfg.ok || 'Spara';
        $('cfSubOk').className = 'btn ' + (cfg.danger ? 'btn-danger' : 'btn-primary');
        $('cfSubOk').disabled = false;
        show($('cfSubError'), false);
        showModal('cfSubModal', cfg.from);
    }
    $('cfSubOk').addEventListener('click', async function () {
        if (!subCfg) return;
        var btn = this; btn.disabled = true;
        var err = null;
        try { err = await subCfg.onOk(); } catch (e) { err = 'Något gick fel. Försök igen.'; }
        btn.disabled = false;
        if (err) { $('cfSubError').textContent = err; show($('cfSubError'), true); return; }
        bootstrap.Modal.getOrCreateInstance($('cfSubModal')).hide();
    });
    $('cfSubBody').addEventListener('keydown', function (ev) { if (ev.key === 'Enter' && ev.target.tagName === 'INPUT') { ev.preventDefault(); $('cfSubOk').click(); } });
    $('cfSubBody').addEventListener('click', function (ev) {
        if (ev.target.closest('[data-cf-open-age]') && subCfg) { subCfg.after = function () { window.hpskMemberTypes.openAge(mtOpts()); }; bootstrap.Modal.getOrCreateInstance($('cfSubModal')).hide(); }
    });
    $('cfSubModal').addEventListener('shown.bs.modal', function () { var i = $('cfSubBody').querySelector('input,select'); if (i) i.focus(); });
    $('cfSubModal').addEventListener('hidden.bs.modal', function () {
        var c = subCfg; subCfg = null;
        if (!c) return;
        if (c.after) c.after(); else if (c.back) bootstrap.Modal.getOrCreateInstance($(c.back)).show();
    });

    // ── Medlemstyper, åldersgränser, Åldersförslag och "Sätt typ på alla" ───────────────
    // Delad komponent (wwwroot/js/member-types.js + Partials/_MemberTypesDialogs) — samma dialoger når klubbadmin under Medlemmar.
    function mtOpts() {
        return { clubId: CLUB_ID, year: year(), canSeeFees: true, readOnly: READONLY, clubUrl: CLUB_URL,
                 onChanged: function () { load(); }, onMessage: function (t) { msg(true, t); } };
    }
    if ($('cfSetUntyped')) $('cfSetUntyped').addEventListener('click', function () { window.hpskMemberTypes.openSetUntyped(mtOpts()); });
    $('cfAgeRowBtn').addEventListener('click', function () { window.hpskMemberTypes.openAge(mtOpts()); });
    // Åldersförslagen är en gul uppgiftsrad (som "Sätt typ på alla"). Går det inte att räkna visas ingenting.
    var ageSeq = 0;
    async function refreshAgeCount() {
        var row = $('cfAgeRow');
        if (!ageRules.enabled) { show(row, false); return; }
        var mine = ++ageSeq, s = await window.hpskMemberTypes.summary(mtOpts());
        if (mine !== ageSeq) return;
        var n = s && s.ageEnabled ? s.ageProposals : 0;
        show(row, n > 0);
        $('cfAgeRowText').textContent = n + (n === 1 ? ' medlem har ett förslag på ny medlemstyp efter ålder.' : ' medlemmar har förslag på ny medlemstyp efter ålder.');
    }

    $('cfFilter').addEventListener('change', function () { filterVal = this.value; render(); });

    // ⚠️ Beskedet säger vad som FAKTISKT hände.
    function report(s) {
        if (!s || !s.success) { msg(false, 'Mejlen gick inte att skicka. ' + ((s && s.message) || '')); return; }
        var parts = [s.sent + (s.sent === 1 ? ' medlem fick' : ' medlemmar fick') + ' ett mejl.'];
        var missed = (s.noEmail || []).concat(s.failed || []);
        if (missed.length > 0) parts.push('Inget mejl gick till ' + missed.join(', ') + '. Välj Åtgärder → Kopiera betallänk på raden och skicka länken själv.');
        msg(true, parts.join(' '), missed.length > 0 ? 'warn' : 'ok');
    }

    var now = new Date().getFullYear();
    for (var y = now + 1; y >= now - 3; y--) {
        var o = document.createElement('option');
        o.value = y; o.textContent = y; if (y === now) o.selected = true;
        $('cfYear').appendChild(o);
    }
    $('cfYear').addEventListener('change', function () { msg(true, ''); load(); });

    // Laddas lat när ekonomisidans rälspost öppnas.
    var loadedOnce = false;
    window.hpskLoadClubFees = function () { loadedOnce = true; return load(); };
    // Avgiftsreglerna ändrade något: listan läser om bara om den redan visats (annars laddas den när rälsposten öppnas).
    window.hpskRefreshClubFees = function () { if (loadedOnce) load(); };
})();
