import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

// Run the shipped shortcut handlers with only their browser/host boundaries
// replaced. Removing the input guards must make these regression cases fail.
function shortcuts(file, player) {
  const source = readFileSync(new URL(`../src/ExoCinema/wwwroot/${file}`, import.meta.url), 'utf8')
  const start = source.lastIndexOf("document.addEventListener('keydown', (e) => {")
  assert.notEqual(start, -1)
  const end = source.indexOf('\n})', start) + 3
  const calls = []
  let handler
  const context = {
    document: { addEventListener: (_, fn) => { handler = fn }, activeElement: null },
    inPlayer: () => player,
    actions: Object.fromEntries(['pause', 'mute', 'fullscreen', 'info', 'back'].map((key) => [key, () => calls.push(key)])),
    state: { volume: 50, fullscreen: false },
    seekBy: (to) => calls.push(`seek:${to}`),
    setVolume: (to) => calls.push(`volume:${to}`),
    wake: () => calls.push('wake'),
    history: { back: () => calls.push('back') },
    settingsOpen: () => false,
    openPalette: () => calls.push('search'),
  }
  vm.runInNewContext(source.slice(start, end), context, { filename: file })
  return (props = {}) => {
    calls.length = 0
    const event = {
      key: 'k', repeat: false, defaultPrevented: false,
      target: { isContentEditable: false, closest: () => null },
      preventDefault: () => calls.push('preventDefault'),
      ...props,
    }
    context.document.activeElement = event.target
    handler(event)
    return [...calls]
  }
}

for (const [file, player, key] of [['player.js', true, 'k'], ['app.js', false, '/']]) {
  for (const props of [
    { isComposing: true }, { keyCode: 229 }, { defaultPrevented: true },
  ]) {
    test(`${file} leaves composing or already handled keys alone: ${JSON.stringify(props)}`, () => {
      assert.deepEqual(shortcuts(file, player)({ key, ...props }), [])
    })
  }
  for (const tag of ['INPUT', 'TEXTAREA', 'SELECT']) {
    test(`${file} leaves ${tag} text/navigation keys alone`, () => {
      const dispatch = shortcuts(file, player)
      const target = { tagName: tag, closest: () => ({ tagName: tag }) }
      assert.deepEqual(dispatch({ key, target }), [])
      assert.deepEqual(dispatch({ key: 'Backspace', target }), [])
      assert.deepEqual(dispatch({ key: 'ArrowRight', repeat: true, target }), [])
    })
  }
  test(`${file} leaves inherited contenteditable input alone`, () => {
    assert.deepEqual(shortcuts(file, player)({ key, target: { isContentEditable: true, closest: () => null } }), [])
  })
}

test('player preserves browser/OS modifier shortcuts', () => {
  const dispatch = shortcuts('player.js', true)
  for (const modifier of ['ctrlKey', 'altKey', 'metaKey']) assert.deepEqual(dispatch({ [modifier]: true }), [])
})
test('player keeps ordinary shortcuts and arrow repeat', () => {
  const dispatch = shortcuts('player.js', true)
  assert.deepEqual(dispatch(), ['pause', 'preventDefault', 'wake'])
  assert.deepEqual(dispatch({ key: 'ArrowRight', repeat: true }), ['seek:5', 'preventDefault', 'wake'])
  assert.deepEqual(dispatch({ repeat: true }), [])
})
test('library keeps search and back navigation', () => {
  const dispatch = shortcuts('app.js', false)
  assert.deepEqual(dispatch({ key: '/' }), ['preventDefault', 'search'])
  assert.deepEqual(dispatch({ key: 'Backspace' }), ['back'])
  assert.deepEqual(dispatch({ key: 'k', ctrlKey: true }), ['preventDefault', 'search'])
})
