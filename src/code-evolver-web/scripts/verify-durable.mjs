import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdir, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'
import { chromium } from 'playwright'

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const root = path.resolve(webRoot, '../..')
const connection = process.env.CODEEVOLVER_TEST_MYSQL
assert(connection && /(?:Database|Initial Catalog)=code_evolver_test[\w]*[;]?/i.test(connection), 'An explicit isolated test database is required.')
assert(/(?:Server|Host)=127\.0\.0\.1[;]?/i.test(connection), 'Use the isolated loopback test server.')
const dotnet = process.env.CODEEVOLVER_DOTNET ?? 'dotnet'
const api = 'http://127.0.0.1:5278/api'
const env = { ...process.env, ConnectionStrings__CodeEvolver: connection, Agent__Provider: 'local', Agent__Copilot__PublishChanges: 'false', ASPNETCORE_URLS: 'http://127.0.0.1:5278' }
const processes = new Set()
const errors = []
const start = (executable, args, cwd) => {
  const child = spawn(executable, args, { cwd, env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true })
  processes.add(child)
  child.stderr.on('data', data => errors.push(data.toString()))
  child.stdout.resume()
  child.on('error', error => errors.push(error.message))
  return child
}
const startApi = () => start(dotnet, [path.join(root, 'src/CodeEvolver.Api/bin/Debug/net10.0/CodeEvolver.Api.dll')], path.join(root, 'src/CodeEvolver.Api'))
const startWorker = () => start(dotnet, [path.join(root, 'src/CodeEvolver.Worker/bin/Debug/net10.0/CodeEvolver.Worker.dll')], root)
const stop = async child => {
  if (child.exitCode !== null) return
  const exited = once(child, 'exit')
  child.kill()
  await exited
  processes.delete(child)
}
const request = async (url, body) => {
  const response = await fetch(`${api}${url}`, { ...(body !== undefined ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}), signal: AbortSignal.timeout(5000) })
  assert(response.ok, `${url}: ${response.status} ${await (!response.ok ? response.text() : Promise.resolve(''))}`)
  return response.status === 204 ? undefined : response.json()
}
const eventually = async (fn, label) => {
  const deadline = Date.now() + 30000
  while (Date.now() < deadline) {
    try { if (await fn()) return } catch { /* Startup may still be in progress. */ }
    await delay(200)
  }
  throw new Error(`Timed out: ${label}. ${errors.join('\n')}`)
}
let browser
try {
  // Do not take control of a user's already-running API.
  await assert.rejects(fetch(`${api}/health`, { signal: AbortSignal.timeout(1000) }), 'Port 5278 must be unused.')
  let apiProcess = startApi()
  await eventually(async () => (await request('/health')).storage === 'mysql', 'API readiness')
  const vite = start(process.execPath, [path.join(webRoot, 'node_modules/vite/bin/vite.js')], webRoot)
  await eventually(async () => (await fetch('http://127.0.0.1:5173')).ok, 'UI readiness')
  let worker = startWorker()
  await eventually(async () => (await request('/service')).workerOnline, 'worker heartbeat')
  await request('/service/control', { paused: true })
  browser = await chromium.launch({ channel: process.env.CODEEVOLVER_BROWSER_CHANNEL ?? 'msedge', headless: true })
  let page = await browser.newPage({ viewport: { width: 1440, height: 1000 } })
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('http://127.0.0.1:5173')
  await page.getByRole('heading', { name: 'Background service', exact: true }).waitFor()
  await page.getByLabel('Cloned repository path').fill(root)
  await page.getByRole('textbox', { name: 'Evolution direction', exact: true }).fill('Durable E2E simulation')
  await page.getByLabel('Cycle budget (1–50)').fill('10')
  await page.getByLabel('Between cycles (seconds)').fill('1')
  await page.getByRole('button', { name: 'Create evolution', exact: true }).click()
  await page.getByRole('button', { name: 'Start', exact: true }).click()
  const [saved] = (await request('/evolutions')).filter(e => e.direction === 'Durable E2E simulation')
  assert(saved)
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await eventually(async () => (await request(`/evolutions/${saved.id}`)).status === 'paused', 'UI pause')
  await page.getByRole('button', { name: 'Resume', exact: true }).click()
  await page.getByRole('button', { name: 'Resume dispatch', exact: true }).click()
  await eventually(async () => (await request(`/evolutions/${saved.id}`)).cycle >= 2, 'second cycle')
  await mkdir(path.join(root, '.runtime/evidence'), { recursive: true })
  await page.screenshot({ path: path.join(root, '.runtime/evidence/desktop.png'), fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: path.join(root, '.runtime/evidence/mobile.png'), fullPage: true })
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'Mobile viewport overflow')
  const before = await request(`/evolutions/${saved.id}`)
  await page.close()
  await stop(apiProcess)
  await delay(2500)
  apiProcess = startApi()
  await eventually(async () => (await request('/health')).status === 'healthy', 'API restart')
  const after = await request(`/evolutions/${saved.id}`)
  assert(after.events.length > before.events.length, 'Worker must progress without browser/API')
  await request('/service/control', { paused: true })
  await eventually(async () => !(await request(`/evolutions/${saved.id}`)).events.some(e => e.status === 'processing'), 'drain before worker restart')
  await stop(worker)
  const checkpoint = await request(`/evolutions/${saved.id}`)
  worker = startWorker()
  assert((await request('/service')).dispatchPaused, 'Dispatch pause survives worker restart')
  await request('/service/control', { paused: false })
  await eventually(async () => (await request(`/evolutions/${saved.id}`)).status === 'completed', 'checkpoint completion')
  const completed = await request(`/evolutions/${saved.id}`)
  assert.equal(completed.cycle, 10)
  assert.equal(completed.workItems.length, 10)
  assert(completed.events.length >= checkpoint.events.length)
  page = await browser.newPage()
  await page.goto('http://127.0.0.1:5173')
  await page.getByRole('heading', { name: 'Durable E2E simulation', exact: true }).waitFor()
  const fileName = `analysis-${Date.now()}.csv`
  await page.getByLabel('Cloned repository path').fill(root)
  await page.locator('input[type=file]').setInputFiles({ name: fileName, mimeType: 'text/csv', buffer: Buffer.from('count,amount\n1,12\n') })
  await page.getByRole('button', { name: 'Analyze data', exact: true }).click()
  await eventually(async () => (await request('/data-analysis')).some(run => run.fileName === fileName && run.status === 'completed' && run.events.length === 4), 'durable analyzer completion')
  await page.reload()
  await page.getByRole('button', { name: 'Apply to evolution', exact: true }).waitFor()
  assert.equal(errors.length, 0, errors.join('\n'))
  const evidence = { result: 'passed', agent: 'local simulation (no Copilot call)', cycles: 10, uiClosedAndApiStoppedProgress: [before.events.length, after.events.length], workerRestartPreservedCheckpoint: true, desktopAndMobile: true, analyzerReload: true }
  await writeFile(path.join(root, '.runtime/evidence/durable-e2e.json'), JSON.stringify(evidence, null, 2))
  console.log(JSON.stringify(evidence, null, 2))
  await stop(vite)
} finally {
  await browser?.close()
  for (const child of processes) await stop(child)
}