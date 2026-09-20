import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { performance } from 'node:perf_hooks'
import { chromium } from 'playwright'
import ffmpegPath from 'ffmpeg-static'
import { createServer } from 'vite'

const root = fileURLToPath(new URL('..', import.meta.url))
const output = path.resolve(root, '../../artifacts/demo', new Date().toISOString().replaceAll(/[:.]/g, '-'))
const viewport = { width: 1920, height: 960 }
const direction = 'Improve Code Evolver with GitHub-style colors and selection controls.'
const logRequest = 'Support searching agent team logs and exporting them to a CSV file for review.'
const repositoryPath = path.resolve(root, '../..')
const repositoryUrl = 'https://github.com/caigen/CodeEvolver'
const feedbackPath = path.join(root, 'scripts/demo-feedback.json')
const chapters = []
const errors = []
let narration
let evolution
let analysis
let server
let browser
let context
let recordingStart

function runProcess(executable, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, { cwd: output, windowsHide: true })
    let log = ''
    child.stdout.on('data', (data) => { log += data.toString() })
    child.stderr.on('data', (data) => { log += data.toString() })
    child.on('error', reject)
    child.on('close', (code) => code === 0 ? resolve(log) : reject(new Error(`${path.basename(executable)} failed (${code}):\n${log}`)))
  })
}

const runFfmpeg = (args) => runProcess(process.env.FFMPEG_PATH || ffmpegPath || 'ffmpeg', args)

async function generateNarration() {
  assert.equal(process.platform, 'win32', 'Local narration requires Windows. Set DEMO_NARRATION=off for a silent demo.')
  const directory = path.join(output, 'narration')
  const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe')
  const log = await runProcess(powershell, [
    '-NoProfile', '-NonInteractive', '-File', path.join(root, 'scripts/synthesize-narration.ps1'),
    '-InputPath', path.join(root, 'scripts/narration.json'), '-OutputDirectory', directory,
  ])
  console.log(log.trim())
  const metadataPath = path.join(directory, 'narration.json')
  const metadata = JSON.parse((await readFile(metadataPath, 'utf8')).replace(/^\uFEFF/, ''))
  assert.equal(metadata.segments.length, 7, 'Expected one narration clip per demo chapter')
  for (const segment of metadata.segments) {
    const probe = await runFfmpeg(['-hide_banner', '-nostats', '-i', path.join(directory, segment.file), '-af', 'volumedetect', '-progress', 'pipe:2', '-f', 'null', '-'])
    const elapsed = [...probe.matchAll(/^out_time_us=(\d+)/gm)].at(-1)
    segment.duration = Number(elapsed?.[1]) / 1000000
    assert.ok(Number.isFinite(segment.duration) && segment.duration > 0, `Invalid narration duration: ${segment.file}`)
    assert.ok(Number(probe.match(/max_volume: ([-\d.]+) dB/)?.[1]) > -60, `Narration is silent: ${segment.file}`)
  }
  await writeFile(metadataPath, JSON.stringify(metadata, null, 2))
  return metadata
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
    assert.ok(request.postDataBuffer()?.includes(Buffer.from('demo-feedback.json')), 'Upload must use the guide feedback file')
    analysis = { id: 'demo-analysis', fileName: 'demo-feedback.json', status: 'running', keyPoints: [], events: [event('data-purpose.started', 'processing')] }
    body = analysis
  } else if (pathname === '/api/data-analysis/demo-analysis' && method === 'GET') {
    body = analysis
  } else if (pathname === '/api/evolutions' && method === 'GET') {
    body = evolution ? [evolution] : []
  } else if (pathname === '/api/evolutions' && method === 'POST') {
    const form = request.postDataJSON()
    assert.equal(form.repositoryPath, repositoryPath)
    assert.ok(form.direction.startsWith(direction))
    assert.ok(form.direction.includes(logRequest))
    assert.equal(form.scope, 'src/code-evolver-web')
    evolution = { ...form, id: 'demo-evolution', status: 'draft', updatedAt: new Date().toISOString(), workItems: [], events: [] }
    body = evolution
  } else if (pathname === '/api/evolutions/demo-evolution/start' && method === 'POST') {
    assert.equal(evolution?.status, 'draft')
    evolution.status = 'running'
    evolution.startedAt = new Date().toISOString()
    evolution.events = [event('scan.started', 'processing', 'Simulated inspection of Code Evolver colors, human input controls, and agent team logs.')]
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
  const feedback = JSON.parse(await readFile(feedbackPath, 'utf8'))
  assert.equal(feedback.length, 2)
  await writeFile(path.join(output, 'demo-feedback.json'), JSON.stringify(feedback, null, 2))
  await runFfmpeg(['-version'])
  if (process.env.DEMO_NARRATION !== 'off') narration = await generateNarration()
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
  const finishNarration = async () => {
    const current = chapters.at(-1)
    if (!current?.narration) return
    const remaining = current.start + current.narration.duration + 0.6 - (performance.now() - recordingStart) / 1000
    if (remaining > 0) await hold(remaining)
  }
  const chapter = async (title) => {
    await finishNarration()
    const segment = narration?.segments[chapters.length]
    if (narration) assert.equal(segment?.title, title, 'Narration must match the recorded chapter')
    chapters.push({ title, start: (performance.now() - recordingStart) / 1000, narration: segment })
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
  await page.waitForFunction((target) => document.querySelector('.path-input input')?.value === target, repositoryPath)
  await page.evaluate(() => {
    const cursor = document.createElement('div')
    cursor.style.cssText = 'position:fixed;width:22px;height:22px;border:3px solid #e44b36;border-radius:50%;pointer-events:none;z-index:2147483647;transform:translate(-50%,-50%);left:-100px;top:-100px'
    document.body.append(cursor)
    document.addEventListener('mousemove', (move) => { cursor.style.left = `${move.clientX}px`; cursor.style.top = `${move.clientY}px` })
  })
  await chapter('Code Evolver | Evolving its own user experience')
  await frame('01-overview')
  await hold(3)

  await chapter('Analyze JSON feedback | GitHub-style colors and selection controls')
  await page.locator('input[type=file]').setInputFiles(feedbackPath)
  await click(page.getByRole('button', { name: 'Analyze data', exact: true }))
  await page.locator('.analysis-status.running').waitFor()
  await hold(3)
  for (const [index, type] of ['data-purpose.started', 'data-insight.started', 'evolution-direction.started', 'analysis-summary.started'].entries()) {
    analysis.events[index] = event(type)
  }
  Object.assign(analysis, { status: 'completed', direction, keyPoints: ['Use GitHub-like colors with accessible contrast.', 'Offer selection controls for known human-input choices.'] })
  await page.getByRole('button', { name: 'Apply to evolution', exact: true }).waitFor()
  await frame('02-analysis')
  await hold(4)

  await chapter('Target this repository | Add log search and CSV export to the request')
  await click(page.getByRole('button', { name: 'Apply to evolution', exact: true }))
  assert.ok((await page.getByLabel('Evolution direction').inputValue()).startsWith(direction))
  const requestedDirection = `${await page.getByLabel('Evolution direction').inputValue()}\n\n${logRequest}`
  await page.getByLabel('Evolution direction').fill(requestedDirection)
  await page.getByLabel('Scope Path', { exact: true }).fill('src/code-evolver-web')
  await page.getByLabel('Target branch').fill('main')
  await hold(3)
  await click(page.getByRole('button', { name: 'Create evolution', exact: true }))
  await page.locator('.status-label.draft').waitFor()
  await focusEvolution()
  await frame('03-draft')
  await hold(3)

  await chapter('Start the evolution and follow live agent activity')
  await click(page.getByRole('button', { name: 'Start', exact: true }))
  await page.locator('.status-label.running').waitFor()
  await hold(3)
  evolution.events = [event('scan.completed'), event('plan.completed'), event('work-item.started', 'processing', 'Simulated work on GitHub-style colors, selection controls, and searchable, exportable agent logs.')]
  evolution.workItems = [
    { id: 'demo-colors', title: 'GitHub-style colors', description: 'Use neutral surfaces, accessible contrast, and restrained accents.', status: 'running' },
    { id: 'demo-input', title: 'Selection controls for human input', description: 'Offer known options while preserving free-form input where needed.', status: 'pending' },
    { id: 'demo-logs', title: 'Search agent logs and export CSV', description: 'Filter team logs by search text and export results as CSV for review.', status: 'pending' },
  ]
  await click(page.getByRole('button', { name: 'Refresh', exact: true }))
  await page.locator('.agent-member.working').filter({ hasText: 'Worker agent' }).waitFor()
  await focusEvolution()
  await frame('04-working')
  await hold(4)

  await chapter('Review the change and run the quality gate')
  evolution.events[2] = event('work-item.completed')
  evolution.events.push(event('review.completed'), event('gate.started', 'processing', 'Simulated checks for color contrast, selection behavior, log filtering, and CSV escaping.'))
  await click(page.getByRole('button', { name: 'Refresh', exact: true }))
  await page.locator('.agent-member.working').filter({ hasText: 'Gate agent' }).waitFor()
  await focusEvolution()
  await hold(4)

  await chapter('Completed | Inspect the work item and event history')
  evolution.events[evolution.events.length - 1] = event('gate.completed')
  evolution.events.push(event('change.merged', 'completed', 'Demo lifecycle complete. No files, commits, pushes or pull requests were created.'))
  evolution.status = 'completed'
  evolution.completedAt = new Date().toISOString()
  for (const item of evolution.workItems) {
    item.status = 'completed'
    item.result = 'Simulated outcome only. This recording does not implement or validate this feature.'
  }
  await click(page.getByRole('button', { name: 'Refresh', exact: true }))
  await page.locator('.status-label.completed').waitFor()
  await frame('05-completed')
  await hold(3)
  await focusEvolution()
  await frame('06-results')
  await hold(4)
  await chapter('Visit https://github.com/caigen/CodeEvolver')
  const closingPage = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Code Evolver - Demo</title>
<style>body{margin:0;background:#f6f8fa;color:#1f2328;font-family:Segoe UI,sans-serif}main{max-width:1400px;margin:0 auto;padding:180px 80px}h1{font-size:72px;margin:0 0 32px}p{font-size:28px;line-height:1.6}a{color:#0969da;font-size:40px}small{display:block;margin-top:80px;font-size:22px;color:#59636e}</style>
</head><body><main><h1>Code Evolver</h1><p>Explore the project on GitHub</p>
<a href="${repositoryUrl}">${repositoryUrl}</a>
<small>Simulated walkthrough of proposed improvements. No real agent execution or repository changes.</small>
</main></body></html>`
  await writeFile(path.join(output, 'visit-code-evolver.html'), closingPage)
  await page.setContent(closingPage)
  assert.equal(await page.getByRole('link', { name: repositoryUrl }).getAttribute('href'), repositoryUrl)
  await frame('07-github')
  await hold(5)
  await finishNarration()
  assert.equal(chapters.length, 7)
  assert.deepEqual(errors, [], 'Recording must not contain browser or route errors')

  const end = (performance.now() - recordingStart) / 1000
  await context.close()
  context = undefined
  const raw = await video.path()
  const trim = chapters[0].start
  const duration = end - trim
  const subtitles = chapters.map((entry, index) => `${index + 1}\n${subtitleTime(entry.start - trim)} --> ${subtitleTime((chapters[index + 1]?.start ?? end) - trim)}\nSIMULATED DEMO - No real agent execution\n${entry.title}\n`).join('\n')
  await writeFile(path.join(output, 'chapters.srt'), subtitles)
  await writeFile(path.join(output, 'chapters.json'), JSON.stringify({ simulated: true, repositoryPath, repositoryUrl, viewport, duration, voice: narration?.voice, chapters }, null, 2))
  console.log('Editing: trim startup, add chapter captions, fade in/out, encode H.264 MP4' + (narration ? ' with English narration' : ''))
  const filter = `pad=iw:ih+120:0:0:color=0x172621,subtitles=chapters.srt:force_style='FontName=Arial,FontSize=9,Outline=0,Shadow=0,MarginV=7',fade=t=in:st=0:d=0.3,fade=t=out:st=${Math.max(0, duration - 0.5)}:d=0.5`
  const audioInputs = narration ? narration.segments.flatMap((segment) => ['-i', path.join(output, 'narration', segment.file)]) : []
  const audioOptions = ['-an']
  if (narration) {
    const delayed = chapters.map((entry, index) => {
      assert.ok(entry.start + entry.narration.duration + 0.15 <= (chapters[index + 1]?.start ?? end), 'Narration must finish before the chapter ends')
      return `[${index + 1}:a]aresample=48000,adelay=${Math.round((entry.start - trim + 0.15) * 1000)}:all=1[voice${index}]`
    })
    const mix = `${chapters.map((_, index) => `[voice${index}]`).join('')}amix=inputs=${chapters.length}:duration=longest:normalize=0,apad,atrim=duration=${duration}[narration]`
    audioOptions.splice(0, 1, '-filter_complex', [...delayed, mix].join(';'), '-map', '0:v:0', '-map', '[narration]', '-c:a', 'aac', '-b:a', '192k', '-ar', '48000')
  }
  await runFfmpeg(['-hide_banner', '-y', '-ss', String(trim), '-i', raw, ...audioInputs, '-t', String(duration), '-vf', filter, ...audioOptions, '-c:v', 'libx264', '-preset', 'fast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', '30', '-movflags', '+faststart', 'code-evolver-demo.mp4'])
  await runFfmpeg(['-hide_banner', '-v', 'error', '-xerror', '-i', 'code-evolver-demo.mp4', '-map', '0:v:0', ...(narration ? ['-map', '0:a:0'] : []), '-f', 'null', '-'])
  if (narration) {
    const audioCheck = await runFfmpeg(['-hide_banner', '-i', 'code-evolver-demo.mp4', '-map', '0:a:0', '-af', 'volumedetect', '-f', 'null', '-'])
    assert.ok(Number(audioCheck.match(/max_volume: ([-\d.]+) dB/)?.[1]) > -60, 'Final video must have an audible narration track')
    console.log('Exporting WebM with Opus narration for players without AAC support')
    await runFfmpeg(['-hide_banner', '-y', '-i', 'code-evolver-demo.mp4', '-map', '0:v:0', '-map', '0:a:0', '-c:v', 'libvpx-vp9', '-b:v', '0', '-crf', '30', '-deadline', 'realtime', '-cpu-used', '6', '-row-mt', '1', '-c:a', 'libopus', '-b:a', '128k', 'code-evolver-demo-with-voice.webm'])
    await runFfmpeg(['-hide_banner', '-v', 'error', '-xerror', '-i', 'code-evolver-demo-with-voice.webm', '-map', '0:v:0', '-map', '0:a:0', '-f', 'null', '-'])
    console.log(`Voiced WebM: ${path.join(output, 'code-evolver-demo-with-voice.webm')}`)
  }
  await runFfmpeg(['-hide_banner', '-y', '-ss', '2', '-i', 'code-evolver-demo.mp4', '-frames:v', '1', 'preview.png'])
  console.log(`Validated video: ${path.join(output, 'code-evolver-demo.mp4')}`)
} finally {
  try { await context?.close() } finally {
    try { await browser?.close() } finally { await server?.close() }
  }
}