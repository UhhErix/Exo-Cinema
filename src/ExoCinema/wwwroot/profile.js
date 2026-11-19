// ---- Profile: Exo Cinema's copy of Exo Launcher's Profile room (ui/src/components/profile/*; DESIGN.md › Rooms ›
// Profile), the person's own page, one for Exo Launcher, Exo Cinema and Exo Browser and drawn by the same classes
// (profile.css). Signed in, it is their stage: the look's background (else the backdrop of what they watch most), the
// effect over it in their accent (profile-effect.js), the banner, the picture in its frame, the name in its style and
// font, then the page as they laid it out (the canvas: profile-canvas.js) or their showcases, scaled to fit one screen.
// Customize changes it live at the room's side (Look and Layout), saved with Save;
// Account holds the Exo account, the recovery code and the PCs. Signed out, the starter looks take turns over Cinema's
// art around the sign-in, with set up, link and the recovery code under it. Its steps are Sync/ExoAccount.cs's, on
// Exo.Identity's ProfileFlows; what a look may hold is Exo.Identity's ProfileItems.

const P_ACCENT = '#f5f5f7'
const P_ACCENTS = ['#f5f5f7', '#ff6a2b', '#ffd36b', '#3ee6b0', '#4fd1ff', '#6fa8ff', '#ff5fd2', '#ff4d6d', '#9be15d']
const P_STARTERS = [
  { name: 'Ember', accent: '#ff6a2b', effect: 'embers', frame: 'fire', style: 'glow' },
  { name: 'Aurora', accent: '#3ee6b0', effect: 'aurora', frame: 'orbit', style: 'shimmer' },
  { name: 'Frost', accent: '#9fd8ff', effect: 'snow', frame: 'glow', style: 'plain' },
  { name: 'Holo', accent: '#ff5fd2', effect: 'sparkles', frame: 'holo', style: 'wave' },
  { name: 'Night', accent: '#ffd36b', effect: 'stars', frame: 'orbit', style: 'glow' },
  { name: 'Storm', accent: '#6fa8ff', effect: 'rain', frame: 'glow', style: 'shimmer' },
]
// The look's lists, in Exo.Identity's order (ProfileItems), with what Customize calls each.
const P_EFFECTS = { none: 'None', embers: 'Embers', snow: 'Snow', stars: 'Stars', rain: 'Rain', sparkles: 'Sparkles', aurora: 'Aurora', matrix: 'Matrix', petals: 'Petals', fireflies: 'Fireflies', lightning: 'Lightning' }
const P_FRAMES = { none: 'None', glow: 'Glow', orbit: 'Orbit', holo: 'Holo', fire: 'Fire' }
const P_STYLES = { plain: 'Plain', glow: 'Glow', shimmer: 'Shimmer', wave: 'Wave', gradient: 'Gradient', neon: 'Neon', glitch: 'Glitch', typewriter: 'Typewriter', chrome: 'Chrome' }
const P_FONTS = { exo: 'Exo', orbitron: 'Orbitron', bebas: 'Bebas', pixel: 'Pixel', marker: 'Marker', script: 'Script', tube: 'Tube' }
const P_SHOWCASES = ['stats', 'most-played', 'recent', 'setup', 'trackers', 'shelf', 'pc', 'gear', 'watching']
const P_SLOTS = { avatar: 'Picture', banner: 'Banner', background: 'Background' }
const P_TURN_MS = 6000
// The profile's one clock: every video starts and loops from it, so loops of one length stay together however late each
// loaded (as Exo Launcher's MediaView keeps them).
const P_CLOCK = performance.now()

const prof = {
  sheet: null,      // 'customize' | 'account' | 'setup' | 'link' | 'recovery'
  draft: null,      // { name, look, urls, files, canvas?, picture?, banner? } while Customize is open
  tab: 'look',      // Customize's tab: 'look' or 'layout'
  selected: null,   // the canvas block picked on the page or in Layout's list
  picking: false,
  durations: {},    // a video block's length, by its media's SHA-256, for its loop's sliders
  busy: null,
  error: null,
  turn: 0,
  turnTimer: 0,
  stopEffect: null,
  enterUntil: 0,    // the room opening plays until then (pEnter)
  enterTimer: 0,
  leanAt: [0, 0],   // the pointer's place in the room, -1 to 1 each way (pLean)
  leanFrame: 0,
  more: false,      // Layout's More, open for the picked block
  hist: { past: [], future: [], last: null, at: 0 }, // Customize's undo and redo (pHistory)
  effectKey: '',
  link: null,       // Link a PC, as the host says (ExoAccount: role, step, nameplate, match, message)
  linkTyped: '',
  code: null,       // the recovery code, shown once
  renaming: null,   // { id, name }
  setupName: '',
  recoveryTyped: '',
}

const pAccentCss = (accent, accent2) => `--profile-accent:${accent}${accent2 ? `;--profile-accent2:${accent2}` : ''}`
const pIsVideo = (type) => !!type && type.startsWith('video/')
const pReduce = () => document.documentElement.dataset.exoMotion === 'reduce'
const pShown = (look) => look.showcases ?? [...P_SHOWCASES]

// One of the look's media: a muted video loop on the profile's clock and its trim, or a picture or GIF; focus is the
// point kept in view, zoom how far in.
function pMedia(url, type, cls = '', view = {}) {
  const css = view.focus || view.zoom
    ? `${view.focus ? `object-position:${view.focus[0]}% ${view.focus[1]}%;transform-origin:${view.focus[0]}% ${view.focus[1]}%;` : ''}${view.zoom && view.zoom !== 100 ? `transform:scale(${view.zoom / 100});` : ''}`
    : ''
  return pIsVideo(type)
    ? `<video class="${cls}" style="${css}" src="${esc(url)}" muted loop playsinline preload="${exoHeld() ? 'metadata' : 'auto'}" aria-hidden="true"${view.trim ? ` data-trim="${view.trim[0]},${view.trim[1]}"` : ''}></video>`
    : `<img class="${cls}" style="${css}" src="${esc(url)}" alt="" draggable="false" decoding="async">`
}

// The profile's picture in its frame: the look's media, else the still picture, else the name's first letter (`art`, a
// poster, stands in for all three on the sign-in page).
function pAvatar({ frame, media, picture, name, size, art, cls = '' }) {
  const letter = esc((name ?? '').trim().charAt(0).toUpperCase())
  const face = art ?? (media ? pMedia(media.url, media.type) : picture ? `<img src="${esc(picture)}" alt="" draggable="false">` : `<span class="exo-profile-avatar-letter">${letter}</span>`)
  return `<span class="exo-profile-avatar-frame is-${frame ?? 'none'} ${cls}" ${size ? ` style="--profile-avatar:${size}px"` : ''} aria-hidden="true"><span class="exo-profile-avatar-face">${face}</span></span>`
}

// A name in its style and font; Wave moves letter by letter, Glitch draws copies of it (data-text), Typewriter types it
// out (--n, its length).
function pName(name, style = 'plain', font, tag = 'h1', cls = '') {
  const letters = Array.from(name)
  const body = style === 'wave'
    ? `<span aria-label="${esc(name)}">${letters.map((l, i) => `<span aria-hidden="true" style="--i:${i}">${l === ' ' ? '&nbsp;' : esc(l)}</span>`).join('')}</span>`
    : esc(name)
  return `<${tag} class="exo-profile-nametag is-${style} ${cls}" data-text="${esc(name)}"${font && font !== 'exo' ? ` data-font="${font}"` : ''} style="--n:${Math.max(1, letters.length)}">${body}</${tag}>`
}

// ---- The art the room borrows where a look has none: backdrops of what is watched most, else of what Home shows

// What was watched, the most first, by show (or film): from Exo Watch's History, each by its IMDb ID.
function pWatched() {
  const by = new Map()
  for (const e of watchData?.history ?? []) {
    const key = e.showId ?? e.id
    if (!key) continue
    const held = by.get(key) ?? { item: { id: key }, count: 0, at: e.at }
    held.count++
    by.set(key, held)
  }
  return [...by.values()].sort((a, b) => b.count - a.count || b.at - a.at)
}

function pArt(count) {
  const items = pWatched().map((w) => w.item)
  for (const e of watchData?.continue ?? []) if (e.showId ?? e.id) items.push({ id: e.showId ?? e.id })
  for (const r of cache.get('rows') ?? []) for (const it of cache.get(`row:${rowKey(r)}`) ?? []) items.push(it)
  const seen = new Set()
  return items.filter((it) => backdrop(it) && !seen.has(it.id) && seen.add(it.id)).slice(0, count)
}

// ---- Showcases, from what the profile itself carries (vectors /16 and /17), so they read the same in every Exo app and
// on a friend's: Exo Launcher's stats (its library's figures, its games with their covers, the part list and gear it
// reads), Cinema's own Watching, and a block's own parts and gear. With nothing to show, a showcase isn't drawn.
function pShowcase(showcase, block, files = {}) {
  const p = exo.profile ?? {}
  const stats = p.stats ?? null
  const games = new Map((stats?.games ?? []).map((g) => [g.id, g]))
  const art = (media) => (media?.sha && files[media.sha]) || ''
  switch (showcase) {
    case 'trackers': return pTrackers(stats, games, block?.trackers ?? ['hours', 'top-game', 'this-year'])
    case 'stats': return pStatsBand(stats, games)
    case 'most-played': return pCovers('Most played', (stats?.most ?? []).map((id) => games.get(id)).filter(Boolean), art, (g) => pPlayed(g.minutes))
    case 'recent': return pCovers('Recently played', (stats?.recent ?? []).map((id) => games.get(id)).filter(Boolean), art, (g) => pDayWords(g.day))
    case 'shelf': return pShelf(stats, games, art, block?.shelf)
    case 'watching': return pCovers('Watching', p.watching?.titles ?? [], (media) => art(media), (t) => pDayWords(t.day), 'poster')
    case 'setup': case 'pc': case 'gear': return pSetup(showcase, block, stats, art)
    default: return ''
  }
}

// Hours played in Exo's words ("1,761 hr", "45 min"; Exo Launcher's hoursWords), each number kept with its unit; nothing
// without minutes.
const pPlayed = (minutes) => (minutes >= 60 ? `${Math.floor(minutes / 60).toLocaleString('en-US')}\u00a0hr` : minutes > 0 ? `${minutes}\u00a0min` : '')

// A day (days since 1970, never the time) in words, as Exo Launcher's dayWords: "Today", "Yesterday", "3 days ago",
// then the date ("Oct 3"), with its year when it isn't this one.
function pDayWords(day) {
  if (day == null) return ''
  const ago = Math.floor(Date.now() / 864e5) - day
  if (ago <= 0) return 'Today'
  if (ago === 1) return 'Yesterday'
  if (ago < 7) return `${ago} days ago`
  const date = new Date(day * 864e5)
  const year = date.getUTCFullYear() === new Date().getUTCFullYear() ? {} : { year: 'numeric' }
  return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', ...year, timeZone: 'UTC' })
}

// The person's three numbers to brag with, from the stats' figures.
function pTrackers(stats, games, chosen) {
  const f = stats?.figures
  if (!f) return ''
  const top = games.get(stats.top)
  const n = (x) => (x ?? 0).toLocaleString('en-US')
  const figure = (t) => ({
    hours: [n(Math.floor((f.minutes ?? 0) / 60)), 'Hours played'],
    games: [n(f.games), 'Games'],
    played: [n(f.played), 'Games played'],
    'top-game': [top?.minutes ? `${n(Math.floor(top.minutes / 60))} h` : '—', top ? `in ${top.title}` : 'Most played'],
    'this-year': [n(f['this-year']), `Played in ${f.year}`],
    installed: [n(f.installed), 'Installed'],
    stores: [n(f.stores), 'Stores'],
    pcs: [n(f.pcs), 'PCs'],
  })[t] ?? ['—', '']
  return `<section class="exo-profile-trackers" aria-label="Trackers">${chosen.map((t) => {
    const [value, label] = figure(t)
    return `<div class="exo-profile-tracker"><span class="exo-profile-tracker-value">${esc(value)}</span><span class="exo-profile-tracker-label">${esc(label)}</span></div>`
  }).join('')}</section>`
}

// Hours, games, the game played most and the PCs, as figures on one glass band.
function pStatsBand(stats, games) {
  const f = stats?.figures
  if (!f) return ''
  const rows = [['Hours played', Math.floor((f.minutes ?? 0) / 60).toLocaleString('en-US')], ['Games', (f.games ?? 0).toLocaleString('en-US')], ['Most played', games.get(stats.top)?.title ?? '—'], ['PCs', String(f.pcs ?? 0)]]
  return `<section class="exo-profile-stats exo-glass is-panel" aria-label="Stats">${rows.map(([label, value]) =>
    `<div class="exo-profile-stat"><span class="exo-profile-stat-value">${esc(value)}</span><span class="exo-profile-stat-label">${esc(label)}</span></div>`).join('')}</section>`
}

// A game's wide art: the top game's hero where it's that one, else its cover.
const pWide = (game, stats, art) => art(game.id === stats.top && stats.hero ? stats.hero : game.cover)

// A cover as the stats keep it (the same picture in every app), or a quiet blank where there's none.
const pCover = (src) => (src
  ? `<img class="exo-profile-cover-art" src="${esc(src)}" alt="" width="300" height="450" draggable="false" loading="lazy" decoding="async">`
  : '<span class="exo-profile-cover-art is-blank"></span>')

// A row of covers (games, or titles watched) with a line under each.
function pCovers(heading, list, art, line, key = 'cover') {
  if (!list.length) return ''
  return `<section class="exo-profile-showcase" aria-label="${esc(heading)}"><h2>${esc(heading)}</h2>${pCoverRow(list, art, line, key)}</section>`
}

const pCoverRow = (list, art, line, key = 'cover') => `<div class="exo-profile-recent">${list.map((item) =>
  `<article class="exo-profile-cover">${pCover(art(item[key]))}<strong>${esc(item.title)}</strong>${line ? `<span>${esc(line(item))}</span>` : ''}</article>`).join('')}</div>`

// The game shelf: a favorite with a line about it, then what's in rotation, wanted and finished. A block names its games;
// without, the game played most and the last four played.
function pShelf(stats, games, art, shelf) {
  if (!stats) return ''
  const pick = (ids) => (ids ?? []).map((id) => games.get(id)).filter(Boolean)
  const favorite = shelf?.favorite ? games.get(shelf.favorite.game) : games.get(stats.top)
  const rotation = shelf ? pick(shelf.rotation) : pick(stats.recent).filter((g) => g.id !== favorite?.id).slice(0, 4)
  const rows = [['In rotation', rotation], ['Want to play', pick(shelf?.want)], ['Finished', pick(shelf?.finished)]].filter(([, list]) => list.length)
  if (!favorite && !rows.length) return ''
  const wide = favorite && pWide(favorite, stats, art)
  return `<section class="exo-profile-showcase exo-profile-shelf" aria-label="Game shelf"><h2>Game shelf</h2>
    ${favorite ? `<article class="exo-profile-favorite">${wide ? `<img class="exo-profile-game-art" src="${esc(wide)}" alt="" draggable="false" decoding="async">` : '<span class="exo-profile-game-art is-blank"></span>'}
      <div class="exo-profile-game-meta"><strong>${esc(favorite.title)}</strong><span>${esc(shelf?.favorite?.note || pPlayed(favorite.minutes))}</span></div></article>` : ''}
    ${rows.map(([label, list]) => `<div class="exo-profile-shelf-row"><h3>${label}</h3>${pCoverRow(list, art, null)}</div>`).join('')}</section>`
}

// The PC's part list and the gear: a block's own (the parts the person named, the gear they picked), else what the stats
// carry from the PC Exo Launcher runs on. A pc block is the part list alone, a gear block the gear alone.
function pSetup(showcase, block, stats, art) {
  // The part list as Exo Launcher draws it (the stats' build: what it reads, with the parts named), each part the block
  // names in place of that part, as Launcher's partList does.
  const named = new Map((block?.build ?? []).filter((b) => b.name.trim()).map((b) => [b.part, b.name.trim()]))
  const parts = showcase === 'gear' ? [] : P_PARTS.flatMap((part) => named.has(part) ? [[P_PART_NAMES[part], named.get(part)]]
    : (stats?.build ?? []).filter((b) => b.part === part && b.name.trim()).map((b) => [P_PART_NAMES[part], b.name.trim()]))
  const gear = showcase === 'pc' ? [] : block?.gear?.length ? block.gear : stats?.gear ?? []
  if (!parts.length && !gear.length) return ''
  const heading = showcase === 'pc' ? 'PC' : showcase === 'gear' ? 'Gear' : 'Setup'
  return `<section class="exo-profile-showcase" aria-label="${heading}"><h2>${heading}</h2>
    <div class="exo-profile-setup${showcase !== 'setup' ? ` is-${showcase}` : ''}">${parts.length ? `<div class="exo-profile-rig">${parts.map(([label, value]) =>
      `<div class="exo-profile-rig-part"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`).join('')}</div>` : ''}${gear.map((pick) => {
    const picture = art(pick.picture)
    // Its shape, once loaded, sizes it (--gear-ratio, below): every piece at one area, as Exo Launcher draws them, so a tall
    // mouse is no bigger than a wide keyboard.
    return `<article class="exo-profile-gear">${picture ? `<div class="exo-profile-gear-frame"><img src="${esc(picture)}" alt="" width="300" height="200" draggable="false"></div>` : '<span class="exo-profile-gear-blank" aria-hidden="true"></span>'}
      <strong>${esc(pGearName(pick))}</strong><span>${esc(pGearLine(pick))}</span></article>`
  }).join('')}</div></section>`
}

// The part list's order and names, as Exo Launcher's (BUILD_PARTS, partLabel).
const P_PARTS = ['cpu', 'gpu', 'memory', 'motherboard', 'storage', 'case', 'psu', 'cooler', 'fans']
const P_PART_NAMES = { cpu: 'CPU', gpu: 'Video card', memory: 'Memory', motherboard: 'Motherboard', storage: 'Storage', case: 'Case', psu: 'Power supply', cooler: 'Cooler', fans: 'Fans' }

// "Sony DualSense Edge": the maker once, then the model (Exo Launcher's gearName).
const pGearName = (pick) => (pick.maker && !pick.model.toLowerCase().startsWith(pick.maker.toLowerCase()) ? `${pick.maker} ${pick.model}` : pick.model)

const P_GEAR_KINDS = { mouse: 'Mouse', keyboard: 'Keyboard', audio: 'Headphones', controller: 'Controller', mousepad: 'Mousepad', microphone: 'Microphone' }
const P_GEAR_LOOKS = { headphones: 'Headphones', iem: 'In-ear monitors', headset: 'Headset' }

// What it is (Exo Launcher's gearLine).
const pGearLine = (pick) => (pick.kind === 'audio' && pick.look ? P_GEAR_LOOKS[pick.look] : P_GEAR_KINDS[pick.kind] ?? '')

// ---- The room

function renderProfile() {
  drawRooms()
  if (!exo.read) {
    view.innerHTML = '<div class="loading"><div class="spinner"></div></div>'
    readAccount()
    return
  }
  if (!exo.enabled) { location.replace('#/home'); return }
  if (!watchData) readWatch()
  let room = view.querySelector(':scope > .exo-profile-room')
  if (!room) {
    view.innerHTML = '<main class="exo-profile-room" aria-label="Profile"></main>'
    room = view.firstElementChild
  }
  // Drawn again only when something it shows changed: a redraw starts every video, effect and moving name over (it
  // flashed, and its motion kept stopping, each time the account or Exo Watch was read again behind it).
  const sig = pSig()
  if (exo.profile && room.dataset.sig === sig && room.querySelector('.exo-profile-stage')) {
    if (prof.resume) { prof.resume = false; pResume(room) }
    return
  }
  prof.resume = false
  // A sheet whose side of the room is gone closes: Account or Customize once signed out, a way in once in a profile.
  if (!exo.profile && (prof.sheet === 'account' || prof.sheet === 'customize')) Object.assign(prof, { sheet: null, draft: null })
  if (exo.profile && ['setup', 'link', 'recovery'].includes(prof.sheet) && !prof.code) prof.sheet = null
  // Each redraw keeps where the page and a sheet were scrolled to.
  const scrolls =[...room.querySelectorAll('.exo-profile-room-scroll, .exo-profile-sheet-scroll')].map((el) => el.scrollTop)
  pHistory()
  if (exo.profile) drawStage(room)
  else drawWelcome(room)
  if (prof.opening && exo.profile) pOpening(room)
  room.querySelectorAll('.exo-profile-room-scroll, .exo-profile-sheet-scroll').forEach((el, i) => { if (scrolls[i]) el.scrollTop = scrolls[i] })
  pPlaceError(room)
  pVideos(room)
  pTurns()
  room.dataset.sig = pSig()
}

// Everything the room's drawing depends on, as one string to compare.
function pSig() {
  const p = prof
  return JSON.stringify([exo.profile, exo.account?.provider, exo.busy, exo.error, exo.email, exoStreaming(), p.sheet, p.draft, p.tab,
    p.selected, p.picking, p.busy, p.error, p.more, p.hist.past.length, p.hist.future.length, p.link, p.linkTyped, p.code, p.renaming,
    p.setupName, p.recoveryTyped, p.durations, pArt(P_STARTERS.length).map((it) => backdrop(it))])
}

// The room kept, not thrown away, while another room is shown (render › pPark): the page as it was, its pictures
// decoded, its videos paused and its effect stopped; back, it's simply there and they go on.
function pPark() {
  const room = view.querySelector(':scope > .exo-profile-room')
  if (!room) return
  let kept = document.getElementById('profileKept')
  if (!kept) {
    kept = document.createElement('div')
    kept.id = 'profileKept'
    kept.hidden = true
    document.body.append(kept)
  }
  kept.replaceChildren(room)
  for (const video of room.querySelectorAll('video')) video.pause()
  prof.stopEffect?.()
  prof.stopEffect = null
  prof.effectKey = ''
  clearInterval(prof.videoTimer)
}

function pUnpark() {
  const room = document.getElementById('profileKept')?.firstElementChild
  if (!room) return
  view.replaceChildren(room)
  prof.resume = true
}

function pResume(room) {
  const v = pView()
  pEffect(room, v.look.effect ?? 'none', v.look.accent ?? P_ACCENT)
  pVideos(room)
  pFit(room)
  pNameFit(room)
}

// What the page draws: the saved profile, or the draft over it while Customize is open.
function pView() {
  const p = exo.profile
  const d = prof.sheet === 'customize' ? prof.draft : null
  return {
    look: d?.look ?? p.look ?? { v: 1 },
    name: d?.name ?? p.name ?? '',
    urls: d?.urls ?? p.media ?? {},
    picture: d && d.picture !== undefined ? d.picture : p.picture,
    banner: d && d.banner !== undefined ? d.banner : p.banner,
    canvas: d && d.canvas !== undefined ? d.canvas : p.canvas ?? null,
    files: { ...(p.files ?? {}), ...(d?.files ?? {}) },
  }
}

// The canvas Layout works on: the draft's, or the default one everyone starts with.
const pCanvasOf = (d) => d.canvas ?? defaultCanvas(d.look)

const pSlot = (v, slot) => v.look[slot] && v.urls[slot] ? { url: v.urls[slot], type: v.look[slot].type, view: v.look[slot] } : null

function drawStage(room) {
  const v = pView()
  const accent = v.look.accent ?? P_ACCENT
  const effect = v.look.effect ?? 'none'
  const wide = pSlot(v, 'banner')
  const busy = prof.busy ? ' disabled' : ''
  const entering = performance.now() < prof.enterUntil
  const canvas = pCanvasShown(v)
  const inBlocks = canvas?.hero === 'blocks'
  const actions = `<button type="button" class="set-btn is-primary" data-p="customize" aria-expanded="${prof.sheet === 'customize'}"${busy}>Customize</button>
    <button type="button" class="set-btn" data-p="account" aria-expanded="${prof.sheet === 'account'}">Account</button>
    <button type="button" class="set-btn" data-app="settings">Settings</button>`
  // The page is the theme's own (Void's black), as Exo Launcher's: no video under the whole room, so the room opens at
  // once and the glass over it stays clean. With the hero as blocks, Customize and Account sit at the layout's top right.
  room.innerHTML = `<div class="exo-profile-stage${prof.sheet === 'customize' ? ' is-customizing' : ''}${entering ? ' is-entering' : ''}" style="${pAccentCss(accent, v.look.accent2)}">
    ${effect !== 'none' ? `<canvas class="exo-profile-effect is-${effect}" aria-hidden="true"></canvas>` : ''}
    <div class="exo-profile-room-scroll is-fit"><div class="exo-profile-page${canvas ? ' is-filled' : ''}${inBlocks ? ' is-hero-blocks' : ''}">
      ${inBlocks ? `<div class="exo-profile-page-actions">${actions}</div>` : `<header class="exo-profile-hero${wide || v.banner || pTopArt(v) ? ' has-banner' : ''}">
        ${wide || v.banner || pTopArt(v) ? `<div class="exo-profile-banner" aria-hidden="true">${wide ? pMedia(wide.url, wide.type, '', wide.view) : `<img src="${esc(v.banner || pTopArt(v))}" alt="" draggable="false">`}</div>` : ''}
        <div class="exo-profile-hero-id">
          ${pAvatar({ frame: v.look.frame, media: pSlot(v, 'avatar'), picture: v.picture, name: v.name, size: 160, cls: 'exo-profile-hero-avatar' })}
          ${pName(v.name || 'Exo profile', v.look.name, v.look.font, 'h1', 'exo-profile-hero-name')}
          <div class="exo-profile-hero-actions">${actions}</div>
        </div>
      </header>`}
      ${pPage(v, canvas)}
    </div></div>
    ${prof.sheet === 'customize' ? pCustomize(v) : prof.sheet === 'account' ? pAccount() : ''}
  </div>`
  pEffect(room, effect, accent)
  cBind(room, {
    onSelect: (id) => { prof.selected = id; renderProfile() },
    onPlace: (id, place) => pCanvasPatch(id, place),
    onAction: (id, action) => pBlockAction(id, action),
  })
  cTilt(room)
  pLean(room.querySelector('.exo-profile-stage'))
  pFit(room)
  pNameFit(room)
  pGearRatios(room)
  if (entering) {
    clearTimeout(prof.enterTimer)
    prof.enterTimer = setTimeout(() => room.querySelector('.exo-profile-stage')?.classList.remove('is-entering'), prof.enterUntil - performance.now())
  }
}

// A picked block's toolbar (and Layout's keys): a copy just under it, one layer nearer or further, or it gone.
function pBlockAction(id, action) {
  prof.draft.canvas = blockAction(pCanvasOf(prof.draft), id, action)
  if (action === 'remove') prof.selected = null
  renderProfile()
}

// ---- Undo and redo for Customize, as Exo Launcher's (useDraftHistory): every change to the draft is a step, a quick run
// of them (a slider dragged, a block nudged along with the keys) one step; a draft that ends or starts again clears both.
// Undo and Redo put a whole earlier draft back, so a step can be anything Customize changes.

const P_RUN_MS = 600, P_STEPS = 100

function pHistory() {
  const h = prof.hist
  if (prof.sheet !== 'customize' || !prof.draft) { Object.assign(h, { past: [], future: [], last: null, at: 0 }); return }
  const now = JSON.stringify(prof.draft)
  if (h.last === null) { h.last = now; return }
  if (now === h.last) return
  if (performance.now() - h.at > P_RUN_MS) {
    h.past.push(h.last)
    if (h.past.length > P_STEPS) h.past.shift()
  }
  h.at = performance.now()
  h.future = []
  h.last = now
}

function pStep(back) {
  const h = prof.hist
  const step = (back ? h.past : h.future).pop()
  if (step === undefined || !prof.draft) return
  ;(back ? h.future : h.past).push(h.last)
  h.last = step
  h.at = 0
  prof.draft = JSON.parse(step)
  if (prof.selected && !pCanvasOf(prof.draft).blocks.some((b) => b.id === prof.selected)) prof.selected = null
  renderProfile()
}

// Customize's keys, as editors everywhere have them: Ctrl+Z and Ctrl+Shift+Z (or Ctrl+Y) undo and redo; with a block
// picked on Layout, Ctrl+D copies it, Delete removes it, Ctrl+] and Ctrl+[ bring it forward or send it back, and Escape
// lets it go. A key typed into a field is the field's.
document.addEventListener('keydown', (e) => {
  if (prof.sheet !== 'customize' || !prof.draft || !view.querySelector('.exo-profile-room') || prof.busy) return
  if (e.target.closest?.('input, textarea, select, [contenteditable="true"]')) return
  const ctrl = e.ctrlKey || e.metaKey, key = e.key.toLowerCase()
  const done = () => { e.preventDefault(); e.stopPropagation() }
  if (ctrl && key === 'z') { done(); pStep(!e.shiftKey); return }
  if (ctrl && key === 'y') { done(); pStep(false); return }
  const picked = pArranging() ? prof.selected : null
  if (!picked) return
  if (ctrl && key === 'd') { done(); pBlockAction(picked, 'copy') }
  else if (key === 'delete' || key === 'backspace') { done(); pBlockAction(picked, 'remove') }
  else if (ctrl && key === ']') { done(); pBlockAction(picked, 'forward') }
  else if (ctrl && key === '[') { done(); pBlockAction(picked, 'back') }
  // Ahead of the sheet's own Escape, so letting a block go doesn't close Customize.
  else if (key === 'escape') { done(); prof.selected = null; renderProfile() }
}, true)

// The room opening: one short fade of the page each time the room is shown (is-entering, in profile.css), as Exo
// Launcher's; not under Reduce motion.
function pEnter() {
  // Coming in from another room: the room opens behind its opening (pOpening) once all of it has loaded.
  if (!view.querySelector(':scope > .exo-profile-room')) prof.opening = true
}

// The room's opening: the profile's picture in its accent's ring on the page's black while everything the room shows
// loads behind it (every picture decoded, every video's first frame, the name's font), then the room comes in whole, as
// one, with its entrance, and the picture grows away. Never longer than 2.5 s.
const P_OPEN_MAX_MS = 2500

function pOpening(room) {
  prof.opening = false
  const v = pView()
  room.classList.add('is-preparing')
  document.querySelector('.exo-profile-opening')?.remove()
  const veil = document.createElement('div')
  veil.className = 'exo-profile-opening'
  veil.setAttribute('aria-hidden', 'true')
  veil.style.cssText = pAccentCss(v.look.accent ?? P_ACCENT, v.look.accent2)
  const still = v.urls.avatar && v.look.avatar?.type?.startsWith('image/') ? v.urls.avatar : v.picture
  veil.innerHTML = `<div class="exo-profile-opening-mark"><span class="exo-profile-opening-ring"></span>${still ? `<img src="${esc(still)}" alt="" draggable="false">` : `<span class="exo-profile-opening-letter">${esc((v.name || 'E').slice(0, 1).toUpperCase())}</span>`}</div>`
  // As Exo Launcher's opening: a room ready within 300 ms shows no mark at all; past that the mark comes and, once shown,
  // stays at least 400 ms, so it never flashes.
  veil.classList.add('is-quiet')
  document.body.append(veil)
  const wait = (ms) => new Promise((r) => setTimeout(r, Math.max(0, ms)))
  let shownAt = 0
  const show = setTimeout(() => { veil.classList.remove('is-quiet'); shownAt = performance.now() }, 300)
  Promise.race([pLoaded(room), wait(P_OPEN_MAX_MS)]).then(async () => {
    clearTimeout(show)
    if (shownAt) await wait(400 - (performance.now() - shownAt))
    if (!room.isConnected) { veil.remove(); return }
    prof.enterUntil = pReduce() ? 0 : performance.now() + 500
    const stage = room.querySelector('.exo-profile-stage')
    if (prof.enterUntil) {
      stage?.classList.add('is-entering')
      clearTimeout(prof.enterTimer)
      prof.enterTimer = setTimeout(() => room.querySelector('.exo-profile-stage')?.classList.remove('is-entering'), 500)
    }
    room.classList.remove('is-preparing')
    if (!shownAt) { veil.remove(); return }
    veil.classList.add('is-leaving')
    setTimeout(() => veil.remove(), pReduce() ? 0 : 450)
  })
}

// Everything the room shows, loaded: its pictures decoded (a lazy one made to load now), its videos at their first frame,
// and the fonts its name and blocks use.
function pLoaded(room) {
  const imgs = [...room.querySelectorAll('img')]
  for (const img of imgs) if (img.loading === 'lazy') img.loading = 'eager'
  const videos = [...room.querySelectorAll('video')].map((video) => video.readyState >= 2 ? null
    : new Promise((r) => { video.addEventListener('loadeddata', r, { once: true }); video.addEventListener('error', r, { once: true }) }))
  return Promise.allSettled([...imgs.map((img) => img.decode()), ...videos, document.fonts.ready])
}

// Depth: the pointer's place in the room (-1 to 1 each way, as --px and --py on the stage, once a frame), which the
// backdrop, the effect, the banner and each picture block follow by how far back they sit. Held still while a layout is
// arranged, so the blocks stay where the pointer drags them, and under Reduce motion.
function pLean(stage) {
  if (!stage) return
  const still = () => pReduce() || (prof.sheet === 'customize' && prof.tab === 'layout')
  const put = () => {
    prof.leanFrame = 0
    const [x, y] = still() ? [0, 0] : prof.leanAt
    stage.style.setProperty('--px', x.toFixed(3))
    stage.style.setProperty('--py', y.toFixed(3))
  }
  put()
  stage.addEventListener('pointermove', (e) => {
    if (still()) return
    const box = stage.getBoundingClientRect()
    prof.leanAt = [((e.clientX - box.left) / box.width) * 2 - 1, ((e.clientY - box.top) / box.height) * 2 - 1]
    prof.leanFrame ||= requestAnimationFrame(put)
  })
}

// What the page holds under the hero: the canvas being arranged (Customize's Layout), else the canvas as laid out, else the
// showcases one under another.
function pPage(v, canvas) {
  const hero = { look: v.look, name: v.name, banner: pSlot(v, 'banner'), bannerStill: v.banner, avatar: pSlot(v, 'avatar'), picture: v.picture, art: pTopArt(v) }
  return cCanvas(canvas, { editing: pArranging(), selected: prof.selected, files: v.files, hero, showcase: (s, block) => pShowcase(s, block, v.files) })
}

// The banner's art while there's no banner of one's own: the top game's wide art from the stats (/17), as Exo Launcher's.
const pTopArt = (v) => (exo.profile?.stats?.hero?.sha && v.files[exo.profile.stats.hero.sha]) || ''

const pArranging = () => !!(prof.sheet === 'customize' && prof.tab === 'layout' && prof.draft)
// The layout the page shows: the one being arranged, else the profile's own, else the default everyone starts with.
const pCanvasShown = (v) => (pArranging() ? pCanvasOf(prof.draft) : v.canvas ?? defaultCanvas(v.look))

// One screen, never a scroll (the user's: "i want profiles to no scroll so it must all fit on one page"). A layout fills
// the room edge to edge, its rows shared out down the height, and is never scaled (a scaled page resamples every frame and
// word, so it reads soft); the showcases list is drawn at its own size and scaled down from its top until all of it fits
// the room's height, never up past its size.
function pFit(room) {
  const box = room.querySelector('.exo-profile-room-scroll.is-fit')
  const page = box?.querySelector('.exo-profile-page')
  prof.fitObserver?.disconnect()
  if (!page || page.classList.contains('is-filled')) return
  const fit = () => {
    const style = getComputedStyle(box)
    const height = box.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom)
    page.style.transform = ''
    const natural = page.scrollHeight
    const scale = natural > 0 && height > 0 ? Math.min(1, height / natural) : 1
    page.style.transform = scale < 1 ? `scale(${scale})` : ''
  }
  fit()
  prof.fitObserver = new ResizeObserver(fit)
  prof.fitObserver.observe(box)
}

// A name block's name: as tall as the block lets it be, and smaller only when it would run past the block's width (a wide
// font, a long name), as Exo Launcher's BlockName measures it: from name to name a letter is 0.37 to 0.94 of its height
// wide in the same font, so no count of letters can say it.
function pNameFit(room) {
  prof.nameObserver?.disconnect()
  const names = [...room.querySelectorAll('.exo-profile-block-name')]
  if (!names.length) return
  const fit = (el) => {
    const body = el.parentElement
    if (!body) return
    const was = Number(el.style.getPropertyValue('--name-fit')) || 1
    const full = el.offsetWidth / was
    const pad = getComputedStyle(body)
    const fits = body.clientWidth - parseFloat(pad.paddingLeft) - parseFloat(pad.paddingRight)
    const next = full > 0 ? Math.min(1, fits / full) : 1
    if (Math.abs(next - was) > 0.004) el.style.setProperty('--name-fit', next.toFixed(3))
  }
  const all = () => names.forEach(fit)
  all()
  prof.nameObserver = new ResizeObserver(all)
  for (const el of names) if (el.parentElement) prof.nameObserver.observe(el.parentElement)
  document.fonts.ready.then(all)
}

// A gear picture's shape, as it loads (load doesn't bubble, so it's caught on its way down), or now for one the cache
// had ready before the listener could hear it.
function pGearRatios(room) {
  for (const img of room.querySelectorAll('.exo-profile-gear-frame > img'))
    if (img.complete && img.naturalHeight > 0) img.style.setProperty('--gear-ratio', String(img.naturalWidth / img.naturalHeight))
}
// A gear picture's shape, as it loads (load doesn't bubble, so it's caught on its way down).
document.addEventListener('load', (e) => {
  const img = e.target
  if (img instanceof HTMLImageElement && img.parentElement?.classList.contains('exo-profile-gear-frame') && img.naturalHeight > 0)
    img.style.setProperty('--gear-ratio', String(img.naturalWidth / img.naturalHeight))
}, true)

function pCanvasPatch(id, values) {
  const c = pCanvasOf(prof.draft)
  prof.draft.canvas = { ...c, blocks: c.blocks.map((b) => (b.id === id ? cClean({ ...b, ...values }) : b)) }
  prof.selected = id
  renderProfile()
}

// The effect runs on its canvas until the next redraw or the room goes.
function pEffect(room, effect, accent) {
  prof.stopEffect?.()
  prof.stopEffect = startEffect(room.querySelector('.exo-profile-effect'), effect, accent)
}

// Every video in the room keeps to the profile's clock (put back when it drifts 150 ms) and plays only its trim, and
// plays at all only while the room shows, motion is on and no game holds it (window.js › exoHeld).
function pVideos(room) {
  for (const video of room.querySelectorAll('video')) {
    const [start, end] = video.dataset.trim ? video.dataset.trim.split(',').map((ms) => Number(ms) / 1000) : [0, NaN]
    const keep = () => {
      const length = (Number.isFinite(end) ? end : video.duration) - start
      if (!Number.isFinite(length) || length <= 0) return
      const due = start + ((performance.now() - P_CLOCK) / 1000) % length
      if (Math.abs(video.currentTime - due) > 0.15) video.currentTime = due
    }
    video.addEventListener('loadedmetadata', keep)
    video.addEventListener('timeupdate', () => { if (Number.isFinite(end) && video.currentTime >= end) video.currentTime = start })
    if (!pReduce() && !exoHeld()) video.play().catch(() => {})
  }
  clearInterval(prof.videoTimer)
  prof.videoTimer = setInterval(() => {
    if (!room.isConnected) { clearInterval(prof.videoTimer); return }
    for (const video of room.querySelectorAll('video')) video.dispatchEvent(new Event('loadedmetadata'))
  }, 2000)
}

// A game started or ended, or the pointer came over the window or left it: the room's videos hold or go on.
window.addEventListener('exo:held', () => {
  for (const video of view.querySelectorAll('.exo-profile-room video')) exoHeld() || pReduce() ? video.pause() : video.play().catch(() => {})
})

// ---- Signed out: what a profile can be, before anything is asked

function drawWelcome(room) {
  const art = pArt(P_STARTERS.length)
  const look = P_STARTERS[prof.turn]
  const on = art.length ? prof.turn % art.length : -1
  const cover = (it) => it ? `<img class="exo-profile-avatar-art" src="${esc(poster(it, 300))}" alt="" draggable="false">` : null
  const parade = [...P_STARTERS, ...P_STARTERS]
  room.innerHTML = `<div class="exo-profile-welcome" style="${pAccentCss(look.accent)}">
    <div class="exo-profile-backdrop" aria-hidden="true">${art.map((it, i) =>
      `<div class="exo-profile-backdrop-layer${i === on ? ' is-on' : ''}"><img src="${esc(backdrop(it))}" alt="" draggable="false"></div>`).join('')}</div>
    <canvas class="exo-profile-effect is-${look.effect}" aria-hidden="true"></canvas>
    <div class="exo-profile-welcome-body">
      <div class="p-turned">${pWelcomeTurn(art)}</div>
      ${exo.account ? `<div class="exo-profile-signin-panel exo-glass is-panel"><p class="set-name">Signed in. Setting up your profile…</p></div>`
        : exo.providers.length ? `<div class="exo-profile-signin-panel exo-glass is-panel">${exoWays('set-btn')}</div>`
          : `<div class="exo-profile-signin-panel exo-glass is-panel"><p class="set-name">Sign in with your Exo account in Settings › Account.</p></div>`}
      ${exo.providers.length ? `<div class="exo-profile-welcome-more">
        <button type="button" class="set-btn" data-p="setup">Set up without an account</button>
        <button type="button" class="set-btn" data-p="link">Link with a code</button>
        <button type="button" class="set-btn" data-p="recovery">Recovery code</button>
      </div>` : ''}
    </div>
    <div class="exo-profile-parade" aria-hidden="true"><div class="exo-profile-parade-track${pReduce() || prof.sheet ? ' is-paused' : ''}">${parade.map((s, i) => {
      const it = art.length ? art[(i + 1) % art.length] : null
      return `<div class="exo-profile-card${i % P_STARTERS.length === prof.turn ? ' is-on' : ''}" style="${pAccentCss(s.accent)}">
        <div class="exo-profile-card-art">${it ? `<img class="exo-profile-card-wash" src="${esc(backdrop(it, 640))}" alt="" draggable="false">` : ''}</div>
        ${pAvatar({ frame: s.frame, size: 64, cls: 'exo-profile-card-avatar', art: cover(it) })}
        ${pName(s.name, s.style, null, 'strong', 'exo-profile-card-name')}</div>`
    }).join('')}</div></div>
    ${prof.sheet ? pWays() : ''}
  </div>`
  pEffect(room, look.effect, look.accent)
}

// The look on stage now: its framed poster and "Your profile" in its style.
function pWelcomeTurn(art) {
  const look = P_STARTERS[prof.turn]
  const it = art.length ? art[prof.turn % art.length] : null
  return `${pAvatar({ frame: look.frame, size: 148, cls: 'exo-profile-welcome-avatar', art: it ? `<img class="exo-profile-avatar-art" src="${esc(poster(it, 300))}" alt="" draggable="false">` : null })}
    ${pName('Your profile', look.style, null, 'h1', 'exo-profile-welcome-title')}`
}

// The next starter look takes the stage: only what it changes is drawn again, so the sign-in keeps what is typed in it.
function pTurns() {
  clearInterval(prof.turnTimer)
  if (exo.profile || pReduce()) return
  prof.turnTimer = setInterval(() => {
    const welcome = view.querySelector('.exo-profile-welcome')
    if (!welcome || route() !== 'profile') { clearInterval(prof.turnTimer); return }
    if (prof.sheet || welcome.contains(document.activeElement) && document.activeElement.matches('input')) return
    prof.turn = (prof.turn + 1) % P_STARTERS.length
    const art = pArt(P_STARTERS.length)
    const look = P_STARTERS[prof.turn]
    welcome.style.cssText = pAccentCss(look.accent)
    welcome.querySelectorAll('.exo-profile-backdrop-layer').forEach((layer, i) => layer.classList.toggle('is-on', art.length > 0 && i === prof.turn % art.length))
    welcome.querySelector('.p-turned').innerHTML = pWelcomeTurn(art)
    welcome.querySelectorAll('.exo-profile-card').forEach((card, i) => card.classList.toggle('is-on', i % P_STARTERS.length === prof.turn))
    const canvas = welcome.querySelector('.exo-profile-effect')
    canvas.className = `exo-profile-effect is-${look.effect}`
    prof.stopEffect?.()
    prof.stopEffect = startEffect(canvas, look.effect, look.accent)
  }, P_TURN_MS)
}

// ---- Sheets: Customize at the room's side; Account and the ways in (set up, link, the recovery code) centered

// As Exo Launcher's GameOverlay draws it (its presence layer, which profile.css places Customize by): Customize's scrim
// lets presses through to the page, so its blocks can be arranged while the panel is open.
const pOverlay = (sheet, scrim, label) => `<div class="exo-game-overlay-presence"><div class="exo-game-overlay" role="dialog" aria-modal="${scrim !== 'clear'}" aria-label="${label}">
  <button type="button" class="exo-game-overlay-scrim${scrim === 'clear' ? ' is-clear' : ''}" aria-label="Close" tabindex="-1" data-p="${scrim === 'clear' ? 'cancel' : 'close'}"></button>
  <div class="exo-game-overlay-stage">${sheet}</div></div></div>`

const pBusy = (what, words) => `${prof.busy === what ? '<span class="spinner"></span>' : ''}${words}`
// An error reads under the control that caused it (one action, one response; Launcher's AccountSheet): drawn after the
// room by pPlaceError under the pressed control's row; under the sheet's head only when that control isn't there anymore.
const pError = () => ''

// The control last pressed in the room or a sheet, as a selector to find it again after a redraw.
document.addEventListener('click', (e) => {
  const t = e.target.closest?.('.exo-profile-room [data-p]')
  if (!t || t.classList.contains('exo-game-overlay-scrim')) return
  prof.pressed = `[data-p="${CSS.escape(t.dataset.p)}"]${t.dataset.id ? `[data-id="${CSS.escape(t.dataset.id)}"]` : ''}${t.dataset.key ? `[data-key="${CSS.escape(t.dataset.key)}"]` : ''}`
}, true)

function pPlaceError(room) {
  room.querySelector('.exo-profile-sheet-error')?.remove()
  if (!prof.error) return
  const words = Object.assign(document.createElement('p'), { className: 'form-error exo-profile-sheet-error press-error', textContent: prof.error })
  words.setAttribute('role', 'alert')
  const control = prof.pressed && room.querySelector(prof.pressed)
  const row = control?.closest('.set-row, .exo-actions, .exo-profile-sheet-head, .exo-profile-form')
  if (row) { row.after(words); return }
  const head = room.querySelector('.exo-profile-sheet .exo-profile-sheet-head')
  if (head) head.after(words)
  else room.querySelector('.exo-profile-sheet-body, .exo-profile-welcome')?.prepend(words)
}

function pCustomize(v) {
  const look = v.look
  const accent = look.accent ?? P_ACCENT
  const busy = prof.busy ? ' disabled' : ''
  const radio = (on) => `role="radio" aria-checked="${on}"`
  const shown = pShown(look)
  const order = [...shown, ...P_SHOWCASES.filter((s) => !shown.includes(s))]
  // The picture, banner and background, a row each as Settings' are: a small look at what it is now, Choose and Remove.
  const mediaRow = (slot) => {
    const media = look[slot]
    const url = prof.draft.urls[slot]
    const still = slot === 'avatar' ? v.picture : slot === 'banner' ? v.banner : null
    const kind = media && pIsVideo(media.type) ? 'Video' : media?.type === 'image/gif' ? 'GIF' : media || still ? 'Picture' : 'None'
    return `<div class="set-row exo-profile-row"><span class="exo-profile-media-thumb is-${slot}">${url && media ? pMedia(url, media.type, '', media) : still ? `<img src="${esc(still)}" alt="" draggable="false">` : ''}</span>
      <span class="set-name">${P_SLOTS[slot]}<span class="exo-profile-row-detail">${kind}</span></span>
      <span class="set-actions"><button type="button" class="set-btn is-compact" data-p="pick" data-slot="${slot}"${busy}>${pBusy('pick-' + slot, 'Choose')}</button>
        ${media || still ? `<button type="button" class="set-btn is-compact" data-p="drop" data-slot="${slot}"${busy}>Remove</button>` : ''}</span></div>`
  }
  const swatches = (key, current) => P_ACCENTS.map((color) => `<button type="button" ${radio(current === color)} aria-label="${color}" class="exo-profile-swatch" style="background:${color}" data-p="set" data-key="${key}" data-value="${color}"${busy}>${current === color ? icon('check') : ''}</button>`).join('')
  const name = esc(prof.draft.name.trim() || 'Name')
  return pOverlay(`<div class="exo-profile-sheet is-customize exo-glass is-panel" style="${pAccentCss(accent)}">
    <header class="exo-profile-sheet-head"><h2>Customize</h2><span class="exo-profile-spacer"></span>
      <button type="button" class="set-btn is-compact" data-p="undo" aria-label="Undo" aria-keyshortcuts="Control+Z"${busy || !prof.hist.past.length ? ' disabled' : ''}>${icon('undo')}</button>
      <button type="button" class="set-btn is-compact" data-p="redo" aria-label="Redo" aria-keyshortcuts="Control+Shift+Z"${busy || !prof.hist.future.length ? ' disabled' : ''}>${icon('redo')}</button>
      <button type="button" class="set-btn" data-p="cancel"${busy}>Cancel</button>
      <button type="button" class="set-btn is-primary" data-p="save"${busy || !prof.draft.name.trim() ? ' disabled' : ''}>${pBusy('save', 'Save')}</button></header>
    <div class="exo-glass segmented exo-profile-tabs" role="tablist" aria-label="Customize">${['look', 'layout'].map((tab) =>
      `<button type="button" role="tab" aria-selected="${prof.tab === tab}" class="seg${prof.tab === tab ? ' is-on' : ''}" data-p="tab" data-value="${tab}">${tab === 'look' ? 'Look' : 'Layout'}</button>`).join('')}</div>
    ${pError()}
    <div class="exo-profile-sheet-scroll">${prof.tab === 'layout'
      ? cLayout(pCanvasOf(prof.draft), prof.selected, { busy, picking: prof.picking, error: prof.error, durations: prof.durations, more: prof.more })
      : `<section class="exo-profile-sheet-part"><h3>Media</h3><div class="exo-set-list">${['avatar', 'banner', 'background'].map(mediaRow).join('')}</div></section>
      <section class="exo-profile-sheet-part"><h3>Name</h3>
        <input class="exo-field exo-profile-name-field" id="pName" value="${esc(prof.draft.name)}" maxlength="64" autocomplete="off" spellcheck="false" aria-label="Name"${busy}>
        <div class="exo-profile-tiles" role="radiogroup" aria-label="Name style">${Object.entries(P_STYLES).map(([style, label]) =>
          `<button type="button" ${radio((look.name ?? 'plain') === style)} class="exo-profile-tile" data-p="set" data-key="name" data-value="${style}"${busy}>${pName(prof.draft.name.trim() || 'Name', style, look.font, 'span', 'is-tile')}<span class="exo-profile-tile-label">${label}</span></button>`).join('')}</div></section>
      <section class="exo-profile-sheet-part"><h3>Font</h3>
        <div class="exo-profile-tiles" role="radiogroup" aria-label="Font">${Object.entries(P_FONTS).map(([font, label]) =>
          `<button type="button" ${radio((look.font ?? 'exo') === font)} class="exo-profile-tile" data-p="set" data-key="font" data-value="${font}"${busy}>${pName(prof.draft.name.trim() || 'Name', 'plain', font, 'span', 'is-tile')}<span class="exo-profile-tile-label">${label}</span></button>`).join('')}</div></section>
      ${look.name === 'gradient' || look.name === 'glitch' ? `<section class="exo-profile-sheet-part"><h3>Second color</h3><div class="exo-profile-swatches" role="radiogroup" aria-label="Second color">${swatches('accent2', look.accent2 ?? '')}</div></section>` : ''}
      <section class="exo-profile-sheet-part"><h3>Looks</h3><div class="exo-profile-tiles is-looks">${P_STARTERS.map((s, i) =>
        `<button type="button" class="exo-profile-tile is-look" style="${pAccentCss(s.accent)}" data-p="starter" data-value="${i}"${busy}>${pAvatar({ frame: s.frame, picture: v.picture, name, size: 44 })}${pName(s.name, s.style, null, 'span', 'is-tile')}</button>`).join('')}</div></section>
      <section class="exo-profile-sheet-part"><h3>Accent</h3><div class="exo-profile-swatches" role="radiogroup" aria-label="Accent">${swatches('accent', accent)}
        <label class="exo-profile-swatch is-well${P_ACCENTS.includes(accent) ? '' : ' is-on'}"${P_ACCENTS.includes(accent) ? '' : ` style="background:${accent}"`}><input type="color" id="pAccent" value="${accent}" aria-label="Your own color"${busy}></label></div></section>
      <section class="exo-profile-sheet-part"><h3>Frame</h3><div class="exo-profile-tiles" role="radiogroup" aria-label="Frame">${Object.entries(P_FRAMES).map(([frame, label]) =>
        `<button type="button" ${radio((look.frame ?? 'none') === frame)} class="exo-profile-tile" data-p="set" data-key="frame" data-value="${frame}"${busy}>${pAvatar({ frame, picture: v.picture, name: v.name, size: 52 })}<span class="exo-profile-tile-label">${label}</span></button>`).join('')}</div></section>
      <div class="exo-set-list">${cRow('Effect', cChoice('Effect', 'effect', look.effect ?? 'none', busy))}</div>
      ${prof.draft.canvas ? '' : `<section class="exo-profile-sheet-part"><h3>Showcases</h3><div class="exo-profile-showcase-list">${order.map((s) => {
        const on = shown.includes(s)
        const at = shown.indexOf(s)
        return `<div class="exo-profile-showcase-row${on ? '' : ' is-off'}"><span class="set-name">${C_SHOWCASE_NAMES[s]}</span><span class="exo-profile-spacer"></span>
          <button type="button" class="set-btn is-compact exo-profile-icon-btn" aria-label="Move ${C_SHOWCASE_NAMES[s]} up" data-p="move" data-value="${s}" data-by="-1"${busy || !on || at === 0 ? ' disabled' : ''}>↑</button>
          <button type="button" class="set-btn is-compact exo-profile-icon-btn" aria-label="Move ${C_SHOWCASE_NAMES[s]} down" data-p="move" data-value="${s}" data-by="1"${busy || !on || at === shown.length - 1 ? ' disabled' : ''}>↓</button>
          <button type="button" class="set-btn is-compact" role="switch" aria-checked="${on}" data-p="show" data-value="${s}"${busy}>${on ? 'Shown' : 'Hidden'}</button></div>`
      }).join('')}</div></section>`}`}
    </div></div>`, 'clear', 'Customize profile')
}

function pAccount() {
  const p = exo.profile
  const a = exo.account
  const busy = prof.busy ? ' disabled' : ''
  const pcs = p.pcs ?? []
  const account = a
    ? `<div class="set-row"><span class="set-name">${esc((!exoStreaming() && a.email) || `Signed in with ${PROVIDER_NAMES[a.provider] ?? a.provider}`)}</span><button type="button" class="set-btn" data-exo="sign-out"${exo.busy ? ' disabled' : ''}>Sign out</button></div>${exoError()}`
    : exo.providers.length ? `<div class="set-row exo-row"><span class="set-name">Sign in</span>${exoWays('set-btn')}</div>` : ''
  // Offered once a second PC is linked (plan §2.12): with one PC there is nothing to take back yet.
  const recovery = p.recovery || pcs.length >= 2
    ? `<div class="set-row"><span class="set-name">Recovery code</span>${p.recovery ? '<span class="set-value">On</span>' : `<button type="button" class="set-btn" data-p="recovery-on"${busy}>${pBusy('recovery-on', 'Turn on')}</button>`}</div>`
    : ''
  const shownCode = prof.code ? `<div class="exo-profile-code"><span class="exo-profile-recovery" aria-label="Recovery code ${esc(prof.code)}">${esc(prof.code)}</span>
    <div class="exo-profile-actions"><button type="button" class="set-btn is-primary" data-p="code-done">Done</button></div></div>` : ''
  const linking = prof.sheet === 'account' && prof.linking
  const rows = pcs.map((pc) => {
    const renaming = prof.renaming?.id === pc.id
    return `<div class="set-row"><span class="set-name">${esc(pc.name)}</span>${pc.me ? '<span class="set-value">This PC</span>' : ''}
      <button type="button" class="set-btn" data-p="rename" data-id="${esc(pc.id)}" aria-expanded="${renaming}"${busy}>Rename</button>
      ${pc.me ? '' : `<button type="button" class="set-btn" data-p="remove" data-id="${esc(pc.id)}"${busy}>${pBusy('remove-' + pc.id, 'Remove')}</button>`}</div>
      ${renaming ? `<div class="exo-profile-form exo-form"><input class="exo-field" id="pRename" value="${esc(prof.renaming.name)}" maxlength="64" autocomplete="off" spellcheck="false" aria-label="PC name"${busy}>
        <div class="exo-profile-actions exo-actions"><button type="button" class="set-btn" data-p="rename-cancel"${busy}>Cancel</button>
        <button type="button" class="set-btn is-primary" data-p="rename-save"${busy || !prof.renaming.name.trim() || prof.renaming.name.trim() === pc.name ? ' disabled' : ''}>${pBusy('rename', 'Save')}</button></div></div>` : ''}`
  }).join('')
  return pOverlay(`<div class="exo-profile-sheet is-account exo-glass is-panel">
    <header class="exo-profile-sheet-head"><h2>Account</h2><span class="exo-profile-spacer"></span><button type="button" class="set-btn" data-p="close">Done</button></header>
    ${pError()}
    <div class="exo-profile-sheet-scroll">
      <section class="set-group"><h2>Exo account</h2>${account}${recovery}${shownCode}</section>
      <section class="set-group"><div class="set-row"><h2 class="set-name">PCs</h2><button type="button" class="set-btn is-compact" data-p="add-link" aria-expanded="${linking}"${busy}>Link a PC</button></div>
        ${linking ? pAddLink() : ''}${rows}</section>
    </div></div>`, 'dim', 'Account')
}

// On the PC already in: the 8 digits the other PC shows, then the 6 digits both show.
function pAddLink() {
  const link = prof.link?.role === 'existing' ? prof.link : null
  if (link) return pMatch(link)
  const digits = prof.linkTyped.replace(/[\s-]/g, '')
  return `<div class="exo-profile-form exo-form"><input class="exo-field exo-profile-digits-field" id="pLinkTyped" value="${esc(prof.linkTyped)}" inputmode="numeric" autocomplete="off" spellcheck="false" placeholder="0000 0000" aria-label="The code your other PC shows"${prof.busy ? ' disabled' : ''}>
    <div class="exo-profile-actions exo-actions"><button type="button" class="set-btn" data-p="link-cancel">Cancel</button>
    <button type="button" class="set-btn is-primary" data-p="link-join"${prof.busy || !/^\d{8}$/.test(digits) ? ' disabled' : ''}>${pBusy('link-join', 'Link')}</button></div></div>`
}

// Both PCs show the same 6 digits: They match goes on, anything else stops it.
function pMatch(link) {
  if (link.step === 'failed') return `<div class="exo-profile-code"><p class="form-error" role="alert">${esc(link.message || 'The link didn’t finish. Start again.')}</p>
    <div class="exo-profile-actions"><button type="button" class="set-btn" data-p="link-cancel">Close</button></div></div>`
  if (link.step === 'done') return '<div class="exo-profile-wait"><span class="set-value">Linked</span></div>'
  const working = link.step === 'working'
  return `<div class="exo-profile-code"><div class="exo-profile-code-words"><span class="exo-profile-digits is-match" aria-label="Match code ${esc(link.match ?? '')}">${esc((link.match ?? '').replace(/^(\d{3})(\d{3})$/, '$1 $2'))}</span></div>
    <div class="exo-profile-actions"><button type="button" class="set-btn" data-p="link-cancel"${working ? ' disabled' : ''}>Cancel</button>
    <button type="button" class="set-btn is-primary" data-p="link-confirm"${working ? ' disabled' : ''}>${working ? '<span class="spinner"></span>' : ''}They match</button></div></div>`
}

// Signed out: the ways in that aren't the account, each a small sheet under the sign-in.
function pWays() {
  const title = { setup: 'Set up without an account', link: 'Link with a code', recovery: 'Recovery code' }[prof.sheet]
  let body = ''
  if (prof.sheet === 'setup') {
    body = `<div class="exo-profile-form exo-form"><input class="exo-field" id="pSetupName" value="${esc(prof.setupName)}" maxlength="64" autocomplete="off" spellcheck="false" placeholder="Your name" aria-label="Your name"${prof.busy ? ' disabled' : ''}>
      <div class="exo-profile-actions exo-actions"><button type="button" class="set-btn" data-p="close"${prof.busy ? ' disabled' : ''}>Cancel</button>
      <button type="button" class="set-btn is-primary" data-p="setup-create"${prof.busy || !prof.setupName.trim() ? ' disabled' : ''}>${pBusy('setup', 'Create')}</button></div></div>`
  } else if (prof.sheet === 'link') {
    const link = prof.link?.role === 'new' ? prof.link : null
    body = !link ? '<div class="exo-profile-wait"><span class="spinner"></span></div>'
      : link.step === 'code' ? `<div class="exo-profile-code"><div class="exo-profile-code-words"><span class="exo-profile-digits" aria-label="Link code ${esc(link.nameplate ?? '')}">${esc((link.nameplate ?? '').replace(/^(\d{4})(\d{4})$/, '$1 $2'))}</span><span class="spinner"></span></div>
          <div class="exo-profile-actions"><button type="button" class="set-btn" data-p="close">Cancel</button></div></div>`
      : pMatch(link)
  } else if (prof.code) {
    body = `<div class="exo-profile-code"><span class="exo-profile-recovery" aria-label="Recovery code ${esc(prof.code)}">${esc(prof.code)}</span>
      <div class="exo-profile-actions"><button type="button" class="set-btn is-primary" data-p="code-done">Done</button></div></div>`
  } else {
    const typed = prof.recoveryTyped.replace(/[\s-]/g, '')
    body = `<div class="exo-profile-form exo-form"><input class="exo-field exo-profile-code-field" id="pRecovery" value="${esc(prof.recoveryTyped)}" autocomplete="off" spellcheck="false" placeholder="ABCDE-FGHIJ-KLMNO-PQRST-UVWXYZ" aria-label="Recovery code"${prof.busy ? ' disabled' : ''}>
      <div class="exo-profile-actions exo-actions"><button type="button" class="set-btn" data-p="close"${prof.busy ? ' disabled' : ''}>Cancel</button>
      <button type="button" class="set-btn is-primary" data-p="restore"${prof.busy || typed.length !== 26 ? ' disabled' : ''}>${pBusy('restore', 'Sign in')}</button></div></div>`
  }
  return pOverlay(`<div class="exo-profile-sheet is-small exo-glass is-panel"><header class="exo-profile-sheet-head"><h2>${title}</h2></header>${pError()}
    <div class="exo-profile-sheet-body">${body}</div></div>`, 'dim', title)
}

// ---- What the room does

async function pRun(what, call, fallback) {
  prof.busy = what
  prof.error = null
  renderProfile()
  try { return await call() } catch (err) { prof.error = err.message || fallback; return null } finally {
    prof.busy = null
    await readAccount()
  }
}

function pUpdate(patch) {
  prof.draft.look = { ...prof.draft.look, ...patch }
  for (const [key, value] of Object.entries(patch)) if (value === undefined) delete prof.draft.look[key]
  renderProfile()
}

// A still of a picked file: the file itself for a picture or GIF, a frame half a second in for a video.
async function pStill(url, type) {
  if (!pIsVideo(type)) { try { return await (await fetch(url)).blob() } catch { return null } }
  return await new Promise((resolve) => {
    const video = Object.assign(document.createElement('video'), { muted: true, preload: 'auto', crossOrigin: 'anonymous', src: url })
    video.onloadeddata = () => { video.currentTime = Math.min(0.5, (video.duration || 1) / 2) }
    video.onseeked = () => {
      const canvas = Object.assign(document.createElement('canvas'), { width: video.videoWidth, height: video.videoHeight })
      canvas.getContext('2d').drawImage(video, 0, 0)
      canvas.toBlob((blob) => resolve(blob), 'image/png')
    }
    video.onerror = () => resolve(null)
  })
}

// A still cut to the profile's size from its middle, as Exo Launcher's (lib/exoProfile.ts): the best WebP quality that
// fits in 48,000 bytes, or null when it can't be read or won't fit.
async function pWebp(blob, width, height) {
  let bitmap
  try { bitmap = await createImageBitmap(blob) } catch { return null }
  const scale = Math.min(bitmap.width / width, bitmap.height / height)
  const canvas = Object.assign(document.createElement('canvas'), { width, height })
  const context = canvas.getContext('2d')
  context.imageSmoothingQuality = 'high'
  context.drawImage(bitmap, (bitmap.width - width * scale) / 2, (bitmap.height - height * scale) / 2, width * scale, height * scale, 0, 0, width, height)
  bitmap.close()
  for (const quality of [0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3]) {
    const out = await new Promise((resolve) => canvas.toBlob(resolve, 'image/webp', quality))
    if (!out || out.type !== 'image/webp') return null
    if (out.size <= 48000) return await new Promise((resolve) => { const r = new FileReader(); r.onload = () => resolve(String(r.result)); r.onerror = () => resolve(null); r.readAsDataURL(out) })
  }
  return null
}

async function pPick(slot) {
  prof.busy = 'pick-' + slot
  prof.error = null
  renderProfile()
  try {
    const answer = await app('account-media-pick')
    if (answer?.cancelled || !answer?.media) return
    // The picture and banner keep a still too (the first frame of a GIF or video), for the other apps and small places.
    const blob = answer.url && slot !== 'background' ? await pStill(answer.url, answer.media.type) : null
    const still = blob ? await pWebp(blob, slot === 'avatar' ? 256 : 960, slot === 'avatar' ? 256 : 320) : null
    prof.draft.look = { ...prof.draft.look, [slot]: answer.media }
    prof.draft.urls = { ...prof.draft.urls, [slot]: answer.url ?? null }
    if (slot === 'avatar' && still) prof.draft.picture = still
    if (slot === 'banner' && still) prof.draft.banner = still
  } catch (err) {
    prof.error = err.message || 'That file couldn’t be used. Try another.'
  } finally {
    prof.busy = null
    renderProfile()
  }
}

async function pSave() {
  const d = prof.draft
  const patch = { look: d.look }
  // The layout only when it changed: the saved one stays as it is otherwise.
  const canvas = d.canvas && { ...d.canvas, blocks: d.canvas.blocks.map((b) => {
    if (!b.build) return b
    const build = b.build.filter((part) => part.name.trim()).map((part) => ({ part: part.part, name: part.name.trim() }))
    return cClean({ ...b, build: build.length ? build : undefined })
  }) }
  if (canvas !== undefined && JSON.stringify(canvas) !== JSON.stringify(exo.profile.canvas ?? null)) patch.canvas = canvas
  if (d.name.trim() !== (exo.profile.name ?? '')) patch.name = d.name.trim()
  if (d.picture !== undefined) patch.picture = d.picture
  if (d.banner !== undefined) patch.banner = d.banner
  prof.busy = 'save'
  prof.error = null
  renderProfile()
  try {
    await app('account-edit', patch)
    prof.sheet = null
    prof.draft = null
  } catch (err) {
    prof.error = err.message || 'Your profile couldn’t be saved. Try again.'
  }
  prof.busy = null
  await readAccount()
}

function pClose() {
  if (prof.link && prof.link.step !== 'done') app('account-link-cancel').catch(() => {})
  Object.assign(prof, { sheet: null, draft: null, tab: 'look', selected: null, error: null, link: null, linkTyped: '', linking: false, code: null, renaming: null, setupName: '', recoveryTyped: '' })
  renderProfile()
}

document.addEventListener('click', async (e) => {
  const t = e.target.closest('[data-p]')
  if (!t || t.disabled || !t.closest('.exo-profile-room')) return
  const what = t.dataset.p
  if (what === 'customize') {
    const p = exo.profile
    Object.assign(prof, { sheet: 'customize', tab: 'look', selected: null, error: null, draft: { name: p.name ?? '', look: structuredClone(p.look ?? { v: 1 }), urls: { ...(p.media ?? {}) }, files: { ...(p.files ?? {}) }, ...(p.canvas ? { canvas: structuredClone(p.canvas) } : {}) } })
    renderProfile()
    focusWhenActive($('pName'))
  } else if (what === 'account' || what === 'setup' || what === 'recovery') {
    Object.assign(prof, { sheet: what, error: null })
    renderProfile()
    focusWhenActive($('pSetupName') ?? $('pRecovery'))
  } else if (what === 'link') {
    Object.assign(prof, { sheet: 'link', error: null, link: null })
    renderProfile()
    try { prof.link = await app('account-link-start') } catch (err) { prof.link = { role: 'new', step: 'failed', message: err.message } }
    renderProfile()
  } else if (what === 'tab') {
    prof.tab = t.dataset.value
    prof.selected = null
    // Layout works on the fine grid, with the banner, picture and name as blocks, as Exo Launcher's.
    if (prof.tab === 'layout') {
      const c = pCanvasOf(prof.draft)
      if (c.hero !== 'blocks' || c.grid !== 96) prof.draft.canvas = toFineGrid(withHeroBlocks(c))
    }
    renderProfile()
  } else if (what === 'undo' || what === 'redo') pStep(what === 'undo')
  else if (what === 'cancel' || what === 'close') pClose()
  else if (what === 'save') pSave()
  else if (what === 'set') pUpdate({ [t.dataset.key]: t.dataset.key === 'font' && t.dataset.value === 'exo' ? undefined : t.dataset.value })
  else if (what === 'starter') {
    const s = P_STARTERS[Number(t.dataset.value)]
    pUpdate({ accent: s.accent, effect: s.effect, frame: s.frame, name: s.style })
  } else if (what === 'pick') pPick(t.dataset.slot)
  else if (what === 'drop') {
    const slot = t.dataset.slot
    const look = { ...prof.draft.look }
    delete look[slot]
    prof.draft.look = look
    prof.draft.urls = { ...prof.draft.urls, [slot]: null }
    if (slot === 'avatar') prof.draft.picture = null
    if (slot === 'banner') prof.draft.banner = null
    renderProfile()
  } else if (what === 'show' || what === 'move') {
    // Within the look's whole list, so Exo Launcher's showcases keep their places.
    const shown = [...pShown(prof.draft.look)]
    const s = t.dataset.value
    if (what === 'show') pUpdate({ showcases: shown.includes(s) ? shown.filter((x) => x !== s) : [...shown, s] })
    else {
      const at = shown.indexOf(s), to = at + Number(t.dataset.by)
      if (at < 0 || to < 0 || to >= shown.length) return
      shown.splice(at, 1)
      shown.splice(to, 0, s)
      pUpdate({ showcases: shown })
    }
  } else if (what === 'recovery-on') {
    const answer = await pRun('recovery-on', () => app('account-recovery-on'), 'Recovery couldn’t be turned on. Try again.')
    if (answer?.code) { prof.code = answer.code; renderProfile() }
  } else if (what === 'code-done') pClose()
  else if (what === 'add-link') {
    prof.linking = !prof.linking
    if (!prof.linking) app('account-link-cancel').catch(() => {})
    prof.link = null
    prof.linkTyped = ''
    renderProfile()
    focusWhenActive($('pLinkTyped'))
  } else if (what === 'link-join') {
    prof.busy = 'link-join'
    renderProfile()
    try { prof.link = await app('account-link-join', { code: prof.linkTyped }) } catch (err) { prof.link = { role: 'existing', step: 'failed', message: err.message } }
    prof.busy = null
    renderProfile()
  } else if (what === 'link-confirm') {
    prof.link = { ...prof.link, step: 'working' }
    renderProfile()
    try { prof.link = await app('account-link-confirm') } catch (err) { prof.link = { ...prof.link, step: 'failed', message: err.message } }
    if (prof.link?.step === 'done') { prof.linking = false; if (prof.sheet === 'link') prof.sheet = null }
    await readAccount()
  } else if (what === 'link-cancel') {
    app('account-link-cancel').catch(() => {})
    prof.link = null
    prof.linking = false
    if (prof.sheet === 'link') prof.sheet = null
    renderProfile()
  } else if (what === 'rename') {
    const pc = exo.profile.pcs.find((x) => x.id === t.dataset.id)
    prof.renaming = prof.renaming?.id === pc.id ? null : { id: pc.id, name: pc.name }
    renderProfile()
    focusWhenActive($('pRename'))
  } else if (what === 'rename-cancel') { prof.renaming = null; renderProfile() }
  else if (what === 'rename-save') {
    const { id, name } = prof.renaming
    await pRun('rename', () => app('account-rename', { id, name: name.trim() }), 'The name couldn’t be saved. Try again.')
    if (!prof.error) { prof.renaming = null; renderProfile() }
  } else if (what === 'remove') {
    if (!confirmInPlace(t, 'Remove?')) return
    await pRun('remove-' + t.dataset.id, () => app('account-remove', { id: t.dataset.id }), 'The PC couldn’t be removed. Try again.')
  } else if (what === 'setup-create') {
    await pRun('setup', () => app('account-setup', { name: prof.setupName.trim() }), 'Your profile could not be set up. Try again.')
    if (!prof.error) pClose()
  } else if (what === 'restore') {
    if (!confirmInPlace(t, 'Remove your other PCs?')) return
    const answer = await pRun('restore', () => app('account-restore', { code: prof.recoveryTyped }), 'That code didn’t work. Check it and try again.')
    if (answer?.code) { prof.code = answer.code; prof.sheet = exo.profile ? 'account' : 'recovery'; renderProfile() }
  }
})

// Typing keeps what's typed; the step's button follows it. The name shows on the page as it's typed.
document.addEventListener('input', (e) => {
  const f = e.target
  if (!f.closest?.('.exo-profile-room')) return
  const primary = f.closest('.exo-profile-form, .exo-profile-sheet')?.querySelector('.is-primary')
  if (f.id === 'pName' && prof.draft) {
    prof.draft.name = f.value
    const v = pView()
    view.querySelector('.exo-profile-hero-name')?.replaceWith(Object.assign(document.createElement('template'), { innerHTML: pName(v.name || 'Exo profile', v.look.name, v.look.font, 'h1', 'exo-profile-hero-name') }).content)
    if (primary) primary.disabled = !f.value.trim()
  } else if (f.id === 'pAccent' && prof.draft) pUpdate({ accent: f.value.toLowerCase() })
  else if (f.id === 'pSetupName') { prof.setupName = f.value; if (primary) primary.disabled = !f.value.trim() }
  else if (f.id === 'pRecovery') { prof.recoveryTyped = f.value; if (primary) primary.disabled = f.value.replace(/[\s-]/g, '').length !== 26 }
  else if (f.id === 'pLinkTyped') { prof.linkTyped = f.value; if (primary) primary.disabled = !/^\d{8}$/.test(f.value.replace(/[\s-]/g, '')) }
  else if (f.id === 'pRename' && prof.renaming) { prof.renaming.name = f.value; if (primary) primary.disabled = !f.value.trim() }
})

// Enter presses a form's step; Escape closes the sheet.
document.addEventListener('keydown', (e) => {
  if (!view.querySelector('.exo-profile-room')) return
  if (e.key === 'Escape' && prof.sheet && !prof.busy) { e.preventDefault(); pClose() }
  else if (e.key === 'Enter' && e.target.closest?.('.exo-profile-form')) {
    e.preventDefault()
    const button = e.target.closest('.exo-profile-form').querySelector('.is-primary')
    if (button && !button.disabled) button.click()
  }
})

// Link a PC moved on, on the host (the other PC typed the digits, or it finished).
onHost((m) => {
  if (m.type !== 'profile-link') return
  prof.link = m.link
  if (route() === 'profile' && (prof.sheet === 'link' || prof.linking)) renderProfile()
})

// ---- Layout's controls (profile-canvas.js draws them): every change shows on the page at once and keeps with Save

function pLayoutBlock() {
  return pCanvasOf(prof.draft).blocks.find((b) => b.id === prof.selected) ?? null
}

function pLayoutChange(next) {
  prof.draft.canvas = next(pCanvasOf(prof.draft))
  renderProfile()
}

// A new block at the canvas's own scale (its sizes are given in 24's columns and rows), under every other.
function pLayoutAdd(block, stem, atTop = false) {
  const c = pCanvasOf(prof.draft)
  const id = newBlockId(c, stem)
  const f = cFine(c)
  prof.selected = id
  prof.more = false
  pLayoutChange((x) => ({ ...x, blocks: [...x.blocks, cClean({ ...block, id, x: block.x * f, w: block.w * f, h: block.h * f, y: atTop ? (block.y ?? 0) * f : nextRow(x) })] }))
}

// Windows' picker, as the look's media: kept by its hash and played from Cinema's host.
async function pLayoutPick() {
  prof.picking = true
  prof.error = null
  renderProfile()
  try {
    const answer = await app('account-media-pick')
    if (answer?.cancelled || !answer?.media || !answer.url) return null
    prof.draft.files = { ...prof.draft.files, [answer.media.sha]: answer.url }
    if (pIsVideo(answer.media.type)) {
      const probe = Object.assign(document.createElement('video'), { preload: 'metadata', muted: true })
      probe.onloadedmetadata = () => { if (Number.isFinite(probe.duration)) { prof.durations[answer.media.sha] = probe.duration * 1000; renderProfile() } }
      probe.src = answer.url
    }
    return answer.media
  } catch (err) {
    prof.error = err.message || 'That file couldn’t be used. Try another.'
    return null
  } finally {
    prof.picking = false
    renderProfile()
  }
}

C_CHOICES.effect = P_EFFECTS

document.addEventListener('click', async (e) => {
  const t = e.target.closest('[data-c]')
  if (!t || t.disabled || !t.closest('.exo-profile-sheet.is-customize') || !prof.draft || t.matches('input')) return
  const what = t.dataset.c
  const block = pLayoutBlock()
  const set = (values) => pCanvasPatch(block.id, values)
  if (what === 'choice') { await pChoose(t, block); return }
  if (what === 'add') {
    const id = t.dataset.value
    if (id === 'media') {
      const media = await pLayoutPick()
      if (media) pLayoutAdd({ kind: 'media', x: 0, w: 8, h: 8, media, surface: 'clear', radius: 18 }, 'art')
    } else if (id === 'text') pLayoutAdd({ kind: 'text', x: 0, w: 12, h: 4, text: '# Hello\nThis is my profile.', surface: 'glass' }, 'text')
    else if (id in C_HERO_ADD) pLayoutAdd({ kind: id, x: 0, y: 0, w: id === 'banner' ? 24 : id === 'avatar' ? 3 : 12, h: id === 'banner' ? 5 : id === 'avatar' ? 4 : 2, surface: 'clear' }, id, true)
    else pLayoutAdd({ kind: 'showcase', showcase: id, x: 0, w: C_COLUMNS, h: C_SHOWCASE_ROWS[id], surface: id === 'stats' ? 'glass' : 'clear' }, id)
  } else if (what === 'select') { prof.selected = t.dataset.id; prof.more = false; renderProfile() }
  else if (what === 'list') { prof.selected = null; renderProfile() }
  else if (what === 'more') { prof.more = !prof.more; renderProfile() }
  else if (!block) return
  else if (what === 'act') pBlockAction(block.id, t.dataset.value)
  else if (what === 'replace') { const media = await pLayoutPick(); if (media) set({ media }) }
  else if (what === 'part-remove') set({ build: pBuildOf(block).filter((b) => b.part !== t.dataset.part) })
  else if (what === 'gear-remove') {
    const gear = (block.gear ?? []).filter((_, i) => i !== Number(t.dataset.at))
    set({ gear })
  }
})

// A dropdown's list, from the control pressed (the stream and genre menus' own); what's picked goes on the block (or,
// for Effect, on the look).
async function pChoose(t, block) {
  const key = t.dataset.key
  const names = C_CHOICES[key]
  const label = await chooseFrom(t, Object.values(names), t.getAttribute('aria-label') ?? '')
  const value = label == null ? null : Object.keys(names).find((k) => names[k] === label)
  if (value == null) return
  if (key === 'effect') { pUpdate({ effect: value }); return }
  if (!block) return
  const set = (values) => pCanvasPatch(block.id, values)
  if (key === 'size') set({ size: value === 'm' ? undefined : value })
  else if (key === 'text-size') set({ size: value })
  else if (key === 'font') set({ font: value === 'exo' ? undefined : value })
  else if (key === 'surface') set({ surface: value })
  else if (key === 'frame') set({ frame: value === 'none' ? undefined : value })
  else if (key === 'blend') set({ blend: value === 'normal' ? undefined : value })
  else if (key === 'align') set({ align: value })
  else if (key === 'tracker' || key === 'tracker-more') {
    const next = [...(block.trackers ?? ['hours', 'top-game', 'this-year'])]
    const at = Number(t.dataset.at)
    if (value === 'none') next.splice(at, 1)
    else next[at] = value
    set({ trackers: next.filter((x, i) => next.indexOf(x) === i) })
  } else if (key === 'add-part') set({ build: C_PARTS.flatMap((p) => (p === value ? [{ part: value, name: '' }] : pBuildOf(block).filter((b) => b.part === p))) })
}

const pBuildOf = (block) => block.build ?? []

// A slider moves its block as it's dragged (only the block's place on the page is touched, so the slider keeps the
// pointer), and the page and panel are drawn again when it's let go; text shows in its block as it's typed.
document.addEventListener('input', (e) => {
  const f = e.target
  if (!prof.draft || !f.closest?.('.exo-profile-sheet.is-customize')) return
  const block = pLayoutBlock()
  if (!block) return
  if (f.id === 'cText') {
    pCanvasQuiet(block.id, { text: f.value || ' ' })
    return
  }
  if (f.dataset?.c === 'part') {
    const name = f.value.replace(/\p{Cc}/gu, '')
    pCanvasQuiet(block.id, { build: pBuildOf(block).map((b) => (b.part === f.dataset.part ? { part: b.part, name } : b)) })
    return
  }
  const what = f.dataset?.c
  if (!what || f.type !== 'range') return
  const v = Number(f.value)
  const media = block.media
  const values = what === 'radius' ? { radius: v }
    : what === 'opacity' ? { opacity: v === 100 ? undefined : v }
    : what === 'rot' ? { rot: Math.abs(v) < 2 ? undefined : v }
    : what === 'across' ? { media: cCleanMedia({ ...media, focus: [v, (media.focus ?? [50, 50])[1]] }) }
    : what === 'down' ? { media: cCleanMedia({ ...media, focus: [(media.focus ?? [50, 50])[0], v] }) }
    : what === 'zoom' ? { media: cCleanMedia({ ...media, zoom: v }) }
    : what === 'trim-from' ? { media: cCleanMedia({ ...media, trim: [v, (media.trim ?? [0, prof.durations[media.sha] ?? 0])[1]] }) }
    : what === 'trim-to' ? { media: cCleanMedia({ ...media, trim: [(media.trim ?? [0, 0])[0], v] }) }
    : null
  if (!values) return
  f.nextElementSibling.textContent = what.startsWith('trim') ? cSeconds(v) : v
  pCanvasQuiet(block.id, values)
})
document.addEventListener('change', (e) => {
  if (prof.draft && e.target.closest?.('.exo-profile-sheet.is-customize') && (e.target.type === 'range' || e.target.id === 'cText' || e.target.dataset?.c === 'part')) renderProfile()
})

// A block changed without drawing the panel again: the draft, and that block on the page.
function pCanvasQuiet(id, values) {
  const c = pCanvasOf(prof.draft)
  prof.draft.canvas = { ...c, blocks: c.blocks.map((b) => (b.id === id ? cClean({ ...b, ...values }) : b)) }
  const v = pView()
  const on = view.querySelector(`.exo-profile-canvas .exo-profile-block[data-block="${CSS.escape(id)}"]`)
  const canvas = on?.closest('.exo-profile-canvas')
  if (!canvas) return
  const hero = { look: v.look, name: v.name, banner: pSlot(v, 'banner'), bannerStill: v.banner, avatar: pSlot(v, 'avatar'), picture: v.picture }
  const fresh = Object.assign(document.createElement('template'), { innerHTML: cCanvas(prof.draft.canvas, { editing: true, selected: prof.selected, files: v.files, hero, showcase: (s, b) => pShowcase(s, b, v.files) }) }).content
  const block = fresh.querySelector(`[data-block="${CSS.escape(id)}"]`)
  if (block) on.replaceWith(block)
}
