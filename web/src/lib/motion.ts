let reduced = false;

if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
  const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
  reduced = mq.matches;
  mq.addEventListener?.('change', (e) => {
    reduced = e.matches;
  });
}

export function prefersReducedMotion(): boolean {
  return reduced;
}

export function motion(ms: number): number {
  return reduced ? 0 : ms;
}
