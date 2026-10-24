// Decompress a multi-frame zstd file (DSH session.v4.jsonl.zstd).
//
// 背景：DSH 会话日志是**每帧一个独立 zstd frame** 的顺序拼接（实测本文件 6937 帧）。
// Node 的 `zlib.zstdDecompressSync` 只解第一帧（199 字节），`createZstdDecompress`
// 遇到帧边界也提前 end —— 两者都无法读全。因此这里手动按 magic 切帧、逐帧解压。
//
// 用法：node zst_frames.js <file.zstd>   →  stdout 为完整解压后的 UTF-8 文本
'use strict';
const fs = require('fs');
const zlib = require('zlib');

const MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);

/** 返回文件内每个 zstd frame 的起始偏移（升序），末尾追加文件长度作为哨兵。 */
function frameOffsets(buf) {
  const offsets = [];
  let cursor = 0;
  for (;;) {
    const at = buf.indexOf(MAGIC, cursor);
    if (at < 0) break;
    offsets.push(at);
    cursor = at + MAGIC.length;
  }
  offsets.push(buf.length);
  return offsets;
}

/** 读全整个多帧 zstd 文件并解码为字符串。 */
function readAllFrames(file) {
  const buf = fs.readFileSync(file);
  const offsets = frameOffsets(buf);
  const parts = [];
  for (let i = 0; i < offsets.length - 1; i++) {
    const frame = buf.subarray(offsets[i], offsets[i + 1]);
    try {
      parts.push(zlib.zstdDecompressSync(frame));
    } catch (err) {
      // 边界要显式：损坏帧必须报错，不许静默跳过
      throw new Error(`frame #${i} at offset ${offsets[i]} failed: ${err.message}`);
    }
  }
  return Buffer.concat(parts).toString('utf8');
}

module.exports = { readAllFrames, frameOffsets };

if (require.main === module) {
  const file = process.argv[2];
  if (!file) {
    process.stderr.write('usage: node zst_frames.js <file.zstd>\n');
    process.exit(2);
  }
  process.stdout.write(readAllFrames(file));
}
