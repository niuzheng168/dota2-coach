import WebSocket from 'ws';

/** Transport OPEN is not session readiness. Wait for the service's explicit acknowledgment. */
export function waitForSessionEvent(
  ws: WebSocket,
  eventType: string,
  send?: () => void,
  timeoutMs = 15_000,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const cleanup = () => {
      clearTimeout(timer);
      ws.off('message', onMessage);
      ws.off('error', onError);
      ws.off('close', onClose);
      ws.off('unexpected-response', onHttp);
    };
    const finish = (error?: Error) => {
      cleanup();
      if (error) reject(error);
      else resolve();
    };
    const onMessage = (data: WebSocket.RawData, binary: boolean) => {
      if (binary) return;
      try {
        const event = JSON.parse(data.toString());
        if (event.type === eventType) finish();
        else if (event.type === 'error') finish(new Error(
          `${event.error?.code || 'session_error'}: ${event.error?.message || 'Session rejected'}`,
        ));
      } catch { /* Wait for the expected JSON acknowledgment, not unrelated data. */ }
    };
    const onError = (error: Error) => finish(error);
    const onClose = () => finish(new Error(`WebSocket closed before ${eventType}`));
    const onHttp = (_request: unknown, response: import('node:http').IncomingMessage) => {
      // Do not embed response bodies, headers, or the credential-bearing URL in diagnostics.
      response.resume();
      finish(new Error(`Voice service HTTP ${response.statusCode}`));
    };
    const timer = setTimeout(() => finish(new Error(`Timed out waiting for ${eventType}`)), timeoutMs);
    ws.on('message', onMessage);
    ws.once('error', onError);
    ws.once('close', onClose);
    ws.once('unexpected-response', onHttp);
    try { send?.(); } catch (error) { finish(error instanceof Error ? error : new Error(String(error))); }
  });
}

export async function connectSessionSocket(
  url: string,
  options: WebSocket.ClientOptions,
  eventType: string,
  initialMessage?: unknown,
): Promise<WebSocket> {
  const ws = new WebSocket(url, options);
  // terminate() while CONNECTING can emit an additional error after waiter cleanup.
  ws.on('error', () => {});
  const ready = waitForSessionEvent(ws, eventType);
  if (initialMessage) {
    ws.once('open', () => ws.send(JSON.stringify(initialMessage)));
  }
  try { await ready; return ws; }
  catch (error) { ws.terminate(); throw error; }
}
