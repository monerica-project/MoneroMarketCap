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
