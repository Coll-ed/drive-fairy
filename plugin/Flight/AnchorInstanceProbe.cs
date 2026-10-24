// 驱动妖精 AI-deal-Rays · 锚点实例化验证（只读探针，决定实现路线）
//
// ── 要验证什么 ──────────────────────────────────────────────────────────
//   用户定稿的设计：**把锁定目标固定到锚点上**，于是
//       LockonTargetCharacter = 锚点 → 准心/身体/攻击/冲刺/大招全部自动朝锚点
//   （用户原话：「锁定就是准心：这个的意思是把锁定固定到锚点那里去，
//               然后锚点又是一个属于我们的可控准心，所以就做到了让锁定成为我们的准心」）
//
//   **该设计的唯一前提**：锚点必须是一个真实的 `CharacterControllerBase` 实例 ——
//   因为 `CharacterControllerBase.LockonTargetCharacter` 的类型就是它，
//   既不能置 null（历史事故：战斗系统崩溃），也不能塞空物体（类型不符）。
//
// ── 为什么需要本探针 ────────────────────────────────────────────────────
//   旧方案 `PhantomTarget.cs` 里写过 `_go.AddComponent<CharacterControllerBase>()`，
//   但**从未被执行过**（`PhantomTargetEnabled` 一直是 false）——
//   所以"能否创建"是**未验证**状态，不是"已失败"状态。
//
//   本探针把这件事单独测掉，输出足够的信息判断：
//     · AddComponent 是否返回非 null
//     · 对象的 name / transform / 是否存活
//     · 能否安全读取 CharacterControllerBase 上的字段（读崩溃说明实例不可用）
//
// ── 安全性 ──────────────────────────────────────────────────────────────
//   · 只在**按键触发**时创建（不是每帧），且创建后立即销毁 —— 不留残留
//   · 只读实例自身的字段，**不写入任何字段、不接入任何游戏系统**
//   · 不碰 LockonTargetCharacter（这一步只是"能不能造出零件"，不是"装上零件"）

using System;
using BepInEx.Logging;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 验证能否创建一个可用的 CharacterControllerBase 实例（用户设计的前提条件）。
/// </summary>
internal static class AnchorInstanceProbe
{
    // 探测用的对象名（创建后立即销毁，不会长期存在）
    private const string ProbeObjectName = "AidealRays_AnchorProbe";

    private static ManualLogSource? _log;

    public static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// 创建一次 CharacterControllerBase 实例、读它几个字段、然后销毁。
    /// 目的只有一个：确定"零件能不能造出来"。
    /// </summary>
    public static void Test()
    {
        if (_log is null)
        {
            return;
        }
        GameObject? go = null;
        try
        {
            _log.LogInfo("[ANCHORPROBE] ===== instance test begin =====");

            go = new GameObject(ProbeObjectName);
            _log.LogInfo($"[ANCHORPROBE] GameObject created: name='{go.name}'"
                         + $" active={go.activeSelf}");

            // ★ 核心验证：能否把游戏自己的 CharacterControllerBase 加到对象上
            CharacterControllerBase? ccb = go.AddComponent<CharacterControllerBase>();
            if (ccb is null)
            {
                _log.LogError("[ANCHORPROBE] AddComponent<CharacterControllerBase>() returned NULL"
                              + " -> cannot create an anchor instance this way."
                              + " The 'lock target = anchor' design needs another route.");
                return;
            }
            _log.LogInfo("[ANCHORPROBE] AddComponent SUCCEEDED: instance created");

            // 读几个字段确认实例可用（读崩溃 => 实例是空壳，不可用）
            try
            {
                Transform t = ccb.transform;
                _log.LogInfo($"[ANCHORPROBE] read transform OK: pos=({t.position.x:F1},"
                             + $"{t.position.y:F1},{t.position.z:F1}) name='{t.name}'");
            }
            catch (Exception ex)
            {
                _log.LogError($"[ANCHORPROBE] read transform FAILED: {ex.Message}"
                              + " -> instance exists but is not usable");
            }

            try
            {
                Vector3 f = ccb.transform.forward;
                _log.LogInfo($"[ANCHORPROBE] transform.forward OK: ({f.x:F2},{f.y:F2},{f.z:F2})");
            }
            catch (Exception ex)
            {
                _log.LogError($"[ANCHORPROBE] read forward FAILED: {ex.Message}");
            }

            // 读一个游戏自己的属性（验证这个实例能正常走 IL2CPP 的属性访问）
            try
            {
                bool lookLock = ccb.IsLookLock;
                _log.LogInfo($"[ANCHORPROBE] read game property IsLookLock OK: {lookLock}");
            }
            catch (Exception ex)
            {
                _log.LogError($"[ANCHORPROBE] read IsLookLock FAILED: {ex.Message}");
            }

            // 读锁定目标字段（只读，不写！）—— 验证能访问该字段本身
            try
            {
                CharacterControllerBase? lt = ccb.LockonTargetCharacter;
                _log.LogInfo($"[ANCHORPROBE] read LockonTargetCharacter OK:"
                             + $" {(lt is not null ? lt.name : "null")}");
            }
            catch (Exception ex)
            {
                _log.LogError($"[ANCHORPROBE] read LockonTargetCharacter FAILED: {ex.Message}");
            }

            _log.LogInfo("[ANCHORPROBE] ===== test end (verdict above) =====");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"[ANCHORPROBE] test threw: {ex}");
        }
        finally
        {
            // 无论成败都销毁，不留残留对象
            if (go is not null)
            {
                try
                {
                    UnityEngine.Object.Destroy(go);
                    _log.LogInfo("[ANCHORPROBE] probe object destroyed (no leftovers)");
                }
                catch (Exception ex)
                {
                    _log.LogError($"[ANCHORPROBE] cleanup failed: {ex.Message}");
                }
            }
        }
    }
}
