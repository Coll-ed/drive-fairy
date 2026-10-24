// 驱动妖精 AI-deal-Rays · 锁定目标重定向（向相机视线看齐）
//
// ── 用户的设计意图 ──────────────────────────────────────────────────────
//   原版：锁定目标 = 离玩家**最近**的敌人（配合"自动距离切换"的战斗风格）。
//   目标：锁定目标 = 最接近**相机视线方向**的敌人，从而支持：
//     · 相机瞄哪个敌人就锁哪个（自由选择目标）
//     · 朝任意方向释放绝招（含三维方向）
//
// ── 为什么选这个方案（对比"造幻影敌人"）────────────────────────────────
//   方案 A（造隐形幻影实例）：需要 IL2CPP 实例化 CharacterControllerBase，
//     且该实例会被 AI / 胜负判定当作真单位（未知副作用）。
//   方案 B（本方案，重排候选列表）：**不创建任何对象**，
//     只改变"游戏从候选里挑谁"的顺序 —— 其余全部照原样，
//     因此准心、锁定相机、绝招、乱舞绕敌等依赖锁定的机制全都继续工作。
//   方案 C（每帧写 LockonTargetCharacter）：风险最高 ——
//     该字段是战斗枢纽，历史上把它写成 null 曾导致整个战斗系统崩溃
//     （敌人 AI 消失、锁定失效、战斗 UI 消失）。
//   => 选 B：干预面最小，且不写入任何战斗状态字段。
//
// ── 实现原理 ───────────────────────────────────────────────────────────
//   反编译确认（PlayerControlCustom.cs）：
//     public unsafe List<CharacterControllerBase> GetLockonEnemys()   L6680
//     返回 Il2CppSystem.Collections.Generic.List<CharacterControllerBase>（**可变**）
//   该 List 的 Count / 索引器（get_Item / set_Item）均可用（见 Il2Cppmscorlib）。
//   因此只要在这个方法**返回之后**调整元素顺序，把"最贴合相机视线"的敌人
//   换到首位，游戏的选取逻辑就会优先取它。
//
//   注意：不用 List.Sort(Comparison<T>) —— Comparison<T> 是 Il2Cpp 委托，
//   需经 DelegateSupport 转换；而"找最优 + 换到首位"用朴素循环即可，
//   少一层委托转换就少一处可能失败的地方。

using System;
using BepInEx.Logging;
using Il2CppSystem.Collections.Generic;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 锁定目标重定向：把"最接近相机视线的敌人"排到候选列表首位。
/// 纯顺序调整，不创建对象、不写战斗状态字段。
/// </summary>
internal static class LockTargetRedirect
{
    // 低于此分值的候选视为"不在视线附近"，不抢占首位。
    // 取值理由：1.0 表示目标正好在相机正前方；0 表示在正侧方或后方。
    // 0.5 约等于偏离视线 60° 以内 —— 既不会锁到身后的敌人，
    // 也不会因为目标略微偏出准心就拒绝锁定。
    private const float MinAimScore = 0.5f;
    // 日志节流：每 N 次调整报告一次
    private const int LogEveryCalls = 120;
    // 与视线的最小距离：太近的敌人 direction 归一化会噪声很大
    private const float MinDirectionMagnitude = 0.01f;

    private static ManualLogSource? _log;
    private static int _callCount;

    public static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// 由 Harmony postfix 调用：重排候选列表，使最贴合相机视线的敌人排首位。
    /// 返回是否有改动（供上层判断，也可不关心）。
    /// </summary>
    public static void ReorderByCameraAim(List<CharacterControllerBase>? candidates)
    {
        if (_log is null || candidates is null)
        {
            return;
        }
        try
        {
            int n = candidates.Count;
            if (n <= 1)
            {
                return;   // 只有一个（或没有）候选，顺序无从调整
            }

            Camera? cam = Camera.main;
            if (cam is null)
            {
                return;   // 拿不到相机就不干预
            }
            Transform camT = cam.transform;

            int best = -1;
            float bestScore = float.NegativeInfinity;
            float bestDist = float.NaN;
            for (int i = 0; i < n; i++)
            {
                CharacterControllerBase? s = candidates[i];
                if (s is null)
                {
                    continue;
                }
                Vector3 toTarget;
                try
                {
                    toTarget = s.transform.position - camT.position;
                }
                catch (Exception ex)
                {
                    // 单个候选取位置失败不应打断整体逻辑，但要可见
                    _log.LogError($"[LOCKREDIR] read candidate position failed: {ex.Message}");
                    continue;
                }
                float mag = toTarget.magnitude;
                if (mag < MinDirectionMagnitude)
                {
                    continue;
                }
                // 评分 = 方向与相机正前方的点积（-1..1）。越大越贴近视线中心。
                float score = Vector3.Dot(toTarget / mag, camT.forward);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                    bestDist = mag;
                }
            }

            if (best < 0)
            {
                return;   // 没有任何可用候选
            }
            if (bestScore < MinAimScore)
            {
                // 视线附近没有敌人：保持原版顺序（按距离），不强行改目标
                if (++_callCount % LogEveryCalls == 0)
                {
                    _log.LogInfo($"[LOCKREDIR] no target near aim (best score={bestScore:F2}"
                                 + $" < {MinAimScore:F2}); keeping vanilla order");
                }
                return;
            }
            if (best == 0)
            {
                // 视线方向上的敌人本来就是首位，无需改动
                if (++_callCount % LogEveryCalls == 0)
                {
                    _log.LogInfo($"[LOCKREDIR] aim target already first (score={bestScore:F2}"
                                 + $" dist={bestDist:F1} candidates={n})");
                }
                return;
            }

            // 把最佳候选移到首位：逐个前移，避免依赖额外的 List API
            CharacterControllerBase chosen = candidates[best];
            for (int j = best; j > 0; j--)
            {
                candidates[j] = candidates[j - 1];
            }
            candidates[0] = chosen;

            if (++_callCount % LogEveryCalls == 0)
            {
                _log.LogInfo($"[LOCKREDIR] promoted candidate #{best} to first"
                             + $" (score={bestScore:F2} dist={bestDist:F1} candidates={n})");
            }
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"lock redirect failed: {ex}");
        }
    }
}
