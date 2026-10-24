// 驱动妖精 AI-deal-Rays · 相机距离调节（F3 模式）
//
// ── 需求（用户实测）────────────────────────────────────────────────────
//   用户原话：「还有就是视角太远了，要离爱迪尔近一点」
//
// ── 为什么不能改 camDist（反编译 + 探针实测）────────────────────────────
//   F10 相机探针输出：
//     LockonLong.components:        Composer=Y  Transposer=Y  FramingTransposer=N
//     LockonCrossSingle.components: Composer=N  Transposer=N  FramingTransposer=Y
//                                        camDist=5.00
//   ⇒ **实际激活的锁定相机（LockonLong / DashReady）用的是 `CinemachineTransposer`**，
//     它的距离来自 `m_FollowOffset`（一个向量），**不是** `FramingTransposer.m_CameraDistance`。
//     改 camDist 对这些相机完全无效 —— 这是必须用对字段的原因。
//
// ── 做法 ───────────────────────────────────────────────────────────────
//   在每个激活的 vcam 上，把 `Transposer.m_FollowOffset` 的长度按比例缩放。
//   · 只缩放"跟随偏移"的长度，不改变方向 —— 保持游戏原生的构图角度
//   · 用比例（不是绝对值）⇒ 游戏自己调整距离时我们也跟着走，不硬对抗
//   · 可逆：记录原始长度，关闭功能或退出 F3 时还原
//
// ── 边界 ───────────────────────────────────────────────────────────────
//   · 只处理"当前激活"的 vcam：未激活的相机不碰，避免污染游戏状态
//   · 每个相机独立记录原始长度，重复应用不会累积缩小（幂等）

using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Cinemachine;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>按比例缩放当前激活 vcam 的跟随距离，让镜头靠近角色。</summary>
internal sealed class CameraDistanceTuner
{
    // 记录每个 vcam 的原始 FollowOffset 长度（按实例 id 索引），保证可逆且幂等
    private readonly Dictionary<int, float> _originalLengths = new();
    private readonly ManualLogSource _log;
    private float _lastAppliedScale = float.NaN;
    private bool _loggedOnce;

    public CameraDistanceTuner(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// 把当前激活的相机距离缩放到原始长度的 <paramref name="scale"/> 倍。
    /// </summary>
    /// <param name="vcams">场景里所有 vcam</param>
    /// <param name="scale">缩放比例（&lt;1 拉近，&gt;1 拉远）。等于 1 时视为关闭。</param>
    public void Apply(CinemachineVirtualCameraBase[]? vcams, float scale)
    {
        if (vcams is null || vcams.Length == 0)
        {
            return;
        }
        // 比例接近 1 视为不干预（不写任何相机状态）
        if (Math.Abs(scale - 1f) < 0.001f)
        {
            if (!float.IsNaN(_lastAppliedScale))
            {
                Restore(vcams);
            }
            return;
        }

        try
        {
            int touched = 0;
            for (int i = 0; i < vcams.Length; i++)
            {
                CinemachineVirtualCameraBase? v = vcams[i];
                if (v is null)
                {
                    continue;
                }
                // 只调"正在渲染"的相机 —— 未激活的相机保持游戏原状
                if (!v.isActiveAndEnabled)
                {
                    continue;
                }
                CinemachineTransposer? t = v.GetComponent<CinemachineTransposer>();
                if (t is null)
                {
                    continue;   // 该相机用别的管线（Composer / FramingTransposer），不归本类管
                }
                if (ApplyToOne(v, t, scale))
                {
                    touched++;
                }
            }

            if (!_loggedOnce && touched > 0)
            {
                _loggedOnce = true;
                _log.LogInfo($"[CAMTUNE] camera distance scale {scale:F2} applied to"
                             + $" {touched} active vcam(s) using Transposer");
            }
            _lastAppliedScale = scale;
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"[CAMTUNE] apply failed: {ex}");
        }
    }

    /// <summary>缩放单个相机的跟随偏移长度。返回是否发生了写入。</summary>
    private bool ApplyToOne(CinemachineVirtualCameraBase v, CinemachineTransposer t, float scale)
    {
        int id = v.GetInstanceID();
        Vector3 off = t.m_FollowOffset;

        if (!_originalLengths.TryGetValue(id, out float originalLen))
        {
            originalLen = off.magnitude;
            // 长度为 0（没有偏移）时无事可做，也不该记录 —— 否则恢复时会写出零向量
            if (originalLen < MinimalLength)
            {
                return false;
            }
            _originalLengths[id] = originalLen;
        }

        // 已是目标长度 → 不写（幂等，避免每帧无谓写入）
        float wantLen = originalLen * scale;
        if (Math.Abs(off.magnitude - wantLen) < LengthEpsilon)
        {
            return false;
        }
        // 只改长度，方向保持不变（维持游戏原生构图角度）
        Vector3 dir = off.sqrMagnitude > 0f ? off.normalized : Vector3.back;
        t.m_FollowOffset = dir * wantLen;
        return true;
    }

    /// <summary>把所有改过的相机还原为原始长度。</summary>
    public void Restore(CinemachineVirtualCameraBase[]? vcams)
    {
        if (vcams is null || _originalLengths.Count == 0)
        {
            _lastAppliedScale = float.NaN;
            return;
        }
        try
        {
            int restored = 0;
            for (int i = 0; i < vcams.Length; i++)
            {
                CinemachineVirtualCameraBase? v = vcams[i];
                if (v is null)
                {
                    continue;
                }
                if (!_originalLengths.TryGetValue(v.GetInstanceID(), out float originalLen))
                {
                    continue;
                }
                CinemachineTransposer? t = v.GetComponent<CinemachineTransposer>();
                if (t is null)
                {
                    continue;
                }
                Vector3 off = t.m_FollowOffset;
                Vector3 dir = off.sqrMagnitude > 0f ? off.normalized : Vector3.back;
                t.m_FollowOffset = dir * originalLen;
                restored++;
            }
            if (restored > 0)
            {
                _log.LogInfo($"[CAMTUNE] restored original camera distance on {restored} vcam(s)");
            }
            _loggedOnce = false;
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMTUNE] restore failed: {ex.Message}");
        }
        finally
        {
            _lastAppliedScale = float.NaN;
        }
    }

    // 低于此长度视为"相机没有跟随偏移"，不参与缩放（单位：Unity 米）
    private const float MinimalLength = 0.01f;
    // 长度比较容差：小于 1 厘米视为"已是目标值"，避免每帧抖动写入
    private const float LengthEpsilon = 0.01f;
}
