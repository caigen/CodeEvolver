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
const workspace = path.resolve(root, '../..')
const repository = path.resolve('D:/work/CodeEvolver')
const narration = JSON.parse(await readFile(path.join(root, 'scripts/narration.json'), 'utf8'))
assert.equal(narration.length, 4, 'Provide four narration sections: purpose, analyzer, evolution, GitHub')
const args = process.argv.slice(2)
assert.ok(args.length === 0 || (args.length === 2 && args[0] === '--from'), 'Usage: demo:real [--from <saved-real-run>]')
const source = args.length ? path.resolve(args[1]) : undefined
const output = path.join(source || path.join(workspace, 'artifacts/demo'), `${source ? 'edit' : 'real'}-${new Date().toISOString().replaceAll(/[:.]/g, '-')}`)
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
let voiceSegments

function run(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: workspace, windowsHide: true, ...options })
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
  const dll = path.join(workspace, 'src/CodeEvolver.Api/bin/Debug/net10.0/CodeEvolver.Api.dll')
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
  assert.equal(await page.getByLabel('Cloned repository path').inputValue(), repository)
  await scene(narration[0].title, narration[0].text)
  await scene(narration[1].title, narration[1].text, page.getByRole('heading', { name: 'Analyzer Team', exact: true }))
  await page.getByLabel('Evolution direction').fill('Fix a user-flow bug and improve the experience in the analyzer-to-evolution journey.')
  await page.getByLabel('Scope Path', { exact: true }).fill('src/code-evolver-web/src')
  await page.getByLabel('Custom target branch', { exact: true }).fill('main')
  const previewResponse = page.waitForResponse((reply) => reply.url() === `${apiUrl}/api/evolutions` && reply.request().method() === 'POST')
  await page.getByRole('button', { name: 'Create evolution', exact: true }).click()
  assert.ok((await previewResponse).ok())
  await scene(narration[2].title, narration[2].text, page.getByRole('heading', { name: 'Agent team', exact: true }))
  await page.locator('input[type=file]').setInputFiles(path.join(root, 'scripts/demo-feedback.json'))
  await scene('Analyze user-flow feedback', 'Upload feedback to get a recommended direction from the Analyzer Agent Team.', page.getByRole('button', { name: 'Analyze data', exact: true }))
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
  await scene('Analyzer result | Turn feedback into a direction', 'Apply the actual analyzer recommendation to an evolution.', page.getByRole('button', { name: 'Apply to evolution', exact: true }))
  await scene('Analyzer Agent Team | Completed execution', 'Review the completed analyzer stages and their actual event history. The recommendation is evidence from this run, not a simulated result.', page.getByRole('heading', { name: 'Analysis Timeline', exact: true }))
  await page.getByRole('button', { name: 'Apply to evolution', exact: true }).click()
  const direction = [
    'Fix a reproducible user-flow bug and improve the experience in the analyze, apply, create, and start evolution journey. Inspect the existing code first. Keep repository, direction, and scope consistent, preserve entered work, and provide clear validation for invalid input.',
    `Analyzer recommendation from this run: ${analysis.direction}`,
    'Plan exactly ONE bounded work item based on a concrete bug found in the existing implementation. Make a small functional improvement, not a redesign. Preserve the Visual Studio light theme, larger agent names, log search, CSV export, and custom selection controls. Use existing React and lucide conventions. Reuse dependencies. Do not add packages.',
    `The only repository you may modify is ${repository}. Only edit files under src/code-evolver-web/src. Do not edit C:/work/CodeEvolver, scripts, package files, README, HumanDesign, API, or any existing unrelated changes. Preserve all pre-existing work. Do not create branches, stage, commit, push, or create pull requests. Do not invoke the demo recorder. Do not launch persistent servers.`,
    'Validate with npm run build --prefix src/code-evolver-web and npm run lint --prefix src/code-evolver-web. Reproduce the chosen bug and verify the fix using focused checks with available tools. Report any validation that cannot run without claiming it passed.',
    'Final reporting must state exactly what changed and what validation actually ran. Publishing is disabled.',
  ].join('\n\n')
  await page.getByLabel('Evolution direction').fill(direction)
  await page.getByLabel('Scope Path', { exact: true }).fill('src/code-evolver-web/src')
  await page.getByLabel('Custom target branch', { exact: true }).fill('main')
  const createResponse = page.waitForResponse((reply) => reply.url() === `${apiUrl}/api/evolutions` && reply.request().method() === 'POST')
  await page.getByRole('button', { name: 'Create evolution', exact: true }).click()
  const created = await createResponse
  assert.ok(created.ok())
  const createdEvolution = await created.json()
  assert.equal(path.resolve(createdEvolution.repositoryPath), repository)
  evolutionId = createdEvolution.id
  await save('request.json', { repository, direction, scope: 'src/code-evolver-web/src', publishing: false })
  await page.getByRole('button', { name: 'Start', exact: true }).waitFor()
  await scene('Evolve from a direction', 'Start the Code Evolver Agent Team to implement and validate a focused improvement in the separate demo clone.', page.getByRole('button', { name: 'Start', exact: true }))
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
  await scene('Code Evolver Agent Team | Work item and validation result', 'The day plan records the work item and its reported result. Review this alongside the event timeline to see what the agents actually changed and which checks they performed.', page.getByRole('heading', { name: 'Agent day plan', exact: true }))
  await page.setContent(`<!doctype html><html lang="en"><head><title>Code Evolver</title></head><body style="margin:0;background:#eeeef2;color:#1e1e1e;font-family:Segoe UI,sans-serif"><main style="padding:180px 150px"><h1 style="font-size:72px">Code Evolver</h1><p style="font-size:28px">Your direction. Your data. Your next repository improvement.</p><a style="font-size:40px;color:#007acc" href="${projectUrl}">${projectUrl}</a></main></body></html>`)
  await scene(narration[3].title, narration[3].text)
  await context.close()
  context = undefined
  rawVideo = await video.path()
  await save('scenes.json', { realExecution: true, repository, apiUrl, outcome: outcome.status, browserErrors, rawVideo, scenes })
}

async function prepareNarration() {
  await save('narration-input.json', narration)
  const directory = path.join(output, 'narration')
  const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe')
  console.log((await run(powershell, ['-NoProfile', '-NonInteractive', '-File', path.join(root, 'scripts/synthesize-narration.ps1'), '-InputPath', path.join(output, 'narration-input.json'), '-OutputDirectory', directory])).trim())
  const metadata = JSON.parse((await readFile(path.join(directory, 'narration.json'), 'utf8')).replace(/^\uFEFF/, ''))
  assert.equal(metadata.segments.length, 4)
  voiceSegments = []
  for (const segment of metadata.segments) {
    const audio = path.join(directory, segment.file)
    const probe = await encode(['-nostats', '-i', audio, '-progress', 'pipe:2', '-f', 'null', '-'])
    const duration = Math.ceil((Number([...probe.matchAll(/^out_time_us=(\d+)/gm)].at(-1)?.[1]) / 1000000 + 0.5) * 30) / 30
    assert.ok(Number.isFinite(duration) && duration > 1)
    voiceSegments.push({ audio, duration })
  }
  const seconds = voiceSegments.reduce((total, segment) => total + segment.duration, 0)
  assert.ok(seconds <= 59, `Narration needs ${seconds.toFixed(1)} seconds. Shorten scripts/narration.json to fit the 60-second video limit (59-second edit budget).`)
  console.log(`Four-section narration ready: ${seconds.toFixed(1)} seconds; maximum export: 60 seconds`)
}

async function edit() {
  const selected = [
    scenes[0],
    scenes.find(scene => scene.title === 'Analyzer Agent Team | Completed execution'),
    scenes.find(scene => scene.title === `Actual outcome | ${outcome.status}`),
    scenes.find(scene => scene.title === `Visit ${projectUrl}`),
  ]
  assert.ok(selected.every(Boolean), 'Missing required purpose, analyzer result, evolution outcome, or GitHub scene')
  const clips = []
  const chapters = []
  let elapsed = 0
  for (const [index, scene] of selected.entries()) {
    const { audio, duration } = voiceSegments[index]
    assert.ok(scene.end > scene.start && scene.start >= 0, 'Invalid source scene timing')
    const heading = index === 2 ? `${narration[index].title} | ${outcome.status}` : narration[index].title
    const title = `REAL EXECUTION - EDITED HIGHLIGHTS - PUBLISHING DISABLED\n${heading}`
    const subtitle = `caption-${index}.srt`
    await writeFile(path.join(output, subtitle), `1\n00:00:00,000 --> 00:10:00,000\n${title}\n`)
    const filename = `clip-${index}.mp4`
    const filter = `tpad=stop_mode=clone:stop_duration=${duration},pad=iw:ih+120:0:0:color=0x1e1e1e,subtitles=${subtitle}:force_style='FontName=Arial,FontSize=9,Outline=0,Shadow=0,MarginV=7'`
    await encode(['-y', '-ss', String(scene.start), '-t', String(scene.end - scene.start), '-i', rawVideo, '-i', audio, '-map', '0:v:0', '-map', '1:a:0', '-vf', filter, '-af', 'apad', '-t', String(duration), '-c:v', 'libx264', '-preset', 'fast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', '30', '-c:a', 'aac', '-ar', '48000', '-ac', '1', filename])
    await encode(['-y', '-ss', '1', '-i', filename, '-frames:v', '1', `preview-${index + 1}.png`])
    clips.push(`file '${filename}'`)
    chapters.push({ ...narration[index], start: elapsed, duration, sourceScene: scene.title })
    elapsed += duration
  }
  await writeFile(path.join(output, 'clips.txt'), clips.join('\n'))
  await encode(['-y', '-f', 'concat', '-safe', '0', '-i', 'clips.txt', '-c', 'copy', '-movflags', '+faststart', 'code-evolver-real-demo.mp4'])
  await encode(['-y', '-i', 'code-evolver-real-demo.mp4', '-c:v', 'libvpx-vp9', '-b:v', '0', '-crf', '30', '-deadline', 'realtime', '-cpu-used', '6', '-row-mt', '1', '-c:a', 'libopus', '-b:a', '128k', 'code-evolver-real-demo-with-voice.webm'])
  const validation = []
  for (const filename of ['code-evolver-real-demo.mp4', 'code-evolver-real-demo-with-voice.webm']) {
    const decoded = await encode(['-v', 'error', '-xerror', '-i', filename, '-map', '0:v:0', '-map', '0:a:0', '-progress', 'pipe:2', '-f', 'null', '-'])
    const seconds = Number([...decoded.matchAll(/^out_time_us=(\d+)/gm)].at(-1)?.[1]) / 1000000
    assert.ok(seconds > 0 && seconds <= 60, `${filename} must be at most 60 seconds; measured ${seconds}`)
    const volume = await encode(['-i', filename, '-map', '0:a:0', '-af', 'volumedetect', '-f', 'null', '-'])
    const meanDb = Number(volume.match(/mean_volume: ([-\d.]+) dB/)?.[1])
    assert.ok(meanDb > -40, 'Narration must not be silent')
    validation.push({ filename, seconds, meanDb, decode: 'passed' })
  }
  await save('video-validation.json', { source, realExecution: true, outcome: outcome.status, maxSeconds: 60, chapters, validation })
  console.log(`Validated real demo (${elapsed.toFixed(1)} seconds): ${output}`)
}

await mkdir(output, { recursive: true })
try {
  assert.equal(process.platform, 'win32', 'This workflow uses Windows local narration')
  await encode(['-version'])
  await prepareNarration()
  if (source) {
    const saved = JSON.parse(await readFile(path.join(source, 'scenes.json'), 'utf8'))
    assert.equal(saved.realExecution, true, 'Only saved real execution may be edited')
    assert.ok(['completed', 'failed', 'stopped'].includes(saved.outcome), 'Saved execution must be terminal')
    scenes.push(...saved.scenes)
    rawVideo = saved.rawVideo
    outcome = { status: saved.outcome }
    await save('scenes.json', saved)
    for (const filename of ['analysis.json', 'evolution.json']) {
      await writeFile(path.join(output, filename), await readFile(path.join(source, filename)))
    }
    console.log(`Reusing saved real execution: ${source}; no agents will run`)
  } else {
  assert.notEqual(repository.toLowerCase(), workspace.toLowerCase(), 'Demo changes must target the separate clone')
  assert.equal((await run('git', ['rev-parse', '--show-toplevel'], { cwd: repository })).trim().replaceAll('\\', '/').toLowerCase(), repository.replaceAll('\\', '/').toLowerCase())
  console.log(`Demo target: ${repository}; artifacts: ${output}`)
  console.log((await run(process.env.DEMO_COPILOT_EXECUTABLE || 'copilot.exe', ['--version'])).trim())
  console.log('Building the API before launching real agents')
  await run('dotnet', ['build', path.join(workspace, 'src/CodeEvolver.Api/CodeEvolver.Api.csproj'), '--no-restore'])
  await writeFile(path.join(output, 'before-status.txt'), await run('git', ['status', '--short'], { cwd: repository }))
  await writeFile(path.join(output, 'before.patch'), await run('git', ['diff', '--binary'], { cwd: repository }))
  await record()
  await writeFile(path.join(output, 'after.patch'), await run('git', ['diff', '--binary'], { cwd: repository }))
  await writeFile(path.join(output, 'after-status.txt'), await run('git', ['status', '--short'], { cwd: repository }))
  }
  await edit()
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