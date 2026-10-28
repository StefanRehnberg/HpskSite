// Mitt schema, "Din del": skjutledare och stationschef lämnar över uppdrag i delen de leder (ReplacementAccessRules).
// Servern prövar rätten vid varje anrop — den här filen ritar bara. Samma slutpunkter som planeringens "Hitta ersättare".
(function () {
    'use strict';
    var cfg = window.hpskLedPart;
    var box = document.getElementById('ledPartList');
    if (!cfg || !box) return;

    function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; }
    function surface(action, qs) { return '/umbraco/surface/Staffing/' + action + (qs ? '?' + new URLSearchParams(qs).toString() : ''); }
    async function getJson(url) {
        var r = await fetch(url, { headers: { Accept: 'application/json' }, credentials: 'same-origin' });
        return r.json();
    }
    async function postJson(url, body) {
        var r = await fetch(url, { method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': cfg.token }, body: JSON.stringify(body) });
        return r.json();
    }

    var rows = [];

    async function load() {
        rows = [];
        try {
            for (var i = 0; i < cfg.parts.length; i++) {
                var r = await getJson(surface('GetMyPartAssignments', { partId: cfg.parts[i] }));
                if (r && r.success) rows = rows.concat(r.rows || []);
            }
        } catch (e) { box.innerHTML = '<span class="text-danger small">Kunde inte hämta uppdragen i din del. Ladda om sidan.</span>'; return; }
        draw();
    }

    function draw() {
        if (!rows.length) { box.innerHTML = '<span class="text-muted small">Inga kommande uppdrag att lämna över i din del.</span>'; return; }
        box.innerHTML = '<ul class="list-group list-group-flush">' + rows.map(function (r) {
            var state = r.open ? '<span class="badge text-bg-warning fw-normal ms-1">Ingen utsedd</span>' : '';
            return '<li class="list-group-item px-0 d-flex justify-content-between align-items-start gap-2 flex-wrap">'
                + '<span><b>' + esc(r.role) + '</b> ' + esc(r.when) + '<br><span class="text-muted small">' + esc(r.holder) + '</span>' + state + '</span>'
                + '<button type="button" class="btn btn-sm ' + (r.open ? 'btn-primary' : 'btn-outline-secondary') + ' flex-shrink-0" data-ld-find="' + r.id + '">'
                + '<i class="bi bi-person-plus me-1" aria-hidden="true"></i>Hitta ersättare</button></li>';
        }).join('') + '</ul>';
    }

    box.addEventListener('click', function (e) {
        var b = e.target.closest && e.target.closest('[data-ld-find]'); if (!b) return;
        find(parseInt(b.getAttribute('data-ld-find'), 10), b);
    });

    async function find(id, btn) {
        btn.disabled = true;
        var r;
        try { r = await getJson(surface('GetReplacementCandidates', { assignmentId: id })); } catch (e) { r = null; }
        btn.disabled = false;
        if (!r || !r.success) { alert((r && r.message) || 'Kunde inte leta efter någon.'); return; }
        show(id, r);
    }

    function badge(c) {
        return c.free === true ? '<span class="badge text-bg-success fw-normal">Ledig</span>'
            : c.free === false ? '<span class="badge text-bg-warning fw-normal">Upptagen</span>'
            : '<span class="badge text-bg-secondary fw-normal">Tiden är inte satt</span>';
    }

    function show(id, r) {
        var list = r.candidates || [];
        var el = document.createElement('div');
        el.className = 'modal fade'; el.tabIndex = -1; el.setAttribute('aria-labelledby', 'ldTitle'); el.setAttribute('aria-hidden', 'true');
        el.innerHTML = '<div class="modal-dialog modal-dialog-scrollable"><div class="modal-content">'
            + '<div class="modal-header"><h2 class="modal-title fs-5" id="ldTitle">Hitta ersättare</h2><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Stäng"></button></div>'
            + '<div class="modal-body">'
            + '<p class="mb-2">' + esc(r.target.hasHolder ? r.target.holder : 'Ingen') + ' har ' + esc(r.target.role) + ' ' + esc(r.target.when) + '. Här är de som anmält sig till dagen.</p>'
            + (list.length
                ? '<div class="list-group" role="radiogroup" aria-label="Möjliga ersättare">' + list.map(function (c) {
                    return '<label class="list-group-item d-flex gap-2 align-items-start"><input class="form-check-input mt-1 flex-shrink-0" type="radio" name="ldRepl" value="' + c.memberId + '">'
                        + '<span><b>' + esc(c.name) + '</b> ' + badge(c)
                        + (c.roleMatch ? ' <span class="badge text-bg-light border fw-normal">Har anmält sig som ' + esc(r.target.role) + '</span>' : '')
                        + (c.free === false && c.busyWith ? '<span class="d-block small text-warning-emphasis">' + esc(c.busyWith) + '</span>' : '')
                        + (c.phone ? '<a class="d-block small" href="tel:' + esc(c.phone) + '">' + esc(c.phone) + '</a>' : '')
                        + (c.comment ? '<span class="d-block text-muted small">' + esc(c.comment) + '</span>' : '')
                        + '</span></label>';
                }).join('') + '</div><p class="small text-muted mt-2 mb-0">Den som tar över får besked om placeringen. '
                    + 'Den som hade uppdraget får också veta att hen inte ska komma.</p>'
                : '<p class="mb-0">Ingen har anmält sig till den dagen ännu. Hör av dig till den som bemannar.</p>')
            + '<div class="text-danger small mt-2" id="ldErr" role="alert"></div></div>'
            + '<div class="modal-footer"><button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Avbryt</button>'
            + (list.length ? '<button type="button" class="btn btn-primary" id="ldOk">Lämna över</button>' : '') + '</div></div></div>';
        document.body.appendChild(el);
        var modal = new bootstrap.Modal(el);
        el.addEventListener('hidden.bs.modal', function () { modal.dispose(); el.remove(); });
        var confirmedBusy = null;
        var ok = el.querySelector('#ldOk');
        if (ok) ok.addEventListener('click', async function () {
            var err = el.querySelector('#ldErr'); err.textContent = '';
            var sel = el.querySelector('input[name="ldRepl"]:checked');
            if (!sel) { err.textContent = 'Välj vem som tar över.'; return; }
            var chosen = list.filter(function (c) { return String(c.memberId) === sel.value; })[0];
            if (chosen && chosen.free === false && confirmedBusy !== sel.value) {
                confirmedBusy = sel.value;
                err.textContent = chosen.name + ' är upptagen' + (chosen.busyWith ? ' (' + chosen.busyWith + ')' : '') + ' den tiden. Tryck Lämna över igen om du vill lämna över ändå.';
                return;
            }
            ok.disabled = true;
            var d;
            try { d = await postJson(surface('ReplaceAssignmentHolder'), { id: id, memberId: parseInt(sel.value, 10) }); } catch (e) { d = null; }
            ok.disabled = false;
            if (!d || !d.success) { err.textContent = (d && d.message) || 'Uppdraget kunde inte lämnas över.'; return; }
            modal.hide();
            await load();
            box.insertAdjacentHTML('afterbegin', '<div class="alert alert-success py-2 small" role="status">Uppdraget är lämnat till <b>' + esc(d.name) + '</b>. '
                + (d.notified ? 'Hen har fått besked.' : 'Hen kunde inte nås automatiskt — ring.') + '</div>');
        });
        modal.show();
    }

    load();
})();
