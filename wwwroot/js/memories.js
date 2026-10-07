// "Natais que fizemos juntos": the carousel of past Christmas photos on the home page.
// Photos are placed by setting --offset/--abs on each one (CSS turns them into a fan of polaroids),
// so dragging can move them by fractions and everything else is a CSS transition.
(() => {
    const root = document.querySelector('[data-carousel]');
    if (!root) return;

    const stage = root.querySelector('[data-stage]');
    const slides = [...root.querySelectorAll('.memory')];
    const bulbs = [...root.querySelectorAll('.bulb')];
    const timeline = root.querySelector('.memories-timeline');
    const status = root.querySelector('[data-status]');
    const playButton = root.querySelector('[data-play]');
    const ring = root.querySelector('.ring-progress');
    const viewer = document.querySelector('[data-viewer]');
    const viewerImg = viewer.querySelector('[data-viewer-img]');
    const viewerCaption = viewer.querySelector('[data-viewer-caption]');
    const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
    const last = slides.length - 1;
    const ease = 'cubic-bezier(0.22, 1, 0.36, 1)';

    let current = 0;
    let playing = false;

    // ------------------------------------------------------------------ Layout

    function place(position) {
        slides.forEach((slide, i) => {
            const offset = i - position;
            const abs = Math.abs(offset);
            slide.style.setProperty('--offset', offset.toFixed(3));
            slide.style.setProperty('--abs', abs.toFixed(3));
            slide.style.zIndex = String(100 - Math.round(abs * 10));
            slide.classList.toggle('is-far', abs > 2.4);
        });
    }

    const yearOf = i => slides[i].dataset.year;

    function go(index, { announce = true } = {}) {
        index = Math.max(0, Math.min(last, index));
        const hadFocus = slides.includes(document.activeElement);
        current = index;

        place(current);
        slides.forEach((slide, i) => {
            const active = i === current;
            slide.classList.toggle('is-active', active);
            slide.tabIndex = active ? 0 : -1;
            slide.setAttribute('aria-label', active
                ? `Natal ${yearOf(i)}: ampliar foto`
                : `Ir para o Natal ${yearOf(i)}`);
        });
        if (hadFocus) slides[current].focus({ preventScroll: true });

        const year = yearOf(current);
        const yearIndex = bulbs.findIndex(b => b.dataset.year === year);
        bulbs.forEach((bulb, i) => {
            bulb.classList.toggle('is-lit', i <= yearIndex);
            if (i === yearIndex) bulb.setAttribute('aria-current', 'true');
            else bulb.removeAttribute('aria-current');
        });
        timeline.style.setProperty('--p', bulbs.length > 1 ? yearIndex / (bulbs.length - 1) : 0);

        if (announce) status.textContent = `Natal ${year}, foto ${current + 1} de ${slides.length}`;
        if (viewer.open) showInViewer();
        restartTimer();
    }

    const next = () => go(current === last ? 0 : current + 1);
    const previous = () => go(current === 0 ? last : current - 1);

    // ------------------------------------------------------------------ Autoplay
    // The ring around the pause button is the timer: when its fill animation ends, the next photo
    // comes. Pausing the animation (hover, off screen, ...) pauses the slideshow with it.

    const holds = new Set();
    function hold(reason, on) {
        if (on) holds.add(reason); else holds.delete(reason);
        root.classList.toggle('is-held', holds.size > 0);
    }

    function restartTimer() {
        ring.style.animation = 'none';
        ring.getBoundingClientRect();  // force a style flush so the animation starts over
        ring.style.animation = '';
    }

    function setPlaying(on) {
        playing = on;
        root.classList.toggle('is-playing', on);
        playButton.setAttribute('aria-label', on ? 'Pausar apresentação' : 'Continuar apresentação');
        status.setAttribute('aria-live', on ? 'off' : 'polite');
        if (on) restartTimer();
    }

    ring.addEventListener('animationend', () => {
        if (playing) go(current === last ? 0 : current + 1, { announce: false });
    });

    // People who asked for less motion get no slideshow; everything else still works.
    if (!reducedMotion && slides.length > 1) {
        playButton.hidden = false;
        playButton.addEventListener('click', () => setPlaying(!playing));
        setPlaying(true);
    }

    root.addEventListener('pointerenter', e => { if (e.pointerType === 'mouse') hold('hover', true); });
    root.addEventListener('pointerleave', e => { if (e.pointerType === 'mouse') hold('hover', false); });
    document.addEventListener('visibilitychange', () => hold('hidden', document.hidden));

    // ------------------------------------------------------------------ Entrance, and pause off screen

    root.classList.add('is-stacked');
    let entered = false;
    new IntersectionObserver(([entry]) => {
        hold('offscreen', !entry.isIntersecting);
        if (entry.isIntersecting && !entered) {
            entered = true;
            root.classList.add('is-entering');
            requestAnimationFrame(() => root.classList.remove('is-stacked'));
            setTimeout(() => root.classList.remove('is-entering'), 1400);
            restartTimer();
        }
    }, { threshold: 0.35 }).observe(stage);

    // ------------------------------------------------------------------ Clicks and keys

    let suppressClick = false;
    slides.forEach((slide, i) => slide.addEventListener('click', () => {
        if (suppressClick) return;
        if (i === current) openViewer(); else go(i);
    }));
    bulbs.forEach(bulb => bulb.addEventListener('click', () => go(Number(bulb.dataset.index))));
    root.querySelector('[data-prev]').addEventListener('click', previous);
    root.querySelector('[data-next]').addEventListener('click', next);

    root.addEventListener('keydown', e => {
        const keys = { ArrowLeft: current - 1, ArrowRight: current + 1, Home: 0, End: last };
        if (!(e.key in keys)) return;
        e.preventDefault();
        go(keys[e.key]);
    });

    // ------------------------------------------------------------------ Drag / swipe
    // The photos follow the finger, with some resistance past the first and last one,
    // then settle on the nearest photo (or the next one, after a quick flick).

    let drag = null;
    const spread = () => slides[0].offsetWidth * parseFloat(getComputedStyle(root).getPropertyValue('--spread-ratio'));

    stage.addEventListener('pointerdown', e => {
        if (e.button !== 0) return;
        drag = { id: e.pointerId, x: e.clientX, y: e.clientY, dx: 0, moved: false, vx: 0, lastX: e.clientX, lastT: e.timeStamp };
    });

    stage.addEventListener('pointermove', e => {
        if (!drag || e.pointerId !== drag.id) return;
        const dx = e.clientX - drag.x;
        if (!drag.moved) {
            if (Math.abs(dx) < 8) return;
            if (Math.abs(e.clientY - drag.y) > Math.abs(dx)) { drag = null; return; }  // a vertical scroll
            drag.moved = true;
            stage.setPointerCapture(e.pointerId);
            root.classList.add('is-dragging');
            hold('drag', true);
        }
        const dt = e.timeStamp - drag.lastT;
        if (dt > 0) drag.vx = (e.clientX - drag.lastX) / dt;
        drag.lastX = e.clientX;
        drag.lastT = e.timeStamp;
        drag.dx = dx;

        let position = current - dx / spread();
        if (position < 0) position *= 0.3;
        if (position > last) position = last + (position - last) * 0.3;
        place(position);
    });

    function endDrag(e) {
        if (!drag || e.pointerId !== drag.id) return;
        const { moved, dx, vx } = drag;
        drag = null;
        if (!moved) return;

        root.classList.remove('is-dragging');
        hold('drag', false);
        suppressClick = true;
        setTimeout(() => { suppressClick = false; });

        let steps = Math.round(dx / spread());
        if (steps === 0 && (Math.abs(dx) > 40 || Math.abs(vx) > 0.45)) steps = Math.sign(dx);
        go(current - steps);
    }
    stage.addEventListener('pointerup', endDrag);
    stage.addEventListener('pointercancel', endDrag);

    // ------------------------------------------------------------------ Full-size viewer

    function showInViewer() {
        const img = slides[current].querySelector('img');
        viewerImg.src = img.currentSrc || img.src;
        viewerImg.alt = img.alt;
        viewerCaption.textContent = `Natal ${yearOf(current)}`;
        if (!reducedMotion && viewerImg.animate) {
            viewerImg.animate([{ opacity: 0, transform: 'scale(0.97)' }, { opacity: 1, transform: 'none' }], { duration: 380, easing: ease });
        }
    }

    function openViewer() {
        showInViewer();
        hold('viewer', true);
        viewer.showModal();
    }

    viewer.addEventListener('close', () => {
        hold('viewer', false);
        slides[current].focus({ preventScroll: true });
    });
    viewer.addEventListener('click', e => { if (e.target === viewer) viewer.close(); });  // the backdrop
    viewer.querySelector('[data-viewer-close]').addEventListener('click', () => viewer.close());
    viewer.querySelector('[data-viewer-prev]').addEventListener('click', previous);
    viewer.querySelector('[data-viewer-next]').addEventListener('click', next);
    viewer.addEventListener('keydown', e => {
        if (e.key === 'ArrowLeft') { e.preventDefault(); previous(); }
        if (e.key === 'ArrowRight') { e.preventDefault(); next(); }
    });

    go(0, { announce: false });
})();
