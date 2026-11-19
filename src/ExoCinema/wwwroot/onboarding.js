// The first run, as Exo Launcher's: Welcome, the Exo account (while there's an account service to reach) and Jellyfin,
// the server Cinema's films come from. Continue keeps what is shown; a PC that leaves the Jellyfin step without a
// sign-in finds the same sign-in as its rooms (app.js › renderSignIn). Signing in to Exo brings your choices from your
// other PCs.

const ob = { active: false, step: 0 }
const obSteps = () => ['welcome', ...(exo.enabled ? ['account'] : []), 'jellyfin']

function startOnboarding() {
  ob.active = true
  document.body.classList.add('is-onboarding')
  $('onboarding').hidden = false
  drawOb(0, 0)
}

function drawOb(n, dir) {
  const steps = obSteps()
  ob.step = Math.min(n, steps.length - 1)
  const name = steps[ob.step]
  const step = $('obStep')
  $('onboarding').classList.toggle('is-welcome', name === 'welcome')
  $('obProgress').style.setProperty('--p', String(ob.step / (steps.length - 1)))
  $('obProgress').setAttribute('aria-valuemax', String(steps.length - 1))
  $('obProgress').setAttribute('aria-valuenow', String(ob.step))
  $('obProgress').setAttribute('aria-valuetext', `Step ${ob.step} of ${steps.length - 1}`)
  $('obBack').hidden = ob.step === 0
  note('')
  const cta = $('obNext')
  cta.disabled = false
  cta.textContent = 'Continue'
  // A redraw of the same step (the server answered, the account signed in) keeps the keys in the field being typed in.
  const typing = dir === 0 && step.contains(document.activeElement) ? document.activeElement.dataset?.jellyfinField : null

  const title = (words) => `<h1 class="ob-title" tabindex="-1">${words}</h1>`
  switch (name) {
    case 'welcome':
      step.innerHTML = `<img class="ob-logo" src="logo.svg" alt="" draggable="false">${title('Welcome to Exo Cinema')}`
      cta.textContent = 'Get started'
      break
    case 'jellyfin':
      step.innerHTML = `${title('Jellyfin')}<div class="ob-body">${jellyfinForm()}
        <div class="ob-links"><button type="button" class="acct-link" data-link="aiostreams-setup">Set up AIOStreams</button>
          <button type="button" class="acct-link" data-link="publicmetadb">Get PublicMetaDB</button></div></div>`
      if (typing) step.querySelector(`[data-jellyfin-field="${typing}"]`)?.focus({ preventScroll: true })
      break
    case 'account':
      // A new account's recovery code is shown here, once, before the step says Signed in: until 1.5 the step went
      // straight to Signed in, and someone who made their account in the first run never saw their code.
      step.innerHTML = `${title('Exo account')}<div class="ob-body">${exo.account && !acct.code
        ? `<div class="set-group is-lined"><div class="set-row"><span class="set-name">Signed in as</span><span class="set-value">${esc(exo.account.email || '')}</span></div></div>`
        : accountRows()}${!exo.account && acct.offline ? `<p class="acct-hint">Exo works without an account for now and asks again next time.</p>` : ''}</div>`
      // No skip: Continue waits for a sign-in, or for the offline choice when the service can't be reached (CLIENT.md 4.1 F).
      cta.disabled = (!exo.account && !acct.offline) || !!acct.code
      cta.textContent = !exo.account && acct.offline ? 'Continue offline' : 'Continue'
      break

  }
  if (ob.step === steps.length - 1) cta.textContent = 'Start watching'
  step.classList.remove('is-forward', 'is-back')
  if (dir) { void step.offsetWidth; step.classList.add(dir > 0 ? 'is-forward' : 'is-back') }
  if (dir !== 0 || !step.contains(document.activeElement)) focusWhenActive(step.querySelector('[data-jellyfin-field]') ?? step.querySelector('.ob-title'))
}

function note(words, bad = false) {
  $('obNote').textContent = words
  $('obNote').classList.toggle('is-bad', bad)
}

async function finishOnboarding() {
  ob.active = false
  // A first run is never an upgrade: the account sheet is for someone who had Cinema before accounts.
  if (appInfo) appInfo.accountOffered = true
  try { await app('set', { onboarded: true, whatsNewSeen: appInfo?.version, accountOffered: true }) } catch { }
  const el = $('onboarding')
  el.classList.add('is-leaving')
  setTimeout(() => {
    el.hidden = true
    el.classList.remove('is-leaving')
    document.body.classList.remove('is-onboarding')
  }, 220)
}

$('obNext').addEventListener('click', () => {
  if (ob.step < obSteps().length - 1) drawOb(ob.step + 1, 1)
  else finishOnboarding()
})
$('obBack').addEventListener('click', () => drawOb(Math.max(0, ob.step - 1), -1))

// What the steps show changed (the server, the account): the step draws again in place.
const redrawOb = (...names) => { if (ob.active && names.includes(obSteps()[ob.step])) drawOb(ob.step, 0) }
window.addEventListener('exo:jellyfin', () => redrawOb('jellyfin'))
window.addEventListener('exo:account', () => redrawOb('account'))
