// 驱动妖精 AI-deal-Rays · 朝向与攻击方向探针（只读）
//
// ── 为什么需要这个 ──────────────────────────────────────────────────────
//   "第三人称脱离准心"要求把【朝向】接到我们的瞄准锚点上。
//   反编译确认存在这些接线点：
//       PlayerControlCustom.ApproachAllow : LookTargetController
//       LookTargetController.Target : Transform      （SetTarget 的入口）
//       CharacterControllerBase.LookTarget(step, gravity) / LookTargetForce() / LookTargetUpdate()
//
//   但 **方法体不可读**（IL 已丢弃），所以无法静态确定：
//     ① 这些 LookTargetController 实例在运行时指向谁
//     ② 攻击方向到底是跟着"身体前方"还是别的什么
//
//   ⇒ 本探针用**只读 + 几何测量**回答这两个问题，不猜。
//
// ── 测什么 ──────────────────────────────────────────────────────────────
//   A. 遍历场景里所有 LookTargetController，报告它们的 Target / LookStep / Gravity
//   B. 报告身体朝向（transform.forward）与三个参考方向的夹角：
//        · 到瞄准锚点
//        · 到锁定目标
//        · 到相机前方
//      夹角最小的那个，就是"身体实际在跟谁"——这是判断接线点是否有效的客观依据。
//
// ── 安全性 ──────────────────────────────────────────────────────────────
//   纯只读：只读字段与 transform、只写日志。不设置任何游戏状态。

using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 朝向探针：报告 LookTargetController 的目标与身体朝向的几何关系。
/// </summary>
internal static class FacingProbe
{
    // 报告的最大实例数（防御性）
    private const int MaxReported = 12;
    // 夹角小于此值（度）视为"朝向基本对齐某参考方向"
    private const float AlignedThresholdDeg = 25f;

    private static ManualLogSource? _log;

    public static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// 打印朝向信息。anchor 为我们的瞄准锚点（可能为 null，那就不测它）。
    /// </summary>
    public static void Dump(PlayerControlCustom player, Transform? anchor)
    {
        if (_log is null || player is null)
        {
            return;
        }
        try
        {
            _log.LogInfo("[FACEPROBE] ===== facing dump begin =====");

            Transform body = player.transform;
            Vector3 bodyFwd = body.forward;

            // ---- A. 场景里所有 LookTargetController ----
            DumpLookTargetControllers(player, body);

            // ---- B. 身体朝向 vs 三个参考方向 ----
            Vector3 defPos = body.position;
            ReportAngle("to ANCHOR", bodyFwd, anchor is not null ? anchor.position - defPos : (Vector3?)null);

            Transform? enemy = CameraAimTuner.ResolveLockTarget(player);
            ReportAngle("to LOCK TARGET", bodyFwd, enemy is not null ? enemy.position - defPos : (Vector3?)null);

            Camera? cam = Camera.main;
            ReportAngle("to CAMERA FWD", bodyFwd, cam is not null ? cam.transform.forward : (Vector3?)null);

            if (anchor is not null)
            {
                Vector3 toAnchor = anchor.position - defPos;
                Vector3? toEnemy = enemy is not null ? enemy.position - defPos : (Vector3?)null;
                if (toEnemy.HasValue && toAnchor.sqrMagnitude > 1e-6f && toEnemy.Value.sqrMagnitude > 1e-6f)
                {
                    // 锚点方向与敌人方向的夹角：这个值决定"接锚点后会不会打偏"
                    float ang = Vector3.Angle(toAnchor.normalized, toEnemy.Value.normalized);
                    _log.LogInfo($"[FACEPROBE] anchor-vs-enemy angle = {ang:F1} deg"
                                 + $" (0 = aiming exactly at the locked enemy)");
                }
            }

            _log.LogInfo("[FACEPROBE] ===== dump end =====");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"[FACEPROBE] dump failed: {ex}");
        }
    }

    /// <summary>遍历所有 LookTargetController 并报告其目标。</summary>
    private static void DumpLookTargetControllers(PlayerControlCustom player, Transform body)
    {
        if (_log is null)
        {
            return;
        }

        // 玩家身上明确存在的那个（反编译确认 L432）
        ReportOne("player.ApproachAllow", player.ApproachAllow, body);

        // 场景里其余的（可能挂在子对象/别的组件上）—— 用 FindObjectsOfType 全量枚举
        try
        {
            LookTargetController[] all = UnityEngine.Object.FindObjectsOfType<LookTargetController>();
            _log.LogInfo($"[FACEPROBE] scene LookTargetController count = {all.Length}");
            int limit = Math.Min(all.Length, MaxReported);
            for (int i = 0; i < limit; i++)
            {
                LookTargetController c = all[i];
                if (c is null)
                {
                    continue;
                }
                // 已经单独报过 ApproachAllow 的不重复
                if (player.ApproachAllow is not null && c == player.ApproachAllow)
                {
                    continue;
                }
                ReportOne($"scene[{i}] host={(c.gameObject is not null ? c.gameObject.name : "?")}",
                          c, body);
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"[FACEPROBE] enumerate LookTargetController failed: {ex.Message}");
        }
    }

    /// <summary>报告单个 LookTargetController 的状态。</summary>
    private static void ReportOne(string label, LookTargetController? c, Transform body)
    {
        if (_log is null)
        {
            return;
        }
        if (c is null)
        {
            _log.LogInfo($"[FACEPROBE] {label} = null");
            return;
        }
        try
        {
            Transform? t = c.Target;
            string targetName = t is not null ? t.name : "null";
            string detail = $"target={targetName} lookStep={c.LookStep:F2} gravity={c.Gravity}";
            if (t is not null)
            {
                float dist = Vector3.Distance(t.position, body.position);
                detail += $" targetDist={dist:F1}";
            }
            _log.LogInfo($"[FACEPROBE] {label}: {detail}");
        }
        catch (Exception ex)
        {
            _log.LogError($"[FACEPROBE] {label} read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 报告 bodyFwd 与某方向的夹角，并标注是否"基本对齐"。
    /// 这是判断"身体在跟谁"的客观依据 —— 不看名字，只看角度。
    /// </summary>
    private static void ReportAngle(string label, Vector3 bodyFwd, Vector3? refDir)
    {
        if (_log is null)
        {
            return;
        }
        if (!refDir.HasValue || refDir.Value.sqrMagnitude < 1e-6f)
        {
            _log.LogInfo($"[FACEPROBE] bodyFwd vs {label}: n/a");
            return;
        }
        float ang = Vector3.Angle(bodyFwd, refDir.Value.normalized);
        string mark = ang <= AlignedThresholdDeg ? "  <== ALIGNED" : string.Empty;
        _log.LogInfo($"[FACEPROBE] bodyFwd vs {label}: {ang:F1} deg{mark}");
    }
}
