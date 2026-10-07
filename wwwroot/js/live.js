// Keeps the page in step with the spreadsheet. Every few seconds it asks the server for the
// spreadsheet's version (a cheap check); when that changed, it re-fetches the page and swaps in
// the fresh content of every element marked data-live. Nothing reloads, so scroll position and
// anything typed elsewhere on the page are kept.
window.Live = (() => {
    const main = document.querySelector('main');
    const opts = {
        interval: 5000,
        canSwap: () => true,  // return false to postpone a swap (e.g. while a dialog is open)
        beforeSwap: () => {},
        afterSwap: () => {},
    };
    let busy = false;
    let pending = false;

    async function refresh() {
        const res = await fetch(location.href, { cache: 'no-store' });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const doc = new DOMParser().parseFromString(await res.text(), 'text/html');

        opts.beforeSwap();
        document.querySelectorAll('[data-live]').forEach(el => {
            const fresh = doc.getElementById(el.id);
            if (!fresh) return;
            const open = [...el.querySelectorAll('details[id][open]')].map(d => d.id);
            el.innerHTML = fresh.innerHTML;
            open.forEach(id => document.getElementById(id)?.setAttribute('open', ''));
        });
        main.dataset.sheetVersion = doc.querySelector('main')?.dataset.sheetVersion ?? '';
        pending = false;
        opts.afterSwap();
    }

    async function check() {
        if (busy || document.hidden) return;
        busy = true;
        try {
            if (!pending) {
                const res = await fetch(`${location.pathname}?handler=Version`, { cache: 'no-store' });
                const { version } = await res.json();
                pending = version !== main.dataset.sheetVersion;
            }
            if (pending && opts.canSwap()) await refresh();
        } catch {
            // Server unreachable or restarting; try again on the next tick.
        } finally {
            busy = false;
        }
    }

    function start(options = {}) {
        Object.assign(opts, options);
        setInterval(check, opts.interval);
        document.addEventListener('visibilitychange', () => { if (!document.hidden) check(); });
    }

    return { start, refresh };
})();
