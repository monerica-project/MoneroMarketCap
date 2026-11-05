// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Coin search autocomplete (navbar). Queries /api/coins/suggest and shows a
// dropdown; selecting a coin navigates to its page. Keyboard + mouse supported.
(function () {
    var input = document.getElementById('coinSearch');
    var box = document.getElementById('coinSearchResults');
    if (!input || !box) {
        return;
    }

    var items = [];
    var active = -1;
    var timer = null;

    function close() {
        box.hidden = true;
        box.innerHTML = '';
        items = [];
        active = -1;
        input.setAttribute('aria-expanded', 'false');
    }

    function go(url) {
        if (url) {
            window.location.href = url;
        }
    }

    function render(list, q) {
        box.innerHTML = '';
        items = [];
        active = -1;

        if (!list.length) {
            var empty = document.createElement('div');
            empty.className = 'coin-search-empty';
            empty.textContent = 'No coins match “' + q + '”';
            box.appendChild(empty);
            box.hidden = false;
            input.setAttribute('aria-expanded', 'true');
            return;
        }

        list.forEach(function (c) {
            var a = document.createElement('a');
            a.className = 'coin-search-item';
            a.href = c.url;
            a.setAttribute('role', 'option');

            if (c.image) {
                var img = document.createElement('img');
                img.src = c.image;
                img.alt = '';
                img.loading = 'lazy';
                a.appendChild(img);
            }

            var sym = document.createElement('span');
            sym.className = 'coin-search-sym';
            sym.textContent = c.symbol;
            a.appendChild(sym);

            var name = document.createElement('span');
            name.className = 'coin-search-name';
            name.textContent = c.name;
            a.appendChild(name);

            box.appendChild(a);
            items.push(a);
        });

        box.hidden = false;
        input.setAttribute('aria-expanded', 'true');
    }

    function highlight(idx) {
        items.forEach(function (el) { el.classList.remove('active'); });
        if (idx >= 0 && idx < items.length) {
            items[idx].classList.add('active');
            items[idx].scrollIntoView({ block: 'nearest' });
        }
        active = idx;
    }

    function fetchSuggest(q) {
        fetch('/api/coins/suggest?q=' + encodeURIComponent(q))
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (list) {
                if (input.value.trim() !== q) {
                    return; // a newer query superseded this one
                }
                render(list, q);
            })
            .catch(function () { close(); });
    }

    input.addEventListener('input', function () {
        var q = input.value.trim();
        if (timer) {
            clearTimeout(timer);
        }
        if (q.length < 1) {
            close();
            return;
        }
        timer = setTimeout(function () { fetchSuggest(q); }, 150);
    });

    input.addEventListener('keydown', function (ev) {
        if (box.hidden) {
            return;
        }
        if (ev.key === 'ArrowDown') {
            ev.preventDefault();
            highlight(Math.min(active + 1, items.length - 1));
        } else if (ev.key === 'ArrowUp') {
            ev.preventDefault();
            highlight(Math.max(active - 1, 0));
        } else if (ev.key === 'Enter') {
            if (active >= 0 && items[active]) {
                ev.preventDefault();
                go(items[active].getAttribute('href'));
            } else if (items.length === 1) {
                ev.preventDefault();
                go(items[0].getAttribute('href'));
            }
        } else if (ev.key === 'Escape') {
            close();
        }
    });

    input.addEventListener('focus', function () {
        var q = input.value.trim();
        if (q.length >= 1) {
            fetchSuggest(q);
        }
    });

    document.addEventListener('click', function (ev) {
        if (!input.parentNode.contains(ev.target)) {
            close();
        }
    });
})();

// "Data updated": show how long ago (e.g. "3 min ago"), with the exact UTC time on hover.
// The server renders the UTC time as the no-JS fallback; <time class="time-ago" datetime="...Z">.
(function () {
    function pad(n) { return (n < 10 ? '0' : '') + n; }

    function utcText(d) {
        return d.getUTCFullYear() + '-' + pad(d.getUTCMonth() + 1) + '-' + pad(d.getUTCDate()) + ' ' +
            pad(d.getUTCHours()) + ':' + pad(d.getUTCMinutes()) + ':' + pad(d.getUTCSeconds()) + ' UTC';
    }

    function agoText(d) {
        var s = Math.max(0, Math.round((Date.now() - d.getTime()) / 1000));
        if (s < 60) { return 'just now'; }
        var m = Math.floor(s / 60);
        if (m < 60) { return m + ' min ago'; }
        var h = Math.floor(m / 60);
        if (h < 48) { return h + (h === 1 ? ' hour ago' : ' hours ago'); }
        var days = Math.floor(h / 24);
        return days + ' days ago';
    }

    function render(el) {
        var d = new Date(el.getAttribute('datetime'));
        if (isNaN(d.getTime())) { return; }   // leave the server's UTC text as is
        el.textContent = agoText(d);
        el.title = utcText(d);
    }

    function renderAll() {
        var els = document.querySelectorAll('time.time-ago[datetime]');
        for (var i = 0; i < els.length; i++) { render(els[i]); }
    }

    window.timeAgo = {
        set: function (el, date) {
            el.classList.add('time-ago');
            el.setAttribute('datetime', date.toISOString());
            render(el);
        },
        refresh: renderAll
    };

    renderAll();
    setInterval(renderAll, 30000);
})();
