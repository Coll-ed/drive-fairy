// 驱动妖精 AI-deal-Rays · 虚拟瞄准锚点（第三人称脱离准心的核心构件）
//
// ── 这是什么 ────────────────────────────────────────────────────────────
//   一个**隐形、无碰撞、不参与渲染**的对象，位置由"爱迪尔 + 鼠标角度 + 距离"算出。
//   它的存在意义：原版整套瞄准系统（准心 / 身体朝向 / 接近点 / 冲刺点 / 大招突进）
//   都归结为**读一个 Transform**。把这个 Transform 换成"我们造的锚点"，
//   下游就会自动跟随 —— 这是唯一能保证"准心/朝向/攻击三者一致"的改法。
//
// ── 用户定稿的锚点模型（轨道式）─────────────────────────────────────────
//     锚点角度  ← 鼠标控制（绕爱迪尔旋转），与相机朝向共用同一套角度状态
//     锚点原点  ← 爱迪尔位置（她移动，锚点平移）
//     锚点位置  = 爱迪尔位置 + 方向(yaw,pitch) × Distance
//
//   为什么不直接读相机位置：相机有阻尼/缩放，会摆动 ⇒ 锚点会抖。
//   用户方案把"角度来源"(鼠标) 与"位置来源"(爱迪尔) 解耦，不经过相机 ⇒ 无累积误差。
//
//   参考实现是游戏自己的场景鉴赏自由相机 CameraHolder（实测确认存在）：
//     public float currDistance / xRotate / yRotate / yMinLimit / yMaxLimit
//   以及 CameraOrbit 的 lerpSpeed（平滑）。本模块照此模型实现。
//
// ── 本阶段的边界（重要）────────────────────────────────────────────────
//   **阶段 2：只造点 + 写日志，不接入任何游戏系统。**
//   不碰 LookTargetController / LockonCursol / SetApproachPoint / LockonTargetCharacter。
//   验收标准：日志能证明锚点位置正确、且游戏零副作用。
//   接入是阶段 3~7 的事（见 DESIGN_FREE_AIM.md 的阶段表）。
//
// ── 安全性 ──────────────────────────────────────────────────────────────
//   · 只创建自己的对象、只写自己的 transform、只写日志
//   · 不读也不写任何战斗状态字段
//   · 可随时销毁，不留垃圾

using System;
using BepInEx.Logging;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 虚拟瞄准锚点：轨道式跟随爱迪尔的隐形点，代表"当前瞄准方向"。
/// </summary>
internal sealed class VirtualAimAnchor
{
    // 对象名（便于在场景/日志里识别）
    public const string ObjectName = "AidealRays_AimAnchor";

    // 平滑速度下限：低于此值视为"不平滑"（避免除零 / 无意义计算）
    private const float MinLerpSpeed = 0.0001f;
    // 位置日志节流（帧）
    private const int LogEveryFrames = 120;

    private readonly ManualLogSource _log;
    // 平滑速度：把锚点从上一帧位置插值到目标位置的速率。
    // 参考游戏 CameraOrbit.lerpSpeed 的用法；取值理由：过大等于不平滑（锚点瞬移），
    // 过小会让瞄准迟钝。这里只做轻度平滑，主要用于抵消逐帧数值噪声。
    private readonly float _lerpSpeed;
    private readonly float _minPitch;
    private readonly float _maxPitch;

    private GameObject? _go;
    private CharacterControllerBase? _ccb;
    private Vector3 _smoothedPos;
    private bool _hasSmoothed;
    private int _frameCounter;
    private bool _loggedFirst;

    // ---- 锚点自己的方向状态（★ 必须独立，不能读相机/身体）----
    // 为什么独立：一旦"锁定目标 = 锚点"生效，链路变成
    //     相机朝向 → 锚点 → 锁定目标 → 身体朝向 → 相机朝向
    // 这是一个**闭环**。若锚点还去读相机朝向，就会自我循环，
    // 表现为锚点在角色头顶乱转（实测：dir 每两秒跳一次、dy 在 6~7 之间摆动）。
    // 因此锚点由【鼠标输入】直接驱动，成为链条的**唯一源头**：
    //     鼠标 → 锚点 → 锁定目标 → 身体/相机
    // 单向下游，无环。
    private float _yaw;
    private float _pitch;
    private bool _hasLastMouse;
    private Vector2 _lastMousePos;

    // 鼠标灵敏度（度/像素）。与 FreeFlightCamera 的默认值同源，避免两套手感。
    private readonly float _mouseSensitivity;

    /// <summary>
    /// 锚点是否同时具备 CharacterControllerBase 实例。
    /// 这决定能否走"锁定目标 = 锚点"的设计（用户定稿）。
    /// </summary>
    public bool HasControllerInstance => _ccb is not null;

    /// <summary>
    /// 锚点的 CharacterControllerBase 实例 —— 供"把锁定目标固定到锚点"使用。
    /// 用户定稿：「把锁定固定到锚点那里去，然后锚点又是一个属于我们的可控准心，
    /// 所以就做到了让锁定成为我们的准心」。
    /// </summary>
    public CharacterControllerBase? Controller => _ccb;

    public VirtualAimAnchor(ManualLogSource log, float lerpSpeed, float minPitch, float maxPitch,
                            float mouseSensitivity)
    {
        _log = log;
        _lerpSpeed = Math.Max(lerpSpeed, MinLerpSpeed);
        _minPitch = minPitch;
        _maxPitch = maxPitch;
        _mouseSensitivity = mouseSensitivity;
    }

    /// <summary>当前瞄准方向的水平角（度）——供诊断与后续阶段读取。</summary>
    public float AimYaw => _yaw;

    /// <summary>当前瞄准方向的俯仰角（度）。</summary>
    public float AimPitch => _pitch;

    /// <summary>锚点是否已创建。</summary>
    public bool Exists => _go is not null;

    /// <summary>锚点 transform（供后续阶段接入用；阶段 2 不对外使用）。</summary>
    public Transform? Transform => _go is not null ? _go.transform : null;

    /// <summary>
    /// 给锚点补上"最小合法角色状态"。
    ///
    /// ── 为什么必须做（实测事故）────────────────────────────────────────────
    ///   把锁定目标设成锚点后，游戏每帧会调用
    ///     `BattleManager.ShowWeakInfo(CharacterControllerBase character)`
    ///   对"锁定目标"显示弱点信息。而锚点是从零 `AddComponent` 出来的**空壳**，
    ///   大量引用类型字段仍是 **null** ⇒ 每帧抛 NullReferenceException（实测 2588 次），
    ///   最终导致**战斗系统崩溃**（人物无法移动与攻击、敌人 AI 消失）——
    ///   与"把 LockonTargetCharacter 置 null"是同一种病：游戏无法处理"不完整的角色"。
    ///
    ///   所以锚点必须**像**一个合法角色：集合字段给空集合而不是 null，
    ///   引用字段指向自身而不是 null。这样游戏的读取路径不会踩空。
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 只初始化"读到就炸"的集合/引用字段，**不伪造战斗数据**
    ///     （不设置血量、攻击力等，以免被误判为可击杀单位）
    ///   · 每个字段单独 try/catch：某个字段在特定版本不存在时不应拖垮整个初始化
    /// </summary>
    private void InitializeMinimalState()
    {
        if (_ccb is null)
        {
            return;
        }
        int ok = 0;
        int failed = 0;

        // ① 弱点攻击列表：ShowWeakInfo 很可能遍历它 ⇒ 必须是空集合而不是 null
        TryInit(() => _ccb.WeakAttacks = NewLevelList(), "WeakAttacks", ref ok, ref failed);
        TryInit(() => _ccb.WeakAttacksSteam = NewLevelList(), "WeakAttacksSteam", ref ok, ref failed);

        // ② 受伤位置：UI 定位可能读它 ⇒ 指向锚点自身
        Transform self = _ccb.transform;
        TryInit(() => _ccb.DamagePosition = self, "DamagePosition", ref ok, ref failed);

        // ③ 弱点计数上限：给 0 表示"没有弱点"，比留下垃圾值更安全
        TryInit(() => _ccb.WeakCntMax = 0f, "WeakCntMax", ref ok, ref failed);
        TryInit(() => _ccb.WeakCntMaxSteam = 0f, "WeakCntMaxSteam", ref ok, ref failed);

        // ④ 中心偏移：瞄准点用；0 表示就在原点（锚点本身是个纯坐标点）
        TryInit(() => _ccb.MyCenterOffset = 0f, "MyCenterOffset", ref ok, ref failed);

        _log.LogInfo($"[ANCHOR] minimal character state initialized:"
                     + $" {ok} field(s) OK, {failed} failed"
                     + " (prevents ShowWeakInfo NullReference on the lock target)");
    }

    /// <summary>新建一个空的 ATTACK_LEVEL 列表（IL2CPP 集合类型）。
    /// 注意 `ATTACK_LEVEL` 是**顶层枚举**（反编译确认），不是 CharacterControllerBase 的嵌套类型。</summary>
    private static Il2CppSystem.Collections.Generic.List<ATTACK_LEVEL> NewLevelList()
    {
        return new Il2CppSystem.Collections.Generic.List<ATTACK_LEVEL>();
    }

    /// <summary>
    /// 执行一个字段初始化，失败时计数并记录 ——
    /// 不静默吞掉，也不因为单个字段失败而放弃其余初始化。
    /// </summary>
    private void TryInit(Action action, string field, ref int ok, ref int failed)
    {
        try
        {
            action();
            ok++;
        }
        catch (Exception ex)
        {
            failed++;
            _log.LogError($"[ANCHOR] init '{field}' failed: {ex.Message}");
        }
    }

    /// <summary>创建锚点对象（幂等）。</summary>
    public void EnsureCreated()
    {
        if (_go is not null)
        {
            return;
        }
        try
        {
            _go = new GameObject(ObjectName);
            // 挂到场景根并跨场景保留：锚点不参与渲染也没有碰撞，
            // 因此不会影响任何视觉效果或物理。
            UnityEngine.Object.DontDestroyOnLoad(_go);

            // ★ 关键：锚点必须是真实的 CharacterControllerBase 实例，
            //   因为"锁定目标"字段的类型就是它（不能置 null，也不能塞空物体）。
            //   实测（F12 探针）确认 AddComponent 可行 —— 见 AnchorInstanceProbe 的结论。
            _ccb = _go.AddComponent<CharacterControllerBase>();
            if (_ccb is null)
            {
                _log.LogError("[ANCHOR] AddComponent<CharacterControllerBase>() returned null"
                              + " -> 'lock target = anchor' design cannot work;"
                              + " will fall back to facing/reticle-only wiring.");
            }
            else
            {
                // ★ 让锚点"像"一个合法角色 —— 否则游戏读它的空字段会每帧抛异常并拖垮战斗系统
                InitializeMinimalState();
            }

            _hasSmoothed = false;
            _loggedFirst = false;
            _log.LogInfo($"[ANCHOR] created '{ObjectName}'"
                         + $" (lerp={_lerpSpeed:F2} pitch=[{_minPitch:F0},{_maxPitch:F0}])"
                         + $" controllerInstance={(_ccb is not null ? "OK" : "FAILED")}");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"[ANCHOR] create failed: {ex}");
            _go = null;
        }
    }

    /// <summary>销毁锚点（离开第三人称模式时调用）。</summary>
    public void Destroy()
    {
        if (_go is null)
        {
            return;
        }
        try
        {
            UnityEngine.Object.Destroy(_go);
            _log.LogInfo("[ANCHOR] destroyed");
        }
        catch (Exception ex)
        {
            _log.LogError($"[ANCHOR] destroy failed: {ex.Message}");
        }
        finally
        {
            _go = null;
            _ccb = null;
            _hasSmoothed = false;
            _loggedFirst = false;
            _frameCounter = 0;
        }
    }

    /// <summary>
    /// 把锚点吸附到某个世界坐标并"跟死"（用户玩法：长按中键锁定最近敌人）。
    ///
    /// 为什么要单独一个模式：锚点平时的原点**是爱迪尔**，所以她一动锚点就动。
    /// 锁定敌人时如果还这样，敌人会跟着玩家抖。所以锁定后锚点**脱离爱迪尔**，
    /// 直接钉在目标坐标上。
    /// </summary>
    /// <param name="worldPos">目标世界坐标</param>
    /// <param name="snapRate">吸附速率（越小越平滑，避免瞬间跳变）</param>
    public void LockTo(Vector3 worldPos, float snapRate)
    {
        _locked = true;
        _lockTargetPos = worldPos;
        _lockSnapRate = snapRate;
    }

    /// <summary>解除锁定，锚点回到"绕爱迪尔轨道"模式。</summary>
    public void Unlock()
    {
        _locked = false;
    }

    /// <summary>当前是否处于锁定（跟死敌人）状态。</summary>
    public bool IsLocked => _locked;

    private bool _locked;
    private Vector3 _lockTargetPos;
    private float _lockSnapRate;

    /// <summary>
    /// 每帧更新锚点。**方向由鼠标输入驱动**（不读相机/身体 —— 那会形成闭环，见字段注释）。
    /// 只写自己的 transform，不碰任何游戏对象。
    /// </summary>
    /// <param name="pivot">轨道中心（爱迪尔的位置）—— 锁定时忽略</param>
    /// <param name="distance">锚点距离（沿瞄准方向走多远）—— 锁定时忽略</param>
    /// <param name="seedYaw">初始水平角（首次进入时用作初值，避免跳变）</param>
    /// <param name="seedPitch">初始俯仰角</param>
    public void Tick(Vector3 pivot, float distance, float seedYaw, float seedPitch)
    {
        if (_go is null)
        {
            return;
        }
        try
        {
            // ---- ① 鼠标输入累积角度（锚点是方向的唯一源头）----
            Vector2 delta = ReadMouseDelta();
            _yaw += delta.x * _mouseSensitivity;
            _pitch -= delta.y * _mouseSensitivity;
            _pitch = Mathf.Clamp(_pitch, _minPitch, _maxPitch);

            // 首帧用传入的种子角度（通常是当前相机视角），避免锚点从 0 度猛跳
            if (!_hasSeeded)
            {
                _yaw = seedYaw;
                _pitch = Mathf.Clamp(seedPitch, _minPitch, _maxPitch);
                _hasSeeded = true;
            }

            // ---- ② 算目标位置：锁定模式 or 轨道模式 ----
            Vector3 want;
            if (_locked)
            {
                // 锁定模式：钉在目标坐标上（平滑逼近，避免瞬移跳变）
                want = _lockTargetPos;
            }
            else
            {
                Vector3 dir = Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.forward;
                want = pivot + dir * distance;
            }

            // ---- ③ 平滑（帧率无关的指数插值；锁定模式用独立的吸附速率）----
            Vector3 pos;
            if (!_hasSmoothed)
            {
                pos = want;                 // 首帧直接就位，避免从原点飞过来
                _hasSmoothed = true;
            }
            else
            {
                float dt = Math.Min(Time.deltaTime, 0.1f);   // 卡顿时限制单帧步长
                float rate = _locked ? _lockSnapRate : _lerpSpeed;
                float t = 1f - Mathf.Exp(-rate * dt);
                pos = Vector3.Lerp(_smoothedPos, want, t);
            }
            _smoothedPos = pos;
            _go.transform.position = pos;

            // 日志用方向：锁定时取"从爱迪尔指向锚点"，否则取瞄准方向。
            // 注意锁定时**完全跳过** Quaternion.Euler 的方向合成 —— 这保证了
            // "旋转只在爱迪尔锚点分支允许"（用户定稿的优先级语义）。
            Vector3 logDir;
            if (_locked)
            {
                Vector3 d = want - pivot;
                logDir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
            }
            else
            {
                logDir = Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.forward;
            }
            LogPeriodically(pivot, logDir, pos, distance);
        }
        catch (Exception ex)
        {
            _log.LogError($"[ANCHOR] tick failed: {ex.Message}");
        }
    }

    // 是否已经用种子角度初始化过
    private bool _hasSeeded;

    /// <summary>
    /// 读取鼠标帧间位移。
    ///
    /// 用 Input.mousePosition 差分而不是 "Mouse X" 轴：
    /// 该游戏的 inputManager.asset 不存在（实测），所以传统轴不可用。
    /// 与 FreeFlightCamera 用的是同一套办法，保证手感一致。
    /// </summary>
    private Vector2 ReadMouseDelta()
    {
        Vector3 cur = Input.mousePosition;
        if (!_hasLastMouse)
        {
            _hasLastMouse = true;
            _lastMousePos = new Vector2(cur.x, cur.y);
            return Vector2.zero;            // 首帧不产生位移，避免巨大跳变
        }
        Vector2 now = new Vector2(cur.x, cur.y);
        Vector2 delta = now - _lastMousePos;
        _lastMousePos = now;
        return delta;
    }

    /// <summary>重置鼠标差分基准（进入模式/光标被重置时调用，避免一次巨大跳变）。</summary>
    public void ResetMouseBaseline()
    {
        _hasLastMouse = false;
        _hasSeeded = false;
    }

    /// <summary>
    /// 周期性写日志 —— 这是阶段 2 的验收手段：证明锚点位置正确且不动游戏状态。
    /// </summary>
    private void LogPeriodically(Vector3 pivot, Vector3 dir, Vector3 pos, float distance)
    {
        if (!_loggedFirst)
        {
            _loggedFirst = true;
            _log.LogInfo($"[ANCHOR] first tick: pivot=({pivot.x:F1},{pivot.y:F1},{pivot.z:F1})"
                         + $" dir=({dir.x:F2},{dir.y:F2},{dir.z:F2})"
                         + $" dist={distance:F1}"
                         + $" -> anchor=({pos.x:F1},{pos.y:F1},{pos.z:F1})");
            return;
        }
        if (++_frameCounter % LogEveryFrames != 0)
        {
            return;
        }
        // 相对爱迪尔的高差与水平距离：用于确认"锚点确实在瞄准方向上"
        Vector3 delta = pos - pivot;
        float horiz = new Vector2(delta.x, delta.z).magnitude;
        _log.LogInfo($"[ANCHOR] pos=({pos.x:F1},{pos.y:F1},{pos.z:F1})"
                     + $" | relPivot: horiz={horiz:F1} dy={delta.y:F1}"
                     + $" | dir=({dir.x:F2},{dir.y:F2},{dir.z:F2}) dist={distance:F1}");
    }
}
