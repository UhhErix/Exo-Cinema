// The profile's effect, Exo Cinema's copy of Exo Launcher's (ui/src/components/profile/ProfileEffect.tsx), so the two
// rooms draw the same: particles over the room in the look's accent, under the words and never in the way of a press.
// It runs only while its canvas is on the page, the window isn't hidden and no game holds it (window.js › exoHeld), at
// 30 frames a second and at most 1.5 pixels a point, as Launcher's; under Reduce motion it draws one still frame.

// How many, by effect, for a 1920-wide room (fewer in a smaller one).
const EFFECT_COUNT = { embers: 70, snow: 120, stars: 160, rain: 180, sparkles: 50, aurora: 5, matrix: 90, petals: 45, fireflies: 40, lightning: 3 }

// Matrix's glyphs: half-width katakana and digits, as the films' rain.
const MATRIX_GLYPHS = 'ｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789'
const MATRIX_TRAIL = 14
const MATRIX_SIZE = 16

const reduceMotion = () => document.documentElement.dataset.exoMotion === 'reduce' || matchMedia('(prefers-reduced-motion: reduce)').matches

/** Starts `effect` on `canvas` in `accent`; answers a stop. Nothing for 'none'. */
function startEffect(canvas, effect, accent) {
  if (!canvas || !effect || effect === 'none' || !(effect in EFFECT_COUNT)) return () => {}
  const context = canvas.getContext('2d')
  const still = reduceMotion()
  let width = 0, height = 0, frame = 0, last = performance.now(), stopped = false
  let particles = []
  const rgb = hexToRgb(accent)
  const resize = () => {
    const box = canvas.getBoundingClientRect()
    const scale = Math.min(window.devicePixelRatio || 1, 1.5)
    width = box.width
    height = box.height
    canvas.width = Math.max(1, Math.round(width * scale))
    canvas.height = Math.max(1, Math.round(height * scale))
    context.setTransform(scale, 0, 0, scale, 0, 0)
    const fewest = effect === 'aurora' || effect === 'lightning' ? EFFECT_COUNT[effect] : 8
    const count = Math.max(fewest, Math.round(EFFECT_COUNT[effect] * Math.min(1, (width * height) / (1920 * 1080))))
    particles = Array.from({ length: count }, () => spawn(effect, width, height, true))
  }
  const running = () => !still && !stopped && !document.hidden && !exoHeld()
  const draw = (now) => {
    if (stopped || !canvas.isConnected) return
    // A frame in two at 60 Hz, fewer at higher rates: 30 a second.
    if (now - last < 32 && particles.length) {
      if (running()) frame = requestAnimationFrame(draw)
      return
    }
    const dt = Math.min(0.05, (now - last) / 1000)
    last = now
    context.clearRect(0, 0, width, height)
    for (let i = 0; i < particles.length; i++) {
      const p = particles[i]
      if (!still) step(effect, p, dt, width, height)
      const gone = effect === 'lightning' ? p.life <= 0 && p.size === 1
        : effect === 'matrix' ? p.y - MATRIX_TRAIL * MATRIX_SIZE > height
        : p.life <= 0 || p.y > height + 40 || p.y < -60 || p.x < -60 || p.x > width + 60
      if (gone) particles[i] = spawn(effect, width, height, false)
      paint(effect, context, particles[i], rgb, now / 1000, width, height)
    }
    if (running()) frame = requestAnimationFrame(draw)
  }
  // Hidden or held, the window draws nothing; back, it goes on from where it was.
  const visible = () => { if (running()) { last = performance.now() - 32; cancelAnimationFrame(frame); frame = requestAnimationFrame(draw) } }
  resize()
  const observer = new ResizeObserver(resize)
  observer.observe(canvas)
  document.addEventListener('visibilitychange', visible)
  window.addEventListener('exo:held', visible)
  last -= 32
  frame = requestAnimationFrame(draw)
  return () => {
    stopped = true
    cancelAnimationFrame(frame)
    observer.disconnect()
    document.removeEventListener('visibilitychange', visible)
    window.removeEventListener('exo:held', visible)
  }
}

function spawn(effect, width, height, anywhere) {
  const r = Math.random
  switch (effect) {
    case 'embers':
      return { x: r() * width, y: anywhere ? r() * height : height + 10, vx: (r() - 0.5) * 20, vy: -(30 + r() * 60), size: 1.2 + r() * 2.6, life: 4 + r() * 6, phase: r() * 6.28 }
    case 'snow':
      return { x: r() * width, y: anywhere ? r() * height : -10, vx: (r() - 0.5) * 12, vy: 20 + r() * 40, size: 1 + r() * 2.8, life: 99, phase: r() * 6.28 }
    case 'stars':
      return { x: r() * width, y: r() * height, vx: 0, vy: 0, size: 0.5 + r() * 1.6, life: 99, phase: r() * 6.28 }
    case 'rain':
      return { x: r() * (width + 200) - 100, y: anywhere ? r() * height : -40, vx: -60, vy: 600 + r() * 400, size: 10 + r() * 16, life: 99, phase: 0 }
    case 'sparkles':
      return { x: r() * width, y: r() * height, vx: 0, vy: -6, size: 3 + r() * 6, life: 0.6 + r() * 1.6, phase: r() * 6.28 }
    case 'matrix':
      // A column's head, on the glyph grid: falls, and its trail fades above it.
      return { x: Math.floor(r() * (width / MATRIX_SIZE)) * MATRIX_SIZE, y: anywhere ? r() * height : -r() * height * 0.5, vx: 0, vy: 60 + r() * 140, size: MATRIX_SIZE, life: 99, phase: r() * 1000 }
    case 'petals':
      return { x: r() * width, y: anywhere ? r() * height : -20, vx: 10 + r() * 25, vy: 18 + r() * 30, size: 4 + r() * 5, life: 99, phase: r() * 6.28 }
    case 'fireflies':
      return { x: r() * width, y: height * (0.3 + r() * 0.7), vx: (r() - 0.5) * 16, vy: (r() - 0.5) * 12, size: 1.5 + r() * 2, life: 6 + r() * 10, phase: r() * 6.28 }
    case 'lightning':
      // Waits a few seconds (life), then strikes for a fraction of one: never more than one flash a second.
      return { x: width * (0.1 + r() * 0.8), y: 0, vx: 0, vy: 0, size: 0, life: anywhere ? 2 + r() * 8 : 4 + r() * 8, phase: r() * 1000 }
    default:
      return { x: r() * width, y: height * (0.08 + r() * 0.3), vx: 8 + r() * 14, vy: 0, size: width * (0.18 + r() * 0.2), life: 99, phase: r() * 6.28 }
  }
}

function step(effect, p, dt, width) {
  p.phase += dt
  p.life -= dt
  if (effect === 'snow') p.x += Math.sin(p.phase) * 10 * dt
  if (effect === 'embers') p.vx += (Math.random() - 0.5) * 30 * dt
  if (effect === 'aurora' && p.x > width + p.size) p.x = -p.size
  if (effect === 'petals') p.x += Math.sin(p.phase * 1.3) * 22 * dt
  if (effect === 'fireflies') { p.vx += (Math.random() - 0.5) * 20 * dt; p.vy += (Math.random() - 0.5) * 20 * dt; p.vx = Math.max(-14, Math.min(14, p.vx)); p.vy = Math.max(-10, Math.min(10, p.vy)) }
  if (effect === 'lightning' && p.life <= 0 && p.size === 0) { p.size = 1; p.life = 0.22 }
  p.x += p.vx * dt
  p.y += p.vy * dt
}

function paint(effect, c, p, [r, g, b], t, w, h) {
  switch (effect) {
    case 'embers': {
      const a = Math.max(0, Math.min(1, p.life / 3)) * (0.6 + 0.4 * Math.sin(t * 8 + p.phase))
      c.fillStyle = `rgba(${Math.min(255, r + 60)},${g},${Math.max(0, b - 40)},${a})`
      c.beginPath(); c.arc(p.x, p.y, p.size, 0, 6.283); c.fill()
      break
    }
    case 'snow':
      c.fillStyle = `rgba(255,255,255,${0.35 + p.size / 8})`
      c.beginPath(); c.arc(p.x, p.y, p.size, 0, 6.283); c.fill()
      break
    case 'stars': {
      const a = 0.25 + 0.75 * Math.abs(Math.sin(t * 1.4 + p.phase))
      c.fillStyle = `rgba(${Math.round((r + 255) / 2)},${Math.round((g + 255) / 2)},${Math.round((b + 255) / 2)},${a})`
      c.beginPath(); c.arc(p.x, p.y, p.size, 0, 6.283); c.fill()
      break
    }
    case 'rain':
      c.strokeStyle = `rgba(${r},${g},${b},0.35)`
      c.lineWidth = 1
      c.beginPath(); c.moveTo(p.x, p.y); c.lineTo(p.x + p.vx * 0.02, p.y + p.size); c.stroke()
      break
    case 'sparkles': {
      const a = Math.max(0, Math.sin(Math.min(1, p.life) * Math.PI))
      const s = p.size * a
      c.strokeStyle = `rgba(${r},${g},${b},${a})`
      c.lineWidth = 1.4
      c.beginPath(); c.moveTo(p.x - s, p.y); c.lineTo(p.x + s, p.y); c.moveTo(p.x, p.y - s); c.lineTo(p.x, p.y + s); c.stroke()
      break
    }
    case 'matrix': {
      c.font = `${MATRIX_SIZE}px ui-monospace, monospace`
      for (let k = 0; k < MATRIX_TRAIL; k++) {
        const y = p.y - k * MATRIX_SIZE
        if (y < -MATRIX_SIZE || y > h + MATRIX_SIZE) continue
        const glyph = MATRIX_GLYPHS[Math.floor(p.phase + k * 7 + t * (k === 0 ? 12 : 2)) % MATRIX_GLYPHS.length]
        c.fillStyle = k === 0 ? `rgba(${Math.round((r + 255) / 2)},${Math.round((g + 255) / 2)},${Math.round((b + 255) / 2)},0.95)` : `rgba(${r},${g},${b},${(1 - k / MATRIX_TRAIL) * 0.7})`
        c.fillText(glyph, p.x, y)
      }
      break
    }
    case 'petals': {
      c.save()
      c.translate(p.x, p.y)
      c.rotate(p.phase * 1.7)
      c.scale(1, 0.55 + 0.45 * Math.abs(Math.sin(p.phase * 2)))
      c.fillStyle = `rgba(${Math.round((r + 255 * 2) / 3)},${Math.round((g + 190 * 2) / 3)},${Math.round((b + 210 * 2) / 3)},0.85)`
      c.beginPath(); c.ellipse(0, 0, p.size, p.size * 0.6, 0, 0, 6.283); c.fill()
      c.restore()
      break
    }
    case 'fireflies': {
      const a = Math.max(0, Math.sin(t * 1.6 + p.phase)) * Math.min(1, p.life / 2)
      const glow = c.createRadialGradient(p.x, p.y, 0, p.x, p.y, p.size * 6)
      glow.addColorStop(0, `rgba(${r},${g},${b},${0.9 * a})`)
      glow.addColorStop(0.25, `rgba(${r},${g},${b},${0.35 * a})`)
      glow.addColorStop(1, `rgba(${r},${g},${b},0)`)
      c.fillStyle = glow
      c.fillRect(p.x - p.size * 6, p.y - p.size * 6, p.size * 12, p.size * 12)
      break
    }
    case 'lightning': {
      if (p.size !== 1) break
      // A jagged bolt from the top, seeded by the strike so it holds its shape, with a faint wash over the page.
      const a = Math.max(0, p.life / 0.22)
      c.fillStyle = `rgba(${r},${g},${b},${0.05 * a})`
      c.fillRect(0, 0, w, h)
      let seed = p.phase
      const next = () => { seed = (seed * 9301 + 49297) % 233280; return seed / 233280 }
      let x = p.x, y = 0
      c.strokeStyle = `rgba(${Math.round((r + 255) / 2)},${Math.round((g + 255) / 2)},${Math.round((b + 255) / 2)},${0.9 * a})`
      c.shadowColor = `rgba(${r},${g},${b},${a})`
      c.shadowBlur = 18
      c.lineWidth = 2
      c.beginPath(); c.moveTo(x, y)
      while (y < h * 0.75) { x += (next() - 0.5) * 60; y += 20 + next() * 40; c.lineTo(x, y) }
      c.stroke()
      c.shadowBlur = 0
      break
    }
    default: {
      // Aurora: slow soft bands drifting across the top of the page.
      const y = p.y + Math.sin(t * 0.3 + p.phase) * h * 0.05
      const glow = c.createRadialGradient(p.x, y, 0, p.x, y, p.size)
      glow.addColorStop(0, `rgba(${r},${g},${b},0.13)`)
      glow.addColorStop(1, `rgba(${r},${g},${b},0)`)
      c.fillStyle = glow
      c.fillRect(p.x - p.size, y - p.size, p.size * 2, p.size * 2)
    }
  }
}

function hexToRgb(hex) {
  const m = /^#([0-9a-f]{6})$/i.exec(hex ?? '')
  if (!m) return [245, 245, 247]
  const n = parseInt(m[1], 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}
