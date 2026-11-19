// The opening (boot.css). The page's first frames are its busiest: the intro starts once two frames have drawn and a
// beat has passed. app.js calls exoBoot.reveal() when the first screen is ready; the splash always plays in full
// (MIN_MS) and never holds the app back past CAP_MS.
// The look every Exo app shares (Exo Launcher's Settings › General, %LOCALAPPDATA%\Exo\appearance.json): the host gives it
// as window.exoAppearance before this runs and posts it again when it changes (MainWindow.PageScale.cs). Light, Dark or
// Void (Match Windows is Light or Dark as Windows' apps are), Glass or Solid, and Reduce motion. With no Launcher there's
// no file: Windows decides the theme and the motion, and the material is glass.
function exoApplyAppearance(a) {
  const root = document.documentElement
  const choice = a?.appearance ?? 'system'
  root.dataset.exoTheme = choice === 'system' ? (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark') : choice
  // A choice synced from another PC (the account, 1.4.10) is kept under exo:pref:*; it wins over the Launcher's file here.
  const synced = (key) => { try { return localStorage.getItem(`exo:pref:${key}`) } catch { return null } }
  const material = (synced('material') ?? a?.material) === 'solid' ? 'solid' : 'glass'
  root.dataset.material = material
  // Exo Launcher's name for it, which the Exo Profile's styles (profile.css, its copy) read.
  root.dataset.exoGlass = material
  const motion = synced('motion')
  const reduce = motion != null ? motion === 'on' : typeof a?.reduceMotion === 'boolean' ? a.reduceMotion : matchMedia('(prefers-reduced-motion: reduce)').matches
  if (reduce) root.dataset.exoMotion = 'reduce'
  else delete root.dataset.exoMotion
  window.exoAppearance = a ?? null
}
exoApplyAppearance(window.exoAppearance)
for (const query of ['(prefers-color-scheme: light)', '(prefers-reduced-motion: reduce)'])
  matchMedia(query).addEventListener('change', () => exoApplyAppearance(window.exoAppearance))

const exoBoot = (() => {
  const INTRO_SETTLE_MS = 120
  const MIN_MS = 900
  const CAP_MS = 8000
  const startedAt = performance.now()
  const root = document.getElementById('boot')
  let revealed = false

  // The opening's steps go to app.log (MainWindow › mark), to see where a slow start spends its time.
  const mark = (what) => { try { window.chrome.webview.postMessage({ cmd: 'mark', what }) } catch { } }
  mark('page-start')
  requestAnimationFrame(() => requestAnimationFrame(() => { mark('page-paint'); setTimeout(() => root.classList.add('is-playing'), INTRO_SETTLE_MS) }))

  function reveal() {
    if (revealed || !root) return
    revealed = true
    mark('page-ready')
    const wait = Math.max(0, MIN_MS - (performance.now() - startedAt))
    setTimeout(() => {
      const mark = root.querySelector('.exo-boot-icon')
      if (document.documentElement.dataset.exoMotion === 'reduce' || typeof root.animate !== 'function') { root.remove(); return }
      // Exo Cinema, already drawn underneath, comes up as the mark sinks back and fades: transform and opacity only,
      // so it stays on the compositor and sharp (it shrinks, never grows past its raster). Exactly as Exo Launcher's
      // BootSplash: both on the flight time and the ease-out (DESIGN.md › Motion), the fade a quick time behind the mark.
      const styles = getComputedStyle(document.documentElement)
      const ms = (name, fallback) => {
        const value = styles.getPropertyValue(name).trim()
        const n = parseFloat(value)
        return Number.isFinite(n) ? (value.endsWith('ms') ? n : n * 1000) : fallback
      }
      const easing = styles.getPropertyValue('--ease-out').trim() || 'ease-out'
      const flight = ms('--dur-flight', 300)
      mark.animate([
        { transform: 'translateY(0) scale(1)', opacity: 1 },
        { transform: 'translateY(-6px) scale(0.86)', opacity: 0 },
      ], { duration: flight, easing, fill: 'forwards' })
      const fade = root.animate([{ opacity: 1 }, { opacity: 0 }], { duration: flight, delay: ms('--dur-quick', 120), easing, fill: 'forwards' })
      fade.onfinish = () => root.remove()
    }, wait)
  }

  // Whatever happens behind it, the splash lifts by the cap.
  setTimeout(reveal, CAP_MS)
  return { reveal, startedAt, mark }
})()
