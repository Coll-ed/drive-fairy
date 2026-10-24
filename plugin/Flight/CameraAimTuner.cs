// 驱动妖精 AI-deal-Rays · 锁定相机瞄准重定向（让相机三轴跟随敌人）
//
// ── 这个模块只做一件事 ──────────────────────────────────────────────────
//   把锁定相机的 LookAt 从"玩家自己"/"玩家与敌人的中点"改成 **敌人本体**。
//
// ── 为什么这才是 Z 轴问题的正解（实测链条）──────────────────────────────
//   用户实测：「原版 XY 轴是已经做到了锁定敌人的，为什么 Z 轴不行呢？」
//
//   1) 主力相机 LockonLong（实测 prio=100，激活 34 次，最常用）：
//        lookAt = AIdealBattle（玩家自己）
//      ⇒ 它的瞄准对象根本不是敌人，所以水平方向也只是"因为相机在玩家身后、
//         敌人在正前方"而**看起来**对准了。玩家一飞高，垂直方向立刻暴露偏差。
//
//   2) 四个"单锁"相机（LockonCrossSingle / LockonMiddleSingle /
//      LockonCrossCross / LockonCrossMulti）：
//        lookAt = CinemachineTargetGroup（玩家 + 敌人，等权，posMode=GroupCenter）
//      ⇒ 瞄准的是两人**包围盒中心**。
//        水平分量：两人贴身时中点≈敌人位置 ⇒ XY 看起来锁住了。
//        垂直分量：中点是两人高度的**平均** ⇒ 玩家飞高时瞄准点抬到两人之间
//                  ⇒ 相机对着敌人头顶上方的空气。**这就是 Z 轴不对称的几何原因。**
//
//   3) DashReady（冲刺蓄力，用户最初报告问题的相机）：
//        lookAt = AIdealBattle（玩家自己）—— 同样从未指向敌人。
//
//   ⇒ 统一修法：把上述相机的 LookAt 指向敌人。
//     · FramingTransposer / Composer 的 Aim 阶段会把**敌人的位置**对齐到屏幕
//       ⇒ 三个轴一起对准 ⇒ 飞高时相机俯视、飞低时仰视。
//     · Follow 一律不动 ⇒ 相机位置逻辑仍是原版 ⇒ 玩家仍在画面里，
//       且 m_GroupFramingMode=HorizontalAndVertical 会缩放着保证两人入镜。
//
// ── 关于被删掉的错误尝试（留档，避免重犯）──────────────────────────────
//   曾经的"方案 B"：把目标组 posMode 改成 GroupAverage + 玩家权重设 0.2，
//   想把瞄准点"用权重拉向敌人"。
//   **实测无效** —— 因为真正生效的相机（LockonLong / DashReady）根本不看目标组，
//   它们的 lookAt 就是玩家自己。改组的权重对它们毫无影响。
//   该方案及相关配置（LockCameraAimTune / LockCameraPlayerWeight）已移除。
//
// ── 安全性 ──────────────────────────────────────────────────────────────
//   · 只写 vcam.LookAt（相机指向），不碰任何战斗状态
//     （不碰 LockonTargetCharacter / LockonLock —— 那些是历史事故来源）
//   · 只在框架启用（FreeFlight）时由调用方启用；原版模式完全不介入
//   · 没有锁定目标时把 LookAt 还原为最初记录的值 ⇒ 脱战后恢复原版行为
//   · 每次改变都记日志，可取证

using System;
using BepInEx.Logging;
using Cinemachine;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 把锁定相机的 LookAt 重定向到敌人本体，使相机三轴跟随敌人（含垂直方向）。
/// </summary>
internal sealed class CameraAimTuner
{
    private readonly ManualLogSource _log;

    // 记住"第一次改动前"的 LookAt，用于在无锁定目标时还原。
    // 每个实例只管一个 vcam，所以一个字段就够。
    private Transform? _originalLookAt;
    // 当前是否已把 LookAt 设为敌人（用于避免每帧重复写字段与重复打日志）
    private Transform? _currentEnemy;

    public CameraAimTuner(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// 供"lookAt 是目标组"的相机使用：优先用传入的锁定目标，
    /// 取不到时退回从目标组里找第一个非玩家成员；group 为 null 时由 vcam 自行解析目标组。
    ///
    /// 为什么要这个包装：单锁相机的 lookAt 原本是 CinemachineTargetGroup，
    /// 而组的成员会随战斗变化。用"游戏自己的锁定目标"优先、组内查找兜底，最稳。
    /// </summary>
    public void AimAtGroupOrEnemy(string label, CinemachineVirtualCamera? vcam,
                                  CinemachineTargetGroup? group, Transform player, Transform? enemy)
    {
        Transform? target = enemy;
        if (target is null)
        {
            // 没拿到锁定目标：尝试从组里找（也用于组自身仍在提供敌人的情况）
            CinemachineTargetGroup? g = group;
            if (g is null && vcam is not null)
            {
                try
                {
                    g = vcam.LookAt is not null ? vcam.LookAt.GetComponent<CinemachineTargetGroup>() : null;
                    if (g is null && vcam.Follow is not null)
                    {
                        g = vcam.Follow.GetComponent<CinemachineTargetGroup>();
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError($"[AIMTUNE] {label} resolve group failed: {ex.Message}");
                }
            }
            target = FindEnemyInGroup(g, player);
        }
        AimAt(label, vcam, target);
    }

    /// <summary>
    /// 把 vcam 的 LookAt 指向 enemy。
    /// enemy 为 null 表示当前没有锁定目标 ⇒ 还原为最初的 LookAt（游戏原样）。
    /// </summary>
    public void AimAt(string label, CinemachineVirtualCamera? vcam, Transform? enemy)
    {
        if (vcam is null)
        {
            return;
        }
        try
        {
            if (enemy is null)
            {
                Restore(label, vcam);
                return;
            }
            if (_originalLookAt is null)
            {
                _originalLookAt = vcam.LookAt;    // 记下原值以便还原
            }
            if (_currentEnemy == enemy)
            {
                return;                            // 已经是它了，不重复写
            }
            vcam.LookAt = enemy;
            _currentEnemy = enemy;
            _log.LogInfo($"[AIMTUNE] {label}: LookAt -> '{enemy.name}'"
                         + $" (was '{(_originalLookAt is not null ? _originalLookAt.name : "null")}')"
                         + $"; Follow kept '{(vcam.Follow is not null ? vcam.Follow.name : "null")}'");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"[AIMTUNE] {label} aim failed: {ex}");
        }
    }

    /// <summary>还原 LookAt 为最初记录的值（无锁定目标 / 关闭功能时）。</summary>
    public void Restore(string label, CinemachineVirtualCamera? vcam)
    {
        if (vcam is null || _originalLookAt is null)
        {
            return;
        }
        try
        {
            if (_currentEnemy is null)
            {
                return;                            // 本来就没改过
            }
            if (vcam.LookAt != _originalLookAt)
            {
                vcam.LookAt = _originalLookAt;
                _log.LogInfo($"[AIMTUNE] {label}: LookAt restored to '{_originalLookAt.name}'");
            }
            _currentEnemy = null;
        }
        catch (Exception ex)
        {
            _log.LogError($"[AIMTUNE] {label} restore failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 从玩家身上取"当前锁定目标"的 transform。
    /// 用游戏自己的字段 CharacterControllerBase.LockonTargetCharacter —— 它就是
    /// 原版判断"锁的是谁"的权威来源（不是我们推断的）。
    /// 取不到（未锁定/脱战）返回 null。
    /// </summary>
    public static Transform? ResolveLockTarget(PlayerControlCustom player)
    {
        try
        {
            CharacterControllerBase? t = player.LockonTargetCharacter;
            return t is not null ? t.transform : null;
        }
        catch (Exception)
        {
            // 拿不到目标不算错误（未锁定是正常状态），返回 null 即可。
            // 但要在调用方可见 —— AimAt(null) 会走到还原分支并记录。
            return null;
        }
    }

    /// <summary>
    /// 从目标组里找出第一个非玩家成员（=敌人）。
    /// 供那些 lookAt 是目标组的相机使用（组里成员可能随时变化）。
    /// </summary>
    public static Transform? FindEnemyInGroup(CinemachineTargetGroup? group, Transform player)
    {
        if (group is null || player is null)
        {
            return null;
        }
        try
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<CinemachineTargetGroup.Target>? targets = group.m_Targets;
            if (targets is null || targets.Length == 0)
            {
                return null;
            }
            for (int i = 0; i < targets.Length; i++)
            {
                Transform? t = targets[i].target;
                if (t is not null && t != player)
                {
                    return t;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }
}
