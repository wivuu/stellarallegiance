import { atom, read, update } from 'claude-code'
import type { EngineInterface, ProcessRunResult, Register, Timer } from 'claude-code'

import type { AspireBusy, AspireSnapshot, AspireWorkspace } from '../types'
import {
  appHostFromConfig,
  conditionOf,
  describeState,
  isSettling,
  lastLine,
  overallCondition,
  parentOf,
  parseDescribe,
  parseJson,
  parsePs,
  pickAppHost,
  relativeTo,
  report,
  statusLine,
} from './aspire'

const PANE = 'aspire'
const IDLE_CHECK_MS = 15_000
const SETTLING_CHECK_MS = 3_000
const START_TIMEOUT_MS = 5 * 60_000
const STOP_TIMEOUT_MS = 2 * 60_000
const CHECK_TIMEOUT_MS = 20_000

const COMMANDS = [
  { name: 'aspire-start', description: 'Start this workspace’s Aspire AppHost in the background' },
  { name: 'aspire-stop', description: 'Stop this workspace’s running Aspire AppHost' },
  { name: 'aspire-status', description: 'Show the Aspire AppHost’s resources and open the Aspire pane' },
]

const COLOR = { up: 'success', pending: 'warning', down: 'error', done: 'inactive' } as const

const workspace = atom({ plugin: 'aspire-controls', key: 'workspace' } as const, null)
const snapshot = atom({ plugin: 'aspire-controls', key: 'snapshot' } as const, null)
const busy = atom({ plugin: 'aspire-controls', key: 'busy' } as const, null)

// The module's own: a reload starts them over, and session.start sets them again.
let bin = 'aspire'
let checking: Promise<void> | undefined
let timer: Timer | undefined

const messageOf = (error: unknown) => (error instanceof Error ? error.message : String(error))

const failure = (ran: ProcessRunResult) =>
  lastLine(ran.stderr) || lastLine(ran.stdout) || `exit ${ran.exitCode}`

async function aspire($: EngineInterface, args: string[], timeoutMs: number): Promise<ProcessRunResult> {
  const root = (await read($, workspace))?.root

  return $.process.run([bin, ...args, '--nologo', '--non-interactive'], { cwd: root, timeoutMs })
}

/** The nearest folder at or above `cwd`, short of the home folder, holding an Aspire config. */
async function findWorkspace($: EngineInterface, cwd: string): Promise<AspireWorkspace | null> {
  const home = await $.env.get('HOME')
  for (let dir = cwd; dir !== home; dir = parentOf(dir)) {
    const configs = [
      { path: `${dir}/aspire.config.json`, dir },
      { path: `${dir}/.aspire/settings.json`, dir: `${dir}/.aspire` },
    ]
    for (const config of configs) {
      if (await $.fs.exists(config.path)) {
        const text = await $.fs.read(config.path).catch(() => '')
        return { root: dir, appHostPath: appHostFromConfig(typeof text === 'string' ? text : '', config.dir) }
      }
    }
    if (dir === '/') {
      break
    }
  }

  return null
}

async function setBusy($: EngineInterface, doing: AspireBusy) {
  await update($, busy, () => doing)
}

function schedule($: EngineInterface, ms: number) {
  timer?.cancel()
  timer = $.clock.after(ms, () => void check($))
}

/** Asks `aspire ps`, and `aspire describe` when the workspace's AppHost runs; one check at a time. */
function check($: EngineInterface): Promise<void> {
  checking ??= checkNow($).finally(() => {
    checking = undefined
  })

  return checking
}

async function checkNow($: EngineInterface) {
  const found = await read($, workspace)
  if (found === null) {
    return
  }

  let next: AspireSnapshot
  try {
    const ps = await aspire($, ['ps', '--format', 'Json'], CHECK_TIMEOUT_MS)
    if (ps.exitCode !== 0) {
      throw new Error(`aspire ps: ${failure(ps)}`)
    }
    const host = pickAppHost(parsePs(ps.stdout), found)
    const described =
      host === undefined
        ? undefined
        : await aspire($, ['describe', '--format', 'Json', '--apphost', host.appHostPath], CHECK_TIMEOUT_MS)
    next = {
      appHostPath: host?.appHostPath ?? null,
      dashboardUrl: host?.dashboardUrl ?? null,
      resources: described?.exitCode === 0 ? parseDescribe(described.stdout) : [],
      error: null,
    }
  } catch (error) {
    const previous = await read($, snapshot)
    next = { appHostPath: null, dashboardUrl: null, resources: [], ...previous, error: messageOf(error) }
  }

  await update($, snapshot, () => next)
  schedule($, isSettling(next, await read($, busy)) ? SETTLING_CHECK_MS : IDLE_CHECK_MS)
}

/** How a start or stop went: a toast, and a dim transcript line the model does not read. */
function tell($: EngineInterface, text: string) {
  $.ui.toast(text, { timeoutMs: 6000 })
  $.ui.log(text)
}

async function start($: EngineInterface) {
  const found = await read($, workspace)
  const doing = await read($, busy)
  if (found === null) {
    return
  }
  if (doing !== null) {
    tell($, `Aspire is already ${doing}; try again once it is done.`)
    return
  }

  await setBusy($, 'starting')
  try {
    await check($)
    if ((await read($, snapshot))?.appHostPath) {
      tell($, 'Aspire is already running.')
      return
    }
    const appHost = found.appHostPath === null ? [] : ['--apphost', found.appHostPath]
    const ran = await aspire($, ['start', '--format', 'Json', ...appHost], START_TIMEOUT_MS)
    if (ran.exitCode !== 0) {
      tell($, `Aspire failed to start: ${failure(ran)}`)
      return
    }
    const started = parseJson(ran.stdout) as { dashboardUrl?: unknown } | undefined
    const dashboard = typeof started?.dashboardUrl === 'string' ? ` Dashboard: ${started.dashboardUrl}` : ''
    tell($, `Aspire started.${dashboard}`)
  } catch (error) {
    tell($, `Aspire failed to start: ${messageOf(error)}`)
  } finally {
    await setBusy($, null)
    await check($)
  }
}

async function stop($: EngineInterface) {
  const doing = await read($, busy)
  if ((await read($, workspace)) === null) {
    return
  }
  if (doing !== null) {
    tell($, `Aspire is already ${doing}; try again once it is done.`)
    return
  }

  await setBusy($, 'stopping')
  try {
    await check($)
    const appHostPath = (await read($, snapshot))?.appHostPath
    if (!appHostPath) {
      tell($, 'Aspire is not running.')
      return
    }
    const ran = await aspire($, ['stop', '--apphost', appHostPath], STOP_TIMEOUT_MS)
    tell($, ran.exitCode === 0 ? 'Aspire stopped.' : `Aspire failed to stop: ${failure(ran)}`)
  } catch (error) {
    tell($, `Aspire failed to stop: ${messageOf(error)}`)
  } finally {
    await setBusy($, null)
    await check($)
  }
}

/** Starts or stops off the hook that asked, so the prompt stays free while the CLI works. */
function launch($: EngineInterface, action: 'start' | 'stop') {
  $.clock.after(0, () => void (action === 'start' ? start($) : stop($)))
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    timer?.cancel()
    const found = await findWorkspace($, e.cwd)
    await update($, workspace, () => found)
    await update($, busy, () => null)
    // The row under the prompt shows the state now; clear the status line an older load may have left.
    $.ui.status(undefined)
    if (found === null) {
      return next(e)
    }

    const home = await $.env.get('HOME')
    const installed = home === undefined ? undefined : `${home}/.aspire/bin/aspire`
    bin = installed !== undefined && (await $.fs.exists(installed)) ? installed : 'aspire'
    for (const command of COMMANDS) {
      await $.command.register(command)
    }
    void check($)

    return next(e)
  })

  on('command.run', { command: 'aspire-start' }, async $ => {
    launch($, 'start')
    return { text: 'Starting Aspire in the background; a toast says when it is up.' }
  })

  on('command.run', { command: 'aspire-stop' }, async $ => {
    launch($, 'stop')
    return { text: 'Stopping Aspire in the background; a toast says when it is down.' }
  })

  on('command.run', { command: 'aspire-status' }, async $ => {
    const found = await read($, workspace)
    if (found === null) {
      return { text: 'No aspire.config.json in this workspace.' }
    }
    await check($)
    await $.ui.open({ id: PANE, title: 'Aspire' })

    return { text: report(await read($, snapshot), await read($, busy), found) }
  })

  // Aspire started or stopped from Bash, by the person or the model, shows without waiting for the next check.
  on('tool.call', { tool: 'Bash' }, async ($, e, next) => {
    const ran = await next(e)
    if (/\baspire\s+(start|stop|run|resource)\b/.test(e.command)) {
      void check($)
    }

    return ran
  })

  // The hint line under the prompt: the engine's own line, then the AppHost's state and a Start or Stop button.
  // Buttons only where a click reaches them: the fullscreen terminal and the desktop app, not the terminal's
  // main screen, which gets the slash command's name instead.
  on('ui.render', { component: 'PromptHint' }, async ($, e, next) => {
    const engine = await next(e)
    const found = await read($, workspace)
    if (found === null) {
      return engine
    }

    const { Box, Button, Text } = $.ui.resolve(e)
    const [current, doing] = await Promise.all([read($, snapshot), read($, busy)])
    const isRunning = Boolean(current?.appHostPath)
    const isClickable = e.viewport?.isFullscreen !== false

    return (
      <Box>
        <Box flexGrow={1} flexShrink={1}>
          {engine}
        </Box>
        <Box gap={1} flexShrink={0}>
          <Text color={COLOR[overallCondition(current, doing)]}>●</Text>
          <Text dimColor>{statusLine(current, doing)}</Text>
          {isClickable && doing === null && !isRunning && (
            <Button key="start" label="Start" variant="primary" onPress={() => launch($, 'start')} />
          )}
          {isClickable && doing === null && isRunning && (
            <Button key="stop" label="Stop" onPress={() => launch($, 'stop')} />
          )}
          {isClickable && (
            <Button key="details" label="Details" dimColor onPress={() => void $.ui.open({ id: PANE, title: 'Aspire' })} />
          )}
          {!isClickable && doing === null && <Text dimColor>{isRunning ? '/aspire-stop' : '/aspire-start'}</Text>}
        </Box>
      </Box>
    )
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Button, Link, Text } = $.ui.resolve(e)
    const [found, current, doing] = await Promise.all([read($, workspace), read($, snapshot), read($, busy)])
    if (found === null) {
      return <Text dimColor>No aspire.config.json in this workspace.</Text>
    }

    const isRunning = Boolean(current?.appHostPath)
    const resources = current?.resources ?? []
    const nameWidth = Math.max(8, ...resources.map(resource => resource.name.length)) + 1
    const appHost = current?.appHostPath ?? found.appHostPath

    return (
      <Box flexDirection="column">
        <Text bold>{statusLine(current, doing)}</Text>
        {appHost !== null && <Text dimColor>{relativeTo(found.root, appHost)}</Text>}
        {current?.dashboardUrl && (
          <Box gap={1}>
            <Text dimColor>Dashboard</Text>
            <Link href={current.dashboardUrl} label="open in browser" />
          </Box>
        )}
        {current?.error && <Text color="error">{current.error}</Text>}
        {resources.length > 0 && (
          <Box flexDirection="column" marginTop={1}>
            {resources.map(resource => {
              const condition = conditionOf(resource)
              return (
                <Box key={`resource-${resource.name}`} gap={1}>
                  <Text color={COLOR[condition]}>●</Text>
                  <Box width={nameWidth} flexShrink={0}>
                    <Text dimColor={condition === 'done'} wrap="truncate-end">
                      {resource.name}
                    </Text>
                  </Box>
                  <Text dimColor>{describeState(resource)}</Text>
                  {resource.urls[0] !== undefined && <Link href={resource.urls[0]} />}
                </Box>
              )
            })}
          </Box>
        )}
        <Box gap={1} marginTop={1}>
          {doing === null && !isRunning && (
            <Button key="start" label="Start" hotkey="s" variant="primary" onPress={() => launch($, 'start')} />
          )}
          {doing === null && isRunning && (
            <Button key="stop" label="Stop" hotkey="x" onPress={() => launch($, 'stop')} />
          )}
          <Button key="refresh" label="Refresh" hotkey="r" onPress={() => void check($)} />
        </Box>
      </Box>
    )
  })
}
