const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');

function setup() {
    const handlers = {}, frames = [], timers = new Map(), present = new Set();
    let nextTimer = 0, now = 0;
    const windowHandlers = {};
    const element = () => ({
        classList: { toggle() {}, contains() { return false; }, add() {}, remove() {} },
        style: {}, dataset: {}, addEventListener() {}, setAttribute() {}, appendChild() {},
        querySelector() { return element(); }, querySelectorAll() { return []; }
    });
    const document = {
        documentElement: element(), body: element(), activeElement: null,
        addEventListener(name, callback) { if (!(handlers[name] ??= []).includes(callback)) handlers[name].push(callback); },
        removeEventListener(name, callback) { handlers[name] = (handlers[name] ?? []).filter(fn => fn !== callback); },
        createElement: element, querySelector(selector) { return present.has(selector) ? element() : null; }, querySelectorAll() { return []; }
    };
    const window = { addEventListener(name, callback) { (windowHandlers[name] ??= []).push(callback); }, matchMedia() { return { matches: false }; }, scrollY: 0 };
    vm.runInNewContext(readFileSync(join(__dirname, '../../src/KeepmarkMobile/wwwroot/js/gestures.js'), 'utf8'), {
        window, document, navigator: {}, performance: { now: () => now }, Date: { now: () => now },
        requestAnimationFrame(callback) { frames.push(callback); return frames.length; },
        setTimeout(callback) { const id = ++nextTimer; timers.set(id, callback); return id; },
        clearTimeout(id) { timers.delete(id); }
    });
    const emit = (name, event) => handlers[name]?.forEach(fn => fn(event));
    const row = element();
    row.dataset = { rowid: '7', nodeid: '42', contentVersion: '19' };
    row.isConnected = true;
    const classes = new Set();
    row.classList = { add: name => classes.add(name), remove: name => classes.delete(name), contains: name => classes.has(name) };
    const event = x => ({ target: { closest(selector) { return selector === '.xbs-row' ? row : null; } },
        clientX: x, clientY: 0, pointerId: 1, cancelable: true, preventDefault() {} });
    const swipe = () => { emit('pointerdown', event(0)); emit('pointermove', event(90)); emit('pointerup', event(90)); };
    return { api: window.xbsGestures, window, frames, timers, emit, event, swipe, row, present,
        listening: name => (handlers[name] ?? []).length,
        setNow(value) { now = value; },
        emitWindow(name) { windowHandlers[name]?.forEach(fn => fn()); },
        fireTimers() { const pending = [...timers.values()]; timers.clear(); pending.forEach(fn => fn()); }
    };
}

test('late disposal does not release the new Home; current disposal stops gestures', () => {
    const ui = setup(), calls = [];
    ui.api.init({ invokeMethodAsync() { calls.push('old'); return Promise.resolve(); } }, 'old');
    ui.api.init({ invokeMethodAsync(method, id) { calls.push([method, id]); return Promise.resolve(); } }, 'new');
    ui.api.release('old');
    ui.swipe();
    assert.deepEqual(calls, [['OnRowSwipeRight', 7]]);
    ui.api.release('new');
    ui.swipe();
    assert.equal(calls.length, 1);
});

test('disposal cancels an outstanding long press', () => {
    const ui = setup();
    ui.api.init({ invokeMethodAsync() { throw Error('disposed'); } }, 'page');
    ui.emit('pointerdown', ui.event(0));
    assert.equal(ui.timers.size, 1);
    ui.api.release('page');
    assert.equal(ui.timers.size, 0);
});

test('multiple layout requests are coalesced into one animation frame', () => {
    const ui = setup();
    for (let i = 0; i < 20; i++) ui.api.layoutBrowse();
    assert.equal(ui.frames.length, 1);
    ui.frames[0]();
    ui.api.layoutBrowse();
    assert.equal(ui.frames.length, 2);
});

function recording(ui) {
    const calls = [];
    ui.api.init({ invokeMethodAsync(...args) { calls.push(args); return Promise.resolve(); } }, 'page');
    return calls;
}

// One-finger drag from (0,0) to (dx,dy); returns how many moves were claimed.
function drag(ui, dx, dy, { cancelable = true, end = 'touchend' } = {}) {
    let prevented = 0;
    const touch = (x, y) => ({ identifier: 5, clientX: x, clientY: y });
    const target = { closest() { return null; } };
    ui.emit('touchstart', { target, touches: [touch(0, 0)] });
    for (const f of [0.25, 1]) {
        ui.emit('touchmove', { target, touches: [touch(dx * f, dy * f)], cancelable, preventDefault() { prevented++; } });
    }
    ui.emit(end, { target, touches: [], changedTouches: [touch(dx, dy)] });
    return prevented;
}

test('pull past the threshold syncs once; a second pull waits for the first to finish', async () => {
    const ui = setup(), calls = [];
    let finish;
    ui.api.init({ invokeMethodAsync(...args) { calls.push(args); return new Promise(resolve => { finish = resolve; }); } }, 'page');
    assert.equal(drag(ui, 0, 200), 2);
    assert.deepEqual(calls, [['OnPullRefresh']]);
    assert.equal(drag(ui, 0, 200), 0);
    assert.equal(calls.length, 1);
    finish();
    await new Promise(resolve => setImmediate(resolve));
    drag(ui, 0, 200);
    assert.equal(calls.length, 2);
});

test('short, upward, sideways, cancelled and non-cancelable drags never sync', () => {
    const ui = setup(), calls = recording(ui);
    assert.equal(drag(ui, 0, 100), 2); // claimed, but released before 64px of travel
    assert.equal(drag(ui, 0, -200), 0);
    assert.equal(drag(ui, 200, 120), 0);
    assert.equal(drag(ui, 0, 200, { cancelable: false }), 0);
    drag(ui, 0, 200, { end: 'touchcancel' });
    assert.deepEqual(calls, []);
});

test('pull needs a live Home and the page at the very top', () => {
    const ui = setup();
    assert.equal(drag(ui, 0, 200), 0);
    const calls = recording(ui);
    ui.window.scrollY = 30;
    assert.equal(drag(ui, 0, 200), 0);
    ui.api.release('page');
    ui.window.scrollY = 0;
    assert.equal(drag(ui, 0, 200), 0);
    assert.deepEqual(calls, []);
});

test('long press captures stable identity and content version and removes press highlight', () => {
    const ui = setup(), calls = recording(ui);
    ui.emit('pointerdown', ui.event(0));
    assert.equal(ui.row.classList.contains('xbs-pressing'), true);
    ui.fireTimers();
    assert.deepEqual(calls, [['OnRowLongPress', 7, 42, 19]]);
    assert.equal(ui.row.classList.contains('xbs-pressing'), false);
});

test('a row reused or changed after pointerdown never invokes long press', () => {
    for (const change of [row => row.dataset.nodeid = '43', row => row.dataset.contentVersion = '20', row => row.isConnected = false]) {
        const ui = setup(), calls = recording(ui);
        ui.emit('pointerdown', ui.event(0));
        change(ui.row);
        ui.fireTimers();
        assert.deepEqual(calls, []);
    }
});

test('holding beyond initial suppression still suppresses the finger-up click', () => {
    const ui = setup(); recording(ui);
    ui.emit('pointerdown', ui.event(0));
    ui.setNow(500); ui.fireTimers();
    ui.setNow(2500); ui.emit('pointerup', ui.event(0));
    let stopped = false, prevented = false;
    ui.emit('click', { target: { closest() { return ui.row; } },
        stopPropagation() { stopped = true; }, preventDefault() { prevented = true; } });
    assert.equal(stopped, true);
    assert.equal(prevented, true);
});

test('selection disables swipe and long press without preventing vertical scroll', () => {
    const ui = setup(), calls = recording(ui);
    ui.row.dataset.selection = '1';
    let prevented = false;
    const move = { ...ui.event(90), clientY: 100, preventDefault() { prevented = true; } };
    ui.emit('pointerdown', ui.event(0)); ui.emit('pointermove', move); ui.emit('pointerup', move);
    ui.fireTimers();
    assert.deepEqual(calls, []);
    assert.equal(prevented, false);
    ui.emit('pointerdown', ui.event(0)); ui.fireTimers();
    assert.deepEqual(calls, []);
});

test('pointer cancellation and scroll clear pending presses; cancelled swipes never execute', () => {
    for (const cancel of [ui => ui.emit('pointercancel', ui.event(0)), ui => ui.emitWindow('scroll')]) {
        const ui = setup(), calls = recording(ui);
        ui.emit('pointerdown', ui.event(0)); cancel(ui); ui.fireTimers();
        assert.deepEqual(calls, []);
        assert.equal(ui.row.classList.contains('xbs-pressing'), false);
    }
    const ui = setup(), calls = recording(ui);
    ui.emit('pointerdown', ui.event(0)); ui.emit('pointermove', ui.event(90));
    ui.emit('pointercancel', ui.event(90)); ui.emit('pointerup', ui.event(90));
    assert.deepEqual(calls, []);
});

test('a failed sync frees the gesture; a sync that never answers is released by the watchdog', async () => {
    const ui = setup(), calls = [];
    ui.api.init({ invokeMethodAsync(...args) { calls.push(args); return calls.length === 1 ? Promise.reject(new Error('boom')) : new Promise(() => {}); } }, 'page');
    drag(ui, 0, 200);
    await new Promise(resolve => setImmediate(resolve));
    drag(ui, 0, 200); // the rejection cleared the busy flag
    assert.equal(calls.length, 2);
    assert.equal(drag(ui, 0, 200), 0); // second sync still pending
    ui.fireTimers(); // watchdog
    drag(ui, 0, 200);
    assert.equal(calls.length, 3);
});

test('selection or a modal that starts after touchstart blocks the pull', () => {
    const ui = setup(), calls = recording(ui);
    ui.present.add('.xbs-selection-actions');
    assert.equal(drag(ui, 0, 200), 0);
    ui.present.clear();
    assert.equal(drag(ui, 0, 200), 2);
    assert.equal(calls.length, 1);
});

test('only a live Home keeps the non-passive touchmove listener', () => {
    const ui = setup();
    assert.equal(ui.listening('touchmove'), 0);
    ui.api.init({ invokeMethodAsync() { return Promise.resolve(); } }, 'a');
    ui.api.init({ invokeMethodAsync() { return Promise.resolve(); } }, 'b');
    assert.equal(ui.listening('touchmove'), 1);
    ui.api.release('a'); // stale owner: the live Home keeps it
    assert.equal(ui.listening('touchmove'), 1);
    ui.api.release('b');
    assert.equal(ui.listening('touchmove'), 0);
});

test('a wobble under the decision distance does not discard the pull', () => {
    const ui = setup(), calls = recording(ui);
    const touch = (x, y) => ({ identifier: 5, clientX: x, clientY: y });
    const target = { closest() { return null; } };
    const move = (x, y) => ui.emit('touchmove', { target, touches: [touch(x, y)], cancelable: true, preventDefault() {} });
    ui.emit('touchstart', { target, touches: [touch(0, 0)] });
    move(1, -2); move(0, 60); move(0, 200);
    ui.emit('touchend', { target, touches: [], changedTouches: [touch(0, 200)] });
    assert.deepEqual(calls, [['OnPullRefresh']]);
});

test('a sync that outlives the watchdog cannot free the pull that followed it', async () => {
    const ui = setup(), calls = [], finish = [];
    ui.api.init({ invokeMethodAsync(...args) { calls.push(args); return new Promise(resolve => finish.push(resolve)); } }, 'page');
    drag(ui, 0, 200);
    ui.fireTimers(); // watchdog frees the first pull
    drag(ui, 0, 200); // second pull starts
    assert.equal(calls.length, 2);
    finish[0](); // the late first run answers
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(drag(ui, 0, 200), 0); // the second pull is still busy
    assert.equal(calls.length, 2);
    finish[1]();
    await new Promise(resolve => setImmediate(resolve));
    drag(ui, 0, 200);
    assert.equal(calls.length, 3);
});
