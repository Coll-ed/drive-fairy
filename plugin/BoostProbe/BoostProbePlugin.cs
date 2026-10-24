// 加力（疾飞）取证探针 —— 独立插件，**只读 + 可控点火**，不是正式功能。
//
// ═════════════════════════════════════════════════════════════════════════
// 为什么必须有这个探针（证据前置，约束二·4）
// ═════════════════════════════════════════════════════════════════════════
//   用户的 D1 反馈有两条互相矛盾的线索：
//     · 第 159 轮：「按住 shift 后，动画特效出现了」       → 看起来点火成功了
//     · 第 159 轮：「移速没有变化」                        → 看起来点火没成功
//   而前任探针记录 `BoostStatus` **恒为 IDLE_NONBOOST**（43/45 次采样）。
//
//   静态路线已经走到头（我复核过，不是转述）：
//     · Cpp2IL `dll_il_recovery` 产物 `_recovered\Assembly-CSharp.il:31848`
//       的 `PlayerControlCustom::ForceBooston` 体是 `IL_0000: ldnull / IL_0001: throw`
//     · ilspycmd 的 interop 反编译里，该方法只是 `il2cpp_runtime_invoke(token)` 转发
//     ⇒ **静态看不到"点火到底改了什么"，只能运行期观测。**
//
//   而这个观测**不需要人按键**（约束三·6「禁止让人替你产生数据」）：
//   本探针按帧数自己点火、自己关火、自己记数。
//
// ═════════════════════════════════════════════════════════════════════════
// 它能回答的问题
// ═════════════════════════════════════════════════════════════════════════
//   Q1  `ForceBooston()` 调用前后，下列数值有没有变？
//       BoostStatus / Boostpoint / AssistSpeedMagni / LockonMoveSpeed
//       / MoveControlHelper.MoveSpeedMultiplier / CharacterMotor.velocity
//   Q2  `BoostStatus` 是否会被游戏**下帧就复位**回 IDLE_NONBOOST？
//       （采样粒度 = 每 SAMPLES_PER_SECOND 帧一次，不是秒级 → 能抓到快速复位）
//   Q3  位移速度到底变没变？（用**位置差**这个与实现无关的客观量）
//
// ═════════════════════════════════════════════════════════════════════════
// 安全边界（重要）
// ═════════════════════════════════════════════════════════════════════════
//   · **绝不写 `LockonTargetCharacter`**（历史事故：写成 null 会让整个战斗系统崩溃，
//     见 COMBAT_SYSTEM_MAP.md 事故节）。本探针只**读**它。
//   · 只写两个东西：① 调用游戏自己的 `ForceBooston/off`（本来就是这个功能要做的）
//     ② 探针自己的静态计数器。不碰任何游戏状态字段的赋值。
//   · 未开启 `AutoIgniteEnabled` 时本插件**完全不动作**（默认 false）。

using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;   // BasePlugin（IL2CPP 侧基类；Flight 工程同样用它）
using HarmonyLib;
using UnityEngine;

namespace AidealRays.BoostProbe;

/// <summary>
/// 自动点火 + 数值对照取证。见文件头部的完整说明。
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class BoostProbePlugin : BasePlugin
{
    // 插件身份（单一来源：Harmony id 与 BepInEx 元数据共用这两个常量）
    public const string PluginGuid = "com.aidealrays.boostprobe";
    public const string PluginName = "AidealRays Boost Probe";
    public const string PluginVersion = "0.1.0";

    // ---- 时序常量（单位：帧；@60fps，游戏的键位/精灵帧率）----
    /// <summary>
    /// 点火等待窗口：最多等这么多帧，直到观察到"玩家正在推移动键"。
    /// 为什么必须等：没有移动输入时 `Move`/位移都是 0，拿那种数据做前后对照等于自欺。
    /// 300 帧 ≈ 5 秒。
    /// </summary>
    private const int WAIT_FOR_MOVE_INPUT_FRAMES = 300;
    /// <summary>点火后保持多少帧再关火。600 帧 ≈ 10 秒（足够覆盖多段输入）。</summary>
    private const int BOOST_HOLD_FRAMES = 600;
    /// <summary>关火后再观测多少帧（做"关火后"对照）。300 帧 ≈ 5 秒。</summary>
    private const int POST_OFF_FRAMES = 300;
    /// <summary>数值采样粒度（帧）。10 帧 ≈ 6Hz：足以抓住"下帧被复位"。</summary>
    private const int SAMPLE_EVERY_FRAMES = 10;
    /// <summary>位置差测量窗口（帧）。60 帧 ≈ 1 秒。</summary>
    private const int DISP_WINDOW_FRAMES = 60;
    /// <summary>打印小数点后位数（列对齐用）。</summary>
    private const string NUM_FMT = "F3";
    /// <summary>
    /// 目标帧率兜底：`Application.targetFrameRate` 未设置时返回 -1（Unity 语义="平台默认"），
    /// 拿它当除数会得到负速度。取 60 因为本游戏按 60fps 设计。
    /// </summary>
    private const int ASSUMED_FPS_FALLBACK = 60;
    /// <summary>
    /// A/B 对照：在加力窗的**头 N 帧**里每帧都调一次 `ForceBooston()`。
    /// 用途：拿到"每帧调 vs 只调一次"的同条件对照，判定
    /// "点火到底有没有被游戏接受"（若每帧调也不改 BoostStatus，说明该方法在此情境下无效）。
    /// 60 帧 = 1 秒，与前任"每帧叠加"的观测窗口同量级。
    /// </summary>
    private const int EVERY_FRAME_IGNITE_FRAMES = 60;

    private static ManualLogSource _log = null!;
    private static ConfigEntry<bool> _cfgAutoIgnite = null!;

    // ---- 时序状态 ----
    private static long _frames;
    private static int _phase;                     // 0=点火前 1=加力中 2=关火后 3=结束
    private static long _igniteFrame;              // 实际点火的帧号（等输入后才确定）
    private static PlayerControlCustom? _player;
    private static Vector3 _windowStartPos;
    private static bool _hasWindowStart;
    private static bool _reportedMotor;

    // BepInEx 6 IL2CPP 的基类是 BasePlugin，入口覆盖 Load()（不是 MonoBehaviour 的 Awake()）。
    // 与 Flight 工程保持一致，避免两套写法。
    public override void Load()
    {
        _log = Log;
        _cfgAutoIgnite = Config.Bind(
            "Probe",
            "AutoIgniteEnabled",
            false,
            "自动点火取证开关。true = 本探针按帧数自己调用 ForceBooston/ForceBoostoff；"
            + "false = 完全不动作（默认）。取证完成后请置回 false。");

        var harmony = new Harmony(PluginGuid);
        try
        {
            MethodInfo? target = AccessTools.Method(typeof(PlayerControlCustom), "Update");
            if (target is null)
            {
                _log.LogError("[BPROBE] PlayerControlCustom.Update not found - probe disabled");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BoostProbePlugin), nameof(Postfix_Update))));
            _log.LogInfo($"[BPROBE] loaded {PluginVersion}; autoIgnite={_cfgAutoIgnite.Value}"
                         + $" waitForMoveInput={WAIT_FOR_MOVE_INPUT_FRAMES} hold={BOOST_HOLD_FRAMES}"
                         + $" postOff={POST_OFF_FRAMES} sampleEvery={SAMPLE_EVERY_FRAMES}"
                         + $" everyFrameIgnite={EVERY_FRAME_IGNITE_FRAMES}");
        }
        catch (Exception ex)
        {
            // 边界显式：patch 失败必须吵，不许静默
            _log.LogError($"[BPROBE] harmony patch failed: {ex}");
        }
    }

    private static void Postfix_Update(PlayerControlCustom __instance)
    {
        if (__instance is null)
        {
            return;
        }
        _player = __instance;

        // 未开启时完全不动作 —— 默认 false，避免误部署时干扰游戏
        if (!_cfgAutoIgnite.Value)
        {
            return;
        }

        _frames++;
        try
        {
            Tick(__instance);
        }
        catch (Exception ex)
        {
            _log.LogError($"[BPROBE] tick failed at frame {_frames}: {ex}");
        }
    }

    private static void Tick(PlayerControlCustom p)
    {
        // ---- 阶段切换 ----
        // phase 0 → 1：等到"玩家确实在推移动键"再点火（否则数据无意义）。
        // 等不到（WAIT_FOR_MOVE_INPUT_FRAMES 用尽）也点火，但打上 NOT-HELD 标记，
        // 让读日志的人一眼看出这批对照的输入前提不成立。
        if (_phase == 0 && (_frames >= WAIT_FOR_MOVE_INPUT_FRAMES || MoveInputActive(p)))
        {
            _phase = 1;
            _igniteFrame = _frames;
            bool moveHeld = MoveInputActive(p);
            Snapshot(p, "BEFORE-IGNITE");
            _log.LogInfo($"[BPROBE] lockBeforeIgnite="
                         + $"{(p.LockonTargetCharacter is not null ? SafeName(p.LockonTargetCharacter) : "null")}"
                         + $" cameraStatus={SafeRead(() => p.CameraStatus.ToString())}"
                         + $" moveInputActive={moveHeld}"
                         + $"{(moveHeld ? string.Empty : " (WARN: NOT-HELD - comparison is weak)")}");
            p.ForceBooston();
            Snapshot(p, "AFTER-IGNITE-call");
            _log.LogInfo("[BPROBE] === ignite issued (ForceBooston called ONCE) ===");
        }
        else if (_phase == 1)
        {
            // A/B 对照：加力窗头 EVERY_FRAME_IGNITE_FRAMES 帧里每帧再调一次。
            // 目的不是"修功能"，而是判定"这个方法到底接不接受调用"：
            //   · 若每帧调也不改 BoostStatus ⇒ 该方法在当前情境下无效（不是频率问题）
            //   · 若改成了 FORWARD_BOOST 但只调一次时不改 ⇒ 是"需要持续调用"的问题
            if (_frames - _igniteFrame <= EVERY_FRAME_IGNITE_FRAMES)
            {
                p.ForceBooston();
                if (_frames == _igniteFrame + EVERY_FRAME_IGNITE_FRAMES)
                {
                    Snapshot(p, "AFTER-EVERY-FRAME-IGNITE");
                    _log.LogInfo($"[BPROBE] === every-frame ignite段结束"
                                 + $" ({EVERY_FRAME_IGNITE_FRAMES} frames) ===");
                }
            }
            if (_frames >= _igniteFrame + BOOST_HOLD_FRAMES)
            {
                _phase = 2;
                Snapshot(p, "BEFORE-OFF");
                p.ForceBoostoff();
                Snapshot(p, "AFTER-OFF-call");
                _log.LogInfo("[BPROBE] === off issued (ForceBoostoff called ONCE) ===");
            }
        }
        else if (_phase == 2 && _frames >= _igniteFrame + BOOST_HOLD_FRAMES + POST_OFF_FRAMES)
        {
            _phase = 3;
            _log.LogInfo("[BPROBE] === probe finished; set AutoIgniteEnabled=false ===");
        }

        if (_phase >= 3)
        {
            return;
        }

        // ---- 周期采样（按帧粒度，能看到"下帧复位"）----
        if (_frames % SAMPLE_EVERY_FRAMES == 0)
        {
            Snapshot(p, _phase switch { 0 => "pre", 1 => "boost", _ => "post" });
        }

        // ---- 位置差测量（与实现无关的客观速度指标）----
        Vector3 pos = p.transform.position;
        if (!_hasWindowStart)
        {
            _windowStartPos = pos;
            _hasWindowStart = true;
        }
        else if (_frames % DISP_WINDOW_FRAMES == 0)
        {
            Vector3 d = pos - _windowStartPos;
            float dt = DISP_WINDOW_FRAMES / (float)EffectiveFps();
            _log.LogInfo($"[BPROBE][DISP] phase={_phase} frames={_frames}"
                         + $" dPos=({d.x.ToString(NUM_FMT)},{d.y.ToString(NUM_FMT)},{d.z.ToString(NUM_FMT)})"
                         + $" |dHoriz|={new Vector2(d.x, d.z).magnitude.ToString(NUM_FMT)}"
                         + $" approxSpeed={(dt > 0f ? (new Vector2(d.x, d.z).magnitude / dt).ToString(NUM_FMT) : "n/a")}"
                         + $" targetFps={Application.targetFrameRate}");
            _windowStartPos = pos;
        }
    }

    /// <summary>`Application.targetFrameRate` 的兜底（-1 表示"平台默认"）。</summary>
    private static int EffectiveFps()
    {
        int fps = Application.targetFrameRate;
        return fps > 0 ? fps : ASSUMED_FPS_FALLBACK;
    }

    /// <summary>
    /// 玩家这一帧是否真的在推移动键（只读 `Move` 的水平分量）。
    /// 用它当"可以开始对照"的门槛：没有输入时位移恒为 0，前后对照没有信息量。
    /// </summary>
    private static bool MoveInputActive(PlayerControlCustom p)
    {
        try
        {
            Vector3 mv = p.Move;
            return new Vector2(mv.x, mv.z).sqrMagnitude > MOVE_INPUT_EPSILON;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>判定"有移动输入"的平方阈值（0.01 ⇒ 模长 0.1）。</summary>
    private const float MOVE_INPUT_EPSILON = 0.01f;

    /// <summary>
    /// 采样并打印一行：加力状态机 + 所有候选"速度载体"。纯读。
    /// </summary>
    private static void Snapshot(PlayerControlCustom p, string tag)
    {
        // 加力状态机与燃料
        string boostStatus = SafeRead(() => p.BoostStatus.ToString());
        string boostPoint = SafeRead(() => $"{p.Boostpoint}/{p.BoostpointMax}(min={p.BoostpointMin})");
        string assistMagni = SafeRead(() => p.AssistSpeedMagni.ToString(NUM_FMT));
        string lockonMove = SafeRead(() => p.LockonMoveSpeed.ToString(NUM_FMT));
        string lockTarget = SafeRead(() => p.LockonTargetCharacter is not null
            ? SafeName(p.LockonTargetCharacter) : "null");
        string move = SafeRead(() => Fmt(p.Move));

        // MoveControlHelper：前任转储结论说它的 MoveSpeedMultiplier 对应"移速没变化"
        string helper = ReadMoveHelper(p);
        // CharacterMotor：Standard Assets 运动学层，velocity 是"实际速度载体"
        string motor = ReadCharacterMotor(p);

        _log.LogInfo($"[BPROBE][{tag}] f={_frames}"
                     + $" status={boostStatus} point={boostPoint} assistMagni={assistMagni}"
                     + $" lockonMove={lockonMove} lockTarget={lockTarget} Move={move}"
                     + $" || helper: {helper} || motor: {motor}");

        if (!_reportedMotor && motor != "n/a")
        {
            _reportedMotor = true;   // 只报一次，避免刷屏
        }
    }

    /// <summary>读 MoveControlHelper 的私有/受保护数值（前任指认的速度载体）。</summary>
    private static string ReadMoveHelper(PlayerControlCustom p)
    {
        try
        {
            object? helper = p.MoveHelper;
            if (helper is null)
            {
                return "MoveHelper=null";
            }
            Type t = helper.GetType();
            const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.Public
                                       | BindingFlags.NonPublic;
            string speedMultiplier = Field(t, helper, "MoveSpeedMultiplier", FLAGS);
            string turnAmount = Field(t, helper, "TurnAmount", FLAGS);
            string forwardAmount = Field(t, helper, "ForwardAmount", FLAGS);
            string moveVector = Field(t, helper, "MoveVector", FLAGS);
            return $"MoveSpeedMultiplier={speedMultiplier} TurnAmount={turnAmount}"
                   + $" ForwardAmount={forwardAmount} MoveVector={moveVector}";
        }
        catch (Exception ex)
        {
            return $"err({ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>
    /// 读 CharacterMotor（Standard Assets 运动学层）。
    /// 用反射而非强类型引用：那套类型来自复制进来的 Standard Assets，
    /// 反射能避免为一个探针去改公共引用集（改动最小，约束五·5）。
    /// 查找范围含**子物体**（实测：`GetComponents` 在玩家根物体上找不到它，
    /// 组件可能挂在模型的子节点上）。
    /// </summary>
    private static string ReadCharacterMotor(PlayerControlCustom p)
    {
        try
        {
            // 先把实际挂在玩家层级上的组件名报一次 —— 没有这份清单就只能瞎猜
            if (!_reportedMotorList)
            {
                _reportedMotorList = true;
                MonoBehaviour[] all = p.GetComponentsInChildren<MonoBehaviour>(true);
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour? c = all[i];
                    if (c is not null && names.Count < MAX_COMPONENT_NAMES_LOGGED)
                    {
                        names.Add(c.GetType().Name);
                    }
                }
                _log.LogInfo($"[BPROBE] player components ({all.Length}):"
                             + $" {string.Join(", ", names)}");
            }

            MonoBehaviour[] comps = p.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                MonoBehaviour? c = comps[i];
                if (c is null)
                {
                    continue;
                }
                Type ct = c.GetType();
                if (ct.Name != "CharacterMotor")
                {
                    continue;
                }
                const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.Public
                                           | BindingFlags.NonPublic;
                object? movement = FindField(ct, "movement")?.GetValue(c);
                if (movement is null)
                {
                    return "CharacterMotor present but movement=null";
                }
                Type mt = movement.GetType();
                string velocity = Field(mt, movement, "velocity", FLAGS);
                string maxFwd = Field(mt, movement, "maxForwardSpeed", FLAGS);
                string maxSide = Field(mt, movement, "maxSidewaysSpeed", FLAGS);
                string maxBack = Field(mt, movement, "maxBackwardsSpeed", FLAGS);
                string inputDir = Field(ct, c, "inputMoveDirection", FLAGS);
                return $"velocity={velocity} maxFwd={maxFwd} maxSide={maxSide}"
                       + $" maxBack={maxBack} inputMoveDir={inputDir}";
            }
            return "n/a";
        }
        catch (Exception ex)
        {
            return $"err({ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>
    /// 沿继承链找字段。
    /// 实测踩到的坑：`MoveControlHelper.MoveSpeedMultiplier` 是 **protected**，
    /// 而 `TurnAmount`/`ForwardAmount` 是 public —— 直接 `GetField(name, Instance|Public)`
    /// 取不到受保护字段 ⇒ 必须用 `Instance|Public|NonPublic` 且逐层向上找。
    /// </summary>
    private static FieldInfo? FindField(Type t, string name)
    {
        const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.Public
                                   | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (Type? cur = t; cur is not null; cur = cur.BaseType)
        {
            FieldInfo? f = cur.GetField(name, FLAGS);
            if (f is not null)
            {
                return f;
            }
        }
        return null;
    }

    /// <summary>玩家层级组件名清单的打印上限（避免日志被上百个组件淹没）。</summary>
    private const int MAX_COMPONENT_NAMES_LOGGED = 40;

    private static bool _reportedMotorList;

    private static string Field(Type t, object target, string name, BindingFlags flags)
    {
        FieldInfo? f = FindField(t, name) ?? t.GetField(name, flags);
        if (f is null)
        {
            return "n/a";
        }
        object? v = f.GetValue(target);
        return v switch
        {
            null => "null",
            float fl => fl.ToString(NUM_FMT),
            Vector3 vec => Fmt(vec),
            _ => v.ToString() ?? "?",
        };
    }

    private static string Fmt(Vector3 v)
        => $"({v.x.ToString(NUM_FMT)},{v.y.ToString(NUM_FMT)},{v.z.ToString(NUM_FMT)})";

    /// <summary>把"可能抛异常的读取"收敛到一处，出错时返回错误串而不是崩掉采样。</summary>
    private static string SafeRead(Func<string> get)
    {
        try
        {
            return get();
        }
        catch (Exception ex)
        {
            return $"err({ex.GetType().Name})";
        }
    }

    private static string SafeName(CharacterControllerBase c)
    {
        try
        {
            return c.transform is not null ? c.transform.name : "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
