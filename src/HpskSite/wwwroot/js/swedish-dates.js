/*
 * Svensk tid i webbläsaren (fas E, 2026-10-17).
 *
 * `new Date().toISOString()` räknar i UTC. Används den för att ta fram dagens datum blir det GÅRDAGENS
 * datum mellan midnatt och 01/02 svensk tid, och används den för ett klockslag blir det två timmar
 * för tidigt. Hjälparna här räknar alltid i Europe/Stockholm, oavsett var webbläsaren står.
 *
 *   hpskLocalDate(d?)      → "2026-10-17"        (dagens datum om d saknas)
 *   hpskLocalDateTime(d?)  → "2026-10-17T18:30"  (för datetime-fält och flatpickr "Y-m-d H:i")
 *
 * Laddas i Masters <head>, så de finns innan någon vy körs. En sida utan Master laddar filen själv.
 */
(function () {
    'use strict';
    var tz = 'Europe/Stockholm';
    function parts(d) {
        var f = new Intl.DateTimeFormat('sv-SE', {
            timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit',
            hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
        });
        var o = {};
        f.formatToParts(d || new Date()).forEach(function (p) { o[p.type] = p.value; });
        return o;
    }
    window.hpskLocalDate = function (d) {
        var p = parts(d);
        return p.year + '-' + p.month + '-' + p.day;
    };
    window.hpskLocalDateTime = function (d) {
        var p = parts(d);
        return p.year + '-' + p.month + '-' + p.day + 'T' + p.hour + ':' + p.minute;
    };
})();
