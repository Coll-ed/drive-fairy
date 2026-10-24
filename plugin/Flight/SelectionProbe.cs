// 驱动妖精 AI-deal-Rays · 技能配置探查（只读诊断）
//
// ── 目的 ────────────────────────────────────────────────────────────────
//   反编译已确认玩家身上挂着全部技能配置表：
//       PlayerControlCustom.Selection : SelectionManager        (L366)
//
//   而 SelectionManager 含 9 个列表（近战/远程/冲刺/闪避/大招两路/反击/终结/吸取）：
//       public List<AttackSelection>         AttackList;
//       public List<ShotSelection>           ShotList;
//       public List<SonicBurstSelection>     SonicBurstList;
//       public List<SonicBurstLongSelection> SonicBurstLongList;
//       public List<CounterBurstSelection>   CounterBurstList;
//       public List<ExBurstSelection>        ExBurstList;
//       public List<DrainSelection>          DrainList;
//       public List<DashSelection>           DashList;
//       public List<AssistSelection>         AssistList;
//
//   但**这些列表在运行时的实际内容未知**（有几项、顺序如何、各是什么 Key）。
//   反编译只能看到结构，看不到数据。
//
//   ⇒ 本探查器的作用：把运行时真实数据打印出来，**在读到数据之前不做任何修改**。
//      这是"先取证再动手"的落实；也是从之前多次"凭推断改坏游戏"中吸取的教训。
//
// ── 安全性 ──────────────────────────────────────────────────────────────
//   **纯只读**：只读字段、只写日志。不设置任何游戏状态。
//   与历史上出事的做法（清空 LockonTargetCharacter）有本质区别。

using System;
using BepInEx.Logging;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 技能配置表探查器：把 PlayerControlCustom.Selection 的运行时内容打印出来。
/// </summary>
internal static class SelectionProbe
{
    // 单个列表最多打印多少项（避免刷屏；配置表通常只有几项）
    private const int MaxItemsPerList = 12;

    private static ManualLogSource? _log;

    public static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// 打印玩家的全部技能配置。
    /// 只读；每次调用输出固定几行。
    /// </summary>
    public static void Dump(PlayerControlCustom player)
    {
        if (_log is null || player is null)
        {
            return;
        }
        try
        {
            SelectionManager? sel = player.Selection;
            if (sel is null)
            {
                // 尚未加载或不在战斗中：明确记录，不静默跳过
                _log.LogWarning("[SELPROBE] player.Selection is null (not in battle yet?)");
                return;
            }

            _log.LogInfo("[SELPROBE] ===== SelectionManager dump begin =====");

            // 近战：每项含 5 段普攻 + 5 个绝招，以及 CrossRange 阈值
            DumpAttackList(sel.AttackList, "AttackList");

            // 远程射击：含 ExtendRange / ArmorPoint
            DumpShotList(sel.ShotList, "ShotList");

            // 冲刺：含 DashTimeline / LongRange / CountMax
            DumpDashList(sel.DashList, "DashList");

            // 闪避/辅助：含 MiddleRange / LockonMoveSpeed
            DumpAssistList(sel.AssistList, "AssistList");

            // 其余列表只需计数与 Key —— 它们的内部结构对本项目暂不关键
            LogCount(sel.SonicBurstList, "SonicBurstList");
            LogCount(sel.SonicBurstLongList, "SonicBurstLongList");
            LogCount(sel.CounterBurstList, "CounterBurstList");
            LogCount(sel.ExBurstList, "ExBurstList");
            LogCount(sel.DrainList, "DrainList");

            // ★ 当前生效的攻击形态（决定锚点该用哪套参数）
            DumpCurrentForm(player, sel);

            _log.LogInfo("[SELPROBE] ===== dump end =====");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"[SELPROBE] dump failed: {ex}");
        }
    }

    /// <summary>
    /// ★ 判定"当前生效的攻击形态"（刀 / 枪）。
    ///
    /// 做法：读游戏自己的权威值 —— `CharacterControllerBase.GetCurrentAttackParam()`，
    /// 再拿它去 `AttackList`（近战）/ `ShotList`（远程）里比对身份。
    ///
    /// 为什么可靠：`AttackParam` 是 MonoBehaviour（引用类型），
    /// 配置表里放的就是同一个实例，所以身份比较能确定它属于哪一套。
    /// **不依赖任何推断**（此前 `CrossRange` 的语义推断就错过一次）。
    ///
    /// 用途：锚点参数要"随武器切换变化"（用户要求），必须先知道当前是哪个形态。
    /// </summary>
    private static void DumpCurrentForm(PlayerControlCustom player,
                                        SelectionManager sel)
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            AttackParam? cur = player.GetCurrentAttackParam();
            if (cur is null)
            {
                _log.LogInfo("[SELPROBE] currentAttackParam = null (no form active yet)");
                return;
            }
            _log.LogInfo($"[SELPROBE] currentAttackParam = '{cur.name}'"
                         + $" (instanceId={cur.GetInstanceID()})");

            // 在近战配置表里找
            string meleeHit = FindInAttackList(sel.AttackList, cur);
            // 在远程配置表里找（ShotSelection 只有一个 ShotParam，比对很简单）
            string rangedHit = FindInShotList(sel.ShotList, cur);

            string verdict;
            if (meleeHit.Length > 0 && rangedHit.Length == 0)
            {
                verdict = "MELEE (blade)";
            }
            else if (rangedHit.Length > 0 && meleeHit.Length == 0)
            {
                verdict = "RANGED (gun)";
            }
            else if (meleeHit.Length > 0 && rangedHit.Length > 0)
            {
                verdict = "AMBIGUOUS (found in both lists!)";
            }
            else
            {
                verdict = "UNKNOWN (not found in either list)";
            }

            _log.LogInfo($"[SELPROBE] ★ current form = {verdict}"
                         + $" | meleeSlot={meleeHit} | rangedSlot={rangedHit}");
        }
        catch (Exception ex)
        {
            _log.LogError($"[SELPROBE] current form detect failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 在当前 AttackParam 是否属于某套近战配置。返回命中的槽位描述（未命中返回空串）。
    /// </summary>
    private static string FindInAttackList(
        Il2CppSystem.Collections.Generic.List<AttackSelection>? list, AttackParam cur)
    {
        if (list is null)
        {
            return string.Empty;
        }
        int n = list.Count;
        int limit = Math.Min(n, MaxItemsPerList);
        for (int i = 0; i < limit; i++)
        {
            AttackSelection? a = list[i];
            if (a is null)
            {
                continue;
            }
            // 逐槽比对：5 段普攻 + 5 个绝招
            if (Same(a.SingleAttack1Param, cur)) { return $"[{i}]{a.Key}.Single1"; }
            if (Same(a.SingleAttack2Param, cur)) { return $"[{i}]{a.Key}.Single2"; }
            if (Same(a.SingleAttack3Param, cur)) { return $"[{i}]{a.Key}.Single3"; }
            if (Same(a.SingleAttack4Param, cur)) { return $"[{i}]{a.Key}.Single4"; }
            if (Same(a.SingleAttack5Param, cur)) { return $"[{i}]{a.Key}.Single5"; }
            if (Same(a.ExAttack1Param, cur)) { return $"[{i}]{a.Key}.Ex1"; }
            if (Same(a.ExAttack2Param, cur)) { return $"[{i}]{a.Key}.Ex2"; }
            if (Same(a.ExAttack3Param, cur)) { return $"[{i}]{a.Key}.Ex3"; }
            if (Same(a.ExAttack4Param, cur)) { return $"[{i}]{a.Key}.Ex4"; }
            if (Same(a.ExAttack5Param, cur)) { return $"[{i}]{a.Key}.Ex5"; }
        }
        return string.Empty;
    }

    /// <summary>是否属于某套远程配置（每套只有一个 ShotParam）。</summary>
    private static string FindInShotList(
        Il2CppSystem.Collections.Generic.List<ShotSelection>? list, AttackParam cur)
    {
        if (list is null)
        {
            return string.Empty;
        }
        int n = list.Count;
        int limit = Math.Min(n, MaxItemsPerList);
        for (int i = 0; i < limit; i++)
        {
            ShotSelection? s = list[i];
            if (s is null)
            {
                continue;
            }
            if (Same(s.ShotParam, cur)) { return $"[{i}]{s.Key}.Shot"; }
        }
        return string.Empty;
    }

    /// <summary>
    /// 两个 AttackParam 是否同一个实例。
    /// 用引用比较（MonoBehaviour 是引用类型），而不是值比较 ——
    /// 值比较在 IL2CPP 下语义不明，可能给出错误结果。
    /// </summary>
    private static bool Same(AttackParam? a, AttackParam? b)
    {
        if (a is null || b is null)
        {
            return false;
        }
        return ReferenceEquals(a, b);
    }

    private static void DumpAttackList(Il2CppSystem.Collections.Generic.List<AttackSelection>? list, string label)
    {
        if (_log is null)
        {
            return;
        }
        if (list is null)
        {
            _log.LogInfo($"[SELPROBE] {label} = null");
            return;
        }
        int n = list.Count;
        _log.LogInfo($"[SELPROBE] {label}: {n} item(s)");
        int limit = Math.Min(n, MaxItemsPerList);
        for (int i = 0; i < limit; i++)
        {
            AttackSelection? a = list[i];
            if (a is null)
            {
                _log.LogInfo($"[SELPROBE]   [{i}] null");
                continue;
            }
            // Key = 该套属于哪个变体；CrossRange = 近战范围阈值（原版自动切换判据）
            // 后面 5 个 Single / 5 个 Ex 只报"是否存在"，避免刷屏
            string singles = CountNonNull(
                a.SingleAttack1Param, a.SingleAttack2Param, a.SingleAttack3Param,
                a.SingleAttack4Param, a.SingleAttack5Param);
            string exs = CountNonNull(
                a.ExAttack1Param, a.ExAttack2Param, a.ExAttack3Param,
                a.ExAttack4Param, a.ExAttack5Param);
            _log.LogInfo($"[SELPROBE]   [{i}] Key={a.Key}"
                         + $" CrossRange={a.CrossRange:F2}"
                         + $" CamDist={a.CameraDiostance:F2}"
                         + $" HP+{a.HitpointPlus}"
                         + $" singles={singles} exs={exs}");
        }
        if (n > limit)
        {
            _log.LogInfo($"[SELPROBE]   ... ({n - limit} more omitted)");
        }
    }

    private static void DumpShotList(Il2CppSystem.Collections.Generic.List<ShotSelection>? list, string label)
    {
        if (_log is null)
        {
            return;
        }
        if (list is null)
        {
            _log.LogInfo($"[SELPROBE] {label} = null");
            return;
        }
        int n = list.Count;
        _log.LogInfo($"[SELPROBE] {label}: {n} item(s)");
        int limit = Math.Min(n, MaxItemsPerList);
        for (int i = 0; i < limit; i++)
        {
            ShotSelection? s = list[i];
            if (s is null)
            {
                _log.LogInfo($"[SELPROBE]   [{i}] null");
                continue;
            }
            _log.LogInfo($"[SELPROBE]   [{i}] Key={s.Key}"
                         + $" ExtendRange={s.ExtendRange:F2}"
                         + $" ArmorPoint={s.ArmorPoint}"
                         + $" HP+{s.HitpointPlus}"
                         + $" hasShotParam={(s.ShotParam is not null)}");
        }
    }

    private static void DumpDashList(Il2CppSystem.Collections.Generic.List<DashSelection>? list, string label)
    {
        if (_log is null)
        {
            return;
        }
        if (list is null)
        {
            _log.LogInfo($"[SELPROBE] {label} = null");
            return;
        }
        int n = list.Count;
        _log.LogInfo($"[SELPROBE] {label}: {n} item(s)");
        int limit = Math.Min(n, MaxItemsPerList);
        for (int i = 0; i < limit; i++)
        {
            DashSelection? d = list[i];
            if (d is null)
            {
                _log.LogInfo($"[SELPROBE]   [{i}] null");
                continue;
            }
            _log.LogInfo($"[SELPROBE]   [{i}] Key={d.Key}"
                         + $" LongRange={d.LongRange:F2}"
                         + $" CountMax={d.CountMax}"
                         + $" DashAvoid={d.DashAvoid}"
                         + $" hasForm={(d.DashFormParam is not null)}"
                         + $" hasTimeline={(d.DashTimeline is not null)}");
        }
    }

    private static void DumpAssistList(Il2CppSystem.Collections.Generic.List<AssistSelection>? list, string label)
    {
        if (_log is null)
        {
            return;
        }
        if (list is null)
        {
            _log.LogInfo($"[SELPROBE] {label} = null");
            return;
        }
        int n = list.Count;
        _log.LogInfo($"[SELPROBE] {label}: {n} item(s)");
        int limit = Math.Min(n, MaxItemsPerList);
        for (int i = 0; i < limit; i++)
        {
            AssistSelection? a = list[i];
            if (a is null)
            {
                _log.LogInfo($"[SELPROBE]   [{i}] null");
                continue;
            }
            _log.LogInfo($"[SELPROBE]   [{i}] Key={a.Key}"
                         + $" MiddleRange={a.MiddleRange:F2}"
                         + $" LockonMoveSpeed={a.LockonMoveSpeed:F2}"
                         + $" SpeedMagni={a.SpeedMagni:F2}"
                         + $" hasForm={(a.AvoidFormParam is not null)}");
        }
    }

    /// <summary>对只关心数量的列表，报计数即可（结构对本项目暂不关键）。</summary>
    private static void LogCount<T>(Il2CppSystem.Collections.Generic.List<T>? list, string label)
        where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        _log?.LogInfo(list is null
            ? $"[SELPROBE] {label} = null"
            : $"[SELPROBE] {label}: {list.Count} item(s)");
    }

    /// <summary>统计非空参数个数（用于概览一套配置的完整度）。</summary>
    private static string CountNonNull(params AttackParam?[] items)
    {
        int c = 0;
        foreach (AttackParam? p in items)
        {
            if (p is not null)
            {
                c++;
            }
        }
        return $"{c}/5";
    }
}
