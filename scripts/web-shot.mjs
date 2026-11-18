#!/usr/bin/env node
// A screenshot of one page of the web UI, signed in, with headless Chrome over the DevTools protocol (Node 22+: its WebSocket).
// Used by the iOS smoke test (scripts/smoke-ios-collections.sh) for the web UI's view of the iPhone; any server works.
//
//   node scripts/web-shot.mjs <server> <path> <out.png> --cookie <async_refresh value> [--theme light|dark] [--size 1280x900]
//        [--wait 5] [--js "<expression, awaited>"]
//
// The refresh cookie signs the page in (the SPA renews its session from it on load); sign in once per shot, since a refresh
// replaces it. --js runs after --wait seconds (e.g. clicks a button), then 3 s more before the shot. CHROME names the browser
// (default: macOS's Google Chrome, else google-chrome / chromium on the PATH).
import { spawn } from 'node:child_process'
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const [server, path, out, ...rest] = process.argv.slice(2)
if (!server || !path || !out) { console.error('usage: node web-shot.mjs <server> <path> <out.png> --cookie <value> [options]'); process.exit(2) }
const opt = name => { const i = rest.indexOf(`--${name}`); return i >= 0 ? rest[i + 1] : undefined }
const cookie = opt('cookie')
const theme = opt('theme') ?? 'light'
const [width, height] = (opt('size') ?? '1280x900').split('x').map(Number)
const wait = Number(opt('wait') ?? '5')
const js = opt('js')
const mac = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
const chromePath = process.env.CHROME ?? (existsSync(mac) ? mac : 'google-chrome')
if (typeof WebSocket === 'undefined') { console.error('web-shot: this Node has no WebSocket (Node 22+ needed)'); process.exit(3) }

const sleep = ms => new Promise(r => setTimeout(r, ms))
const port = 9400 + Math.floor(Math.random() * 400)
const profile = mkdtempSync(join(tmpdir(), 'web-shot-'))
const chrome = spawn(chromePath, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, `--window-size=${width},${height}`,
  '--no-first-run', '--no-default-browser-check', 'about:blank'], { stdio: 'ignore' })

let ws
let id = 0
const pending = new Map()
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const n = ++id
  pending.set(n, { resolve, reject })
  ws.send(JSON.stringify({ id: n, method, params }))
})

try {
  let page
  for (let i = 0; i < 100 && !page; i++) {
    await sleep(200)
    try { page = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.type === 'page') } catch { /* not up yet */ }
  }
  if (!page) throw new Error(`Chrome (${chromePath}) did not start`)
  ws = new WebSocket(page.webSocketDebuggerUrl)
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject })
  ws.onmessage = e => {
    const m = JSON.parse(e.data)
    if (m.id && pending.has(m.id)) {
      const p = pending.get(m.id)
      pending.delete(m.id)
      if (m.error) p.reject(new Error(m.error.message)); else p.resolve(m.result)
    } else if (m.method === 'Runtime.exceptionThrown') console.error('page exception:', m.params.exceptionDetails?.exception?.description ?? m.params.exceptionDetails?.text)
  }
  await send('Runtime.enable')
  await send('Network.enable')
  if (cookie) await send('Network.setCookie', { name: 'async_refresh', value: cookie, url: new URL('/api/auth', server).href, httpOnly: true })
  await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false })
  await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: theme }] })
  await send('Page.enable')
  await send('Page.navigate', { url: new URL(path, server).href })
  await sleep(wait * 1000)
  if (js) {
    const r = await send('Runtime.evaluate', { expression: js, awaitPromise: true, returnByValue: true })
    if (r.exceptionDetails) console.error('js:', r.exceptionDetails.exception?.description ?? r.exceptionDetails.text)
    else console.log('js:', JSON.stringify(r.result?.value))
    await sleep(3000)
  }
  const shot = await send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(out, Buffer.from(shot.data, 'base64'))
  console.log(`web-shot: ${out}`)
} catch (e) {
  console.error(`web-shot: ${e.message}`)
  process.exitCode = 1
} finally {
  try { ws?.close() } catch { /* closing */ }
  chrome.kill()
  await sleep(300)
  try { rmSync(profile, { recursive: true, force: true }) } catch { /* Chrome may still hold it */ }
}
