import assert from 'node:assert/strict'
import { readFile, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'
import { createServer } from 'vite'

assert.ok(process.argv[2], 'Pass the real demo output directory')
const output = path.resolve(process.argv[2])
const evolution = JSON.parse(await readFile(path.join(output, 'evolution.json'), 'utf8'))
const root = fileURLToPath(new URL('..', import.meta.url))
const server = await createServer({ root, server: { host: '127.0.0.1', port: 0, open: false }, define: { 'import.meta.env.VITE_API_URL': JSON.stringify('/api') } })
let browser
try {
  await server.listen()
  browser = await chromium.launch()
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } })
  const errors = []
  page.on('pageerror', (error) => errors.push(error.message))
  await page.route('**/api/**', (route) => route.fulfill({ json: route.request().url().endsWith('/repository') ? { repositoryPath: evolution.repositoryPath } : [evolution] }))
  await page.goto(`http://127.0.0.1:${server.httpServer.address().port}`)
  const search = page.getByRole('textbox', { name: 'Search event logs' })
  await search.fill('Dispatching')
  const expected = evolution.events.flatMap((event) => event.logs.filter((log) => log.toLowerCase().includes('dispatching')))
  assert.ok(expected.length > 0)
  await page.waitForFunction((count) => document.querySelectorAll('.evolution-workspace .event').length === count, evolution.events.filter((event) => event.logs.some((log) => log.toLowerCase().includes('dispatching'))).length)
  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Export CSV', exact: true }).click()
  const download = await downloadPromise
  await download.saveAs(path.join(output, 'verified-filtered-logs.csv'))
  const csv = await readFile(path.join(output, 'verified-filtered-logs.csv'), 'utf8')
  assert.equal(csv.split('\r\n').length, expected.length + 1)
  for (const log of expected) assert.ok(csv.includes(log.replaceAll('"', '""')))
  await search.fill('no-such-log-demo-validation-019283')
  await page.getByText('No event logs match the current search.', { exact: true }).waitFor()
  assert.equal(await page.getByRole('button', { name: 'Export CSV', exact: true }).isDisabled(), true)
  await search.fill('')
  assert.ok(await page.locator('select').count() >= 3)
  await search.scrollIntoViewIfNeeded()
  await page.screenshot({ path: path.join(output, 'verified-desktop.png') })
  await page.setViewportSize({ width: 390, height: 844 })
  await search.scrollIntoViewIfNeeded()
  await page.screenshot({ path: path.join(output, 'verified-mobile.png') })
  const mobileFits = await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)
  const bytes = await readFile(path.join(output, 'code-evolver-real-demo-with-voice.webm'))
  const audio = await page.evaluate(async (base64) => {
    const context = new AudioContext()
    try {
      const buffer = await context.decodeAudioData(Uint8Array.from(atob(base64), (character) => character.charCodeAt(0)).buffer)
      const samples = buffer.getChannelData(0)
      let energy = 0
      for (const sample of samples) energy += sample * sample
      return { duration: buffer.duration, rmsDb: 10 * Math.log10(energy / samples.length) }
    } finally { await context.close() }
  }, bytes.toString('base64'))
  assert.ok(audio.duration > 30)
  assert.ok(audio.rmsDb > -40)
  assert.deepEqual(errors, [])
  const report = { validationUsesSavedRealApiData: true, actualEvolutionStatus: evolution.status, filteredLogRows: expected.length, csvDownload: 'passed', noMatches: 'passed', mobileFits, audio, browserErrors: errors }
  await writeFile(path.join(output, 'verification.json'), JSON.stringify(report, null, 2))
  console.log(JSON.stringify(report, null, 2))
} finally {
  await browser?.close()
  await server.close()
}