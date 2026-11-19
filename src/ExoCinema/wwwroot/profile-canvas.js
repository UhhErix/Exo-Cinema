// The profile's page as the person lays it out, Exo Cinema's copy of Exo Launcher's (components/profile/ProfileCanvas.tsx,
// ProfileLayout.tsx, ProfileText.tsx and lib/exoProfile.ts): the canvas (Exo.Identity's ProfileItems.Canvas, vectors /16),
// 24 columns (or 96, four times finer) across the room and its rows shared out down the room's height, each block where it
// was put, layered, turned, blended, on its surface, in its font and size; the banner, picture and name as blocks or as
// the strip over them; the Layout tab that adds, lists and changes blocks; and the text blocks' small markup, drawn as
// text, never as HTML.

const C_COLUMNS = 24
const C_MAX_BLOCKS = 48
const C_SHOWCASE_ROWS = { stats: 3, 'most-played': 6, recent: 6, setup: 7, trackers: 4, shelf: 8, pc: 6, gear: 6, watching: 6 }
// What each showcase is called, the same in every Exo app (the profile is one thing, whichever app draws it).
const C_SHOWCASE_NAMES = { stats: 'Stats', 'most-played': 'Most played', recent: 'Recently played', setup: 'Setup', trackers: 'Trackers', shelf: 'Game shelf', pc: 'PC parts', gear: 'Gear', watching: 'Watching' }
const C_TRACKERS = { hours: 'Hours played', games: 'Games', played: 'Games played', 'top-game': 'Hours in your top game', 'this-year': 'Played this year', installed: 'Installed', stores: 'Stores', pcs: 'PCs' }
const C_BLENDS = { normal: 'Normal', screen: 'Screen', lighten: 'Lighten', overlay: 'Overlay', multiply: 'Multiply', add: 'Add' }
// Where a block is taken to resize it: an edge or a corner.
const C_EDGES = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw']

const cClamp = (value, low, high) => Math.max(low, Math.min(high, value))

/** The canvas's columns (24, or 96 for placing four times finer), and how many of them make one of 24's. */
const cGrid = (canvas) => canvas.grid ?? C_COLUMNS
const cFine = (canvas) => cGrid(canvas) / C_COLUMNS

/** The canvas on the fine grid (96): every block where it was, in four times as many columns and rows. */
function toFineGrid(canvas) {
  if (canvas.grid === 96) return canvas
  const f = 96 / cGrid(canvas)
  return { ...canvas, grid: 96, blocks: canvas.blocks.map((b) => ({ ...b, x: b.x * f, y: b.y * f, w: b.w * f, h: b.h * f })) }
}

/**
 * The canvas with its banner, picture and name as blocks, placed as the strip drew them (the banner across the top, the
 * picture over its lower edge, the name beside it), everything else moved down under them; as it is when it has them.
 */
function withHeroBlocks(canvas) {
  if (canvas.hero === 'blocks') return canvas
  const f = cFine(canvas)
  const heroes = [
    { id: 'banner', kind: 'banner', x: 0, y: 0, w: 24 * f, h: 5 * f, radius: 22, surface: 'clear' },
    { id: 'avatar', kind: 'avatar', x: 0, y: 3 * f, w: 3 * f, h: 4 * f, z: 2, surface: 'clear' },
    { id: 'name', kind: 'name', x: 3 * f, y: 5 * f, w: 12 * f, h: 2 * f, z: 2, surface: 'clear' },
  ]
  const blocks = heroes.filter((hero) => !canvas.blocks.some((b) => b.id === hero.id))
  return { ...canvas, hero: 'blocks', blocks: [...blocks, ...canvas.blocks.map((b) => ({ ...b, y: Math.min(500 * f - 1, b.y + 7 * f) }))] }
}

/** A canvas made from the look's showcases, one under another across the page. */
function canvasFromShowcases(shown) {
  let y = 0
  return { v: 1, blocks: shown.map((showcase) => {
    const block = { id: showcase.replace(/[^a-z0-9]/g, '').slice(0, 12), kind: 'showcase', showcase, x: 0, y, w: C_COLUMNS, h: C_SHOWCASE_ROWS[showcase] ?? 6 }
    y += block.h + 1
    return block
  }) }
}

/**
 * The layout everyone starts with, the one a profile shows until its own is saved: the banner across the top with the
 * picture over its edge and the name beside it, the three numbers, then what was played lately beside the gear. A look
 * whose showcases were chosen in the older list keeps them, in its order, under the banner.
 */
function defaultCanvas(look) {
  if (look.showcases) return toFineGrid(withHeroBlocks(canvasFromShowcases(look.showcases)))
  return {
    v: 1, grid: 96, hero: 'blocks',
    blocks: [
      { id: 'banner', kind: 'banner', x: 0, y: 0, w: 96, h: 20, radius: 22, surface: 'clear' },
      { id: 'avatar', kind: 'avatar', x: 2, y: 12, w: 12, h: 16, z: 2, surface: 'clear' },
      { id: 'name', kind: 'name', x: 15, y: 21, w: 45, h: 8, z: 2, surface: 'clear' },
      { id: 'trackers', kind: 'showcase', showcase: 'trackers', x: 0, y: 32, w: 96, h: 12, surface: 'clear' },
      { id: 'recent', kind: 'showcase', showcase: 'recent', x: 0, y: 46, w: 44, h: 30, surface: 'glass' },
      { id: 'gear', kind: 'showcase', showcase: 'gear', x: 46, y: 46, w: 50, h: 30, surface: 'glass' },
    ],
  }
}

/** A new block id, 1 to 12 of a-z0-9, not used by the canvas yet. */
function newBlockId(canvas, stem) {
  const base = stem.replace(/[^a-z0-9]/g, '').slice(0, 8) || 'b'
  const used = new Set(canvas.blocks.map((b) => b.id))
  for (let i = 1; ; i++) if (!used.has(base + i)) return base + i
}

const nextRow = (canvas) => canvas.blocks.reduce((bottom, b) => Math.max(bottom, b.y + b.h), 0)

/** The canvas with `action` done to block `id`: a copy just under it, one layer nearer or further, or it gone. */
function blockAction(canvas, id, action) {
  const block = canvas.blocks.find((b) => b.id === id)
  if (!block) return canvas
  if (action === 'remove') return { ...canvas, blocks: canvas.blocks.filter((b) => b.id !== id) }
  if (action === 'copy') {
    return canvas.blocks.length >= C_MAX_BLOCKS ? canvas
      : { ...canvas, blocks: [...canvas.blocks, { ...block, id: newBlockId(canvas, block.id), y: Math.min(500 * cFine(canvas) - 1, block.y + block.h) }] }
  }
  const z = cClamp((block.z ?? 0) + (action === 'forward' ? 1 : -1), -50, 50)
  return { ...canvas, blocks: canvas.blocks.map((b) => (b.id === id ? cClean({ ...b, z: z || undefined }) : b)) }
}

/** What a block is, in words: its name in the Blocks list and for a screen reader. */
function blockName(block) {
  if (block.kind === 'showcase') return C_SHOWCASE_NAMES[block.showcase ?? 'stats'] ?? 'Showcase'
  if (block.kind === 'media') return block.media?.type?.startsWith('video/') ? 'Video' : block.media?.type === 'image/gif' ? 'GIF' : 'Picture'
  if (block.kind === 'text') return (block.text ?? '').split('\n')[0].replace(/^#+\s*/, '').slice(0, 24) || 'Text'
  return { banner: 'Banner', avatar: 'Picture', name: 'Name' }[block.kind] ?? 'Block'
}

/** Every block's name as Layout and the page show it: the same names numbered ("Video 1", "Video 2"), top of the page first. */
function blockNames(blocks) {
  const ordered = [...blocks].sort((a, b) => a.y - b.y || a.x - b.x)
  const counts = new Map()
  for (const b of ordered) counts.set(blockName(b), (counts.get(blockName(b)) ?? 0) + 1)
  const seen = new Map()
  return new Map(ordered.map((b) => {
    const name = blockName(b)
    const nth = (seen.get(name) ?? 0) + 1
    seen.set(name, nth)
    return [b.id, (counts.get(name) ?? 1) > 1 ? `${name} ${nth}` : name]
  }))
}

/** A block's surface: what it was given, else clear for art and glass for the rest. */
const cSurface = (block) => block.surface ?? (block.kind === 'media' || block.kind === 'banner' ? 'clear' : 'glass')

// ---- The canvas

/**
 * The canvas as HTML; `editing`, every block wears its name, and the picked one its edges, corners and toolbar (cBind
 * wires them). `hero` is what the banner, picture and name blocks draw. A showcase with nothing to show here (what only
 * Exo Launcher reads on its own PC: games, the part list it reads, what's plugged in) isn't drawn unless arranged, but
 * keeps its rows, so the page has Exo Launcher's proportions on the same screen.
 */
function cCanvas(canvas, { editing = false, selected = null, files = {}, showcase = () => '', hero = null } = {}) {
  const drawn = canvas.blocks.map((block) => ({ block, inner: cContent(block, files, showcase, hero) }))
    .filter(({ block, inner }) => editing || block.kind !== 'showcase' || inner)
  const rows = Math.max(1, ...canvas.blocks.map((b) => b.y + b.h))
  const names = editing ? blockNames(canvas.blocks) : null
  return `<div class="exo-profile-canvas${editing ? ' is-editing' : ''}" style="--rows:${rows};--cols:${cGrid(canvas)};--major:${cFine(canvas)}"${editing ? ' data-dismiss-free' : ''}>${drawn.map(({ block, inner }) => {
    const surface = cSurface(block)
    const framed = block.frame && block.frame !== 'none'
    const blend = block.blend && block.blend !== 'normal' ? (block.blend === 'add' ? 'plus-lighter' : block.blend) : ''
    const style = `--x:${block.x};--y:${block.y};--w:${block.w};--h:${block.h};--z:${block.z ?? 0};--rot:${block.rot ?? 0}deg;--radius:${block.radius ?? 18}px;opacity:${(block.opacity ?? 100) / 100}${blend ? `;mix-blend-mode:${blend}` : ''}`
    const font = block.font && block.font !== 'exo' ? ` data-font="${esc(block.font)}"` : ''
    const size = block.kind === 'showcase' && block.size && block.size !== 'm' ? ` data-size="${esc(block.size)}"` : ''
    const picked = selected === block.id
    const name = names?.get(block.id) ?? ''
    return `<div class="exo-profile-block is-${esc(block.kind)} is-${surface}${framed ? ` has-frame is-frame-${block.frame}` : ''}${picked ? ' is-selected' : ''}" data-block="${esc(block.id)}"${font}${size} style="${style}"${editing ? ` tabindex="0" role="button" aria-label="${esc(name)}" aria-pressed="${picked}"` : ''}>
      <div class="exo-profile-block-body${surface === 'glass' ? ' exo-glass is-panel' : ''}">${inner}</div>${editing ? cChrome(name, picked) : ''}</div>`
  }).join('')}</div>`
}

/** What a block wears while the page is arranged: its name; picked, its edges and corners to resize it and its toolbar. */
function cChrome(name, picked) {
  const tool = (action, glyph, words, danger = '') => `<button type="button" class="exo-profile-block-tool${danger}" data-c-act="${action}" aria-label="${esc(words)}">${icon(glyph)}</button>`
  return `<span class="exo-profile-block-tag" aria-hidden="true">${esc(name)}</span>${picked ? `${C_EDGES.map((edge) => `<span class="exo-profile-block-handle is-${edge}" data-edge="${edge}" aria-hidden="true"></span>`).join('')}
    <div class="exo-profile-block-tools">${tool('copy', 'copy', `Copy ${name}`)}${tool('forward', 'arrow-up', `Bring ${name} forward`)}${tool('back', 'arrow-down', `Send ${name} back`)}${tool('remove', 'trash', `Remove ${name}`, ' is-danger')}</div>` : ''}`
}

function cContent(block, files, showcase, hero) {
  if (block.kind === 'showcase') return block.showcase ? showcase(block.showcase, block) : ''
  if (block.kind === 'media') {
    const url = block.media && files[block.media.sha]
    return url ? pMedia(url, block.media.type, 'exo-profile-block-media', block.media) : '<span class="exo-profile-block-empty"></span>'
  }
  if (block.kind === 'text') return cText(block.text ?? '', block.align, block.size)
  if (!hero) return ''
  // The banner, picture or name as a block: the look's own, drawn to fill the block.
  if (block.kind === 'avatar') return pAvatar({ frame: hero.look.frame, media: hero.avatar, picture: hero.picture, name: hero.name, cls: 'exo-profile-block-avatar' })
  if (block.kind === 'name') return pName(hero.name || 'Exo profile', hero.look.name, block.font ?? hero.look.font, 'span', 'exo-profile-block-name')
  if (block.kind === 'banner') {
    return hero.banner ? pMedia(hero.banner.url, hero.banner.type, 'exo-profile-block-media', hero.banner.view)
      : hero.bannerStill ? `<img src="${esc(hero.bannerStill)}" alt="" draggable="false" class="exo-profile-block-media">`
      : hero.art ? `<img src="${esc(hero.art)}" alt="" draggable="false" class="exo-profile-block-media">`
      : '<span class="exo-profile-block-empty"></span>'
  }
  return ''
}

/** A place resized from `edge` by whole cells: the far edges stay where they are, and it keeps at least one cell. */
function cResized(start, edge, dx, dy, limits) {
  let { x, y, w, h } = start
  if (edge.includes('e')) w = cClamp(start.w + dx, 1, limits.cols - start.x)
  if (edge.includes('w')) { x = cClamp(start.x + dx, 0, start.x + start.w - 1); w = start.w + start.x - x }
  if (edge.includes('s')) h = cClamp(start.h + dy, 1, limits.tall)
  if (edge.includes('n')) { y = cClamp(start.y + dy, 0, start.y + start.h - 1); h = start.h + start.y - y }
  return { x, y, w, h }
}

/** The blocks of this canvas under a point, topmost first, by id. */
function cBlocksAt(canvas, x, y) {
  const ids = []
  for (const element of document.elementsFromPoint(x, y)) {
    const block = element.closest('.exo-profile-block')
    const id = block?.dataset.block
    if (block && canvas.contains(block) && id && !ids.includes(id)) ids.push(id)
  }
  return ids
}

/**
 * Wires a drawn canvas for arranging: a press picks a block and drags it, its edges and corners resize it, its toolbar
 * copies, layers or removes it, the arrow keys move it a cell (with Shift, resize it), a press on the canvas itself picks
 * none, and a press on the picked block that doesn't move it picks the next one under the pointer (how one hidden under
 * another is reached). While it moves only its place is drawn; `onPlace` gets where it ends.
 */
function cBind(root, { onSelect, onPlace, onAction }) {
  const canvas = root.querySelector('.exo-profile-canvas.is-editing')
  if (!canvas) return
  const cols = +canvas.style.getPropertyValue('--cols') || C_COLUMNS
  const fine = +canvas.style.getPropertyValue('--major') || 1
  const limits = { cols, rows: 500 * fine, tall: 120 * fine }
  const read = (el) => ({ x: +el.style.getPropertyValue('--x'), y: +el.style.getPropertyValue('--y'), w: +el.style.getPropertyValue('--w'), h: +el.style.getPropertyValue('--h') })
  const put = (el, p) => { for (const k of ['x', 'y', 'w', 'h']) el.style.setProperty(`--${k}`, p[k]) }
  const rowsNow = () => Math.max(1, ...[...canvas.querySelectorAll('.exo-profile-block')].map((b) => read(b).y + read(b).h))
  // A column's width and a row's height on screen, as the canvas's style works them out (a row never taller than two
  // columns are wide).
  const cell = () => {
    const box = canvas.getBoundingClientRect()
    const w = box.width / cols || 1
    return { w, h: Math.min(box.height / rowsNow(), w * 2) || w }
  }
  canvas.addEventListener('click', (e) => {
    const tool = e.target.closest('[data-c-act]')
    if (!tool) return
    e.stopPropagation()
    onAction(tool.closest('.exo-profile-block').dataset.block, tool.dataset.cAct)
  })
  canvas.addEventListener('pointerdown', (e) => {
    if (e.target.closest('.exo-profile-block-tools')) { e.stopPropagation(); return }
    const el = e.target.closest('.exo-profile-block')
    if (!el) { onSelect(null); return }
    if (e.button !== 0) return
    e.preventDefault()
    e.stopPropagation()
    const edge = e.target.dataset.edge ?? null
    const id = el.dataset.block
    const under = el.classList.contains('is-selected') ? cBlocksAt(canvas, e.clientX, e.clientY) : []
    canvas.querySelectorAll('.exo-profile-block').forEach((b) => b.classList.toggle('is-selected', b === el))
    const size = cell()
    const from = { x: e.clientX, y: e.clientY }
    const start = read(el)
    let place = start, moved = false
    el.classList.add('is-moving')
    // On the window, so the drag holds when the pointer runs ahead of the block.
    const move = (m) => {
      const dx = Math.round((m.clientX - from.x) / size.w), dy = Math.round((m.clientY - from.y) / size.h)
      if (dx || dy) moved = true
      place = edge ? cResized(start, edge, dx, dy, limits) : { ...start, x: cClamp(start.x + dx, 0, cols - start.w), y: cClamp(start.y + dy, 0, limits.rows - 1) }
      put(el, place)
      canvas.style.setProperty('--rows', rowsNow())
    }
    const up = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      window.removeEventListener('pointercancel', up)
      el.classList.remove('is-moving')
      if (place !== start) onPlace(id, place)
      else if (!moved && !edge && under.length > 1) onSelect(under[(under.indexOf(id) + 1) % under.length])
      else onSelect(id)
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
    window.addEventListener('pointercancel', up)
  })
  canvas.addEventListener('keydown', (e) => {
    const el = e.target.closest('.exo-profile-block')
    const step = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key]
    if (!el || !step) return
    e.preventDefault()
    const b = read(el)
    const [dx, dy] = step
    onPlace(el.dataset.block, e.shiftKey
      ? { x: b.x, y: b.y, w: cClamp(b.w + dx, 1, cols - b.x), h: cClamp(b.h + dy, 1, limits.tall) }
      : { x: cClamp(b.x + dx, 0, cols - b.w), y: cClamp(b.y + dy, 0, limits.rows - 1), w: b.w, h: b.h })
  })
}

// ---- Text: Exo's small markup, drawn as text

/**
 * "# " and "## " start a heading, "- " a list item, "> " a quote; a line of three backticks opens and closes a block drawn
 * exactly as typed in a fixed-width face (ASCII and Unicode art). In a line: **bold**, *italic*, `code`, ||spoiler||
 * (pressed to show) and [words](https://link), which opens in the browser.
 */
function cText(text, align = 'left', size = 'm') {
  const parts = []
  const lines = text.split('\n')
  let list = []
  const endList = () => { if (list.length) parts.push(`<ul>${list.join('')}</ul>`); list = [] }
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]
    if (line.trim() === '```') {
      endList()
      const art = []
      for (i++; i < lines.length && lines[i].trim() !== '```'; i++) art.push(lines[i])
      parts.push(`<pre class="exo-profile-text-art">${esc(art.join('\n'))}</pre>`)
      continue
    }
    if (line.startsWith('- ')) { list.push(`<li>${cInline(line.slice(2))}</li>`); continue }
    endList()
    if (line.startsWith('## ')) parts.push(`<h4>${cInline(line.slice(3))}</h4>`)
    else if (line.startsWith('# ')) parts.push(`<h3>${cInline(line.slice(2))}</h3>`)
    else if (line.startsWith('> ')) parts.push(`<blockquote>${cInline(line.slice(2))}</blockquote>`)
    else if (line.trim() === '') parts.push('<div class="exo-profile-text-gap"></div>')
    else parts.push(`<p>${cInline(line)}</p>`)
  }
  endList()
  return `<div class="exo-profile-text is-${align} is-${size}">${parts.join('')}</div>`
}

const C_INLINE = /(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`|\|\|[^|]+\|\||\[[^\]]+\]\(https:\/\/[^)\s]+\))/g

function cInline(line) {
  return line.split(C_INLINE).filter(Boolean).map((part) => {
    if (part.startsWith('**') && part.endsWith('**') && part.length > 4) return `<strong>${esc(part.slice(2, -2))}</strong>`
    if (part.startsWith('||') && part.endsWith('||') && part.length > 4) return `<button type="button" class="exo-profile-text-spoiler" aria-pressed="false">${esc(part.slice(2, -2))}</button>`
    if (part.startsWith('`') && part.endsWith('`') && part.length > 2) return `<code>${esc(part.slice(1, -1))}</code>`
    if (part.startsWith('*') && part.endsWith('*') && part.length > 2) return `<em>${esc(part.slice(1, -1))}</em>`
    const link = /^\[([^\]]+)\]\((https:\/\/[^)\s]+)\)$/.exec(part)
    if (link) return `<button type="button" class="exo-profile-text-link" data-link="${esc(link[2])}">${esc(link[1])}</button>`
    return esc(part)
  }).join('')
}

// A spoiler shows when pressed; a link opens in the person's browser (https only, checked again by the host).
document.addEventListener('click', (e) => {
  const spoiler = e.target.closest('.exo-profile-text-spoiler')
  if (spoiler) { spoiler.classList.add('is-shown'); spoiler.setAttribute('aria-pressed', 'true'); return }
  const link = e.target.closest('.exo-profile-text-link[data-link]')
  if (link && !link.closest('.is-editing')) app('account-open-link', { url: link.dataset.link }).catch(() => {})
})

// ---- Layout: what the page holds, and how each block looks

// Words for each choice, as Exo Launcher's Layout names them.
const C_SURFACE_NAMES = { glass: 'Glass', clear: 'None', window: 'Window', tint: 'Your color' }
const C_FRAME_NAMES = { none: 'None', glow: 'Glow', orbit: 'Orbit', holo: 'Holo', fire: 'Fire' }
const C_FONT_NAMES = { exo: 'Exo', orbitron: 'Orbitron', bebas: 'Bebas', pixel: 'Pixel', marker: 'Marker', script: 'Script', tube: 'Tube' }
const C_ALIGN_NAMES = { left: 'Left', center: 'Center', right: 'Right' }
const C_SIZE_NAMES = { s: 'Small', m: 'Medium', l: 'Large', xl: 'Huge' }
const C_HERO_ADD = { banner: 'Banner', avatar: 'Your picture', name: 'Your name' }
const C_ADD_ICONS = { media: 'gallery', text: 'text', banner: 'cover', avatar: 'user-circle', name: 'user', stats: 'poll', 'most-played': 'trophy', recent: 'clock', setup: 'devices', trackers: 'award', shelf: 'bookmark', pc: 'cpu', gear: 'mouse', watching: 'movie' }
const C_PARTS = ['cpu', 'gpu', 'memory', 'motherboard', 'storage', 'case', 'psu', 'cooler', 'fans']
const C_PART_NAMES = { cpu: 'CPU', gpu: 'Video card', memory: 'Memory', motherboard: 'Motherboard', storage: 'Storage', case: 'Case', psu: 'Power supply', cooler: 'Cooler', fans: 'Fans' }
const C_GEAR_KINDS = { mouse: 'Mouse', keyboard: 'Keyboard', audio: 'Headphones', controller: 'Controller', mousepad: 'Mousepad', microphone: 'Microphone' }
// Every dropdown's choices by its key, for the handler that opens it (profile.js).
const C_CHOICES = { size: C_SIZE_NAMES, font: C_FONT_NAMES, surface: C_SURFACE_NAMES, frame: C_FRAME_NAMES, blend: C_BLENDS, align: C_ALIGN_NAMES, 'text-size': C_SIZE_NAMES, tracker: C_TRACKERS, 'tracker-more': { none: 'None', ...C_TRACKERS }, 'add-part': C_PART_NAMES }

// A row as Settings has them: its name (and a line under it), its control at the end.
const cRow = (label, control = '', detail = '') => `<div class="set-row exo-profile-row"><span class="set-name">${esc(label)}${detail ? `<span class="exo-profile-row-detail">${esc(detail)}</span>` : ''}</span>${control ? `<span class="set-actions">${control}</span>` : ''}</div>`

// A dropdown: the choice's name, opening the list from the control pressed (profile.js).
const cChoice = (label, key, value, busy, extra = '') => `<button type="button" class="set-btn is-compact exo-profile-choice" data-c="choice" data-key="${key}"${extra} aria-label="${esc(label)}" aria-haspopup="listbox"${busy}>${esc(C_CHOICES[key][value] ?? value)}${icon('chevron-down')}</button>`

const cRangeRow = (label, value, min, max, data, busy, step = 1, format = (v) => v) => cRow(label, `<span class="exo-profile-range">
  <input type="range" class="exo-range" min="${min}" max="${max}" step="${step}" value="${value}" aria-label="${esc(label)}" data-c="${data}"${busy}>
  <span class="exo-profile-range-value">${format(value)}</span></span>`)

const cSeconds = (ms) => `${(ms / 1000).toFixed(1)} s`

/**
 * Layout, as Exo Launcher's: everything on the page is a block, the banner, picture and name too, moved by dragging it and
 * resized by its edges and corners on the page itself. With nothing picked, what can be added (as pictures, not words)
 * and what is on the page; a block picked (on the page or in the list) shows what it holds, then its style, with the
 * rarer settings under More. Every change shows on the page at once and keeps with Save.
 */
function cLayout(canvas, selected, { busy = '', picking = false, error = null, durations = {}, more = false } = {}) {
  const full = canvas.blocks.length >= C_MAX_BLOCKS
  const names = blockNames(canvas.blocks)
  const block = canvas.blocks.find((b) => b.id === selected)
  const err = error ? `<p class="form-error" role="alert">${esc(error)}</p>` : ''
  if (block) return cBlockPanel(block, names.get(block.id) ?? blockName(block), { busy, full, picking, err, durations, more })
  const heroes = Object.keys(C_HERO_ADD).filter((kind) => !canvas.blocks.some((b) => b.kind === kind))
  const tiles = [['media', 'Picture or video'], ['text', 'Text'], ...heroes.map((kind) => [kind, C_HERO_ADD[kind]]), ...Object.entries(C_SHOWCASE_NAMES)]
  return `<section class="exo-profile-sheet-part"><h3>Add to your page</h3>${err}<div class="exo-profile-add-grid">${tiles.map(([id, label]) =>
      `<button type="button" class="exo-profile-add-tile" data-c="add" data-value="${id}"${busy || full || picking ? ' disabled' : ''}>${picking && id === 'media' ? '<span class="spinner"></span>' : icon(C_ADD_ICONS[id])}<span>${esc(label)}</span></button>`).join('')}</div></section>
    <section class="exo-profile-sheet-part"><h3>On your page</h3><div class="exo-set-list exo-profile-block-list">${[...canvas.blocks].sort((a, b) => a.y - b.y || a.x - b.x).map((b) =>
      `<button type="button" class="exo-profile-block-row" data-c="select" data-id="${esc(b.id)}">${esc(names.get(b.id))}${icon('chevron-right')}</button>`).join('')}</div></section>`
}

/** A picked block: back to the list, what it holds, its style (the rarer settings under More), then copy and remove. */
function cBlockPanel(block, name, { busy, full, picking, err, durations, more }) {
  const worded = block.kind === 'text' || block.kind === 'showcase' || block.kind === 'name'
  return `<section class="exo-profile-sheet-part">
    <div class="exo-profile-actions is-start"><button type="button" class="set-btn is-compact" data-c="list">${icon('chevron-left')}All blocks</button></div>
    <h3>${esc(name)}</h3>${err}${cHeld(block, busy, picking, durations)}
    <h4 class="exo-profile-sheet-subhead">Style</h4>
    <div class="exo-set-list">
      ${block.kind === 'showcase' ? cRow('Size', cChoice('Size', 'size', block.size ?? 'm', busy)) : ''}
      ${worded ? cRow('Font', cChoice('Font', 'font', block.font ?? 'exo', busy)) : ''}
      ${cRow('Background', cChoice('Background', 'surface', cSurface(block), busy))}
      ${cRow('Frame', cChoice('Frame', 'frame', block.frame ?? 'none', busy))}
      ${cRangeRow('Corners', block.radius ?? 18, 0, 64, 'radius', busy)}
    </div>
    <button type="button" class="exo-profile-more" data-c="more" aria-expanded="${more}">More${icon('chevron-down')}</button>
    ${more ? `<div class="exo-set-list">
      ${cRangeRow('Opacity', block.opacity ?? 100, 10, 100, 'opacity', busy)}
      ${cRangeRow('Turn', block.rot ?? 0, -180, 180, 'rot', busy)}
      ${cRow('Blend', cChoice('Blend', 'blend', block.blend ?? 'normal', busy))}
      ${cRow('Layer', `<button type="button" class="set-btn is-compact" data-c="act" data-value="back"${busy}>Back</button><button type="button" class="set-btn is-compact" data-c="act" data-value="forward"${busy}>Forward</button>`)}
    </div>` : ''}
    <div class="exo-profile-actions is-start">
      <button type="button" class="set-btn" data-c="act" data-value="copy"${busy || full ? ' disabled' : ''}>${icon('copy')}Copy</button>
      <button type="button" class="set-btn is-danger" data-c="act" data-value="remove"${busy}>${icon('trash')}Remove</button>
    </div></section>`
}

/** What a block holds: its text, its media's view, its numbers, its parts and gear. */
function cHeld(block, busy, picking, durations) {
  if (block.kind === 'text') {
    return `<textarea class="exo-field exo-profile-text-field" id="cText" maxlength="2000" rows="5" spellcheck="true" aria-label="Text"${busy}>${esc(block.text ?? '')}</textarea>
      <div class="exo-set-list">${cRow('Align', cChoice('Align', 'align', block.align ?? 'left', busy))}${cRow('Size', cChoice('Size', 'text-size', block.size ?? 'm', busy))}</div>`
  }
  if (block.kind === 'media' && block.media) {
    const media = block.media, video = pIsVideo(media.type)
    const focus = media.focus ?? [50, 50]
    const length = Math.min(3_600_000, Math.round(durations[media.sha] ?? 0))
    const trim = media.trim ?? [0, length]
    return `<div class="exo-set-list">${cRow(video ? 'Video' : media.type === 'image/gif' ? 'GIF' : 'Picture', `<button type="button" class="set-btn is-compact" data-c="replace"${busy || picking ? ' disabled' : ''}>${picking ? '<span class="spinner"></span>' : ''}Replace</button>`)}
      ${cRangeRow('Across', focus[0], 0, 100, 'across', busy)}${cRangeRow('Down', focus[1], 0, 100, 'down', busy)}${cRangeRow('Zoom', media.zoom ?? 100, 100, 400, 'zoom', busy)}
      ${video && length > 0 ? cRangeRow('Loop from', trim[0], 0, Math.max(0, trim[1] - 100), 'trim-from', busy, 100, cSeconds) + cRangeRow('Loop to', trim[1], Math.min(length, trim[0] + 100), length, 'trim-to', busy, 100, cSeconds) : ''}</div>`
  }
  if (block.kind !== 'showcase') return ''
  if (block.showcase === 'trackers') {
    const chosen = block.trackers ?? ['hours', 'top-game', 'this-year']
    return `<div class="exo-set-list">${['First number', 'Second number', 'Third number'].map((label, at) => (at > chosen.length ? ''
      : cRow(label, cChoice(label, at === 0 ? 'tracker' : 'tracker-more', chosen[at] ?? 'none', busy, ` data-at="${at}"`)))).join('')}</div>`
  }
  return (block.showcase === 'pc' || block.showcase === 'setup' ? cBuild(block, busy) : '') + (block.showcase === 'gear' || block.showcase === 'setup' ? cGear(block, busy) : '')
}

/**
 * The PC's parts as the person names them: what Exo can't read (the case, the power supply, the cooler, the fans) or a
 * name they would rather show for one it does. Each part once, in the part list's order.
 */
function cBuild(block, busy) {
  const build = block.build ?? []
  const missing = C_PARTS.filter((part) => !build.some((b) => b.part === part))
  return `<section class="exo-profile-sheet-sub"><header class="exo-profile-sheet-part-head"><h4>Parts</h4>
      <button type="button" class="set-btn is-compact" data-c="choice" data-key="add-part" aria-haspopup="listbox"${busy || !missing.length ? ' disabled' : ''}>${icon('plus')}Add</button></header>
    <div class="exo-set-list">${build.map((entry) => cRow(C_PART_NAMES[entry.part], `<span class="exo-profile-part-edit">
      <input class="exo-field" data-c="part" data-part="${entry.part}" value="${esc(entry.name)}" maxlength="64" placeholder="${C_PART_NAMES[entry.part]}" aria-label="${C_PART_NAMES[entry.part]}"${busy}>
      <button type="button" class="set-btn is-compact" data-c="part-remove" data-part="${entry.part}"${busy}>Remove</button></span>`)).join('')}</div></section>`
}

/** The gear a block shows, each with Remove. New gear comes from Exo's own list of devices, in Exo Launcher. */
function cGear(block, busy) {
  const gear = block.gear ?? []
  if (!gear.length) return ''
  return `<section class="exo-profile-sheet-sub"><header class="exo-profile-sheet-part-head"><h4>Gear</h4></header>
    <div class="exo-set-list">${gear.map((pick, i) => cRow(pGearName(pick), `<button type="button" class="set-btn is-compact" data-c="gear-remove" data-at="${i}"${busy}>Remove</button>`, C_GEAR_KINDS[pick.kind] ?? '')).join('')}</div></section>`
}

/** A block without its unset fields, so the canvas the host keeps carries only what was chosen. */
function cClean(block) {
  const out = { ...block }
  for (const key of Object.keys(out)) if (out[key] === undefined) delete out[key]
  return out
}

function cCleanMedia(media) {
  const out = { bytes: media.bytes, sha: media.sha, type: media.type }
  if (media.focus && (media.focus[0] !== 50 || media.focus[1] !== 50)) out.focus = media.focus
  if (media.zoom && media.zoom !== 100) out.zoom = media.zoom
  if (media.trim && pIsVideo(media.type)) out.trim = media.trim
  return out
}

/** The block under the pointer leans toward it and catches the light where it points; the one it left settles back. */
function cTilt(root) {
  const canvas = root.querySelector('.exo-profile-canvas:not(.is-editing)')
  if (!canvas) return
  const settle = (block) => {
    block.classList.remove('is-tilted')
    block.style.setProperty('--tx', '0')
    block.style.setProperty('--ty', '0')
  }
  canvas.addEventListener('pointermove', (e) => {
    if (document.documentElement.dataset.exoMotion === 'reduce') return
    const block = e.target.closest('.exo-profile-block')
    const last = canvas.querySelector('.exo-profile-block.is-tilted')
    if (last && last !== block) settle(last)
    if (!block) return
    const box = block.getBoundingClientRect()
    const x = (e.clientX - box.left) / box.width, y = (e.clientY - box.top) / box.height
    block.classList.add('is-tilted')
    block.style.setProperty('--tx', (x * 2 - 1).toFixed(3))
    block.style.setProperty('--ty', (y * 2 - 1).toFixed(3))
    block.style.setProperty('--gx', `${(x * 100).toFixed(1)}%`)
    block.style.setProperty('--gy', `${(y * 100).toFixed(1)}%`)
  })
  canvas.addEventListener('pointerleave', () => {
    const last = canvas.querySelector('.exo-profile-block.is-tilted')
    if (last) settle(last)
  })
}
