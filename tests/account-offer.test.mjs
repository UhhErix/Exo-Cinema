import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'

// The "Exo now has accounts" sheet's one rule (app.js accountOfferDue), cut out of app.js and run on its own.
// Ways it could fail with no error on either side: a first run is offered the sheet it was never meant for; an upgrader
// is offered it twice; a signed-in or offline PC is nagged; the sheet lands on top of What's New, an update or the player.
function accountOfferDue(info, signedIn, enabled, otherUp) {
  // app.js is CRLF on disk; the function is cut out of it with LF line endings.
  const source = readFileSync(new URL('../src/ExoCinema/wwwroot/app.js', import.meta.url), 'utf8').split('\r\n').join('\n')
  const start = source.indexOf('function accountOfferDue(')
  assert.notEqual(start, -1)
  const end = source.indexOf('\n}\n', start) + 3
  const context = {}
  vm.runInNewContext(source.slice(start, end), context, { filename: 'app.js' })
  return context.accountOfferDue(info, signedIn, enabled, otherUp)
}

const upgrader = { onboarded: true, accountOffered: false, version: '1.4.8' }

test('an upgrader who is signed out and reachable is offered the sheet once', () => {
  assert.equal(accountOfferDue(upgrader, false, true, false), true)
})

test('an upgrader already offered is not offered again', () => {
  assert.equal(accountOfferDue({ ...upgrader, accountOffered: true }, false, true, false), false)
})

test('a first run (not yet onboarded) is not offered the sheet', () => {
  assert.equal(accountOfferDue({ ...upgrader, onboarded: false }, false, true, false), false)
})

test('a signed-in PC is not offered the sheet', () => {
  assert.equal(accountOfferDue(upgrader, true, true, false), false)
})

test('an unreachable account service (not enabled) is not offered the sheet', () => {
  assert.equal(accountOfferDue(upgrader, false, false, false), false)
})

test('the sheet waits while another sheet, onboarding or the player is up', () => {
  assert.equal(accountOfferDue(upgrader, false, true, true), false)
})

test('no settings yet is not offered the sheet', () => {
  assert.equal(accountOfferDue(undefined, false, true, false), false)
})
