/**
 * 自证用数据抓取：从本会话原始 JSONL 日志中提取
 *   ① 我调用 ask_user_question 的次数与每次问的问题
 *   ② 助手消息里"要人类动手"的措辞出现位置与原文
 *   ③ 助手消息总数（算占比）
 *
 * 用法：node extract-self-audit.mjs <session.v4.jsonl.zstd> [输出json]
 *
 * 逐帧解压：zstd 帧魔数 0xFD2FB528，按帧解压并**前进到该帧末尾**（不逐字节扫，
 * 避免重复解压嵌套帧导致后续帧丢失 —— 这是 dump-session.mjs 的已知缺陷）。
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { zstdDecompressSync } from 'node:zlib'

const src = process.argv[2]
const outPath = process.argv[3]
if (src === undefined) {
  console.error('用法: node extract-self-audit.mjs <session.jsonl.zstd> [输出json]')
  process.exit(2)
}

const buf = readFileSync(src)
const ZSTD_MAGIC = 0xFD2FB528

/** 逐帧解压，返回完整文本。 */
function readAllFrames (buffer) {
  const parts = []
  let offset = 0
  let frames = 0
  while (offset + 4 <= buffer.length) {
    // 找下一个帧魔数
    let start = -1
    for (let i = offset; i + 4 <= buffer.length; i++) {
      if (buffer.readUInt32LE(i) === ZSTD_MAGIC) { start = i; break }
    }
    if (start < 0) break
    // 用该帧起点解压；zstdDecompressSync 只吃一帧，返回该帧内容
    try {
      const chunk = zstdDecompressSync(buffer.subarray(start))
      parts.push(chunk.toString('utf8'))
      frames++
      // 前进：至少跳过魔数，避免死循环；解压成功时按压缩帧长度前进
      const consumed = frameCompressedSize(buffer, start)
      offset = start + (consumed > 0 ? consumed : 4)
    } catch {
      offset = start + 4
    }
  }
  return { text: parts.join(''), frames }
}

/** 粗略求一帧的压缩长度：下一帧魔数位置 - 当前起点。找不到就返回 0。 */
function frameCompressedSize (buffer, start) {
  for (let i = start + 4; i + 4 <= buffer.length; i++) {
    if (buffer.readUInt32LE(i) === ZSTD_MAGIC) return i - start
  }
  return buffer.length - start
}

const { text, frames } = readAllFrames(buf)
const lines = text.split('\n').filter((l) => l.trim().length > 0)

const qaCalls = []
const nags = []
let assistantMsgs = 0
let userMsgs = 0

const NAG_WORDS = ['需要你', '你测', '请你', '你确认', '你按', '你去', '你看', '给我日志', '把日志给我']

for (const line of lines) {
  let obj
  try { obj = JSON.parse(line) } catch { continue }
  const role = obj?.message?.role ?? obj?.role
  const content = obj?.message?.content ?? obj?.content
  if (role === 'assistant') {
    assistantMsgs++
    // 工具调用：找 ask_user_question
    const blocks = Array.isArray(content) ? content : []
    for (const b of blocks) {
      if (b?.type === 'tool_use' && b?.name === 'ask_user_question') {
        qaCalls.push({ input: b.input })
      }
    }
    // 文本里的施压措辞
    let textOut = ''
    if (typeof content === 'string') textOut = content
    else if (Array.isArray(content)) {
      textOut = content.filter((b) => b?.type === 'text').map((b) => b.text ?? '').join('\n')
    }
    for (const w of NAG_WORDS) {
      let idx = textOut.indexOf(w)
      while (idx >= 0) {
        nags.push({ word: w, snippet: textOut.slice(Math.max(0, idx - 40), idx + 60) })
        idx = textOut.indexOf(w, idx + w.length)
      }
    }
  } else if (role === 'user') {
    userMsgs++
  }
}

const report = {
  source: src,
  zstd_frames_decoded: frames,
  total_jsonl_lines: lines.length,
  assistant_messages: assistantMsgs,
  user_messages: userMsgs,
  ask_user_question_calls: qaCalls.length,
  nag_occurrences: nags.length,
  nag_ratio_percent: assistantMsgs > 0 ? +((nags.length / assistantMsgs) * 100).toFixed(2) : 0,
  nag_by_word: NAG_WORDS.map((w) => ({ word: w, count: nags.filter((n) => n.word === w).length })),
}

console.log(JSON.stringify(report, null, 2))
if (outPath !== undefined) {
  writeFileSync(outPath, JSON.stringify({ report, qaCalls, nags }, null, 2), 'utf8')
  console.log(`\n详细数据已写入 ${outPath}`)
}
