import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { performance } from 'node:perf_hooks'
import { chromium } from 'playwright'
import ffmpeg from 'ffmpeg-static'
import { createServer } from 'vite'

const root = fileURLToPath(new URL('..', import.meta.url))
const repository = path.resolve(root, '../..')
const output = path.join(repository, 'artifacts/demo', `real-${new Date().toISOString().replaceAll(/[:.]/g, '-')}`)
const projectUrl = 'https://github.com/caigen/CodeEvolver'
const viewport = { width: 1920, height: 960 }
const scenes = []
const browserErrors = []
let api
let apiUrl
let server
let browser
let context
let evolutionId
let outcome
let analysis
let recordingStart
let rawVideo

function run(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: repository, windowsHide: true, ...options })
    let log = ''
    child.stdout.on('data', (data) => { log += data })
    child.stderr.on('data', (data) => { log += data })
    child.on('error', reject)
    child.on('close', (code) => code === 0 ? resolve(log) : reject(new Error(`${command} exited ${code}:\n${log.slice(-12000)}`)))
  })
}
const encode = (args) => run(process.env.FFMPEG_PATH || ffmpeg, ['-hide_banner', ...args], { cwd: output })
const save = (name, data) => writeFile(path.join(output, name), JSON.stringify(data, null, 2))
const stamp = () => (performance.now() - recordingStart) / 1000

async function getJson(route) {
  const response = await fetch(`${apiUrl}/api/${route}`, { signal: AbortSignal.timeout(15000) })
  if (!response.ok) throw new Error(`API ${route}: ${response.status}`)
  return response.json()
}

async function startApi() {
  const dll = path.join(repository, 'src/CodeEvolver.Api/bin/Debug/net10.0/CodeEvolver.Api.dll')
  const contentRoot = path.join(output, 'api')
  await mkdir(contentRoot, { recursive: true })
  let logs = ''
  await new Promise((resolve, reject) => {
    api = spawn('dotnet', [dll, '--contentRoot', contentRoot, '--urls', 'http://127.0.0.1:0'], {
      cwd: contentRoot, windowsHide: true,
      env: {
        ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', Storage__Provider: 'json',
        Agent__Provider: 'copilot', Agent__Copilot__PublishChanges: 'false',
        Agent__Copilot__Executable: process.env.DEMO_COPILOT_EXECUTABLE || 'copilot.exe',
        Agent__Copilot__ReadOnly: 'false', Agent__Copilot__MaxAiCredits: '30',
        Agent__Copilot__TimeoutMinutes: '15',
        Agent__Copilot__SecretEnvironmentVariables: 'GH_TOKEN,GITHUB_TOKEN,COPILOT_GITHUB_TOKEN',
      },
    })
    const timeout = setTimeout(() => reject(new Error('API startup timed out')), 60000)
    const receive = (data) => {
      logs += data.toString()
      const match = logs.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)
      if (match) { apiUrl = match[1]; clearTimeout(timeout); resolve() }
    }
    api.stdout.on('data', receive)
    api.stderr.on('data', receive)
    api.once('error', (error) => { clearTimeout(timeout); reject(error) })
    api.once('exit', (code) => { clearTimeout(timeout); reject(new Error(`API stopped: ${code}\n${logs.slice(-6000)}`)) })
  })
  assert.equal((await getJson('health')).agent, 'copilot')
  console.log(`Real API ready: ${apiUrl}; publishing disabled; isolated state: ${contentRoot}`)
}

async function record() {
  await startApi()
  server = await createServer({ root, server: { host: '127.0.0.1', port: 0, open: false, hmr: false }, define: { 'import.meta.env.VITE_API_URL': JSON.stringify(`${apiUrl}/api`) } })
  await server.listen()
  const origin = `http://127.0.0.1:${server.httpServer.address().port}`
  browser = await chromium.launch({ headless: true })
  context = await browser.newContext({ viewport, locale: 'en-US', timezoneId: 'UTC', recordVideo: { dir: path.join(output, 'raw'), size: viewport } })
  await context.addInitScript((target) => localStorage.setItem('code-evolver.repository-path', target), repository)
  const page = await context.newPage()
  page.setDefaultTimeout(30000)
  page.on('pageerror', (error) => browserErrors.push(error.message))
  recordingStart = performance.now()
  const video = page.video()
  const scene = async (title, text, focus) => {
    if (focus) await focus.scrollIntoViewIfNeeded()
    const start = stamp()
    await page.screenshot({ path: path.join(output, `scene-${scenes.length + 1}.png`) })
    await page.waitForTimeout(3500)
    scenes.push({ title, text, start, end: stamp() })
    console.log(`Captured: ${title}`)
  }
  await page.goto(origin)
  await page.getByRole('heading', { name: 'Code Evolver', exact: true }).waitFor()
  await scene('Real execution | Code Evolver evolves its own UI', 'This is a real Code Evolver run against the current repository. GitHub Copilot agents execute the work. Publishing is disabled, so changes remain local and uncommitted.')
  await page.locator('input[type=file]').setInputFiles(path.join(root, 'scripts/demo-feedback.json'))
  const analysisResponse = page.waitForResponse((response) => response.url() === `${apiUrl}/api/data-analysis` && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Analyze data', exact: true }).click()
  const response = await analysisResponse
  assert.ok(response.ok(), 'Real data analysis must start successfully')
  analysis = await response.json()
  let lastStage
  const analysisDeadline = Date.now() + 65 * 60000
  while (analysis.status === 'running') {
    assert.ok(Date.now() < analysisDeadline, 'Analysis exceeded the overall deadline')
    await page.waitForTimeout(1500)
    analysis = await getJson(`data-analysis/${analysis.id}`)
    const stage = analysis.events.find((entry) => entry.status === 'processing')?.type
    if (stage !== lastStage) { console.log(`Real analysis: ${stage ?? analysis.status}`); lastStage = stage }
    await save('analysis.json', analysis)
  }
  assert.equal(analysis.status, 'completed', analysis.error || 'Real analysis failed')
  await page.getByRole('button', { name: 'Apply to evolution', exact: true }).waitFor()
  await scene('Actual analyzer recommendation', 'The analyzer team has processed a bounded profile of the uploaded JSON. This is its actual recommendation. The human will now specify the concrete product requirements from the demo guide.', page.getByRole('button', { name: 'Apply to evolution', exact: true }))
  await page.getByRole('button', { name: 'Apply to evolution', exact: true }).click()
  const direction = [
    'Improve Code Evolver itself following these explicit human requirements: use GitHub-like neutral colors with accessible contrast; offer selection controls for known human-input options while keeping custom input possible; add agent team log search and export filtered logs to CSV for review.',
    'Plan exactly ONE bounded work item covering these related UI changes. Make small functional changes, not a redesign. Use existing React and lucide conventions. Reuse dependencies. Do not add packages.',
    'Only edit files under src/code-evolver-web/src. Do not edit scripts, package files, README, HumanDesign, API, or any existing unrelated changes. Preserve all pre-existing work. Do not create branches, stage, commit, push, or create pull requests. Do not invoke the demo recorder. Do not launch persistent servers.',
    'Validate with npm run build --prefix src/code-evolver-web and npm run lint --prefix src/code-evolver-web. Add focused tests using available tools if appropriate. Search must filter actual event logs and CSV must escape quotes/newlines and protect spreadsheet formula cells.',
    'Final reporting must state exactly what changed and what validation actually ran. Publishing is disabled.',
  ].join('\n\n')
  await page.getByLabel('Evolution direction').fill(direction)
  await page.getByLabel('Scope Path', { exact: true }).fill('src/code-evolver-web/src')
  await page.getByLabel('Target branch').fill('main')
  const createResponse = page.waitForResponse((reply) => reply.url() === `${apiUrl}/api/evolutions` && reply.request().method() === 'POST')
  await page.getByRole('button', { name: 'Create evolution', exact: true }).click()
  const created = await createResponse
  assert.ok(created.ok())
  evolutionId = (await created.json()).id
  await save('request.json', { repository, direction, scope: 'src/code-evolver-web/src', publishing: false })
  await page.getByRole('button', { name: 'Start', exact: true }).waitFor()
  await scene('Human direction | Colors, selection controls, log search and CSV', 'The target is this Code Evolver repository. The human requests GitHub-style colors, selection controls, and searchable agent logs with CSV export. Edits are restricted to web application source files.', page.getByRole('button', { name: 'Start', exact: true }))
  await page.getByRole('button', { name: 'Start', exact: true }).click()
  const seen = new Set()
  const deadline = Date.now() + 95 * 60000
  do {
    assert.ok(Date.now() < deadline, 'Evolution exceeded the overall deadline')
    await page.waitForTimeout(2000)
    outcome = await getJson(`evolutions/${evolutionId}`)
    await save('evolution.json', outcome)
    const active = outcome.events.find((entry) => entry.status === 'processing')
    if (active && !seen.has(active.id)) {
      seen.add(active.id)
      console.log(`Real evolution: ${active.type}`)
      await scene(`Real agent | ${active.type}`, `The ${active.type.split('.')[0].replaceAll('-', ' ')} stage is running through GitHub Copilot. The activity and logs shown here come from the real API. Waiting time between stages is removed in this edited recording.`, page.locator('.active-work'))
    }
  } while (outcome.status === 'running' || outcome.status === 'stopRequested')
  await page.reload()
  await page.locator(`.status-label.${outcome.status}`).waitFor()
  await scene(`Actual outcome | ${outcome.status}`, outcome.status === 'completed'
    ? 'The real evolution has completed. Review the actual work items and event history. Changes remain uncommitted in the working tree. No commit, push, or pull request was performed.'
    : 'The real evolution did not complete successfully. The recorded status and saved event history describe the failure. This recording does not claim successful implementation.', page.getByRole('heading', { name: 'Agent team', exact: true }))
  await page.setContent(`<!doctype html><html lang="en"><head><title>Code Evolver</title></head><body style="margin:0;background:#f6f8fa;color:#1f2328;font-family:Segoe UI,sans-serif"><main style="padding:180px 150px"><h1 style="font-size:72px">Code Evolver</h1><p style="font-size:28px">Real agent execution. Local changes. Publishing disabled.</p><a style="font-size:40px;color:#0969da" href="${projectUrl}">${projectUrl}</a></main></body></html>`)
  await scene(`Visit ${projectUrl}`, 'Explore Code Evolver on GitHub. Visit github dot com, slash caigen, slash Code Evolver. The original recording and actual execution results are saved alongside this edited demo.')
  await context.close()
  context = undefined
  rawVideo = await video.path()
  await save('scenes.json', { realExecution: true, repository, apiUrl, outcome: outcome.status, browserErrors, rawVideo, scenes })
}

async function edit() {
  await save('narration-input.json', scenes)
  const directory = path.join(output, 'narration')
  const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe')
  console.log((await run(powershell, ['-NoProfile', '-NonInteractive', '-File', path.join(root, 'scripts/synthesize-narration.ps1'), '-InputPath', path.join(output, 'narration-input.json'), '-OutputDirectory', directory])).trim())
  const metadata = JSON.parse((await readFile(path.join(directory, 'narration.json'), 'utf8')).replace(/^\uFEFF/, ''))
  assert.equal(metadata.segments.length, scenes.length)
  const clips = []
  for (const [index, scene] of scenes.entries()) {
    const audio = path.join(directory, metadata.segments[index].file)
    const probe = await encode(['-nostats', '-i', audio, '-progress', 'pipe:2', '-f', 'null', '-'])
    const duration = Number([...probe.matchAll(/^out_time_us=(\d+)/gm)].at(-1)?.[1]) / 1000000 + 0.6
    assert.ok(Number.isFinite(duration) && duration > 1)
    const title = `REAL EXECUTION - EDITED HIGHLIGHTS - PUBLISHING DISABLED\n${scene.title}`
    const subtitle = `caption-${index}.srt`
    await writeFile(path.join(output, subtitle), `1\n00:00:00,000 --> 00:10:00,000\n${title}\n`)
    const filename = `clip-${index}.mp4`
    const filter = `tpad=stop_mode=clone:stop_duration=${duration},pad=iw:ih+120:0:0:color=0x172621,subtitles=${subtitle}:force_style='FontName=Arial,FontSize=9,Outline=0,Shadow=0,MarginV=7'`
    await encode(['-y', '-ss', String(scene.start), '-t', String(scene.end - scene.start), '-i', rawVideo, '-i', audio, '-map', '0:v:0', '-map', '1:a:0', '-vf', filter, '-af', 'apad', '-t', String(duration), '-c:v', 'libx264', '-preset', 'fast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', '30', '-c:a', 'aac', '-ar', '48000', '-ac', '1', filename])
    clips.push(`file '${filename}'`)
  }
  await writeFile(path.join(output, 'clips.txt'), clips.join('\n'))
  await encode(['-y', '-f', 'concat', '-safe', '0', '-i', 'clips.txt', '-c', 'copy', '-movflags', '+faststart', 'code-evolver-real-demo.mp4'])
  await encode(['-y', '-i', 'code-evolver-real-demo.mp4', '-c:v', 'libvpx-vp9', '-b:v', '0', '-crf', '30', '-deadline', 'realtime', '-cpu-used', '6', '-row-mt', '1', '-c:a', 'libopus', '-b:a', '128k', 'code-evolver-real-demo-with-voice.webm'])
  for (const filename of ['code-evolver-real-demo.mp4', 'code-evolver-real-demo-with-voice.webm']) {
    await encode(['-v', 'error', '-xerror', '-i', filename, '-map', '0:v:0', '-map', '0:a:0', '-f', 'null', '-'])
    const volume = await encode(['-i', filename, '-map', '0:a:0', '-af', 'volumedetect', '-f', 'null', '-'])
    assert.ok(Number(volume.match(/mean_volume: ([-\d.]+) dB/)?.[1]) > -40, 'Narration must not be silent')
  }
  console.log(`Real demo exported: ${output}`)
}

await mkdir(output, { recursive: true })
try {
  assert.equal(process.platform, 'win32', 'This workflow uses Windows local narration')
  await encode(['-version'])
  console.log((await run(process.env.DEMO_COPILOT_EXECUTABLE || 'copilot.exe', ['--version'])).trim())
  console.log('Building the API before launching real agents')
  await run('dotnet', ['build', path.join(repository, 'src/CodeEvolver.Api/CodeEvolver.Api.csproj'), '--no-restore'])
  await writeFile(path.join(output, 'before-status.txt'), await run('git', ['status', '--short']))
  await writeFile(path.join(output, 'before.patch'), await run('git', ['diff', '--binary']))
  await record()
  await edit()
  await writeFile(path.join(output, 'after.patch'), await run('git', ['diff', '--binary']))
  assert.equal(outcome.status, 'completed', outcome.error || 'Real execution was not completed; see recorded evidence')
} catch (error) {
  await save('failure.json', { error: error.message, evolutionId, analysis, outcome, scenes, browserErrors })
  throw error
} finally {
  if (evolutionId && !['completed', 'failed', 'stopped'].includes(outcome?.status)) {
    await fetch(`${apiUrl}/api/evolutions/${evolutionId}/stop`, { method: 'POST', signal: AbortSignal.timeout(5000) }).catch(() => {})
  }
  await context?.close().catch(() => {})
  await browser?.close().catch(() => {})
  await server?.close()
  if (api && api.exitCode === null) await run('taskkill', ['/PID', String(api.pid), '/T', '/F']).catch(() => {})
}