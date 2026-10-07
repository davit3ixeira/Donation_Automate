// Small festive effects shared by the pages. Everything here is decoration: it is skipped for
// people who asked their system to reduce motion, and the pages work the same without it.
window.Festive = (() => {
    const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;

    /** A short burst of confetti in the campaign colours, cleaned up when it lands. */
    function confetti(pieces = 110) {
        if (reducedMotion()) return;
        const colors = ['#b8322a', '#1f5a3d', '#d4a24c', '#ffffff', '#2e7d5b', '#e05a4f'];
        const layer = document.createElement('div');
        layer.className = 'confetti';
        layer.setAttribute('aria-hidden', 'true');
        for (let i = 0; i < pieces; i++) {
            const p = document.createElement('i');
            p.style.left = `${Math.random() * 100}vw`;
            p.style.background = colors[i % colors.length];
            p.style.animationDuration = `${2.6 + Math.random() * 1.8}s`;
            p.style.animationDelay = `${Math.random() * 0.5}s`;
            p.style.setProperty('--drift', `${(Math.random() * 2 - 1) * 140}px`);
            p.style.setProperty('--spin', `${360 + Math.random() * 720}deg`);
            if (i % 3 === 0) p.style.borderRadius = '50%';
            layer.append(p);
        }
        document.body.append(layer);
        setTimeout(() => layer.remove(), 5200);
    }

    /** Restarts a one-shot CSS animation class on an element (e.g. a "pop" on click). */
    function replay(el, className) {
        if (!el || reducedMotion()) return;
        el.classList.remove(className);
        void el.offsetWidth;  // force a reflow so the animation starts again
        el.classList.add(className);
    }

    return { confetti, replay, reducedMotion };
})();
