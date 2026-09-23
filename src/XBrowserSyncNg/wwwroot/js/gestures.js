// Swipe + long-press detection (delegation-based: survives Blazor re-renders).
// Taps and clicks are plain Blazor @onclick — this script never interferes.
// Explore rows: <div class="xbs-row" data-rowid="N">…</div>
//   swipe right/left -> OnRowSwipeRight/Left, long-press (500ms) -> OnRowLongPress
// Duplicates rows: <div class="xbs-duprow" data-rowid="N">…</div>
//   swipe right/left -> OnDupSwipeRight/Left (no long-press there)

window.xbsGestures = (() => {
    let dotNet = null;
    let dupRef = null;
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
        document.addEventListener('pointermove', onMove, { passive: true });
        document.addEventListener('pointerup', onUp, { passive: true });
        document.addEventListener('pointercancel', onUp, { passive: true });
        document.addEventListener('click', (e) => {
            if (Date.now() < suppressUntil && e.target.closest && e.target.closest('.xbs-row,.xbs-duprow')) {
                e.stopPropagation();
                e.preventDefault();
            }
        }, true);
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
        scrollToRow(rowId) {
            const el = document.querySelector(`.xbs-row[data-rowid="${rowId}"]`);
            if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        }
    };
})();
