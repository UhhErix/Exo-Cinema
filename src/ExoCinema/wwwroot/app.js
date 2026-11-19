// Exo Cinema's library: Home, Movies and Shows, a title's page, search, the Watchlist and History, all from the Jellyfin
// server this PC is signed in to; with none, every room is the Jellyfin sign-in. The host (Library/CinemaLibrary.cs,
// Jellyfin/JellyfinLibrary.cs) asks the server; this page asks the host through jf() and draws what comes back.

// The page's heartbeat, once a second: the host (PageHeartbeat.cs) reports the page as frozen when it stops for five seconds.
setInterval(() => { try { send({ cmd: 'heartbeat' }) } catch { } }, 1000)

let nextId = 1
const pending = new Map()
onHost((msg) => {
  if (msg.type !== 'reply') return
  const p = pending.get(msg.id)
  if (!p) return
  pending.delete(msg.id)
  msg.ok ? p.resolve(msg.data) : p.reject(new Error(msg.error))
})

function jf(op, args = {}) {
  const id = nextId++
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject })
    send({ cmd: 'jf', id, op, args })
  })
}

function app(op, args = {}) {
  const id = nextId++
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject })
    send({ cmd: 'app', id, op, args })
  })
}

const view = $('view')
// The Exo account and profile as the host last said (Profile, below).
const exo = { read: false, enabled: false, account: null, profile: null, providers: [], busy: null, error: null, email: null }

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c])

// Cache first: what was drawn last time draws at once, then the server's answer replaces it.
const cache = {
  get(key) { try { return JSON.parse(localStorage.getItem(`exo:lib:${key}`)) } catch { return null } },
  set(key, value) { try { localStorage.setItem(`exo:lib:${key}`, JSON.stringify(value)) } catch { } },
}

// What a Jellyfin sign-in left on this page long ago (its copies of Home and title pages, the server's address and
// the user name): gone. And once, at the first start of 1.5: the copies of Home, the rooms and title pages kept while
// Cinema drew other sources too, whose rows and ids 1.5 doesn't have.
try {
  for (const key of Object.keys(localStorage)) if (/^exo:[0-9a-f]{32}:|^exo:undefined:|^exo:last-(server|user)$/.test(key)) localStorage.removeItem(key)
  if (localStorage.getItem('exo:lib-kept') !== '1.5') {
    for (const key of Object.keys(localStorage)) if (key.startsWith('exo:lib:')) localStorage.removeItem(key)
    localStorage.setItem('exo:lib-kept', '1.5')
  }
} catch { }

// ---- Pictures: each through the host's picture cache (Library/ArtCache.cs), which fetches it from the server through
// Egress and keeps it on disk. A title the server has no picture for shows its name.

// With a width (the box's, in page pixels) the host makes the picture to fit it at this screen's density, in a few steps
// so the same picture isn't kept at many sizes (Library/ArtCache.cs).
const ART_STEPS = [96, 160, 200, 240, 320, 480, 640, 960, 1280, 1920]
const artStep = (w) => ART_STEPS.find((s) => s >= w * dpr()) ?? 0
const art = (url, w) => (url ? `https://art.cinema.exo/i?u=${encodeURIComponent(url)}${w && artStep(w) ? `&w=${artStep(w)}` : ''}` : '')
const poster = (item, w = 160) => art(item?.poster, w)
const backdrop = (item, w = 1920) => art(item?.background, w)
const logo = (item) => art(item?.logo, 400)

// An episode's still: its own, else its show's backdrop.
const still = (item, w = 240) => item?.thumbnail ? art(item.thumbnail, w) : backdrop(item, w)
// Pictures far from the screen (more than about three screens away) let go of their decoded image and keep their place;
// near again, they take it back from the disk cache at once. Movies and Shows grow as they scroll, and every cover ever
// passed stayed decoded: after twelve pages, 546 pictures held 74 megapixels.
// Each card is watched, not its picture: a picture inside a card the browser skips (content-visibility) is never
// reported again when the card comes back.
const artKeeper = new IntersectionObserver((entries) => {
  for (const { target: card, isIntersecting } of entries) {
    const img = card.querySelector('.art img')
    if (!img) continue
    if (isIntersecting) { if (img.dataset.away) { img.src = img.dataset.away; delete img.dataset.away } }
    else if (img.getAttribute('src') && img.complete) { img.dataset.away = img.getAttribute('src'); img.removeAttribute('src') }
  }
}, { rootMargin: '300% 100%' })
new MutationObserver((records) => {
  for (const r of records) for (const n of r.addedNodes) {
    if (n.nodeType !== 1) continue
    for (const card of n.matches?.('.grid .card, .shelf .card') ? [n] : n.querySelectorAll?.('.grid .card, .shelf .card') ?? []) artKeeper.observe(card)
  }
}).observe($('view'), { childList: true, subtree: true })

// A picture the server doesn't have after all steps aside: a logo for the name in
// words, a cover for the title's name on the card's own colour.
document.addEventListener('error', (e) => {
  const img = e.target
  if (img?.tagName !== 'IMG' || !img.src.startsWith('https://art.cinema.exo/')) return
  if (img.classList.contains('hero-logo')) { img.hidden = true; img.nextElementSibling?.removeAttribute('hidden') }
  else if (img.dataset.name) img.replaceWith(Object.assign(document.createElement('span'), { className: 'art-name', textContent: img.dataset.name }))
  else img.classList.add('is-missing')
}, true)

// ---- Words

const minutes = (m) => (m >= 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${m} min`)

// What's left of a title, from where the server says it was left.
const left = (p) => (p?.runtime && p?.ms ? `${minutes(Math.max(1, Math.round((p.runtime - p.ms) / 6e4)))} left` : '')

const code = (s, e) => (s != null && e != null ? `S${s}:E${e}` : '')

const progress = (p) => {
  const pct = p?.runtime ? (100 * p.ms) / p.runtime : 0
  return pct > 0 && pct < 100 ? `<span class="progress"><span style="width:${pct.toFixed(1)}%"></span></span>` : ''
}

const watchedMark = (on) => (on ? `<span class="watched exo-glass is-over-art" aria-label="Watched">${icon('check')}</span>` : '')

const year = (item) => item?.releaseInfo ?? item?.year ?? ''

const released = (v) => !v.released || new Date(v.released) <= new Date()

const shortDay = (ms) => new Date(ms).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: new Date(ms).getFullYear() === new Date().getFullYear() ? undefined : 'numeric' })

// An episode's name comes as "Show · S1:E2 Episode": its show, and the episode's own name.
function titleParts(e) {
  const m = /^(.*) · S\d+:E\d+ ?(.*)$/.exec(e.title ?? '')
  return m ? { show: m[1], episode: m[2] } : { show: e.title ?? '', episode: '' }
}

// ---- Cards

const playAttrs = (id, type, show) => `data-play="${esc(id)}" data-type="${esc(type)}"${show ? ` data-show="${esc(show)}"` : ''}`
const openAttr = (type, id, season) => `data-open="${esc([type, id, season].filter((x) => x != null && x !== '').join('/'))}"`

// A title from a catalog or a search: its poster, its name and its year.
// In a grid a card is as wide as its column (150 px and up, about 180 at 1080p): its poster is made for 200.
const gridCard = (item) => posterCard(item, 200)

function posterCard(item, w = 160) {
  const src = poster(item, w)
  return `<button class="card poster" ${openAttr(item.type, item.id)}>
    <span class="art">${src ? `<img loading="lazy" src="${esc(src)}" alt="" data-name="${esc(item.name)}">` : `<span class="art-name">${esc(item.name)}</span>`}</span>
    <span class="card-title">${esc(item.name)}</span>
    <span class="card-sub">${esc(year(item))}</span>
  </button>`
}

// One of the server's in Continue watching, Next up, the Watchlist or History: a film or show by its poster, or an
// episode by its still, its show's name over the episode's. One kept from before 1.5 has no id: its name (awayCard).
function entryCard(e, shape) {
  if (!e.id) return awayCard(e, shape)
  const { show, episode } = titleParts(e)
  const isEpisode = e.season != null
  const open = isEpisode ? openAttr('series', e.showId, e.season) : openAttr(e.type ?? 'any', e.id)
  if (shape === 'poster') {
    return `<button class="card poster" ${open}>
      <span class="art">${poster(e) ? `<img loading="lazy" src="${esc(poster(e))}" alt="" data-name="${esc(show)}">` : `<span class="art-name">${esc(show)}</span>`}${progress(e.position)}</span>
      <span class="card-title">${esc(show)}</span>
      <span class="card-sub">${esc(isEpisode ? code(e.season, e.episode) : '')}</span>
    </button>`
  }
  const sub = isEpisode ? [code(e.season, e.episode), episode].filter(Boolean).join(' · ') : left(e.position)
  return `<button class="card wide" ${open}>
    <span class="art"><img loading="lazy" src="${esc(isEpisode ? still(e, 320) : backdrop(e, 320))}" alt="" data-name="${esc(show)}">${progress(e.position)}</span>
    <span class="card-title">${esc(show)}</span>
    <span class="card-sub">${esc(sub)}</span>
  </button>`
}

// Airing soon (the server's upcoming episodes): the show by its still, its episode as S1 E2 and when it airs, from the
// host's own words ("Today", "Fri", local time). Pressing it opens the show's title page; it never plays.
function airingCard(e) {
  // Only what the server gave: a season and episode it doesn't know are left out, and so is the line when there's nothing.
  const number = [e.season != null ? `S${e.season}` : '', e.episode != null ? `E${e.episode}` : ''].filter(Boolean).join(' ')
  const sub = [number, [e.airDay, e.airTime].filter(Boolean).join(' ')].filter(Boolean).join(' · ')
  return `<button class="card wide" ${openAttr('series', e.showId)}>
    <span class="art"><img loading="lazy" src="${esc(still(e, 320))}" alt="" data-name="${esc(e.title)}"></span>
    <span class="card-title">${esc(e.title)}</span>
    <span class="card-sub">${esc(sub)}</span>
  </button>`
}
const placeholders = (n) => '<span class="card poster is-placeholder" aria-hidden="true"><span class="art exo-skeleton exo-shine"></span><span class="card-title">&nbsp;</span><span class="card-sub"></span></span>'.repeat(n)

// A page on its way (DESIGN.md › Waits): the cards it will be, shining, never a spinner in the middle of the page. A grid
// for the rooms and a search, the hero's shape for a title.
const waitGrid = () => `<section class="grid-page" aria-busy="true"><div class="grid">${placeholders(18)}</div></section>`
const waitTitle = () => `<section class="wait-title" aria-busy="true"><span class="wait-hero exo-skeleton exo-shine"></span></section>`

// A shelf, as Exo Launcher's Store rows: its name, and arrows that page the row; they hide when the row fits.
const shelf = (title, cards) => cards ? `<section class="shelf">
  <div class="shelf-head"><h2>${esc(title)}</h2><span class="shelf-arrows">
    <button type="button" class="exo-glass set-btn" data-page="-1" aria-label="Back in ${esc(title)}" disabled>${icon('chevron-left')}</button>
    <button type="button" class="exo-glass set-btn" data-page="1" aria-label="More ${esc(title)}">${icon('chevron-right')}</button></span></div>
  <div class="row">${cards}</div></section>` : ''

// An arrow is off at its end of the row; they change only when an edge is reached or left.
function drawArrows(row) {
  const head = row.previousElementSibling
  if (!head?.classList.contains('shelf-head')) return
  const [back, more] = head.querySelectorAll('[data-page]')
  back.disabled = row.scrollLeft < 4
  more.disabled = row.scrollLeft + row.clientWidth >= row.scrollWidth - 4
}
const drawAllArrows = () => document.querySelectorAll('#view .row').forEach(drawArrows)
document.addEventListener('scroll', (e) => { if (e.target.classList?.contains('row')) drawArrows(e.target) }, true)
new MutationObserver(() => requestAnimationFrame(drawAllArrows)).observe(view, { childList: true, subtree: true })
addEventListener('resize', drawAllArrows)

// A page at a time: nine tenths of the row, so the last card seen stays in sight.
document.addEventListener('click', (e) => {
  const arrow = e.target.closest('[data-page]')
  const row = arrow?.closest('.shelf')?.querySelector('.row')
  if (!row) return
  row.scrollBy({ left: Number(arrow.dataset.page) * row.clientWidth * 0.9, behavior: document.documentElement.dataset.exoMotion === 'reduce' ? 'auto' : 'smooth' })
})

// ---- Rooms (the Bar)

// A room's glyph, as Exo's room tabs have: line at rest, solid for the current one where Tabler draws one.
const ROOM_GLYPH = { home: ['home', 'home-filled'], movies: ['movie', 'movie'], shows: ['device-tv', 'device-tv-filled'], watchlist: ['bookmark', 'bookmark-filled'], history: ['history', 'history'], profile: ['user-circle', 'user-circle'] }

// Home, Movies and Shows, then the Watchlist and History once they hold something. Signed out of Jellyfin, the rooms
// are the sign-in, so only the first three are named.
function drawRooms() {
  const current = route().split('/')[0] || 'home'
  const tabs = [
    ['home', 'Home'], ['movies', 'Movies'], ['shows', 'Shows'],
    ...jellyfin.signedIn && watchData?.watchlist?.length ? [['watchlist', 'Watchlist']] : [],
    ...jellyfin.signedIn && watchData?.history?.length ? [['history', 'History']] : [],
  ]
  // The profile is the picture at the top right, lit while it's open, never a tab beside the rooms.
  const html = tabs.map(([id, name]) => {
    const on = current === id
    const [line, solid] = ROOM_GLYPH[id]
    return `<a class="tab${on ? ' is-on' : ''}" href="#/${id}"${on ? ' aria-current="page"' : ''}>${icon(on ? solid : line)}<span>${esc(name)}</span></a>`
  }).join('')
  if ($('rooms').innerHTML !== html) $('rooms').innerHTML = html
}

// ---- Home

const rowKey = (r) => `${r.type}/${r.id}`
// A Jellyfin library whole is a list for Movies and Shows, not a row on Home (its newest is).
const onHome = (r) => !r.room
let homeRedraw = null

// The account banner (Exo Launcher's AccountBanner): shown while this PC is signed out, slim, on Home. Create account opens the
// account's form in Settings; × hides it for a week (a per-PC convenience, kept in this PC's browser storage).
const BANNER_HIDE_MS = 7 * 24 * 3600 * 1000
function accountBannerDue() {
  if (!exo.enabled || !acct.read || acct.signedIn) return false
  try { return Date.now() - Number(localStorage.getItem('exo:account-banner-hidden') ?? 0) > BANNER_HIDE_MS } catch { return true }
}
const accountBannerHtml = () => accountBannerDue()
  ? '<section class="account-banner" aria-label="Create your Exo account"><p>Create your Exo account to keep your settings on every PC.</p><div class="account-banner-actions"><button type="button" class="set-btn" data-banner="create">Create account</button><button type="button" class="set-btn" data-banner="hide" aria-label="Hide for a week">×</button></div></section>'
  : ''
document.addEventListener('click', (e) => {
  const button = e.target.closest?.('[data-banner]')
  if (!button) return
  if (button.dataset.banner === 'hide') {
    try { localStorage.setItem('exo:account-banner-hidden', String(Date.now())) } catch { }
    return homeRedraw?.()
  }
  // Create account: Settings opens on Account, straight into the create form.
  if (!settingsOpen()) $('avatar').click()
  document.querySelector('[data-settings-page=account]')?.click()
  acct.screen = 'create'
  acctDraw()
})
window.addEventListener('exo:account', () => homeRedraw?.())

// Home draws in parts: what was there last time at once, then Continue watching and the Watchlist, then each library's
// newest on its own (a slow one never holds up the others). Only the part that changed is touched: pictures already on
// screen stay put, so nothing flashes.
async function renderHome() {
  drawRooms()
  if (!jellyfin.signedIn) return renderSignIn()
  view.querySelector('.signin-room')?.remove()
  const home = { rows: cache.get('rows') ?? [], items: {} }
  for (const r of home.rows) home.items[rowKey(r)] = cache.get(`row:${rowKey(r)}`) ?? undefined
  if (!view.querySelector('.home')) view.innerHTML = '<div class="home"><div class="home-banner"></div><div class="home-hero"></div><div class="shelves"></div></div>'
  const draw = () => {
    if (route() !== 'home') return
    const root = view.querySelector('.home')
    if (!root) return
    patch(root.querySelector('.home-banner'), accountBannerDue() ? 'due' : '', () => accountBannerHtml())
    const going = watchData?.continue ?? []
    const listed = home.rows.filter(onHome)
    const first = listed.map((r) => home.items[rowKey(r)]).find((l) => l?.length)?.[0]
    const hero = going[0] ? { entry: going[0] } : first ? { item: first } : null
    patch(root.querySelector('.home-hero'), hero ? (hero.entry ? `e:${hero.entry.pid}:${hero.entry.position?.ms ?? ''}` : `i:${hero.item.id}`) : '', () => (hero ? homeHero(hero) : ''))
    const rows = [
      { key: 'continue', title: 'Continue watching', items: watchData?.continue, card: (e) => entryCard(e, 'wide'), sig: (e) => `${e.pid}:${e.position?.ms ?? ''}`, mine: true },
      { key: 'next-up', title: 'Next up', items: watchData?.nextUp, card: (e) => entryCard(e, 'wide'), sig: (e) => e.pid, mine: true },
      { key: 'airing', title: 'Airing soon', items: watchData?.airing, card: airingCard, sig: (e) => `${e.pid}:${e.airDay}:${e.airTime ?? ''}`, mine: true },
      { key: 'watchlist', title: 'Watchlist', items: watchData?.watchlist?.slice(0, 30), card: (e) => entryCard(e, 'poster'), sig: (e) => e.pid, mine: true },
      ...listed.map((r) => ({ key: rowKey(r), title: r.title, items: home.items[rowKey(r)], card: (i) => posterCard(i), sig: (i) => i.id })),
    ]
    const shelves = root.querySelector('.shelves')
    for (const r of rows) {
      let el = shelves.querySelector(`:scope > [data-row="${CSS.escape(r.key)}"]`)
      if (!el) { el = document.createElement('div'); el.dataset.row = r.key; shelves.append(el) }
      // A row still on its way keeps its place with blank cards; an empty one goes. The watch rows show once read.
      const sig = r.items ? r.items.map(r.sig).join() : 'loading'
      patch(el, sig, () => r.items ? shelf(r.title, r.items.map(r.card).join('')) : r.mine ? '' : shelf(r.title, placeholders(8)))
    }
    // In the server's order, after the watch rows. Moved only where it's out of place: a row moved is taken out and put
    // back, which starts its cards' fade-ins again, and every row moved on every list that came in made Home flicker.
    let before = null
    for (const r of rows) {
      const el = shelves.querySelector(`:scope > [data-row="${CSS.escape(r.key)}"]`)
      const at = before ? before.nextElementSibling : shelves.firstElementChild
      if (el !== at) shelves.insertBefore(el, at)
      before = el
    }
    for (const el of shelves.querySelectorAll(':scope > [data-row]')) if (!rows.some((r) => r.key === el.dataset.row)) el.remove()
  }
  homeRedraw = draw
  draw()
  readWatch()
  let rows
  try {
    rows = await jf('rows')
  } catch (e) {
    if (!home.rows.length && route() === 'home') view.innerHTML = problem(e.message, 'home')
    return
  }
  home.rows = rows
  if (!rows.length && !watchData?.continue?.length) { if (route() === 'home') view.innerHTML = '<p class="empty-words">Nothing on your server yet</p>'; return }
  for (const r of rows) home.items[rowKey(r)] ??= cache.get(`row:${rowKey(r)}`) ?? undefined
  cache.set('rows', rows)
  draw()
  await Promise.all(rows.filter(onHome).map(async (r) => {
    try {
      const items = (await jf('catalog', { type: r.type, id: r.id })).slice(0, 40)
      home.items[rowKey(r)] = items
      cache.set(`row:${rowKey(r)}`, items.map(({ description, ...card }) => card))
    } catch {
      home.items[rowKey(r)] ??= []
    }
    draw()
  }))
}

// Replaces an element's content only when what it shows changed.
function patch(el, sig, html) {
  if (el.dataset.sig === sig) return
  el.dataset.sig = sig
  el.innerHTML = html()
}

// A backdrop arrives in two steps: a small one at once (about 25 KB), then the full one once it has loaded, so a slow
// connection shows the picture in a second instead of after the whole image.
function heroArt(item) {
  const small = backdrop(item, 480)
  if (!small) return ''
  const full = backdrop(item, Math.min(1920, innerWidth))
  return `<img class="hero-art" src="${esc(small)}" data-full="${esc(full)}" alt="" onload="upgradeArt(this)">`
}

function upgradeArt(img) {
  const full = img.dataset.full
  if (!full || img.src === full) return
  delete img.dataset.full
  const hi = new Image()
  hi.onload = () => { img.src = full }
  hi.src = full
}

// The title's logo, or its name in words where it has none.
const logoOrName = (src, name) => src
  ? `<img class="hero-logo" src="${esc(src)}" alt="${esc(name)}"><h1 class="hero-title" hidden>${esc(name)}</h1>`
  : `<h1 class="hero-title">${esc(name)}</h1>`

const playButton = (id, type, show, words) => `<button class="primary is-primary" ${playAttrs(id, type, show)}>${icon('player-play-filled')}${esc(words)}</button>`
// Choose version: the same title, its versions on the server listed first to pick one (Exo picks the best otherwise).
const chooseButton = (id, type, show) => `<button class="secondary exo-glass is-over-art icon-only" ${playAttrs(id, type, show)} data-choose aria-label="Choose version" aria-haspopup="listbox">${icon('chevron-down')}</button>`

// Home's top: what you're in the middle of, else the first title of the first row.
function homeHero({ entry, item }) {
  if (entry) {
    const { show, episode } = titleParts(entry)
    const isEpisode = entry.season != null
    const title = { id: entry.showId ?? entry.id, background: entry.background, logo: entry.logo }
    const line = [isEpisode ? [code(entry.season, entry.episode), episode].filter(Boolean).join(' · ') : '', left(entry.position)].filter(Boolean).join(' · ')
    return `<section class="hero">
      ${heroArt(title)}
      <div class="hero-body">
        ${logoOrName(logo(title), show)}
        <p class="hero-meta">${esc(line)}</p>
        <div class="actions">
          ${playButton(entry.id, isEpisode ? 'series' : 'movie', entry.showId, entry.position?.ms > 0 ? 'Resume' : 'Play')}
          <button class="secondary exo-glass is-over-art" ${isEpisode ? openAttr('series', entry.showId, entry.season) : openAttr('movie', entry.id)}>Details</button>
        </div>
      </div>
    </section>`
  }
  const line = [esc(year(item)), esc(item.runtime), item.imdbRating ? `<span class="rating">${icon('star-filled')}${esc(item.imdbRating)}</span>` : ''].filter(Boolean).join(' · ')
  return `<section class="hero">
    ${heroArt(item)}
    <div class="hero-body">
      ${logoOrName(logo(item), item.name)}
      <p class="hero-meta">${line}</p>
      <div class="actions">
        ${item.type === 'movie' ? playButton(item.id, 'movie', '', 'Play') : ''}
        <button class="${item.type === 'movie' ? 'secondary exo-glass is-over-art' : 'primary is-primary'}" ${openAttr(item.type, item.id)}>Details</button>
      </div>
    </div>
  </section>`
}

// ---- Movies and Shows: one of the server's libraries at a time, as a grid that grows as you scroll.
const ROOM_TYPES = { movies: ['movie'], shows: ['series'] }
const browse = { movies: { at: 0 }, shows: { at: 0 } }
let browsing = 0

async function renderBrowse(room) {
  drawRooms()
  if (!jellyfin.signedIn) return renderSignIn()
  let rows = cache.get('rows')
  if (!rows?.length) {
    view.innerHTML = waitGrid()
    try { rows = await jf('rows'); cache.set('rows', rows) } catch (e) { if (route() === room) view.innerHTML = problem(e.message, room); return }
    if (route() !== room) return
  }
  const catalogs = rows.filter((r) => ROOM_TYPES[room].includes(r.type))
  const pick = browse[room]
  const cat = catalogs[pick.at] ?? catalogs[0]
  if (!cat) {
    view.innerHTML = '<p class="empty-words">Nothing on your server yet</p>'
    return
  }
  // One library needs no list of libraries over it.
  view.innerHTML = `<section class="grid-page browse">
    ${catalogs.length > 1 ? `<div class="browse-head">
      <div class="exo-glass seasons" role="tablist" aria-label="Libraries">${catalogs.map((c, i) =>
        `<button type="button" role="tab" class="tab${c === cat ? ' is-on' : ''}" aria-selected="${c === cat}" data-catalog="${i}" data-room="${room}">${esc(c.title)}</button>`).join('')}</div>
    </div>` : ''}
    <div class="grid"></div>
    <div class="grid-more"><div class="spinner"></div></div>
  </section>`
  loadBrowse(room, cat)
}

// The library's pages, one after another as the end of the grid comes near: last time's first page at once, then the
// server's. A library that pages no further (the same titles again, or none) ends there.
function loadBrowse(room, cat) {
  const mine = ++browsing
  const grid = view.querySelector('.browse .grid')
  const more = view.querySelector('.browse .grid-more')
  const key = `browse:${rowKey(cat)}`
  const seen = new Set()
  let done = false, busy = false, count = 0
  const add = (items, fresh) => {
    if (fresh) { grid.innerHTML = ''; seen.clear(); count = 0 }
    const news = items.filter((i) => !seen.has(i.id) && seen.add(i.id))
    grid.insertAdjacentHTML('beforeend', news.map(gridCard).join(''))
    count += news.length
    return news.length
  }
  const cached = cache.get(key)
  if (cached) add(cached, true)
  let first = true
  const next = async () => {
    if (busy || done || mine !== browsing) return
    busy = true
    try {
      const page = await jf('catalog', { type: cat.type, id: cat.id, skip: first ? undefined : count })
      if (mine !== browsing) return
      if (first) cache.set(key, page.map(({ description, ...card }) => card))
      if (!add(page, first)) done = true
      first = false
    } catch (e) {
      done = true
      if (first && !cached && mine === browsing) grid.outerHTML = problem(e.message, room)
    } finally {
      busy = false
    }
    if (mine !== browsing) return
    more.hidden = done
    // Watched afresh after each page: observing again reports where the end is now, so a page that left it in reach
    // asks for the next (the cards' own sizes come a frame later, under content-visibility, so measuring here missed).
    if (!done) { watcher.unobserve(more); watcher.observe(more) }
  }
  const watcher = new IntersectionObserver((seen) => {
    if (mine !== browsing) { watcher.disconnect(); return }
    if (seen.some((s) => s.isIntersecting)) next()
  }, { rootMargin: '800px' })
  watcher.observe(more)
  next()
}

// ---- A title's page

async function renderTitle(type, id, season) {
  drawRooms()
  if (!jellyfin.signedIn) return renderSignIn()
  const here = route()
  const cached = cache.get(`meta:${id}`)
  if (cached) drawTitle(cached, season)
  else view.innerHTML = waitTitle()
  try {
    const meta = await jf('meta', { type: type === 'any' ? undefined : type, id })
    // A long show's episodes are kept without their words, so the copy stays small.
    cache.set(`meta:${id}`, meta.videos?.length > 150 ? { ...meta, videos: meta.videos.map(({ overview, ...v }) => v) } : meta)
    if (route() === here) drawTitle(meta, season)
  } catch (e) {
    if (!cached && route() === here) view.innerHTML = problem(e.message, here)
  }
}

// The episode to play: the one under way, else the one after the last watched, else the first.
function nextToWatch(videos) {
  const real = videos.filter((v) => v.season > 0)
  const list = real.length ? real : videos
  const underWay = list.filter((v) => v.position?.ms > 0).sort((a, b) => (b.position.at ?? 0) - (a.position.at ?? 0))[0]
  let last = -1
  list.forEach((v, i) => { if (v.watchedAt && (last < 0 || v.watchedAt > list[last].watchedAt)) last = i })
  if (underWay && (last < 0 || (underWay.position.at ?? 0) >= list[last].watchedAt)) return underWay
  if (last >= 0) return list.slice(last + 1).find((v) => !v.watchedAt && released(v)) ?? list[0]
  return list[0] ?? null
}

function drawTitle(meta, seasonParam) {
  const videos = meta.videos ?? []
  const isShow = meta.type !== 'movie' && videos.length > 0
  const seasons = [...new Set(videos.map((v) => v.season))].sort((a, b) => (a === 0) - (b === 0) || a - b)
  const upNext = isShow ? nextToWatch(videos) : null
  const season = seasons.includes(Number(seasonParam)) ? Number(seasonParam) : upNext?.season ?? seasons.find((s) => s > 0) ?? seasons[0]
  const episodes = videos.filter((v) => v.season === season)
  const resumable = !isShow && meta.position?.ms > 0
  const seasonCount = seasons.filter((s) => s > 0).length
  const line = [
    esc(year(meta)),
    isShow ? (seasonCount ? `${seasonCount} season${seasonCount === 1 ? '' : 's'}` : '') : esc(meta.runtime),
    meta.imdbRating ? `<span class="rating">${icon('star-filled')}${esc(meta.imdbRating)}</span>` : '',
  ].filter(Boolean).join(' · ')
  const playWords = isShow
    ? (upNext ? `${upNext.position?.ms > 0 ? 'Resume' : 'Play'} ${code(upNext.season, upNext.episode)}` : '')
    : resumable ? 'Resume' : 'Play'
  const saved = !!meta.saved
  const seen = !!meta.watchedAt
  view.innerHTML = `<article class="title-page">
    <section class="hero is-title">
      ${heroArt(meta)}
      <div class="hero-body">
        ${logoOrName(logo(meta), meta.name)}
        <p class="hero-meta">${line}</p>
        ${meta.genres?.length ? `<p class="hero-genres">${esc(meta.genres.slice(0, 4).join(' · '))}</p>` : ''}
        ${watchedLine(meta, videos) ? `<p class="hero-genres">${esc(watchedLine(meta, videos))}</p>` : ''}
        <div class="actions">
          ${isShow ? (upNext ? playButton(upNext.id, meta.type, meta.id, playWords) + chooseButton(upNext.id, meta.type, meta.id) : '') : playButton(meta.id, meta.type, '', playWords) + chooseButton(meta.id, meta.type, '')}
          ${resumable ? `<button class="secondary exo-glass is-over-art" ${playAttrs(meta.id, meta.type)} data-start>From the start</button>` : ''}
          <button class="secondary exo-glass is-over-art${seen ? ' is-on' : ''}" data-played="${esc(meta.id)}" aria-pressed="${seen}">${icon('check')}Watched</button>
          <button class="secondary exo-glass is-over-art${saved ? ' is-on' : ''}" data-watchlist="${esc(meta.id)}" aria-pressed="${saved}">${icon(saved ? 'bookmark-filled' : 'bookmark')}Watchlist</button>
        </div>
        ${meta.description ? `<p class="overview">${esc(meta.description)}</p>` : ''}
      </div>
    </section>
    ${seasons.length ? `<section class="episodes">
      <div class="exo-glass seasons" role="tablist">${seasons.map((s) =>
        `<a role="tab" class="tab ${s === season ? 'is-on' : ''}" href="#/title/${esc(meta.type)}/${esc(encodeURIComponent(meta.id))}/${s}">${s === 0 ? 'Specials' : `Season ${s}`}</a>`).join('')}</div>
      <ol class="episode-list">${episodes.map((e) => episodeRow(e, meta)).join('')}</ol>
    </section>` : ''}
  </article>`
}

function episodeRow(e, meta) {
  const src = still({ ...e, showId: meta.id })
  const out = released(e)
  // Where it was left, else its length (the show's), else when it comes out.
  const line = out ? left(e.position) || meta.runtime || '' : `Out ${shortDay(new Date(e.released).getTime())}`
  // An episode is marked on the server, so every Jellyfin app sees it.
  const mark = `<button type="button" class="secondary exo-glass icon-only episode-mark${e.watchedAt ? ' is-on' : ''}" data-played="${esc(e.id)}" aria-pressed="${!!e.watchedAt}" aria-label="Watched">${icon('check')}</button>`
  return `<li>${mark}<button class="episode" ${playAttrs(e.id, meta.type, meta.id)}${out ? '' : ' disabled'}>
    <span class="art">${src ? `<img loading="lazy" src="${esc(src)}" alt="">` : ''}${progress(e.position)}${watchedMark(e.watchedAt)}</span>
    <span class="episode-body">
      <span class="episode-title">${esc(e.episode != null ? `${e.episode}. ` : '')}${esc(e.name || `Episode ${e.episode}`)}</span>
      <span class="episode-meta">${esc(line)}</span>
      ${e.overview ? `<span class="episode-overview">${esc(e.overview)}</span>` : ''}
    </span>
  </button></li>`
}

// The day, where one is known (the server can say played without one: 1).
const onDay = (ms) => (ms > 1 ? ` · ${shortDay(ms)}` : '')

// When it was watched: a film's last time, or a show's last episode.
function watchedLine(meta, videos) {
  if (videos.length) {
    const last = videos.filter((v) => v.watchedAt).sort((a, b) => b.watchedAt - a.watchedAt)[0]
    return last ? `Last watched ${code(last.season, last.episode)}${onDay(last.watchedAt)}` : ''
  }
  return meta.watchedAt ? `Watched${onDay(meta.watchedAt)}` : ''
}

let searchTimer = 0
async function renderSearch(term) {
  drawRooms()
  if (!jellyfin.signedIn) return renderSignIn()
  if (!term) { view.innerHTML = ''; return }
  view.innerHTML = waitGrid()
  try {
    const items = await jf('search', { term })
    if (route() !== `search/${encodeURIComponent(term)}` || !items) return
    view.innerHTML = items.length
      ? `<section class="grid-page"><div class="grid">${items.map(gridCard).join('')}</div></section>`
      : `<p class="empty-words">No results for “${esc(term)}”</p>`
  } catch (e) {
    view.innerHTML = problem(e.message, `search/${encodeURIComponent(term)}`)
  }
}

const problem = (message, again) =>
  `<div class="problem"><p>${esc(message)}</p><a class="secondary exo-glass" href="#/${again}" data-retry>Try again</a></div>`

// ---- Continue watching, Next up, Airing soon, the Watchlist and History (the host's Library/CinemaLibrary.cs): the
// server's own, read from it each time. What Exo Watch kept before 1.5 is in the Watchlist and History too, by its name.
// Read with Home, after playing, and as either room opens; kept for the next start.
let watchData = cache.get('watch')
let watchRead = -Infinity
// The first read done (read or not), for the opening to wait on (firstScreenReady).
let watchSettled
const watchFirst = new Promise((r) => { watchSettled = r })

let watchLateTries = 0
async function readWatch(force = false) {
  if (!force && performance.now() - watchRead < 30_000) return
  watchRead = performance.now()
  try {
    watchData = await jf('watch')
  } catch (err) {
    // A room still waiting on it says why, with Try again, instead of turning on.
    const page = route().split('/')[0]
    if (!watchData && (page === 'history' || page === 'watchlist')) view.innerHTML = problem(err.message, page)
    watchRead = -Infinity
    watchSettled()
    return
  }
  // Jellyfin's rooms that were still on their way are read again shortly, so they fill in when they arrive (three tries).
  if (watchData?.late) { if (watchLateTries < 3) setTimeout(() => readWatch(true), 3000 * ++watchLateTries) }
  else watchLateTries = 0
  cache.set('watch', watchData)
  drawRooms()
  const page = route().split('/')[0]
  if (page === 'history' || page === 'watchlist') renderWatch(page)
  else if (page === 'home') homeRedraw?.()
  watchSettled()
}

function renderWatch(page) {
  drawRooms()
  if (!jellyfin.signedIn) return renderSignIn()
  if (!watchData) {
    view.innerHTML = waitGrid()
    readWatch(true)
    return
  }
  if (page === 'watchlist') {
    view.innerHTML = `<section class="grid-page"><div class="grid">${watchData.watchlist.map((e) => entryCard(e, 'poster')).join('')}</div></section>`
    return
  }
  // By day, the newest first.
  const days = []
  for (const e of watchData.history) {
    const day = new Date(e.at).toDateString()
    if (days.at(-1)?.day !== day) days.push({ day, at: e.at, entries: [] })
    days.at(-1).entries.push(e)
  }
  view.innerHTML = `<section class="grid-page history">${days.map((d) => `<h2 class="day-head">${esc(dayName(d.at))}</h2>
    <div class="grid is-wide">${d.entries.map((e) => entryCard(e, 'wide')).join('')}</div>`).join('')}</section>`
}

// One kept from before 1.5, when Cinema played from other sources: its name, as it was kept. Pressing it looks the
// name up on the server.
function awayCard(e, shape) {
  const { show } = titleParts(e)
  const episode = / · (S\d+:E\d+)/.exec(e.title ?? '')?.[1] ?? ''
  return `<button class="card ${shape} is-away" data-find="${esc(show)}">
    <span class="art"><span class="art-name">${esc(show)}</span></span>
    <span class="card-title">${esc(show)}</span>
    <span class="card-sub">${esc(episode)}</span>
  </button>`
}
function dayName(ms) {
  const d = new Date(ms), today = new Date()
  const days = Math.round((new Date(today.toDateString()) - new Date(d.toDateString())) / 864e5)
  if (days === 0) return 'Today'
  if (days === 1) return 'Yesterday'
  return d.toLocaleDateString(undefined, { weekday: days < 7 ? 'long' : 'short', day: 'numeric', month: 'long', year: d.getFullYear() === today.getFullYear() ? undefined : 'numeric' })
}

// A toggle on a title's page, drawn pressed or not at once; its copy of the page keeps the change.
function setPressed(b, on, glyph) {
  b.classList.toggle('is-on', on)
  b.setAttribute('aria-pressed', on)
  if (glyph) b.querySelector('svg')?.replaceWith(document.createRange().createContextualFragment(icon(on ? `${glyph}-filled` : glyph)))
}

function keepOnPage(id, change) {
  const meta = cache.get(`meta:${id}`)
  if (meta) cache.set(`meta:${id}`, { ...meta, ...change })
}

// ---- Routing

const route = () => location.hash.replace(/^#\/?/, '') || 'home'

function render(keepSettings = false) {
  // A room chosen from the Bar takes the place of the one Settings floats over.
  if (keepSettings !== true) closeSettings()
  const [page, a, b, c] = route().split('/')
  if (page !== 'home') { view.querySelector('.home')?.remove(); window.scrollTo(0, 0) }
  // The profile's room is kept while another room is shown, so it's back at once (profile.js › pPark).
  if (page !== 'profile') pPark()
  browsing++
  switch (page) {
    case 'movies':
    case 'shows': return renderBrowse(page)
    case 'title': return renderTitle(a, decodeURIComponent(b ?? ''), c)
    case 'search': return renderSearch(decodeURIComponent(a ?? ''))
    case 'history':
    case 'watchlist':
      readWatch()
      return renderWatch(page)
    case 'profile':
      drawRooms()
      pUnpark()
      pEnter()
      return renderProfile()
    // Settings was a page of its own up to 1.4: its old address opens the sheet over Home.
    case 'settings':
      history.replaceState(null, '', '#/home')
      renderHome()
      return openSettings()
    // Home, and the libraries and items of the Jellyfin days before 1.5.
    default:
      if (page !== 'home') history.replaceState(null, '', '#/home')
      return renderHome()
  }
}

window.addEventListener('hashchange', () => render())

// ---- Playing

// Play: the host takes the best stream for this PC. Choose version (the chevron beside Play) asks first, unless there's
// just one.
async function play({ id, type, show }, resume = true, ask = false, from = null) {
  let source
  if (ask) {
    source = await pickSource({ id, type }, from)
    if (source === null) return
  }
  try {
    enterPlayer('')
    $('title').textContent = ''
    $('spinner').hidden = false
    const r = await jf('play', { id, type, show: show || undefined, resume, source: source == null ? undefined : Number(source) })
    // The original file plays as it is, so the host draws the seek bar's pictures from it.
    if (r) state.playMethod = 'Direct play'
    peekAhead()
    if (r?.item) window.dispatchEvent(new CustomEvent('exo:now-playing', { detail: r.item }))
  } catch (e) {
    if (!inPlayer()) return
    leavePlayer()
    // Said where Play was pressed (one action, one response); Up Next's own start, which nobody pressed, as a notice.
    if (from?.isConnected) pressError(from, e.message)
    else toast(e.message, true)
  }
}

// A version as the menu lists it: the picture first, then what else sets it apart.
const RANGES = { DOVI: 'Dolby Vision', HDR10: 'HDR10', HDR10Plus: 'HDR10+', HLG: 'HLG' }
const CODECS = { hevc: 'HEVC', h264: 'H.264', av1: 'AV1', vp9: 'VP9', mpeg2video: 'MPEG-2', atmos: 'Atmos', truehd: 'TrueHD', eac3: 'E-AC-3', ac3: 'AC-3', dtshd: 'DTS-HD', dts: 'DTS', aac: 'AAC', flac: 'FLAC', opus: 'Opus', mp3: 'MP3' }
const CHANNELS = { 1: 'Mono', 2: 'Stereo', 6: '5.1', 8: '7.1' }

function sourceRow(s) {
  const h = s.height ?? 0
  const res = h >= 2000 ? '4K' : h >= 1400 ? '1440p' : h >= 1000 ? '1080p' : h >= 700 ? '720p' : h ? `${h}p` : ''
  const range = RANGES[s.range] ?? ''
  const picture = [res, range, CODECS[s.videoCodec] ?? s.videoCodec?.toUpperCase()].filter(Boolean).join(' · ') || s.name || 'Stream'
  const audio = [CODECS[s.audioCodec] ?? s.audioCodec?.toUpperCase(), CHANNELS[s.channels] ?? (s.channels ? `${s.channels} ch` : '')].filter(Boolean).join(' ')
  // The stream's own name only when it says more than the picture does ("4K Remux", not "1080p" again).
  const named = s.name && !picture.toLowerCase().includes(s.name.toLowerCase()) ? s.name : ''
  const detail = [named, s.size ? bytes(s.size) : '', s.bitrate ? `${Math.round(s.bitrate / 1e6)} Mbps` : '', audio].filter(Boolean).join(' · ')
  return `<button type="button" class="source-row" role="option" data-source="${esc(s.id)}">
    <span class="source-text"><span class="source-name">${esc(picture)}</span><span class="source-detail">${esc(detail)}</span></span>
    ${s.best ? '<span class="source-best">Best</span>' : ''}</button>`
}

// The version menu: the server's versions of a film or an episode, best first, to pick one; null when it is put away.
// It opens with a spinner and fills when the server answers; a title with one version plays without asking.
function pickSource({ id, type }, from) {
  return new Promise((resolve) => {
    const menu = $('sources')
    let done = false
    const finish = (value) => {
      if (done) return
      done = true
      menu.hidden = true
      menu.onclick = null
      document.removeEventListener('keydown', onKey, true)
      document.removeEventListener('pointerdown', onAway, true)
      from?.removeAttribute('aria-expanded')
      resolve(value)
    }
    const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); finish(null); from?.focus({ preventScroll: true }) } }
    // A press anywhere else puts it away, as any menu.
    const onAway = (e) => { if (!menu.contains(e.target) && !from?.contains(e.target)) finish(null) }
    menu.innerHTML = '<div class="spinner sources-wait"></div>'
    // Shown after a beat: a title with one version plays straight away, without the menu flashing up.
    setTimeout(() => { if (!done && menu.hidden) { menu.hidden = false; placeSources(menu, from) } }, 200)
    document.addEventListener('keydown', onKey, true)
    document.addEventListener('pointerdown', onAway, true)
    menu.onclick = (e) => {
      const row = e.target.closest('[data-source]')
      if (row) finish(row.dataset.source)
    }
    jf('sources', { id, type }).then((all) => {
      if (done) return
      if (!all?.length || all.length === 1) { finish(all?.[0]?.id ?? undefined); return }
      menu.innerHTML = all.map(sourceRow).join('')
      menu.hidden = false
      from?.setAttribute('aria-expanded', 'true')
      placeSources(menu, from)
      menu.querySelector('[data-source]')?.focus({ preventScroll: true })
    }).catch((err) => { if (!done) { finish(null); from?.isConnected ? pressError(from, err.message) : toast(err.message, true) } })
  })
}

// The menu grows from the control pressed: under it, or above it where the window runs out, its edge on the control's
// and never past the window's (DESIGN.md › Motion › Appear: menus grow from their trigger's side).
function placeSources(menu, from) {
  const gap = 10, bar = 52
  const r = from?.getBoundingClientRect() ?? { left: (innerWidth - 460) / 2, top: innerHeight / 3, bottom: innerHeight / 3 }
  const m = menu.getBoundingClientRect()
  const left = Math.max(gap, Math.min(r.left, innerWidth - m.width - gap))
  const below = r.bottom + 8 + m.height <= innerHeight - gap || r.top - 8 - m.height < bar + gap
  menu.style.left = `${left}px`
  menu.style.top = `${below ? Math.min(r.bottom + 8, innerHeight - gap - m.height) : r.top - 8 - m.height}px`
  menu.style.transformOrigin = `${left === r.left ? 'left' : 'center'} ${below ? 'top' : 'bottom'}`
}

window.addEventListener('exo:notice', (e) => toast(e.detail, true))

// A press that didn't take says so where it was pressed (Exo's one action, one response): a red line under its row of
// buttons, gone at the next press there. A toast is only for what happens unattended, or a press whose control went.
function pressError(t, message) {
  const row = t.closest('.actions') ?? t.parentElement
  row.parentElement?.querySelector(':scope > .press-error')?.remove()
  row.insertAdjacentHTML('afterend', `<p class="form-error press-error" role="alert">${esc(message)}</p>`)
}
document.addEventListener('click', (e) => {
  const row = e.target.closest?.('.actions')
  if (row && e.target.closest('button')) row.parentElement?.querySelector(':scope > .press-error')?.remove()
}, true)

window.addEventListener('exo:player-closed', async () => {
  await jf('stop').catch(() => { })
  // The server has where it was left now: the page draws again with it.
  render()
  readWatch(true)
})

// ---- Clicks

document.addEventListener('click', async (e) => {
  const t = e.target.closest('[data-play],[data-open],[data-played],[data-app],[data-retry],[data-pref],[data-watchlist],[data-catalog],[data-find]')
  if (!t || inPlayer() || t.disabled) return
  if (t.dataset.watchlist) {
    // Shown at once; the server follows, and a refusal puts it back.
    const id = t.dataset.watchlist, on = t.getAttribute('aria-pressed') !== 'true'
    setPressed(t, on, 'bookmark')
    try {
      await jf('watch-save', { id, on })
      keepOnPage(id, { saved: on })
      readWatch(true)
    } catch (err) { setPressed(t, !on, 'bookmark'); pressError(t, err.message) }
  } else if (t.dataset.play) play({ id: t.dataset.play, type: t.dataset.type || 'movie', show: t.dataset.show }, !('start' in t.dataset), 'choose' in t.dataset, t)
  else if (t.dataset.open) location.hash = `#/title/${t.dataset.open.split('/').map(encodeURIComponent).join('/')}`
  else if (t.dataset.played) {
    const id = t.dataset.played, on = t.getAttribute('aria-pressed') !== 'true'
    setPressed(t, on)
    try {
      await jf('played', { id, on })
      keepOnPage(id, { watchedAt: on ? Date.now() : null, position: on ? null : cache.get(`meta:${id}`)?.position })
      readWatch(true)
      // The server's answer for the whole page: a show's episodes, or the episode's row, follow what was marked.
      render()
    } catch (err) { setPressed(t, !on); pressError(t, err.message) }
  } else if (t.dataset.catalog) {
    browse[t.dataset.room].at = Number(t.dataset.catalog)
    renderBrowse(t.dataset.room)
  } else if (t.dataset.find) location.hash = `#/search/${encodeURIComponent(t.dataset.find)}`
  else if (t.dataset.retry != null) { e.preventDefault(); render() }
  else if (t.dataset.app === 'settings') openSettings()
  else if (t.dataset.app === 'clear-cache') {
    t.disabled = true
    try { await jf('cache-clear') } catch { }
    t.disabled = false
    await readImageCache()
  } else if (t.dataset.pref) {
    setPref(t.dataset.pref, t.dataset.value)
    renderSettings()
    window.dispatchEvent(new Event('exo:prefs'))
  }
})

// Settings' gear at the top right, as Exo Launcher's. Your profile is a row inside Settings.
$('profileBtn').addEventListener('click', () => openSettings())

// ---- Settings: only what can change (Exo's rule), each applied the moment it is picked

const PREFS = {
  autoplay: { label: 'Play next episode', fallback: 'on' },
  smooth: { label: 'Smooth motion', fallback: 'off' },
}

const pref = (key) => { try { return localStorage.getItem(`exo:pref:${key}`) ?? PREFS[key].fallback } catch { return PREFS[key].fallback } }

function setPref(key, value) {
  try { localStorage.setItem(`exo:pref:${key}`, value) } catch { }
  applyPrefs()
  syncPut('setting', key, value)
}

// Smooth motion is the player's to apply (MainWindow.Player.cs › ApplyMotion): told at the start and whenever the
// choice changes, here or on another PC.
const tellSmooth = () => send({ cmd: 'smooth', on: pref('smooth') === 'on' })
tellSmooth()
window.addEventListener('exo:prefs', tellSmooth)

// ---- Sync: what your Exo profile carries to your other PCs (end to end encrypted). The host takes only what
// Sync/CinemaSettings.cs lists: these choices and each show's languages.
// What another PC chose comes back and is applied here.
const syncPut = (type, id, body) => send({ cmd: 'sync-put', type, id, body })
const SHOW_ID = /^(imdb:tt\d{1,10}|tmdb:tv:\d{1,10})$/

// Choices made before sync existed go in as the oldest of all, so one made on purpose on another PC wins over them.
function seedSync() {
  const items = []
  try {
    for (const key of Object.keys(PREFS)) {
      const value = localStorage.getItem(`exo:pref:${key}`)
      if (value != null) items.push({ type: 'setting', id: key, body: value })
    }
    for (let i = 0; i < localStorage.length; i++) {
      const id = localStorage.key(i)?.match(/^exo:lang:(.+)$/)?.[1]
      if (id && (id === 'movies' || SHOW_ID.test(id))) items.push({ type: 'languages', id, body: JSON.parse(localStorage.getItem(`exo:lang:${id}`)) })
    }
  } catch { }
  if (items.length) send({ cmd: 'sync-seed', items })
}
seedSync()

// This PC left its profile, or started or joined one: its choices go in again, as the oldest of all.
onHost((m) => { if (m.type === 'sync-reseed') seedSync() })

// The profile's name, picture or banner changed on another PC or in another Exo app: the Profile room shows the new ones.
onHost((m) => { if (m.type === 'profile-synced' && route() === 'profile') readAccount() })

// Exo Watch changed on another PC: History and the Watchlist are read again.
onHost((m) => { if (m.type === 'watch-synced') readWatch(true) })

onHost((m) => {
  if (m.type !== 'synced') return
  for (const { type, id, body } of m.items ?? []) {
    try {
      // Every setting the host accepted (Sync/CinemaSettings.cs): Play next episode, Glass or Solid, Reduce motion, Best or Ask.
      if (type === 'setting') body == null ? localStorage.removeItem(`exo:pref:${id}`) : localStorage.setItem(`exo:pref:${id}`, body)
      else if (type === 'languages') body == null ? localStorage.removeItem(`exo:lang:${id}`) : localStorage.setItem(`exo:lang:${id}`, JSON.stringify(body))
    } catch { }
  }
  applyPrefs()
  // Settings open shows the new choices, unless a field in it is being typed in.
  if (!$('settingsBody').contains(document.activeElement) || document.activeElement.matches('button')) renderSettings()
  window.dispatchEvent(new Event('exo:prefs'))
})

// The picture and the decoder aren't here: the app picks them from this PC as it plays (MainWindow.Player.cs). The look
// isn't either: every Exo app follows Exo Launcher's (boot.js › exoApplyAppearance).
// The look follows the choices: Glass or Solid and Reduce motion (synced or Launcher's) are drawn again (boot.js).
function applyPrefs() { exoApplyAppearance(window.exoAppearance) }
onHost((m) => { if (m.type === 'appearance') { exoApplyAppearance(m.appearance); window.dispatchEvent(new Event('exo:prefs')) } })
// The page's zoom on a screen bigger than 1080p (MainWindow.PageScale.cs): the player sizes what mpv draws by it.
onHost((m) => { if (m.type === 'page-zoom') { window.exoPageZoom = m.zoom; setHair() } })
// The one line (--exo-hair) is one device pixel wide. The page is laid out bigger than the screen and zoomed down on
// 1080p (0.9), where a 1px line is nine tenths of a pixel and comes out soft or uneven; this keeps it whole.
function setHair() { document.documentElement.style.setProperty('--exo-hair', `${1 / (dpr() || 1)}px`) }
setHair()
addEventListener('resize', setHair)
try { for (const key of ['quality', 'hwdec']) localStorage.removeItem(`exo:pref:${key}`) } catch { }

// One on-or-off choice as Settings shows it: its name, and a switch (Exo Launcher's: on and off are a switch, a list of choices a dropdown).
const prefRow = (key) => {
  const on = pref(key) === 'on'
  return `<div class="set-row"><span class="set-name" id="pref-${key}">${PREFS[key].label}</span>
  <button type="button" class="switch" role="switch" aria-checked="${on}" aria-labelledby="pref-${key}" data-pref="${key}" data-value="${on ? 'off' : 'on'}"><span class="switch__control"><span class="switch__thumb"></span></span></button></div>`
}

const bytes = (n) => n >= 1e9 ? `${(n / 1e9).toFixed(1)} GB` : n >= 1e6 ? `${Math.round(n / 1e6)} MB` : `${Math.round(n / 1e3)} KB`

// Settings floats over the room it was opened from (index.html › #settings), as Exo Launcher's and Exo Browser's: a
// sidebar of pages, then the page it names. The profile button's menu opens it; Escape, the close and a press around it
// close it. The room behind stays where it was and holds still.
const settingsOpen = () => !$('settings').hidden
let imageCacheBytes = null

// The pages, in the sidebar's order (Exo Launcher's SettingsPanel): the account first, then what Cinema itself has.
function settingsPages() {
  return [
    ...(exo.enabled ? [{ id: 'account', name: 'Account' }] : []),
    { id: 'playback', name: 'Playback' },
    { id: 'jellyfin', name: 'Jellyfin' },
    { id: 'storage', name: 'Storage' },
    { id: 'about', name: 'About' },
  ]
}
let settingsPage = 'account'

function openSettings() {
  if (settingsOpen()) return
  $('settings').hidden = false
  $('view').inert = true
  document.documentElement.classList.add('is-settings')
  if (!settingsPages().some(p => p.id === settingsPage)) settingsPage = settingsPages()[0].id
  renderSettings(true)
  focusWhenActive($('settings').querySelector('.settings-sheet'))
  readImageCache()
  // Each time Settings opens, the server is asked again (its row says Checking… until it answers).
  readJellyfin()
}

// The image cache's size, asked for each time Settings opens and after Clear; only its words change when it comes.
async function readImageCache() {
  try { imageCacheBytes = (await jf('cache-size')).bytes } catch { return }
  const value = $('imageCache')
  if (value) value.textContent = bytes(imageCacheBytes)
}

function closeSettings() {
  if (!settingsOpen()) return
  $('settings').hidden = true
  $('view').inert = false
  document.documentElement.classList.remove('is-settings')
}

$('settings').addEventListener('click', (e) => { if (e.target === $('settings')) closeSettings() })
$('settingsClose').addEventListener('click', () => { closeSettings(); $('profileBtn').focus({ preventScroll: true }) })
$('settingsNav').addEventListener('click', (e) => {
  const button = e.target.closest('[data-settings-page]')
  if (!button || button.dataset.settingsPage === settingsPage) return
  settingsPage = button.dataset.settingsPage
  renderSettings(true)
  button.focus({ preventScroll: true })
})

// The page's own sections (the page's name is its title over them, so no section repeats it).
function settingsPageBody(id) {
  switch (id) {
    case 'account':
      return accountRows()
    case 'playback':
      return `<section class="set-group" id="set-playback">${prefRow('autoplay')}${prefRow('smooth')}</section>`
    case 'jellyfin':
      // Signed out, the same form as the sign-in room; signed in, the account's row.
      return jellyfin.read && !jellyfin.me
        ? `<section class="set-group" id="set-jellyfin"><h2>Sign in to Jellyfin</h2>${jellyfinForm()}</section>`
        : `<section class="set-group" id="set-jellyfin">${jellyfinRows()}</section>`
    case 'storage':
      return `<section class="set-group" id="set-storage"><div class="set-row"><span class="set-name">Image cache</span>
        <span class="set-value" id="imageCache">${imageCacheBytes == null ? '' : bytes(imageCacheBytes)}</span>
        <button type="button" class="set-btn" data-app="clear-cache">Clear</button></div></section>`
    default:
      return aboutGroup()
  }
}

// The sidebar, the page's title and the page itself, drawn again in place when a choice changes. A page change starts
// it at its top.
function renderSettings(pageChanged = false) {
  if (!settingsOpen()) return
  const pages = settingsPages()
  if (!pages.some(p => p.id === settingsPage)) settingsPage = pages[0].id
  const body = $('settingsBody'), top = body.scrollTop
  // A field being typed in keeps the keys through a redraw (an error, Checking…).
  const typing = body.contains(document.activeElement) ? document.activeElement.dataset?.jellyfinField : null
  $('settingsNav').innerHTML = pages.map(p =>
    `<li><button type="button" data-settings-page="${p.id}"${p.id === settingsPage ? ' aria-current="page"' : ''}>${p.name}</button></li>`).join('')
  $('settingsPageTitle').textContent = pages.find(p => p.id === settingsPage).name
  body.innerHTML = settingsPageBody(settingsPage)
  body.scrollTop = pageChanged ? 0 : top
  if (typing) body.querySelector(`[data-jellyfin-field="${typing}"]`)?.focus({ preventScroll: true })
  drawAbout()
}

// ---- Jellyfin: one Connect (Jellyfin/JellyfinLibrary.cs). It says connected only once the server took the name and
// password and then answered as that account; each failure says which it was. Only the server's token is kept, sealed on
// this PC, so it's one sign-in. What's typed lives here until Connect, and the password goes as Connect answers.
// The server field starts at jlly.cc, filled in and editable; nothing is sent to it until Connect is pressed.
const JELLYFIN_DEFAULT_SERVER = 'https://jlly.cc'
// signedIn: whether this PC holds a sign-in, known as the page opens (the host's settings answer) and kept true while
// the server merely doesn't answer; me: who the server says that is, once it has answered.
const jellyfin = { read: false, signedIn: false, me: null, server: JELLYFIN_DEFAULT_SERVER, name: '', password: '', busy: false, error: null }
const JELLYFIN_FIELDS = [['server', 'url', 'Server URL', 'url'], ['name', 'text', 'Username', 'username'], ['password', 'password', 'Password', 'current-password']]

function jellyfinRows() {
  const s = jellyfin
  // Until the server has answered, the row says so: a slow answer is not an offline server.
  if (!s.read) return `<div class="set-row"><span class="set-name">Jellyfin</span><span class="set-value">Checking…</span></div>`
  if (s.me) {
    const state = s.checking ? 'Checking…' : s.me.offline ? 'Not answering' : s.me.encrypted ? '' : 'Not encrypted'
    return `<div class="set-row"><span class="set-name">Connected as ${esc(s.me.userName)} on ${esc(s.me.serverName)}</span>
      <span class="set-value">${state}</span>
      <button type="button" class="set-btn" data-jellyfin-out>Sign out</button></div>`
  }
  return ''
}

// The sign-in as a page's own form (Settings › Jellyfin, the sign-in room, the first run): the three fields stacked,
// Connect the form's one primary. Signed in, it is the account's row.
function jellyfinForm() {
  const s = jellyfin
  if (!s.read || s.me) return `<div class="set-group is-lined">${jellyfinRows()}</div>`
  const busy = s.busy ? ' disabled' : ''
  return `<div class="page-form">${JELLYFIN_FIELDS.map(([key, type, label, auto]) =>
      `<label class="acct-field"><span>${label}</span><input class="exo-field" data-jellyfin-field="${key}" type="${type}" value="${esc(s[key])}" autocomplete="${auto}" spellcheck="false" autocapitalize="off"${busy}></label>`).join('')}
    ${s.error ? `<p class="form-error" role="alert">${esc(s.error)}</p>` : ''}
    <div class="set-actions"><button type="button" class="set-btn is-primary" data-jellyfin-connect${busy}>${s.busy ? '<span class="spinner"></span>' : ''}Connect</button>
      <button type="button" class="acct-link" data-link="jellyfin-signup">Create an account</button></div></div>`
}

function drawJellyfin() {
  renderSettings()
  drawSignIn()
  window.dispatchEvent(new Event('exo:jellyfin'))
}

// With no server signed in to there is no library: every room is the sign-in, on the page's own black, the same form
// as Settings › Jellyfin. Connected, the room that was asked for takes its place (jellyfinChanged).
function renderSignIn() {
  if (!view.querySelector('.signin-room')) view.innerHTML = '<section class="signin-room"><div class="signin-column"></div></section>'
  drawSignIn()
}

// Drawn again in place as its state changes (checking, an error), the field being typed in keeping the keys.
function drawSignIn() {
  const column = view.querySelector('.signin-room .signin-column')
  if (!column) return
  const typing = column.contains(document.activeElement) ? document.activeElement.dataset?.jellyfinField : null
  column.innerHTML = `<img class="signin-logo" src="logo.svg" alt="" width="56" height="56" draggable="false">
    <h1 class="signin-title">Sign in to Jellyfin</h1>${jellyfinForm()}`
  if (typing) column.querySelector(`[data-jellyfin-field="${typing}"]`)?.focus({ preventScroll: true })
}

// Connected or signed out: what was drawn from the last library goes, and the new one (or the sign-in) comes in where
// you are, under Settings when that is where it was done.
function jellyfinChanged() {
  try { for (const key of Object.keys(localStorage)) if (/^exo:lib:(rows|row:|browse:|meta:|watch$)/.test(key)) localStorage.removeItem(key) } catch { }
  watchData = null
  watchRead = -Infinity
  readWatch(true)
  if (inPlayer() || route().split('/')[0] === 'profile') return
  view.querySelector('.home')?.remove()
  render(true)
}

// Asks the server who's signed in: one ask at a time, and the row says Checking… while it's on its way.
let jellyfinAsk = null
function readJellyfin() {
  if (jellyfinAsk) return jellyfinAsk
  const had = !!jellyfin.me
  const wasOffline = !!jellyfin.me?.offline
  jellyfin.checking = true
  drawJellyfin()
  jellyfinAsk = (async () => {
    try {
      const answer = await jf('jellyfin')
      jellyfin.me = answer?.userName ? answer : null
      jellyfin.signedIn = !!jellyfin.me
      if (answer?.expired) jellyfin.error = 'Signed out by the server. Connect again.'
    } catch { }
    jellyfin.read = true
    jellyfin.checking = false
    drawJellyfin()
    // The room shows the wrong side of the sign-in now (the server ended it, or it came back): drawn again.
    if (route().split('/')[0] !== 'profile' && jellyfin.signedIn === !!view.querySelector('.signin-room')) jellyfinChanged()
    // Back after being offline: the library is read again, as it is when the account changes.
    if (wasOffline && jellyfin.me && !jellyfin.me.offline) {
      readWatch(true)
      if (route().split('/')[0] === 'home') { view.querySelector('.home')?.remove(); renderHome() }
    }
    settleJellyfin()
  })().finally(() => { jellyfinAsk = null })
  return jellyfinAsk
}
// Offline: asked again on a backoff (15 s, 30 s, 60 s, then every 2 min), but only while Settings or Home is on screen,
// the window is in front and no film is playing. Nothing runs when idle in the background.
let jellyfinBackoff = 0, jellyfinNextAt = 0, jellyfinTimer = 0
function settleJellyfin() {
  clearTimeout(jellyfinTimer)
  if (!jellyfin.me?.offline) { jellyfinBackoff = 0; jellyfinNextAt = 0; return }
  jellyfinBackoff = Math.min(jellyfinBackoff ? jellyfinBackoff * 2 : 15_000, 120_000)
  jellyfinNextAt = Date.now() + jellyfinBackoff
  jellyfinTimer = setTimeout(jellyfinCheckIfOnScreen, jellyfinBackoff)
}
function jellyfinCheckIfOnScreen() {
  if (!jellyfin.me?.offline || jellyfinAsk || Date.now() < jellyfinNextAt) return
  const home = route().split('/')[0] === 'home'
  if (!(settingsOpen() || home) || document.hidden || document.body.classList.contains('mode-player')) return
  readJellyfin()
}
window.addEventListener('hashchange', jellyfinCheckIfOnScreen)
window.addEventListener('exo:left-player', jellyfinCheckIfOnScreen)
document.addEventListener('visibilitychange', () => { if (!document.hidden) jellyfinCheckIfOnScreen() })

async function jellyfinConnect() {
  const s = jellyfin
  if (s.busy) return
  Object.assign(s, { busy: true, error: null })
  drawJellyfin()
  try {
    s.me = await jf('jellyfin-connect', { server: s.server, name: s.name, password: s.password })
    s.signedIn = true
    s.read = true
    jellyfinChanged()
  } catch (err) {
    s.error = err.message
  }
  Object.assign(s, { busy: false, password: '' })
  drawJellyfin()
  if (!s.me) document.querySelector('[data-jellyfin-field="password"]')?.focus()
}

document.addEventListener('input', (e) => {
  const key = e.target.dataset?.jellyfinField
  if (key) jellyfin[key] = e.target.value
})
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Enter' || !e.target.dataset?.jellyfinField) return
  e.preventDefault()
  jellyfinConnect()
})
document.addEventListener('click', async (e) => {
  const t = e.target.closest('[data-jellyfin-connect],[data-jellyfin-out]')
  if (!t || t.disabled) return
  if ('jellyfinConnect' in t.dataset) return jellyfinConnect()
  if (!confirmInPlace(t, 'Sign out?')) return
  t.disabled = true
  try { await jf('jellyfin-sign-out') } catch { }
  jellyfin.me = null
  jellyfin.signedIn = false
  drawJellyfin()
  jellyfinChanged()
})

// ---- Profile: the Exo profile as a room of its own (Sync/ExoAccount.cs), as Exo Launcher's Profile room
// (ProfileRoom.tsx), one for Exo Launcher, Exo Cinema and Exo Browser. Its top is the banner, the picture over its lower
// edge, the name and Edit profile; under them the Exo account (Continue with Google, Apple or email; Sign out) and the
// profile's PCs. Signed in once for all three on this PC, it joins the account's profile with everything in it (Exo
// Watch, your choices), or starts one. Its tab shows only while there's an account service to reach.
const PROVIDER_NAMES = { google: 'Google', apple: 'Apple', email: 'email', dev: 'the dev account' }

async function readAccount() {
  try {
    const status = await app('account')
    acct.read = true
    acct.signedIn = status.signedIn === true
    acct.email = status.email || ''
    exo.enabled = true
    exo.account = status.signedIn ? { id: status.accountId, email: status.email } : null
  } catch {
    acct.read = true
  }
  drawAccount()
  if (acct.signedIn) readSessions()
}

function drawAccount() {
  drawRooms()
  if (route() === 'profile') renderProfile()
  // The top right button is Settings' gear, as Exo Launcher's; your profile is a row inside Settings.
  if (!$('avatar').querySelector('svg')) $('avatar').innerHTML = icon('settings')
  renderSettings()
  window.dispatchEvent(new Event('exo:account'))
  focusExo()
}

// ---- The Exo account (Account/LiveAccount.cs, cloud/account/CLIENT.md): one email and password for every Exo app, drawn
// as Exo Launcher's Settings › Account. Signed out: how to make one or sign in. Signed in: this PC's session, the other
// PCs, the password, a new recovery code and deleting the account. The password goes as the press it is; the recovery
// code is shown once and kept nowhere.
const acct = { read: false, signedIn: false, email: '', busy: null, screen: 'choose', error: null, note: null, code: null, sessions: null, sync: null, offline: false, open: null, form: { email: '', password: '', code: '', confirm: '', current: '', next: '', pw: '' } }

const ACCT_FIELDS = {
  email: ['Email', 'email', 'email', 'off'],
  password: ['Password', 'password', 'new-password', 'off'],
  current: ['Current password', 'password', 'current-password', 'off'],
  next: ['New password', 'password', 'new-password', 'off'],
  code: ['Recovery code', 'text', 'off', 'off'],
  pw: ['Password', 'password', 'current-password', 'off'],
  confirm: ['Type the code to confirm', 'text', 'off', 'off'],
}

const acctField = (key) => {
  const [label, type, auto] = ACCT_FIELDS[key]
  return `<label class="acct-field"><span>${label}</span><input class="exo-field" data-acct-field="${key}" type="${type}" value="${esc(acct.form[key] ?? '')}" autocomplete="${auto}" spellcheck="false"${acct.busy ? ' disabled' : ''}></label>`
}

// What the service takes for the code: dashes and spaces ignored, O read as 0 and I or L as 1 (CLIENT.md 4.1 C).
const acctCodeOf = (text) => String(text ?? '').toUpperCase().replace(/[^A-Z0-9]/g, '').replace(/O/g, '0').replace(/[IL]/g, '1')

// Signed out, as Exo Browser draws it (browser/Settings.tsx > SignedOutAccount): one row that says what this is, the
// form under it, and the other way in beside the primary.
function accountRows() {
  if (!acct.read) return `<p class="acct-hint">Reading your account…</p>`
  if (acct.code) return acctCodeRows()
  if (acct.signedIn) return acctSignedInRows()
  return acctForm(acct.screen === 'create' ? 'create' : acct.screen === 'reset' ? 'reset' : 'signin')
}

function acctForm(kind) {
  const create = kind === 'create'
  const reset = kind === 'reset'
  const words = create ? 'Create account' : reset ? 'Reset password' : 'Sign in'
  const heading = create ? 'Create your Exo account' : reset ? 'Reset your password' : 'Sign in to Exo'
  const hint = reset ? 'At least 12 characters. Every PC signed in to this account will sign in again.' : create ? 'At least 12 characters.' : ''
  const other = create
    ? '<button type="button" class="acct-link" data-acct="screen-signin">I already have one</button>'
    : reset
      ? '<button type="button" class="acct-link" data-acct="screen-signin">Back to sign in</button>'
      : '<button type="button" class="acct-link" data-acct="screen-create">Create an account</button><button type="button" class="acct-link" data-acct="screen-reset">Forgot your password?</button>'
  return `<section class="set-group" id="set-account"><h2>${heading}</h2>
    <form class="set-sub page-form" data-acct-form="${kind}" novalidate>
      ${acctField('email')}
      ${reset ? acctField('code') : ''}
      ${acctField('password')}
      ${hint ? `<p class="acct-hint">${hint}</p>` : ''}
      ${acct.error ? `<p class="form-error" role="alert">${esc(acct.error)}</p>` : ''}
      <div class="set-actions">
        <button type="submit" class="set-btn is-primary"${acct.busy ? ' disabled' : ''}>${acct.busy ? '<span class="spinner"></span>' : ''}${words}</button>
        ${other}
      </div>
    </form></section>`
}

function acctCodeRows() {
  const matches = acctCodeOf(acct.form.confirm) === acctCodeOf(acct.code) && acctCodeOf(acct.code).length > 0
  // Shown once and kept nowhere, so the words stay: this is the one screen where something is lost by reading past it.
  return `<section class="set-group" id="set-account"><h2>Your recovery code</h2>
    <div class="page-form is-code">
      <p class="acct-lede">This is the only way back in if you forget your password. Exo doesn't keep it, and you won't see it again.</p>
      <div class="acct-code-row"><p class="acct-code">${esc(acct.code)}</p><button type="button" class="set-btn" data-acct="copy-code">${acct.note === 'Copied' ? 'Copied' : 'Copy'}</button></div>
      <label class="acct-field"><span>Type the code to confirm</span>
        <input class="exo-field" id="acctConfirm" data-acct-field="confirm" type="text" value="${esc(acct.form.confirm)}" autocomplete="off" spellcheck="false"></label>
      <div class="set-actions"><button type="button" class="set-btn is-primary" data-acct="code-done"${matches ? '' : ' disabled'}>Confirm</button></div>
    </div></section>`
}
// Signed in, as Exo Browser draws it (browser/Settings.tsx): the section's rows are the actions, and a form opens in
// place under the row that asked for it. The signed-in PCs are a Show, so the page stays short.
function acctSignedInRows() {
  const sessions = acct.sessions
  const list = sessions === null ? '<p class="acct-hint">Loading…</p>' : sessions.length === 0 ? '<p class="acct-hint">No PCs are signed in.</p>' :
    `<ul class="acct-list">${sessions.map(row => `<li class="acct-row">
      <span>${row.thisPc ? 'This PC' : 'Another PC'}</span>
      <span class="acct-hint">Created ${new Date(row.created * 1000).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })} · Last used ${new Date(row.lastUsed * 1000).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}</span>
      ${row.thisPc ? '' : `<button type="button" class="set-btn is-compact" data-acct="revoke" data-device="${esc(row.device)}">Remove</button>`}</li>`).join('')}</ul>`
  const toggle = (open) => acct.open === open ? 'Cancel' : null
  return `
    <section class="set-group" id="set-account">
      <div class="set-row"><span class="set-name">Signed in as</span><span class="set-value">${esc(acct.email)}</span></div>
      <div class="set-row"><span class="set-name">Sync</span><span class="set-value">${esc(syncLine(acct.sync))}</span><button type="button" class="set-btn is-compact" data-acct="sync-now"${acct.busy ? ' disabled' : ''}>Sync now</button></div>
      ${acct.sync && acct.sync.held > 0 ? `<p class="acct-hint">Held on this PC · ${acct.sync.held}. ${esc(acct.sync.heldReasons.includes('too-large') ? 'Too large to sync, so it stays on this PC.' : 'The service did not accept it, so it stays on this PC.')}</p>` : ''}
    </section>
    <section class="set-group" id="set-security"><h2>Security</h2>
      <div class="set-row"><span class="set-name">Change password<small>Other PCs are signed out.</small></span>
        <button type="button" class="set-btn is-compact" data-acct="open-password">${toggle('password') ?? 'Change'}</button></div>
      ${acct.open === 'password' ? `<form class="set-sub page-form" data-acct-form="password" novalidate>
        ${acctField('current')}${acctField('next')}<p class="acct-hint">At least 12 characters.</p>
        <div class="set-actions"><button type="submit" class="set-btn is-primary"${acct.busy ? ' disabled' : ''}>Change password</button></div></form>` : ''}
      <div class="set-row"><span class="set-name">New recovery code<small>Your old code stops working.</small></span>
        <button type="button" class="set-btn is-compact" data-acct="open-recovery">${toggle('recovery') ?? 'New code'}</button></div>
      ${acct.open === 'recovery' ? `<form class="set-sub page-form" data-acct-form="recovery" novalidate>
        ${acctField('pw')}
        <div class="set-actions"><button type="submit" class="set-btn is-primary"${acct.busy ? ' disabled' : ''}>Make a new code</button></div></form>` : ''}
    </section>
    <section class="set-group" id="set-sessions"><h2>Sessions</h2>
      <div class="set-row"><span class="set-name">Signed-in PCs</span>
        <button type="button" class="set-btn is-compact" data-acct="open-sessions">${acct.open === 'sessions' ? 'Hide' : 'Show'}</button></div>
      ${acct.open === 'sessions' ? list : ''}
    </section>
    <section class="set-group" id="set-data"><h2>Your data</h2>
      <div class="set-row"><span class="set-name">Export my data</span>
        <button type="button" class="set-btn is-compact" data-acct="export">Save a copy</button></div>
      <div class="set-row"><span class="set-name">Sign out</span>
        ${acct.open === 'signout'
          ? `<span class="acct-actions"><button type="button" class="set-btn is-compact" data-acct="open-signout">Keep me signed in</button><button type="button" class="set-btn is-compact is-primary" data-acct="signout"${acct.busy ? ' disabled' : ''}>Sign out</button></span>`
          : `<button type="button" class="set-btn is-compact" data-acct="open-signout">Sign out</button>`}</div>
      <div class="set-row"><span class="set-name">Delete account</span>
        <button type="button" class="set-btn is-compact is-danger" data-acct="open-delete">${toggle('delete') ?? 'Delete'}</button></div>
      ${acct.open === 'delete' ? `<form class="set-sub page-form" data-acct-form="delete" novalidate>
        ${acctField('pw')}
        <p class="acct-hint">This deletes the account and every item synced to it. Your watch history and settings stay on this PC.</p>
        <div class="set-actions"><button type="submit" class="set-btn is-danger"${acct.busy ? ' disabled' : ''}>Delete my account</button></div></form>` : ''}
    </section>
    ${acct.error ? `<p class="form-error" role="alert">${esc(acct.error)}</p>` : ''}
    ${acct.note ? `<p class="acct-hint" role="status">${esc(acct.note)}</p>` : ''}`
}

async function acctCall(op, args = {}) {
  try { return await app(op, args) } catch (e) { return { ok: false, message: e.message || 'Something went wrong.' } }
}

function acctDraw() {
  if (settingsOpen()) renderSettings()
  window.dispatchEvent(new Event('exo:account'))
}

async function acctSubmit(kind) {
  if (acct.busy) return
  acct.offline = false
  const f = acct.form
  if (!f.email.includes('@')) { acct.error = 'Enter an email address, like name@example.com.'; return acctDraw() }
  if (kind === 'create' && f.password.length < 12) { acct.error = 'Use at least 12 characters.'; return acctDraw() }
  if (kind === 'reset' && f.password.length < 12) { acct.error = 'Use at least 12 characters.'; return acctDraw() }
  acct.busy = kind; acct.error = null; acct.note = null
  acctDraw()
  const result = kind === 'create' ? await acctCall('account-create', { email: f.email, password: f.password })
    : kind === 'signin' ? await acctCall('account-signin', { email: f.email, password: f.password })
    : await acctCall('account-reset', { email: f.email, code: f.code, password: f.password })
  acct.busy = null
  if (!result.ok) { acct.error = result.message || 'Something went wrong.'; acct.offline = result.offline === true; return acctDraw() }
  f.password = ''
  if (result.recoveryCode) {
    acct.code = result.recoveryCode
    acct.form.confirm = ''
    acct.note = null
  } else {
    acct.code = null
  }
  acct.signedIn = true
  acct.email = f.email
  acct.screen = 'choose'
  exo.account = { email: f.email }
  acctDraw()
  readSessions()
}

async function readSessions() {
  const result = await acctCall('account-sessions')
  acct.sessions = result.ok ? result.sessions ?? [] : []
  await readSync()
  acctDraw()
}

// Settings › Account's Sync row: the account sync's state (Services/Account/AccountSync in Exo Launcher; account-sync-status here).
async function readSync() {
  const status = await acctCall('account-sync-status')
  acct.sync = status && status.ok !== false && 'lastSyncAt' in status ? status : null
}

function syncLine(s) {
  if (!s) return 'Not synced yet'
  if (s.signedOut) return 'Your session ended. Sign in again to keep syncing.'
  if (s.pushStopped) return 'Your sync storage is full. Your data stays on this PC.'
  if (s.pending > 0) return `Waiting to sync · ${s.pending}`
  return s.lastSyncAt ? `Up to date · ${new Date(s.lastSyncAt).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}` : 'Not synced yet'
}

// The page asks for the state while Settings › Account is open: the sync moves on its own.
setInterval(() => { if (acct.signedIn && settingsOpen() && !acct.busy) readSync().then(acctDraw) }, 5000)

// ---- "Exo now has accounts": shown once to someone who had Exo Cinema before accounts, while signed out. The flag is
// saved as the sheet shows, so it is never offered twice; Not now closes it for good. Its look follows Exo Launcher's
// AccountSheet: the logo, three lines and Not now, Sign in, Create account; Create and Sign in open the account form inside.
let accountOfferScreen = 'intro'

// Due once the first run is done, the account service answers, nothing is signed in, and no other sheet or the player is up.
function accountOfferDue(info, signedIn, enabled, otherUp) {
  return !!info?.onboarded && !info.accountOffered && enabled && !signedIn && !otherUp
}

function maybeAccountOffer() {
  const otherUp = !$('whatsNew').hidden || !$('updateOffer').hidden || ob.active || inPlayer()
  if (!accountOfferDue(appInfo, acct.signedIn, exo.enabled, otherUp)) return
  appInfo.accountOffered = true
  app('set', { accountOffered: true }).catch(() => { })
  accountOfferScreen = 'intro'
  $('accountOffer').hidden = false
  drawAccountOffer()
  focusWhenActive($('accountOffer').querySelector('.whats-new-sheet'))
}

function drawAccountOffer() {
  const box = $('accountOffer')
  if (box.hidden) return
  // Signed in with no recovery code left to show: the sheet's job is done.
  if (acct.signedIn && !acct.code) return closeAccountOffer()
  const form = box.querySelector('[data-offer-form]')
  box.querySelector('[data-offer-intro]').hidden = accountOfferScreen !== 'intro'
  form.hidden = accountOfferScreen === 'intro'
  if (!form.hidden) form.innerHTML = accountRows()
}

function closeAccountOffer() {
  $('accountOffer').hidden = true
  accountOfferScreen = 'intro'
  acct.screen = 'choose'
}

$('accountOffer').addEventListener('click', (e) => {
  const action = e.target.closest?.('[data-offer]')?.dataset.offer
  if (action === 'not-now') return closeAccountOffer()
  if (action === 'create' || action === 'signin') {
    acct.screen = action === 'create' ? 'create' : 'signin'
    accountOfferScreen = 'form'
    drawAccountOffer()
  }
})
$('accountOffer').addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && accountOfferScreen === 'intro') closeAccountOffer()
})
window.addEventListener('exo:account', drawAccountOffer)

document.addEventListener('input', (e) => {
  const key = e.target.dataset?.acctField
  if (!key) return
  acct.form[key] = e.target.value
  // The code screen's Continue follows what is typed, without redrawing the field under the caret.
  if (key === 'confirm') {
    const confirmText = e.target.value
    const matches = acctCodeOf(confirmText) === acctCodeOf(acct.code) && acctCodeOf(acct.code).length > 0
    const button = document.querySelector('[data-acct="code-done"]')
    if (button) button.disabled = !matches
  }
})

document.addEventListener('submit', (e) => {
  const form = e.target.closest?.('[data-acct-form]')
  if (!form) return
  e.preventDefault()
  const kind = form.dataset.acctForm
  if (kind === 'create' || kind === 'signin' || kind === 'reset') return acctSubmit(kind)
  acctAccountAction(kind)
})

async function acctAccountAction(kind) {
  if (acct.busy) return
  const f = acct.form
  acct.busy = kind; acct.error = null; acct.note = null
  acctDraw()
  if (kind === 'password') {
    if (f.next.length < 12) { acct.busy = null; acct.error = 'Use at least 12 characters.'; return acctDraw() }
    const result = await acctCall('account-password', { current: f.current, password: f.next })
    acct.busy = null
    if (!result.ok) { acct.error = result.message; return acctDraw() }
    f.current = ''; f.next = ''
    acct.open = null
    acct.note = 'Password changed. Other PCs were signed out.'
  } else if (kind === 'recovery') {
    const result = await acctCall('account-recovery', { password: f.pw })
    acct.busy = null
    if (!result.ok) { acct.error = result.message; return acctDraw() }
    f.pw = ''
    acct.open = null
    acct.code = result.recoveryCode
    acct.note = null
  } else if (kind === 'delete') {
    const result = await acctCall('account-delete', { password: f.pw })
    acct.busy = null
    if (!result.ok) { acct.error = result.message; return acctDraw() }
    f.pw = ''
    acct.signedIn = false
    acct.email = ''
    acct.sessions = null
    acct.open = null
    acct.note = 'Your account was deleted. The data on this PC stays.'
    exo.account = null
  }
  acctDraw()
}

document.addEventListener('click', async (e) => {
  const target = e.target.closest('[data-acct]')
  if (!target || target.disabled) return
  const action = target.dataset.acct
  if (action === 'screen-create' || action === 'screen-signin' || action === 'screen-reset') {
    acct.screen = action.slice(7)
    acct.error = null; acct.note = null
    return acctDraw()
  }
  if (action === 'back') { acct.screen = 'choose'; acct.error = null; acct.code = null; acct.note = null; return acctDraw() }
  if (action === 'copy-code') {
    try { await navigator.clipboard.writeText(acct.code) ; acct.note = 'Copied' } catch { acct.note = null }
    return acctDraw()
  }
  if (action === 'code-done') { acct.code = null; acct.note = null; acct.form.confirm = ''; return acctDraw() }
  if (action === 'sync-now') {
    acct.busy = 'sync'; acctDraw()
    await acctCall('account-sync-now')
    acct.busy = null
    await readSync()
    return acctDraw()
  }
  if (action === 'open-password' || action === 'open-recovery' || action === 'open-sessions' || action === 'open-delete' || action === 'open-signout') {
    const which = action.slice(5)
    acct.open = acct.open === which ? null : which
    acct.error = null; acct.note = null
    return acctDraw()
  }
  if (action === 'export') {
    const result = await acctCall('account-export')
    if (result.cancelled) return
    acct.note = result.ok ? 'Saved.' : (result.message || 'The file was not saved.')
    return acctDraw()
  }
  if (action === 'signout') {
    acct.busy = 'signout'; acctDraw()
    await acctCall('account-signout')
    acct.busy = null; acct.signedIn = false; acct.email = ''; acct.sessions = null; acct.open = null; acct.screen = 'choose'; acct.code = null
    exo.account = null
    return acctDraw()
  }
  if (action === 'revoke') {
    const result = await acctCall('account-revoke', { device: target.dataset.device })
    if (!result.ok) { acct.error = result.message; return acctDraw() }
    acct.note = 'That PC was signed out.'
    return readSessions()
  }
})

// Continue with Google, Apple or email (whichever the service has on), then the email's two steps under them; and,
// on a PC set up for Exo's development, the dev servers' own dev account.
function exoWays(kind) {
  const busy = exo.busy ? ' disabled' : ''
  // Continue with Google is the one primary (DESIGN.md › First run; Launcher's SignIn).
  const way = (p, mark, words) => exo.providers.includes(p)
    ? `<button type="button" class="${p === 'google' ? `${kind.replace('secondary', 'primary')} is-primary` : kind} exo-way" data-exo="${p}"${busy}${p === 'email' ? ` aria-expanded="${!!exo.email}"` : ''}>${exo.busy === p ? '<span class="spinner"></span>' : mark}${words}</button>`
    : ''
  return `<div class="exo-signin">${way('google', MARKS.google, 'Continue with Google')}${way('apple', MARKS.apple, 'Continue with Apple')}${way('email', icon('mail'), 'Continue with email')}${way('dev', icon('flask'), 'Continue with dev account')}</div>
    ${exo.email ? exoEmail() : ''}${exoError()}`
}

function exoEmail() {
  const e = exo.email, busy = exo.busy ? ' disabled' : ''
  if (!e.ticket) return `<div class="exo-form"><input class="exo-field" type="email" id="exoEmail" autocomplete="email" spellcheck="false" placeholder="Email address" aria-label="Email address" value="${esc(e.address)}"${busy}>
    <div class="exo-actions"><button type="button" class="set-btn" data-exo="email-cancel"${busy}>Cancel</button><button type="button" class="set-btn is-primary" data-exo="email-send"${busy || !e.address.includes('@') ? ' disabled' : ''}>${exo.busy === 'email-send' ? '<span class="spinner"></span>' : ''}Send code</button></div></div>`
  return `<div class="exo-form"><input class="exo-field exo-code" id="exoEmailCode" inputmode="numeric" autocomplete="one-time-code" maxlength="7" placeholder="6-digit code" aria-label="Code sent to ${esc(e.address)}" value="${esc(e.code)}"${busy}>
    <div class="exo-actions"><button type="button" class="set-btn" data-exo="email-send"${busy}>Send again</button><button type="button" class="set-btn is-primary" data-exo="email-finish"${busy || e.code.replace(/\D/g, '').length !== 6 ? ' disabled' : ''}>${exo.busy === 'email-finish' ? '<span class="spinner"></span>' : ''}Sign in</button></div></div>`
}

const exoError = () => exo.error ? `<p class="form-error exo-error" role="alert">${esc(exo.error)}</p>${exo.unsent ? `<div class="exo-actions">
  <button type="button" class="set-btn" data-exo="sign-out"${exo.busy ? ' disabled' : ''}>Try again</button>
  <button type="button" class="set-btn" data-exo="sign-out-anyway"${exo.busy ? ' disabled' : ''}>Sign out anyway</button></div>` : ''}` : ''

// The email's step keeps the keys after a redraw: its address, then its code.
function focusExo() {
  const field = !exo.busy && exo.email && document.querySelector('#exoEmailCode, #exoEmail')
  if (field && field !== document.activeElement) focusWhenActive(field)
}

async function exoRun(way) {
  exo.busy = way
  exo.error = null
  exo.unsent = false
  drawAccount()
  try {
    if (way === 'google' || way === 'apple' || way === 'dev') await app('account-sign-in', { provider: way })
    else if (way === 'email-send') {
      exo.email.ticket = (await app('account-email-start', { email: exo.email.address.trim() })).ticket
      exo.email.code = ''
    } else if (way === 'email-finish') {
      await app('account-email-finish', { ticket: exo.email.ticket, code: exo.email.code.replace(/\D/g, '') })
      exo.email = null
    } else if (way === 'sign-out-keep' || way === 'sign-out-remove' || way === 'sign-out' || way === 'sign-out-anyway') {
      // Asked once: keep this PC's copy of the profile (taken back at the next sign-in) or remove it. Changes this PC
      // couldn't send stop it, with Try again and Sign out anyway beside the words.
      if (way === 'sign-out-keep' || way === 'sign-out-remove') exo.keep = way === 'sign-out-keep'
      const out = await app('account-sign-out', { anyway: way === 'sign-out-anyway', keep: exo.keep !== false })
      exo.unsent = !!out?.unsent
      if (out?.unsent) exo.error = out.message
    }
  } catch (err) {
    exo.error = err.message || (way === 'sign-out' ? 'You couldn’t be signed out. Try again.' : 'Signing in didn’t finish. Try again.')
  }
  exo.busy = null
  await readAccount()
  // Signed in or out: Exo Watch is the account's now.
  if (way !== 'email-send') readWatch(true)
}

document.addEventListener('click', (e) => {
  const t = e.target.closest('[data-exo]')
  if (!t || t.disabled) return
  const way = t.dataset.exo
  if (way === 'email') {
    exo.email = exo.email ? null : { address: '', ticket: null, code: '' }
    exo.error = null
    drawAccount()
  } else if (way === 'email-cancel') {
    exo.email = null
    drawAccount()
  } else if (way === 'sign-out' && !exo.unsent) {
    // Sign out asks what happens to this PC's copy of the profile: a menu from the button (DESIGN.md › Confirm, a choice
    // of more than yes or no), its second row asking in place in Bad, since what it removes can't be taken back.
    exo.error = null
    signOutMenu(t)
  } else exoRun(way)
})

function signOutMenu(from) {
  document.querySelector('.choice-menu')?.remove()
  const menu = document.createElement('div')
  menu.className = 'exo-glass is-panel is-menu sources-menu choice-menu'
  menu.setAttribute('role', 'menu')
  menu.setAttribute('aria-label', 'Sign out')
  menu.innerHTML = `<button type="button" class="menu-row" role="menuitem" data-choice="sign-out-keep">${icon('check')}Keep a copy here</button>
    <button type="button" class="menu-row" role="menuitem" data-choice="sign-out-remove">${icon('trash')}Remove from this PC</button>`
  document.body.append(menu)
  from.setAttribute('aria-expanded', 'true')
  const close = () => {
    menu.remove()
    from.removeAttribute('aria-expanded')
    document.removeEventListener('pointerdown', away, true)
    document.removeEventListener('keydown', key, true)
  }
  const away = (e) => { if (!menu.contains(e.target) && !from.contains(e.target)) close() }
  const key = (e) => { if (e.key === 'Escape' && !menu.querySelector('.is-armed')) { e.preventDefault(); e.stopPropagation(); close(); from.focus({ preventScroll: true }) } }
  menu.addEventListener('click', (e) => {
    const row = e.target.closest('[data-choice]')
    if (!row) return
    if (row.dataset.choice === 'sign-out-remove' && !confirmInPlace(row, 'Remove from this PC?')) return
    close()
    exoRun(row.dataset.choice)
  })
  placeSources(menu, from)
  document.addEventListener('pointerdown', away, true)
  document.addEventListener('keydown', key, true)
  menu.querySelector('.menu-row').focus({ preventScroll: true })
}

// Typing keeps what's typed and lets the step's button go; Enter presses it.
document.addEventListener('input', (e) => {
  const f = e.target
  const button = f.closest('.exo-form')?.querySelector('.is-primary')
  if (f.id === 'exoEmail') { exo.email.address = f.value; button.disabled = !f.value.includes('@') }
  else if (f.id === 'exoEmailCode') { exo.email.code = f.value; button.disabled = f.value.replace(/\D/g, '').length !== 6 }
})
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Enter' || !e.target.closest?.('.exo-form')) return
  e.preventDefault()
  e.stopPropagation()
  const button = e.target.closest('.exo-form').querySelector('.is-primary')
  if (!button.disabled) button.click()
}, true)


// A choice of several, as a menu from the control pressed (the version menu's own).
function chooseFrom(from, options, label) {
  return new Promise((resolve) => {
    const menu = $('sources')
    menu.setAttribute('aria-label', label)
    menu.innerHTML = options.map((o) => `<button type="button" class="source-row" role="option" data-choice="${esc(o)}"><span class="source-text"><span class="source-name">${esc(o)}</span></span></button>`).join('')
    menu.hidden = false
    placeSources(menu, from)
    const done = (value) => {
      menu.hidden = true
      menu.onclick = null
      menu.setAttribute('aria-label', 'Choose a version')
      document.removeEventListener('pointerdown', away, true)
      document.removeEventListener('keydown', key, true)
      resolve(value)
    }
    const away = (e) => { if (!menu.contains(e.target)) done(null) }
    const key = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(null) } }
    menu.onclick = (e) => { const row = e.target.closest('[data-choice]'); if (row) done(row.dataset.choice) }
    document.addEventListener('pointerdown', away, true)
    document.addEventListener('keydown', key, true)
    menu.querySelector('[data-choice]')?.focus({ preventScroll: true })
  })
}

// Mouse back button and Backspace go back in the library.
document.addEventListener('mouseup', (e) => {
  if (e.button !== 3) return
  if (inPlayer()) actions.back()
  else history.back()
})
document.addEventListener('keydown', (e) => {
  if (inPlayer() || e.defaultPrevented || e.isComposing || e.keyCode === 229) return
  const editing = e.target?.isContentEditable || e.target?.closest?.('input, textarea, select')
  if ((e.ctrlKey && e.key.toLowerCase() === 'k') || (e.key === '/' && !editing)) {
    e.preventDefault()
    openPalette()
  } else if (settingsOpen()) {
    // Escape closes Settings; Backspace never goes back in the room behind it.
    if (e.key === 'Escape') { e.preventDefault(); closeSettings(); $('profileBtn').focus({ preventScroll: true }) }
  } else if (e.key === 'Backspace' && !editing) {
    history.back()
  }
})

// ---- Ctrl+K: search the server as you type; arrows and Enter open a result

let paletteItems = []
let paletteIndex = 0
let paletteAsk = 0

function openPalette() {
  if (inPlayer()) return
  $('palette').hidden = false
  $('paletteInput').value = ''
  $('paletteResults').innerHTML = ''
  paletteItems = []
  searching(false)
  requestAnimationFrame(() => $('paletteInput').focus())
}

function closePalette() {
  $('palette').hidden = true
  $('paletteInput').blur()
  paletteAsk++
  searching(false)
}

// While the server looks, the field's own spinner turns; what was found stays until new results replace it.
const searching = (on) => $('palette').classList.toggle('is-searching', on)

// The results, or when there are none, what happened: no matches, or why the search failed.
function drawPalette(note = '', bad = false) {
  if (!paletteItems.length && note) {
    $('paletteResults').innerHTML = `<p class="palette-note${bad ? ' is-bad' : ''}">${esc(note)}</p>`
    return
  }
  $('paletteResults').innerHTML = paletteItems.map((i, n) => {
    const src = poster(i, 80)
    const sub = [i.type === 'movie' ? 'Movie' : 'Show', year(i)].filter(Boolean).join(' · ')
    return `<button type="button" class="palette-row${n === paletteIndex ? ' is-on' : ''}" role="option" aria-selected="${n === paletteIndex}" data-pick="${n}">
      <span class="palette-art">${src ? `<img src="${esc(src)}" alt="">` : ''}</span>
      <span class="palette-text"><span class="palette-name">${esc(i.name)}</span><span class="palette-sub">${esc(sub)}</span></span>
    </button>`
  }).join('')
}

function pickPalette(n) {
  const i = paletteItems[n]
  if (!i) return
  closePalette()
  location.hash = `#/title/${encodeURIComponent(i.type)}/${encodeURIComponent(i.id)}`
}

$('openSearch').addEventListener('click', openPalette)
$('palette').addEventListener('click', (e) => {
  if (e.target.closest('[data-close-palette]')) closePalette()
  const row = e.target.closest('[data-pick]')
  if (row) pickPalette(+row.dataset.pick)
})
$('paletteInput').addEventListener('input', () => {
  clearTimeout(searchTimer)
  const term = $('paletteInput').value.trim()
  const ask = ++paletteAsk
  if (!term) { paletteItems = []; drawPalette(); searching(false); return }
  searching(true)
  // A beat after the last key, so a word typed quickly asks the server once.
  searchTimer = setTimeout(async () => {
    try {
      const r = await jf('search', { term })
      if (ask !== paletteAsk || !r) return
      paletteItems = r.slice(0, 12)
      paletteIndex = 0
      drawPalette('No matches')
    } catch (err) {
      if (ask !== paletteAsk) return
      paletteItems = []
      drawPalette(err.message, true)
    } finally {
      if (ask === paletteAsk) searching(false)
    }
  }, 250)
})
$('paletteInput').addEventListener('keydown', (e) => {
  if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closePalette() }
  else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault()
    if (!paletteItems.length) return
    paletteIndex = (paletteIndex + (e.key === 'ArrowDown' ? 1 : -1) + paletteItems.length) % paletteItems.length
    drawPalette()
    $('paletteResults').querySelector('.is-on')?.scrollIntoView({ block: 'nearest' })
  } else if (e.key === 'Enter') { e.preventDefault(); pickPalette(paletteIndex) }
})

// A notice, as Exo Launcher's: each stays 4.5 s from when it shows, none goes while the pointer is on them, and coming
// off them gives each its full time again.
const NOTICE_STAY_MS = 4500
const notices = Object.assign(document.createElement('section'), { className: 'notices' })
notices.setAttribute('aria-label', 'Notifications')
document.body.append(notices)
let noticesHeld = false
notices.addEventListener('pointerenter', () => { noticesHeld = true; for (const n of notices.children) clearTimeout(n.leaveTimer) })
notices.addEventListener('pointerleave', () => { noticesHeld = false; for (const n of notices.children) armNotice(n) })

function armNotice(n) {
  clearTimeout(n.leaveTimer)
  n.leaveTimer = setTimeout(() => {
    if (noticesHeld) return
    n.classList.add('is-leaving')
    setTimeout(() => n.remove(), 220)
  }, NOTICE_STAY_MS)
}

// A notice, for what nobody pressed (a film that stopped, a sign-in that ended). A failure stays until it's closed
// (its × top right); anything else leaves after its time.
function toast(message, bad = false) {
  const n = document.createElement('div')
  n.className = `notice exo-glass is-panel${bad ? ' is-bad' : ''}`
  n.setAttribute('role', bad ? 'alert' : 'status')
  n.innerHTML = `<span class="notice-words"></span><button type="button" class="notice-close" aria-label="Close">${icon('x')}</button>`
  n.querySelector('.notice-words').textContent = message
  n.querySelector('.notice-close').addEventListener('click', () => { n.classList.add('is-leaving'); setTimeout(() => n.remove(), 220) })
  notices.append(n)
  if (!bad) armNotice(n)
}

// ---- Opening: the library is the Jellyfin server's, so with no server signed in to, the rooms are the sign-in
// (renderSignIn). The Exo account carries your choices to your other PCs and stands in front of nothing.

function openLibrary() {
  $('app').hidden = false
  if (!location.hash) location.replace('#/home')
  return render()
}

// ---- Start

// Behind the opening (boot.js): the saved sign-in, the first screen with all of its data (the watch rows, every
// library's row) and its pictures decoded at their full size, so it shows whole when the splash lifts and nothing fills
// in after (the user: the opening is there so everything has loaded before we get in; longer if it has to be). A slow
// server holds it only up to the cap; its rows fill in after. Then, out of sight, the title pages a click away.
const FIRST_DATA_CAP_MS = 5000
const KEPT_DATA_WAIT_MS = 250
const FIRST_PICTURES_CAP_MS = 2500
const nextFrame = () => new Promise((r) => requestAnimationFrame(() => r()))
const waitMs = (ms) => new Promise((r) => setTimeout(r, Math.max(0, ms)))

async function firstScreenReady(drawing) {
  // With last time's Home kept (its rows and the watch rows), that is the screen: it draws at once, and the server's
  // fresh answers, almost always the same, only touch a row that changed, in place. Without it (a first start, a new
  // sign-in) they're waited for, up to a cap, so the screen comes in whole.
  const times = window.exoBootTimes = { start: Math.round(performance.now()) }
  watchFirst.then(() => { times.watch = Math.round(performance.now()) })
  Promise.resolve(drawing).then(() => { times.rows = Math.round(performance.now()) })
  // (A row that never answered isn't kept, and fills in when it does, rather than holding the rest.)
  const kept = !jellyfin.signedIn || route() !== 'home' || (cache.get('rows')?.some((r) => cache.get(`row:${rowKey(r)}`)) && cache.get('watch'))
  await Promise.race([Promise.allSettled([drawing, route() === 'home' ? watchFirst : null]), waitMs(kept ? KEPT_DATA_WAIT_MS : FIRST_DATA_CAP_MS)])
  times.data = Math.round(performance.now())
  await document.fonts.ready
  await nextFrame()
  await nextFrame()
  const onScreen = [...document.querySelectorAll('#view img')].filter((img) => {
    const r = img.getBoundingClientRect()
    return r.width > 0 && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth
  }).slice(0, 40)
  // A lazy picture out of sight never starts: on the first screen it loads now, so it is there with the screen.
  for (const img of onScreen) if (img.loading === 'lazy') img.loading = 'eager'
  // A backdrop's full size too (heroArt shows a small one first): put in now, so it doesn't sharpen after the splash.
  const full = (img) => {
    const src = img.dataset.full
    if (!src) return img.decode()
    delete img.dataset.full
    const hi = new Image()
    hi.src = src
    return hi.decode().then(() => { img.src = src; return img.decode() })
  }
  await Promise.race([Promise.allSettled(onScreen.map(full)), waitMs(FIRST_PICTURES_CAP_MS)])
  times.pictures = Math.round(performance.now())
}

// The title pages a click away (Continue Watching's), fetched quietly so they open at once.
async function warmTitles() {
  const going = (watchData?.continue ?? []).filter((e) => e.id).slice(0, 8)
  for (const e of going) {
    const id = e.showId ?? e.id
    if (cache.get(`meta:${id}`)) continue
    try { cache.set(`meta:${id}`, await jf('meta', { type: e.season != null ? 'series' : 'movie', id })) } catch { }
  }
}

;(async () => {
  try {
    appInfo = await app('settings')
    Object.assign(update, { latest: appInfo.update?.latest ?? null, notes: appInfo.update?.notes ?? null, installable: !!appInfo.update?.installable })
    // Whether this PC has a Jellyfin sign-in, known before the server is asked: the rooms open on it, or on the sign-in.
    jellyfin.signedIn = !!appInfo.server
  } catch { }
  const drawing = openLibrary()
  // Not over a file opened with Cinema: the first run's steps wait until the player is left.
  if (appInfo && !appInfo.onboarded) {
    if (document.body.classList.contains('mode-player')) window.addEventListener('exo:left-player', startOnboarding, { once: true })
    else startOnboarding()
  }
  readAccount()
  readJellyfin()
  try { await firstScreenReady(drawing) } catch { }
  exoBoot.reveal()
  setTimeout(warmTitles, 1200)
  // A newer version first (it was looked for as the window opened); What's New when there's none.
  setTimeout(async () => { if (!(await offerUpdate())) await maybeWhatsNew().catch(() => { }); maybeAccountOffer() }, 600)
})()
