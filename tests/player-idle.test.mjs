import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

// The player's glass shapes (what the host draws the chrome's blur over) are sent when the chrome shows, and a frame loop
// runs for them only while the chrome is up. The clock itself has no frame loop (player-clock.test.mjs).
const source = readFileSync(new URL('../src/ExoCinema/wwwroot/player.js', import.meta.url), 'utf8').replace(/\r\n/g, '\n')
for (const mode of ['idle', 'hidden', 'library', 'visible']) {
  test(`player animation scheduling: ${mode}`, () => {
    let frames = 0, shapes = 0
    const messages = []
    const context = {
      inPlayer: () => mode !== 'library',
      document: { hidden: mode === 'hidden', querySelector: () => ({ getBoundingClientRect: () => ({ top: 500 }) }) },
      stage: { classList: { contains: () => mode === 'idle' } },
      requestAnimationFrame: () => ++frames,
      performance: { now: () => 0 },
      glassShapes: () => { shapes++; return [] },
      dpr: () => 1, half: (v) => v, innerHeight: 600,
      send: (message) => messages.push(message),
    }
    vm.runInNewContext(
      source.slice(source.indexOf('function placeGlass('), source.indexOf('// Each glass shape')) +
      source.slice(source.indexOf('let glassSent ='), source.indexOf('const half =')) +
      source.slice(source.indexOf('function sendGlass()'), source.indexOf('// Each glass shape')) +
      '\nplaceGlass();', context)
    assert.equal(frames, mode === 'visible' ? 1 : 0)
    assert.equal(shapes, mode === 'visible' ? 1 : 0)
    assert.equal(messages.length, 1)
  })
}
