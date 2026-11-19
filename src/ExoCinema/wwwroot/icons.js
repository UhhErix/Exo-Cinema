// Exo's icons are Tabler's (tabler.io/icons, MIT), at Exo's 1.5 line, the same release and picks as Exo Launcher's (ui/scripts/tabler-map.mjs).
const GLYPHS = {
  'player-play-filled': [1, 'M6 4v16a1 1 0 0 0 1.524.852l13-8a1 1 0 0 0 0-1.704l-13-8A1 1 0 0 0 6 4'],
  'player-pause-filled': [1, 'M9 4H7a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h2a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2m8 0h-2a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h2a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2'],
  'arrows-maximize': [0, 'M16 4h4v4m-6 2l6-6M8 20H4v-4m0 4l6-6m6 6h4v-4m-6-2l6 6M8 4H4v4m0-4l6 6'],
  'arrows-minimize': [0, 'M5 9h4V5M3 3l6 6m-4 6h4v4m-6 2l6-6m10-6h-4V5m0 4l6-6m-2 12h-4v4m0-4l6 6'],
  'volume': [0, 'M15 8a5 5 0 0 1 0 8m2.7-11a9 9 0 0 1 0 14M6 15H4a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1h2l3.5-4.5A.8.8 0 0 1 11 5v14a.8.8 0 0 1-1.5.5z'],
  'volume-off': [0, 'M15 8a5 5 0 0 1 1.912 4.934m-1.377 2.602A5 5 0 0 1 15 16m2.7-11a9 9 0 0 1 2.362 11.086m-1.676 2.299A9 9 0 0 1 17.7 19M9.069 5.054L9.5 4.5A.8.8 0 0 1 11 5v2m0 4v8a.8.8 0 0 1-1.5.5L6 15H4a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1h2l1.294-1.664M3 3l18 18'],
  'chevron-left': [0, 'm15 6l-6 6l6 6'],
  'chevron-right': [0, 'm9 6l6 6l-6 6'],
  'chevron-up': [0, 'm6 15l6-6l6 6'],
  'star-filled': [1, 'm8.243 7.34l-6.38.925l-.113.023a1 1 0 0 0-.44 1.684l4.622 4.499l-1.09 6.355l-.013.11a1 1 0 0 0 1.464.944l5.706-3l5.693 3l.1.046a1 1 0 0 0 1.352-1.1l-1.091-6.355l4.624-4.5l.078-.085a1 1 0 0 0-.633-1.62l-6.38-.926l-2.852-5.78a1 1 0 0 0-1.794 0z'],
  'arrow-up': [0, 'M12 5v14m6-8l-6-6m-6 6l6-6'],
  'arrow-down': [0, 'M12 5v14m6-6l-6 6m-6-6l6 6'],
  'plus': [0, 'M12 5v14m-7-7h14'],
  'gallery': [0, 'M9 15H6a3 3 0 0 1-3-3V6a3 3 0 0 1 3-3h6a3 3 0 0 1 3 3v3', 'M9 12a3 3 0 0 1 3-3h6a3 3 0 0 1 3 3v6a3 3 0 0 1-3 3h-6a3 3 0 0 1-3-3zm-6 0l2.296-2.296a2.41 2.41 0 0 1 3.408 0L9 10', 'M14 13.5v3l2.5-1.5zM7 6v.01'],
  'text': [0, 'M4 20h3m7 0h7M6.9 15h6.9m-3.6-8.7L16 20M5 20l6-16h2l7 16'],
  'cover': [0, 'M15 8h.01M3 6a3 3 0 0 1 3-3h12a3 3 0 0 1 3 3v12a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3z', 'm3 16l5-5c.928-.893 2.072-.893 3 0l5 5', 'm14 14l1-1c.928-.893 2.072-.893 3 0l3 3'],
  'poll': [0, 'M3 13a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1zm12-4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1h-4a1 1 0 0 1-1-1zM9 5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1h-4a1 1 0 0 1-1-1zM4 20h14'],
  'trophy': [0, 'M8 21h8m-4-4v4M7 4h10m0 0v8a5 5 0 0 1-10 0V4M3 9a2 2 0 1 0 4 0a2 2 0 1 0-4 0m14 0a2 2 0 1 0 4 0a2 2 0 1 0-4 0'],
  'clock': [0, 'M3 12a9 9 0 1 0 18 0a9 9 0 0 0-18 0', 'M12 7v5l3 3'],
  'devices': [0, 'M5 5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2z', 'M9 14a3 3 0 1 0 6 0a3 3 0 1 0-6 0m3-7v.01'],
  'award': [0, 'M6 9a6 6 0 1 0 12 0A6 6 0 1 0 6 9', 'm12 15l3.4 5.89l1.598-3.233l3.598.232l-3.4-5.889M6.802 12l-3.4 5.89L7 17.657l1.598 3.232l3.4-5.889'],
  'cpu': [0, 'M5 6a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1z', 'M9 9h6v6H9zm-6 1h2m-2 4h2m5-11v2m4-2v2m7 5h-2m2 4h-2m-5 7v-2m-4 2v-2'],
  'mouse': [0, 'M6 7a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v10a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4zm6 0v4'],
  'undo': [0, 'M9 14L5 10l4-4', 'M5 10h11a4 4 0 1 1 0 8h-1'],
  'redo': [0, 'm15 14l4-4l-4-4', 'M19 10H8a4 4 0 1 0 0 8h1'],
  'copy': [0, 'M7 9.667A2.667 2.667 0 0 1 9.667 7h8.666A2.667 2.667 0 0 1 21 9.667v8.666A2.667 2.667 0 0 1 18.333 21H9.667A2.667 2.667 0 0 1 7 18.333z', 'M4.012 16.737A2 2 0 0 1 3 15V5c0-1.1.9-2 2-2h10c.75 0 1.158.385 1.5 1'],
  'trash': [0, 'M4 7h16m-10 4v6m4-6v6M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2l1-12M9 7V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v3'],
  'chevron-down': [0, 'm6 9l6 6l6-6'],
  'puzzle': [0, 'M4 7h3a1 1 0 0 0 1-1V5a2 2 0 0 1 4 0v1a1 1 0 0 0 1 1h3a1 1 0 0 1 1 1v3a1 1 0 0 0 1 1h1a2 2 0 0 1 0 4h-1a1 1 0 0 0-1 1v3a1 1 0 0 1-1 1h-3a1 1 0 0 1-1-1v-1a2 2 0 0 0-4 0v1a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-3a1 1 0 0 1 1-1h1a2 2 0 0 0 0-4H4a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1'],
  'info': [0, 'M3 12a9 9 0 1 0 18 0a9 9 0 0 0-18 0m9-3h.01M11 12h1v4h1'],
  'check': [0, 'm5 12l5 5L20 7'],
  'minus': [0, 'M5 12h14'],
  'x': [0, 'M18 6L6 18M6 6l12 12'],
  'square': [0, 'M3 5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'],
  'box-multiple': [0, 'M7 5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2z', 'M17 17v2a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2h2'],
  'search': [0, 'M3 10a7 7 0 1 0 14 0a7 7 0 1 0-14 0m18 11l-6-6'],
  'settings': [0, 'M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 0 0 2.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 0 0 1.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 0 0-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 0 0-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 0 0-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 0 0-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 0 0 1.066-2.573c-.94-1.543.826-3.31 2.37-2.37c1 .608 2.296.07 2.572-1.065', 'M9 12a3 3 0 1 0 6 0a3 3 0 0 0-6 0'],
  'home': [0, 'M5 12H3l9-9l9 9h-2M5 12v7a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-7', 'M9 21v-6a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v6'],
  'home-filled': [1, 'm12.707 2.293l9 9c.63.63.184 1.707-.707 1.707h-1v6a3 3 0 0 1-3 3h-1v-7a3 3 0 0 0-2.824-2.995L13 12h-2a3 3 0 0 0-3 3v7H7a3 3 0 0 1-3-3v-6H3c-.89 0-1.337-1.077-.707-1.707l9-9a1 1 0 0 1 1.414 0M13 14a1 1 0 0 1 1 1v7h-4v-7a1 1 0 0 1 .883-.993L11 14z'],
  'movie': [0, 'M4 6a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2zm4-2v16m8-16v16M4 8h4m-4 8h4m-4-4h16m-4-4h4m-4 8h4'],
  'device-tv': [0, 'M3 9a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zm13-6l-4 4l-4-4'],
  'device-tv-filled': [1, 'M8.707 2.293L12 5.585l3.293-3.292a1 1 0 0 1 1.32-.083l.094.083a1 1 0 0 1 0 1.414L14.414 6H19a3 3 0 0 1 3 3v9a3 3 0 0 1-3 3H5a3 3 0 0 1-3-3V9a3 3 0 0 1 3-3h4.585L7.293 3.707a1 1 0 0 1 1.414-1.414'],
  'sparkles': [0, 'M16 18a2 2 0 0 1 2 2a2 2 0 0 1 2-2a2 2 0 0 1-2-2a2 2 0 0 1-2 2m0-12a2 2 0 0 1 2 2a2 2 0 0 1 2-2a2 2 0 0 1-2-2a2 2 0 0 1-2 2M9 18a6 6 0 0 1 6-6a6 6 0 0 1-6-6a6 6 0 0 1-6 6a6 6 0 0 1 6 6'],
  'door-exit': [0, 'M14 8V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h7a2 2 0 0 0 2-2v-2M9 12h12l-3-3m0 6l3-3'],
  'adjustments-horizontal': [0, 'M12 6a2 2 0 1 0 4 0a2 2 0 1 0-4 0M4 6h8m4 0h4M6 12a2 2 0 1 0 4 0a2 2 0 1 0-4 0m-2 0h2m4 0h10m-5 6a2 2 0 1 0 4 0a2 2 0 1 0-4 0M4 18h11m4 0h1'],
  'player-skip-forward-filled': [1, 'M2 5v14c0 .86 1.012 1.318 1.659.753l8-7a1 1 0 0 0 0-1.506l-8-7C3.012 3.682 2 4.141 2 5m11 0v14c0 .86 1.012 1.318 1.659.753l8-7a1 1 0 0 0 0-1.506l-8-7C14.012 3.682 13 4.141 13 5'],
  'user': [0, 'M8 7a4 4 0 1 0 8 0a4 4 0 0 0-8 0M6 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2'],
  'lock': [0, 'M5 13a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2zM11 16a1 1 0 1 0 2 0a1 1 0 0 0-2 0m-3-5V7a4 4 0 1 1 8 0v4'],
  'bookmark': [0, 'M18 7v14l-6-4l-6 4V7a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4'],
  'bookmark-filled': [1, 'M14 2a5 5 0 0 1 5 5v14a1 1 0 0 1-1.555.832L12 18.202l-5.444 3.63a1 1 0 0 1-1.55-.72L5 21V7a5 5 0 0 1 5-5z'],
  'history': [0, 'M12 8v4l2 2', 'M3.05 11a9 9 0 1 1 .5 4m-.5 5v-5h5'],
  'mail': [0, 'M3 7a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z', 'm3 7l9 6l9-6'],
  'flask': [0, 'M9 3h6m-5 6h4m-4-6v6L6 20a.7.7 0 0 0 .5 1h11a.7.7 0 0 0 .5-1L14 9V3'],
  'user-circle': [0, 'M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0', 'M9 10a3 3 0 1 0 6 0a3 3 0 1 0-6 0m-2.832 8.849A4 4 0 0 1 10 16h4a4 4 0 0 1 3.834 2.855'],
}

// The ways into the Exo account, as Exo Launcher draws them (ui/src/brand/icons.tsx): Google's four-color G, and
// Apple's logo in the text's color, as Sign in with Apple's own button draws it.
const MARKS = {
  google: `<svg width="16" height="16" viewBox="0 0 48 48" aria-hidden="true"><path fill="#EA4335" d="M24 9.5c3.54 0 6.71 1.22 9.21 3.6l6.85-6.85C35.9 2.38 30.47 0 24 0 14.62 0 6.51 5.38 2.56 13.22l7.98 6.19C12.43 13.72 17.74 9.5 24 9.5z"/><path fill="#4285F4" d="M46.98 24.55c0-1.57-.15-3.09-.38-4.55H24v9.02h12.94c-.58 2.96-2.26 5.48-4.78 7.18l7.73 6c4.51-4.18 7.09-10.36 7.09-17.65z"/><path fill="#FBBC05" d="M10.53 28.59c-.48-1.45-.76-2.99-.76-4.59s.27-3.14.76-4.59l-7.98-6.19C.92 16.46 0 20.12 0 24c0 3.88.92 7.54 2.56 10.78l7.97-6.19z"/><path fill="#34A853" d="M24 48c6.48 0 11.93-2.13 15.89-5.81l-7.73-6c-2.15 1.45-4.92 2.3-8.16 2.3-6.26 0-11.57-4.22-13.47-9.91l-7.98 6.19C6.51 42.62 14.62 48 24 48z"/></svg>`,
  apple: `<svg width="16" height="16" viewBox="0 0 814 1000" fill="currentColor" aria-hidden="true"><path d="M788.1 340.9c-5.8 4.5-108.2 62.2-108.2 190.5 0 148.4 130.3 200.9 134.2 202.2-.6 3.2-20.7 71.9-68.7 141.9-42.8 61.6-87.5 123.1-155.5 123.1s-85.5-39.5-164-39.5c-76.5 0-103.7 40.8-165.9 40.8s-105.6-57-155.5-127C46.7 790.7 0 663 0 541.8c0-194.4 126.4-297.5 250.8-297.5 66.1 0 121.2 43.4 162.7 43.4 39.5 0 101.1-46 176.3-46 28.5 0 130.9 2.6 198.3 99.2zm-234-181.5c31.1-36.9 53.1-88.1 53.1-139.3 0-7.1-.6-14.3-1.9-20.1-50.6 1.9-110.8 33.7-147.1 75.8-28.5 32.4-55.1 83.6-55.1 135.5 0 7.8 1.3 15.6 1.9 18.1 3.2.6 8.4 1.3 13.6 1.3 45.4 0 102.5-30.4 135.5-71.3z"/></svg>`,
}

function glyph(svg, name) {
  const [filled, ...paths] = GLYPHS[name]
  svg.setAttribute('viewBox', '0 0 24 24')
  svg.setAttribute('aria-hidden', 'true')
  svg.dataset.glyph = name
  svg.innerHTML = paths.map((d) => filled
    ? `<path fill="currentColor" d="${d}"/>`
    : `<path fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" d="${d}"/>`).join('')
}

function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg')
  glyph(svg, name)
  return svg.outerHTML
}

document.querySelectorAll('svg[data-glyph]').forEach((s) => glyph(s, s.dataset.glyph))

// Light from the press: glass lights from within where it is pressed (glass.css › ::after).
document.addEventListener('pointerdown', (event) => {
  const glass = event.target.closest?.('.exo-glass')
  if (!glass) return
  const box = glass.getBoundingClientRect()
  glass.style.setProperty('--glass-x', `${event.clientX - box.left}px`)
  glass.style.setProperty('--glass-y', `${event.clientY - box.top}px`)
}, { capture: true, passive: true })

// Focus rings only for moving with Tab: a field clicked into, or a button clicked and then a key pressed (Space to
// pause, the arrows to seek), wore Chromium's ring though nobody was tabbing. Tab turns them on; the pointer, off.
// Arrows, Home and End inside a menu, list or tab row move the focus too (a menu opened from a click focuses its first
// row, and arrowing through it showed nothing): they turn rings on there, never in the player.
document.addEventListener('keydown', (event) => {
  const moving = /^(Arrow(Up|Down|Left|Right)|Home|End)$/.test(event.key) &&
    document.activeElement?.closest('[role="menu"], [role="listbox"], [role="tablist"], [role="radiogroup"], .palette, #sources')
  if (event.key === 'Tab' || moving) document.documentElement.classList.add('is-tabbing')
}, true)
document.addEventListener('pointerdown', () => document.documentElement.classList.remove('is-tabbing'), true)

// Focus the page moves by itself (a sheet, sign-in, the first run) lands only while Exo Cinema is the active window:
// moving it in a window that just opened in the background brought that window to the front, over a game. Otherwise
// it lands when the window is next activated, if nothing else took it.
function focusWhenActive(el) {
  if (document.hasFocus()) { el.focus({ preventScroll: true }); return }
  window.addEventListener('focus', () => { if (el.isConnected && !el.closest('[hidden], [inert]') && document.activeElement === document.body) el.focus({ preventScroll: true }) }, { once: true })
}

// Confirm in place, as Exo Launcher's ConfirmButton: no dialog. The first press turns the control into its question
// ("Sign out?", in Bad for what can't be taken back), its width springing to the new words while they fade in; a second
// press, Enter or Space confirms. Escape (only that: the sheet or menu it sits in stays), a press elsewhere, focus
// leaving or 4 s puts it back. Returns true when this press confirms, false when it only asked.
const confirming = new Map()
let confirmVoice = null

function confirmInPlace(el, question, danger = true) {
  if (confirming.has(el)) { confirming.get(el)(); return true }
  const before = el.innerHTML
  const words = [...el.childNodes].find((n) => n.nodeType === 3 && n.textContent.trim())
  morphTo(el, () => {
    if (words) words.replaceWith(Object.assign(document.createElement('span'), { className: 'confirm-words', textContent: question }))
    else el.textContent = question
  })
  el.classList.add('is-armed')
  el.classList.toggle('is-danger', danger)
  const away = (e) => { if (!el.contains(e.target)) putBack() }
  const left = (e) => { if (!el.contains(e.relatedTarget)) putBack() }
  const key = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); putBack() } }
  const timer = setTimeout(() => putBack(), 4000)
  function putBack() {
    if (!confirming.has(el)) return
    confirming.delete(el)
    clearTimeout(timer)
    document.removeEventListener('pointerdown', away, true)
    el.removeEventListener('focusout', left)
    el.removeEventListener('keydown', key, true)
    el.classList.remove('is-armed', 'is-danger')
    if (el.isConnected) morphTo(el, () => { el.innerHTML = before })
    else el.innerHTML = before
  }
  confirming.set(el, putBack)
  document.addEventListener('pointerdown', away, true)
  el.addEventListener('focusout', left)
  el.addEventListener('keydown', key, true)
  // One polite live region for every question, so each is read once.
  confirmVoice ??= Object.assign(document.body.appendChild(document.createElement('div')), { className: 'sr-only' })
  confirmVoice.setAttribute('aria-live', 'polite')
  confirmVoice.textContent = question
  return false
}

// The control's width springs from its old words to its new ones (--dur-move on --ease-spring) and the new words fade
// in (--dur-base on --ease-out); under Reduce motion the width snaps and only the fade stays. A control as wide as its
// row (a menu row) keeps its width.
function morphTo(el, swap) {
  const from = el.getBoundingClientRect().width
  swap()
  const to = el.getBoundingClientRect().width
  const css = getComputedStyle(document.documentElement)
  const still = document.documentElement.dataset.exoMotion === 'reduce'
  if (!still && Math.abs(to - from) > 0.5) el.animate([{ width: `${from}px` }, { width: `${to}px` }], { duration: 220, easing: css.getPropertyValue('--ease-spring').trim() || 'ease-out' })
  // The words fade, not the control: a control of glass fading whole would show its tint before its glass.
  const words = el.querySelector('.confirm-words')
  if (words) words.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 160, easing: css.getPropertyValue('--ease-out').trim() || 'ease-out' })
}

// In the background (another window focused, or minimized) the page's loops pause (app.css › data-exo-inactive).
const markInactive = () => { document.documentElement.dataset.exoInactive = String(document.hidden || !document.hasFocus()) }
for (const type of ['focus', 'blur']) window.addEventListener(type, markInactive)
document.addEventListener('visibilitychange', markInactive)
markInactive()
