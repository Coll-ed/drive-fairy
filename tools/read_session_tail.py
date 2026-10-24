"""读取本会话的原始 JSONL 日志（zstd），提取最近的用户消息。

用途：用户要求「自己去翻当前会话记忆，不是压缩的，看近10轮的」——
      即从原始 session 日志里核对用户到底说过什么，而不是依赖我的压缩摘要。

用法：python read_session_tail.py <session.v4.jsonl.zstd> [消息条数]
"""
import json
import sys


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: read_session_tail.py <session.jsonl.zstd> [n]", file=sys.stderr)
        return 2
    path = sys.argv[1]
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 12

    try:
        import zstandard  # type: ignore
    except ImportError:
        print("zstandard module not available", file=sys.stderr)
        return 3

    dctx = zstandard.ZstdDecompressor()
    with open(path, "rb") as fh:
        # 流式解压：文件可能包含多个 zstd 帧
        reader = dctx.stream_reader(fh)
        raw = reader.read()

    text = raw.decode("utf-8", errors="replace")
    lines = [ln for ln in text.splitlines() if ln.strip()]
    print(f"total jsonl lines: {len(lines)}")

    # 收集所有"用户发出的真实消息"（排除工具结果、系统注入）
    user_msgs: list[str] = []
    for ln in lines:
        try:
            obj = json.loads(ln)
        except json.JSONDecodeError:
            continue

        # 兼容多种结构：直接看有没有 role/content
        role = obj.get("role") or obj.get("type") or ""
        content = obj.get("content")

        # 有些实现把消息放在 payload/message 里
        if not role and isinstance(obj.get("payload"), dict):
            payload = obj["payload"]
            role = payload.get("role") or payload.get("type") or ""
            content = payload.get("content", content)

        if role != "user":
            continue

        if isinstance(content, str):
            txt = content
        elif isinstance(content, list):
            parts = []
            for item in content:
                if isinstance(item, dict) and item.get("type") == "text":
                    parts.append(str(item.get("text", "")))
            txt = "\n".join(parts)
        else:
            continue

        txt = txt.strip()
        if txt:
            user_msgs.append(txt)

    print(f"user messages found: {len(user_msgs)}")
    print("=" * 70)
    for i, m in enumerate(user_msgs[-n:], start=max(1, len(user_msgs) - n + 1)):
        print(f"--- user #{i} ---")
        print(m[:1500])
        print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
