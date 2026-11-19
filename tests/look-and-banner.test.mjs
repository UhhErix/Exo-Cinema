import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

// Two page rules cut out of the page and run on their own (1.4.10).
// - The look (boot.js exoApplyAppearance): a Glass or Solid and Reduce motion choice synced from another PC wins over the
//   Launcher file here, and a PC with no synced choice keeps the Launcher look. Ways it could fail with no error:
//   the synced choice is ignored, or the Launcher look is lost when nothing is synced.
// - The account banner (app.js accountBannerDue): shown while signed out, hidden for a week after ×, never once signed in.
const read = (file) => readFileSync(new URL(`../src/ExoCinema/wwwroot/${file}`, import.meta.url), 'utf8').split('\r\n').join('\n')

function cut(source, start, end) {
  const from = source.indexOf(start)
  assert.notEqual(from, -1, start)
  return source.slice(from, source.indexOf(end, from) + end.length)
}

function appearance(stored, launcher, systemDark = false) {
  const root = { dataset: {} }
  const storage = { getItem: (key) => (key in stored ? stored[key] : null), removeItem() {}, setItem() {} }
  const context = {
    document: { documentElement: root },
    localStorage: storage,
    matchMedia: () => ({ matches: systemDark, addEventListener() {} }),
    window: {},
  }
  vm.runInNewContext(cut(read('boot.js'), 'function exoApplyAppearance(a) {', '\n}\n'), context, { filename: 'boot.js' })
  context.exoApplyAppearance(launcher)
  return root.dataset
}

test('a Solid choice synced from another PC is drawn here, over the Launcher Glass', () => {
  const look = appearance({ 'exo:pref:material': 'solid' }, { appearance: 'dark', material: 'glass', reduceMotion: false })
  assert.equal(look.material, 'solid')
  assert.equal(look.exoGlass, 'solid')
})

test('Reduce motion synced on is drawn here, over the Launcher off', () => {
  const look = appearance({ 'exo:pref:motion': 'on' }, { appearance: 'dark', material: 'glass', reduceMotion: false })
  assert.equal(look.exoMotion, 'reduce')
})

test('Reduce motion synced off clears the Launcher reduce', () => {
  const look = appearance({ 'exo:pref:motion': 'off' }, { appearance: 'dark', material: 'glass', reduceMotion: true })
  assert.equal(look.exoMotion, undefined)
})

test('with nothing synced, the Launcher look is drawn as before', () => {
  const look = appearance({}, { appearance: 'dark', material: 'solid', reduceMotion: true })
  assert.equal(look.material, 'solid')
  assert.equal(look.exoMotion, 'reduce')
})

// The banner rule.
function bannerDue(state, now, hiddenAt) {
  const source = read('app.js')
  const body = cut(source, 'function accountBannerDue() {', '\n}\n')
  const constant = source.split('\n').find((line) => line.startsWith('const BANNER_HIDE_MS = ')) + '\n'
  const context = {
    exo: { enabled: state.enabled },
    acct: { read: state.read, signedIn: state.signedIn },
    localStorage: {
      getItem: (key) => {
        if (key !== 'exo:account-banner-hidden') throw new Error('unexpected key ' + key);
        if (state.storageThrows) throw new Error('denied');
        return hiddenAt === undefined ? null : String(hiddenAt)
      },
    },
    Date: { now: () => now },
    Number,
  }
  vm.runInNewContext(constant + body, context, { filename: 'app.js' })
  return context.accountBannerDue()
}

const DAY = 24 * 3600 * 1000
const NOW = 1_800_000_000_000

test('a signed-out PC whose account service answers is offered the banner', () => {
  assert.equal(bannerDue({ enabled: true, read: true, signedIn: false }, NOW), true)
})

test('a signed-in PC never sees the banner', () => {
  assert.equal(bannerDue({ enabled: true, read: true, signedIn: true }, NOW), false)
})

test('the banner stays hidden for a week after ×, then comes back', () => {
  const state = { enabled: true, read: true, signedIn: false }
  assert.equal(bannerDue(state, NOW, NOW - 6 * DAY), false);
  assert.equal(bannerDue(state, NOW, NOW - 8 * DAY), true)
})

test('before the account is read, or with no account service, there is no banner', () => {
  assert.equal(bannerDue({ enabled: true, read: false, signedIn: false }, NOW), false)
  assert.equal(bannerDue({ enabled: false, read: true, signedIn: false }, NOW), false)
})

test('when browser storage is refused, the banner is shown rather than the page failing', () => {
  assert.equal(bannerDue({ enabled: true, read: true, signedIn: false, storageThrows: true }, NOW), true)
})
