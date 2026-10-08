/*
 * Medlemmarnas motionssida (2026-10-08) — /motion?klubb=ID eller /motion?krets=ID.
 * Ingen användartext i onclick: knapparna bär motionens id i data-attribut.
 */
(function () {
    'use strict';
    const app = document.getElementById('moApp'); if (!app) return;
    const OT = parseInt(app.dataset.ownerType, 10) || 0, OID = parseInt(app.dataset.ownerId, 10) || 0;
    const $ = id => document.getElementById(id);
    let data = null, formOpen = false;

    function token() { return document.querySelector('input[name="__RequestVerificationToken"]')?.value || ''; }
    function req(url, opts) {
        return fetch(url, opts).then(r => r.text().then(t => {
            let d = null; try { d = t ? JSON.parse(t) : null; } catch (e) { d = null; }
            if (r.ok && d !== null) return d;
            const info = window.hpskRequestFailureMessage ? window.hpskRequestFailureMessage(r.status) : { msg: 'Anropet misslyckades.' };
            const msg = (d && d.message) || info.msg;
            if (window.hpskReportRequestFailure) window.hpskReportRequestFailure(msg, info.reload);
            throw new Error(msg);
        }));
    }
    const get = (a, q) => req('/umbraco/surface/BoardWork/' + a + '?' + new URLSearchParams(q || {}));
    const post = (a, p) => req('/umbraco/surface/BoardWork/' + a, {
        method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': token() },
        body: new URLSearchParams(p).toString()
    });
    function esc(s) { if (s == null) return ''; const m = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }; return String(s).replace(/[&<>"']/g, c => m[c]); }
    function d8(s) { if (!s) return ''; const d = new Date(s.slice(0, 10) + 'T12:00:00'); return isNaN(d) ? s : d.toLocaleDateString('sv-SE', { day: 'numeric', month: 'long', year: 'numeric' }); }
    function msg(text, kind) {
        $('moMsg').innerHTML = text ? `<div class="alert alert-${kind || 'success'} alert-dismissible fade show" role="alert">${text}<button type="button" class="btn-close" data-bs-dismiss="alert" aria-label="Stäng"></button></div>` : '';
        if (text) $('moMsg').scrollIntoView({ block: 'nearest' });
    }
    const stateClass = s => ({ Received: 'wait', OpinionReady: 'blue', OnAgenda: 'blue', Decided: 'ok', Withdrawn: 'off' })[s] || 'wait';

    function load() {
        get('GetMotionPage', { ownerType: OT, ownerId: OID }).then(r => {
            if (!r.success) {
                $('moBody').innerHTML = `<div class="mo-card"><p class="mb-0">${esc(r.message || 'Motionerna kunde inte visas.')}</p></div>`;
                return;
            }
            data = r;
            render();
        }).catch(() => { $('moBody').innerHTML = '<div class="mo-card"><p class="text-danger mb-0">Motionerna kunde inte hämtas.</p></div>'; });
    }

    function render() {
        const r = data, a = r.annualMeeting;
        $('moTitle').textContent = r.isClub ? 'Motioner till årsmötet' : 'Motioner till kretsårsmötet';
        document.title = 'Motioner – ' + r.orgName;
        $('moSub').innerHTML = `<strong>${esc(r.orgName)}</strong> · ` + (a
            ? `Årsmöte ${esc(d8(a.meetingDate))}${a.motionDeadline ? ` · sista dag för motioner <strong>${esc(d8(a.motionDeadline))}</strong>` : ''}`
            : 'Inget årsmöte är inlagt ännu. Motioner som lämnas nu behandlas på nästa årsmöte.');

        let h = '';
        if (r.canSubmit) {
            h += `<div class="mo-card"><h2>Lämna en motion</h2>
                <p class="mo-help mb-2">En motion är ett förslag som årsmötet beslutar om. Styrelsen yttrar sig först. Motionen och ditt namn syns för klubbens medlemmar och blir en del av årsmöteshandlingarna.</p>
                ${a && a.deadlinePassed ? '<div class="alert alert-warning py-2">Sista dagen har passerat. Motionen tas emot och märks att den kom in sent; årsmötet avgör om den tas upp.</div>' : ''}
                <div id="moForm" class="${formOpen ? '' : 'd-none'}">${formHtml()}</div>
                <button type="button" class="btn btn-primary ${formOpen ? 'd-none' : ''}" id="moOpen">Skriv en motion</button></div>`;
        } else if (r.isClub) {
            h += '<div class="mo-card"><p class="mb-0 mo-help">Motioner lämnas av klubbens medlemmar.</p></div>';
        }

        const motions = r.motions || [];
        const mine = motions.filter(x => x.mine);
        const open = motions.filter(x => x.dto.state !== 'Decided');
        const decided = motions.filter(x => x.dto.state === 'Decided');
        if (mine.length) h += '<h2 class="h5 mt-4">Dina motioner</h2>' + mine.map(card).join('');
        h += `<h2 class="h5 mt-4">${a ? 'Motioner till årsmötet' : 'Motioner'}</h2>`;
        const others = open.filter(x => !x.mine);
        h += others.length ? others.map(card).join('') : '<p class="mo-help">Inga andra motioner har kommit in.</p>';
        if (decided.length) h += '<h2 class="h5 mt-4">Behandlade det senaste året</h2>' + decided.map(card).join('');
        $('moBody').innerHTML = h;
        bind();
    }

    function card(x) {
        const m = x.dto;
        const props = (m.proposals || []).map(p => `<li>${esc(p)}</li>`).join('');
        const sup = x.canSupport ? `<div class="mo-support d-flex flex-wrap gap-2 align-items-center mt-2">
                <button type="button" class="btn btn-sm ${m.mySupport === 'Medmotionar' ? 'btn-primary' : 'btn-outline-primary'}" data-sup="${m.id}" data-kind="Medmotionar" aria-pressed="${m.mySupport === 'Medmotionar'}">${m.mySupport === 'Medmotionar' ? '✓ Du står som medmotionär' : 'Sätt mitt namn på motionen'}</button>
                <button type="button" class="btn btn-sm ${m.mySupport === 'Upp' ? 'btn-success' : 'btn-outline-secondary'}" data-sup="${m.id}" data-kind="Upp" aria-pressed="${m.mySupport === 'Upp'}" title="Jag tycker det är ett bra förslag">👍 ${m.up}</button>
                <button type="button" class="btn btn-sm ${m.mySupport === 'Ner' ? 'btn-secondary' : 'btn-outline-secondary'}" data-sup="${m.id}" data-kind="Ner" aria-pressed="${m.mySupport === 'Ner'}" title="Jag tycker inte det är ett bra förslag">👎 ${m.down}</button>
            </div><div class="mo-help small mt-1">Ditt namn syns för klubbens medlemmar när du sätter ditt namn på motionen eller ger en tumme.</div>`
            : `<div class="mo-help small mt-2">👍 ${m.up} · 👎 ${m.down}${x.supportNote ? ' · ' + esc(x.supportNote) : ''}</div>`;
        // En tumme är aldrig anonym: namnen bakom tummarna står under knapparna.
        const who = ((m.upNames || []).length ? `<div class="mo-help small">👍 ${m.upNames.map(esc).join(', ')}</div>` : '')
            + ((m.downNames || []).length ? `<div class="mo-help small">👎 ${m.downNames.map(esc).join(', ')}</div>` : '');
        return `<div class="mo-card">
            <div class="d-flex justify-content-between gap-2 flex-wrap"><h2>${esc(m.number)} ${esc(m.title)}</h2><span class="mo-state ${stateClass(m.state)}">${esc(m.stateLabel)}</span></div>
            <div class="mo-help">${esc(m.motionerName)}${m.signedByName ? ' (för styrelsen: ' + esc(m.signedByName) + ')' : ''} · inkom ${esc(d8(m.submittedDate))}${m.isLate ? ' · <span class="text-danger">efter sista dag</span>' : ''}</div>
            ${m.coSigners && m.coSigners.length ? `<div class="mo-help">Medmotionärer: ${m.coSigners.map(esc).join(', ')}</div>` : ''}
            ${m.background ? `<div class="mt-2" style="white-space:pre-wrap;">${esc(m.background)}</div>` : ''}
            ${props ? `<div class="mt-2 fw-semibold">Förslag till beslut</div><ul class="mb-1">${props}</ul>` : ''}
            ${m.boardProposalLabel ? `<div class="mo-opinion"><strong>Styrelsen föreslår: ${esc(m.boardProposalLabel)}</strong>${m.boardOpinion ? `<div class="mt-1" style="white-space:pre-wrap;">${esc(m.boardOpinion)}</div>` : ''}</div>` : ''}
            ${attachmentsHtml(m, x.mine && m.state !== 'Decided' && m.state !== 'Withdrawn')}
            ${m.decision ? `<div class="mo-opinion"><strong>Årsmötets beslut:</strong> <span style="white-space:pre-wrap;">${esc(m.decision)}</span></div>` : ''}
            ${sup}
            ${who}
            ${x.canWithdraw ? `<div class="mt-2"><button type="button" class="btn btn-link text-danger p-0" data-withdraw="${m.id}">Återkalla motionen…</button></div>` : ''}
        </div>`;
    }

    /** Bilagorna som länkar. Motionären kan bifoga och ta bort så länge motionen inte är behandlad. */
    function attachmentsHtml(m, canEdit) {
        const list = m.attachments || [];
        if (!list.length && !canEdit) return '';
        const kb = n => n >= 1048576 ? (n / 1048576).toFixed(1).replace('.', ',') + ' MB' : Math.max(1, Math.round(n / 1024)) + ' kB';
        let h = '<div class="mt-2"><div class="fw-semibold">Bilagor</div>';
        h += list.length ? '<ul class="mb-1 list-unstyled">' + list.map(a => `<li class="d-flex flex-wrap gap-2 align-items-center">
                <i class="bi bi-paperclip" aria-hidden="true"></i><a href="${esc(a.url)}" target="_blank" rel="noopener">${esc(a.fileName)}</a>
                <span class="mo-help small">${kb(a.size)}</span>
                ${canEdit ? `<button type="button" class="btn btn-link btn-sm text-danger p-0" data-att-remove="${a.id}">Ta bort</button>` : ''}</li>`).join('') + '</ul>'
            : '<div class="mo-help small mb-1">Inga bilagor.</div>';
        if (canEdit && list.length < 5)
            h += `<label class="btn btn-sm btn-outline-secondary mb-0"><i class="bi bi-paperclip me-1" aria-hidden="true"></i>Bifoga en fil
                <input type="file" class="d-none" data-att-add="${m.id}" accept=".pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.csv,.jpg,.jpeg,.png"></label>`;
        return h + '</div>';
    }

    function uploadFile(motionId, file) {
        const fd = new FormData();
        fd.append('motionId', motionId);
        fd.append('file', file);
        return req('/umbraco/surface/BoardWork/AddMotionAttachment', { method: 'POST', headers: { 'RequestVerificationToken': token() }, body: fd });
    }

    function formHtml() {
        const clubs = data.myClubs || [];
        return `<label class="form-label fw-semibold" for="moClub">Till</label>
            <select id="moClub" class="form-select mb-3" ${clubs.length > 1 ? '' : 'disabled'}>${clubs.map(c => `<option value="${c.id}" ${c.id === OID ? 'selected' : ''}>${esc(c.name)}</option>`).join('')}</select>
            <label class="form-label fw-semibold" for="moT">Rubrik</label><input type="text" id="moT" class="form-control mb-3" maxlength="300">
            <label class="form-label fw-semibold" for="moB">Bakgrund</label><textarea id="moB" class="form-control mb-1" rows="5"></textarea>
            <div class="mo-help mb-3">Varför föreslår du det här? Vad är problemet, och vad blir bättre?</div>
            <span class="form-label fw-semibold d-block">Förslag till beslut</span>
            <div id="moProps"><input type="text" class="form-control mb-2" data-prop placeholder="att …" aria-label="Förslag 1"></div>
            <button type="button" class="btn btn-link p-0" id="moAddProp">+ Lägg till ett förslag</button>
            <div class="mo-help mb-3">Skriv varje förslag som en egen rad som börjar med "att". Det är det årsmötet röstar om.</div>
            <label class="form-label fw-semibold" for="moFiles">Bilagor <span class="fw-normal mo-help">(valfritt, högst 5)</span></label>
            <input type="file" id="moFiles" class="form-control mb-1" multiple accept=".pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.csv,.jpg,.jpeg,.png">
            <div class="mo-help mb-3">T.ex. en offert, en ritning eller ett foto. Bilagorna syns för klubbens medlemmar tillsammans med motionen.</div>
            <div id="moErr" class="alert alert-danger d-none"></div>
            <div class="d-flex gap-2 justify-content-end"><button type="button" class="btn btn-outline-secondary" id="moCancel">Avbryt</button>
            <button type="button" class="btn btn-primary" id="moSend">Lämna motionen</button></div>`;
    }

    function bind() {
        $('moOpen')?.addEventListener('click', () => { formOpen = true; render(); $('moT')?.focus(); });
        $('moCancel')?.addEventListener('click', () => { formOpen = false; render(); });
        $('moAddProp')?.addEventListener('click', () => {
            const i = document.createElement('input'); i.type = 'text'; i.className = 'form-control mb-2'; i.placeholder = 'att …'; i.dataset.prop = '';
            $('moProps').appendChild(i); i.focus();
        });
        $('moSend')?.addEventListener('click', send);
        document.querySelectorAll('[data-att-add]').forEach(inp => inp.addEventListener('change', () => {
            const f = inp.files && inp.files[0]; if (!f) return;
            uploadFile(parseInt(inp.dataset.attAdd, 10), f).then(r => {
                if (r.success) { msg('Bilagan <strong>' + esc(r.fileName) + '</strong> är bifogad.'); load(); }
                else msg(esc(r.message), 'danger');
            }).catch(() => { });
        }));
        document.querySelectorAll('[data-att-remove]').forEach(b => b.addEventListener('click', () => {
            post('RemoveMotionAttachment', { attachmentId: b.dataset.attRemove }).then(r => {
                if (r.success) { msg('Bilagan är borttagen.'); load(); } else msg(esc(r.message), 'danger');
            });
        }));
        document.querySelectorAll('[data-sup]').forEach(b => b.addEventListener('click', () => {
            const id = parseInt(b.dataset.sup, 10);
            const m = (data.motions || []).find(x => x.dto.id === id); if (!m) return;
            const kind = m.dto.mySupport === b.dataset.kind ? '' : b.dataset.kind;   // klick igen = ta bort
            post('SetMotionSupport', { motionId: id, kind }).then(r => { if (r.success) load(); else msg(esc(r.message), 'danger'); });
        }));
        document.querySelectorAll('[data-withdraw]').forEach(b => b.addEventListener('click', () => {
            const id = parseInt(b.dataset.withdraw, 10);
            const m = (data.motions || []).find(x => x.dto.id === id); if (!m) return;
            // Bekräftelse i sidan, inte confirm().
            b.outerHTML = `<span class="d-inline-flex gap-2 align-items-center flex-wrap"><span>Återkalla ${esc(m.dto.number)}? Det går inte att ångra.</span>
                <button type="button" class="btn btn-sm btn-danger" data-withdraw-yes="${id}">Återkalla</button>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-withdraw-no>Avbryt</button></span>`;
            document.querySelector(`[data-withdraw-yes="${id}"]`).addEventListener('click', () =>
                post('WithdrawMyMotion', { motionId: id }).then(r => { if (r.success) { msg('Motionen är återkallad.'); load(); } else msg(esc(r.message), 'danger'); }));
            document.querySelector('[data-withdraw-no]').addEventListener('click', render);
        }));
    }

    function send() {
        const err = $('moErr'), btn = $('moSend');
        const title = $('moT').value.trim();
        const props = Array.from(document.querySelectorAll('[data-prop]')).map(i => i.value.trim()).filter(Boolean);
        const fail = t => { err.textContent = t; err.classList.remove('d-none'); };
        if (!title) return fail('Skriv en rubrik.');
        if (!props.length) return fail('Skriv minst ett förslag till beslut, som börjar med "att".');
        btn.disabled = true;
        const club = parseInt($('moClub').value, 10) || OID;
        const files = Array.from(($('moFiles') && $('moFiles').files) || []).slice(0, 5);
        post('SubmitMemberMotion', { clubId: club, title, background: $('moB').value, proposalsJson: JSON.stringify(props) }).then(async r => {
            if (!r.success) { fail(r.message || 'Motionen kunde inte lämnas.'); btn.disabled = false; return; }
            // Bilagorna laddas upp EFTER att motionen finns (de hänger på dess id). En bilaga som
            // inte gick upp fäller inte motionen — den namnges, och går att bifoga på nytt på kortet.
            const failed = [];
            for (const f of files) {
                try { const u = await uploadFile(r.id, f); if (!u.success) failed.push(f.name + ' (' + (u.message || 'fel') + ')'); }
                catch (e) { failed.push(f.name); }
            }
            formOpen = false;
            msg(`Tack! Motion <strong>${esc(r.number)}</strong> är inlämnad.` + (r.receiptSent ? ' Ett kvitto har gått till din e-post.' : '')
                + (r.late ? ' Den kom in efter sista dag och är märkt så; årsmötet avgör om den tas upp.' : '')
                + (failed.length ? '<br><strong>Bilagor som inte kom med:</strong> ' + failed.map(esc).join(', ') + '. Bifoga dem på nytt på motionen nedan.' : ''),
                failed.length ? 'warning' : 'success');
            if (club !== OID) location.href = '/motion?klubb=' + club; else load();
        }).catch(() => { btn.disabled = false; });
    }

    load();
})();
