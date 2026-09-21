export function saveBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.rel = 'noopener';
  a.style.display = 'none';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}

function stamp(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}.${p(d.getMinutes())}.${p(d.getSeconds())}`;
}

const UNSAFE = /[\\/:*?"<>|\u0000-\u001f\u007f]/g;

export function textFileName(preview: string, createdMs: number): string {
  const line = preview.split(/\r?\n/)[0].replace(UNSAFE, ' ').replace(/\s+/g, ' ').trim().slice(0, 48).trim();
  return line ? `${line}.txt` : `Clipboard ${stamp(createdMs)}.txt`;
}

export function imageFileName(createdMs: number): string {
  return `Image ${stamp(createdMs)}.png`;
}
