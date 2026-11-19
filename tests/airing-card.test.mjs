import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

// Airing soon's card, as app.js draws it: only the season and episode the server gave, and nothing for a line with
// neither (never "S0 E0" for an episode the server didn't number).
function airingCard(entry) {
  // app.js is CRLF on disk; the function is cut out of it with LF line endings.
  const source = readFileSync(new URL('../src/ExoCinema/wwwroot/app.js', import.meta.url), 'utf8').split('\r\n').join('\n')
  const start = source.indexOf('function airingCard(e) {')
  assert.notEqual(start, -1)
  const end = source.indexOf('\n}\n', start) + 3
  const context = { esc: (s) => String(s ?? ''), openAttr: () => '', still: () => '' }
  vm.runInNewContext(source.slice(start, end), context, { filename: 'app.js' })
  return context.airingCard(entry)
}

const sub = (entry) => /<span class="card-sub">([^<]*)<\/span>/.exec(airingCard({ title: 'The Show', showId: 'jf:s1', ...entry }))[1]

test('a season and episode the server gives are shown with the day and time', () => {
  assert.equal(sub({ season: 1, episode: 2, airDay: 'Today', airTime: '21:30' }), 'S1 E2 · Today 21:30')
})

test('a missing season or episode is left out, not shown as zero', () => {
  assert.equal(sub({ season: null, episode: null, airDay: 'Tomorrow', airTime: null }), 'Tomorrow')
  assert.equal(sub({ season: null, episode: 4, airDay: 'Tomorrow' }), 'E4 · Tomorrow')
  assert.equal(sub({ season: 2, episode: null, airDay: 'Sun' }), 'S2 · Sun')
})

test('season 0 is a real season (specials) and is shown', () => {
  assert.equal(sub({ season: 0, episode: 3, airDay: 'Sun' }), 'S0 E3 · Sun')
})

test('nothing known gives no line, and never S0 E0', () => {
  assert.equal(sub({ season: null, episode: null, airDay: null, airTime: null }), '')
  assert.doesNotMatch(airingCard({ title: 'x', season: null, episode: null }), /S0 E0/)
})
