// 从 DSH 会话日志里查"某关键字附近的用户发言 + 助手发言"，用于复盘历史需求。
//
// 用法：node grep_session.js <log.zstd> <正则> [before] [after]
'use strict';
const { readAllFrames } = require('./zst_frames.js');

const [, , file, pattern, beforeArg = '1', afterArg = '6'] = process.argv;
if (!file || !pattern) {
  process.stderr.write('usage: node grep_session.js <log.zstd> <regex> [before] [after]\n');
  process.exit(2);
}
const re = new RegExp(pattern);
const before = Number(beforeArg);
const after = Number(afterArg);

const NOISE = ['Current runtime context', '【会话名录】', '[会话名录]', '【工程纪律', '【技能路由', '<<<WBC'];

function flatten(content) {
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  return content.map((p) => (p && typeof p.text === 'string' ? p.text : '')).filter(Boolean).join('\n');
}

const lines = readAllFrames(file).split('\n').filter((l) => l.trim());
const msgs = [];
for (const line of lines) {
  let rec;
  try {
    rec = JSON.parse(line);
  } catch {
    continue;
  }
  if (rec.type !== 'user/message' && rec.type !== 'assistant/message') continue;
  const d = rec.data || {};
  const text = flatten(d.content ?? d.message ?? d.text).trim();
  if (!text) continue;
  const kind = rec.type === 'user/message' ? 'U' : 'A';
  const noise = kind === 'U' && NOISE.some((p) => text.startsWith(p));
  msgs.push({ kind, text, noise, time: rec.time, seq: rec.seq });
}

const hits = [];
msgs.forEach((m, i) => {
  if (re.test(m.text)) hits.push(i);
});
process.stderr.write(`messages=${msgs.length} hits=${hits.length}\n`);

const shown = new Set();
for (const i of hits) {
  const lo = Math.max(0, i - before);
  const hi = Math.min(msgs.length - 1, i + after);
  for (let j = lo; j <= hi; j++) {
    if (shown.has(j) || msgs[j].noise) continue;
    shown.add(j);
    const m = msgs[j];
    const when = m.time ? new Date(m.time).toISOString().replace('T', ' ').slice(0, 19) : '?';
    console.log(`--- [${m.kind}] #${j} seq=${m.seq} ${when} ---`);
    console.log(m.text.slice(0, 1200));
    console.log();
  }
}
