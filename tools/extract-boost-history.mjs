/**
 * 从会话 JSONL 日志里提取历史上"加力点火次数"的实测证据。
 *
 * 目的：判定"只调一次"那个版本在运行时的真实行为 ——
 *   是「一次按住 = 一次 ignition」还是「每帧 = N 次 ignition」。
 *
 * 用法：node extract-boost-history.mjs <session.v4.jsonl.zstd>
 */
import { readFileSync } from 'node:fs'
import { zstdDecompressSync } from 'node:zlib'

const src = process.argv[2]
const buf = readFileSync(src)
const M = 0xFD2FB528

function readAll (buffer) {
  const parts = []
  let off = 0
  while (off + 4 <= buffer.length) {
    let s = -1
    for (let i = off; i + 4 <= buffer.length; i++) {
      if (buffer.readUInt32LE(i) === M) { s = i; break }
    }
    if (s < 0) break
    let nx = buffer.length
    for (let i = s + 4; i + 4 <= buffer.length; i++) {
      if (buffer.readUInt32LE(i) === M) { nx = i; break }
    }
    try { parts.push(zstdDecompressSync(buffer.subarray(s, nx)).toString('utf8')) } catch { /* 帧不完整 */ }
    off = nx
  }
  return parts.join('')
}

const lines = readAll(buf).split('\n').filter((l) => l.trim().length > 0)
const ignitions = []
const offs = []
const reapplies = []

for (const l of lines) {
  let o
  try { o = JSON.parse(l) } catch { continue }
  if (o.type !== 'tool/result') continue
  const s = JSON.stringify(o.data ?? {})
  // 统计出现次数（同一份日志可能被读多次）
  const ig = (s.match(/fast flight engaged/g) ?? []).length
  const offC = (s.match(/ForceBoostoff\(\) - fast flight OFF/g) ?? []).length
  const re = s.match(/re-applied (\d+) time\(s\)/g) ?? []
  if (ig > 0 || offC > 0 || re.length > 0) {
    ignitions.push(ig)
    offs.push(offC)
    for (const r of re) reapplies.push(Number(r.match(/(\d+)/)[1]))
  }
}

console.log('=== 含 BOOST 证据的工具结果条数: ' + ignitions.length + ' ===')
if (reapplies.length > 0) {
  console.log('=== 历史"重施加次数"记录（证明曾经每帧调用）===')
  reapplies.sort((a, b) => a - b)
  console.log('  样本数=' + reapplies.length)
  console.log('  最小=' + reapplies[0] + '  最大=' + reapplies[reapplies.length - 1])
  console.log('  全部: ' + reapplies.join(', '))
  console.log('  ⇒ 这些数字是"一次按住期间 ForceBooston 被调用的次数"')
  console.log('  ⇒ 数字 >> 1 即证明【每帧重复点火】= 特效叠加的根因')
} else {
  console.log('  未找到 re-applied 记录（说明当时还是"只调一次"的干净版本，或日志已轮转）')
}
console.log('=== ignition 出现次数分布 ===')
console.log('  ' + ignitions.join(', '))
