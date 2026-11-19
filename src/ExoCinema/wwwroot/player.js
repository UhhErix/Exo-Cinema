// Exo Cinema's player chrome. The host (PlayerWindow.cs) owns mpv; this page sends commands and draws state
// from the property changes the host posts.

const $ = (id) => document.getElementById(id)
const stage = $('stage')
const send = (msg) => window.chrome.webview.postMessage(msg)

const state = {
  pause: true,
  duration: 0,
  time: 0,
  timeAt: performance.now(),
  cache: 0,
  buffering: false,
  // The read-ahead hold the host reports ("buffer": the wait it holds for, and whether the link is too slow for the film).
  buffer: null,
  mute: false,
  fullscreen: false,
  dragging: false,
  props: {},
  timings: {},
  tuning: {},
}

// The host's replies and pushes, for app.js too.
const listeners = new Set()
const onHost = (fn) => listeners.add(fn)

const fmt = (s) => {
  if (!Number.isFinite(s) || s < 0) s = 0
  s = Math.floor(s)
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60
  return (h ? `${h}:${String(m).padStart(2, '0')}` : `${m}`) + `:${String(sec).padStart(2, '0')}`
}

const inPlayer = () => document.body.classList.contains('mode-player')

// Between the host's posts (every 100 ms) the position moves on its own, so the bar glides; never more than a quarter
// second past mpv's last word, so a stream that stalls without saying so can't run the bar ahead of the picture.
// While a seek is on its way the bar holds its target.
const AHEAD_MAX = 0.25

function currentTime() {
  if (state.seekTarget != null) return state.seekTarget
  if (state.pause || state.waiting) return state.time
  return Math.min(state.duration || Infinity, state.time + Math.min(AHEAD_MAX, (performance.now() - state.timeAt) / 1000))
}

// The spinner says what mpv is doing, never what an event last said: loading a file, seeking, or out of data while
// playing. It shows after a beat, so a quick seek doesn't flash it.
let spinnerTimer = 0
function drawSpinner() {
  const p = state.props
  const busy = inPlayer() && !p['eof-reached'] && (state.loading || p.seeking || p['paused-for-cache'] || (p['core-idle'] && !state.pause))
  state.waiting = !!busy
  clearTimeout(spinnerTimer)
  if (!busy) { $('spinner').hidden = true; return }
  spinnerTimer = setTimeout(() => { if (state.waiting) $('spinner').hidden = false }, 250)
  drawBuffering()
}

// The read-ahead hold (the host's "buffer" message, StartBuffer): a calm line under the spinner while mpv holds playback
// for the read-ahead its link needs. It is over the picture, never the controls, takes no clicks, and goes as soon as
// the hold does (or the player does). Its bar is the read-ahead gathered so far against the wait, when mpv has said how
// far it has read. Nothing animates in a loop: the bar moves when mpv reports, and the fade is one pass.
const bufferLine = Object.assign(document.createElement('div'), { className: 'exo-glass is-panel buffering', hidden: true })
bufferLine.setAttribute('role', 'status')
bufferLine.setAttribute('data-over-video', '')
bufferLine.innerHTML = '<span class="buffering-words">Loading ahead so it won\'t stop</span>'
  + '<span class="buffering-track"><span class="buffering-fill"></span></span>'
  + '<span class="buffering-note">Your connection or the source is slower than this video</span>'
stage.append(bufferLine)
const bufferFill = bufferLine.querySelector('.buffering-fill')
const bufferTrack = bufferLine.querySelector('.buffering-track')
const bufferNote = bufferLine.querySelector('.buffering-note')
let bufferShown = false

function drawBuffering() {
  const b = state.buffer
  const holding = !!b && inPlayer() && !state.props['eof-reached'] && !!state.props['paused-for-cache']
  if (!holding) {
    if (bufferShown) { bufferShown = false; bufferLine.hidden = true; placeGlass() }
    return
  }
  if (!bufferShown) { bufferShown = true; bufferLine.hidden = false; placeGlass() }
  bufferNote.hidden = !b.slow
  // mpv's cache end, less where playback is held: the read-ahead in hand, as a share of the wait.
  const known = state.cache > 0 && b.wait > 0
  bufferTrack.hidden = !known
  if (known) bufferFill.style.transform = `scaleX(${Math.min(1, Math.max(0, state.cache - state.time) / b.wait)})`
}

// The clock, the bar and its thumb. They are painted four times a second while a film plays (the timer below), and when the
// host posts a change other than the position, a seek or a drag moves them, or the controls or the window come back.
// That timer is the only thing that runs on its own, and only while a film is playing and shown: it stops when paused,
// when nothing is open, and when the window is minimized. There is no frame loop, so nothing waits on the window's
// focus, its frames, or the controls (is-idle hides them 2.5 s after the pointer last moved, and the loop this replaced
// stopped with them, so a film watched from another window kept its first second on the clock).
let clockTimer = 0
function syncClock() {
  const playing = inPlayer() && !document.hidden && state.duration > 0 && !state.pause
  if (playing && !clockTimer) clockTimer = setInterval(paintClock, 250)
  else if (!playing && clockTimer) { clearInterval(clockTimer); clockTimer = 0 }
}
function paintClock() {
  syncClock()
  if (!inPlayer() || document.hidden) return
  const t = state.dragging ? state.dragTime : currentTime()
  const d = state.duration
  const p = d > 0 ? Math.min(1, t / d) : 0
  $('played').style.transform = `scaleX(${p})`
  $('thumbRail').style.translate = `${p * 100}% 0`
  $('cached').style.transform = `scaleX(${d > 0 ? Math.min(1, state.cache / d) : 0})`
  const pos = fmt(t), dur = fmt(d)
  if ($('pos').textContent !== pos) $('pos').textContent = pos
  if ($('dur').textContent !== dur) $('dur').textContent = dur
}
document.addEventListener('visibilitychange', paintClock)

function setPause(paused) {
  state.pause = paused
  const btn = document.querySelector('.play')
  glyph(btn.querySelector('svg'), paused ? 'player-play-filled' : 'player-pause-filled')
  btn.setAttribute('aria-label', paused ? 'Play' : 'Pause')
  wake()
}

function drawReadings() {
  const el = $('readings')
  if (el.hidden) return
  const p = state.props
  const rows = [
    ['GPU', state.tuning.gpu],
    ['Picture', state.picture],
    ['Motion', state.motion],
    ['Plays by', state.playMethod],
    // Software decoding says what it is decoding, which says why (10-bit H.264 has no hardware decoder anywhere).
    ['Decoder', p['hwdec-current'] && p['hwdec-current'] !== 'no' ? p['hwdec-current'] : `software · ${[p['video-format'], p['video-params/pixelformat']].filter(Boolean).join(' ')}`],
    ['Video', [p['video-codec']?.split(' ')[0], p['video-params/w'] && `${p['video-params/w']}×${p['video-params/h']}`].filter(Boolean).join(' · ')],
    ['Frame rate', p['container-fps'] ? p['container-fps'].toFixed(3) : ''],
    ['Transfer', [p['video-params/gamma'], p['video-params/primaries']].filter(Boolean).join(' · ')],
    ['Audio', [p['audio-codec-name'], passingThrough() && 'passthrough'].filter(Boolean).join(' · ')],
    ['First frame', state.timings['first-frame'] != null ? `${state.timings['first-frame']} ms` : ''],
    ['Last seek', state.timings.seek != null ? `${state.timings.seek} ms` : ''],
  ].filter(([, v]) => v)
  el.innerHTML = `<dl>${rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')}</dl>`
  placeGlass()
}

// Glass over video. mpv blurs and tones the picture under the page's glass itself as it draws each frame
// (Shaders/exo-glass.glsl), since backdrop-filter can't see it. The host gets the glass shapes in device pixels,
// topmost first, with how visible each is; while anything over the video moves or fades, they follow it.
let glassSent = ''
let glassUntil = 0
let glassLoop = false
let glassAt = 0

const half = (v) => Math.round(v * 2) / 2
// Device pixels per page pixel: Windows' scale times the page's zoom on a screen bigger than 1080p (app.js › page-zoom).
const dpr = () => devicePixelRatio * (window.exoPageZoom || 1)

function glassShapes() {
  if (!inPlayer() || document.documentElement.dataset.material === 'solid') return []
  const k = dpr()
  const shapes = []
  for (const el of [...document.querySelectorAll('[data-over-video]')].reverse()) {
    if (el.hidden) continue
    let seen = 1
    for (let e = el; e && e !== document.body; e = e.parentElement) seen *= Number(getComputedStyle(e).opacity)
    const r = el.getBoundingClientRect()
    if (seen < 0.01 || !r.width || !r.height) continue
    const radius = Math.min(parseFloat(getComputedStyle(el).borderTopLeftRadius) || 0, r.height / 2, r.width / 2)
    shapes.push({ x: half(r.left * k), y: half(r.top * k), w: half(r.width * k), h: half(r.height * k), r: half(radius * k), k: el.classList.contains('is-panel') ? 1 : 0, a: Math.round(seen * 100) / 100 })
  }
  return shapes
}

function sendGlass() {
  const showing = inPlayer() && !document.hidden && !stage.classList.contains('is-idle')
  const msg = {
    shapes: showing ? glassShapes() : [],
    scale: dpr(),
    // Where the subtitles rise to while the controls show: just above them.
    lift: showing ? half((innerHeight - document.querySelector('.controls').getBoundingClientRect().top + 8) * dpr()) : 0,
  }
  const key = JSON.stringify(msg)
  if (key === glassSent) return
  glassSent = key
  send({ cmd: 'glass', ...msg })
}

// The shapes now and through whatever has just started moving (30 times a second at most).
function placeGlass(ms = 450) {
  sendGlass()
  if (!inPlayer() || document.hidden || stage.classList.contains('is-idle')) return
  glassUntil = Math.max(glassUntil, performance.now() + ms)
  if (glassLoop) return
  glassLoop = true
  const step = (t) => {
    if (!inPlayer() || document.hidden || stage.classList.contains('is-idle')) { glassLoop = false; sendGlass(); return }
    if (t - glassAt >= 33) { glassAt = t; sendGlass() }
    if (t < glassUntil) requestAnimationFrame(step)
    else { glassLoop = false; sendGlass() }
  }
  requestAnimationFrame(step)
}

// Each glass shape follows its own size too: a longer title or a menu filling in changes it with nothing else moving.
const glassSizes = new ResizeObserver(() => placeGlass())
glassSizes.observe(document.documentElement)
for (const el of document.querySelectorAll('[data-over-video]')) glassSizes.observe(el)
for (const type of ['transitionrun', 'animationstart']) stage.addEventListener(type, () => placeGlass())

window.chrome.webview.addEventListener('message', ({ data: msg }) => {
  switch (msg.type) {
    case 'playing':
      // What the host plays without the page having asked (a file opened with Cinema): the player comes up around it.
      if (!document.body.classList.contains('mode-player')) {
        enterPlayer('Direct play')
        // It started before the page could hear it: whether it's paused is asked, as after a load.
        send({ cmd: 'get', name: 'pause' })
        if (msg.started) { state.loading = false; drawSpinner() }
      }
      break
    case 'hello':
      state.tuning = msg.tuning
      state.picture = msg.picture
      state.motion = msg.motion
      Object.assign(state.timings, msg.timings)
      break
    case 'picture':
      state.picture = msg.text
      drawReadings()
      break
    // Smooth motion, when chosen in Settings › Playback: how the video is timed to the screen, or why it isn't.
    case 'motion':
      state.motion = msg.text
      drawReadings()
      break
    case 'video-glass':
      document.documentElement.dataset.videoGlass = msg.ok ? 'gpu' : 'still'
      break
    case 'prop': {
      state.props[msg.name] = msg.value
      switch (msg.name) {
        case 'pause':
          // Held exactly where it stopped, not where the glide had got to.
          if (msg.value) { state.time = currentTime(); state.timeAt = performance.now() }
          setPause(!!msg.value)
          drawSpinner()
          break
        case 'duration': state.duration = msg.value || 0; break
        case 'time-pos':
          // A seek still held with nothing on its way (one that landed off its mark): the bar follows mpv again.
          if (state.seekTarget != null && !state.props.seeking && !state.loading && performance.now() - (state.restartAt ?? 0) > 2000 && performance.now() - state.seekAt > 2000) settleSeek()
          if (msg.value != null && state.seekTarget == null) { state.time = msg.value; state.timeAt = performance.now() }
          break
        case 'demuxer-cache-time': state.cache = msg.value || 0; drawBuffering(); break
        case 'paused-for-cache':
        case 'core-idle':
        case 'seeking':
          drawSpinner()
          break
        case 'mute':
          state.mute = !!msg.value
          drawVolume()
          break
        case 'volume':
          if (!volumeDragging && typeof msg.value === 'number') { state.volume = msg.value; drawVolume() }
          break
        case 'audio-out-params/format':
          // Passed through to a receiver, the sound can't be made louder or quieter here: the receiver's volume does it.
          volumeInput.closest('.volume').hidden = passingThrough()
          break
        case 'media-title': $('title').textContent = msg.value || ''; break
        case 'eof-reached':
          drawSpinner()
          if (!msg.value || !inPlayer() || state.loading) break
          if (currentTime() < 1) streamFailed('The stream ended as it started.')
          else if (readAgain()) break
          // The end: the next episode if there is one (playerplus.js), otherwise back where it was opened from.
          else if (!window.exoOnEnded?.()) setTimeout(() => { if (state.props['eof-reached']) actions.back() }, 800)
          break
      }
      // The position is the timer's (four times a second): a paint per post would write the bar ten times a second.
      if (msg.name !== 'time-pos') paintClock()
      drawReadings()
      break
    }
    case 'event':
      if (msg.name === 'start-file') { state.loading = true; state.buffer = null; delete state.timings.seek; drawSpinner() }
      if (msg.name === 'seek') state.seekTaken = true
      if (msg.name === 'playback-restart') {
        // Loaded or a seek landed: the bar follows mpv again from here. Seeks sent in a row (a fast scrub, keys held)
        // can land one by one; one that lands away from the latest target is an earlier one, and the bar holds the
        // latest until it lands too, instead of jumping back and forth.
        state.loading = false
        state.restartAt = performance.now()
        const pos = msg.data?.pos
        if (state.seekTarget != null && state.seeksOut > 1 && pos != null && Math.abs(pos - state.seekTarget) > 1) state.seeksOut--
        else settleSeek()
        drawSpinner()
        send({ cmd: 'get', name: 'pause' })
        peekAhead()
      }
      if (msg.name === 'end-file') {
        state.loading = false
        settleSeek()
        drawSpinner()
        if (msg.data?.error && inPlayer()) streamFailed(`The stream couldn't be played: ${msg.data.error}.`)
      }
      break
    case 'buffer':
      state.buffer = { wait: Number(msg.wait) || 0, slow: !!msg.slow }
      drawBuffering()
      break
    case 'value':
      if (msg.name === 'pause') setPause(msg.value === 'yes')
      break
    case 'timing':
      state.timings[msg.timing.what] = msg.timing.ms
      drawReadings()
      break
    case 'fullscreen':
      state.fullscreen = msg.on
      glyph(document.querySelector('[data-action="fullscreen"] svg'), msg.on ? 'arrows-minimize' : 'arrows-maximize')
      break
  }
  listeners.forEach((fn) => fn(msg))
})

// A stream that ends well before its length didn't end: its host refused a new request (seen live: a third connection
// on a host that allows two), and mpv took that as the end of the file. mpv's buffers are dropped (they hold that end,
// so a seek alone meets it again), it seeks to where it stopped, which reads from the host anew, and plays on (it
// paused at the end it took for real). 1, 2 and 4 s on; three tries at one place, then it's the end.
const again = { at: -1, tries: 0 }
function readAgain() {
  const t = currentTime()
  if (!(state.duration - t > 10) || state.props.seekable === false) return false
  if (Math.abs(t - again.at) > 5) { again.at = t; again.tries = 0 }
  if (++again.tries > 3) return false
  setTimeout(() => {
    if (!inPlayer() || !state.props['eof-reached']) return
    send({ cmd: 'command', args: ['drop-buffers'] })
    seekTo(t, 'absolute+exact')
    send({ cmd: 'set', name: 'pause', value: 'no' })
  }, 1000 * 2 ** (again.tries - 1))
  return true
}

function enterPlayer(playMethod) {
  state.playMethod = playMethod
  state.time = 0
  state.duration = 0
  state.cache = 0
  state.seekTarget = null
  state.seeksOut = 0
  state.buffer = null
  state.loading = true
  for (const k of ['eof-reached', 'seeking', 'paused-for-cache', 'core-idle']) state.props[k] = false
  delete state.props.seekable
  document.body.classList.replace('mode-app', 'mode-player')
  resetPeek()
  again.at = -1
  enteredAt = performance.now()
  wake()
  placeGlass()
  paintClock()
}

function leavePlayer() {
  if (state.fullscreen && state.fullscreenByPlayer) send({ cmd: 'fullscreen', on: false })
  state.fullscreenByPlayer = false
  state.loading = false
  state.seekTarget = null
  state.seeksOut = 0
  state.buffer = null
  clearTimeout(spinnerTimer)
  $('spinner').hidden = true
  document.body.classList.replace('mode-player', 'mode-app')
  drawBuffering()
  window.dispatchEvent(new Event('exo:left-player'))
  sendGlass()
  send({ cmd: 'media', off: true })
}

// Volume: the slider sets mpv's volume as it moves; the wheel over it steps by 5; the level is kept for next time.
const volumeInput = $('volume')
let volumeDragging = false
state.volume = (() => { try { return Number(localStorage.getItem('exo:volume') ?? 100) } catch { return 100 } })()

function drawVolume() {
  const shown = state.mute ? 0 : Math.round(state.volume)
  if (!volumeDragging) volumeInput.value = shown
  volumeInput.style.setProperty('--fill', `${shown}%`)
  volumeInput.setAttribute('aria-valuetext', `${shown} percent`)
  glyph(document.querySelector('[data-action="mute"] svg'), state.mute || shown === 0 ? 'volume-off' : 'volume')
}

// mpv sends the track to the receiver as it is (MainWindow.Sound.cs).
function passingThrough() { return String(state.props['audio-out-params/format'] ?? '').startsWith('spdif-') }

function setVolume(v) {
  if (passingThrough()) return
  state.volume = Math.max(0, Math.min(100, Math.round(v)))
  if (state.mute && state.volume > 0) send({ cmd: 'set', name: 'mute', value: 'no' })
  send({ cmd: 'set', name: 'volume', value: String(state.volume) })
  try { localStorage.setItem('exo:volume', String(state.volume)) } catch { }
  drawVolume()
}

volumeInput.addEventListener('input', () => setVolume(Number(volumeInput.value)))
volumeInput.addEventListener('pointerdown', () => { volumeDragging = true })
volumeInput.addEventListener('pointerup', () => { volumeDragging = false; drawVolume() })
volumeInput.closest('.volume').addEventListener('wheel', (e) => {
  if (!e.deltaY) return
  e.preventDefault()
  setVolume(state.volume + (e.deltaY < 0 ? 5 : -5))
  wake()
}, { passive: false })
send({ cmd: 'set', name: 'volume', value: String(state.volume) })
drawVolume()

function streamFailed(message) {
  if (!inPlayer()) return
  actions.back()
  window.dispatchEvent(new CustomEvent('exo:notice', { detail: message }))
}

const actions = {
  pause: () => send({ cmd: 'toggle-pause' }),
  mute: () => { if (!passingThrough()) send({ cmd: 'command', args: ['cycle', 'mute'] }) },
  fullscreen: () => {
    state.fullscreenByPlayer = !state.fullscreen
    send({ cmd: 'fullscreen', on: !state.fullscreen })
  },
  info: () => {
    $('readings').hidden = !$('readings').hidden
    $('trackMenu').hidden = true
    drawReadings()
    placeGlass()
  },
  tracks: () => window.exoTrackMenu?.(),
  back: () => {
    if (!inPlayer()) return
    leavePlayer()
    window.dispatchEvent(new Event('exo:player-closed'))
  },
}

// The player opens on the first click of Play: a second click, or a double click, landed on the picture and paused the
// video before it began. The picture takes no clicks while a video opens, nor for a moment after the player shows.
let enteredAt = 0
const pictureListens = () => !state.loading && performance.now() - enteredAt > 700

stage.addEventListener('click', (e) => {
  const btn = e.target.closest('[data-action]')
  if (btn) { actions[btn.dataset.action](); return }
  // A click on the picture itself plays or pauses; a double click goes full screen.
  if (e.target === stage && pictureListens()) actions.pause()
})
stage.addEventListener('dblclick', (e) => {
  if (e.target === stage && pictureListens()) actions.fullscreen()
})

// Exact by default: a keyframe seek lands up to a few seconds before its target, and the bar jumped back to it.
// A stream whose host serves no byte ranges can only be sought in as far as it has loaded (the lighter part of the
// bar), so a seek past that goes to its end.
let seekCheck = 0
function seekTo(seconds, mode = 'absolute+exact') {
  let to = Math.max(0, Math.min(state.duration || Infinity, seconds))
  if (state.props.seekable === false && state.cache > 0) to = Math.min(to, Math.max(0, state.cache - 1))
  state.seekTarget = to
  state.seeksOut = (state.seeksOut ?? 0) + 1
  state.seekAt = performance.now()
  state.seekTaken = false
  state.time = to
  state.timeAt = performance.now()
  clearTimeout(peek.intent)
  send({ cmd: 'seek', to, mode })
  clearTimeout(seekCheck)
  seekCheck = setTimeout(checkSeekTaken, 1000)
  paintClock()
}

// mpv says "seek" as it takes one (after the hover pictures let go of their connection, a few ms). One it didn't take
// (a stream it can't seek in, a time it can't reach) puts the bar back on the picture, rather than on a time the
// picture never shows.
function checkSeekTaken() {
  if (state.seekTarget == null || state.seekTaken) return
  if (state.props.seeking || state.loading) { seekCheck = setTimeout(checkSeekTaken, 700); return }
  state.seekTarget = null
  state.seeksOut = 0
  if (typeof state.props['time-pos'] === 'number') { state.time = state.props['time-pos']; state.timeAt = performance.now() }
  paintClock()
}

function settleSeek() {
  if (state.seekTarget != null) { state.time = state.seekTarget; state.timeAt = performance.now() }
  state.seekTarget = null
  state.seeksOut = 0
}

// Relative seeks go as absolute ones from where the bar is, so presses in a row add up from what is shown.
function seekBy(seconds) {
  seekTo(currentTime() + seconds)
  wake()
}

// The bar's place is read once as the pointer arrives or presses, not on every move.
const scrub = $('scrub')
let scrubRect = null
const ratioAt = (x) => {
  const r = scrubRect ??= scrub.getBoundingClientRect()
  return Math.max(0, Math.min(1, (x - r.left) / r.width))
}
scrub.addEventListener('pointerenter', (e) => {
  scrubRect = null
  // A picture from somewhere else on the bar doesn't show before the one for here.
  peek.shown = null
  $('hoverPeek').classList.remove('has-picture')
  drawPeek(e.clientX)
})
scrub.addEventListener('pointerleave', () => { if (!state.dragging) peek.hovered = null })

// The bar, the time and the picture move together, in the same frame as the pointer: nothing waits on mpv.
scrub.addEventListener('pointermove', (e) => {
  if (state.dragging) { state.dragTime = ratioAt(e.clientX) * state.duration; paintClock() }
  drawPeek(e.clientX)
})
scrub.addEventListener('pointerdown', (e) => {
  scrubRect = null
  scrub.setPointerCapture(e.pointerId)
  state.dragging = true
  state.dragTime = ratioAt(e.clientX) * state.duration
  scrub.classList.add('is-dragging')
  drawPeek(e.clientX)
  paintClock()
})
// Exact on release: the frame the user let go on, not the keyframe before it. A drag the window loses (it went to the
// back mid-drag) ends where it was.
const endDrag = (time) => {
  if (!state.dragging) return
  state.dragging = false
  scrub.classList.remove('is-dragging')
  seekTo(time, 'absolute+exact')
}
scrub.addEventListener('pointerup', (e) => endDrag(ratioAt(e.clientX) * state.duration))
scrub.addEventListener('lostpointercapture', () => endDrag(state.dragTime))

// ── The picture under the pointer ──────────────────────────────────────────────────────────────────────────────
// The host makes pictures along the whole bar ahead of the pointer with a second, hidden mpv (FramePeek.cs), except
// for an HLS stream, where each picture would start another conversion on its host. Either way the picture
// nearest the pointer shows in the same frame it moves; where none is near, the host makes that one first. Pictures
// are asked for on a grid of steps along the bar and kept for the whole video (at most about 600); each shows the
// keyframe at or before its step, and the one whose keyframe is nearest the pointer's time is the one shown.
// cache: grid step → picture; grid: the steps made, in order; shots: the pictures by the time they really show.
const peek = { cache: new Map(), grid: [], shots: [], shown: null, asked: null, none: false, hovered: null, video: 0, ahead: false }

const peekAspect = () => {
  const vw = state.props['video-params/w'], vh = state.props['video-params/h']
  return vw > 0 && vh > 0 ? Math.min(2.4, Math.max(4 / 3, vw / vh)) : 16 / 9
}

// The picture's size: about a seventh of the window's width, in the video's shape.
function peekSize() {
  const w = Math.round(Math.min(304, Math.max(176, innerWidth * 0.15)))
  return { w, h: Math.round(w / peekAspect()) }
}

// The host draws every picture at the largest size the box shows, in device pixels, so one set fits any window.
function pictureSize() {
  const w = Math.round(304 * dpr())
  return { w, h: Math.round(w / peekAspect()) }
}

// The place in a sorted list of the entry nearest t (-1 when empty), and a sorted insert.
function nearestIndex(list, t, of = (x) => x) {
  if (!list.length) return -1
  let lo = 0, hi = list.length
  while (lo < hi) { const mid = (lo + hi) >> 1; if (of(list[mid]) < t) lo = mid + 1; else hi = mid }
  if (lo === 0) return 0
  if (lo === list.length) return lo - 1
  return t - of(list[lo - 1]) <= of(list[lo]) - t ? lo - 1 : lo
}
function insertSorted(list, item, of = (x) => x) {
  let i = list.length
  while (i > 0 && of(list[i - 1]) > of(item)) i--
  list.splice(i, 0, item)
}
const gridStep = (t) => { const step = peekStep(); return Math.min(Math.floor(t / step) * step, Math.max(0, state.duration - 1)) }

// The picture for where the pointer is: the one nearest its time, and, when no step near it has been made, that step
// asked for first.
function showNearest(t) {
  const i = nearestIndex(peek.shots, t, (s) => s.at)
  if (i >= 0) showPicture(peek.shots[i].at, peek.shots[i].url)
  const at = gridStep(t)
  const g = nearestIndex(peek.grid, at)
  // A step made a step or less away shows as near as a keyframe gets; farther, the host makes this one first.
  if (g < 0 || Math.abs(peek.grid[g] - at) > peekStep()) askPicture(at)
}

// Once the video plays, the host starts on the pictures ahead.
function peekAhead() {
  if (peek.ahead || !(state.duration > 0) || !state.playMethod) return
  peek.ahead = true
  send({ cmd: 'peek-ahead', step: peekStep(), d: state.duration, ...pictureSize(), v: peek.video })
}

// The step pictures are kept at: about 600 along the video, never finer than a second.
const peekStep = () => Math.max(1, Math.round(state.duration / 600))

function resetPeek() {
  peek.video++
  for (const shot of peek.shots) URL.revokeObjectURL(shot.url)
  peek.cache.clear()
  peek.grid = []
  peek.shots = []
  peek.shown = peek.asked = peek.hovered = null
  peek.none = peek.ahead = false
  $('hoverPeek').classList.remove('has-picture')
}

function drawPeek(clientX) {
  const box = $('hoverPeek')
  const ratio = ratioAt(clientX)
  const t = ratio * state.duration
  const text = fmt(t)
  if ($('hoverTime').textContent !== text) $('hoverTime').textContent = text
  const size = peekSize()
  if (state.duration > 0) showPeekAt(t, size)
  // Centred on the pointer, kept inside the window.
  const r = scrubRect ??= scrub.getBoundingClientRect()
  const half = box.classList.contains('has-picture') ? size.w / 2 + 4 : 30
  const x = Math.max(half + 8, Math.min(innerWidth - half - 8, r.left + ratio * r.width)) - r.left
  box.style.translate = `calc(${x}px - 50%) 0`
}

function showPeekAt(t, size) {
  const frame = $('peekFrame')
  frame.style.setProperty('--peek-w', `${size.w}px`)
  frame.style.setProperty('--peek-h', `${size.h}px`)
  // Where a seek is likely: the host fetches what it needs once the pointer slows there.
  clearTimeout(peek.intent)
  peek.intent = setTimeout(() => send({ cmd: 'seek-intent', t }), 60)
  if (peek.none) return
  peek.hovered = t
  showNearest(t)
}

function askPicture(at) {
  if (peek.asked === at) return
  peek.asked = at
  send({ cmd: 'peek', t: at, ...pictureSize(), d: state.duration, v: peek.video })
}

function showPicture(at, url) {
  if (peek.shown === at) return
  peek.shown = at
  const frame = $('peekFrame')
  frame.style.backgroundImage = `url("${url}")`
  frame.style.backgroundSize = 'cover'
  frame.style.backgroundPosition = 'center'
  $('hoverPeek').classList.add('has-picture')
}

onHost((m) => {
  // Only pictures of the video playing now.
  if (m.type !== 'peek' || m.v !== peek.video) return
  if (peek.asked === m.t) peek.asked = null
  if (!m.jpg) { if (m.none) peek.none = true; return }
  if (!peek.cache.has(m.t)) {
    // Steps whose keyframe is the same share its picture.
    const at = m.at >= 0 ? m.at : m.t
    let shot = peek.shots[nearestIndex(peek.shots, at, (s) => s.at)]
    if (shot?.at !== at) {
      const bytes = Uint8Array.from(atob(m.jpg), (c) => c.charCodeAt(0))
      shot = { at, url: URL.createObjectURL(new Blob([bytes], { type: 'image/jpeg' })) }
      insertSorted(peek.shots, shot, (s) => s.at)
    }
    peek.cache.set(m.t, shot.url)
    insertSorted(peek.grid, m.t)
  }
  // Nearer to where the pointer is than the one showing: this one shows.
  if (peek.hovered != null) showNearest(peek.hovered)
})

document.addEventListener('keydown', (e) => {
  if (!inPlayer() || e.defaultPrevented || e.isComposing || e.keyCode === 229) return
  if (e.ctrlKey || e.altKey || e.metaKey) return
  if (e.target?.isContentEditable || e.target?.closest?.('input, textarea, select')) return
  if (e.repeat && !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(e.key)) return
  switch (e.key) {
    case ' ': case 'k': case 'K': actions.pause(); break
    case 'ArrowLeft': seekBy(-5); break
    case 'ArrowRight': seekBy(5); break
    case 'j': case 'J': seekBy(-10); break
    case 'l': case 'L': seekBy(10); break
    case 'ArrowUp': setVolume(state.volume + 5); break
    case 'ArrowDown': setVolume(state.volume - 5); break
    case 'm': case 'M': actions.mute(); break
    case 'f': case 'F': actions.fullscreen(); break
    case 'i': case 'I': actions.info(); break
    case 'Escape': if (state.fullscreen && state.fullscreenByPlayer) actions.fullscreen(); else actions.back(); break
    case 'Backspace': actions.back(); break
    default: return
  }
  e.preventDefault()
  wake()
})

// Idle: after 2.5 s without the pointer moving, while playing, the chrome and the cursor leave.
let idleTimer = 0
function wake() {
  if (stage.classList.contains('is-idle')) { stage.classList.remove('is-idle'); placeGlass(); paintClock() }
  clearTimeout(idleTimer)
  idleTimer = setTimeout(() => {
    if (!state.pause && !state.dragging && inPlayer()) {
      stage.classList.add('is-idle')
      placeGlass()
    }
  }, 2500)
}
document.addEventListener('pointermove', () => { if (inPlayer()) wake() })

send({ cmd: 'ready' })
