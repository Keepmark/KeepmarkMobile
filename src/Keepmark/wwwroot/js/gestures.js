// Swipe + long-press detection (delegation-based: survives Blazor re-renders).
// Taps and clicks are plain Blazor @onclick — this script never interferes.
// Explore rows: <div class="xbs-row" data-rowid="N">…</div>
//   swipe right/left -> OnRowSwipeRight/Left, long-press (500ms) -> OnRowLongPress
// Duplicates rows: <div class="xbs-duprow" data-rowid="N">…</div>
//   swipe right/left -> OnDupSwipeRight/Left (no long-press there)

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
            const ref = s.kind === 'dup' ? dupRef : dotNet;
            const suffix = s.kind === 'dup' ? 'DupSwipeRight' : 'RowSwipeRight';
            const suffixL = s.kind === 'dup' ? 'DupSwipeLeft' : 'RowSwipeLeft';
            if (dx >= SWIPE_TRIGGER_PX) call(ref, 'On' + suffix, s.id);
            else if (dx <= -SWIPE_TRIGGER_PX) call(ref, 'On' + suffixL, s.id);
        }
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

    // ---- Scroll jump arrows (single slot above the FAB, ↑/↓ reuse it) ----
    let goDown = null, goUp = null, lastY = -1, idleTimer = 0, jumpUntil = 0;

    function ensureGoButtons() {
        if (goDown) return;
        const mk = (id, label, dir) => {
            const b = document.createElement('button');
            b.id = id;
            b.type = 'button';
            b.className = 'xbs-go';
            b.setAttribute('aria-label', label);
            b.innerHTML = `<span aria-hidden="true">${dir === 'down' ? '↓' : '↑'}</span>`;
            b.addEventListener('click', () => jump(dir));
            document.body.appendChild(b);
            return b;
        };
        goDown = mk('xbs-go-down', 'Go to bottom', 'down');
        goUp = mk('xbs-go-up', 'Go to top', 'up');
    }

    function modalOpen() {
        if (document.documentElement.classList.contains('xbs-locked')) return true;
        const t = document.querySelector('.navbar-toggler');
        if (!t) return false;
        if (t.checked) return true; // legacy checkbox toggler
        const ex = t.getAttribute('aria-expanded');
        return !!ex && ex.toLowerCase() === 'true';
    }

    function hideGo() {
        if (goDown) goDown.classList.remove('show');
        if (goUp) goUp.classList.remove('show');
    }

    function onScroll() {
        if (!goDown || Date.now() < jumpUntil || modalOpen()) { hideGo(); lastY = window.scrollY; return; }
        const y = window.scrollY;
        const max = document.documentElement.scrollHeight - window.innerHeight;
        clearTimeout(idleTimer);
        if (max < 120 || lastY < 0) { lastY = y; return; }
        if (y > lastY + 4 && y < max - 40) {
            // Scrolling down: offer the jump to bottom.
            goUp.classList.remove('show');
            goDown.classList.add('show');
        } else if (y < lastY - 4 && y > 40) {
            // Scrolling up: offer the jump to top (same slot as ↓).
            goDown.classList.remove('show');
            goUp.classList.add('show');
        } else if (y <= 40 || y >= max - 40) {
            hideGo();
        }
        lastY = y;
        idleTimer = setTimeout(hideGo, 2000);
    }

    function jump(dir) {
        hideGo();
        // Don't let the programmatic glide re-trigger the arrows.
        jumpUntil = Date.now() + 900;
        lastY = -1;
        try {
            window.scrollTo({ top: dir === 'down' ? document.documentElement.scrollHeight : 0, behavior: 'smooth' });
        } catch {
            window.scrollTo(0, dir === 'down' ? document.documentElement.scrollHeight : 0);
        }
    }

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
        scrollToRow(rowId) {
            const el = document.querySelector(`.xbs-row[data-rowid="${rowId}"]`);
            if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        }
    };
})();
