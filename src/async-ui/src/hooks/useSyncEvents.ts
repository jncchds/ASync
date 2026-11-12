import { useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { getAccessToken } from '../api'

/** Every message of /hubs/sync (see SyncEvents on the server). */
export interface SyncEvent { seq: number; name: string; args: unknown[] }

export type LiveState = 'connecting' | 'live' | 'offline'

/**
 * Listens to the server's live events (SignalR /hubs/sync) while the component is mounted. After a reconnect,
 * `onEvent` is called with a synthetic `reconnected` event, because events sent while offline are lost.
 */
export function useSyncEvents(onEvent: (event: SyncEvent) => void): LiveState {
  const handler = useRef(onEvent)
  const [state, setState] = useState<LiveState>('connecting')

  useEffect(() => { handler.current = onEvent }, [onEvent])

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/sync', { accessTokenFactory: async () => (await getAccessToken()) ?? '' })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build()
    let stopped = false
    let retry: ReturnType<typeof setTimeout> | undefined

    connection.on('event', (e: SyncEvent) => handler.current(e))
    connection.onreconnecting(() => setState('connecting'))
    connection.onreconnected(() => {
      setState('live')
      handler.current({ seq: 0, name: 'reconnected', args: [] })
    })
    // Automatic reconnects give up after the last delay; keep trying every 30 seconds after that.
    connection.onclose(() => {
      if (stopped) return
      setState('offline')
      retry = setTimeout(start, 30_000)
    })

    async function start() {
      try {
        await connection.start()
        if (stopped) return
        setState('live')
        handler.current({ seq: 0, name: 'reconnected', args: [] })
      } catch {
        if (stopped || connection.state !== HubConnectionState.Disconnected) return
        setState('offline')
        retry = setTimeout(start, 30_000)
      }
    }
    start()

    return () => {
      stopped = true
      clearTimeout(retry)
      connection.stop().catch(() => {})
    }
  }, [])

  return state
}
