// 驱动妖精 AI-deal-Rays · 虚拟光标抑制器
//
// ── 问题（用户实测）────────────────────────────────────────────────────
//   用户原话：「以及游戏的虚拟光标又tm出现了」「一直有，战斗绝对不能有的」
//
// ── 真身（反编译确认）──────────────────────────────────────────────────
//   `Michsky.UI.Shift.VirtualCursor : PointerInputModule`
//     public GameObject cursorObject;      ← ★ 光标对象（一直存在并渲染）
//     public RectTransform border;
//     public EventSystem vEventSystem;
//     public void AnimateCursorIn() / AnimateCursorOut();
//     public override void Process();
//
//   它是 `Michsky.UI.Shift`（第三方 UI 包）的**手柄虚拟光标**，
//   作为 `PointerInputModule` 常驻 EventSystem，自行决定何时显示。
//
// ── 为什么之前没管到 ───────────────────────────────────────────────────
//   我们此前只管了 **OS 鼠标指针**
//   （`GameManagerBase.CursorShow(bool)` / `Cursor.visible`），
//   而这是**另一个东西**：UI 层里的一块 RectTransform/GameObject。
//   两者互不相干 —— 所以关掉系统光标后它照样显示。
//
// ── 处理方式 ───────────────────────────────────────────────────────────
//   把 `cursorObject` 设为不激活（`SetActive(false)`）。
//   · 不需要销毁组件（那会破坏 UI 事件系统）
//   · 可逆：离开战斗/关闭功能时恢复原激活状态
//   · 只在启用时动手；原版模式完全不介入

using System;
using BepInEx.Logging;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 抑制 `Michsky.UI.Shift.VirtualCursor` 的虚拟光标对象。
/// </summary>
internal sealed class VirtualCursorSuppressor
{
    // 对象名兜底：若组件查找失败，按名字找（名字来自组件字段，避免写死魔法串）
    private const string CursorObjectName = "VirtualCursor";

    private readonly ManualLogSource _log;
    private GameObject? _cursorObject;
    private bool _savedActive;
    private bool _suppressed;

    public VirtualCursorSuppressor(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>当前是否处于抑制状态。</summary>
    public bool IsSuppressed => _suppressed;

    /// <summary>
    /// 查找并抑制虚拟光标。幂等：已抑制则直接返回。
    /// </summary>
    public void Suppress()
    {
        if (_suppressed)
        {
            return;
        }
        // "找不到"只报告一次 —— 实测教训：早期版本每帧都写告警，
        // 日志被刷了几百条（Suppress 是每帧调用的）。
        if (_lookupFailed)
        {
            return;
        }
        try
        {
            if (_cursorObject is null)
            {
                _cursorObject = FindCursorObject();
                if (_cursorObject is null)
                {
                    _lookupFailed = true;
                    // 该 UI 包在战斗场景里可能根本没实例化 —— 记录一次即可，不刷屏
                    _log.LogInfo("[VCURSOR] VirtualCursor not present in this scene -"
                                 + " the cursor you see must come from something else"
                                 + " (reporting once; will not retry)");
                    return;
                }
            }

            _savedActive = _cursorObject.activeSelf;
            if (_savedActive)
            {
                _cursorObject.SetActive(false);
            }
            _suppressed = true;
            _log.LogInfo($"[VCURSOR] suppressed '{_cursorObject.name}'"
                         + $" (was active={_savedActive}) - UI virtual cursor hidden");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _lookupFailed = true;
            _log.LogError($"[VCURSOR] suppress failed: {ex}");
        }
    }

    // 查找失败标记（避免每帧重试 + 刷屏）
    private bool _lookupFailed;

    /// <summary>恢复虚拟光标到抑制前的状态（幂等）。</summary>
    public void Restore()
    {
        if (!_suppressed)
        {
            return;
        }
        try
        {
            if (_cursorObject is not null && _savedActive)
            {
                _cursorObject.SetActive(true);
                _log.LogInfo($"[VCURSOR] restored '{_cursorObject.name}' to active");
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"[VCURSOR] restore failed: {ex.Message}");
        }
        finally
        {
            _suppressed = false;
        }
    }

    /// <summary>
    /// 查找虚拟光标对象。
    /// 优先通过组件找（最可靠：拿到 `VirtualCursor.cursorObject` 字段），
    /// 组件找不到时按对象名兜底。
    /// </summary>
    private GameObject? FindCursorObject()
    {
        // ① 通过组件
        try
        {
            Michsky.UI.Shift.VirtualCursor[] all =
                UnityEngine.Object.FindObjectsOfType<Michsky.UI.Shift.VirtualCursor>();
            if (all is not null && all.Length > 0)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    Michsky.UI.Shift.VirtualCursor? vc = all[i];
                    if (vc is null)
                    {
                        continue;
                    }
                    GameObject? obj = vc.cursorObject;
                    if (obj is not null)
                    {
                        _log.LogInfo($"[VCURSOR] found VirtualCursor component on"
                                     + $" '{(vc.gameObject is not null ? vc.gameObject.name : "?")}'"
                                     + $", cursorObject='{obj.name}'");
                        return obj;
                    }
                }
                _log.LogWarning($"[VCURSOR] {all.Length} VirtualCursor component(s) found"
                                + " but none had a cursorObject assigned");
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"[VCURSOR] component lookup failed: {ex.Message}");
        }

        // ② 按名字兜底（组件字段为空或组件不存在时）
        try
        {
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] is not null && all[i].name == CursorObjectName)
                {
                    _log.LogInfo($"[VCURSOR] found by name '{CursorObjectName}'"
                                 + $"(parent={(all[i].parent is not null ? all[i].parent.name : "null")})");
                    return all[i].gameObject;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"[VCURSOR] name lookup failed: {ex.Message}");
        }

        return null;
    }
}
