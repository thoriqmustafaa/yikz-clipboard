const ESCAPES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;'
};

export function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ESCAPES[c]);
}

export function safeUrl(url: string): string | null {
  const u = url.trim();
  if (/^https?:\/\/[^\s]+$/i.test(u) || /^mailto:[^\s]+$/i.test(u)) return u;
  return null;
}

const INLINE = /`([^`]+)`|\[([^\]]+)\]\(([^)\s]+)\)|\*\*(.+?)\*\*/g;

function renderInline(text: string, allowLinks = true): string {
  let out = '';
  let last = 0;
  for (const m of text.matchAll(INLINE)) {
    const start = m.index ?? 0;
    out += escapeHtml(text.slice(last, start));
    last = start + m[0].length;
    if (m[1] !== undefined) {
      out += `<code>${escapeHtml(m[1])}</code>`;
    } else if (m[2] !== undefined && m[3] !== undefined) {
      const href = allowLinks ? safeUrl(m[3]) : null;
      const label = renderInline(m[2], false);
      out += href ? `<a href="${escapeHtml(href)}" target="_blank" rel="noopener noreferrer">${label}</a>` : label;
    } else if (m[4] !== undefined) {
      out += `<strong>${renderInline(m[4], allowLinks)}</strong>`;
    }
  }
  return out + escapeHtml(text.slice(last));
}

export function renderMarkdown(md: string): string {
  const lines = md.replace(/\r\n?/g, '\n').split('\n');
  const out: string[] = [];
  const list: string[] = [];
  const para: string[] = [];

  const flushList = () => {
    if (list.length) out.push(`<ul>${list.map((li) => `<li>${li}</li>`).join('')}</ul>`);
    list.length = 0;
  };
  const flushPara = () => {
    if (para.length) out.push(`<p>${para.join(' ')}</p>`);
    para.length = 0;
  };

  for (const raw of lines) {
    const line = raw.trimEnd();
    const heading = /^(#{1,6})\s+(.+?)\s*#*$/.exec(line);
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line);
    if (line.trim() === '') {
      flushList();
      flushPara();
    } else if (heading) {
      flushList();
      flushPara();
      const level = Math.min(6, heading[1].length + 1);
      out.push(`<h${level}>${renderInline(heading[2])}</h${level}>`);
    } else if (bullet) {
      flushPara();
      list.push(renderInline(bullet[1]));
    } else if (list.length && /^\s{2,}\S/.test(raw)) {
      list[list.length - 1] += ` ${renderInline(line.trim())}`;
    } else {
      flushList();
      para.push(renderInline(line.trim()));
    }
  }
  flushList();
  flushPara();
  return out.join('\n');
}
