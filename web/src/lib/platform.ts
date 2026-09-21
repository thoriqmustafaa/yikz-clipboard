export function browserName(ua: string = typeof navigator !== 'undefined' ? navigator.userAgent : ''): string {
  if (/Edg\//.test(ua)) return 'Edge';
  if (/OPR\/|Opera/.test(ua)) return 'Opera';
  if (/Vivaldi/.test(ua)) return 'Vivaldi';
  if (/Firefox\//.test(ua)) return 'Firefox';
  if (/Chrome\/|CriOS\//.test(ua)) return 'Chrome';
  if (/Safari\//.test(ua)) return 'Safari';
  return 'Browser';
}

export function defaultDeviceName(): string {
  return `Web on ${browserName()}`;
}

export function isMac(): boolean {
  if (typeof navigator === 'undefined') return false;
  return /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
}

export function modKey(): string {
  return isMac() ? '⌘' : 'Ctrl';
}
