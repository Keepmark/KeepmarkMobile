// Long-press detection only (delegation-based: survives Blazor re-renders).
// Taps and clicks are plain Blazor @onclick — this script never interferes.
// Rows: <div class="xbs-row" data-rowid="N">…</div>
//   long-press (500ms, no move) -> OnRowLongPress (+ haptic)

window.xbsGestures = (() => {
    let dotNet = null;
    let state = null;

    const LONG_PRESS_MS = 500;
    const MOVE_TOLERANCE_PX = 12;

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
        if (Math.abs(e.clientX - state.x0) > MOVE_TOLERANCE_PX ||
            Math.abs(e.clientY - state.y0) > MOVE_TOLERANCE_PX) {
            clearTimeout(state.timer);
            state = null;
        }
    }

    function onUp(e) {
        if (state && e.pointerId === state.pointerId) {
            clearTimeout(state.timer);
            state = null;
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
        },
        closeAll() { /* kept for Blazor callers; no snapped rows anymore */ }
    };
})();
