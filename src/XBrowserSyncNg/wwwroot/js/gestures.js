// Long-press detection only (delegation-based: survives Blazor re-renders).
// Taps and clicks are plain Blazor @onclick — this script never interferes.
// Rows: <div class="xbs-row" data-rowid="N">…</div>
//   long-press (500ms, no move) -> OnRowLongPress (+ haptic)

window.xbsGestures = (() => {
    let dotNet = null;
    let state = null;
    let suppressUntil = 0;

    const LONG_PRESS_MS = 500;
    const MOVE_TOLERANCE_PX = 12;
    const SWIPE_TRIGGER_PX = 60;

    function onDown(e) {
        if (e.pointerType === 'mouse' && e.button !== 0) return;
        if (e.target.closest && e.target.closest('button')) return;
        const row = e.target.closest ? e.target.closest('.xbs-row') : null;
        if (!row || !row.dataset.rowid) return;
        const id = parseInt(row.dataset.rowid, 10);
        state = {
            id, x0: e.clientX, y0: e.clientY, pointerId: e.pointerId,
            timer: setTimeout(() => {
                state = null;
                try { if (navigator.vibrate) navigator.vibrate(25); } catch { /* noop */ }
                dotNet.invokeMethodAsync('OnRowLongPress', id);
            }, LONG_PRESS_MS)
        };
    }

    function onMove(e) {
        if (!state || e.pointerId !== state.pointerId) return;
        const dx = e.clientX - state.x0;
        const dy = e.clientY - state.y0;
        if (!state.swiping && Math.abs(dx) > MOVE_TOLERANCE_PX && Math.abs(dx) > Math.abs(dy) * 1.5) {
            state.swiping = true;
            clearTimeout(state.timer);
        }
        if (state.swiping) {
            state.dx = dx;
            if (e.cancelable) e.preventDefault();
        } else if (Math.abs(dy) > MOVE_TOLERANCE_PX || Math.abs(dx) > MOVE_TOLERANCE_PX) {
            clearTimeout(state.timer);
            state = null;
        }
    }

    function onUp(e) {
        if (!state || e.pointerId !== state.pointerId) return;
        const s = state;
        state = null;
        clearTimeout(s.timer);
        if (s.swiping) {
            const dx = s.dx || 0;
            suppressUntil = Date.now() + 400; // a swipe must never trigger the row tap
            if (dx >= SWIPE_TRIGGER_PX) dotNet.invokeMethodAsync('OnRowSwipeRight', s.id);
            else if (dx <= -SWIPE_TRIGGER_PX) dotNet.invokeMethodAsync('OnRowSwipeLeft', s.id);
        }
    }

    return {
        init(ref) {
            if (dotNet) return;
            dotNet = ref;
            document.addEventListener('pointerdown', onDown, { passive: true });
            document.addEventListener('pointermove', onMove, { passive: true });
            document.addEventListener('pointerup', onUp, { passive: true });
            document.addEventListener('pointercancel', onUp, { passive: true });
            document.addEventListener('click', (e) => {
                if (Date.now() < suppressUntil && e.target.closest && e.target.closest('.xbs-row')) {
                    e.stopPropagation();
                    e.preventDefault();
                }
            }, true);
        },
        closeAll() { /* kept for Blazor callers; no snapped rows anymore */ },
        scrollToRow(rowId) {
            const el = document.querySelector(`.xbs-row[data-rowid="${rowId}"]`);
            if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        }
    };
})();
