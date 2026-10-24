// 驱动妖精 AI-deal-Rays · 移动方向追踪器（纯观测，不改任何游戏状态）
//
// 目的：反推出"输入 -> Move -> 世界位移"这条管线的真实映射关系。
//
// 背景（为什么要做这个）：
//   我想实现"朝相机方向移动"，但直接按推导公式改写 Move 上线后**方向全乱**
//   （事故日志：in=(1.000,0.000) -> 我算出 Move=(-1.000,0.000)，
//    而原版实测是 Move(x=-0.156, z=0.988)）。说明 Move 并非与输入同构，
//   中间还有额外变换（很可能是朝向/锁定坐标系投影）。
//   公式推导错误的原因就是**没有观测中间量**，只凭语义猜。
//
// 本追踪器的做法：
//   每帧记录 Move 与角色世界坐标，按 1 秒窗口累计**实际位移方向**，
//   然后输出三者对照：
//       输入(h,v) | Move 方向 | 实际位移方向 | 相机朝向
//   有了对照关系，映射就变成读出来的，而不是猜出来的。
//
// 安全性：只读 + 只写日志。不改任何游戏字段。
//   （这是上次事故的直接教训：任何写操作上线前，必须先有观测数据支撑。）

using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

// 命名空间与类名刻意不同名：若都叫 MoveTrace，FlightPlugin 里的
// MoveTrace.Init(...) 会被解析成"命名空间 MoveTrace 下的 Init"而编译失败。
namespace AidealRays.MoveProbe;

/// <summary>
/// 移动管线追踪器。由 FlightPlugin 在每帧调用 Observe()，
/// 自身不做任何 Harmony 挂钩，因此不会引入新的挂载点风险。
/// </summary>
public static class MoveTrace
{
    // 采样窗口（秒）：1 秒足以让角色走出可测距离，又不至于把不同方向的输入混在一起。
    // 取 1 秒的理由：正常移动速度下 1 秒位移约 3~8 单位，远大于噪声。
    private const float WindowSeconds = 1f;
    // 判定"方向有效"的最小位移（Unity 单位）：低于此值视为原地不动，不参与方向统计
    private const float MinDisplacement = 0.05f;
    // 判定"输入有效"的阈值：低于此值视为没推杆，不参与统计
    private const float MinInput = 0.1f;
    private const string NumFmt = "F3";

    private static bool _enabled;
    private static ManualLogSource? _log;
    private static ConfigEntry<bool>? _cfg;

    private static float _windowStart;
    private static Vector3 _windowStartPos;
    private static Vector3 _lastPos;
    private static bool _hasLastPos;

    // 窗口内累计的 move 方向（用于求平均方向）
    private static Vector3 _moveAccum;
    private static Vector3 _inputAccum;
    private static int _sampleCount;

    // ★ 加力诊断（用户实测：「维持 W 的姿势，ASD 实际是 W 旋转」）
    //   需要证明："输入方向"与"角色朝向"的关系在加力时是否被锁死。
    private static Vector3 _facingAccum;          // 角色朝向（水平）累计
    private static int _facingSamples;
    private static float _inputVsFacingAccum;     // input 与 facing 的夹角累计（度）
    private static int _angleSamples;

    /// <summary>初始化。由 FlightPlugin.Load 调用。</summary>
    public static void Init(ManualLogSource log, ConfigEntry<bool> cfg)
    {
        _log = log;
        _cfg = cfg;
        _enabled = cfg.Value;
    }

    /// <summary>每帧观测一次。只读，不修改任何游戏状态。</summary>
    public static void Observe(PlayerControlCustom player, float horizon, float vertical)
    {
        if (_log is null || _cfg is null)
        {
            return;
        }
        // 配置可热改（用户能在游戏运行中改 cfg 文件，但 BepInEx 不会自动重载；
        // 这里每帧读一次值，代价极低且能让改配置后立刻生效）
        bool on = _cfg.Value;
        if (!on)
        {
            _enabled = false;
            return;
        }
        _enabled = true;

        try
        {
            Vector3 pos = player.transform.position;
            if (!_hasLastPos)
            {
                _lastPos = pos;
                _windowStartPos = pos;
                _windowStart = Time.realtimeSinceStartup;
                _hasLastPos = true;
                return;
            }

            // 累计本帧的 Move 与输入（只统计有输入 + 有水平移动意图的帧）
            float inputMag = new Vector2(horizon, vertical).magnitude;
            Vector3 mv = player.Move;
            float moveMag = new Vector2(mv.x, mv.z).magnitude;
            if (inputMag > MinInput && moveMag > MinInput)
            {
                _moveAccum += new Vector3(mv.x, 0f, mv.z);
                _inputAccum += new Vector3(horizon, 0f, vertical);
                _sampleCount++;

                // ★ 加力诊断（用户实测问题）：加力状态下
                //   用户原话：「维持 W 的姿势，ASD 实际是 W 旋转」
                //   ⇒ 需要证明："输入方向"与"角色朝向"的关系在加力时是否变了。
                //   这里累计两件东西：
                //     ① 角色朝向（水平）
                //     ② input 与 facing 的夹角 —— 若加力时夹角≈0（永远朝前），
                //        而输入指向别处，就证实了"朝向被锁在 W"
                try
                {
                    Vector3 fwd = player.transform.forward;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 1e-6f)
                    {
                        _facingAccum += fwd.normalized;
                        _facingSamples++;

                        Vector3 inDir = new Vector3(horizon, 0f, vertical);
                        if (inDir.sqrMagnitude > 1e-6f)
                        {
                            float ang = Vector3.Angle(inDir.normalized, fwd.normalized);
                            _inputVsFacingAccum += ang;
                            _angleSamples++;
                        }
                    }
                }
                catch (Exception)
                {
                    // 朝向读不到就不统计这一项，不影响其余诊断
                }
            }

            _lastPos = pos;

            // 窗口到期：输出一次统计
            if (Time.realtimeSinceStartup - _windowStart < WindowSeconds)
            {
                return;
            }

            Vector3 disp = pos - _windowStartPos;
            Vector3 dispH = new Vector3(disp.x, 0f, disp.z);

            string inputDir = _sampleCount > 0 ? Fmt(Flat(_inputAccum)) : "n/a";
            string moveDir = _sampleCount > 0 ? Fmt(Flat(_moveAccum)) : "n/a";

            // ★ 角色朝向（加力诊断）—— 加力时若它与 inputDir 无关，就证实"朝向被锁"
            string facingDir = _facingSamples > 0 ? Fmt(Flat(_facingAccum)) : "n/a";
            string angDeg = _angleSamples > 0
                ? (_inputVsFacingAccum / _angleSamples).ToString(NumFmt)
                : "n/a";
            string dispDir = dispH.magnitude > MinDisplacement ? Fmt(dispH.normalized) : "too-small";
            string camDir = "n/a";
            string camFwdY = "n/a";
            try
            {
                Camera? cam = Camera.main;
                if (cam is not null)
                {
                    Vector3 f = cam.transform.forward;
                    camFwdY = f.y.ToString(NumFmt);
                    f.y = 0f;
                    camDir = f.sqrMagnitude > 1e-6f ? Fmt(f.normalized) : "degenerate";
                }
            }
            catch (Exception ex)
            {
                camDir = $"err({ex.Message})";
            }

            _log.LogInfo($"[TRACE] inputDir={inputDir} moveDir={moveDir}"
                         + $" dispDir={dispDir} dispLen={dispH.magnitude.ToString(NumFmt)}"
                         + $" samples={_sampleCount}"
                         + $" | camFwdDir={camDir} camFwdY={camFwdY}"
                         + $" | facingDir={facingDir} inputVsFacing={angDeg}deg"
                         + "  <-- 加力时若 facingDir 恒为同一方向"
                         + "且 angDeg 随输入增大，即证实'朝向被锁在W'");

            // 重置窗口
            _windowStart = Time.realtimeSinceStartup;
            _windowStartPos = pos;
            _moveAccum = Vector3.zero;
            _inputAccum = Vector3.zero;
            _sampleCount = 0;
            _facingAccum = Vector3.zero;
            _facingSamples = 0;
            _inputVsFacingAccum = 0f;
            _angleSamples = 0;
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"move trace failed: {ex.Message}");
        }
    }

    private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    private static string Fmt(Vector3 v)
        => $"({v.x.ToString(NumFmt)},{v.z.ToString(NumFmt)})";
}
