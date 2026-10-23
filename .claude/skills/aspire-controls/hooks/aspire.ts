// Reading the Aspire CLI's output and summing it up. Pure: nothing here calls the engine.

import type { AspireBusy, AspireResource, AspireSnapshot, AspireWorkspace } from '../types'

/** One AppHost `aspire ps` lists as running, anywhere on the machine. */
export type RunningAppHost = { appHostPath: string; dashboardUrl: string | null }

/** How a resource is doing: `done` is a one-shot that finished well (an installer). */
export type Condition = 'up' | 'pending' | 'down' | 'done'

const ANSI = /\x1b\[[0-9;?]*[A-Za-z]/g
const PENDING = new Set(['Starting', 'Waiting', 'NotStarted', 'Building', 'Stopping', 'Unknown'])

export const stripAnsi = (text: string): string => text.replace(ANSI, '')

/** The last non-empty line of CLI output, plain and short: what a failure toast says. */
export function lastLine(text: string): string {
  const lines = stripAnsi(text)
    .split('\n')
    .map(line => line.trim())
    .filter(line => line !== '')
  const line = lines[lines.length - 1] ?? ''

  return line.length > 160 ? `${line.slice(0, 157)}...` : line
}

/** The JSON in CLI output that may carry progress lines around it. */
export function parseJson(text: string): unknown {
  const clean = stripAnsi(text)
  const start = clean.search(/^\s*[[{]/m)
  const end = Math.max(clean.lastIndexOf('}'), clean.lastIndexOf(']'))
  if (start < 0 || end < start) {
    return undefined
  }
  try {
    return JSON.parse(clean.slice(start, end + 1))
  } catch {
    return undefined
  }
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const stringOr = (value: unknown, fallback: string | null) =>
  typeof value === 'string' ? value : fallback

export function parsePs(stdout: string): RunningAppHost[] {
  const list = parseJson(stdout)

  return Array.isArray(list)
    ? list.filter(isRecord).flatMap(item =>
        typeof item.appHostPath === 'string'
          ? [{ appHostPath: item.appHostPath, dashboardUrl: stringOr(item.dashboardUrl, null) }]
          : [],
      )
    : []
}

export function parseDescribe(stdout: string): AspireResource[] {
  const value = parseJson(stdout)
  const list = isRecord(value) && Array.isArray(value.resources) ? value.resources : []

  return list.filter(isRecord).map(item => ({
    name: stringOr(item.displayName, null) ?? stringOr(item.name, '?') ?? '?',
    type: stringOr(item.resourceType, '') ?? '',
    state: stringOr(item.state, 'Unknown') ?? 'Unknown',
    health: stringOr(item.healthStatus, null),
    exitCode: typeof item.exitCode === 'number' ? item.exitCode : null,
    urls: (Array.isArray(item.urls) ? item.urls : [])
      .filter(isRecord)
      .flatMap(url => (typeof url.url === 'string' ? [url.url] : [])),
  }))
}

/** The workspace's AppHost among the running ones: the one its config names, else one under its root. */
export function pickAppHost(
  running: readonly RunningAppHost[],
  workspace: AspireWorkspace,
): RunningAppHost | undefined {
  return (
    running.find(host => host.appHostPath === workspace.appHostPath) ??
    running.find(host => host.appHostPath.startsWith(`${workspace.root}/`))
  )
}

export function conditionOf(resource: AspireResource): Condition {
  const { state, health, exitCode } = resource
  if (state === 'Running') {
    return health === null || health === 'Healthy' ? 'up' : 'down'
  }
  if (state === 'Finished' && (exitCode === 0 || exitCode === null)) {
    return 'done'
  }

  return PENDING.has(state) ? 'pending' : 'down'
}

/** `Running · Healthy`, `Exited (1)`: a resource's state in a few words. */
export function describeState(resource: AspireResource): string {
  const exit = resource.exitCode !== null && resource.state !== 'Running' ? ` (exit ${resource.exitCode})` : ''
  const health = resource.health !== null && resource.state === 'Running' ? ` · ${resource.health}` : ''

  return `${resource.state}${health}${exit}`
}

/** Whether something is still on its way, so the next check comes sooner. */
export function isSettling(snapshot: AspireSnapshot | null, busy: AspireBusy): boolean {
  return busy !== null || (snapshot?.resources.some(resource => conditionOf(resource) === 'pending') ?? false)
}

/** One color for the whole AppHost: stopped reads as `done`, the dim gray. */
export function overallCondition(snapshot: AspireSnapshot | null, busy: AspireBusy): Condition {
  if (busy !== null || snapshot === null) {
    return 'pending'
  }
  if (snapshot.appHostPath === null) {
    return snapshot.error === null ? 'done' : 'down'
  }
  const conditions = snapshot.resources.map(conditionOf)

  return conditions.includes('down') ? 'down' : conditions.includes('pending') ? 'pending' : 'up'
}

/** The AppHost's state in one line, as the row under the prompt shows it. */
export function statusLine(snapshot: AspireSnapshot | null, busy: AspireBusy): string {
  if (busy !== null) {
    return `Aspire: ${busy}…`
  }
  if (snapshot === null) {
    return 'Aspire: checking…'
  }
  if (snapshot.appHostPath === null) {
    return snapshot.error === null ? 'Aspire: stopped' : 'Aspire: unknown (aspire ps failed)'
  }

  const services = snapshot.resources.filter(resource => conditionOf(resource) !== 'done')
  const up = services.filter(resource => conditionOf(resource) === 'up')
  const down = services.filter(resource => conditionOf(resource) === 'down')
  const pending = services.filter(resource => conditionOf(resource) === 'pending')
  const line = `Aspire: running · ${up.length}/${services.length} up`

  if (down.length > 0) {
    return `${line} · ${down.map(resource => `${resource.name} ${resource.health ?? resource.state}`).join(', ')}`
  }
  if (pending.length > 0) {
    return `${line} · ${pending.map(resource => resource.name).join(', ')} starting`
  }

  return line
}

/** What `/aspire-status` prints, which the model reads too. */
export function report(
  snapshot: AspireSnapshot | null,
  busy: AspireBusy,
  workspace: AspireWorkspace,
): string {
  const lines = [statusLine(snapshot, busy)]
  if (snapshot?.error) {
    lines.push(`Last check failed: ${snapshot.error}`)
  }
  if (snapshot?.appHostPath) {
    lines.push(`AppHost: ${relativeTo(workspace.root, snapshot.appHostPath)}`)
  }
  if (snapshot?.dashboardUrl) {
    lines.push(`Dashboard: ${snapshot.dashboardUrl}`)
  }
  for (const resource of snapshot?.resources ?? []) {
    const url = resource.urls[0] === undefined ? '' : `  ${resource.urls[0]}`
    lines.push(`  ${resource.name}: ${describeState(resource)}${url}`)
  }

  return lines.join('\n')
}

export function relativeTo(root: string, path: string): string {
  return path.startsWith(`${root}/`) ? path.slice(root.length + 1) : path
}

/** `a/b/../c/./d` to `a/c/d`. */
export function normalize(path: string): string {
  const parts: string[] = []
  for (const part of path.split('/')) {
    if (part === '..') {
      parts.pop()
    } else if (part !== '.' && part !== '') {
      parts.push(part)
    }
  }

  return `${path.startsWith('/') ? '/' : ''}${parts.join('/')}`
}

export function parentOf(dir: string): string {
  const cut = dir.lastIndexOf('/')

  return cut <= 0 ? '/' : dir.slice(0, cut)
}

/** The AppHost a config names, absolute: `aspire.config.json`'s `appHost.path`, or `.aspire/settings.json`'s `appHostPath`. */
export function appHostFromConfig(text: string, configDir: string): string | null {
  let config: unknown
  try {
    config = JSON.parse(text)
  } catch {
    return null
  }
  if (!isRecord(config)) {
    return null
  }
  const path =
    isRecord(config.appHost) && typeof config.appHost.path === 'string'
      ? config.appHost.path
      : typeof config.appHostPath === 'string'
        ? config.appHostPath
        : null

  return path === null ? null : normalize(path.startsWith('/') ? path : `${configDir}/${path}`)
}
