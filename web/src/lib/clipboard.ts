function hasClipboardItem(): boolean {
  return typeof ClipboardItem !== 'undefined' && typeof navigator.clipboard?.write === 'function';
}

export function canWriteImages(): boolean {
  return hasClipboardItem();
}

export async function writeText(text: string | Promise<string>): Promise<void> {
  if (!navigator.clipboard) throw new Error('Clipboard is not available in this context');
  if (typeof text === 'string') {
    await navigator.clipboard.writeText(text);
    return;
  }
  if (hasClipboardItem()) {
    try {
      const blob = text.then((t) => new Blob([t], { type: 'text/plain' }));
      await navigator.clipboard.write([new ClipboardItem({ 'text/plain': blob })]);
      return;
    } catch (err) {
      if (err instanceof DOMException && err.name === 'NotAllowedError') throw err;
    }
  }
  await navigator.clipboard.writeText(await text);
}

export async function writePng(png: Blob | Promise<Blob>): Promise<void> {
  if (!hasClipboardItem()) throw new Error('This browser cannot copy images');
  const typed = Promise.resolve(png).then((b) => (b.type === 'image/png' ? b : new Blob([b], { type: 'image/png' })));
  await navigator.clipboard.write([new ClipboardItem({ 'image/png': typed })]);
}

export function isNotAllowed(err: unknown): boolean {
  return err instanceof DOMException && (err.name === 'NotAllowedError' || err.name === 'SecurityError');
}
