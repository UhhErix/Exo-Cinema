import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

// The player's clock (#pos, the bar, its thumb) is painted by a 4 Hz timer while a film plays and by the host's posts,
// not by a frame loop: a film watched from another window, with the controls idle, must still show the right time.
// The working tree is CRLF; the markers below are written with LF.
const source = readFileSync(new URL('../src/ExoCinema/wwwroot/player.js', import.meta.url), 'utf8').replace(/\r\n/g, '\n')
const slice = (from, to) => {
  const a = source.indexOf(from), b = source.indexOf(to, a)
  assert.ok(a >= 0 && b > a, `source markers not found: ${from.slice(0, 40)}`)
  return source.slice(a, b)
}
// fmt, the time readers and the clock's paint and timer, as they are in player.js.
const code = [
  slice('const fmt = ', 'const inPlayer'),
  slice('const AHEAD_MAX', '// The spinner says'),
  slice('// The clock, the bar and its thumb', "document.addEventListener('visibilitychange', paintClock)\n"),
].join('\n')

// The label is the whole second the clock was at the last paint: never ahead of the clock, and within the timer's
// spacing behind it (a frozen label would be tens of seconds behind).
function assertLags(p, message) {
  const [m, sec] = p.label().split(':').map(Number)
  const shown = m * 60 + sec
  const t = p.context.state.time
  assert.ok(shown <= t && t - shown < 1.5, `${message}: label ${p.label()} for clock ${t.toFixed(2)}`)
}

function makePlayer({ idle = false, hidden = false, duration = 6920, inPlayerNow = true } = {}) {
  const writes = { pos: 0 }
  const els = {}
  const el = (id) => (els[id] ??= { style: {}, textContent: '' })
  // Counts the label's writes, so a paused or empty player is seen not to write.
  let posText = ''
  Object.defineProperty(el('pos'), 'textContent', { get: () => posText, set: (v) => { writes.pos++; posText = v } })
  const frames = { count: 0 }
  const timers = new Map()
  let nextTimer = 1
  let now = 0
  const context = {
    $: (id) => el(id),
    stage: { classList: { contains: (c) => c === 'is-idle' && idle } },
    document: { hidden, addEventListener() {} },
    inPlayer: () => inPlayerNow,
    performance: { now: () => now },
    requestAnimationFrame: () => { frames.count++; return 1 },
    setInterval: (fn, ms) => { const id = nextTimer++; timers.set(id, { fn, ms, next: now + ms }); return id },
    clearInterval: (id) => { timers.delete(id) },
    state: { pause: false, duration, time: 0, timeAt: 0, cache: 0, seekTarget: null, dragging: false, waiting: false, props: {} },
  }
  vm.runInNewContext(code, context)
  // Runs the page's timers up to the fake time (the 4 Hz clock is the only one here).
  const advance = (ms) => {
    now = ms
    for (const t of timers.values()) if (now >= t.next) { t.next = now + t.ms; t.fn() }
  }
  return {
    context, els, writes, frames, timers, advance,
    setNow: (ms) => { now = ms },
    fmt: (s) => vm.runInContext(`fmt(${s})`, context),
    label: () => posText,
    timerRunning: () => timers.size > 0,
  }
}

test('the clock follows state.time on its own timer, with the controls idle and no frame loop', () => {
  const p = makePlayer({ idle: true })
  const { context } = p
  context.paintClock() // playback starts: painted once, then the timer takes over
  // mpv posts time-pos about every 100 ms while the film plays; each post sets the clock (as the prop case does).
  for (let ms = 0; ms <= 60_000; ms += 100) {
    context.state.time = ms / 1000
    context.state.timeAt = ms
    p.advance(ms)
    if (ms % 5000 === 0) assertLags(p, `label at ${ms} ms`)
  }
  assertLags(p, 'end')
  assert.equal(p.frames.count, 0, 'the clock never asks for a frame')
  assert.equal(p.timerRunning(), true)
})

test('the clock keeps its time through the timer alone, with no host posts', () => {
  const p = makePlayer()
  const { context } = p
  context.state.time = 100
  context.state.timeAt = 0
  context.paintClock()
  for (let ms = 250; ms <= 5000; ms += 250) p.advance(ms)
  // With nothing posted, the position runs on for a quarter second at most, then holds (a stalled stream).
  assert.equal(p.label(), '1:40')
  assert.equal(p.frames.count, 0)
})

test('the seek bar and its thumb follow the same clock', () => {
  const p = makePlayer()
  const { context, els } = p
  context.state.time = 3460
  context.state.timeAt = 0
  context.paintClock()
  assert.equal(els.played.style.transform, `scaleX(${3460 / 6920})`)
  assert.equal(els.thumbRail.style.translate, `${(3460 / 6920) * 100}% 0`)
  assert.equal(p.label(), '57:40')
  assert.equal(p.frames.count, 0)
})

test('paused or with no film, the timer is off and nothing is written', () => {
  const p = makePlayer({ duration: 0 })
  const { context } = p
  // No film: the clock is empty and no timer runs.
  context.paintClock()
  assert.equal(p.timerRunning(), false)
  const emptyWrites = p.writes.pos
  context.paintClock()
  assert.equal(p.writes.pos, emptyWrites, 'an unchanged clock is not written again')
  assert.equal(p.label(), '0:00')
  // Paused: no timer, the label holds where it stopped, and only a paint asked for by a post or a control writes it.
  context.state.duration = 6920
  context.state.pause = true
  context.state.time = 42
  context.paintClock()
  assert.equal(p.timerRunning(), false)
  const paused = p.writes.pos
  assert.equal(p.label(), '0:42')
  for (let ms = 250; ms <= 5000; ms += 250) p.advance(ms)
  assert.equal(p.writes.pos, paused)
  // Playing again starts the timer.
  context.state.pause = false
  context.paintClock()
  assert.equal(p.timerRunning(), true)
  assert.equal(p.frames.count, 0)
})

test('the clock is not painted or timed while the window is minimized or the player is not shown', () => {
  const minimized = makePlayer({ hidden: true })
  minimized.context.state.time = 10
  minimized.context.paintClock()
  assert.equal(minimized.label(), '')
  assert.equal(minimized.writes.pos, 0)
  assert.equal(minimized.timerRunning(), false)
  const library = makePlayer({ inPlayerNow: false })
  library.context.paintClock()
  assert.equal(library.timerRunning(), false)
  assert.equal(library.label(), '')
})

test('the clock has no frame loop left in it, and the host posts other than the position repaint it', () => {
  const clock = slice('// The clock, the bar and its thumb', "document.addEventListener('visibilitychange', paintClock)\n")
  assert.doesNotMatch(clock, /requestAnimationFrame|drawFrame/)
  assert.match(source, /case 'prop': \{[\s\S]*?\n      if \(msg\.name !== 'time-pos'\) paintClock\(\)\n      drawReadings\(\)/)
})
