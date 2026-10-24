import type { EngineInterface, ProcessRunResult, Register, Timer } from 'claude-code'

const POLL_MS = 10_000
const GIT_TIMEOUT_MS = 5_000

// The module's own: a reload starts them over, and session.start sets them again.
let shown: string | undefined
let isShown = false
let checking: Promise<void> | undefined
let timer: Timer | undefined

/** The status line for a checkout: its branch, or the commit a detached HEAD sits on. */
export function label(branch: string, sha: string): string | undefined {
  if (branch !== '') {
    return `⎇ ${branch}`
  }

  return sha === '' ? undefined : `⎇ detached at ${sha}`
}

function git($: EngineInterface, cwd: string, args: string[]): Promise<ProcessRunResult> {
  return $.process.run(['git', '-C', cwd, ...args], { timeoutMs: GIT_TIMEOUT_MS })
}

/** What the status line should say for the session's directory; undefined outside a git checkout. */
async function current($: EngineInterface): Promise<string | undefined> {
  const cwd = await $.session.cwd()
  const branch = await git($, cwd, ['branch', '--show-current'])
  if (branch.exitCode !== 0) {
    return undefined
  }

  const name = branch.stdout.trim()
  const head = name === '' ? await git($, cwd, ['rev-parse', '--short', 'HEAD']) : undefined

  return label(name, head?.exitCode === 0 ? head.stdout.trim() : '')
}

/** Asks git for the branch and pins it as the status line when it changed; one check at a time. */
function check($: EngineInterface): Promise<void> {
  checking ??= checkNow($).finally(() => {
    checking = undefined
  })

  return checking
}

/** Checks again once a tool call has run: it may have switched the branch or moved the session's directory. */
function checkAfter<T>($: EngineInterface, ran: T): T {
  void check($)

  return ran
}

async function checkNow($: EngineInterface) {
  const next = await current($).catch(() => undefined)
  if (isShown && next === shown) {
    return
  }

  $.ui.status(next)
  shown = next
  isShown = true
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    timer?.cancel()
    isShown = false
    void check($)
    timer = $.clock.every(POLL_MS, () => void check($))

    return next(e)
  })

  // A switch made through these shows at once; the poll catches one made outside the session.
  on('tool.call', { tool: 'Bash' }, async ($, e, next) => checkAfter($, await next(e)))
  on('tool.call', { tool: 'EnterWorktree' }, async ($, e, next) => checkAfter($, await next(e)))
  on('tool.call', { tool: 'ExitWorktree' }, async ($, e, next) => checkAfter($, await next(e)))
}
