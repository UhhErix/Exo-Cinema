import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

// Every script the page loads parses. One that doesn't never runs, and the page then fails far from the cause (a name
// from it "is not defined" in the next script): caught here on 2026-11-12, when an edit left a stray brace in app.js and
// every other test still passed.
const folder = new URL('../src/ExoCinema/wwwroot/', import.meta.url)

for (const file of readdirSync(folder).filter((f) => f.endsWith('.js'))) {
  test(`${file} parses`, () => {
    assert.doesNotThrow(() => new vm.Script(readFileSync(new URL(file, folder), 'utf8'), { filename: file }))
  })
}

// The page's scripts share one global scope, in index.html's order: each name a script uses at load is defined by then.
test('every script index.html names exists', () => {
  const html = readFileSync(new URL('index.html', folder), 'utf8')
  const named = [...html.matchAll(/<script src="([^"]+)"/g)].map((m) => m[1])
  assert.ok(named.length > 5)
  for (const file of named) assert.doesNotThrow(() => readFileSync(new URL(file, folder)), file)
})

// Jellyfin only (1.5): a title kept from before, when Cinema played from other sources, has no id. Its card shows its
// name and looks the name up on the server; it never asks for a picture from anywhere.
test('a title kept from before 1.5 is drawn by its name and searches the server', () => {
  const source = readFileSync(new URL('app.js', folder), 'utf8').split('\r\n').join('\n')
  const cut = (start, end) => { const a = source.indexOf(start); assert.notEqual(a, -1, start); return source.slice(a, source.indexOf(end, a) + end.length) }
  const context = { result: null }
  vm.runInNewContext(`
    const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c])
    ${cut('function titleParts(e) {', '\n}')}
    ${cut('function awayCard(e, shape) {', '\n}')}
    result = [awayCard({ pid: 'imdb:tt0903747:s01e02', title: "Breaking Bad · S1:E2 Cat's in the Bag...", source: 'cinema' }, 'wide'),
      awayCard({ pid: 'tmdb:movie:603', title: 'The Matrix', source: 'cinema' }, 'poster')]
  `, context)
  const [episode, film] = context.result
  assert.match(episode, /data-find="Breaking Bad"/)
  assert.match(episode, /<span class="card-sub">S1:E2<\/span>/)
  assert.match(film, /data-find="The Matrix"/)
  for (const card of context.result) {
    assert.doesNotMatch(card, /<img|https?:/)
    assert.doesNotMatch(card, /data-open|data-play/)
  }
})

// The first run's account step shows a new account's recovery code before it says Signed in, and Continue waits for it
// to be typed back. Until 1.5 the step drew "Signed in" the moment the account was made: the one-time code was never
// shown there (seen 2026-11-12, creating an account in a first run against a stand-in service).
test('the first run shows a new account its recovery code before going on', () => {
  const source = readFileSync(new URL('onboarding.js', folder), 'utf8')
  const step = source.slice(source.indexOf("case 'account':"), source.indexOf("break", source.indexOf("case 'account':")))
  assert.match(step, /exo\.account && !acct\.code\s*\?/, 'the signed-in row is drawn only once the code is put away')
  assert.match(step, /: accountRows\(\)/, 'the account rows (the code screen among them) are drawn otherwise')
  assert.match(step, /cta\.disabled = \(!exo\.account && !acct\.offline\) \|\| !!acct\.code/, 'Continue waits for the code')
  // And the rows put the code first, whoever is signed in.
  const app = readFileSync(new URL('app.js', folder), 'utf8')
  const rows = app.slice(app.indexOf('function accountRows() {'), app.indexOf('function acctForm('))
  assert.ok(rows.indexOf('if (acct.code) return acctCodeRows()') < rows.indexOf('if (acct.signedIn)'), 'the code screen comes before the signed-in rows')
})