// -*- coding: utf-8 -*-
// Tävlingsplaneringen, omgång 3 (K8): datumfälten. flatpickrs egen tolkning med dateFormat 'Y-m-d' gjorde "16 maj 2027"
// till 2027-01-01 utan varning, och en planering skapades med fel datum. Här tolkas det vi själva skriver:
//   2027-05-16 · 20270516 · 16/5 2027 · 16/5/2027 · 16.5.2027 · 16 maj 2027 · 16 maj (i år, eller nästa år om dagen passerat)
// Allt annat lämnas och fältet blir rött — gissa aldrig ett datum.
(function () {
    'use strict';
    const MONTHS = ['jan', 'feb', 'mar', 'apr', 'maj', 'jun', 'jul', 'aug', 'sep', 'okt', 'nov', 'dec'];
    const pad = n => String(n).padStart(2, '0');
    function make(y, m, d) {
        if (!(y >= 1900 && y <= 2200 && m >= 1 && m <= 12 && d >= 1 && d <= 31)) return null;
        const dt = new Date(y, m - 1, d);
        return dt.getFullYear() === y && dt.getMonth() === m - 1 && dt.getDate() === d ? dt : null;
    }
    function parse(text) {
        const s = String(text || '').trim().toLowerCase().replace(/\s+/g, ' ');
        if (!s) return null;
        let m = s.match(/^(\d{4})-(\d{1,2})-(\d{1,2})$/);
        if (m) return make(+m[1], +m[2], +m[3]);
        m = s.match(/^(\d{4})(\d{2})(\d{2})$/);
        if (m) return make(+m[1], +m[2], +m[3]);
        m = s.match(/^(\d{1,2})[\/.](\d{1,2})(?:[\/. ] ?(\d{2}|\d{4}))?$/);
        if (m) return withYear(+m[1], +m[2], m[3]);
        m = s.match(/^(\d{1,2})\.? ([a-zåäö]+)\.?(?: (\d{2}|\d{4}))?$/);
        if (m) {
            const mi = MONTHS.findIndex(x => m[2].startsWith(x));
            if (mi < 0) return null;
            return withYear(+m[1], mi + 1, m[3]);
        }
        return null;
    }
    function withYear(d, mo, y) {
        if (y) return make(y.length === 2 ? 2000 + +y : +y, mo, d);
        const today = new Date(); today.setHours(0, 0, 0, 0);
        const dt = make(today.getFullYear(), mo, d);
        if (!dt) return null;
        return dt < today ? make(today.getFullYear() + 1, mo, d) : dt;
    }
    const iso = dt => `${dt.getFullYear()}-${pad(dt.getMonth() + 1)}-${pad(dt.getDate())}`;

    /** Kopplar en datumväljare till fältet. Fältet bär alltid åååå-mm-dd eller är rött. */
    window.hpskPlDatePicker = function (el) {
        if (!el || el._hpskDate) return;
        el._hpskDate = true;
        el.setAttribute('autocomplete', 'off');
        if (!el.placeholder) el.placeholder = 'till exempel 16 maj 2027';
        // Det man skrev — flatpickr tömmer fältet när texten inte går att läsa, och då försvann den utan ett ord.
        // Bara det användaren skriver (isTrusted) — flatpickr skickar egna händelser när den tömmer fältet.
        el.addEventListener('input', e => { if (e.isTrusted) el._hpskRaw = el.value; });
        const check = () => {
            let v = el.value.trim();
            if (!v && el._hpskRaw && el._hpskRaw.trim() && !parse(el._hpskRaw)) { el.value = el._hpskRaw; v = el._hpskRaw.trim(); }
            if (!v) { el.classList.remove('is-invalid'); el.removeAttribute('title'); return; }
            const dt = parse(v);
            if (dt) {
                el.classList.remove('is-invalid'); el.removeAttribute('title');
                if (el.value !== iso(dt)) { if (el._flatpickr) el._flatpickr.setDate(dt, false); el.value = iso(dt); }
            } else {
                el.classList.add('is-invalid');
                el.title = 'Datumet går inte att läsa. Skriv till exempel 16 maj 2027 eller 2027-05-16.';
            }
        };
        if (window.flatpickr) {
            window.flatpickr(el, {
                locale: 'sv', dateFormat: 'Y-m-d', allowInput: true,
                // Inne i en dialog följer kalendern fältet (annars hamnar den bakom eller täcker knapparna).
                static: !!el.closest('.modal'),
                parseDate: str => parse(str) || undefined,
                onClose: check,
            });
        }
        el.addEventListener('keydown', e => { if (e.key === 'Tab' && el._flatpickr) el._flatpickr.close(); });
        el.addEventListener('blur', () => setTimeout(check, 0));
    };
    /** Tolkningen för den som läser ett fält själv: åååå-mm-dd, tomt, eller null om texten inte går att läsa. */
    window.hpskPlParseDate = function (text) {
        const v = String(text || '').trim();
        if (!v) return '';
        const dt = parse(v);
        return dt ? iso(dt) : null;
    };
})();
