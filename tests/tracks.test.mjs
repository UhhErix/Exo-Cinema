import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

const source = readFileSync(new URL('../src/ExoCinema/wwwroot/playerplus.js', import.meta.url), 'utf8')
const start = source.indexOf('function trackName(')
const end = source.indexOf('\nfunction drawTrackMenu(', start)
const context = vm.createContext({})
vm.runInContext(source.slice(start, end), context)

test('malformed file language tags do not prevent opening the track menu', () => {
  assert.equal(context.trackName({ id: 2, lang: 'pt_BR', type: 'sub' }), 'pt_BR')
  assert.equal(context.trackName({ id: 3, lang: 'not a language', type: 'audio', channels: 2 }), 'not a language · Stereo')
})
test('track names preserve normal language, title and accessibility details', () => {
  assert.equal(context.trackName({ id: 2, lang: 'en', type: 'audio', channels: 6 }), 'English · 5.1')
  assert.equal(context.trackName({ id: 3, title: 'Commentary', lang: 'en', forced: true }), 'Commentary · English · Forced')
  assert.equal(context.trackName({ id: 4 }), 'Track 4')
})
