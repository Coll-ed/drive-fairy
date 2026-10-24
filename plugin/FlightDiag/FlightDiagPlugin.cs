// AldealRays 3D 飞行改造 · 阶段 1：只读诊断插件
//
// 目的：在**不改任何行为**的前提下，把游戏运行时的移动状态打进 BepInEx 日志，
//      用于确认「输入 → Move 向量」的真实映射关系，再决定怎么注入垂直轴。
//
// 为什么先只读：IL2CPP 的 interop 方法体是**桩**（只是 il2cpp_runtime_invoke 转发），
//      看不到原生实现。所以只能靠运行时观测来确定字段语义，不能靠猜。
//
// 观测点（均来自 ilspycmd 反编译的真实签名）：
//   · PlayerControlCustom.EvaluateMoveAndAttack(float horizon, float vertical, ...)
//     —— 玩家输入入口，horizon/vertical 就是两维输入
//   · PlayerControlCustom.Move  —— Vector3 属性，移动向量（真三维）
//   · PlayerControlCustom.MoveControl() —— 每帧移动计算
//
// 安全：第一阶段**只写日志，不修改任何值**。确认观测有效后再进第二阶段。

using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace AldealRays.FlightDiag;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class FlightDiagPlugin : BasePlugin
{
    // 插件标识（BepInEx 要求；改动需同步更新依赖声明）
    public const string PluginGuid = "com.aidealrays.flightdiag";
    public const string PluginName = "AldealRays Flight Diagnostic";
    public const string PluginVersion = "0.1.0";

    // 日志节流：MoveControl 每帧都调，全量打印会淹没日志。
    // 值取自经验：60fps 下每 30 帧约 0.5 秒一次，足够观察趋势又不刷屏。
    private const int LOG_INTERVAL_FRAMES = 30;

    // 输入变化阈值：小于此值视为没变，不重复打印（避免浮点噪声刷屏）
    private const float INPUT_EPSILON = 0.01f;

    internal static ManualLogSource Log = null!;
    internal static int FrameCounter;
    internal static float LastHorizon = float.NaN;
    internal static float LastVertical = float.NaN;

    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"{PluginName} v{PluginVersion} 载入中……");

        // Harmony 打补丁。IL2CPP 下用 Il2CppInterop 生成的类型，方法名与原生一致。
        var harmony = new Harmony(PluginGuid);
        int patched = 0;

        patched += TryPatch(harmony,
            "PlayerControlCustom", "EvaluateMoveAndAttack",
            nameof(Postfix_EvaluateMoveAndAttack), isPostfix: false);

        patched += TryPatch(harmony,
            "PlayerControlCustom", "MoveControl",
            nameof(Postfix_MoveControl), isPostfix: true);

        Log.LogInfo($"补丁结果：成功 {patched} 处。");
        if (patched == 0)
        {
            Log.LogError("一处补丁都没打上 —— 类型名或方法名可能不对，请回看反编译结果。");
        }
        else
        {
            Log.LogInfo("进入游戏战斗场景后，本日志会输出 Move 向量与输入值。");
        }
    }

    /// <summary>
    /// 安全打补丁：目标类型/方法不存在时只记日志，不让插件整体崩掉。
    /// 返回 1 表示成功，0 表示失败。
    /// </summary>
    private static int TryPatch(Harmony harmony, string typeName, string methodName,
                                string patchMethodName, bool isPostfix)
    {
        try
        {
            // IL2CPP 类型需从 interop 程序集按全名取
            var type = System.Type.GetType(typeName)
                       ?? System.Linq.Enumerable.FirstOrDefault(
                              System.AppDomain.CurrentDomain.GetAssemblies(),
                              a => a.GetType(typeName) != null)?.GetType(typeName);
            if (type is null)
            {
                Log.LogWarning($"找不到类型 {typeName}，跳过。");
                return 0;
            }

            var target = AccessTools.Method(type, methodName);
            if (target is null)
            {
                Log.LogWarning($"类型 {typeName} 里找不到方法 {methodName}，跳过。");
                return 0;
            }

            var patch = AccessTools.Method(typeof(FlightDiagPlugin), patchMethodName);
            harmony.Patch(target,
                prefix: isPostfix ? null : new HarmonyMethod(patch),
                postfix: isPostfix ? new HarmonyMethod(patch) : null);
            Log.LogInfo($"已打补丁：{typeName}.{methodName} ({(isPostfix ? "postfix" : "prefix")})");
            return 1;
        }
        catch (System.Exception ex)
        {
            // 显式记录失败原因，不静默吞掉
            Log.LogError($"打补丁 {typeName}.{methodName} 抛异常：{ex}");
            return 0;
        }
    }

    /// <summary>
    /// 输入入口 prefix：记录游戏收到的 horizon / vertical。
    /// 这是判断"垂直轴是否存在"的第一手证据。
    /// </summary>
    private static void Postfix_EvaluateMoveAndAttack(float horizon, float vertical)
    {
        bool changed = float.IsNaN(LastHorizon)
                       || System.Math.Abs(horizon - LastHorizon) > INPUT_EPSILON
                       || System.Math.Abs(vertical - LastVertical) > INPUT_EPSILON;
        if (!changed)
        {
            return;
        }
        LastHorizon = horizon;
        LastVertical = vertical;
        Log.LogInfo($"[输入] horizon={horizon:F3}  vertical={vertical:F3}");
    }

    /// <summary>
    /// MoveControl postfix：节流打印当前的 Move 向量。
    /// Move 是 Vector3 —— 观察它的 z 分量是否随 vertical 变化，即可确定映射关系。
    /// </summary>
    private static void Postfix_MoveControl(PlayerControlCustom __instance)
    {
        if (__instance is null)
        {
            return;
        }
        if (++FrameCounter % LOG_INTERVAL_FRAMES != 0)
        {
            return;
        }
        try
        {
            Vector3 mv = __instance.Move;
            Log.LogInfo($"[Move] ({mv.x:F3}, {mv.y:F3}, {mv.z:F3})  输入 h={LastHorizon:F3} v={LastVertical:F3}");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"读取 Move 失败：{ex.Message}");
        }
    }
}
