// 从 DSH 原始会话日志（多帧 zstd）中提取真实用户发言与助手发言。
//
// 用法：
//   node read_session_msgs.js <session.v4.jsonl.zstd> user [n]      # 末尾 n 条用户发言
//   node read_session_msgs.js <session.v4.jsonl.zstd> user-first [n]# 开头 n 条用户发言
//   node read_session_msgs.js <session.v4.jsonl.zstd> all [n]       # 末尾 n 条对话（含助手）
'use strict';
const { readAllFrames } = require('./zst_frames.js');

const [, , file, mode = 'user', countArg = '12'] = process.argv;
if (!file) {
  process.stderr.write('usage: node read_session_msgs.js <log.zstd> [user|user-first|all] [n]\n');
  process.exit(2);
}
const count = Number(countArg);

/** 系统注入的噪声（环境快照 / 会话名录 / 约束提示）——不算用户真实发言。 */
const NOISE_PREFIXES = [
  'Current runtime context',
  '【会话名录】',
  '[会话名录]',
  '【工程纪律',
  '【技能路由',
  '<available_skills>',
];

function isNoise(text) {
  return NOISE_PREFIXES.some((p) => text.startsWith(p));
}

/** 把一条 message 的 content 摊平成纯文本。 */
function flatten(content) {
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  return content
    .map((p) => {
      if (!p || typeof p !== 'object') return '';
      if (typeof p.text === 'string') return p.text;
      if (p.type === 'image') return '[image]';
      return '';
    })
    .filter(Boolean)
    .join('\n');
}

const lines = readAllFrames(file).split('\n').filter((l) => l.trim());
const records = [];
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
  records.push({ kind: rec.type, text, seq: rec.seq, time: rec.time, data: d });
}

const users = records.filter((r) => r.kind === 'user/message' && !isNoise(r.text));
process.stderr.write(`lines=${lines.length} records=${records.length} user(clean)=${users.length}\n`);

const show = (r, index) => {
  const when = r.time ? new Date(r.time).toISOString().replace('T', ' ').slice(0, 19) : '?';
  console.log(`--- #${index} [${r.kind}] seq=${r.seq} ${when} ---`);
  console.log(r.text.slice(0, 3000));
  console.log();
};

if (mode === 'user') {
  const tail = users.slice(-count);
  tail.forEach((r, i) => show(r, users.length - tail.length + i + 1));
} else if (mode === 'user-first') {
  users.slice(0, count).forEach((r, i) => show(r, i + 1));
} else if (mode === 'all') {
  const tail = records.slice(-count);
  tail.forEach((r, i) => show(r, records.length - tail.length + i + 1));
} else if (mode === 'stats') {
  // 统计口径：只算真实用户发言
  console.log(`clean user messages: ${users.length}`);
  console.log(`assistant messages: ${records.filter((r) => r.kind === 'assistant/message').length}`);
} else {
  process.stderr.write(`unknown mode: ${mode}\n`);
  process.exit(2);
}
