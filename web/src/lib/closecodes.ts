export type CloseAction = 'reconnect' | 'login' | 'update' | 'paused';

export function closeAction(code: number): CloseAction {
  if (code === 4001) return 'login';
  if (code === 4003) return 'update';
  if (code === 4002) return 'paused';
  return 'reconnect';
}

export function backoffCapMs(attempt: number): number {
  return Math.min(30, 0.5 * Math.pow(2, attempt)) * 1000;
}

export function backoffDelayMs(attempt: number, random: () => number = Math.random): number {
  return random() * backoffCapMs(attempt);
}
