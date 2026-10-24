// 从 DSH 原始会话日志中提取"用户真实发言"的尾部（不依赖压缩摘要）。
//
// 用法：node read_user_tail.js <session.v4.jsonl.zstd> [n]
'use strict';
const { readAllFrames } = require('./zst_frames.js');

const file = process.argv[2];
const n = Number(process.argv[3] || 12);
if (!file) {
  process.stderr.write('usage: node read_user_tail.js <session.v4.jsonl.zstd> [n]\n');
  process.exit(2);
}

const text = readAllFrames(file);
const lines = text.split('\n').filter((l) => l.trim());
process.stderr.write(`jsonl lines: ${lines.length}\n`);

const msgs = [];
for (const line of lines) {
  let obj;
  try {
    obj = JSON.parse(line);
  } catch {
    continue;
  }
  // 结构探测：逐帧列出出现过的 type
  const type = obj.type || obj.role || '?';
  if (type !== 'message' && type !== 'user') continue;

  // 用户消息可能的落点：obj.message.role / obj.role
  const role = obj.role || (obj.message && obj.message.role) || (obj.payload && obj.payload.role);
  if (role !== 'user') continue;

  const content = obj.content || (obj.message && obj.message.content) || (obj.payload && obj.payload.content);
  let txt = '';
  if (typeof content === 'string') txt = content;
  else if (Array.isArray(content)) {
    txt = content
      .filter((p) => p && (p.type === 'text' || p.type === 'input_text'))
      .map((p) => String(p.text || ''))
      .join('\n');
  }
  txt = txt.trim();
  // 跳过系统注入（环境快照、名录等）
  if (!txt) continue;
  if (txt.startsWith('[会话名录]') || txt.startsWith('【会话名录】')) continue;
  if (txt.startsWith('Current runtime context')) continue;
  msgs.push({ role, txt, obj });
}

process.stderr.write(`user messages: ${msgs.length}\n`);
const tail = msgs.slice(-n);
for (let i = 0; i < tail.length; i++) {
  console.log(`--- user #${msgs.length - tail.length + i + 1} ---`);
  console.log(tail[i].txt.slice(0, 2000));
  console.log();
}
