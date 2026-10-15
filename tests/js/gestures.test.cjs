const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');

function setup() {
    const handlers = {}, frames = [], timers = new Map();
    let nextTimer = 0;
    const element = () => ({
        classList: { toggle() {}, contains() { return false; }, add() {}, remove() {} },
        style: {}, dataset: {}, addEventListener() {}, setAttribute() {}, appendChild() {},
        querySelector() { return element(); }, querySelectorAll() { return []; }
    });
    const document = {
        documentElement: element(), body: element(), activeElement: null,
        addEventListener(name, callback) { (handlers[name] ??= []).push(callback); },
        createElement: element, querySelector() { return null; }, querySelectorAll() { return []; }
    };
    const window = { addEventListener() {}, matchMedia() { return { matches: false }; }, scrollY: 0 };
    vm.runInNewContext(readFileSync(join(__dirname, '../../src/KeepmarkMobile/wwwroot/js/gestures.js'), 'utf8'), {
        window, document, navigator: {}, performance: { now: () => 0 }, Date,
        requestAnimationFrame(callback) { frames.push(callback); return frames.length; },
        setTimeout(callback) { const id = ++nextTimer; timers.set(id, callback); return id; },
        clearTimeout(id) { timers.delete(id); }
    });
    const emit = (name, event) => handlers[name]?.forEach(fn => fn(event));
    const row = { dataset: { rowid: '7' } };
    const event = x => ({ target: { closest(selector) { return selector === '.xbs-row' ? row : null; } },
        clientX: x, clientY: 0, pointerId: 1, cancelable: true, preventDefault() {} });
    const swipe = () => { emit('pointerdown', event(0)); emit('pointermove', event(90)); emit('pointerup', event(90)); };
    return { api: window.xbsGestures, frames, timers, emit, event, swipe };
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
