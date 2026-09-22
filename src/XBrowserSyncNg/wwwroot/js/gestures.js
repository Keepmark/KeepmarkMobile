// Touch gestures for bookmark rows (delegation-based: survives Blazor re-renders).
// Rows: <div class="xbs-row" data-rowid="N"><div class="xbs-row-underlay">…</div><div class="xbs-row-inner">…</div></div>
//   tap          -> OnRowTap (260ms delay so double-tap wins when it happens)
//   double-tap   -> OnRowDoubleTap
//   long-press   -> OnRowLongPress (+ haptic)
//   swipe right  -> OnRowSwipeRight (pin), snap back
//   swipe left   -> snap open action underlay (Share/Delete buttons are plain clicks)
// Vertical scrolling is left to the browser (touch-action: pan-y on rows).

window.xbsGestures = (() => {
    let dotNet = null;
    let state = null;
    let lastTap = { rowId: -1, time: 0 };
    let pendingTapTimer = 0;
    let openRow = null;

    const LONG_PRESS_MS = 500;
    const DOUBLE_TAP_MS = 280;
    const TAP_DELAY_MS = 260;
    const SWIPE_OPEN_PX = 128;
    const SWIPE_TRIGGER_PX = 56;
    const MOVE_TOLERANCE_PX = 12;

    function rowOf(target) {
        if (target.closest && target.closest('button')) return null; // action buttons handle themselves
        return target.closest ? target.closest('.xbs-row') : null;
    }

    function rowIdOf(row) { return parseInt(row.dataset.rowid || '-1', 10); }

    function closeOpen(except) {
        if (openRow && openRow !== except) {
            const inner = openRow.querySelector('.xbs-row-inner');
            if (inner) inner.style.transform = '';
            openRow = null;
        }
    }

    function vibrate(ms) {
        try { if (navigator.vibrate) navigator.vibrate(ms); } catch { /* noop */ }
    }

    function onDown(e) {
        if (e.pointerType === 'mouse' && e.button !== 0) return;
        const row = rowOf(e.target);
        if (!row) return;
        closeOpen(row);
        state = {
            row, x0: e.clientX, y0: e.clientY, t0: Date.now(),
            swiping: false, longFired: false, pointerId: e.pointerId,
            timer: setTimeout(() => {
                if (state && !state.swiping) {
                    state.longFired = true;
                    vibrate(25);
                    dotNet.invokeMethodAsync('OnRowLongPress', rowIdOf(row));
                }
            }, LONG_PRESS_MS)
        };
    }

    function onMove(e) {
        if (!state || e.pointerId !== state.pointerId) return;
        const dx = e.clientX - state.x0;
        const dy = e.clientY - state.y0;
        if (!state.swiping && Math.abs(dx) > MOVE_TOLERANCE_PX && Math.abs(dx) > Math.abs(dy) * 1.2) {
            state.swiping = true;
            clearTimeout(state.timer);
            clearTimeout(pendingTapTimer);
        }
        if (state.swiping) {
            const clamped = Math.max(-160, Math.min(160, dx));
            state.row.querySelector('.xbs-row-inner').style.transform = `translateX(${clamped}px)`;
            state.dx = clamped;
            if (e.cancelable) e.preventDefault();
        } else if (Math.abs(dy) > MOVE_TOLERANCE_PX || Math.abs(dx) > MOVE_TOLERANCE_PX) {
            clearTimeout(state.timer); // scrolling: no long-press, no tap
            state.moved = true;
        }
    }

    function onUp(e) {
        if (!state || e.pointerId !== state.pointerId) return;
        const s = state;
        state = null;
        clearTimeout(s.timer);
        const inner = s.row.querySelector('.xbs-row-inner');
        const id = rowIdOf(s.row);

        if (s.swiping) {
            const dx = s.dx || 0;
            if (dx <= -SWIPE_TRIGGER_PX) {
                inner.style.transform = `translateX(${-SWIPE_OPEN_PX}px)`;
                openRow = s.row;
            } else if (dx >= SWIPE_TRIGGER_PX) {
                inner.style.transform = '';
                vibrate(15);
                dotNet.invokeMethodAsync('OnRowSwipeRight', id);
            } else {
                inner.style.transform = '';
            }
            return;
        }
        if (s.longFired || s.moved) return;

        // Tap (delayed so a second tap can claim it as double-tap).
        const now = Date.now();
        if (id === lastTap.rowId && now - lastTap.time < DOUBLE_TAP_MS) {
            clearTimeout(pendingTapTimer);
            lastTap = { rowId: -1, time: 0 };
            dotNet.invokeMethodAsync('OnRowDoubleTap', id);
        } else {
            lastTap = { rowId: id, time: now };
            inner.classList.add('xbs-tapped');
            pendingTapTimer = setTimeout(() => {
                inner.classList.remove('xbs-tapped');
                dotNet.invokeMethodAsync('OnRowTap', id);
            }, TAP_DELAY_MS);
        }
    }

    function onCancel(e) {
        if (state && e.pointerId === state.pointerId) {
            clearTimeout(state.timer);
            const inner = state.row.querySelector('.xbs-row-inner');
            if (inner && !openRow) inner.style.transform = '';
            state = null;
        }
        clearTimeout(pendingTapTimer);
    }

    return {
        init(ref) {
            if (dotNet) return;
            dotNet = ref;
            document.addEventListener('pointerdown', onDown, { passive: true });
            document.addEventListener('pointermove', onMove, { passive: false });
            document.addEventListener('pointerup', onUp, { passive: true });
            document.addEventListener('pointercancel', onCancel, { passive: true });
            document.addEventListener('click', (e) => {
                // A tap left a row snapped open: first tap closes it.
                if (openRow && !e.target.closest('.xbs-row-underlay') && !e.target.closest('.xbs-row')) closeOpen(null);
            }, { passive: true });
        },
        closeAll() { closeOpen(null); }
    };
})();
