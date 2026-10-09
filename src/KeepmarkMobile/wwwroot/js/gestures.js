// Swipe + long-press detection (delegation-based: survives Blazor re-renders).
// Taps and clicks are plain Blazor @onclick — this script never interferes.
// Explore rows: <div class="xbs-row" data-rowid="N">…</div>
//   swipe right/left -> OnRowSwipeRight/Left, long-press (500ms) -> OnRowLongPress
// Duplicates rows: <div class="xbs-duprow" data-rowid="N">…</div>
//   swipe right/left -> OnDupSwipeRight/Left (no long-press there)

// UI strings come from C# (xbsSetLang in index.html); English is the fallback.
function str(key, fallback) {
    return (window.__xbsStrings && window.__xbsStrings[key]) || fallback;
}

window.xbsGestures = (() => {
    let dotNet = null;
    let dupRef = null;
    const locks = {};
    let listenersAttached = false;
    let state = null;
    let suppressUntil = 0;

    const LONG_PRESS_MS = 500;
    const MOVE_TOLERANCE_PX = 12;
    const SWIPE_TRIGGER_PX = 60;

    // The live pages may be replaced at any moment (old ref disposed, new one
    // not yet registered): never let a gesture crash on a dead reference.
    function call(ref, method, id) {
        try { if (ref) ref.invokeMethodAsync(method, id); } catch { /* noop */ }
    }

    function onDown(e) {
        if (e.pointerType === 'mouse' && e.button !== 0) return;
        if (!e.target.closest) return;
        // Action sheet details: horizontal swipe toggles the full text.
        const info = e.target.closest('[data-sheet-info]');
        if (info) {
            state = { id: 0, kind: 'sheet', x0: e.clientX, y0: e.clientY, pointerId: e.pointerId, timer: null };
            return;
        }
        const row = e.target.closest('.xbs-row');
        const dup = !row ? e.target.closest('.xbs-duprow') : null;
        const el = row || dup;
        if (!el || !el.dataset.rowid) return;
        if (row && (e.target.closest('button') || el.dataset.sep)) return;
        // On duplicate rows the ✕ marker keeps its tap: swipes may start anywhere else.
        if (dup && e.target.closest('.xbs-dupmark')) return;
        const id = parseInt(el.dataset.rowid, 10);
        const kind = row ? 'row' : 'dup';
        state = {
            id, kind, x0: e.clientX, y0: e.clientY, pointerId: e.pointerId,
            timer: kind === 'row' ? setTimeout(() => {
                state = null;
                suppressUntil = Date.now() + 400; // finger-up click must not open the row after a long-press
                try { if (navigator.vibrate) navigator.vibrate(25); } catch { /* noop */ }
                call(dotNet, 'OnRowLongPress', id);
            }, LONG_PRESS_MS) : null
        };
    }

    function onMove(e) {
        if (!state || e.pointerId !== state.pointerId) return;
        const dx = e.clientX - state.x0;
        const dy = e.clientY - state.y0;
        if (!state.swiping && Math.abs(dx) > MOVE_TOLERANCE_PX && Math.abs(dx) > Math.abs(dy) * 1.5) {
            state.swiping = true;
            if (state.timer) clearTimeout(state.timer);
        }
        if (state.swiping) {
            state.dx = dx;
            if (e.cancelable) e.preventDefault();
        } else if (Math.abs(dy) > MOVE_TOLERANCE_PX || Math.abs(dx) > MOVE_TOLERANCE_PX) {
            if (state.timer) clearTimeout(state.timer);
            state = null;
        }
    }

    function onUp(e) {
        if (!state || e.pointerId !== state.pointerId) return;
        const s = state;
        state = null;
        if (s.timer) clearTimeout(s.timer);
        if (s.swiping) {
            const dx = s.dx || 0;
            suppressUntil = Date.now() + 400; // a swipe must never trigger the row tap
            if (s.kind === 'sheet') {
                if (Math.abs(dx) >= SWIPE_TRIGGER_PX) call(dotNet, 'OnSheetSwipe', dx > 0 ? 1 : -1);
                return;
            }
            const ref = s.kind === 'dup' ? dupRef : dotNet;
            const suffix = s.kind === 'dup' ? 'DupSwipeRight' : 'RowSwipeRight';
            const suffixL = s.kind === 'dup' ? 'DupSwipeLeft' : 'RowSwipeLeft';
            if (dx >= SWIPE_TRIGGER_PX) call(ref, 'On' + suffix, s.id);
            else if (dx <= -SWIPE_TRIGGER_PX) call(ref, 'On' + suffixL, s.id);
        }
    }

    // App bar edge: html.xbs-under while any content sits under the bar
    // (every page, not only Home), so the bar shadow can deepen.
    function onEdgeScroll() {
        document.documentElement.classList.toggle('xbs-under', window.scrollY > 4);
    }
    window.addEventListener('scroll', onEdgeScroll, { passive: true });

    // Short list (fits in ~60% of the space under the bars): the browse block
    // gets the full height and a bottom pad, so it sits just above mid-screen
    // with a big folder title; html.xbs-short lets the bar drop its title.
    // Measured on the list body only, so toggling never changes the input.
    const phoneLayout = window.matchMedia('(max-width: 640px), (max-height: 559px)');
    function fitBrowse() {
        const html = document.documentElement;
        const box = document.querySelector('.xbs-browse');
        const body = box && box.querySelector('.xbs-browse-body');
        if (!body) { html.classList.remove('xbs-short'); return; }
        const top = box.getBoundingClientRect().top + window.scrollY;
        const avail = window.innerHeight - top - 16;
        const short = phoneLayout.matches && body.offsetHeight + 96 <= avail * 0.6;
        box.classList.toggle('short', short);
        html.classList.toggle('xbs-short', short);
        box.style.minHeight = short ? avail + 'px' : '';
        box.style.paddingBottom = short ? Math.round(avail * 0.4) + 'px' : '';
    }
    window.addEventListener('resize', fitBrowse, { passive: true });

    // Crumb strip: jump to the end (current level) whenever the path changes,
    // and fade the left edge while earlier levels are scrolled out of view.
    function fitCrumbs() {
        const strip = document.querySelector('.xbs-crumbs');
        if (!strip) return;
        const key = strip.textContent;
        if (strip.dataset.key !== key) {
            strip.dataset.key = key;
            strip.scrollLeft = strip.scrollWidth;
        }
        if (!strip.dataset.bound) {
            strip.dataset.bound = '1';
            strip.addEventListener('scroll', () => strip.classList.toggle('faded', strip.scrollLeft > 2), { passive: true });
        }
        strip.classList.toggle('faded', strip.scrollLeft > 2);
    }

    function attach() {
        if (listenersAttached) return;
        listenersAttached = true;
        document.addEventListener('pointerdown', onDown, { passive: true });
        document.addEventListener('pointermove', onMove, { passive: false });
        document.addEventListener('pointerup', onUp, { passive: true });
        document.addEventListener('pointercancel', onUp, { passive: true });
        document.addEventListener('click', (e) => {
            if (Date.now() < suppressUntil && e.target.closest && e.target.closest('.xbs-row,.xbs-duprow')) {
                e.stopPropagation();
                e.preventDefault();
            }
        }, true);
        window.addEventListener('scroll', onScroll, { passive: true });
        ensureGoButtons();
    }

    // ---- Scroll jump (single button above the FAB + fast-scroll thumb) ----
    // One opaque button whose direction follows the scroll: ↓ after a screen
    // of scrolling down, ↑ after a screen up. The direction only flips after
    // a 48px reverse run (no flicker on micro-scrolls). A 3px ring shows the
    // position in the list. Long-press opens a Top/Bottom chooser. Lists over
    // 8 screens also get a draggable thumb on the right edge after a fling,
    // with the initial of the row under the viewport center.
    const GO_REVERSE_PX = 48;
    const GO_IDLE_MS = 2500;
    const GO_LABEL_MS = 1200;
    const GO_RING_C = 2 * Math.PI * 19.5;
    const THUMB_MIN_SCREENS = 8;
    const THUMB_FLING_PX_MS = 2.5;
    const THUMB_IDLE_MS = 1500;

    let go = null, goRing = null, goChip = null, goMenu = null, thumb = null, bubble = null;
    let goDir = null, run = 0, rev = 0, lastY = -1, lastT = 0, idleTimer = 0, chipTimer = 0, jumping = false, settleTimer = 0;
    let holding = false, pressTimer = 0, pressFired = false, chipShown = false;
    let thumbDrag = null, thumbTimer = 0;

    const ARROW_UP = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 19V6M6.5 11.5L12 6l5.5 5.5"/></svg>';
    const ARROW_DOWN = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v13M6.5 12.5L12 18l5.5-5.5"/></svg>';
    const EDGE_UP = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 5h12M12 20V9M7 14l5-5 5 5"/></svg>';
    const EDGE_DOWN = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 19h12M12 4v11M7 10l5 5 5-5"/></svg>';

    function reducedMotion() {
        try { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return false; }
    }

    function scrollMax() { return document.documentElement.scrollHeight - window.innerHeight; }

    function ensureGoButtons() {
        if (go) return;
        go = document.createElement('button');
        go.id = 'xbs-go';
        go.type = 'button';
        go.className = 'xbs-go';
        go.innerHTML =
            `<svg class="xbs-go-ring" viewBox="0 0 44 44" aria-hidden="true">` +
            `<circle class="xbs-go-track" cx="22" cy="22" r="19.5"/>` +
            `<circle class="xbs-go-fill" cx="22" cy="22" r="19.5" stroke-dasharray="0 ${GO_RING_C.toFixed(1)}"/></svg>` +
            `<span class="xbs-go-face"></span>`;
        goRing = go.querySelector('.xbs-go-fill');
        go.addEventListener('pointerdown', onGoDown);
        go.addEventListener('pointerup', onGoRelease);
        go.addEventListener('pointercancel', onGoRelease);
        go.addEventListener('pointerleave', onGoRelease);
        go.addEventListener('contextmenu', (e) => e.preventDefault());
        go.addEventListener('click', () => {
            if (pressFired) { pressFired = false; return; }
            jump(goDir === 'down' ? 'down' : 'up');
        });
        document.body.appendChild(go);

        goChip = document.createElement('span');
        goChip.className = 'xbs-go-chip';
        goChip.setAttribute('aria-hidden', 'true');
        document.body.appendChild(goChip);

        goMenu = document.createElement('div');
        goMenu.className = 'xbs-go-menu';
        goMenu.setAttribute('role', 'menu');
        goMenu.setAttribute('aria-label', str('jumpTo', 'Jump to'));
        goMenu.innerHTML =
            `<div class="xbs-go-menu-card">` +
            `<button type="button" role="menuitem" data-dir="up">${EDGE_UP}<span data-xbs-s="top">${str('top', 'Top')}</span></button>` +
            `<button type="button" role="menuitem" data-dir="down">${EDGE_DOWN}<span data-xbs-s="bottom">${str('bottom', 'Bottom')}</span></button></div>`;
        goMenu.addEventListener('click', (e) => {
            const b = e.target.closest && e.target.closest('button[data-dir]');
            closeMenu();
            if (b) jump(b.dataset.dir);
        });
        goMenu.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeMenu(); });
        document.body.appendChild(goMenu);

        thumb = document.createElement('div');
        thumb.className = 'xbs-thumb';
        thumb.setAttribute('aria-hidden', 'true');
        thumb.innerHTML = '<span class="xbs-thumb-bar"></span>';
        thumb.addEventListener('pointerdown', onThumbDown);
        thumb.addEventListener('pointermove', onThumbMove);
        thumb.addEventListener('pointerup', onThumbUp);
        thumb.addEventListener('pointercancel', onThumbUp);
        document.body.appendChild(thumb);

        bubble = document.createElement('div');
        bubble.className = 'xbs-thumb-bubble';
        bubble.setAttribute('aria-hidden', 'true');
        document.body.appendChild(bubble);
        lastY = window.scrollY;
        lastT = performance.now();
    }

    function modalOpen() {
        if (document.documentElement.classList.contains('xbs-locked')) return true;
        const t = document.querySelector('.navbar-toggler');
        if (!t) return false;
        if (t.checked) return true; // legacy checkbox toggler
        const ex = t.getAttribute('aria-expanded');
        return !!ex && ex.toLowerCase() === 'true';
    }

    // Re-label the floating jump controls after the language is applied.
    window.__xbsRelabel = function () {
        if (goMenu) {
            goMenu.setAttribute('aria-label', str('jumpTo', 'Jump to'));
            goMenu.querySelectorAll('[data-xbs-s]').forEach((el) => {
                el.textContent = str(el.dataset.xbsS, el.textContent);
            });
        }
        if (go && goDir) {
            const d = goDir;
            goDir = null;
            setDir(d);
        }
    };

    function setDir(dir) {
        if (goDir === dir) return;
        goDir = dir;
        go.querySelector('.xbs-go-face').innerHTML = dir === 'down' ? ARROW_DOWN : ARROW_UP;
        go.setAttribute('aria-label', dir === 'down' ? str('scrollBottom', 'Scroll to bottom') : str('scrollTop', 'Scroll to top'));
        goChip.textContent = dir === 'down' ? str('bottom', 'Bottom') : str('top', 'Top');
    }

    function showGo() {
        if (go.classList.contains('show')) return;
        go.classList.add('show');
        if (!chipShown) {
            // Name the button once per session, the first time it appears.
            chipShown = true;
            goChip.classList.add('show');
            clearTimeout(chipTimer);
            chipTimer = setTimeout(() => goChip.classList.remove('show'), GO_LABEL_MS);
        }
    }

    function hideGo() {
        if (!go || holding) return;
        go.classList.remove('show');
        goChip.classList.remove('show');
    }

    function updateRing(y, max) {
        const p = max > 0 ? Math.max(0, Math.min(1, y / max)) : 0;
        goRing.setAttribute('stroke-dasharray', `${(GO_RING_C * p).toFixed(1)} ${GO_RING_C.toFixed(1)}`);
    }

    function armIdle() {
        clearTimeout(idleTimer);
        idleTimer = setTimeout(hideGo, GO_IDLE_MS);
    }

    function onScroll() {
        if (!go) return;
        const y = window.scrollY;
        // Extended "Add" FAB near the top, compact + once the list scrolls.
        document.body.classList.toggle('xbs-scrolled', y > 160);
        const now = performance.now();
        const max = scrollMax();
        if (jumping) {
            // Programmatic glide: wait until it settles before reacting again.
            armSettle();
            lastY = y; lastT = now; run = 0; rev = 0;
            return;
        }
        if (modalOpen()) {
            hideGo(); closeMenu(); hideThumb(true);
            lastY = y; lastT = now; run = 0; rev = 0;
            return;
        }
        if (thumbDrag) { lastY = y; lastT = now; return; }
        // Lists shorter than 2 screens never get the button.
        if (document.documentElement.scrollHeight < window.innerHeight * 2 || lastY < 0) {
            hideGo(); hideThumb(true);
            lastY = y; lastT = now; run = 0; rev = 0;
            return;
        }
        const dy = y - lastY;
        const dt = Math.max(1, now - lastT);
        // run > 0 = consecutive distance down, < 0 = up. A reverse move is
        // banked in rev and only replaces the run past GO_REVERSE_PX.
        if (dy !== 0) {
            if (run === 0 || Math.sign(dy) === Math.sign(run)) { run += dy; rev = 0; }
            else { rev += dy; if (Math.abs(rev) > GO_REVERSE_PX) { run = rev; rev = 0; } }
        }
        const visible = go.classList.contains('show');
        if (run >= window.innerHeight || (visible && run > 0)) { setDir('down'); if (y < max - 40) showGo(); }
        else if (run <= -window.innerHeight || (visible && run < 0)) { setDir('up'); if (y > 40) showGo(); }
        // At the top it always rests; at the bottom ↓ has nothing left to do.
        if (y <= 40 || (goDir === 'down' && y >= max - 40)) {
            holding = false;
            hideGo();
        }
        updateRing(y, max);
        if (go.classList.contains('show')) armIdle();

        // Fast-scroll thumb: long lists only, revealed by a fling.
        if (max > window.innerHeight * THUMB_MIN_SCREENS) {
            if (Math.abs(dy) / dt > THUMB_FLING_PX_MS) thumb.classList.add('show');
            if (thumb.classList.contains('show')) { placeThumb(y, max); armThumbIdle(); }
        } else {
            hideThumb(true);
        }
        lastY = y;
        lastT = now;
    }

    function jump(dir) {
        holding = false;
        hideGo();
        hideThumb(true);
        // Don't let the programmatic glide re-trigger the button.
        jumping = true;
        armSettle();
        run = 0;
        rev = 0;
        const max = scrollMax();
        const top = dir === 'down' ? max : 0;
        const smooth = !reducedMotion();
        // Long lists: teleport to two screens from the edge, glide the rest
        // (a smooth scroll over the whole list takes seconds).
        const near = window.innerHeight * 2;
        if (smooth && Math.abs(top - window.scrollY) > near * 1.5) {
            window.scrollTo(0, dir === 'down' ? max - near : near);
        }
        try {
            window.scrollTo({ top, behavior: smooth ? 'smooth' : 'auto' });
        } catch {
            window.scrollTo(0, top);
        }
        // Land the eye: flash the first/last row once the glide settles.
        setTimeout(() => {
            const rows = document.querySelectorAll('.xbs-row:not(.xbs-row-sep) .xbs-row-inner');
            const el = rows.length ? rows[dir === 'down' ? rows.length - 1 : 0] : null;
            if (!el) return;
            el.classList.add('xbs-flash-once');
            setTimeout(() => el.classList.remove('xbs-flash-once'), 800);
        }, reducedMotion() ? 50 : 650);
    }

    function armSettle() {
        clearTimeout(settleTimer);
        settleTimer = setTimeout(() => { jumping = false; lastY = window.scrollY; }, 250);
    }

    // Long-press on the button: Top/Bottom chooser.
    function onGoDown(e) {
        if (e.pointerType === 'mouse' && e.button !== 0) return;
        holding = true;
        pressFired = false;
        clearTimeout(idleTimer);
        clearTimeout(pressTimer);
        pressTimer = setTimeout(() => {
            pressFired = true;
            try { if (navigator.vibrate) navigator.vibrate(20); } catch { /* noop */ }
            openMenu();
        }, LONG_PRESS_MS);
    }

    function onGoRelease() {
        clearTimeout(pressTimer);
        if (!holding) return;
        holding = false;
        if (!goMenu.classList.contains('show')) armIdle();
    }

    function openMenu() {
        holding = true;
        go.classList.remove('show');
        goChip.classList.remove('show');
        goMenu.classList.add('show');
        const first = goMenu.querySelector(`button[data-dir="${goDir === 'down' ? 'down' : 'up'}"]`);
        if (first) try { first.focus({ preventScroll: true }); } catch { /* noop */ }
    }

    function closeMenu() {
        if (!goMenu || !goMenu.classList.contains('show')) return;
        goMenu.classList.remove('show');
        holding = false;
    }

    // Fast-scroll thumb: drag maps 1:1 onto the document height.
    function thumbTrack() {
        const top = 72;
        const bottom = window.innerHeight - 160;
        return { top, span: Math.max(1, bottom - top - 48) };
    }

    function placeThumb(y, max) {
        const t = thumbTrack();
        const p = max > 0 ? Math.max(0, Math.min(1, y / max)) : 0;
        thumb.style.transform = `translateY(${(t.top + t.span * p).toFixed(0)}px)`;
        bubble.style.transform = `translateY(${(t.top + t.span * p - 6).toFixed(0)}px)`;
    }

    function armThumbIdle() {
        clearTimeout(thumbTimer);
        thumbTimer = setTimeout(() => hideThumb(false), THUMB_IDLE_MS);
    }

    function hideThumb(force) {
        if (!thumb || (thumbDrag && !force)) return;
        thumb.classList.remove('show', 'drag');
        bubble.classList.remove('show');
    }

    function rowInitial() {
        const el = document.elementFromPoint(window.innerWidth / 2, window.innerHeight / 2);
        const row = el && el.closest ? el.closest('.xbs-row') : null;
        const title = row ? row.querySelector('.xbs-row-title') : null;
        const m = title ? (title.textContent || '').match(/[\p{L}\p{N}]/u) : null;
        return m ? m[0].toUpperCase() : '';
    }

    function onThumbDown(e) {
        if (e.pointerType === 'mouse' && e.button !== 0) return;
        e.preventDefault();
        try { thumb.setPointerCapture(e.pointerId); } catch { /* noop */ }
        thumbDrag = { id: e.pointerId };
        clearTimeout(thumbTimer);
        thumb.classList.add('drag');
        hideGo();
        onThumbMove(e);
    }

    function onThumbMove(e) {
        if (!thumbDrag || e.pointerId !== thumbDrag.id) return;
        const t = thumbTrack();
        const p = Math.max(0, Math.min(1, (e.clientY - t.top - 24) / t.span));
        const max = scrollMax();
        window.scrollTo(0, p * max);
        placeThumb(p * max, max);
        const ch = rowInitial();
        bubble.textContent = ch;
        bubble.classList.toggle('show', !!ch);
    }

    function onThumbUp(e) {
        if (!thumbDrag || e.pointerId !== thumbDrag.id) return;
        thumbDrag = null;
        thumb.classList.remove('drag');
        bubble.classList.remove('show');
        lastY = window.scrollY;
        run = 0;
        rev = 0;
        armThumbIdle();
    }

    // Soft keyboard up (a text field has focus): floating bottom controls
    // step aside so they never cover the field or the rows above the keyboard.
    function isTextField(el) {
        if (!el) return false;
        if (el.isContentEditable || el.tagName === 'TEXTAREA') return true;
        return el.tagName === 'INPUT' && !/^(checkbox|radio|button|submit|range|color|file)$/i.test(el.type || '');
    }
    document.addEventListener('focusin', e => {
        document.documentElement.classList.toggle('xbs-typing', isTextField(e.target));
    });
    document.addEventListener('focusout', () => {
        setTimeout(() => {
            document.documentElement.classList.toggle('xbs-typing', isTextField(document.activeElement));
        }, 0);
    });
    // Back key hides the keyboard but keeps focus: the window grows back
    // (adjustResize), so bring the controls back; a tap on the field re-hides.
    let lastH = window.innerHeight;
    window.addEventListener('resize', () => {
        if (window.innerHeight - lastH > 120) document.documentElement.classList.remove('xbs-typing');
        lastH = window.innerHeight;
    });
    document.addEventListener('click', e => {
        if (isTextField(e.target)) document.documentElement.classList.add('xbs-typing');
    });

    return {
        init(ref) {
            // Home instances come and go with navigation (and the old one
            // disposes its reference): always point at the live instance.
            dotNet = ref;
            attach();
        },
        initDup(ref) {
            // Duplicates page instances come and go like Home's.
            dupRef = ref;
            attach();
        },
        closeAll() { /* kept for Blazor callers; no snapped rows anymore */ },
        lockScroll(on, owner) {
            // Owner-keyed so the nav drawer and modal sheets never fight:
            // the page stays locked while ANY holder asks for it.
            locks[owner || 'x'] = !!on;
            const any = Object.keys(locks).some(k => locks[k]);
            document.documentElement.classList.toggle('xbs-locked', any);
            if (document.body) document.body.classList.toggle('xbs-locked', any);
        },
        back() {
            // Android back key (MainActivity): dismiss the topmost transient
            // layer instead of navigating. Each dismiss control carries
            // data-back="<priority>"; highest priority wins, ties go to the
            // last one in the DOM (rendered on top). Returns false when
            // nothing was open, so the native side falls back to history.
            if (goMenu && goMenu.classList.contains('show')) { closeMenu(); return true; }
            let best = null, bestP = -1;
            document.querySelectorAll('[data-back]').forEach(el => {
                const p = parseInt(el.dataset.back, 10) || 0;
                if (p >= bestP && el.getClientRects().length) { best = el; bestP = p; }
            });
            if (!best) return false;
            best.click();
            return true;
        },
        focus(selector) {
            const el = document.querySelector(selector);
            if (el && el.focus) el.focus();
        },
        layoutBrowse() {
            fitCrumbs();
            fitBrowse();
        },
        scrollToRow(rowId) {
            const el = document.querySelector(`.xbs-row[data-rowid="${rowId}"]`);
            if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        }
    };
})();
