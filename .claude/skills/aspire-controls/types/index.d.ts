/** One resource of a running AppHost, as `aspire describe` reports it. */
export type AspireResource = {
  name: string
  type: string
  /** Running, Starting, Waiting, Finished, Exited, FailedToStart, ... */
  state: string
  /** Healthy, Unhealthy, Degraded; null when the resource has no health checks. */
  health: string | null
  exitCode: number | null
  urls: string[]
}

/** The Aspire workspace the session runs in: where its config lives and the AppHost it names. */
export type AspireWorkspace = {
  root: string
  /** Absolute; null when the config names no AppHost. */
  appHostPath: string | null
}

/** What the last check of `aspire ps` and `aspire describe` found. */
export type AspireSnapshot = {
  /** The workspace's running AppHost; null when none runs. */
  appHostPath: string | null
  dashboardUrl: string | null
  resources: AspireResource[]
  /** Why the last check failed; null when it did not. */
  error: string | null
}

export type AspireBusy = 'starting' | 'stopping' | null

declare module 'claude-code' {
  interface PluginState {
    'aspire-controls': {
      workspace: AspireWorkspace | null
      snapshot: AspireSnapshot | null
      busy: AspireBusy
    }
  }
}
