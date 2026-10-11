/*
 * En skjutbanas karta — samma form som tävlingssidans (Views/Competition.cshtml, "Skjutbana:"):
 * OpenStreetMap, en röd punkt, zoom 14 och en Vägbeskrivning-länk till Google Maps.
 * Används av banans egen sida (/skjutbana/{id}) och klubbsidans flik Banor (2026-10-11).
 *
 * ⚠️ Tävlingssidan har kvar sin egen kopia tills branchen sajtstruktur är sammanslagen (den filen är
 * ändrad där). Byt den då till hpskRangeMap.render — en tredje kopia ska inte uppstå.
 *
 * Kräver Leaflet (L). Kartan ritas först när behållaren syns: Leaflet räknar fel i en dold ruta,
 * så anroparen kallar refresh() när fliken eller delen visas.
 */
(function () {
    'use strict';

    function hasPos(r) { return r && r.latitude != null && r.longitude != null; }

    function directionsUrl(r) {
        return hasPos(r) ? 'https://www.google.com/maps/dir/?api=1&destination=' + r.latitude + ',' + r.longitude : '';
    }

    function addressText(r) {
        if (!r) return '';
        var place = [r.postcode, r.city].filter(Boolean).join(' ');
        return [r.address, place].filter(Boolean).join(', ');
    }

    /** Ritar kartan i el. Returnerar { map, refresh } eller null när banan saknar position. */
    function render(el, r) {
        if (!el || !hasPos(r) || typeof L === 'undefined') return null;
        if (el._hpskRangeMap) { el._hpskRangeMap.refresh(); return el._hpskRangeMap; }
        var map = L.map(el, { scrollWheelZoom: false }).setView([r.latitude, r.longitude], 14);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '© OpenStreetMap' }).addTo(map);
        var name = String(r.name == null ? '' : r.name).replace(/[&<>"]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
        });
        L.circleMarker([r.latitude, r.longitude], { radius: 9, color: '#fff', weight: 2, fillColor: '#dc3545', fillOpacity: 1 })
            .addTo(map).bindPopup(name);
        var api = {
            map: map,
            refresh: function () { setTimeout(function () { map.invalidateSize(); map.setView([r.latitude, r.longitude], map.getZoom()); }, 80); }
        };
        el._hpskRangeMap = api;
        api.refresh();
        return api;
    }

    window.hpskRangeMap = { render: render, directionsUrl: directionsUrl, addressText: addressText, hasPos: hasPos };
})();
