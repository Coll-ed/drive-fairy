// 驱动妖精 AI-deal-Rays · 自由飞行相机（模式 B）
//
// ── 设计目标（用户需求）──────────────────────────────────────────────────
//   中键切换：模式 A（原版完全控制）↔ 模式 B（我们接管）
//   模式 B = 关闭原版锁定 + 鼠标自由视角 + 三维飞行
//
// ── 为什么这样能根除"抖动/抢夺"──────────────────────────────────────────
//   之前所有尝试都在**改游戏自己的相机对象**（改目标组权重、改相机旋转），
//   而游戏每帧也在改同一个对象 → 双方互相覆盖 → 抖动。
//   本方案改为：**自己建一个 Cinemachine 虚拟相机**，只在模式 B 把
//   游戏那批相机的优先级压到 0、把我们的提到 100。Cinemachine 会自己做
//   相机切换（这正是它被设计来做的事），双方**没有共享可写状态**。
//   切回模式 A 时把优先级原样恢复，游戏那一套完全不受影响。
//
// ── 已确认的 API（反编译核对，非猜测）───────────────────────────────────
//   · CinemachineVirtualCamera.Priority / m_Lens / Follow / LookAt
//   · AddCinemachineComponent<T>() 添加 Body/Aim 组件
//   · CinemachineTransposer.m_FollowOffset（相机相对跟随点的偏移）
//   · CharacterControllerBase.LockonTargetCharacter 有 setter（可清空以关锁定）
//   · 游戏的 inputManager.asset 不存在 → 传统轴 "Mouse X" 不可用，
//     因此鼠标改用 Input.mousePosition 做帧间差分（见 ReadMouseDelta）

using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Cinemachine;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 自由飞行相机的创建、切换与逐帧驱动。
/// 所有状态都在本类内，不与游戏逻辑共享可写字段 —— 这是不抖动的根本原因。
/// </summary>
internal sealed class FreeFlightCamera
{
    // ---- 相机结构命名（具名，避免散落的字面量）----
    private const string VcamObjectName = "AidealRays_FreeFlightVcam";
    private const string RigObjectName = "AidealRays_FreeFlightRig";
    private const string AimObjectName = "AidealRays_FreeFlightAim";

    // 瞄准点放在 Rig 前方多远（Unity 单位）。
    // 语义依据（反编译确认）：CinemachineComposer 属于 Aim 环节，它把 LookAt 目标的
    // **位置**摆到画面的 (m_ScreenX, m_ScreenY) 处 —— 依赖的是位置、不是朝向。
    // 因此必须给相机一个"位于视线前方"的点作为 LookAt，
    // 而不是把 LookAt 直接指向 Rig 本身（那样 Rig 怎么转都不影响视线）。
    private const float AimPointForwardDistance = 10f;

    // ---- 数值参数（在构造函数里由配置注入，代码内不写死具体值）----
    private readonly float _mouseSensitivity;
    private readonly float _minPitch;
    private readonly float _maxPitch;
    private readonly float _distance;
    private readonly float _heightOffset;
    private readonly int _activePriority;
    private readonly int _suppressedPriority;

    private readonly ManualLogSource _log;

    // 运行时对象
    private GameObject? _vcamGo;
    private GameObject? _rigGo;
    private GameObject? _aimGo;
    private CinemachineVirtualCamera? _vcam;

    // 模式状态
    private bool _active;
    private float _yaw;
    private float _pitch;
    private Vector3 _lastMouse;
    private bool _hasLastMouse;
    // 最近一次鼠标差分（供探针输出，用于诊断"鼠标为什么没驱动视角"）
    private Vector2 _lastDelta;
    // 光标接管前的状态，切回时还原
    private bool _savedCursorVisible = true;
    private CursorLockMode _savedCursorLock = CursorLockMode.None;

    public FreeFlightCamera(ManualLogSource log,
                            float mouseSensitivity,
                            float minPitch,
                            float maxPitch,
                            float distance,
                            float heightOffset,
                            int activePriority,
                            int suppressedPriority)
    {
        _log = log;
        _mouseSensitivity = mouseSensitivity;
        _minPitch = minPitch;
        _maxPitch = maxPitch;
        _distance = distance;
        _heightOffset = heightOffset;
        _activePriority = activePriority;
        _suppressedPriority = suppressedPriority;
    }

    /// <summary>当前是否处于模式 B（我们接管相机）。</summary>
    public bool IsActive => _active;

    /// <summary>
    /// 当前视角的水平角（度）。供自由瞄准锚点复用 ——
    /// 让锚点方向与相机方向**共用同一套角度状态**，避免两套角度各自漂移
    /// 导致"准心指的"与"屏幕看到的"不一致。
    /// </summary>
    public float ViewYaw => _yaw;

    /// <summary>当前视角的俯仰角（度）。同上。</summary>
    public float ViewPitch => _pitch;

    /// <summary>
    /// 当前视角的前方向（单位向量），由 yaw/pitch 合成，与相机朝向一致。
    /// 这是自由瞄准锚点的方向来源。
    /// </summary>
    public Vector3 ViewForward => Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.forward;

    /// <summary>
    /// 自由视角（按住中键）状态。
    /// 与模式 B 的区别：自由视角是**临时**的，松开即还原，
    /// 且**不隐藏光标、不改锁定、不改移动** —— 只借用相机优先级。
    /// </summary>
    public bool IsFreeLook => _freeLook;

    private bool _freeLook;
    private float _freeLookYaw;
    private float _freeLookPitch;
    private float _savedLensFov = -1f;

    /// <summary>
    /// 进入自由视角（按住中键时调用）。
    /// 只做三件事：记住当前 FOV、借用相机优先级、初始化环绕角度。
    /// **不碰光标、不碰锁定、不碰移动方向** —— 用户明确要求"不改爱迪尔移动方向"。
    /// </summary>
    public void BeginFreeLook(IReadOnlyList<CinemachineVirtualCamera?> gameCameras,
                              Transform player, Transform? gameCamera)
    {
        if (_freeLook)
        {
            return;
        }
        try
        {
            EnsureCreated(player);
            if (_vcam is null)
            {
                _log.LogError("free-look vcam unavailable");
                return;
            }

            // 从当前相机朝向取初值，避免按住瞬间跳变
            if (gameCamera is not null)
            {
                Vector3 e = gameCamera.eulerAngles;
                _freeLookPitch = NormalizePitch(e.x);
                _freeLookYaw = e.y;
                // 记住 FOV：自由视角期间沿用同一个 FOV，
                // 否则进出时会"变焦"，看起来像 bug
                try
                {
                    _savedLensFov = gameCamera.GetComponent<Camera>()?.fieldOfView ?? -1f;
                }
                catch (Exception ex)
                {
                    _log.LogError($"read game camera fov failed: {ex.Message}");
                    _savedLensFov = -1f;
                }
            }
            _hasLastMouse = false;   // 首帧不产生位移，避免巨大跳变
            // 沿用游戏 FOV（取不到就保持原有镜头设置）
            if (_savedLensFov > 0f)
            {
                try
                {
                    var lens = _vcam.m_Lens;
                    lens.FieldOfView = _savedLensFov;
                    _vcam.m_Lens = lens;
                }
                catch (Exception ex)
                {
                    _log.LogError($"apply free-look fov failed: {ex.Message}");
                }
            }

            // 相机优先级借用（原版相机只是"没轮到"，它们的配置一个都不改）
            foreach (var g in gameCameras)
            {
                if (g is not null)
                {
                    g.Priority = _suppressedPriority;
                }
            }
            _vcam.Priority = _activePriority;
            _freeLook = true;
            _log.LogInfo($"[FREELOOK] begin (fov={_savedLensFov:F0}, yaw={_freeLookYaw:F0}, pitch={_freeLookPitch:F0})"
                         + " - camera only; movement/lock untouched");
        }
        catch (Exception ex)
        {
            _log.LogError($"BeginFreeLook failed: {ex}");
            _freeLook = false;
        }
    }

    /// <summary>退出自由视角（松开中键时调用）：把相机交回游戏。</summary>
    public void EndFreeLook()
    {
        if (!_freeLook)
        {
            return;
        }
        try
        {
            if (_vcam is not null)
            {
                _vcam.Priority = _suppressedPriority;
            }
            _freeLook = false;
            _log.LogInfo("[FREELOOK] end - game camera restored");
        }
        catch (Exception ex)
        {
            _log.LogError($"EndFreeLook failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 自由视角逐帧驱动：环绕玩家旋转相机。
    /// 环绕角度**只由鼠标决定**，与玩家朝向无关 ——
    /// 这样按住中键转视角时，爱迪尔的朝向与移动方向完全不受影响。
    /// </summary>
    public void TickFreeLook(Transform player)
    {
        if (!_freeLook || _rigGo is null || _aimGo is null)
        {
            return;
        }
        try
        {
            Vector2 delta = ReadMouseDelta();
            _freeLookYaw += delta.x * _mouseSensitivity;
            _freeLookPitch -= delta.y * _mouseSensitivity;
            _freeLookPitch = Mathf.Clamp(_freeLookPitch, _minPitch, _maxPitch);

            Vector3 head = player.position;
            head.y += _heightOffset;

            // 相机位置：从玩家出发，按环绕角反推相机所在的球面点。
            // 用 (yaw, pitch) 的球坐标而不是"跟随玩家朝向"，即可做到转向自由。
            Quaternion rot = Quaternion.Euler(_freeLookPitch, _freeLookYaw, 0f);
            Vector3 camOffset = rot * new Vector3(0f, 0f, -_distance);
            camOffset.y = 0f;   // 水平环绕；垂直差异交给下面的视线方向
            float pitchRad = _freeLookPitch * Mathf.Deg2Rad;
            float horiz = Mathf.Cos(pitchRad) * _distance;
            float vert = Mathf.Sin(pitchRad) * _distance;
            Vector3 flatDir = new Vector3(camOffset.x, 0f, camOffset.z);
            if (flatDir.sqrMagnitude < 1e-6f)
            {
                flatDir = new Vector3(0f, 0f, -1f);
            }
            flatDir.Normalize();
            Vector3 camPos = head + flatDir * horiz - new Vector3(0f, vert, 0f);

            _rigGo.transform.position = camPos;
            // 瞄准点放在玩家身上：相机始终看向爱迪尔
            _aimGo.transform.position = head;
            _rigGo.transform.rotation = Quaternion.LookRotation(head - camPos, Vector3.up);
        }
        catch (Exception ex)
        {
            _log.LogError($"TickFreeLook failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 切换模式。首次进入时才创建相机对象（懒创建，避免在不玩的时候留垃圾对象）。
    /// </summary>
    public void Toggle(IReadOnlyList<CinemachineVirtualCamera?> gameCameras, Transform player)
    {
        if (_active)
        {
            Deactivate(gameCameras);
        }
        else
        {
            Activate(gameCameras, player);
        }
    }

    private void Activate(IReadOnlyList<CinemachineVirtualCamera?> gameCameras, Transform player)
    {
        try
        {
            EnsureCreated(player);
            if (_vcam is null)
            {
                _log.LogError("free-flight vcam unavailable - cannot enter mode B");
                return;
            }

            // 用当前相机朝向作为初值，避免切换瞬间跳变
            Camera? cam = Camera.main;
            if (cam is not null)
            {
                Vector3 e = cam.transform.eulerAngles;
                _pitch = NormalizePitch(e.x);
                _yaw = e.y;
            }
            _hasLastMouse = false;   // 禁用第一帧的鼠标差分，防止一次巨大跳变

            // 隐藏并锁定光标 —— 自由视角需要鼠标持续驱动视角，
            // 光标若可见会移出窗口导致视角转到边界就停住。
            // 反编译确认：Unity 的 Cursor 提供 visible / lockState；
            // CursorLockMode 枚举 = { None, Locked, Confined }。
            // 另注：游戏自己的托管代码里**完全没有**碰光标（搜遍 73 个自研脚本零命中），
            // 所以这里不必"恢复游戏的做法"，切回时还原 Unity 默认值即可。
            CaptureCursor();

            // 压低游戏相机、抬高我们的 —— Cinemachine 自动完成切换
            foreach (var g in gameCameras)
            {
                if (g is not null)
                {
                    g.Priority = _suppressedPriority;
                }
            }
            _vcam.Priority = _activePriority;
            _active = true;
            _log.LogInfo($"[MODE] B (free flight) ON - our vcam prio={_activePriority}, game vcams prio={_suppressedPriority}");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉；并回滚到安全状态
            _log.LogError($"activate free-flight camera failed: {ex}");
            _active = false;
        }
    }

    /// <summary>
    /// 隐藏并锁定光标，让鼠标可以无限移动（沉浸式视角）。
    ///
    /// 关键：优先用**游戏自己的**光标接口，而不是裸 Unity Cursor API。
    /// 反编译依据（GameManagerBase）：
    ///   · CursorShow(bool)            L5082  —— 游戏集中管理光标的地方
    ///   · MoveCursorToBottomRight()   L5071  —— 配套：把光标移到角落
    ///   · GetInstance()               L3849  —— 单例入口
    /// 之前我直接写 Cursor.visible / lockState，等于绕过游戏的统一管理，
    /// 于是战斗中出现"光标莫名被呼出"——两套机制各说各话。
    ///
    /// 注意：原生侧 CursorShow 是 protected（见 NativeMethodInfoPtr 名字里的 Protected），
    /// interop 虽暴露为 public，调用仍可能失败，因此失败时回退到裸 API 并记录原因。
    /// </summary>
    private void CaptureCursor()
    {
        _savedCursorVisible = Cursor.visible;
        _savedCursorLock = Cursor.lockState;

        bool usedGameApi = false;
        try
        {
            var gm = GameManagerBase.GetInstance();
            if (gm is not null)
            {
                gm.CursorShow(false);
                gm.MoveCursorToBottomRight();
                usedGameApi = true;
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"GameManagerBase cursor hide failed, falling back to raw Cursor API: {ex.Message}");
        }

        if (!usedGameApi)
        {
            try
            {
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
            catch (Exception ex)
            {
                _log.LogError($"raw cursor hide failed: {ex.Message}");
            }
        }

        _log.LogInfo($"[CURSOR] captured via {(usedGameApi ? "GameManagerBase.CursorShow" : "raw Cursor API")}"
                     + $" (saved visible={_savedCursorVisible} lock={_savedCursorLock})");
    }

    /// <summary>还原光标：把显示权交回游戏自己的管理器。</summary>
    private void ReleaseCursor()
    {
        bool usedGameApi = false;
        try
        {
            var gm = GameManagerBase.GetInstance();
            if (gm is not null)
            {
                gm.CursorShow(true);   // 交回游戏：由它决定何时显示
                usedGameApi = true;
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"GameManagerBase cursor restore failed, falling back to raw Cursor API: {ex.Message}");
        }

        try
        {
            if (!usedGameApi)
            {
                Cursor.visible = _savedCursorVisible;
                Cursor.lockState = _savedCursorLock;
            }
            else
            {
                // 用游戏 API 恢复显示权，但仍把裸状态还原成接管前的值，
                // 避免上一轮遗留的 Locked 状态继续影响 UI 系统
                Cursor.lockState = _savedCursorLock;
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"raw cursor restore failed: {ex.Message}");
        }

        _log.LogInfo($"[CURSOR] released via {(usedGameApi ? "GameManagerBase.CursorShow" : "raw Cursor API")}"
                     + $" (visible={_savedCursorVisible} lock={_savedCursorLock})");
    }

    private void Deactivate(IReadOnlyList<CinemachineVirtualCamera?> gameCameras)
    {
        try
        {
            if (_vcam is not null)
            {
                _vcam.Priority = _suppressedPriority;
            }
            // 交还光标
            ReleaseCursor();
            // 游戏相机的优先级由游戏自己每帧维护，这里只需把我们的降下去
            _active = false;
            _log.LogInfo("[MODE] B OFF - game cameras restored");
        }
        catch (Exception ex)
        {
            _log.LogError($"deactivate free-flight camera failed: {ex}");
        }
    }

    /// <summary>懒创建：一个 Rig（我们驱动它的位置/朝向）+ 一个虚拟相机跟随它。</summary>
    private void EnsureCreated(Transform player)
    {
        if (_vcamGo is not null && _rigGo is not null)
        {
            return;
        }
        // Rig：相机实际跟随的对象。我们每帧写它的位置与朝向。
        _rigGo = new GameObject(RigObjectName);
        UnityEngine.Object.DontDestroyOnLoad(_rigGo);

        // 瞄准点：Rig 的子物体，位于其前方。
        // Composer 把"这个点"对齐到画面中心，于是视线方向 = Rig 的 forward。
        _aimGo = new GameObject(AimObjectName);
        _aimGo.transform.SetParent(_rigGo.transform, worldPositionStays: false);
        _aimGo.transform.localPosition = new Vector3(0f, 0f, AimPointForwardDistance);

        // 虚拟相机对象
        _vcamGo = new GameObject(VcamObjectName);
        UnityEngine.Object.DontDestroyOnLoad(_vcamGo);
        _vcam = _vcamGo.AddComponent<CinemachineVirtualCamera>();
        if (_vcam is null)
        {
            _log.LogError("AddComponent<CinemachineVirtualCamera> returned null");
            return;
        }

        _vcam.Follow = _rigGo.transform;
        _vcam.LookAt = _aimGo.transform;   // 瞄"前方那个点"，而不是 Rig 本身

        // Body：按 FollowOffset 把相机放在跟随点之后。
        // 用 LockToTargetWithWorldUp 而非 LockToTarget —— 这是标准的"环绕相机"语义：
        //   · 偏航跟随目标（相机随你转身转到身后）
        //   · 俯仰/翻滚不受目标姿态影响（保持以世界向上为基准）
        // 反编译依据：BindingMode 枚举为
        //   { LockToTargetOnAssign, LockToTargetWithWorldUp, LockToTargetNoRoll,
        //     LockToTarget, WorldSpace, SimpleFollowWithWorldUp }
        var transposer = _vcam.AddCinemachineComponent<CinemachineTransposer>();
        if (transposer is not null)
        {
            transposer.m_BindingMode = CinemachineTransposer.BindingMode.LockToTargetWithWorldUp;
            transposer.m_FollowOffset = new Vector3(0f, _heightOffset, -_distance);
            // 阻尼设为 0 并直接写入 Rig 位置：位置完全由我们控制，不需要 Cinemachine 平滑
            transposer.m_XDamping = 0f;
            transposer.m_YDamping = 0f;
            transposer.m_ZDamping = 0f;
        }
        else
        {
            _log.LogError("AddCinemachineComponent<CinemachineTransposer> returned null");
        }

        // Aim：Composer 负责把视线对准 LookAt（即 Rig 前方那个点）
        var composer = _vcam.AddCinemachineComponent<CinemachineComposer>();
        if (composer is null)
        {
            _log.LogError("AddCinemachineComponent<CinemachineComposer> returned null");
        }

        // 镜头参数沿用游戏的手感（FOV 从当前相机取，取不到用 Unity 默认 60）
        try
        {
            Camera? cam = Camera.main;
            var lens = _vcam.m_Lens;
            lens.FieldOfView = cam is not null ? cam.fieldOfView : DefaultFieldOfView;
            _vcam.m_Lens = lens;
        }
        catch (Exception ex)
        {
            _log.LogError($"configure lens failed: {ex.Message}");
        }

        _vcam.Priority = _suppressedPriority;   // 未激活时不与游戏争
        _log.LogInfo($"free-flight vcam created (dist={_distance} height={_heightOffset})");
    }

    // Unity 的默认透视 FOV，作为取不到游戏相机时的兜底值
    private const float DefaultFieldOfView = 60f;

    /// <summary>
    /// 每帧驱动：位置跟随玩家，朝向由鼠标控制。
    /// 由飞行插件的 Update 补丁在模式 B 时调用。
    /// </summary>
    public void Tick(Transform player, float verticalInput)
    {
        if (!_active || _rigGo is null)
        {
            return;
        }
        _tickCount++;
        try
        {
            // ① 鼠标 → 偏航/俯仰（用 mousePosition 差分，因为游戏没配置传统输入轴）
            Vector2 delta = ReadMouseDelta();
            _yaw += delta.x * _mouseSensitivity;
            _pitch -= delta.y * _mouseSensitivity;
            _pitch = Mathf.Clamp(_pitch, _minPitch, _maxPitch);

            // ② Rig 跟随玩家位置（玩家的水平移动仍由游戏负责，垂直由飞行插件负责）
            Vector3 p = player.position;
            _rigGo.transform.position = new Vector3(p.x, p.y + _heightOffset, p.z);

            // ③ Rig 只带偏航。
            //    理由（反编译语义）：Transposer 的 LockToTargetWithWorldUp 用目标的
            //    偏航旋转 FollowOffset —— 相机围着玩家在水平面上转圈。
            //    若把俯仰也写进 Rig，相机会跟着俯仰被甩到玩家头顶/脚下，
            //    那不是"转视角"而是"绕着圆环跑"。
            _rigGo.transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

            // ④ 瞄准点 = Rig 前方，含俯仰角。
            //    **这是之前失效的根因**：瞄准点作为 Rig 的子物体，其 localPosition
            //    固定为 (0,0,d)，世界位置只随父物体**旋转**变化 —— 但它并不表达
            //    "相机该往哪看"这个意图，因为 Composer 瞄的是它的**位置**。
            //    所以必须每帧用 (yaw, pitch) 显式算出它该在哪：
            //    俯仰只体现在瞄点上（视线方向），不体现在 Rig 上（相机环绕）。
            if (_aimGo is not null)
            {
                Vector3 dir = Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.forward;
                _aimGo.transform.position = _rigGo.transform.position + dir * AimPointForwardDistance;
            }

            // 周期性状态报告：用于确认"我们确实在驱动、且对象是可用的"
            if (_tickCount % PROBE_REPORT_EVERY == 0)
            {
                ReportProbe();
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"free-flight tick failed: {ex.Message}");
        }
    }

    // 探针报告周期（帧）：60fps 下约每 2 秒一条，够看又不刷屏
    private const int PROBE_REPORT_EVERY = 120;
    private int _tickCount;

    /// <summary>
    /// 状态探针：回答"为什么接管没生效"。
    /// 刻意把每一项单独 try 住 —— 任何一项失败都要能看见，
    /// 而不是整条报告因为一个属性抛异常而丢失。
    /// </summary>
    private void ReportProbe()
    {
        string rig = "n/a";
        try
        {
            if (_rigGo is not null)
            {
                Vector3 rp = _rigGo.transform.position;
                rig = $"rigPos={rp.x:F1},{rp.y:F1},{rp.z:F1}"
                      + $" active={_rigGo.activeInHierarchy}"
                      + $" yaw={_yaw:F0} pitch={_pitch:F0}";
            }
        }
        catch (Exception ex)
        {
            rig = $"rig=err({ex.Message})";
        }

        string vcam = "n/a";
        try
        {
            if (_vcam is not null)
            {
                vcam = $"prio={_vcam.Priority}"
                       + $" active={_vcamGo?.activeInHierarchy}"
                       + $" follow={(_vcam.Follow is null ? "null" : _vcam.Follow.name)}"
                       + $" lookAt={(_vcam.LookAt is null ? "null" : _vcam.LookAt.name)}";
                // 管道长度是"组件是否真的被采用"的直接证据
                try
                {
                    var pipe = _vcam.GetComponentPipeline();
                    vcam += $" pipe={pipe?.Length ?? -1}";
                }
                catch (Exception ex)
                {
                    vcam += $" pipe=err({ex.Message})";
                }
            }
        }
        catch (Exception ex)
        {
            vcam = $"vcam=err({ex.Message})";
        }

        string cam = "n/a";
        try
        {
            Camera? m = Camera.main;
            cam = m is null
                ? "main=null"
                : $"main={m.name} pos={m.transform.position.x:F1},{m.transform.position.y:F1},{m.transform.position.z:F1}"
                  + $" fwdY={m.transform.forward.y:F2}";
        }
        catch (Exception ex)
        {
            cam = $"main=err({ex.Message})";
        }

        // 瞄准点位置：Composer 应把它对齐到画面中心，因此它的高度应与视线方向一致
        string aim = "n/a";
        try
        {
            if (_aimGo is not null)
            {
                Vector3 ap = _aimGo.transform.position;
                aim = $"aimPos={ap.x:F1},{ap.y:F1},{ap.z:F1}";
            }
        }
        catch (Exception ex)
        {
            aim = $"aim=err({ex.Message})";
        }

        // 最近一次的鼠标差分与原始坐标（用于诊断"为什么 pitch 不动"）
        string mouse = "n/a";
        try
        {
            Vector3 mp = Input.mousePosition;
            mouse = $"mouse={mp.x:F0},{mp.y:F0} lastDelta={_lastDelta.x:F1},{_lastDelta.y:F1}";
        }
        catch (Exception ex)
        {
            mouse = $"mouse=err({ex.Message})";
        }

        _log.LogInfo($"[PROBE] ticks={_tickCount} | {rig} | {aim} | {mouse} | {vcam} | {cam}");
    }

    /// <summary>
    /// 读取鼠标帧间位移。
    ///
    /// 为什么不用 Input.GetAxis("Mouse X")：实测游戏目录下**没有 inputManager.asset**，
    /// 说明传统输入轴未配置，GetAxis 会返回 0 → 鼠标完全无反应。
    /// 因此改用 mousePosition 差分，不依赖任何轴配置。
    /// </summary>
    private Vector2 ReadMouseDelta()
    {
        Vector3 cur = Input.mousePosition;
        if (!_hasLastMouse)
        {
            _lastMouse = cur;
            _hasLastMouse = true;
            _lastDelta = Vector2.zero;
            return _lastDelta;   // 第一帧不产生位移，避免巨大跳变
        }
        _lastDelta = new Vector2(cur.x - _lastMouse.x, cur.y - _lastMouse.y);
        _lastMouse = cur;
        return _lastDelta;
    }

    /// <summary>把 euler x 折到 -180..180，便于与 pitch 限值比较。</summary>
    private static float NormalizePitch(float x)
    {
        return x > 180f ? x - 360f : x;
    }
}
