/*
 * Tävlingsavgifterna i den nya modellen (P3/P4) — EN modul för alla ytor.
 *
 * Skyttens betalsteg (anmälan, Min sida, tävlingssidan), lagets avgift, och arrangörens
 * avprickning med fakturorna till klubbar. Arrangörens lista är SAMMA på tävlingens
 * Anmälningar-flik och i Ekonomi (Stefans beslut 2026-09-24) — därför en modul, inte två kopior.
 *
 * ⚠️ Inga alert()/confirm()/prompt(): de blockerar sidan och all automatisering. Allt sker inline.
 * ⚠️ Datum skrivs ÅÅÅÅ-MM-DD med flatpickr (sv), aldrig <input type="date">.
 * ⚠️ En påstådd betalning är INTE pengar — ytan skiljer "Betalning anmäld" från "Betald" överallt.
 */
window.HpskFees = (function () {
    'use strict';

    // ── Grundverktyg ─────────────────────────────────────────────────────────────────────
    let tokenPromise = null;
    function token() {
        if (!tokenPromise) {
            // Sidan kan sakna en Umbraco-form — token hämtas separat, annars blir varje POST en tom 400.
            const inPage = document.querySelector('input[name="__RequestVerificationToken"]');
            tokenPromise = inPage && inPage.value
                ? Promise.resolve(inPage.value)
                : fetch('/umbraco/surface/Club/GetFormToken', { credentials: 'same-origin' })
                    .then(r => r.json()).then(d => d.token);
        }
        return tokenPromise;
    }
    async function post(action, body) {
        const t = await token();
        const r = await fetch('/umbraco/surface/CompetitionFee/' + action, {
            method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': t },
            body: JSON.stringify(body || {})
        });
        try { return await r.json(); } catch (e) { return { success: false, message: 'Oväntat svar från servern (' + r.status + ').' }; }
    }
    async function get(action, params) {
        const q = new URLSearchParams(params || {});
        const r = await fetch('/umbraco/surface/CompetitionFee/' + action + '?' + q.toString(), { credentials: 'same-origin' });
        try { return await r.json(); } catch (e) { return { success: false, message: 'Oväntat svar från servern (' + r.status + ').' }; }
    }
    const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const kr = v => (Number(v) || 0).toLocaleString('sv-SE', { minimumFractionDigits: 0, maximumFractionDigits: 2 }) + ' kr';
    const MONTHS = ['jan', 'feb', 'mar', 'apr', 'maj', 'jun', 'jul', 'aug', 'sep', 'okt', 'nov', 'dec'];
    function shortDate(iso) {
        if (!iso) return '';
        const d = new Date(iso);
        if (isNaN(d)) return String(iso).slice(0, 10);
        return d.getDate() + ' ' + MONTHS[d.getMonth()];
    }
    function today() { const d = new Date(); return d.toISOString().slice(0, 10); }
    function datePicker(input) {
        if (window.flatpickr) {
            window.flatpickr(input, { locale: (window.flatpickr.l10ns && window.flatpickr.l10ns.sv) || 'default', dateFormat: 'Y-m-d', allowInput: true });
        } else {
            input.placeholder = 'ÅÅÅÅ-MM-DD';
        }
    }
    function msg(el, text, ok) {
        if (!el) return;
        el.className = 'hf-msg small mt-2 ' + (ok ? 'text-success' : 'text-danger');
        el.textContent = text || '';
    }
    const STATUS_CLASS = {
        'no-fee': 'text-bg-secondary', 'unpaid': 'text-bg-warning', 'claimed': 'text-bg-info',
        'awaiting-invoice': 'text-bg-light border', 'invoiced': 'text-bg-primary', 'paid': 'text-bg-success',
        'overpaid': 'text-bg-danger'
    };
    const badge = s => '<span class="badge ' + (STATUS_CLASS[s.key] || 'text-bg-secondary') + '" data-status="' + esc(s.key) + '">' + esc(s.label) + '</span>';
    const METHODS = [['swish', 'Swish'], ['bankgiro', 'Bankgiro'], ['kontant', 'Kontant'], ['kort', 'Kort'], ['annat', 'Annat']];
    const methodSelect = (sel) => '<select class="form-select form-select-sm hf-method">'
        + METHODS.map(m => '<option value="' + m[0] + '"' + (m[0] === sel ? ' selected' : '') + '>' + m[1] + '</option>').join('') + '</select>';

    function modal(title, bodyHtml, footHtml, size) {
        const wrap = document.createElement('div');
        wrap.className = 'modal fade';
        wrap.tabIndex = -1;
        wrap.innerHTML = '<div class="modal-dialog modal-dialog-scrollable ' + (size || '') + '"><div class="modal-content">'
            + '<div class="modal-header"><h5 class="modal-title">' + esc(title) + '</h5>'
            + '<button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Stäng"></button></div>'
            + '<div class="modal-body">' + bodyHtml + '</div>'
            + '<div class="modal-footer">' + (footHtml || '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Stäng</button>') + '</div>'
            + '</div></div>';
        document.body.appendChild(wrap);
        const m = bootstrap.Modal.getOrCreateInstance(wrap);
        wrap.addEventListener('hidden.bs.modal', () => wrap.remove());
        m.show();
        return { el: wrap, body: wrap.querySelector('.modal-body'), hide: () => m.hide() };
    }

    // ═════════════════════════════════ BETALAREN ══════════════════════════════════════════

    /** Betalningsuppgifterna för EN avgiftsrad: Swish-QR, bankgiro, referens, "Jag har betalat". */
    async function renderPaymentBlock(el, competitionId, paymentId, onChange) {
        el.innerHTML = '<div class="text-body-secondary small">Hämtar betalningsuppgifter…</div>';
        const d = await get('GetPaymentDetails', { competitionId, paymentId });
        if (!d.success) { el.innerHTML = '<div class="text-danger small">' + esc(d.message) + '</div>'; return; }
        const p = d.payment;
        const claimed = !!p.claimed;
        const isMobile = /Android|iPhone|iPad/i.test(navigator.userAgent);
        let html = '<div class="hf-pay border rounded p-3" data-payment-id="' + p.id + '">'
            + '<div class="d-flex justify-content-between align-items-baseline flex-wrap gap-2">'
            + '<div><div class="small text-body-secondary">Att betala</div><div class="fs-4 fw-semibold" data-amount>' + kr(d.amount) + '</div></div>'
            + '<div class="text-end small"><div class="text-body-secondary">Meddelande / referens</div><div class="fw-semibold font-monospace" data-reference>' + esc(p.reference) + '</div></div>'
            + '</div>';
        if (claimed) {
            html += '<div class="alert alert-info mt-3 mb-0 py-2 small">Du har anmält att du betalat. Kvittot kommer när arrangören tagit emot betalningen.</div>';
        } else {
            html += '<div class="row g-3 mt-1">';
            if (d.swishNumber) {
                html += '<div class="col-sm-6 text-center"><div class="small fw-semibold mb-1">Swish ' + esc(d.swishNumber) + '</div>'
                    + '<img src="' + esc(d.qrUrl) + '" alt="QR-kod för Swish" width="180" height="180" class="img-fluid" />'
                    + (isMobile && d.swishAppUrl ? '<div class="mt-2"><a class="btn btn-success btn-sm" href="' + esc(d.swishAppUrl) + '">Öppna Swish</a></div>' : '')
                    + '</div>';
            }
            if (d.bgNumber) {
                html += '<div class="col-sm-6"><div class="small fw-semibold mb-1">Bankgiro ' + esc(d.bgNumber) + '</div>'
                    + '<div class="small">Mottagare: ' + esc(d.payeeName) + '<br>Referens: <span class="font-monospace">' + esc(d.bgReference) + '</span></div>'
                    + (d.bgQrCodeBase64 ? '<img src="data:image/png;base64,' + d.bgQrCodeBase64 + '" alt="QR-kod för bankappen" width="140" height="140" class="mt-2" />' : '')
                    + '</div>';
            }
            if (!d.swishNumber && !d.bgNumber) {
                html += '<div class="col-12"><div class="alert alert-warning small mb-0">Arrangören har varken Swish eller bankgiro angivet. Kontakta arrangören för att betala.</div></div>';
            }
            html += '</div>'
                + '<div class="small text-body-secondary mt-2">Skriv referensen exakt — det är så arrangören ser vilken anmälan betalningen gäller.</div>'
                + '<div class="d-flex gap-2 flex-wrap mt-3">'
                + '<button type="button" class="btn btn-primary btn-sm hf-claim">Jag har betalat</button>'
                + '<button type="button" class="btn btn-outline-secondary btn-sm hf-mail">Mejla uppgifterna</button>'
                + '</div><div class="hf-msg"></div>';
        }
        html += '</div>';
        el.innerHTML = html;

        const m = el.querySelector('.hf-msg');
        const claim = el.querySelector('.hf-claim');
        if (claim) claim.addEventListener('click', async () => {
            claim.disabled = true;
            const r = await post('ClaimPayment', { competitionId, paymentId });
            msg(m, r.message, r.success);
            if (r.success) { if (onChange) onChange(); } else claim.disabled = false;
        });
        const mail = el.querySelector('.hf-mail');
        if (mail) mail.addEventListener('click', async () => {
            mail.disabled = true;
            const r = await post('EmailPaymentCode', { competitionId, paymentId });
            msg(m, r.message, r.success);
            mail.disabled = false;
        });
    }

    /**
     * Skyttens betalsteg för en tävling: varje anmälan med läge, det som ska betalas, och
     * "Klubben betalar" när arrangören tillåtit det för anmälans klasser.
     */
    async function renderPayStep(el, opts) {
        const competitionId = opts.competitionId;
        const params = { competitionId };
        if (opts.memberId) params.memberId = opts.memberId;
        el.innerHTML = '<div class="text-body-secondary small">Hämtar avgiften…</div>';
        const d = await get('GetMyFees', params);
        if (!d.success) { el.innerHTML = '<div class="text-danger small">' + esc(d.message) + '</div>'; return d; }
        if (d.model !== 'ledger') { el.innerHTML = ''; return d; }

        const rerender = () => renderPayStep(el, opts).then(r => { if (opts.onChange) opts.onChange(r); });
        if (!d.registrations.length) { el.innerHTML = '<div class="text-body-secondary small">Ingen anmälan hittades.</div>'; return d; }

        el.innerHTML = d.registrations.map(r => {
            let h = '<div class="hf-reg mb-3" data-registration-id="' + r.registrationId + '">'
                + '<div class="d-flex justify-content-between align-items-center flex-wrap gap-2 mb-2">'
                + '<div><strong>' + esc(r.memberName) + '</strong> <span class="text-body-secondary small">' + esc((r.classes || []).join(', ')) + '</span></div>'
                + badge(r.status) + '</div>';
            if (r.canChooseClubPays) {
                h += '<div class="form-check mb-2"><input class="form-check-input hf-clubpays" type="checkbox" id="hfcp' + r.registrationId + '"' + (r.clubPays ? ' checked' : '') + '>'
                    + '<label class="form-check-label" for="hfcp' + r.registrationId + '">Klubben betalar för de klasser arrangören tillåtit'
                    + (r.clubName ? ' (' + esc(r.clubName) + ')' : '') + '</label>'
                    + '<div class="form-text">Arrangören skickar en faktura till klubben. Övriga klasser betalar du själv nu.</div></div>';
            }
            const clubPart = (r.clubPart || []).reduce((s, x) => s + x.amount, 0);
            if (clubPart > 0) h += '<div class="small text-body-secondary mb-2">Klubbens del, ' + kr(clubPart) + ', faktureras klubben av arrangören.</div>';
            (r.toPay || []).forEach(p => { h += '<div class="hf-payblock" data-pay="' + p.id + '"></div>'; });
            if (!(r.toPay || []).length && r.status.key === 'paid') {
                h += '<div class="small text-success">Betald.</div>';
            }
            (r.receipts || []).forEach(k => { h += '<div class="small"><a href="/betalkvitto/' + k.id + '" target="_blank">Kvitto (' + kr(k.amount) + ')</a></div>'; });
            h += '<div class="hf-msg"></div></div>';
            return h;
        }).join('');

        d.registrations.forEach(r => {
            const regEl = el.querySelector('[data-registration-id="' + r.registrationId + '"]');
            (r.toPay || []).forEach(p => renderPaymentBlock(regEl.querySelector('[data-pay="' + p.id + '"]'), competitionId, p.id, rerender));
            const cb = regEl.querySelector('.hf-clubpays');
            if (cb) cb.addEventListener('change', async () => {
                cb.disabled = true;
                const res = await post('SetClubPays', { competitionId, registrationId: r.registrationId, clubPays: cb.checked });
                if (!res.success) { msg(regEl.querySelector('.hf-msg'), res.message, false); cb.checked = !cb.checked; cb.disabled = false; return; }
                rerender();
            });
        });
        return d;
    }

    function openPayModal(competitionId, memberId, onClose) {
        const m = modal('Betala anmälan', '<div class="hf-paystep"></div>', null, 'modal-lg');
        renderPayStep(m.body.querySelector('.hf-paystep'), { competitionId, memberId });
        if (onClose) m.el.addEventListener('hidden.bs.modal', onClose);
        return m;
    }

    /** Lagets avgift: betala direkt, eller "faktureras klubben" när arrangören tillåtit lag. */
    async function renderTeamPay(el, competitionId, teamId) {
        el.innerHTML = '<div class="text-body-secondary small">Hämtar lagavgiften…</div>';
        const d = await get('GetTeamFee', { competitionId, teamId });
        if (!d.success) { el.innerHTML = '<div class="text-danger small">' + esc(d.message) + '</div>'; return d; }
        if (d.model !== 'ledger') { el.innerHTML = ''; return d; }
        let h = '<div class="d-flex justify-content-between align-items-center mb-2"><strong>Lagavgift</strong>' + badge(d.status) + '</div>';
        const later = (d.invoiceLater || []).reduce((s, x) => s + x.amount, 0);
        if (later > 0) h += '<div class="small">Lagavgiften, ' + kr(later) + ', faktureras klubben av arrangören. Ni behöver inte betala nu.</div>';
        (d.toPay || []).forEach(p => { h += '<div class="hf-payblock" data-pay="' + p.id + '"></div>'; });
        el.innerHTML = h;
        (d.toPay || []).forEach(p => renderPaymentBlock(el.querySelector('[data-pay="' + p.id + '"]'), competitionId, p.id, () => renderTeamPay(el, competitionId, teamId)));
        return d;
    }

    // ═════════════════════════════════ ARRANGÖREN ═════════════════════════════════════════

    /**
     * Arrangörens avprickning: sammanfattning, val av vad klubben får betala för, det som väntar
     * på en handling, alla avgifter och tävlingens fakturor.
     */
    async function renderOrganiserPanel(el, competitionId, opts) {
        opts = opts || {};
        el.innerHTML = '<div class="text-body-secondary small">Hämtar avgifterna…</div>';
        const d = await get('GetOverview', { competitionId });
        if (!d.success) { el.innerHTML = '<div class="text-danger small">' + esc(d.message) + '</div>'; return d; }
        if (d.model !== 'ledger') {
            el.innerHTML = '<div class="alert alert-secondary small mb-0">Tävlingen använder det gamla fakturasättet. Fakturorna sköts under "Fakturor enligt det gamla sättet".</div>';
            return d;
        }
        // Varje ändring laddar om panelen OCH säger till värdsidan (Anmälningar-tabellen, Ekonomi),
        // så att samma betalning inte står som obetald i tabellen bredvid.
        const reload = () => { renderOrganiserPanel(el, competitionId, opts); if (opts.onChange) opts.onChange(); };
        const t = d.totals;
        const claimedRows = [];
        d.items.forEach(i => i.rows.forEach(r => { if (r.claimed && !r.confirmed && !r.voided) claimedRows.push({ item: i, row: r }); }));
        d.invoices.forEach(v => (v.payments || []).forEach(p => { if (p.claimed && !p.confirmed) claimedRows.push({ invoice: v, payment: p }); }));

        let h = '<div class="hf-org" data-competition-id="' + competitionId + '">';

        // Sammanfattning — noll skrivs som "Inget", aldrig en nolla i en beloppsruta.
        const tile = (label, v, key) => '<div class="col-6 col-md"><div class="border rounded p-2 h-100" data-total="' + key + '"><div class="small text-body-secondary">' + label + '</div><div class="fw-semibold">' + (v > 0 ? kr(v) : 'Inget') + '</div></div></div>';
        h += '<div class="row g-2 mb-3">' + tile('Avgifter', t.fee, 'fee') + tile('Mottaget', t.paid, 'paid')
            + tile('Anmält betalt', t.claimed, 'claimed') + tile('Fakturerat, obetalt', t.invoiced, 'invoiced') + tile('Obetalt', t.open, 'open') + '</div>';

        // Att stämma av
        h += '<div class="card mb-3"><div class="card-header fw-semibold">Att stämma av mot Swish och banken'
            + (claimedRows.length ? ' <span class="badge text-bg-info">' + claimedRows.length + '</span>' : '') + '</div><div class="card-body p-0">';
        if (!claimedRows.length) {
            h += '<div class="p-3 small text-body-secondary">Inget väntar — ingen har anmält en betalning som inte är avstämd.</div>';
        } else {
            h += '<table class="table table-sm mb-0 align-middle"><tbody>' + claimedRows.map(c => {
                if (c.row) return '<tr data-claim-payment="' + c.row.id + '"><td>' + esc(c.item.name) + '<div class="small text-body-secondary">Anmäld ' + shortDate(c.row.claimed) + ' · ref ' + esc(c.row.reference) + '</div></td>'
                    + '<td class="text-end">' + kr(c.row.amount) + '</td>'
                    + '<td class="text-end"><button type="button" class="btn btn-success btn-sm hf-confirm" data-payment="' + c.row.id + '">Mottaget</button></td></tr>';
                return '<tr data-claim-invoice="' + c.invoice.id + '"><td>Faktura ' + esc(c.invoice.number) + ' — ' + esc(c.invoice.recipientName)
                    + '<div class="small text-body-secondary">Anmäld ' + shortDate(c.payment.claimed) + ' · ref ' + esc(c.invoice.reference) + '</div></td>'
                    + '<td class="text-end">' + kr(c.payment.amount) + '</td>'
                    + '<td class="text-end"><button type="button" class="btn btn-success btn-sm hf-inv-pay" data-invoice="' + c.invoice.id + '">Mottaget</button></td></tr>';
            }).join('') + '</tbody></table>';
        }
        h += '<div class="hf-msg px-3 pb-2"></div></div></div>';

        // Val: vad får klubben betala för?
        const types = d.settings.clubPayableTypes || [];
        h += '<div class="card mb-3"><div class="card-header fw-semibold">Får klubben betala i stället för skytten?</div><div class="card-body">'
            + '<div class="small text-body-secondary mb-2">För det du inte kryssar i betalar skytten direkt vid anmälan. För det du kryssar i kan skytten välja "Klubben betalar", och du skickar sedan en faktura till klubben.</div>'
            + '<div class="d-flex flex-wrap gap-3">' + d.settings.allTypes.map(x =>
                '<div class="form-check"><input class="form-check-input hf-type" type="checkbox" value="' + x.key + '" id="hft' + competitionId + x.key + '"' + (types.indexOf(x.key) >= 0 ? ' checked' : '') + '>'
                + '<label class="form-check-label" for="hft' + competitionId + x.key + '">' + esc(x.label) + '</label></div>').join('') + '</div>'
            + '<button type="button" class="btn btn-outline-primary btn-sm mt-2 hf-save-types">Spara</button><div class="hf-msg"></div></div></div>';

        // Alla avgifter
        const filterAll = opts.filter || 'all';
        const unpaidCount = d.items.filter(i => i.itemType === 'competition-registration' && i.status.key === 'unpaid').length;
        h += '<div class="card mb-3"><div class="card-header d-flex justify-content-between align-items-center flex-wrap gap-2">'
            + '<span class="fw-semibold">Avgifter per anmälan och lag</span>'
            + '<div class="d-flex gap-2 flex-wrap">'
            + '<select class="form-select form-select-sm w-auto hf-filter"><option value="all">Alla</option><option value="todo"' + (filterAll === 'todo' ? ' selected' : '') + '>Bara det som väntar</option></select>'
            + (unpaidCount > 0 ? '<button type="button" class="btn btn-outline-warning btn-sm hf-remind">Påminn obetalda (' + unpaidCount + ')</button>' : '')
            + '<button type="button" class="btn btn-outline-secondary btn-sm hf-resync">Räkna om avgifterna</button>'
            + '<button type="button" class="btn btn-primary btn-sm hf-invoice-club">Fakturera en klubb</button></div></div>'
            + '<div class="table-responsive"><table class="table table-sm mb-0 align-middle"><thead><tr><th>Namn</th><th>Klubb</th><th>Läge</th><th class="text-end">Kvar</th><th></th></tr></thead><tbody>';
        const items = filterAll === 'todo' ? d.items.filter(i => ['unpaid', 'claimed', 'awaiting-invoice', 'overpaid'].indexOf(i.status.key) >= 0) : d.items;
        h += items.map(i => {
            const left = i.status.open + i.status.awaitingInvoice;
            const confirmed = i.rows.filter(r => r.confirmed && !r.voided);
            return '<tr data-item="' + i.itemType + ':' + i.itemId + '"' + (left > 0 ? ' class="hf-gap"' : '') + '>'
                + '<td>' + esc(i.name) + (i.itemType === 'team-fee' ? ' <span class="badge text-bg-light border">Lag</span>' : '')
                + (i.clubPaysHint ? ' <span class="badge text-bg-light border" title="Skytten valde Klubben betalar">Klubben betalar</span>' : '') + '</td>'
                + '<td class="small">' + esc(i.clubName) + '</td>'
                + '<td>' + badge(i.status) + '</td>'
                + '<td class="text-end">' + (left > 0 ? kr(left) : 'Inget') + '</td>'
                + '<td class="text-end text-nowrap">'
                + (i.status.open > 0 || i.status.claimed > 0 || i.status.awaitingInvoice > 0 ? '<button type="button" class="btn btn-outline-success btn-sm hf-receive" data-type="' + i.itemType + '" data-id="' + i.itemId + '">Ta emot betalning</button> ' : '')
                + (confirmed.length ? '<button type="button" class="btn btn-outline-secondary btn-sm hf-reverse" data-payment="' + confirmed[0].id + '" data-name="' + esc(i.name) + '">Ångra</button>' : '')
                + '</td></tr>';
        }).join('') || '<tr><td colspan="5" class="small text-body-secondary p-3">Inga avgifter.</td></tr>';
        h += '</tbody></table></div><div class="hf-msg px-3 pb-2"></div></div>';

        // Fakturor
        h += renderInvoiceList(d.invoices, 'Fakturor till klubbar');
        h += '</div>';
        el.innerHTML = h;

        const root = el.querySelector('.hf-org');
        // Senaste svaret på roten — kreditdialogen läser fakturans rader härifrån.
        root._data = d;
        root.querySelectorAll('.hf-confirm').forEach(b => b.addEventListener('click', async () => {
            b.disabled = true;
            const r = await post('ConfirmPayment', { competitionId, paymentId: +b.dataset.payment });
            msg(b.closest('.card').querySelector('.hf-msg'), r.message, r.success);
            if (r.success) setTimeout(reload, 900); else b.disabled = false;
        }));
        root.querySelector('.hf-save-types').addEventListener('click', async (e) => {
            const types2 = Array.from(root.querySelectorAll('.hf-type:checked')).map(x => x.value);
            const r = await post('SaveSettings', { competitionId, types: types2 });
            msg(e.target.closest('.card-body').querySelector('.hf-msg'), r.message, r.success);
        });
        root.querySelector('.hf-filter').addEventListener('change', (e) => { opts.filter = e.target.value; reload(); });
        root.querySelector('.hf-resync').addEventListener('click', async (e) => {
            e.target.disabled = true;
            const r = await post('Resync', { competitionId });
            msg(e.target.closest('.card').querySelector('.hf-msg'), r.message, r.success);
            setTimeout(reload, 900);
        });
        root.querySelector('.hf-invoice-club').addEventListener('click', () => openInvoiceClub(competitionId, reload));
        const remind = root.querySelector('.hf-remind');
        if (remind) remind.addEventListener('click', async () => {
            remind.disabled = true;
            const r = await post('SendReminders', { competitionId });
            msg(remind.closest('.card').querySelector('.hf-msg'), r.message, r.success);
            remind.disabled = false;
        });
        root.querySelectorAll('.hf-receive').forEach(b => b.addEventListener('click', () =>
            openReceivePayment(competitionId, b.dataset.type, +b.dataset.id, reload)));
        root.querySelectorAll('.hf-reverse').forEach(b => b.addEventListener('click', () =>
            openReverse(competitionId, +b.dataset.payment, b.dataset.name, reload)));
        wireInvoiceList(root, reload);
        return d;
    }

    function renderInvoiceList(invoices, title) {
        let h = '<div class="card mb-3 hf-invoices"><div class="card-header fw-semibold">' + esc(title) + '</div>';
        if (!invoices.length) return h + '<div class="card-body small text-body-secondary">Inga fakturor än.</div></div>';
        h += '<div class="table-responsive"><table class="table table-sm mb-0 align-middle"><thead><tr><th>Faktura</th><th>Klubb</th><th>Förfaller</th><th class="text-end">Belopp</th><th class="text-end">Kvar</th><th>Läge</th><th></th></tr></thead><tbody>';
        h += invoices.map(v => {
            const state = v.voided ? '<span class="badge text-bg-secondary">Makulerad</span>'
                : v.settled ? '<span class="badge text-bg-success">Betald</span>'
                : v.claimed > 0 ? '<span class="badge text-bg-info">Betalning anmäld</span>'
                : v.overdue ? '<span class="badge text-bg-danger">Förfallen</span>'
                : '<span class="badge text-bg-warning">Obetald</span>';
            const credits = (v.credits || []).map(c => '<div class="small"><a href="' + esc(c.documentUrl) + '" target="_blank">Kreditnota ' + esc(c.number) + '</a> ' + kr(c.amount) + '</div>').join('');
            return '<tr data-invoice-id="' + v.id + '"' + (v.voided ? ' class="text-body-secondary"' : '') + '>'
                + '<td><a href="' + esc(v.documentUrl) + '" target="_blank">' + esc(v.number) + '</a><div class="small text-body-secondary">' + esc(v.competitionName) + ' · ' + esc(v.reference) + '</div>' + credits + '</td>'
                + '<td>' + esc(v.recipientName) + (v.sentUtc ? '<div class="small text-body-secondary">Skickad ' + shortDate(v.sentUtc) + '</div>' : '<div class="small text-warning">Inte skickad</div>') + '</td>'
                + '<td class="small">' + esc(v.dueDate || '') + '</td>'
                + '<td class="text-end">' + kr(v.amount) + '</td>'
                + '<td class="text-end">' + (v.voided ? '—' : v.outstanding > 0 ? kr(v.outstanding) : 'Inget') + '</td>'
                + '<td>' + state + '</td>'
                + '<td class="text-end">' + (v.voided ? '' : '<div class="dropdown"><button class="btn btn-outline-secondary btn-sm dropdown-toggle" data-bs-toggle="dropdown" data-bs-boundary="viewport" type="button">Åtgärder</button><ul class="dropdown-menu dropdown-menu-end">'
                    + (v.outstanding > 0 ? '<li><button class="dropdown-item hf-inv-pay" data-invoice="' + v.id + '">Registrera betalning</button></li>' : '')
                    + '<li><button class="dropdown-item hf-inv-send" data-invoice="' + v.id + '">Skicka' + (v.sentUtc ? ' igen' : '') + '</button></li>'
                    + '<li><button class="dropdown-item hf-inv-credit" data-invoice="' + v.id + '">Kreditera</button></li>'
                    + (v.paid <= 0 && v.claimed <= 0 && v.credited >= 0 ? '<li><button class="dropdown-item text-danger hf-inv-void" data-invoice="' + v.id + '">Makulera</button></li>' : '')
                    + '</ul></div>') + '</td></tr>';
        }).join('');
        return h + '</tbody></table></div></div>';
    }

    function wireInvoiceList(root, reload) {
        root.querySelectorAll('.hf-inv-pay').forEach(b => b.addEventListener('click', () => openInvoicePayment(+b.dataset.invoice, root, reload)));
        root.querySelectorAll('.hf-inv-send').forEach(b => b.addEventListener('click', () => openInvoiceSend(+b.dataset.invoice, root, reload)));
        root.querySelectorAll('.hf-inv-credit').forEach(b => b.addEventListener('click', () => openInvoiceCredit(+b.dataset.invoice, root, reload)));
        root.querySelectorAll('.hf-inv-void').forEach(b => b.addEventListener('click', () => openInvoiceVoid(+b.dataset.invoice, root, reload)));
    }

    function invoiceFromRow(root, id) {
        const tr = root.querySelector('[data-invoice-id="' + id + '"]');
        return tr ? { number: tr.querySelector('a').textContent, recipient: tr.children[1].childNodes[0].textContent } : { number: '', recipient: '' };
    }

    function openInvoicePayment(invoiceId, root, reload) {
        const info = invoiceFromRow(root, invoiceId);
        const m = modal('Betalning på faktura ' + info.number,
            '<div class="mb-2 small">Från ' + esc(info.recipient) + '. Registrera det belopp som kommit in.</div>'
            + '<div class="row g-2"><div class="col-sm-4"><label class="form-label small">Belopp (kr)</label><input type="number" min="0" step="0.01" class="form-control form-control-sm hf-amount" placeholder="Hela saldot"></div>'
            + '<div class="col-sm-4"><label class="form-label small">Betaldatum</label><input type="text" class="form-control form-control-sm hf-date" value="' + today() + '"></div>'
            + '<div class="col-sm-4"><label class="form-label small">Betalsätt</label>' + methodSelect('bankgiro') + '</div></div><div class="hf-msg"></div>',
            '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Avbryt</button><button type="button" class="btn btn-success hf-go">Registrera</button>');
        datePicker(m.body.querySelector('.hf-date'));
        m.el.querySelector('.hf-go').addEventListener('click', async (e) => {
            e.target.disabled = true;
            const a = m.body.querySelector('.hf-amount').value;
            const r = await post('RegisterInvoicePayment', {
                chargeId: invoiceId, actualAmount: a ? Number(a) : null,
                paymentDate: m.body.querySelector('.hf-date').value, method: m.body.querySelector('.hf-method').value
            });
            msg(m.body.querySelector('.hf-msg'), r.message, r.success);
            if (r.success) { setTimeout(() => { m.hide(); reload(); }, 900); } else e.target.disabled = false;
        });
    }

    function openInvoiceSend(invoiceId, root, reload) {
        const info = invoiceFromRow(root, invoiceId);
        const m = modal('Skicka faktura ' + info.number,
            '<label class="form-label small">E-post till ' + esc(info.recipient) + '</label><input type="email" class="form-control hf-email" placeholder="kassor@klubben.se"><div class="hf-msg"></div>',
            '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Avbryt</button><button type="button" class="btn btn-primary hf-go">Skicka</button>');
        m.el.querySelector('.hf-go').addEventListener('click', async (e) => {
            e.target.disabled = true;
            const r = await post('SendInvoice', { chargeId: invoiceId, email: m.body.querySelector('.hf-email').value });
            msg(m.body.querySelector('.hf-msg'), r.message, r.success);
            if (r.success) setTimeout(() => { m.hide(); reload(); }, 900); else e.target.disabled = false;
        });
    }

    async function openInvoiceCredit(invoiceId, root, reload) {
        const info = invoiceFromRow(root, invoiceId);
        const inv = (root._data && root._data.invoices || []).find(v => v.id === invoiceId);
        const lines = inv ? inv.lines : [];
        const m = modal('Kreditera faktura ' + info.number,
            '<div class="small mb-2">Välj raderna som krediteras. En skytt som finns kvar får en ny öppen avgift att betala själv.</div>'
            + lines.map(l => '<div class="form-check"><input class="form-check-input hf-line" type="checkbox" value="' + l.id + '" id="hfl' + l.id + '"><label class="form-check-label" for="hfl' + l.id + '">' + esc(l.description) + ' — ' + kr(l.amount) + '</label></div>').join('')
            + '<label class="form-label small mt-2">Skäl (syns på kreditnotan)</label><input type="text" class="form-control hf-reason"><div class="hf-msg"></div>',
            '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Avbryt</button><button type="button" class="btn btn-danger hf-go">Skapa kreditnota</button>');
        m.el.querySelector('.hf-go').addEventListener('click', async (e) => {
            e.target.disabled = true;
            const ids = Array.from(m.body.querySelectorAll('.hf-line:checked')).map(x => +x.value);
            const r = await post('CreditInvoice', { chargeId: invoiceId, lineIds: ids, reason: m.body.querySelector('.hf-reason').value });
            msg(m.body.querySelector('.hf-msg'), r.message, r.success);
            if (r.success) setTimeout(() => { m.hide(); reload(); }, 1200); else e.target.disabled = false;
        });
    }

    function openInvoiceVoid(invoiceId, root, reload) {
        const info = invoiceFromRow(root, invoiceId);
        const m = modal('Makulera faktura ' + info.number,
            '<div class="small mb-2">En obetald faktura makuleras — den ligger kvar som handling, men ska inte betalas. Avgifterna blir öppna igen.</div>'
            + '<label class="form-label small">Skäl</label><input type="text" class="form-control hf-reason"><div class="hf-msg"></div>',
            '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Avbryt</button><button type="button" class="btn btn-danger hf-go">Makulera</button>');
        m.el.querySelector('.hf-go').addEventListener('click', async (e) => {
            e.target.disabled = true;
            const r = await post('VoidInvoice', { chargeId: invoiceId, reason: m.body.querySelector('.hf-reason').value });
            msg(m.body.querySelector('.hf-msg'), r.message, r.success);
            if (r.success) setTimeout(() => { m.hide(); reload(); }, 900); else e.target.disabled = false;
        });
    }

    /**
     * Arrangörens dialog för EN anmälan eller ett lag: betalningsuppgifterna att visa skytten vid
     * disken, och "Ta emot betalning" när pengarna kommit.
     */
    function openItemModal(competitionId, itemType, itemId, memberId, title, onDone) {
        const m = modal(title || 'Betalning', '<div class="hf-item"></div>',
            '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Stäng</button>'
            + '<button type="button" class="btn btn-success hf-receive-now">Ta emot betalning</button>', 'modal-lg');
        const box = m.body.querySelector('.hf-item');
        if (itemType === 'team-fee') renderTeamPay(box, competitionId, itemId);
        else renderPayStep(box, { competitionId, memberId, onChange: () => { if (onDone) onDone(); } });
        m.el.querySelector('.hf-receive-now').addEventListener('click', () => {
            m.hide();
            openReceivePayment(competitionId, itemType, itemId, onDone);
        });
        return m;
    }

    /** Arrangören tar emot en betalning på plats för en anmälan eller ett lag. */
    function openReceivePayment(competitionId, itemType, itemId, onDone) {
        const m = modal('Ta emot betalning',
            '<div class="row g-2"><div class="col-sm-6"><label class="form-label small">Belopp (kr)</label><input type="number" min="0" step="0.01" class="form-control form-control-sm hf-amount" placeholder="Hela det obetalda"></div>'
            + '<div class="col-sm-6"><label class="form-label small">Betalsätt</label>' + methodSelect('swish') + '</div></div>'
            + '<div class="small text-body-secondary mt-2">Kvitto utfärdas, och betalningen bokförs om föreningen bokför i pistol.nu.</div><div class="hf-msg"></div>',
            '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Avbryt</button><button type="button" class="btn btn-success hf-go">Mottaget</button>');
        m.el.querySelector('.hf-go').addEventListener('click', async (e) => {
            e.target.disabled = true;
            const a = m.body.querySelector('.hf-amount').value;
            const r = await post('RegisterPayment', { competitionId, itemType, itemId, actualAmount: a ? Number(a) : null, method: m.body.querySelector('.hf-method').value });
            msg(m.body.querySelector('.hf-msg'), r.message, r.success);
            if (r.success) setTimeout(() => { m.hide(); if (onDone) onDone(r); }, 900); else e.target.disabled = false;
        });
        return m;
    }

    function openReverse(competitionId, paymentId, name, onDone) {
        const m = modal('Ångra betalning — ' + name,
            '<div class="small mb-2">En mottagen betalning återtas; har den bokförts skrivs en rättelse. Kvittot ligger kvar hos betalaren. Avgiften blir obetald igen.</div>'
            + '<label class="form-label small">Skäl (obligatoriskt)</label><input type="text" class="form-control hf-reason"><div class="hf-msg"></div>',
            '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Avbryt</button><button type="button" class="btn btn-danger hf-go">Ångra betalningen</button>');
        m.el.querySelector('.hf-go').addEventListener('click', async (e) => {
            e.target.disabled = true;
            const r = await post('ReversePayment', { competitionId, paymentId, reason: m.body.querySelector('.hf-reason').value });
            msg(m.body.querySelector('.hf-msg'), r.message, r.success);
            if (r.success) setTimeout(() => { m.hide(); if (onDone) onDone(); }, 1200); else e.target.disabled = false;
        });
    }

    /** Fakturera EN klubb: välj klubb, bocka avgifter, Skapa och skicka. Ett steg. */
    async function openInvoiceClub(competitionId, onDone) {
        const m = modal('Fakturera en klubb', '<div class="text-body-secondary small">Hämtar klubbar med öppna avgifter…</div>', null, 'modal-lg');
        const d = await get('GetInvoiceCandidates', { competitionId });
        if (!d.success) { m.body.innerHTML = '<div class="text-danger small">' + esc(d.message) + '</div>'; return; }
        if (!d.clubs.length) { m.body.innerHTML = '<div class="small">Det finns inga öppna avgifter att fakturera. (Anmälda betalningar räknas inte — någon säger att de redan betalat.)</div>'; return; }

        m.body.innerHTML = '<label class="form-label small">Klubb</label><select class="form-select hf-club">'
            + d.clubs.map(c => '<option value="' + c.clubId + '">' + esc(c.clubName) + ' — ' + c.rows.length + ' avgifter, ' + kr(c.total) + '</option>').join('')
            + '</select><div class="hf-rows mt-3"></div>'
            + '<div class="row g-2 mt-2"><div class="col-sm-6"><label class="form-label small">Skicka till e-post</label><input type="email" class="form-control form-control-sm hf-email"></div>'
            + '<div class="col-sm-6"><label class="form-label small">Förfallodag</label><input type="text" class="form-control form-control-sm hf-due"></div></div>'
            + '<label class="form-label small mt-2">Meddelande på fakturan (valfritt)</label><input type="text" class="form-control form-control-sm hf-note">'
            + '<div class="small text-body-secondary mt-2">Fakturan får ett löpande nummer och kan inte ändras efteråt — den krediteras eller makuleras. Klubben behöver inte logga in: mejlet har en länk till fakturan.</div>'
            + '<div class="hf-msg"></div>';
        m.el.querySelector('.modal-footer').innerHTML = '<button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Avbryt</button>'
            + '<button type="button" class="btn btn-outline-primary hf-create">Skapa utan att skicka</button>'
            + '<button type="button" class="btn btn-primary hf-create-send">Skapa och skicka</button>';
        const due = new Date(); due.setDate(due.getDate() + 30);
        const dueEl = m.body.querySelector('.hf-due'); dueEl.value = due.toISOString().slice(0, 10); datePicker(dueEl);

        const showClub = () => {
            const c = d.clubs.find(x => x.clubId === +m.body.querySelector('.hf-club').value);
            m.body.querySelector('.hf-email').value = c.email || '';
            m.body.querySelector('.hf-rows').innerHTML = '<div class="small fw-semibold mb-1">Avgifter att ta med</div>' + c.rows.map(r =>
                '<div class="form-check"><input class="form-check-input hf-row" type="checkbox" value="' + r.id + '" id="hfr' + r.id + '"' + (r.suggested ? ' checked' : '') + '>'
                + '<label class="form-check-label" for="hfr' + r.id + '">' + esc(r.payerName) + (r.itemType === 'team-fee' ? ' (lag)' : r.part === 'club' ? ' (klubbens del)' : '') + ' — ' + kr(r.amount) + '</label></div>').join('')
                + '<div class="small text-body-secondary mt-1">Förvalt: klubbens delar, lag och de som valt "Klubben betalar".</div>';
        };
        m.body.querySelector('.hf-club').addEventListener('change', showClub);
        showClub();

        const create = async (send, btn) => {
            btn.disabled = true;
            const ids = Array.from(m.body.querySelectorAll('.hf-row:checked')).map(x => +x.value);
            const r = await post('CreateInvoice', {
                competitionId, clubId: +m.body.querySelector('.hf-club').value, paymentIds: ids,
                dueDate: dueEl.value, note: m.body.querySelector('.hf-note').value,
                email: m.body.querySelector('.hf-email').value, send
            });
            msg(m.body.querySelector('.hf-msg'), r.message, r.success);
            if (r.success) {
                m.el.querySelector('.modal-footer').innerHTML = '<a class="btn btn-outline-primary" target="_blank" href="' + esc(r.documentUrl) + '">Öppna fakturan</a><button type="button" class="btn btn-primary" data-bs-dismiss="modal">Klar</button>';
                if (onDone) m.el.addEventListener('hidden.bs.modal', onDone);
            } else btn.disabled = false;
        };
        m.el.querySelector('.hf-create').addEventListener('click', e => create(false, e.target));
        m.el.querySelector('.hf-create-send').addEventListener('click', e => create(true, e.target));
    }

    return {
        renderPayStep, openPayModal, renderPaymentBlock, renderTeamPay,
        renderOrganiserPanel, openReceivePayment, openItemModal, openInvoiceClub,
        renderInvoiceList, wireInvoiceList, get, post, kr, esc
    };
})();
