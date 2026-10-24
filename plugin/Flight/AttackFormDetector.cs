// 驱动妖精 AI-deal-Rays · 当前攻击形态判定（刀 / 枪）
//
// ── 为什么需要它 ────────────────────────────────────────────────────────
//   用户指出（实测）：「爱迪尔的能量弹攻击大部分是锁定性，如果跟着锚点走的话
//   会出现打转的情况，所以我的建议就是把远程的锚点拉远，同步转角」。
//
//   机制：能量弹是**制导（homing）**的，每帧朝锁定目标转向。
//   而 F3 模式下锁定目标 = 瞄准锚点，锚点原点又是爱迪尔自己 ⇒
//   若锚点离爱迪尔太近（12 米），弹药会朝它飞、它又跟着爱迪尔走 ⇒ **绕圈**。
//   ⇒ 远程必须把锚点拉远到"足够的前导距离"。
//
//   所以锚点距离要**按形态分组**：刀近、枪远。这就必须先能判定当前形态。
//
// ── 判定方法（已实测双向验证）──────────────────────────────────────────
//   读游戏自己的权威值 `CharacterControllerBase.GetCurrentAttackParam()`，
//   再拿它去 `AttackList`（近战，每套 10 个槽位）/ `ShotList`（远程，每套 1 个槽位）
//   里做**身份比对**（AttackParam 是 MonoBehaviour，引用类型）。
//
//   实测结果（SelectionProbe 日志）：
//     枪：currentAttackParam = 'ShotParam'(id=-49742)  → rangedSlot=[0]EXS_SHOT.Shot
//     刀：currentAttackParam = 'SingleAttackParam3'(id=-48250) → meleeSlot=[0]EXS_BLADE.Single3
//   两个方向都精确命中 ⇒ 判定可靠，**不依赖任何推断**。
//
//   为什么不用 `AttackSelection.CrossRange` 之类的数值推断：
//   该字段语义未确认（实测 8/10/12 递增、HP+ 也递增，更像成长阶段），
//   基于它做判断会重蹈"凭推断改坏游戏"的覆辙。

using System;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>当前生效的攻击形态。</summary>
internal enum AttackForm
{
    /// <summary>无法判定（未进战斗 / 无攻击参数）。</summary>
    Unknown = 0,
    /// <summary>近战（刀）。</summary>
    Melee = 1,
    /// <summary>远程（枪）。</summary>
    Ranged = 2,
}

/// <summary>用游戏自己的权威值判定当前攻击形态。纯只读。</summary>
internal static class AttackFormDetector
{
    /// <summary>
    /// 判定当前形态。任何异常都返回 Unknown（由调用方决定回退行为）——
    /// 不静默假装成功。
    /// </summary>
    public static AttackForm Detect(PlayerControlCustom? player)
    {
        if (player is null)
        {
            return AttackForm.Unknown;
        }
        try
        {
            AttackParam? cur = player.GetCurrentAttackParam();
            if (cur is null)
            {
                return AttackForm.Unknown;
            }

            SelectionManager? sel = player.Selection;
            if (sel is null)
            {
                return AttackForm.Unknown;
            }

            bool inMelee = InAttackList(sel.AttackList, cur);
            bool inRanged = InShotList(sel.ShotList, cur);

            if (inRanged && !inMelee)
            {
                return AttackForm.Ranged;
            }
            if (inMelee && !inRanged)
            {
                return AttackForm.Melee;
            }
            // 两表都命中或都不命中：无法判定（由调用方回退到默认距离）
            return AttackForm.Unknown;
        }
        catch (Exception)
        {
            // 判定失败不是致命错误：返回 Unknown，调用方用默认距离。
            // 这里不写日志以避免每帧刷屏 —— 调用方会在"形态变化"时记录。
            return AttackForm.Unknown;
        }
    }

    /// <summary>当前攻击参数是否属于某套近战配置（5 段普攻 + 5 个绝招，共 10 个槽位）。</summary>
    private static bool InAttackList(
        Il2CppSystem.Collections.Generic.List<AttackSelection>? list, AttackParam cur)
    {
        if (list is null)
        {
            return false;
        }
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            AttackSelection? a = list[i];
            if (a is null)
            {
                continue;
            }
            if (Same(a.SingleAttack1Param, cur) || Same(a.SingleAttack2Param, cur) ||
                Same(a.SingleAttack3Param, cur) || Same(a.SingleAttack4Param, cur) ||
                Same(a.SingleAttack5Param, cur) ||
                Same(a.ExAttack1Param, cur) || Same(a.ExAttack2Param, cur) ||
                Same(a.ExAttack3Param, cur) || Same(a.ExAttack4Param, cur) ||
                Same(a.ExAttack5Param, cur))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>当前攻击参数是否属于某套远程配置（每套只有一个 ShotParam）。</summary>
    private static bool InShotList(
        Il2CppSystem.Collections.Generic.List<ShotSelection>? list, AttackParam cur)
    {
        if (list is null)
        {
            return false;
        }
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            ShotSelection? s = list[i];
            if (s is not null && Same(s.ShotParam, cur))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 引用比较。`AttackParam` 是 MonoBehaviour（引用类型），
    /// 配置表里放的就是同一个实例 —— 用身份而不是值比较（值比较在 IL2CPP 下语义不明）。
    /// </summary>
    private static bool Same(AttackParam? a, AttackParam? b)
    {
        if (a is null || b is null)
        {
            return false;
        }
        return ReferenceEquals(a, b);
    }
}
