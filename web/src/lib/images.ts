import type { Bytes } from './protocol/encoding';

export interface PngResult {
  png: Bytes;
  width: number;
  height: number;
}

export function pngDimensions(b: Uint8Array): { width: number; height: number } | null {
  const sig = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
  if (b.length < 24) return null;
  for (let i = 0; i < 8; i++) if (b[i] !== sig[i]) return null;
  if (b[12] !== 0x49 || b[13] !== 0x48 || b[14] !== 0x44 || b[15] !== 0x52) return null;
  const view = new DataView(b.buffer, b.byteOffset, b.byteLength);
  const width = view.getUint32(16, false);
  const height = view.getUint32(20, false);
  if (!width || !height) return null;
  return { width, height };
}

function canvasToBlob(canvas: HTMLCanvasElement, type: string, quality?: number): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('Could not encode image'))), type, quality);
  });
}

async function decode(blob: Blob): Promise<ImageBitmap> {
  try {
    return await createImageBitmap(blob);
  } catch {
    throw new Error('This image format is not supported by the browser');
  }
}

export async function toPng(blob: Blob): Promise<PngResult> {
  if (blob.type === 'image/png' || blob.type === '') {
    const bytes = new Uint8Array(await blob.arrayBuffer());
    const dims = pngDimensions(bytes);
    if (dims) return { png: bytes, ...dims };
  }
  const bmp = await decode(blob);
  try {
    const canvas = document.createElement('canvas');
    canvas.width = bmp.width;
    canvas.height = bmp.height;
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('Canvas is not available');
    ctx.drawImage(bmp, 0, 0);
    const png = await canvasToBlob(canvas, 'image/png');
    return {
      png: new Uint8Array(await png.arrayBuffer()),
      width: bmp.width,
      height: bmp.height
    };
  } finally {
    bmp.close();
  }
}

export async function makeThumbnail(
  png: Uint8Array,
  maxSide: number,
  maxSealedBytes: number,
  overhead: number
): Promise<Bytes | null> {
  let bmp: ImageBitmap;
  try {
    bmp = await createImageBitmap(new Blob([png as Bytes], { type: 'image/png' }));
  } catch {
    return null;
  }
  try {
    let scale = Math.min(1, maxSide / Math.max(bmp.width, bmp.height));
    for (let round = 0; round < 6; round++) {
      const w = Math.max(1, Math.round(bmp.width * scale));
      const h = Math.max(1, Math.round(bmp.height * scale));
      const canvas = document.createElement('canvas');
      canvas.width = w;
      canvas.height = h;
      const ctx = canvas.getContext('2d');
      if (!ctx) return null;
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, w, h);
      ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(bmp, 0, 0, w, h);
      for (const q of [0.8, 0.7, 0.6, 0.5, 0.4]) {
        const jpeg = await canvasToBlob(canvas, 'image/jpeg', q);
        if (jpeg.size + overhead <= maxSealedBytes) return new Uint8Array(await jpeg.arrayBuffer());
      }
      scale *= 0.75;
    }
    return null;
  } finally {
    bmp.close();
  }
}
