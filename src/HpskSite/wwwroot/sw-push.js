/* pistol.nu Web Push service worker — shows notifications and focuses the app on click. */
self.addEventListener('push', function (event) {
    var data = {};
    try { data = event.data ? event.data.json() : {}; } catch (e) { data = { body: event.data ? event.data.text() : '' }; }

    var title = data.title || 'pistol.nu';
    var options = {
        body: data.body || '',
        icon: '/images/HpskLogo.jpg',
        badge: '/images/HpskLogo.jpg',
        tag: data.tag || 'pistol-nu',
        renotify: true,
        data: { url: data.url || '/' }
    };
    event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', function (event) {
    event.notification.close();
    var url = (event.notification.data && event.notification.data.url) || '/';
    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (list) {
            for (var i = 0; i < list.length; i++) {
                var c = list[i];
                if (c.url.indexOf(url.split('#')[0]) !== -1 && 'focus' in c) { c.focus(); return; }
            }
            if (clients.openWindow) return clients.openWindow(url);
        })
    );
});
