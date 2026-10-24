import type { On } from 'claude-code'
import { describe, expect, mock, test } from 'claude-code/testing'

import { label } from '../hooks/register'

const ROOT = '/home/me/repo'

// A call on `$` answers its event's hooks as `{ value }`.
const ran = (stdout: string, exitCode = 0) => ({
  value: { exitCode, stdout, stderr: '', isStdoutTruncated: false, isStderrTruncated: false },
})

/** The machine beneath the plugin: a checkout at ROOT whose HEAD the test moves, and a fake `git`. */
function world(on: On, { isRepo = true, branch = 'master', sha = 'fca6d7c' } = {}) {
  const state = { isRepo, branch, sha, statuses: [] as (string | undefined)[], runs: [] as string[][] }
  const clock = mock.clock(on)

  on('session.start', ($, e) => ({ cwd: e.cwd }))
  on('session.cwd', () => ({ value: ROOT }))
  on('process.run', ($, e) => {
    state.runs.push([...e.argv])
    if (!state.isRepo) {
      return ran('', 128)
    }
    switch (e.argv[3]) {
      case 'branch':
        return ran(`${state.branch}\n`)
      case 'rev-parse':
        return ran(`${state.sha}\n`)
      default:
        return ran('', 1)
    }
  })
  on('ui.status', ($, e) => {
    state.statuses.push(e.text)
    return { value: undefined }
  })
  on('tool.call', () => ({ result: {} }))

  return { state, clock }
}

describe('current-branch', () => {
  test('labels a branch, a detached HEAD and nothing', () => {
    expect(label('master', '')).toBe('⎇ master')
    expect(label('', 'fca6d7c')).toBe('⎇ detached at fca6d7c')
    expect(label('', '')).toBeUndefined()
  })

  test('shows the branch when the session starts', async ($, on) => {
    const { state, clock } = world(on, { branch: 'upgrade-dependencies' })
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    expect(state.statuses).toEqual(['⎇ upgrade-dependencies'])
    expect(state.runs[0]).toEqual(['git', '-C', ROOT, 'branch', '--show-current'])
  })

  test('shows the commit a detached HEAD sits on', async ($, on) => {
    const { state, clock } = world(on, { branch: '' })
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    expect(state.statuses).toEqual(['⎇ detached at fca6d7c'])
  })

  test('clears the line outside a git checkout', async ($, on) => {
    const { state, clock } = world(on, { isRepo: false })
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    expect(state.statuses).toEqual([undefined])
  })

  test('a branch switched from Bash shows at once', async ($, on) => {
    const { state, clock } = world(on)
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    state.branch = 'feature'
    await $.tool.call({ tool: 'Bash', command: 'git switch feature' })
    await clock.advance(0)

    expect(state.statuses).toEqual(['⎇ master', '⎇ feature'])
  })

  test('the poll catches a switch made outside the session, and repeats nothing', async ($, on) => {
    const { state, clock } = world(on)
    await $.session.start({ cwd: ROOT, surface: 'terminal', isInteractive: true })
    await clock.advance(0)

    await clock.advance(10_000)
    expect(state.statuses).toEqual(['⎇ master'])

    state.branch = 'elsewhere'
    await clock.advance(10_000)
    expect(state.statuses).toEqual(['⎇ master', '⎇ elsewhere'])
  })
})
