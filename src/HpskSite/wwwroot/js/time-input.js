/*
 * Klockslag utan kolon (2026-10-11, efter Anders på Hästveda: "Det borde räcka att skriva 11 och 13").
 * Ett fält med data-hpsk-time skrivs om till "HH:mm" när man lämnar det: 11 → 11:00, 1130 → 11:30,
 * 11.30 / 11,30 → 11:30. Samma regler som servern (Models/ClockTime.cs, ClockTimeTests) — ändras en
 * regel där, ändra den här. Ett oläsligt värde lämnas orört och markeras, och servern säger varför.
 *
 * Fälten får inputmode="decimal": telefonens siffertangentbord med komma, så ingen behöver leta
 * efter kolon. Laddas av Master, så alla sidor med layouten får det.
 */
(function () {
    'use strict';
    function normalize(raw) {
        var v = String(raw == null ? '' : raw).trim();
        if (!v) return '';
        var m = v.match(/^(\d{1,2})[:.,\s](\d{2})$/) || v.match(/^(\d{1,2})(\d{2})$/) || v.match(/^(\d{1,2})$/);
        if (!m) return null;
        var h = +m[1], mi = m[2] === undefined ? 0 : +m[2];
        if (h > 23 || mi > 59) return null;
        return (h < 10 ? '0' : '') + h + ':' + (mi < 10 ? '0' : '') + mi;
    }
    window.hpskNormalizeTime = normalize;

    function apply(el) {
        if (!el || !el.matches || !el.matches('input[data-hpsk-time]')) return;
        var n = normalize(el.value);
        if (n === null) { el.classList.add('is-invalid'); return; }
        el.classList.remove('is-invalid');
        if (n !== el.value) {
            el.value = n;
            // Lyssnare (krockvarningen i träningsdialogen) ska se det omskrivna värdet.
            el.dispatchEvent(new Event('input', { bubbles: true }));
        }
    }
    // Fångstfasen: lyssnarna ligger på document, så fält i dialoger som ritas senare fungerar också.
    document.addEventListener('focusout', function (e) { apply(e.target); }, true);
    document.addEventListener('change', function (e) { apply(e.target); }, true);
})();
