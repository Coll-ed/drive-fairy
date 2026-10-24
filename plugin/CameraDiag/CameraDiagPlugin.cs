// 驱动妖精 AI-deal-Rays · 相机锁定 Z 轴诊断
//
// 目的：在**不改任何行为**的前提下，量化"相机实际看向哪里"与"敌人身体在哪里"的偏差，
//      用于确定高度差下相机偏移的根因，再决定改什么。
//
// 背景（反编译所得的可用接口）：
//   LookTargetController（相机看向控制器）
//     · Transform Target        —— 看向的目标
//     · float     LookStep      —— 转向步长
//     · GRAVITY   Gravity       —— 重力轴（暗示瞄准点含 Y 概念）
//     · Quaternion RotationOrg  —— 初始朝向
//     · void LookTarget()       —— 执行看向（可作 patch 目标）
//   CharacterControllerBase（角色基类）
//     · float MyCenterOffset    —— 自身中心偏移（瞄身体而非脚底）
//     · float TargetOffset      —— 目标偏移
//     · float LookStep / LookSpeed
//     · bool  IsLookLock
//     · CharacterControllerBase LockonTargetCharacter —— 当前锁定目标
//
// 输出格式（便于对照测试清单）：
//   [CAM] tgtY=...  camY=...  aimY=...  dY=...  ...  case=P0
//     tgtY = 锁定目标物体的 Y 坐标
//     camY = 相机自身 Y 坐标
//     aimY = 相机前向视线在"目标所在 XZ 平面"上的 Y 坐标 —— 即相机究竟看向多高
//     dY   = aimY - tgtY，偏差量；**这个值就是问题的量化指标**
//     off  = MyCenterOffset / TargetOffset 的当前值
//
// 安全：本阶段只读 + 只写日志，不设置任何游戏字段。

using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace AidealRays.CamDiag;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class CameraDiagPlugin : BasePlugin
{
    public const string PluginGuid = "com.aidealrays.camdiag";
    public const string PluginName = "AidealRays Camera Lock Z-Diag";
    public const string PluginVersion = "0.1.0";

    // ---- 采样参数（具名 + 理由）----
    // 每 20 帧采样一次：60fps 下约 0.33 秒，足够捕捉冲刺蓄力这类瞬态
    private const int SAMPLE_INTERVAL_FRAMES = 20;
    // 视线延伸距离：取足够远以保证与目标 XZ 平面相交（战斗距离通常 < 100）
    private const float AIM_RAY_LENGTH = 500f;
    // 归零阈值：小于此值认为"方向向量无效"，避免除零
    private const float DIR_EPSILON = 1e-4f;
    private const string NUM_FMT = "F2";

    internal static new ManualLogSource Log = null!;
    private static ConfigEntry<string> _cfgCase = null!;

    private static int _frameCounter;
    private static int _patchCount;

    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"{PluginName} v{PluginVersion} loading (read-only diagnostic)");

        // 测试用例编号：玩家改这个值来标注当前在测哪一项，日志会带上该编号
        _cfgCase = Config.Bind(
            "Diag", "CaseLabel", "P0",
            "Free-form label echoed into every log line. Change it while testing " +
            "so each scenario is identifiable in the log (e.g. P0/P1/P2...).");

        Log.LogInfo($"case label = {_cfgCase.Value}");

        var harmony = new Harmony(PluginGuid);
        _patchCount = 0;

        // 主挂载点：LookTargetController.Update —— 相机瞄准的每帧更新
        _patchCount += Patch(harmony, typeof(LookTargetController), "Update", nameof(Postfix_LookTargetUpdate));
        // 备用挂载点：LookTarget() —— 实际执行转向的方法
        _patchCount += Patch(harmony, typeof(LookTargetController), "LookTarget", nameof(Postfix_LookTargetFn));

        Log.LogInfo($"patched {_patchCount}/2");
        if (_patchCount == 0)
        {
            Log.LogError("no patch applied - camera diagnostic is inert");
        }
        else
        {
            Log.LogInfo("Enter battle and lock on; camera aim vs target position will be logged.");
        }
    }

    private static int Patch(Harmony harmony, Type type, string method, string patchName)
    {
        try
        {
            var target = AccessTools.Method(type, method);
            if (target is null)
            {
                Log.LogError($"method not found: {type.Name}.{method}");
                return 0;
            }
            harmony.Patch(target,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CameraDiagPlugin), patchName)));
            Log.LogInfo($"patched(postfix): {type.Name}.{method}");
            return 1;
        }
        catch (Exception ex)
        {
            // 显式记录失败原因，不静默吞掉
            Log.LogError($"patch failed {type.Name}.{method}: {ex}");
            return 0;
        }
    }

    private static void Postfix_LookTargetUpdate(LookTargetController __instance)
        => Report(__instance, "Update");

    private static void Postfix_LookTargetFn(LookTargetController __instance)
        => Report(__instance, "LookTarget");

    /// <summary>采样并输出一份相机瞄准偏差报告。</summary>
    private static void Report(LookTargetController cam, string from)
    {
        if (cam is null || ++_frameCounter % SAMPLE_INTERVAL_FRAMES != 0)
        {
            return;
        }
        try
        {
            // ① 相机自身
            Transform camT = cam.transform;
            Vector3 camPos = camT.position;
            Vector3 camFwd = camT.forward;

            // ② cam.Target 指向谁、在哪
            string targetInfo = "none";
            float tgtY = float.NaN;
            Transform? tgt = null;
            try
            {
                tgt = cam.Target;
                if (tgt is not null)
                {
                    Vector3 tp = tgt.position;
                    tgtY = tp.y;
                    targetInfo = $"{tgt.name}@{tp.x.ToString(NUM_FMT)},{tp.y.ToString(NUM_FMT)},{tp.z.ToString(NUM_FMT)}";
                }
            }
            catch (Exception ex)
            {
                targetInfo = $"err({ex.Message})";
            }

            // ③ 相机前向视线与"目标 XZ 平面"的交点高度 —— 即相机究竟看向多高
            float aimY = float.NaN;
            float dY = float.NaN;
            if (tgt is not null && Math.Abs(camFwd.y) > DIR_EPSILON)
            {
                Vector3 tp = tgt.position;
                // 沿 camFwd 走到 tp 所在的 XZ 平面，需要多长
                Vector3 toTgt = tp - camPos;
                float horiz = new Vector2(toTgt.x, toTgt.z).magnitude;
                float horizDir = new Vector2(camFwd.x, camFwd.z).magnitude;
                if (horizDir > DIR_EPSILON)
                {
                    float travel = horiz / horizDir;      // 水平方向走完这段距离所需的射线长度
                    if (travel <= AIM_RAY_LENGTH)
                    {
                        aimY = camPos.y + camFwd.y * travel;
                        dY = aimY - tp.y;
                    }
                }
            }

            // ④ 角色的瞄准相关偏移量（读锁定目标的，因为"瞄身体中心"取决于被瞄对象）
            string offInfo = "off=n/a";
            try
            {
                var tgtChar = cam.Target is null
                    ? null
                    : cam.Target.GetComponent<CharacterControllerBase>();
                if (tgtChar is not null)
                {
                    offInfo = $"tgtChar(center={tgtChar.MyCenterOffset.ToString(NUM_FMT)}"
                              + $" off={tgtChar.TargetOffset.ToString(NUM_FMT)}"
                              + $" lock={(tgtChar.IsLookLock ? 1 : 0)})";
                }
            }
            catch (Exception ex)
            {
                offInfo = $"off=err({ex.Message})";
            }

            // ⑤ 相机自身的控制参数
            string camInfo;
            try
            {
                camInfo = $"step={cam.LookStep.ToString(NUM_FMT)} grav={cam.Gravity}";
            }
            catch (Exception ex)
            {
                camInfo = $"step=err({ex.Message})";
            }

            Log.LogInfo($"[CAM] case={_cfgCase.Value} via={from}"
                        + $" | camY={camPos.y.ToString(NUM_FMT)} fwdY={camFwd.y.ToString(NUM_FMT)}"
                        + $" | aimY={aimY.ToString(NUM_FMT)}"
                        + $" | dY={dY.ToString(NUM_FMT)}"
                        + $" | {offInfo} | {camInfo}"
                        + $" | target={targetInfo}");
        }
        catch (Exception ex)
        {
            Log.LogError($"report failed: {ex}");
        }
    }
}
