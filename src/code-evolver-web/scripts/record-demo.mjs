import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdir, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { performance } from 'node:perf_hooks'
import { chromium } from 'playwright'
import ffmpegPath from 'ffmpeg-static'
import { createServer } from 'vite'

const root = fileURLToPath(new URL('..', import.meta.url))
const output = path.resolve(root, '../../artifacts/demo', new Date().toISOString().replaceAll(/[:.]/g, '-'))
const viewport = { width: 1920, height: 960 }
const direction = 'Improve checkout reliability and error recovery.'
const repositoryPath = 'C:\\demo\\storefront'
const chapters = []
const errors = []
let evolution
let analysis
let server
let browser
let context
let recordingStart

function runFfmpeg(args) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.env.FFMPEG_PATH || ffmpegPath || 'ffmpeg', args, { cwd: output, windowsHide: true })
    let log = ''
    child.stderr.on('data', (data) => { log += data.toString() })
    child.on('error', reject)
    child.on('close', (code) => code === 0 ? resolve(log) : reject(new Error(`FFmpeg failed (${code}):\n${log}`)))
  })
}

function event(type, status = 'completed', detail = 'Simulated result for the recorded demonstration.') {
  const timestamp = new Date().toISOString()
  return {
    id: `${type}-${crypto.randomUUID()}`, type, status, detail,
    createdAt: timestamp, startedAt: timestamp,
    completedAt: status === 'completed' ? timestamp : undefined,
    logs: [detail],
  }
}

async function mockApi(route) {
  const request = route.request()
  const pathname = new URL(request.url()).pathname
  const method = request.method()
  let body
  if (pathname === '/api/repository' && method === 'GET') {
    body = { repositoryPath }
  } else if (pathname === '/api/data-analysis' && method === 'POST') {
    analysis = { id: 'demo-analysis', fileName: 'checkout-feedback.csv', status: 'running', keyPoints: [], events: [event('data-purpose.started', 'processing')] }
    body = analysis
  } else if (pathname === '/api/data-analysis/demo-analysis' && method === 'GET') {
    body = analysis
  } else if (pathname === '/api/evolutions' && method === 'GET') {
    body = evolution ? [evolution] : []
  } else if (pathname === '/api/evolutions' && method === 'POST') {
    const form = request.postDataJSON()
    assert.equal(form.repositoryPath, repositoryPath)
    assert.ok(form.direction.startsWith(direction))
    evolution = { ...form, id: 'demo-evolution', status: 'draft', updatedAt: new Date().toISOString(), workItems: [], events: [] }
    body = evolution
  } else if (pathname === '/api/evolutions/demo-evolution/start' && method === 'POST') {
    assert.equal(evolution?.status, 'draft')
    evolution.status = 'running'
    evolution.startedAt = new Date().toISOString()
    evolution.events = [event('scan.started', 'processing', 'Inspecting checkout flow and existing test coverage.')]
    body = evolution
  } else {
    errors.push(`Unexpected API request: ${method} ${pathname}`)
    return route.fulfill({ status: 501, json: { error: 'Not part of the demo scenario.' } })
  }
  await route.fulfill({ json: body })
}

function subtitleTime(seconds) {
  const milliseconds = Math.max(0, Math.round(seconds * 1000))
  return `${String(Math.floor(milliseconds / 3600000)).padStart(2, '0')}:${String(Math.floor(milliseconds / 60000) % 60).padStart(2, '0')}:${String(Math.floor(milliseconds / 1000) % 60).padStart(2, '0')},${String(milliseconds % 1000).padStart(3, '0')}`
}

await mkdir(output, { recursive: true })
try {
  await runFfmpeg(['-version'])
  server = await createServer({
    root,
    server: { host: '127.0.0.1', port: 0, open: false },
    define: { 'import.meta.env.VITE_API_URL': JSON.stringify('/api') },
  })
  await server.listen()
  const address = server.httpServer.address()
  const origin = `http://127.0.0.1:${address.port}`
  browser = await chromium.launch({ headless: true })
  context = await browser.newContext({
    viewport, deviceScaleFactor: 1, locale: 'en-US', timezoneId: 'UTC',
    serviceWorkers: 'block', recordVideo: { dir: path.join(output, 'raw'), size: viewport },
  })
  context.setDefaultTimeout(15000)
  await context.route('**/*', async (route) => {
    const url = new URL(route.request().url())
    if (url.pathname.startsWith('/api/')) {
      try { await mockApi(route) } catch (error) {
        errors.push(error.message)
        await route.fulfill({ status: 500, json: { error: error.message } })
      }
    } else if (url.origin === origin) {
      await route.continue()
    } else if (url.origin === 'https://fonts.googleapis.com') {
      await route.fulfill({ contentType: 'text/css', body: '' })
    } else {
      errors.push(`Blocked external request: ${url.origin}`)
      await route.abort()
    }
  })
  const page = await context.newPage()
  recordingStart = performance.now()
  page.on('pageerror', (error) => errors.push(error.message))
  page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()) })
  const video = page.video()
  const hold = (seconds) => page.waitForTimeout(seconds * 1000)
  const focusEvolution = () => page.locator('.evolution-workspace').evaluate((element) => element.scrollIntoView({ block: 'start', behavior: 'instant' }))
  const chapter = (title) => {
    chapters.push({ title, start: (performance.now() - recordingStart) / 1000 })
    console.log(`Recording: ${title}`)
  }
  const click = async (locator) => {
    await locator.scrollIntoViewIfNeeded()
    const box = await locator.boundingBox()
    assert.ok(box, 'Demo control must be visible')
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2, { steps: 18 })
    await hold(0.35)
    await locator.click()
  }
  const frame = async (name) => {
    assert.equal(await page.locator('.error-banner').count(), 0)
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Page must fit recording width')
    await page.screenshot({ path: path.join(output, `${name}.png`) })
  }

  await page.goto(origin)
  await page.getByRole('heading', { name: 'Code Evolver', exact: true }).waitFor()
  await page.waitForFunction(() => document.querySelector('.path-input input')?.value === 'C:\\demo\\storefront')
  await page.evaluate(() => {
    const cursor = document.createElement('div')
    cursor.style.cssText = 'position:fixed;width:22px;height:22px;border:3px solid #e44b36;border-radius:50%;pointer-events:none;z-index:2147483647;transform:translate(-50%,-50%);left:-100px;top:-100px'
    document.body.append(cursor)
    document.addEventListener('mousemove', (move) => { cursor.style.left = `${move.clientX}px`; cursor.style.top = `${move.clientY}px` })
  })
  chapter('Code Evolver | From data to an evolution')
  await frame('01-overview')
  await hold(3)

  chapter('Upload feedback and run the analyzer team')
  await page.locator('input[type=file]').setInputFiles({
    name: 'checkout-feedback.csv', mimeType: 'text/csv',
    buffer: Buffer.from('category,count\ncheckout_timeout,42\nunclear_error,28\nretry_request,19\n'),
  })
  await click(page.getByRole('button', { name: 'Analyze data', exact: true }))
  await page.locator('.analysis-status.running').waitFor()
  await hold(3)
  for (const [index, type] of ['data-purpose.started', 'data-insight.started', 'evolution-direction.started', 'analysis-summary.started'].entries()) {
    analysis.events[index] = event(type)
  }
  Object.assign(analysis, { status: 'completed', direction, keyPoints: ['Handle checkout timeouts with clear recovery actions.', 'Cover retry and error states with focused tests.'] })
  await page.getByRole('button', { name: 'Apply to evolution', exact: true }).waitFor()
  await frame('02-analysis')
  await hold(4)

  chapter('Apply the recommendation and create an evolution')
  await click(page.getByRole('button', { name: 'Apply to evolution', exact: true }))
  assert.ok((await page.getByLabel('Evolution direction').inputValue()).startsWith(direction))
  await page.getByLabel('Scope', { exact: true }).fill('src/checkout')
  await page.getByLabel('Target branch').fill('main')
  await hold(3)
  await click(page.getByRole('button', { name: 'Create evolution', exact: true }))
  await page.locator('.status-label.draft').waitFor()
  await focusEvolution()
  await frame('03-draft')
  await hold(3)

  chapter('Start the evolution and follow live agent activity')
  await click(page.getByRole('button', { name: 'Start', exact: true }))
  await page.locator('.status-label.running').waitFor()
  await hold(3)
  evolution.events = [event('scan.completed'), event('plan.completed'), event('work-item.started', 'processing', 'Adding checkout recovery states and focused tests.')]
  evolution.workItems = [{ id: 'demo-work', title: 'Checkout recovery', description: 'Add clear timeout feedback and retry behavior.', status: 'running' }]
  await click(page.getByRole('button', { name: 'Refresh', exact: true }))
  await page.locator('.agent-member.working').filter({ hasText: 'Worker agent' }).waitFor()
  await focusEvolution()
  await frame('04-working')
  await hold(4)

  chapter('Review the change and run the quality gate')
  evolution.events[2] = event('work-item.completed')
  evolution.events.push(event('review.completed'), event('gate.started', 'processing', 'Checking the proposed change and focused tests.'))
  await click(page.getByRole('button', { name: 'Refresh', exact: true }))
  await page.locator('.agent-member.working').filter({ hasText: 'Gate agent' }).waitFor()
  await focusEvolution()
  await hold(4)

  chapter('Completed | Inspect the work item and event history')
  evolution.events[evolution.events.length - 1] = event('gate.completed')
  evolution.events.push(event('change.merged', 'completed', 'Demo lifecycle complete. No files, commits, pushes or pull requests were created.'))
  evolution.status = 'completed'
  evolution.completedAt = new Date().toISOString()
  evolution.workItems[0].status = 'completed'
  evolution.workItems[0].result = 'Simulated outcome: recovery states and focused tests are ready for review.'
  await click(page.getByRole('button', { name: 'Refresh', exact: true }))
  await page.locator('.status-label.completed').waitFor()
  await frame('05-completed')
  await hold(3)
  await focusEvolution()
  await frame('06-results')
  await hold(4)
  assert.deepEqual(errors, [], 'Recording must not contain browser or route errors')

  const end = (performance.now() - recordingStart) / 1000
  await context.close()
  context = undefined
  const raw = await video.path()
  const trim = chapters[0].start
  const duration = end - trim
  const subtitles = chapters.map((entry, index) => `${index + 1}\n${subtitleTime(entry.start - trim)} --> ${subtitleTime((chapters[index + 1]?.start ?? end) - trim)}\nSIMULATED DEMO - No real agent execution\n${entry.title}\n`).join('\n')
  await writeFile(path.join(output, 'chapters.srt'), subtitles)
  await writeFile(path.join(output, 'chapters.json'), JSON.stringify({ simulated: true, viewport, duration, chapters }, null, 2))
  console.log('Editing: trim startup, add chapter captions, fade in/out, encode H.264 MP4')
  const filter = `pad=iw:ih+120:0:0:color=0x172621,subtitles=chapters.srt:force_style='FontName=Arial,FontSize=9,Outline=0,Shadow=0,MarginV=7',fade=t=in:st=0:d=0.3,fade=t=out:st=${Math.max(0, duration - 0.5)}:d=0.5`
  await runFfmpeg(['-hide_banner', '-y', '-ss', String(trim), '-i', raw, '-t', String(duration), '-vf', filter, '-an', '-c:v', 'libx264', '-preset', 'fast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', '30', '-movflags', '+faststart', 'code-evolver-demo.mp4'])
  await runFfmpeg(['-hide_banner', '-v', 'error', '-xerror', '-i', 'code-evolver-demo.mp4', '-f', 'null', '-'])
  await runFfmpeg(['-hide_banner', '-y', '-ss', '2', '-i', 'code-evolver-demo.mp4', '-frames:v', '1', 'preview.png'])
  console.log(`Validated video: ${path.join(output, 'code-evolver-demo.mp4')}`)
} finally {
  try { await context?.close() } finally {
    try { await browser?.close() } finally { await server?.close() }
  }
}