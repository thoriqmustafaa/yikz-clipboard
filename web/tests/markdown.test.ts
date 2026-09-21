import { describe, expect, it } from 'vitest';
import { escapeHtml, renderMarkdown, safeUrl } from '../src/lib/markdown';
import { compareVersions, latestDownloads, parseReleases } from '../src/lib/releases';
import { parseServerMessage } from '../src/lib/protocol/messages';

describe('renderMarkdown', () => {
  it('renders headings, bullets, bold, code and links', () => {
    const html = renderMarkdown(
      '### macOS\n- **Dock** icon toggle\n- Uses `ditto`\n- See [notes](https://clip.yikz.dev/x?a=1&b=2)\n\n### Web\n- Changelog page\n'
    );
    expect(html).toBe(
      [
        '<h4>macOS</h4>',
        '<ul><li><strong>Dock</strong> icon toggle</li><li>Uses <code>ditto</code></li><li>See <a href="https://clip.yikz.dev/x?a=1&amp;b=2" target="_blank" rel="noopener noreferrer">notes</a></li></ul>',
        '<h4>Web</h4>',
        '<ul><li>Changelog page</li></ul>'
      ].join('\n')
    );
  });

  it('renders paragraphs and continuation lines', () => {
    expect(renderMarkdown('Hello\nworld\n\n- a\n  b')).toBe('<p>Hello world</p>\n<ul><li>a b</li></ul>');
  });

  it('escapes raw HTML everywhere', () => {
    const html = renderMarkdown('<script>alert(1)</script>\n### <img src=x onerror=alert(1)>\n- **<b>x</b>**\n- `<i>`');
    expect(html).not.toMatch(/<script|<img|<b>|<i>/);
    expect(html).toContain('&lt;script&gt;alert(1)&lt;/script&gt;');
    expect(html).toContain('<h4>&lt;img src=x onerror=alert(1)&gt;</h4>');
    expect(html).toContain('<strong>&lt;b&gt;x&lt;/b&gt;</strong>');
    expect(html).toContain('<code>&lt;i&gt;</code>');
  });

  it('drops unsafe link targets and keeps the text', () => {
    expect(renderMarkdown('[click](javascript:alert(1))')).toBe('<p>click)</p>');
    expect(renderMarkdown('[x](data:text/html,hi)')).toBe('<p>x</p>');
    expect(renderMarkdown('[x](//evil.example)')).toBe('<p>x</p>');
    const quoted = renderMarkdown('[x](https://a.example/"onmouseover="alert(1))');
    expect(quoted).not.toContain('"onmouseover');
    expect(quoted).toContain('&quot;onmouseover=&quot;');
  });

  it('does not nest links inside link text', () => {
    const html = renderMarkdown('[a [b](https://b.example)](https://a.example)');
    expect((html.match(/<a /g) ?? []).length).toBeLessThanOrEqual(1);
  });

  it('escapes and validates helpers', () => {
    expect(escapeHtml(`<a href="x">'&'</a>`)).toBe('&lt;a href=&quot;x&quot;&gt;&#39;&amp;&#39;&lt;/a&gt;');
    expect(safeUrl('https://example.com')).toBe('https://example.com');
    expect(safeUrl('mailto:me@example.com')).toBe('mailto:me@example.com');
    expect(safeUrl('JavaScript:alert(1)')).toBeNull();
    expect(safeUrl('/relative')).toBeNull();
  });
});

describe('releases', () => {
  const list = parseReleases({
    releases: [
      {
        version: '1.0.0',
        published_at: '2026-09-21T10:00:00.000Z',
        notes_md: '',
        assets: [
          { platform: 'macos', file: 'a.zip', size: 1, sha256: 'x', signature: 'y' },
          { platform: 'android', file: 'a.apk', size: 2, sha256: 'x', signature: 'y' }
        ]
      },
      {
        version: '1.10.0',
        published_at: '2026-09-23T10:00:00.000Z',
        notes_md: '### All\n- x',
        assets: [{ platform: 'macos', file: 'b.zip', size: 3, sha256: 'x', signature: 'y' }]
      }
    ]
  });

  it('compares versions numerically', () => {
    expect(compareVersions('1.10.0', '1.9.0')).toBe(1);
    expect(compareVersions('1.0.0', '1.0.0')).toBe(0);
    expect(compareVersions('0.9.9', '1.0.0')).toBe(-1);
  });

  it('picks the newest asset per platform', () => {
    const d = latestDownloads(list);
    expect(d.map((x) => [x.platform, x.version, x.asset.file])).toEqual([
      ['macos', '1.10.0', 'b.zip'],
      ['android', '1.0.0', 'a.apk']
    ]);
  });

  it('parses release_available messages', () => {
    expect(parseServerMessage('{"type":"release_available","version":"1.1.0"}')).toEqual({ type: 'release_available', version: '1.1.0' });
  });
});
