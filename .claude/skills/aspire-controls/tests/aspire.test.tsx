import type { On } from 'claude-code'
import { describe, expect, mock, test } from 'claude-code/testing'
import type { Engine } from 'claude-code/testing'

import { statusLine } from '../hooks/aspire'

const HOME = '/home/me'
const ROOT = `${HOME}/app`
const APPHOST = `${ROOT}/aspire-apphost/apphost.mts`
const DASHBOARD = 'https://localhost:17076/login?t=token'

const RESOURCES = [
  {
    name: 'web-ab12',
    displayName: 'web',
    resourceType: 'Executable',
    state: 'Running',
    healthStatus: 'Healthy',
    urls: [{ name: 'http', url: 'http://localhost:5173' }],
  },
  {
    name: 'worker-cd34',
    displayName: 'worker',
    resourceType: 'Executable',
    state: 'Running',
    healthStatus: 'Healthy',
    urls: [{ name: 'http', url: 'http://localhost:8787' }],
  },
  { name: 'web-installer-ef56', displayName: 'web-installer', resourceType: 'Executable', state: 'Finished', exitCode: 0, urls: [] },
]

// A call on `$` answers its event's hooks as `{ value }`.
const ran = (stdout: string, exitCode = 0) => ({
  value: { exitCode, stdout, stderr: '', isStdoutTruncated: false, isStderrTruncated: false },
})

/** A slash command as the person types it at the fullscreen prompt. */
const typed = (command: string) => ({
  command,
  args: '',
  origin: { kind: 'composer' as const },
  presentation: { isFullscreen: true, columns: 160 },
})

/** The machine beneath the plugin: an Aspire workspace at ROOT, and a fake `aspire` CLI. */
function world(on: On, { hasConfig = true, isRunning = false, startGate = Promise.resolve() } = {}) {
  const state = {
    isRunning,
    runs: [] as string[][],
    toasts: [] as string[],
    commands: [] as string[],
    panes: [] as string[],
  }
  const clock = mock.clock(on)
  mock.env(on, { HOME })

  on('session.start', ($, e) => ({ cwd: e.cwd }))
  on('fs.exists', ($, e) => ({ value: hasConfig && e.path === `${ROOT}/aspire.config.json` }))
  on('fs.read', () => ({ value: JSON.stringify({ appHost: { path: 'aspire-apphost/apphost.mts' } }) }))
  on('process.run', async ($, e) => {
    state.runs.push([...e.argv])
    switch (e.argv[1]) {
      case 'ps':
        return ran(JSON.stringify(state.isRunning ? [{ appHostPath: APPHOST, status: 'running', dashboardUrl: DASHBOARD }] : []))
      case 'describe':
        return ran(JSON.stringify({ resources: RESOURCES }))
      case 'start':
        await startGate
        state.isRunning = true
        return ran(`Starting Aspire AppHost in the background...\n${JSON.stringify({ appHostPath: APPHOST, dashboardUrl: DASHBOARD })}`)
      case 'stop':
        state.isRunning = false
        return ran('stopped successfully.')
      default:
        return ran('', 1)
    }
  })
  on('command.register', ($, e) => {
    state.commands.push(e.name)
    return { value: { command: e.name } }
  })
  on('ui.status', () => ({ value: undefined }))
  on('ui.toast', ($, e) => {
    state.toasts.push(e.text)
    return { value: undefined }
  })
  on('ui.log', () => ({ value: undefined }))
  on('ui.open', ($, e) => {
    state.panes.push(e.id)
    return { value: { isPlaced: true } }
  })
  // The engine's own hint line, which the plugin draws beside its row.
  on('ui.render', { component: 'PromptHint' }, ($, e) => {
    const { Text } = $.ui.resolve(e)
    return <Text dimColor>{e.props.hint}</Text>
  })

  return { state, clock }
}

/** The hint line under the prompt, drawn on a surface: the fullscreen terminal unless told otherwise. */
function mountHint($: Engine, surface: 'terminal' | 'desktop' = 'terminal', isFullscreen = true) {
  // Only the terminal says whether it is fullscreen; the desktop leaves it out.
  const viewport = surface === 'terminal' ? { columns: 160, rows: 40, isFullscreen } : { columns: 160, rows: 40 }

  return $.ui.mount({
    plugin: 'aspire-controls',
    surface,
    component: 'PromptHint',
    props: { isDraft: false, isWorking: false, hint: '? for shortcuts' },
    viewport,
  })
}

/** What the row under the prompt says about Aspire; undefined when it says nothing. */
async function hint($: Engine): Promise<string | undefined> {
  const ui = await mountHint($)
  const line = await ui.find({ type: 'Text', text: /^Aspire: / })
  await ui.unmount()

  return line?.text
}

describe('aspire-controls', () => {
  test('stays out of a workspace with no Aspire config', async ($, on) => {
    const { state, clock } = world(on, { hasConfig: false })
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    expect(state.commands).toEqual([])
    expect(state.runs).toEqual([])
    expect(await hint($)).toBeUndefined()
  })

  test('registers the commands and shows a stopped AppHost', async ($, on) => {
    const { state, clock } = world(on)
    await $.session.start({ cwd: `${ROOT}/src`, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    expect(state.commands).toEqual(['aspire-start', 'aspire-stop', 'aspire-status'])
    expect(await hint($)).toBe('Aspire: stopped')
  })

  test('shows a running AppHost and its resources', async ($, on) => {
    const { state, clock } = world(on, { isRunning: true })
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)
    expect(await hint($)).toBe('Aspire: running · 2/2 up')

    const { text } = await $.command.run(typed('aspire-status'))
    expect(text).toContain('Aspire: running · 2/2 up')
    expect(text).toContain(`Dashboard: ${DASHBOARD}`)
    expect(text).toContain('worker: Running · Healthy  http://localhost:8787')
    expect(state.panes).toEqual(['aspire'])
    expect(state.runs.some(argv => argv[1] === 'describe' && argv.includes(APPHOST))).toBe(true)
  })

  test('/aspire-start starts the AppHost the config names, off the prompt', async ($, on) => {
    const { state, clock } = world(on)
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    const { text } = await $.command.run(typed('aspire-start'))
    expect(text).toContain('Starting Aspire')
    await clock.advance(0)

    const start = state.runs.find(argv => argv[1] === 'start')
    expect(start).toEqual(['aspire', 'start', '--format', 'Json', '--apphost', APPHOST, '--nologo', '--non-interactive'])
    expect(state.toasts).toEqual([`Aspire started. Dashboard: ${DASHBOARD}`])
    expect(await hint($)).toBe('Aspire: running · 2/2 up')
  })

  test('/aspire-stop stops the running AppHost', async ($, on) => {
    const { state, clock } = world(on, { isRunning: true })
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    await $.command.run(typed('aspire-stop'))
    await clock.advance(0)

    expect(state.runs.find(argv => argv[1] === 'stop')).toEqual(['aspire', 'stop', '--apphost', APPHOST, '--nologo', '--non-interactive'])
    expect(state.toasts).toEqual(['Aspire stopped.'])
    expect(await hint($)).toBe('Aspire: stopped')
  })

  test('/aspire-stop says so when nothing runs', async ($, on) => {
    const { state, clock } = world(on)
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    await $.command.run(typed('aspire-stop'))
    await clock.advance(0)

    expect(state.runs.some(argv => argv[1] === 'stop')).toBe(false)
    expect(state.toasts).toEqual(['Aspire is not running.'])
  })

  test('/aspire-stop while a start runs says so instead of doing nothing', async ($, on) => {
    let release = () => {}
    const startGate = new Promise<void>(resolve => {
      release = resolve
    })
    const { state, clock } = world(on, { startGate })
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    await $.command.run(typed('aspire-start'))
    await clock.advance(0)
    expect(await hint($)).toBe('Aspire: starting…')

    await $.command.run(typed('aspire-stop'))
    await clock.advance(0)
    expect(state.toasts).toEqual(['Aspire is already starting; try again once it is done.'])
    expect(state.runs.some(argv => argv[1] === 'stop')).toBe(false)

    release()
    await clock.advance(0)
    expect(await hint($)).toBe('Aspire: running · 2/2 up')
  })

  test('the row under the prompt starts and stops it with a click, beside the engine’s own hint', async ($, on) => {
    const { state, clock } = world(on)
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    for (const surface of ['terminal', 'desktop'] as const) {
      const ui = await mountHint($, surface)
      expect(await ui.find({ type: 'Text', text: '? for shortcuts' })).toBeDefined()
      expect(await ui.find({ type: 'Text', text: 'Aspire: stopped' })).toBeDefined()
      expect(await ui.find({ key: 'stop' })).toBeUndefined()

      await ui.press({ key: 'start' })
      await clock.advance(0)
      expect(await ui.find({ type: 'Text', text: 'Aspire: running · 2/2 up' })).toBeDefined()
      expect(await ui.find({ key: 'start' })).toBeUndefined()

      await ui.press({ key: 'stop' })
      await clock.advance(0)
      expect(await ui.find({ type: 'Text', text: 'Aspire: stopped' })).toBeDefined()

      await ui.press({ key: 'details' })
      expect(state.panes.at(-1)).toBe('aspire')
      await ui.unmount()
    }
    expect(state.runs.filter(argv => argv[1] === 'start' || argv[1] === 'stop')).toHaveLength(4)
  })

  test('the terminal’s main screen, where nothing is clicked, gets the command instead of buttons', async ($, on) => {
    const { clock } = world(on)
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    const ui = await mountHint($, 'terminal', false)
    expect(await ui.find({ type: 'Text', text: 'Aspire: stopped' })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: '/aspire-start' })).toBeDefined()
    expect(await ui.find({ type: 'Button' })).toBeUndefined()
    await ui.unmount()
  })

  test('the pane offers Start, then Stop, on every surface', async ($, on) => {
    const { state, clock } = world(on)
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    for (const surface of ['terminal', 'desktop'] as const) {
      state.isRunning = false
      await $.command.run(typed('aspire-status'))
      const ui = await $.ui.mount({
        plugin: 'aspire-controls',
        surface,
        component: 'Pane',
        requestId: 'aspire',
        props: {
          title: 'Aspire',
          isFocused: true,
          bodyColumns: 60,
          placement: 'dock',
          scroll: { offset: 0, bodyRows: 20 },
          view: {},
        },
      })
      expect(await ui.find({ type: 'Text', text: 'Aspire: stopped' })).toBeDefined()
      expect(await ui.find({ key: 'stop' })).toBeUndefined()

      await ui.press({ key: 'start' })
      await clock.advance(0)
      expect(await ui.find({ type: 'Text', text: 'Aspire: running · 2/2 up' })).toBeDefined()
      expect(await ui.find({ type: 'Text', text: 'worker' })).toBeDefined()
      expect(await ui.find({ key: 'start' })).toBeUndefined()

      await ui.press({ key: 'stop' })
      await clock.advance(0)
      expect(await ui.find({ type: 'Text', text: 'Aspire: stopped' })).toBeDefined()
      await ui.unmount()
    }
  })
})

describe('statusLine', () => {
  const resource = { name: 'worker', type: 'Executable', state: 'Running', health: 'Healthy', exitCode: null, urls: [] }
  const running = { appHostPath: APPHOST, dashboardUrl: null, error: null }

  test('names what is down', async () => {
    const snapshot = {
      ...running,
      resources: [{ ...resource, name: 'web' }, { ...resource, health: 'Unhealthy' }],
    }
    expect(statusLine(snapshot, null)).toBe('Aspire: running · 1/2 up · worker Unhealthy')
  })

  test('names what is still starting', async () => {
    const snapshot = { ...running, resources: [{ ...resource, state: 'Starting', health: null }] }
    expect(statusLine(snapshot, null)).toBe('Aspire: running · 0/1 up · worker starting')
  })

  test('says when ps itself failed', async () => {
    const snapshot = { appHostPath: null, dashboardUrl: null, resources: [], error: 'aspire: not found' }
    expect(statusLine(snapshot, null)).toBe('Aspire: unknown (aspire ps failed)')
  })
})
