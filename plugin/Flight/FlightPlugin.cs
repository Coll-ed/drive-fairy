// 驱动妖精 AI-deal-Rays · 3D 飞行改造
//
// 当前方案：**路线 D —— 直写 Transform.position.y**（已实测成功，用户确认）
//
// ── 试过并失败的路线（保留记录，避免重犯）────────────────────────────────
//   路线 A：写 PlayerControlCustom.Move.y
//     → 实测无效。日志确认值确实写进去了（y=1.000，松开正确复位），
//       但角色世界高度不变：原生移动实现**不消费** Move 的 Y 分量
//       （Move 是纯输出字段，托管代码里无人读取它）。
//   路线 B：CharacterController.Move()
//     → 前提错误：玩家对象上**根本没有 CharacterController 组件**
//       （实测 6 次 GetComponent 返回 null）。
//   路线 C：GetComponent<Rigidbody>()
//     → 同样是猜组件；而且游戏自己有 CharacterControllerBase.Rigid 字段，
//       本该用它而不是 GetComponent。
//
//   **共同错误：前三条都是"看起来合理"就动手，没有先取证组件/字段是否存在。**
//   教训：先查反编译出的字段清单 + 运行时探测，再决定改什么。
//
// ── 当前实现（路线 D）────────────────────────────────────────────────────
//   在 PlayerControlCustom.Update 之后：
//     float dt = Min(Time.deltaTime, MAX_FRAME_SECONDS);
//     float dy = vertical * VerticalSpeed * dt;
//     t.position = new Vector3(p.x, p.y + dy, p.z);   // 只动 Y
//
//   · 空格 = +1，左Shift = -1，都不按/同时按 = 0（回到游戏原状）
//   · 水平移动仍由游戏自己的系统负责，两边不重叠
//   · VerticalSpeed 是配置项，调手感不必重新编译
//
// ── 为什么直写 Transform 可行 ────────────────────────────────────────────
//   实测 CharacterControllerBase.Rigid（游戏自己的 Rigidbody 字段）位置会随
//   Transform 同步变化（RigidObjY 始终跟随 worldY），说明二者不存在"争夺"，
//   写入的位置不会被原生物理覆盖。Rigid 为非运动学且不受重力（kin=False grav=False），
//   因此可以持续爬升而不被重力拉回。
//
// ── 已知问题（待处理，如实标注）─────────────────────────────────────────
//   1. 松手后不会自动降落（用户反馈"似乎不会自动下降了"）——
//      因为本实现只在按键时写入位置，不按键时完全不干预。
//      是否要补一个缓降/悬停逻辑，待用户裁定。
//   2. 撞墙/穿模未处理：Transform 直写**绕过物理碰撞**，贴墙上升可能穿入几何体。
//      若需要，可改用 Rigid.MovePosition 或自行做射线检测。
//
// ── 验收标准（用户已确认达成）───────────────────────────────────────────
//   **按空格能升、按 Shift 能降。** 相机仍为第三人称跟随。

using System;
using System.Collections.Generic;
using AidealRays.MoveProbe;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 框架运行模式。
///
/// 为什么需要这个概念（用户提出的架构要求）：
///   本插件不是"给游戏打补丁加功能"，而是一个**可启停的框架**：
///     · Vanilla    —— 完全不动游戏。上升/下降也**不生效**。
///                     理由：高度将来要成为战斗规则的一部分（敌人 AI 要加强），
///                     若原版模式下就悄悄改了角色高度，后续两边必然打架。
///     · FreeFlight —— 框架接管：三维飞行 + 自由相机 + 关闭原版锁定。
///                     三者必须同时生效，否则"锁定"与"自由相机"会互相覆盖
///                     （用户实测：flying 时锁定仍在，pitch 被改回去）。
///
/// 后续扩展（敌人 AI、三维空战规则）都应作为新子系统挂到本枚举的模式下，
/// 而不是散落成独立的 Harmony 补丁。
/// </summary>
public enum FrameworkMode
{
    /// <summary>原版：框架完全不干预，一根手指都不碰（F1）。</summary>
    Vanilla = 0,

    /// <summary>自由飞行：框架接管飞行与相机；锁定系统保持原版（F2）。</summary>
    FreeFlight = 1,

    /// <summary>
    /// 第三人称脱离准心（F3）：准心跟随相机视线，攻击/移动朝准心方向。
    /// ⚠️ 原子改动 —— 见 DESIGN_FREE_AIM.md。在全部环节（朝向/准心/接近/冲刺/
    /// 大招/移动）完成前**不得对外暴露**，否则会出现"准心指 A、身体朝 B、攻击打 B"
    /// 三者不一致的状态（用户明确警告过)。
    /// </summary>
    ThirdPerson = 2,
}

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class FlightPlugin : BasePlugin
{
    // ---- 插件标识（BepInEx 要求）----
    public const string PluginGuid = "com.aidealrays.flight";
    public const string PluginName = "AidealRays 3D Flight";
    public const string PluginVersion = "0.4.0";

    // ---- 配置项（写入 BepInEx\config\com.aidealrays.flight.cfg，改完不必重编译）----
    private const string CFG_SECTION = "Flight";
    // 垂直速度，单位：Unity 单位/秒。来源：与游戏内移动速度同量级，
    // 使"按住 1 秒"产生肉眼可辨的高度变化；可按手感在配置里调。
    private const float SPEED_DEFAULT = 6.0f;

    // ---- 日志节流（具名 + 理由）----
    private const int LOG_INTERVAL_FRAMES = 30;   // 60fps 下约 0.5 秒一次
    private const float INPUT_EPSILON = 0.01f;
    private const string NUM_FMT = "F3";
    // Unity 的 deltaTime 在极端情况下可能为 0；给位移一个上限防止瞬移
    private const float MAX_FRAME_SECONDS = 0.1f;

    // Unity 单位：视线延伸距离上限。取足够远以保证与目标 XZ 平面相交（战斗距离通常 < 100）
    private const float AIM_RAY_LENGTH = 500f;
    // 方向向量归零阈值：小于此值认为方向无效，避免除零
    private const float DIR_EPSILON = 1e-4f;

    // ---- 相机俯仰修正（Z 轴适配的核心）----
    // 只检查屏幕中心附近：屏幕中心对应纵向 FOV 的一半。
    // 0.5 = 恰好半个 FOV（即目标刚好处在画面纵向边缘，最不打扰的阈值）
    private const float AIM_VISIBLE_HALF = 0.5f;
    // 修正强度上限：避免完全接管相机导致手感生硬，也避免与 Cinemachine 打架
    private const float AIM_BLEND_DEFAULT = 0.9f;
    // 近目标保护：目标过近时方向向量噪声大，跳过修正
    private const float AIM_MIN_DISTANCE = 0.5f;

    // ---- 目标组权重修正（相机 Z 轴适配）----
    // 玩家在 TargetGroup 里的成员名。来源：运行时实测（[GRP] 日志显示 #0 成员名为
    // AIdealBattle，且同时是 VCameraMain 的 lookAt/follow，据此判定它是玩家节点）。
    private const string PLAYER_GROUP_MEMBER_NAME = "AIdealBattle";
    // 高度差达到该值（Unity 单位）时，玩家在目标组里的权重降到 0，
    // 即相机完全看向敌人。取值理由：常见战斗距离下 10 米的高度差已足以让
    // "中点"明显偏离敌人（偏移 5 米），此时就该收敛到敌人身上。
    private const float GROUP_WEIGHT_FULL_AT_ABS_DIFF = 10f;
    // 组权重修正的日志节流
    private const int GROUPFIX_LOG_EVERY = 40;

    // 修正日志节流：每 N 次修正打印 1 行。
    // 取 1 表示每次都打印 —— 排查相机问题时需要看到每一帧的实际值
    // （此前用 20，导致日志里看不到输出，误判为"修正没执行"）。
    private const int AIM_LOG_EVERY = 1;
    // 虚拟相机配置报告的节流周期（以采样周期为单位）。
    // 列的是"配置"而非逐帧状态，变化慢，10 个周期输出一次足够。
    private const int VCAM_REPORT_EVERY = 10;

    // 俯仰修正的最小有效角度：小于此值不施加旋转，避免每帧微小抖动
    private const float AIM_PITCH_EPSILON_DEG = 0.05f;
    // 俯仰角平滑速率默认值（度/秒）。取值理由：60 度的高度差需要约 60 度修正，
    // 按 90 度/秒计算不到 0.7 秒即可对准，既跟得上又不会瞬移突兀。
    private const float AIM_MAX_RATE_DEFAULT = 90f;

    // ---- 自由飞行相机（模式 B）默认值 ----
    // 鼠标灵敏度：每像素位移对应的角度（度/像素）。取 0.15 时移动 100 像素约转 15 度，
    // 与常见第三人称游戏同量级。
    private const float MOUSE_SENS_DEFAULT = 0.15f;
    // 俯仰限位：留出 5 度余量避免万向锁附近的抖动
    private const float PITCH_MIN_DEFAULT = -85f;
    private const float PITCH_MAX_DEFAULT = 85f;
    // 相机到跟随点的水平距离与抬升（Unity 单位）
    private const float CAM_DISTANCE_DEFAULT = 6f;
    private const float CAM_HEIGHT_DEFAULT = 1.5f;
    // 优先级：我们激活时用高值，未激活时用 0 与游戏普通相机同档（不会抢）
    private const int CAM_PRIO_ACTIVE_DEFAULT = 100;
    private const int CAM_PRIO_IDLE_DEFAULT = 0;

    internal static new ManualLogSource Log = null!;
    private static ConfigEntry<float> _cfgSpeed = null!;
    private static ConfigEntry<bool> _cfgCamDiag = null!;
    private static ConfigEntry<float> _cfgAimBlend = null!;
    // 俯仰角平滑速率（度/秒）
    private static ConfigEntry<float> _cfgAimMaxRateDegPerSec = null!;

    // ---- 自由飞行相机（模式 B）----
    private static ConfigEntry<float> _cfgFreeCamSens = null!;
    private static ConfigEntry<float> _cfgFreeCamPitchMin = null!;
    private static ConfigEntry<float> _cfgFreeCamPitchMax = null!;
    private static ConfigEntry<float> _cfgFreeCamDistance = null!;
    private static ConfigEntry<float> _cfgFreeCamHeight = null!;
    // 自由相机实例（模式 B 的创建/切换/驱动都封装在它内部）
    private static FreeFlightCamera? _freeCam;
    // 切换键（由配置的键名解析而来）。
    // 默认 Mouse2 = 鼠标中键；注意 Unity 的命名是 Mouse0/1/2（左/右/中），
    // 没有名为 "MiddleMouse" 的键 —— 这里写错过一次，已在配置说明里标注。

    // ---- 框架状态 ----
    // 当前模式。默认 Vanilla：进游戏什么都不接管，必须玩家按键才启用。
    private static FrameworkMode _mode = FrameworkMode.Vanilla;

    // 框架总开关（配置）。为 false 时连切换键都不响应，插件等同不存在。
    private static ConfigEntry<bool> _cfgFrameworkEnabled = null!;

    // 框架状态与锁定相关字段已移除：
    // 早期版本保存/恢复 LockonLock 以"暂停锁定"，但用户要求模式切换只接管相机与升降，
    // 不碰锁定系统；保留这些字段只会让后来人误以为锁定仍被干预。
    // ---- 【事故禁用】以下字段属于两处被回滚的改动，暂时保留以备重做 ----
    // 保留而非删除的理由：这两处是我犯过的重大错误（详见各方法头部的 INCIDENT 注释），
    // 删掉代码就会丢掉教训。等重新反编译确认移动管线后再启用。
    // 事故相关字段已随危险实现一并移除（SuppressLock 现为"调用即抛异常"）。
    // 保留 LOCK_LOG_EVERY 供幻影等模块的日志节流复用。
    private const int LOCK_LOG_EVERY = 120;
    private static float _curHorizon;
    private static float _curVertical;
    private static int _moveRemapCount;
    // 详细相位日志开关（排查输入/移动时用）
    private static ConfigEntry<bool> _cfgPhaseLogInput = null!;

    // ---- 自由视角（按住中键）----
    private static ConfigEntry<string> _cfgFreeLookKey = null!;
    // 自由视角默认键（用户指定：改为左Alt）。只定义一次 —— 配置默认值与解析回退共用。
    private const string DEFAULT_FREE_LOOK_KEY = "LeftAlt";
    private static KeyCode _freeLookKeyCode = KeyCode.LeftAlt;
    // 上一帧是否按住（用于识别按下/松开沿）
    private static bool _freeLookHeld;

    // ---- 幻影锁定目标（分阶段）----
    private static ConfigEntry<bool> _cfgPhantomEnabled = null!;
    private static PhantomTarget? _phantom;

    // ---- 锁定目标重定向 ----
    private static ConfigEntry<bool> _cfgLockRedirect = null!;

    // ---- 技能配置探查（只读诊断）----
    // 默认键 F9：功能键不与战斗操作冲突（F 区在战斗中没有绑定）
    private const string DEFAULT_PROBE_KEY = "F9";
    private static ConfigEntry<string> _cfgSelProbeKey = null!;
    private static KeyCode _selProbeKeyCode = KeyCode.F9;
    private static bool _selProbeHeld;

    // ---- 相机组件探查（只读诊断）----
    // 与技能表探查分开的按键：相机数据量大，分开触发日志更易读
    private const string DEFAULT_CAM_PROBE_KEY = "F10";
    private static ConfigEntry<string> _cfgCamProbeKey = null!;
    private static KeyCode _camProbeKeyCode = KeyCode.F10;
    private static bool _camProbeHeld;

    // ---- 三模式切换键（用户指定：F1 原版 / F2 自由 / F3 第三人称脱离准心）----
    // 键名外置到配置，不写死；解析失败时回退到默认值并明确报错。
    private const string DEFAULT_MODE_KEY_VANILLA = "F1";
    private const string DEFAULT_MODE_KEY_FREEFLIGHT = "F2";
    private const string DEFAULT_MODE_KEY_THIRDPERSON = "F3";
    private static ConfigEntry<string> _cfgModeKeyVanilla = null!;
    private static ConfigEntry<string> _cfgModeKeyFreeFlight = null!;
    private static ConfigEntry<string> _cfgModeKeyThirdPerson = null!;
    private static KeyCode _modeKeyVanilla = KeyCode.F1;
    private static KeyCode _modeKeyFreeFlight = KeyCode.F2;
    private static KeyCode _modeKeyThirdPerson = KeyCode.F3;

    /// <summary>
    /// 把一个配置项里的键名解析为 KeyCode。非法名称时记录错误并返回回退值 ——
    /// 不静默吞掉，也不让插件因配置写错而崩。
    /// （历史上 MiddleMouse 这个名字曾导致解析失败，故显式报错很重要。）
    /// </summary>
    private static KeyCode ParseKeyCode(ConfigEntry<string> cfg, KeyCode fallback)
    {
        try
        {
            return (KeyCode)Enum.Parse(typeof(KeyCode), cfg.Value, ignoreCase: true);
        }
        catch (Exception ex)
        {
            Log.LogError($"invalid {cfg.Definition.Key} '{cfg.Value}' ({ex.Message});"
                         + $" falling back to {fallback}");
            return fallback;
        }
    }

    /// <summary>
    /// 读取一个切换键的"本帧刚按下"状态。读取失败时记录并返回 false ——
    /// 不静默吞掉（键盘读取在极少数情况下会抛）。
    /// </summary>
    private static bool PressedThisFrame(KeyCode key, string label)
    {
        try
        {
            return Input.GetKeyDown(key);
        }
        catch (Exception ex)
        {
            Log.LogError($"read mode key {label} ({key}) failed: {ex.Message}");
            return false;
        }
    }

    // ---- 虚拟瞄准锚点（第三人称脱离准心的核心构件，只在 F3 模式生效）----
    // 用户要求："放到F3去" —— 锚点只属于 ThirdPerson 模式，其余模式完全不创建。
    // 阶段 2：只造点 + 写日志，不接入任何游戏系统（见 DESIGN_FREE_AIM.md 阶段表）。
    private const float DEFAULT_ANCHOR_DISTANCE = 12f;   // ≈ 近战 CrossRange 上限（实测 8/10/12）
    // 远程的锚点距离。取值理由（用户实测反馈）：
    //   能量弹是**制导**的，锚点太近（12 米）会导致弹药绕爱迪尔打转。
    //   拉远给制导足够的前导距离即可解决。初值取"战斗实测两人距离 ~30"，
    //   因为那是游戏里远程交火的典型距离。可在配置里继续调。
    private const float DEFAULT_ANCHOR_DISTANCE_RANGED = 30f;
    private const float DEFAULT_ANCHOR_LERP = 8f;        // 轻度平滑
    private const float DEFAULT_ANCHOR_PITCH_LIMIT = 60f; // 锚点不必到正上/正下
    private static ConfigEntry<float> _cfgAnchorDistance = null!;
    private static ConfigEntry<float> _cfgAnchorDistanceRanged = null!;
    private static ConfigEntry<float> _cfgAnchorLerp = null!;
    private static ConfigEntry<float> _cfgAnchorPitchLimit = null!;
    private static VirtualAimAnchor? _aimAnchor;

    // 最近一次观察到的形态（用于"只在变化时"记录日志，避免每帧刷屏）
    private static AttackForm _lastForm = AttackForm.Unknown;

    // ---- F3 索敌与距离调节（用户玩法设计）----
    // 用户原话：「在F3模式下，滚轮调整锚点离爱迪尔的距离，
    //           长按中键会用锚点位置会变成最近的一个敌人坐标，跟死
    //           这样解决了远距离的瞄准，又能调整中近」
    //
    // ⚠️ 滚轮方案已废弃（用户实测发现冲突）：
    //   用户：「我发你的那张图片，我滚轮滑就可以不断切换，场上敌人的简单信息就在那个图片里」
    //   ⇒ **滚轮是游戏原生功能**（切换敌人信息面板 EventResources.TargetInfo /
    //     BattleManager.ShowWeakInfo），我们不能占用。
    //   实测游戏绑定表确认 `<Mouse>/scroll` 已被游戏注册。
    //   ⇒ 改用两个**独立且空闲**的键：一个拉远、一个拉近（键名外置到配置）。
    // ---- F3 三档距离（用户定稿：Q/E 循环切「远/中/近」）----
    // 用户原话：「调整距离还是改了吧，换成Q，E键，按照【远，中，近】三个形态进行切换」
    //           「Q/E 循环切档」
    //
    // ⚠️ 之前的连续调距（R/F 每按一次 ±2 米）已废弃：实测会出现
    //   "distance near -> 4.0" 连续刷屏（按住式触发 + 一路降到下限）。
    //   三档离散切换更符合"中近/远近"这个玩法意图，也不会失控。
    //
    // ⚠️ Q/E 与游戏 UI map 的 Prev/Next 同名，但那只在**菜单 UI**里生效；
    //   战斗中 UI map 不参与导航，实测无冲突。若日后发现冲突，改配置即可。
    private const string DEFAULT_TIER_UP_KEY = "Q";     // 往更远一档
    private const string DEFAULT_TIER_DOWN_KEY = "E";   // 往更近一档
    // 三档距离默认值：按实测武器射程定（近战 CrossRange 8/10/12；远程交火 ~30）
    // 近 = 8（近战最小阈值）、中 = 16、远 = 32（约等于远程交火距离的两倍余量）
    private const float DEFAULT_TIER_NEAR = 1.5f;
    private const float DEFAULT_TIER_MID = 6f;
    private const float DEFAULT_TIER_FAR = 14f;
    private static ConfigEntry<string> _cfgTierUpKey = null!;
    private static ConfigEntry<string> _cfgTierDownKey = null!;
    private static ConfigEntry<float> _cfgTierNear = null!;
    private static ConfigEntry<float> _cfgTierMid = null!;
    private static ConfigEntry<float> _cfgTierFar = null!;
    private static KeyCode _tierUpKeyCode = KeyCode.Q;
    private static KeyCode _tierDownKeyCode = KeyCode.E;
    // 当前档位索引：0=近 1=中 2=远
    private static int _tierIndex = 1;

    private const string DEFAULT_ANCHOR_FAR_KEY = "R";    // 已废弃（保留以免配置报错）
    private const string DEFAULT_ANCHOR_NEAR_KEY = "F";   // 已废弃
    private const float DEFAULT_ANCHOR_MIN_DISTANCE = 4f;    // 太近会导致弹药绕圈
    private const float DEFAULT_ANCHOR_MAX_DISTANCE = 120f;  // 太远则角度精度浪费
    private const float DEFAULT_DISTANCE_STEP = 2f;          // 每次按键调整的米数
    // 索敌半径：超过此距离的敌人不算"最近敌人"。
    // 取值理由：实测战斗双方典型距离约 30，取 100 覆盖远距离交火又不至于
    // 锁到地图另一端的敌人。已具名，便于后续实测调参。
    private const float TARGET_SEARCH_RADIUS = 100f;
    // 锁定吸附速率：越小越平滑。取 6 以避免瞬移造成的视角猛甩。
    private const float LOCK_SNAP_RATE = 6f;
    private static ConfigEntry<float> _cfgAnchorMinDistance = null!;
    private static ConfigEntry<float> _cfgAnchorMaxDistance = null!;
    private static ConfigEntry<float> _cfgDistanceStep = null!;
    private static ConfigEntry<string> _cfgAnchorFarKey = null!;
    private static ConfigEntry<string> _cfgAnchorNearKey = null!;
    private static KeyCode _anchorFarKeyCode = KeyCode.R;
    private static KeyCode _anchorNearKeyCode = KeyCode.F;
    // 运行时可变距离（滚轮调节后的结果）
    private static float _currentAnchorDistance = DEFAULT_ANCHOR_DISTANCE;
    private static bool _lockKeyHeld;
    private static CharacterControllerBase? _lockedEnemy;
    // 索敌键（用户要求：长按中键锁定最近敌人）。键名外置到配置。
    private const string DEFAULT_LOCK_AIM_KEY = "Mouse2";
    private static ConfigEntry<string> _cfgLockAimKey = null!;
    private static KeyCode _lockAimKeyCode = KeyCode.Mouse2;

    // ---- 探针已整合进 F3（用户要求"没必要单独弄一类出来"）----
    // 原先的两个独立探针键及其结论：
    //   F12 锚点实例化验证 → 结论：AddComponent<CharacterControllerBase>() **可行**，
    //        已固化为 VirtualAimAnchor 的创建路径。
    //   F11 朝向验证       → 结论：bodyFwd vs to LOCK TARGET = **0.0 度**，
    //        即身体精确朝锁定目标 ⇒ 换掉锁定目标即可让整条链朝锚点。
    // 两者的信息现在都由 F3 模式的常规日志覆盖，故独立探针已移除。

    // ---- 锁定渲染框重定向（用户问："还有就是边框渲染呢"）----
    // 实测（CAMPROBE）：F3 模式下锁定目标已指向锚点，但
    //   `LockonMarker` 的 parent 是敌方的 Spine 骨骼、世界坐标离锚点 15~39 米，
    //   `player.LockonLine`（准心线）的 Start/Finish 也同样远离锚点
    //   ⇒ 说明渲染链**不读** `LockonTargetCharacter`，改锁定目标影响不到它。
    //
    // 对策：直接把我们算出的锚点位置写进渲染对象的 transform。
    //   ⚠️ 这些对象由游戏的 Tweener 驱动（`LockonMarker.ScaleChanger/PositionChanger`），
    //      我们的写入可能与之互相覆盖 —— 故做成**可开关**的配置项，
    //      便于实测对比"我们写入"与"游戏原生"哪个效果更好。
    private static ConfigEntry<bool> _cfgMoveLockMarker = null!;
    // 渲染对象名（配置项，避免写死具体名字）
    private const string DEFAULT_LOCK_MARKER_NAME = "LockonMarker";
    private static ConfigEntry<string> _cfgLockMarkerName = null!;
    private static Transform? _lockMarkerCache;

    // ---- 虚拟光标抑制（与 OS 鼠标指针是两回事，见 VirtualCursorSuppressor.cs）----
    private static ConfigEntry<bool> _cfgSuppressVirtualCursor = null!;
    private static VirtualCursorSuppressor? _virtualCursor;

    // ---- "锁定目标 = 锚点" 的状态（用户定稿设计的核心）----
    // 用户原话：「把锁定固定到锚点那里去，然后锚点又是一个属于我们的可控准心，
    //           所以就做到了让锁定成为我们的准心」
    private static CharacterControllerBase? _savedLockTarget;   // 进 F3 前的原锁定目标
    private static bool _lockTargetOverridden;
    // "游戏把锁定目标抢回去"的累计次数。
    // 这是判断"覆盖是否被游戏重算冲掉"的客观依据 —— 不靠猜。
    private static int _lockStolenCount;

    // ---- 自由相机开关 ----
    // 用户要求："感觉那个自由操控相机可以先移出去，有点小 bug 后面修"。
    // 置 false 时：左Alt 切换模式**仍然有效**（保留空格/Shift 高度飞行），
    //             但**完全不接管相机** —— 游戏原生相机照常工作。
    //             这样"原生相机 + 上下飞"可以独立验证，不被自有相机的 bug 干扰。
    private static ConfigEntry<bool> _cfgFreeCamEnabled = null!;

    // ---- 锁定相机瞄准重定向（让相机三轴跟随敌人）----
    // 实测链条与机理见 CameraAimTuner.cs 顶部注释。核心事实：
    //   · 主力相机 LockonLong（实测 prio=100、激活 34 次）的 lookAt 是【玩家自己】
    //   · DashReady（冲刺蓄力）同样是【玩家自己】
    //   · 四个单锁相机的 lookAt 是【玩家+敌人的包围盒中心】→ 垂直分量是两人高度的平均
    // ⇒ 统一修法：LookAt 指向敌人本体，让 Aim 阶段把敌人对齐到屏幕（三轴跟随）。
    //
    // 该修法**固定启用**，且只在框架的 FreeFlight 模式下生效（原版模式完全不介入）。
    // 没有单独开关 —— 用户要求"把这个启用固定进入到自由模式中去"。
    //
    // 已删除的错误尝试（留档避免重犯）：曾用"目标组 posMode=GroupAverage + 玩家权重 0.2"
    // 想把瞄准点用权重拉向敌人 —— **实测无效**，因为真正生效的 LockonLong / DashReady
    // 根本不读目标组。相关配置 LockCameraAimTune / LockCameraPlayerWeight 一并移除。
    private static CameraAimTuner? _tunerCrossSingle;
    private static CameraAimTuner? _tunerMiddleSingle;
    private static CameraAimTuner? _tunerCrossCross;
    private static CameraAimTuner? _tunerCrossMulti;
    private static CameraAimTuner? _tunerLong;
    private static CameraAimTuner? _tunerDashReady;

    // 锁定渲染对象取证的节流计数（只在 F3 模式输出，避免刷屏）
    // 间隔取 180 帧 ≈ 3 秒 @60fps：足以看出"跟随对象有没有变化"，又不淹没日志。
    private const int LOCK_VISUAL_PROBE_EVERY = 180;
    private static int _lockVisualProbeCounter;

    // ---- 探针自动输出（用户要求：默认开启，不必手动按键）----
    // 按【帧数】而非秒数节流：不依赖 Time.deltaTime，行为在暂停/变速下更可预测。
    // 60 帧 ≈ 1 秒 @60fps —— 技能表是静态数据，即使 1 秒一次也主要是重复；
    // 相机则每 5 秒采样一次，足以覆盖"地面 → 飞高 → 冲刺"的变化过程而不刷爆日志。
    private const int AUTO_PROBE_SEL_INTERVAL_FRAMES = 600;   // ≈10 秒
    private const int AUTO_PROBE_CAM_INTERVAL_FRAMES = 300;   // ≈5 秒
    private static ConfigEntry<bool> _cfgAutoProbe = null!;
    private static int _autoProbeSelCounter;
    private static int _autoProbeCamCounter;

    // onPreCull 委托实例。必须由静态字段持有：
    // IL2CPP 委托若只被原生事件弱引用，会被 GC 回收导致回调静默失效。
    private static UnityEngine.Camera.CameraCallback? _preCullCallback;

    // 运行态
    private static int _frameCounter;
    private static float _lastHorizon = float.NaN;
    private static float _lastVertical = float.NaN;
    private static float _lastWorldY = float.NaN;
    private static int _instanceSeen;
    // 当前玩家实例。CinemachineBrain.LateUpdate 的 postfix 拿不到这个实例，
    // 只能缓存一份引用供相机修正使用（每帧由 PlayerControlCustom.Update 刷新）。
    private static PlayerControlCustom? _player;
    // 修正日志节流计数器
    private static int _aimLogCounter;
    // 虚拟相机配置报告的节流计数器
    private static int _vcamReportCounter;
    // 最近一次写入的玩家组权重（用于诊断输出）
    private static float _lastPlayerWeight = float.NaN;
    // 组权重修正的节流计数器
    private static int _groupFixLogCounter;
    // 是否启用组权重修正（配置项）
    private static ConfigEntry<bool> _cfgGroupFix = null!;

    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"{PluginName} v{PluginVersion} loading (route D: direct transform.position write)");

        _cfgSpeed = Config.Bind(
            CFG_SECTION, "VerticalSpeed", SPEED_DEFAULT,
            "Vertical speed in Unity units per second while Space (up) or LeftShift (down) is held. " +
            "Suggested range: 2 (slow) ~ 15 (fast).");

        Log.LogInfo($"config: VerticalSpeed = {_cfgSpeed.Value}");

        // 相机诊断开关：默认关（ReportCamera / ReportVCameras / ReportTargetGroups 的输出
        // 与 CameraProbe(F10) 部分重叠，但这里走的是"低频配置对比"路径，排查相机时有用）。
        _cfgCamDiag = Config.Bind(
            CFG_SECTION, "CameraDiagnostic", false,
            "Log camera aim vs lock target each sample, plus a low-frequency dump of every " +
            "virtual camera's configuration and target group. Turn on when diagnosing " +
            "the camera not following the enemy when there is a height difference.");

        _cfgAimBlend = Config.Bind(
            CFG_SECTION, "AimCorrectionBlend", AIM_BLEND_DEFAULT,
            "How strongly the camera pitch is corrected toward the lock target when " +
            "the target leaves the vertical centre of the screen. " +
            "0 = off (vanilla behaviour), 1 = always aim exactly at the target. " +
            "Addresses: when flying high the game does not pitch the camera down, " +
            "so the enemy ends up outside the view and becomes invisible.");

        Log.LogInfo($"config: AimCorrectionBlend = {_cfgAimBlend.Value}");

        _cfgAimMaxRateDegPerSec = Config.Bind(
            CFG_SECTION, "AimMaxRateDegPerSec", AIM_MAX_RATE_DEFAULT,
            "How fast the camera pitch may change, in degrees per second, while the " +
            "correction pulls the view onto the lock target. " +
            "Higher = snappier, lower = smoother. " +
            "Unlike AimCorrectionBlend this is a RATE, so large height differences " +
            "still converge (an incremental blend could not, see the log notes).");

        _cfgGroupFix = Config.Bind(
            CFG_SECTION, "GroupWeightFix", true,
            "Adaptive fix for the camera not following the enemy when flying high. " +
            "Root cause (measured): the lock-on cameras aim at TargetGroupSingle, whose " +
            "GroupCenter average is computed from TWO equal-weight members - the player " +
            "(AIdealBattle) and the enemy. So the camera aims at the MIDPOINT between them; " +
            "fly high and that midpoint sits far above the enemy. " +
            "This option lowers the player's weight toward 0 as height difference grows, " +
            "so the group centre converges on the enemy. " +
            "Weight is untouched when there is no height difference (vanilla behaviour).");

        Log.LogInfo($"config: GroupWeightFix = {_cfgGroupFix.Value}");

        // ---- 自由飞行相机（模式 B，中键切换）----
        // 设计：自己建一个 Cinemachine 虚拟相机，只在模式 B 把游戏那批相机
        // 的优先级压到 0、把我们的提到 100。双方没有共享可写状态，
        // 因此不会再出现"我改、游戏改回"的抖动。详见 FreeFlightCamera.cs。
        _cfgFreeCamSens = Config.Bind(
            CFG_SECTION, "FreeCamMouseSensitivity", MOUSE_SENS_DEFAULT,
            "Degrees of camera rotation per pixel of mouse movement in free-flight mode (mode B).");
        _cfgFreeCamPitchMin = Config.Bind(
            CFG_SECTION, "FreeCamPitchMin", PITCH_MIN_DEFAULT,
            "Lowest camera pitch in degrees in free-flight mode (negative = looking down).");
        _cfgFreeCamPitchMax = Config.Bind(
            CFG_SECTION, "FreeCamPitchMax", PITCH_MAX_DEFAULT,
            "Highest camera pitch in degrees in free-flight mode (positive = looking up).");
        _cfgFreeCamDistance = Config.Bind(
            CFG_SECTION, "FreeCamDistance", CAM_DISTANCE_DEFAULT,
            "Distance from the follow point to the camera in free-flight mode.");
        _cfgFreeCamHeight = Config.Bind(
            CFG_SECTION, "FreeCamHeight", CAM_HEIGHT_DEFAULT,
            "Height offset of the follow point, in Unity units.");
        // FreeCamToggleKey 已废弃：模式切换统一由 F1/F2/F3 承担（ModeKey* 配置）。
        // 保留一个"切换键"只会与中键/左Alt 的功能冲突（实测过：
        // 中键同时被绑成 切换模式+锁定敌人+自由视角 三件事）。
        // 这里只保留一个日志说明，方便旧配置的用户理解为何该键不再生效。

        _freeCam = new FreeFlightCamera(
            Log,
            _cfgFreeCamSens.Value,
            _cfgFreeCamPitchMin.Value,
            _cfgFreeCamPitchMax.Value,
            _cfgFreeCamDistance.Value,
            _cfgFreeCamHeight.Value,
            CAM_PRIO_ACTIVE_DEFAULT,
            CAM_PRIO_IDLE_DEFAULT);

        // 模式切换键已统一为 ModeKey*（见下）；自由摄像机参数：
        Log.LogInfo($"config: free cam sens={_cfgFreeCamSens.Value}"
                    + $" dist={_cfgFreeCamDistance.Value}");

        // 框架总开关。默认 false（用户要求：默认关，手动按键才启用）。
        // 关掉时插件完全不接管任何东西 —— 上升/下降也不生效，
        // 因为原版模式下改角色高度会与将来的敌人 AI / 战斗规则冲突。
        _cfgFrameworkEnabled = Config.Bind(
            CFG_SECTION, "FrameworkEnabled", false,
            "Master switch for the whole framework. When false the plugin takes over NOTHING: " +
            "no vertical flight, no camera, no lock suppression - the game runs untouched. " +
            "Set to true and press FreeCamToggleKey in game to take control. " +
            "Default false on purpose: height will become part of combat rules later, " +
            "so silently changing the player's altitude in vanilla mode would clash with enemy AI.");
        Log.LogInfo($"config: FrameworkEnabled = {_cfgFrameworkEnabled.Value}");

        // ---- 三模式切换键（用户指定：F1 原版 / F2 自由 / F3 第三人称脱离准心）----
        // 键名外置到配置；解析失败时回退默认值并明确报错（沿用 FreeLookHoldKey 的做法）。
        _cfgModeKeyVanilla = Config.Bind(
            CFG_SECTION, "ModeKeyVanilla", DEFAULT_MODE_KEY_VANILLA,
            "Key for VANILLA mode: the framework does nothing at all (pure game behaviour). " +
            "Unity KeyCode name.");
        _cfgModeKeyFreeFlight = Config.Bind(
            CFG_SECTION, "ModeKeyFreeFlight", DEFAULT_MODE_KEY_FREEFLIGHT,
            "Key for FREE-FLIGHT mode: vertical flight (Space/Shift) plus camera takeover, " +
            "with the lock-on system left as vanilla. Unity KeyCode name.");
        _cfgModeKeyThirdPerson = Config.Bind(
            CFG_SECTION, "ModeKeyThirdPerson", DEFAULT_MODE_KEY_THIRDPERSON,
            "Key for THIRD-PERSON free-aim mode: the reticle follows the camera's forward " +
            "direction instead of a locked enemy. ATOMIC feature - until every part of the " +
            "aim chain (facing / reticle / approach / dash / burst / movement) is redirected " +
            "together, this mode applies NO gameplay change (only logs). " +
            "See DESIGN_FREE_AIM.md. Unity KeyCode name.");
        _modeKeyVanilla = ParseKeyCode(_cfgModeKeyVanilla, _modeKeyVanilla);
        _modeKeyFreeFlight = ParseKeyCode(_cfgModeKeyFreeFlight, _modeKeyFreeFlight);
        _modeKeyThirdPerson = ParseKeyCode(_cfgModeKeyThirdPerson, _modeKeyThirdPerson);
        Log.LogInfo($"config: mode keys Vanilla={_modeKeyVanilla} FreeFlight={_modeKeyFreeFlight}"
                    + $" ThirdPerson={_modeKeyThirdPerson}");

        // ---- 虚拟瞄准锚点参数（只在 F3 第三人称模式使用）----
        _cfgAnchorDistance = Config.Bind(
            CFG_SECTION, "AnchorDistance", DEFAULT_ANCHOR_DISTANCE,
            "Distance from the character to the virtual aim anchor for MELEE (F3 mode only). " +
            "The anchor orbits the character and represents where you are aiming. " +
            "Default 12 = the largest melee CrossRange measured in-game (8/10/12).");
        _cfgAnchorDistanceRanged = Config.Bind(
            CFG_SECTION, "AnchorDistanceRanged", DEFAULT_ANCHOR_DISTANCE_RANGED,
            "Distance to the anchor for RANGED (gun) form in F3 mode. Must be much larger " +
            "than the melee value: the energy shots are HOMING, so a near anchor makes them " +
            "orbit the character instead of flying out. Default 30 = the typical player-to-enemy " +
            "distance measured during ranged combat. Raising this also makes the aim direction " +
            "less twitchy (same mouse movement covers a larger lateral distance).");
        // ---- F3 索敌与距离调节（用户定稿玩法）----
        _cfgLockAimKey = Config.Bind(
            CFG_SECTION, "LockAimKey", DEFAULT_LOCK_AIM_KEY,
            "Hold this key in F3 mode to snap the aim anchor onto the NEAREST ENEMY " +
            "(measured from the anchor's current position) and keep it locked there. " +
            "While held, the anchor no longer orbits the character, so rotation cannot " +
            "interfere with aiming. Release to go back to the character-orbit behaviour. " +
            "Mouse buttons are Mouse0/1/2 (left/right/middle). Unity KeyCode name.");
        _cfgAnchorMinDistance = Config.Bind(
            CFG_SECTION, "AnchorMinDistance", DEFAULT_ANCHOR_MIN_DISTANCE,
            "Lower bound for the anchor distance in F3 mode. Too small makes homing shots " +
            "circle the character.");
        _cfgAnchorMaxDistance = Config.Bind(
            CFG_SECTION, "AnchorMaxDistance", DEFAULT_ANCHOR_MAX_DISTANCE,
            "Upper bound for the anchor distance in F3 mode.");
        // 距离调节改用两个独立键 —— **滚轮已被游戏占用**（用户实测：滚轮会切换敌人信息面板）。
        _cfgAnchorFarKey = Config.Bind(
            CFG_SECTION, "AnchorFarKey", DEFAULT_ANCHOR_FAR_KEY,
            "Hold this key in F3 mode to INCREASE the anchor distance (aim further away). " +
            "Two separate keys are used instead of the mouse wheel because the wheel is " +
            "already taken by the game (it cycles the enemy info panel). Unity KeyCode name.");
        _cfgAnchorNearKey = Config.Bind(
            CFG_SECTION, "AnchorNearKey", DEFAULT_ANCHOR_NEAR_KEY,
            "Hold this key in F3 mode to DECREASE the anchor distance (aim closer). " +
            "Unity KeyCode name.");
        _cfgDistanceStep = Config.Bind(
            CFG_SECTION, "AnchorDistanceStep", DEFAULT_DISTANCE_STEP,
            "Metres of anchor-distance change per second while one of the distance keys is held.");
        _anchorFarKeyCode = ParseKeyCode(_cfgAnchorFarKey, _anchorFarKeyCode);
        _anchorNearKeyCode = ParseKeyCode(_cfgAnchorNearKey, _anchorNearKeyCode);

        // ---- F3 三档距离（Q/E 循环切「远/中/近」，用户定稿）----
        _cfgTierUpKey = Config.Bind(
            CFG_SECTION, "TierFarKey", DEFAULT_TIER_UP_KEY,
            "Press in F3 mode to switch the aim anchor to the FARTHER distance tier. Tiers " +
            "cycle NEAR -> MID -> FAR and stop at the ends. Unity KeyCode name.");
        _cfgTierDownKey = Config.Bind(
            CFG_SECTION, "TierNearKey", DEFAULT_TIER_DOWN_KEY,
            "Press in F3 mode to switch the aim anchor to the CLOSER distance tier. " +
            "Unity KeyCode name.");
        _cfgTierNear = Config.Bind(
            CFG_SECTION, "TierNearDistance", DEFAULT_TIER_NEAR,
            "Anchor distance for the NEAR tier, in metres. Default 8 = the smallest melee " +
            "CrossRange measured in game.");
        _cfgTierMid = Config.Bind(
            CFG_SECTION, "TierMidDistance", DEFAULT_TIER_MID,
            "Anchor distance for the MID tier, in metres.");
        _cfgTierFar = Config.Bind(
            CFG_SECTION, "TierFarDistance", DEFAULT_TIER_FAR,
            "Anchor distance for the FAR tier, in metres. Should be generous: the energy shots " +
            "are HOMING, so a short distance makes them circle the character.");
        _tierUpKeyCode = ParseKeyCode(_cfgTierUpKey, _tierUpKeyCode);
        _tierDownKeyCode = ParseKeyCode(_cfgTierDownKey, _tierDownKeyCode);
        _tierIndex = 1;                                   // 默认中档
        _currentAnchorDistance = TierDistance(_tierIndex);
        Log.LogInfo($"config: tiers - far={_tierUpKeyCode} near={_tierDownKeyCode}"
                    + $", distances NEAR={_cfgTierNear.Value:F0} MID={_cfgTierMid.Value:F0}"
                    + $" FAR={_cfgTierFar.Value:F0}, start={TierName(_tierIndex)}");

        // ---- 垂直飞行键（用户 2026-10-16 键位表：space/上升，ctrl/下降）----
        // 为什么下降不再是 Shift：用户键位表里 **Shift 要给冲刺**，下降让到 Ctrl。
        _cfgAscendKey = Config.Bind(
            CFG_SECTION, "AscendKey", DEFAULT_ASCEND_KEY,
            "Hold to fly UP in FreeFlight (F2) mode. Unity KeyCode name. " +
            "User key map: space/ascend, ctrl/descend, shift/dash.");
        _cfgDescendKey = Config.Bind(
            CFG_SECTION, "DescendKey", DEFAULT_DESCEND_KEY,
            "Hold to fly DOWN in FreeFlight (F2) mode. Unity KeyCode name. " +
            "Moved off Shift so that Shift is free for the dash (user key map).");
        _ascendKeyCode = ParseKeyCode(_cfgAscendKey, _ascendKeyCode);
        _descendKeyCode = ParseKeyCode(_cfgDescendKey, _descendKeyCode);

        // ---- 冲刺键（用户键位表：shift/冲刺）----
        // ⚠️ 默认 None = 不接管：原版实测 DashStart / DashIm / DashWay 三个入口全部零命中，
        //    冲刺进入点尚未定位。没定位就不接管 —— 避免重犯"休息姿态"那种
        //    "没定位就反射调用"的错。
        _cfgDashKey = Config.Bind(
            CFG_SECTION, "DashKey", DEFAULT_DASH_KEY,
            "Press to dash (the game's own DashStart). DEFAULT 'None' = do NOT take over " +
            "this key yet. Set to 'LeftShift' (Unity KeyCode name) to make Shift dash, " +
            "per the user's key map: space/ascend, ctrl/descend, shift/dash.");
        _dashKeyCode = ParseKeyCode(_cfgDashKey, _dashKeyCode);

        // 疾飞入口（用户实测反馈：要"疾飞"不要"瞬移"）：
        //   ✗ 第一版 DashStart()       = 一次性冲刺 → 像瞬移
        //   ✓ 本版  ForceBooston/off() = 持续加力（BOOST_STATUS 状态机 + 循环音 + AssistSpeedMagni）
        // 两者都是 **public**，不需要反射 —— 顺带减少了一处失败面。
        Log.LogInfo("[BOOST] fast-flight entry = PlayerControlCustom.ForceBooston/ForceBoostoff"
                    + $" (both public), key={_dashKeyCode}");
        Log.LogInfo($"config: vertical - ascend={_ascendKeyCode} descend={_descendKeyCode}"
                    + $", dash={_dashKeyCode} (None = not taken over)");

        // ---- 虚拟光标抑制（用户实测：战斗中一直有虚拟光标，绝对不能有）----
        _cfgSuppressVirtualCursor = Config.Bind(
            CFG_SECTION, "SuppressVirtualCursor", true,
            "Hide the UI-package virtual cursor (Michsky.UI.Shift.VirtualCursor.cursorObject) " +
            "while a non-vanilla mode is active. This is a SEPARATE thing from the OS mouse " +
            "pointer: it is a UI object that stays rendered by the event system, which is why " +
            "hiding the system cursor did not remove it. Restored when returning to vanilla.");
        _virtualCursor = new VirtualCursorSuppressor(Log);

        // ---- 相机距离缩放 ----
        _cfgCamDistScale = Config.Bind(
            CFG_SECTION, "CameraDistanceScale", DEFAULT_CAM_DIST_SCALE,
            "Scale applied to the follow-distance of the ACTIVE lock-on cameras in F3 mode, so " +
            "the view sits closer to the character (user: 'the view is too far'). 1.0 = leave " +
            "the game's own distance alone. Only cameras using CinemachineTransposer are " +
            "affected - the active lock cameras (LockonLong / DashReady) are the ones that " +
            "matter; FramingTransposer cameras keep their own camDist.");
        _camDistTuner = new CameraDistanceTuner(Log);
        Log.LogInfo($"config: CameraDistanceScale = {_cfgCamDistScale.Value:F2}"
                    + " (applies to active Transposer cameras in F3)");

        // 注：曾经在此绑定"休息姿态"到 `DashReadyFirst()` —— **已拆除**。
        //   原因（用户指出）：「你搞错冲刺了，不是这个姿态，那个是左键的长按姿态，
        //   不是我说的休息冲刺」。
        //   `DashReady` 是**左键长按蓄力**姿态，不是"打赢每波敌人后的休息姿态"。
        //   误绑后果严重：它还拦截了 `DashCancel`，会破坏游戏正常的蓄力取消逻辑。
        //   ▸ 真正的休息姿态待定位，线索：**以爱迪尔为中心、相机在身后**（疑似 VCameraMain）。
        Log.LogInfo($"config: SuppressVirtualCursor = {_cfgSuppressVirtualCursor.Value}");

        // ---- 锁定抢占保险（用户要求："锚点渲染必须加保险"）----
        _cfgLockTargetOverride = Config.Bind(
            CFG_SECTION, "LockTargetOverride", true,
            "Whether F3 mode may take over the game's lock-target field so that the lock, the " +
            "reticle, the body facing and the attacks all follow our aim anchor. " +
            "Turn this OFF if a character was already bound (the lock marker is attached to an " +
            "enemy until it dies) and the anchor keeps losing the fight over the lock - the " +
            "anchor will still orbit/lock-on visually, but will stop competing for the field.");

        // ---- 锁定渲染框重定向 ----
        _cfgMoveLockMarker = Config.Bind(
            CFG_SECTION, "MoveLockMarker", false,
            "Move the game's lock-on marker (the frame drawn around the locked target) onto " +
            "our aim anchor every frame while in F3 mode. Needed because the marker does NOT " +
            "follow the lock target field - it stays wherever the game's own tween puts it. " +
            "Off by default: the game drives it with a Tweener and our writes may fight with it, " +
            "so enable to compare both behaviours in game.");
        _cfgLockMarkerName = Config.Bind(
            CFG_SECTION, "LockMarkerObjectName", DEFAULT_LOCK_MARKER_NAME,
            "Name of the lock-on marker GameObject to reposition when MoveLockMarker is on. " +
            "Default matches the object the camera probe found (parented under the enemy's Spine).");
        _lockAimKeyCode = ParseKeyCode(_cfgLockAimKey, _lockAimKeyCode);
        Log.LogInfo($"config: F3 targeting - LockAimKey={_cfgLockAimKey.Value} -> {_lockAimKeyCode}"
                    + $", farKey={_anchorFarKeyCode} nearKey={_anchorNearKeyCode}"
                    + $", step={_cfgDistanceStep.Value:F1}m/s"
                    + $", distanceRange=[{_cfgAnchorMinDistance.Value:F0},{_cfgAnchorMaxDistance.Value:F0}]");
        _cfgAnchorLerp = Config.Bind(
            CFG_SECTION, "AnchorLerpSpeed", DEFAULT_ANCHOR_LERP,
            "Smoothing rate for the anchor position (F3 mode only). Higher = snappier, " +
            "lower = smoother but laggier. Mirrors Cinemachine/CameraHolder lerp usage.");
        _cfgAnchorPitchLimit = Config.Bind(
            CFG_SECTION, "AnchorPitchLimit", DEFAULT_ANCHOR_PITCH_LIMIT,
            "Maximum upward/downward angle in degrees for the anchor (F3 mode only). " +
            "Keeps the anchor from flipping over the character's head. " +
            "Same idea as CameraHolder.yMinLimit / yMaxLimit in the game's own orbit camera.");
        _aimAnchor = new VirtualAimAnchor(Log, _cfgAnchorLerp.Value,
                                         -_cfgAnchorPitchLimit.Value, _cfgAnchorPitchLimit.Value,
                                         _cfgFreeCamSens.Value);
        Log.LogInfo($"config: AnchorDistance = {_cfgAnchorDistance.Value:F1}"
                    + $", AnchorLerpSpeed = {_cfgAnchorLerp.Value:F1}"
                    + $", AnchorPitchLimit = ±{_cfgAnchorPitchLimit.Value:F0}");

        // 独立探针已整合（用户要求"没必要单独弄一类出来"）：
        //   · 锚点实例化验证 → 结论已固化进 VirtualAimAnchor（AddComponent 可行）
        //   · 朝向验证       → 结论已固化进"锁定目标=锚点"（身体精确朝锁定目标，0.0 度）
        // 两者的信息现在由 F3 模式的常规运行日志覆盖。

        // 移动管线追踪（纯观测）。用于反推"输入 -> Move -> 世界位移"的真实映射，
        // 避免再次凭语义猜公式（上次就是这么把移动方向搞乱的）。
        var cfgMoveTrace = Config.Bind(
            CFG_SECTION, "MoveTraceEnabled", false,
            "Log the movement pipeline once per second (input direction, Move direction, " +
            "actual world displacement, camera direction). Read-only observation used to " +
            "derive the correct camera-relative mapping. Off by default.");
        MoveTrace.Init(Log, cfgMoveTrace);        Log.LogInfo($"config: MoveTraceEnabled = {cfgMoveTrace.Value}");

        // ---- 自由视角（按住中键）----
        // 语义（用户设计）：按住中键可以在**不改变爱迪尔移动方向**的前提下转动相机观察周围。
        // 与"模式 B"的区别：这是临时的、松开即还原，且不碰光标/锁定/移动。
        _cfgFreeLookKey = Config.Bind(
            CFG_SECTION, "FreeLookHoldKey", DEFAULT_FREE_LOOK_KEY,
            "Hold this key to swing the camera around the player without changing her " +
            "movement direction (like the free-look in battle royale games). " +
            "Must be a Unity KeyCode name; mouse buttons are Mouse0/1/2 (left/right/middle). " +
            "Release to snap back to the game camera. Affects the camera only - " +
            "movement, lock-on and attacks are untouched.");
        Log.LogInfo($"config: FreeLookHoldKey = {_cfgFreeLookKey.Value}");

        // ---- 幻影锁定目标（分阶段，默认关）----
        // 阶段 1：只在按住自由视角键时创建一个隐形 CharacterControllerBase 并跟随视线，
        //         **完全不接入锁定系统** —— 用于验证"创建它是否安全"。
        // 阶段 2（未启用）：接入锁定。风险与做法见 PhantomTarget.cs 尾部注释。
        _cfgPhantomEnabled = Config.Bind(
            CFG_SECTION, "PhantomTargetEnabled", false,
            "STAGED FEATURE - default off. Stage 1 only: while the free-look key is held, " +
            "create an invisible CharacterControllerBase that follows where the camera looks. " +
            "It does NOT touch the lock-on system yet - the purpose is to verify that " +
            "creating such an object is harmless to the battle system. " +
            "Only enable when you are ready to test that.");
        _phantom = new PhantomTarget(Log);
        Log.LogInfo($"config: PhantomTargetEnabled = {_cfgPhantomEnabled.Value}");

        // ---- 锁定目标重定向：锁最接近相机视线的敌人 ----
        // 这是"自由瞄准"的实现方式：不造幻影、不写战斗状态字段，
        // 只在游戏选出候选列表后调整顺序。详见 LockTargetRedirect.cs。
        _cfgLockRedirect = Config.Bind(
            CFG_SECTION, "LockRedirectEnabled", false,
            "Redirect lock-on to the enemy closest to the CAMERA's forward direction " +
            "instead of the nearest one. Implemented by reordering the candidate list that " +
            "the game's GetLockonEnemys() returns - no objects created, no battle state " +
            "fields written. This is what enables aiming bursts wherever you look. " +
            "Off by default; enable to test.");
        LockTargetRedirect.Init(Log);
        Log.LogInfo($"config: LockRedirectEnabled = {_cfgLockRedirect.Value}");

        // ---- 技能配置探查（只读）----
        // 反编译已确认 player.Selection : SelectionManager 持有全部技能配置表，
        // 但**运行时实际内容未知**（反编译只看得到结构，看不到数据）。
        // 此探查把真实数据打印出来 —— 在读到数据之前不做任何修改，
        // 这是"先取证再动手"的落实（也是从之前凭推断改坏游戏中吸取的教训）。
        _cfgSelProbeKey = Config.Bind(
            CFG_SECTION, "SelectionProbeKey", DEFAULT_PROBE_KEY,
            "Press this key in battle to dump the player's skill configuration tables " +
            "(player.Selection: AttackList / ShotList / DashList / AssistList ...). " +
            "Read-only diagnostic: prints counts, keys and range thresholds so the REAL " +
            "data layout can be seen before anything is changed. Unity KeyCode name.");
        SelectionProbe.Init(Log);
        try
        {
            _selProbeKeyCode = (KeyCode)Enum.Parse(typeof(KeyCode), _cfgSelProbeKey.Value, ignoreCase: true);
        }
        catch (Exception ex)
        {
            Log.LogError($"invalid SelectionProbeKey '{_cfgSelProbeKey.Value}' ({ex.Message}); "
                         + $"falling back to {_selProbeKeyCode}");
        }
        Log.LogInfo($"config: SelectionProbeKey = {_cfgSelProbeKey.Value} -> {_selProbeKeyCode}");

        // ---- 自由相机开关（用户要求"先移出去，小 bug 后面修"）----
        // 关掉后：左Alt 仍切换模式（高度飞行保留），但相机完全交给游戏。
        // 默认 true 以保持当前行为；测试原生相机时设为 false。
        _cfgFreeCamEnabled = Config.Bind(
            CFG_SECTION, "FreeCamEnabled", true,
            "When false, the mode switch (default LeftAlt) still enables vertical flight " +
            "(Space/Shift) but does NOT take over the camera at all - the game's own " +
            "Cinemachine cameras keep working normally. Use this to test 'native camera + " +
            "fly up/down' in isolation, without the custom camera's quirks interfering.");
        Log.LogInfo($"config: FreeCamEnabled = {_cfgFreeCamEnabled.Value}");

        // ---- 锁定相机瞄准重定向（固定启用，只在 FreeFlight 生效）----
        // 用户要求："把这个启用固定进入到自由模式中去" —— 因此没有开关，
        // 由 SwitchMode 的模式判断控制（Vanilla 模式完全不介入）。
        // 机理与实测依据见 CameraAimTuner.cs 顶部注释。
        _tunerCrossSingle = new CameraAimTuner(Log);
        _tunerMiddleSingle = new CameraAimTuner(Log);
        _tunerCrossCross = new CameraAimTuner(Log);
        _tunerCrossMulti = new CameraAimTuner(Log);
        _tunerLong = new CameraAimTuner(Log);
        _tunerDashReady = new CameraAimTuner(Log);
        Log.LogInfo("config: lock camera aim redirect = ALWAYS ON in FreeFlight mode"
                    + " (LockonLong / DashReady / 4 single-lock cams -> LookAt enemy)");

        // ---- 相机组件探查（只读）----
        // 反编译已确认锁定相机用 CinemachineGroupComposer 把"玩家+敌人"框进画面，
        // 而它的 m_MaxDollyOut / m_MaximumDistance / m_MaximumFOV 上限正是
        // "离远了就不再缩放"的原因。这些是运行时组件参数，可直接读 —— 先读再改。
        _cfgCamProbeKey = Config.Bind(
            CFG_SECTION, "CameraProbeKey", DEFAULT_CAM_PROBE_KEY,
            "Press this key in battle to dump every virtual camera and target group " +
            "(priorities, look-at/follow, Composer screen zones, Transposer offset/damping, " +
            "GroupComposer framing size and its dolly/FOV LIMITS, target group members and " +
            "bounding box). Read-only: this is how the camera's Z-axis / zoom-limit behaviour " +
            "gets measured before anything is changed. Unity KeyCode name.");
        CameraProbe.Init(Log);
        try
        {
            _camProbeKeyCode = (KeyCode)Enum.Parse(typeof(KeyCode), _cfgCamProbeKey.Value, ignoreCase: true);
        }
        catch (Exception ex)
        {
            Log.LogError($"invalid CameraProbeKey '{_cfgCamProbeKey.Value}' ({ex.Message}); "
                         + $"falling back to {_camProbeKeyCode}");
        }
        Log.LogInfo($"config: CameraProbeKey = {_cfgCamProbeKey.Value} -> {_camProbeKeyCode}");

        // ---- 探针自动输出开关（用户要求默认开启）----
        // 打开后不必按键：探针会按固定帧间隔自动把数据写进日志，
        // 于是"边玩边取证"成立 —— 从地面到飞高到冲刺的全过程都被采样。
        // 仍保留 F9 / F10 手动触发（用于精确抓某一刻）。
        _cfgAutoProbe = Config.Bind(
            CFG_SECTION, "ProbeAutoDump", true,
            "Automatically write the diagnostic dumps to the log at a fixed frame interval, " +
            "without pressing any key. Lets the data be captured while playing normally " +
            "(ground -> fly high -> dash) instead of requiring a keypress at the right moment. " +
            "The F9 / F10 keys still work for catching one exact moment. Read-only either way.");
        Log.LogInfo($"config: ProbeAutoDump = {_cfgAutoProbe.Value}"
                    + $" (sel every {AUTO_PROBE_SEL_INTERVAL_FRAMES} frames,"
                    + $" cam every {AUTO_PROBE_CAM_INTERVAL_FRAMES} frames)");

        // 详细相位日志：默认关（平时不需要看每帧输入/重映射细节）
        _cfgPhaseLogInput = Config.Bind(
            CFG_SECTION, "VerbosePhaseLog", false,
            "Log per-frame input and camera-relative move remapping details. " +
            "Off by default; turn on when diagnosing movement direction issues.");

        // 自由视角键名解析（非法时回退默认并明确报错）
        try
        {
            _freeLookKeyCode = (KeyCode)Enum.Parse(typeof(KeyCode), _cfgFreeLookKey.Value, ignoreCase: true);
        }
        catch (Exception ex)
        {
            Log.LogError($"invalid FreeLookHoldKey '{_cfgFreeLookKey.Value}' ({ex.Message}); "
                         + $"falling back to {_freeLookKeyCode}");
        }
        Log.LogInfo($"free look hold key resolved to {_freeLookKeyCode}");

        // ---- 失焦不暂停加载 ----
        // 实测：游戏失去窗口焦点时资源加载会被挂起（Player.log 停止增长、CPU 归零、
        // 窗口仍响应）。用户报告"切屏就卡在加载界面"。Unity 的官方开关就是它。
        // 放在 Load 里设置一次即可（静态属性，全局生效）。
        try
        {
            bool before = Application.runInBackground;
            Application.runInBackground = true;
            Log.LogInfo($"Application.runInBackground: {before} -> {Application.runInBackground}");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"set runInBackground failed: {ex.Message}");
        }

        var harmony = new Harmony(PluginGuid);
        int patched = PatchPostfix(harmony,
            AccessTools.Method(typeof(PlayerControlCustom), "Update"),
            nameof(Postfix_Update),
            "PlayerControlCustom.Update");

        patched += PatchPrefix(harmony,
            AccessTools.Method(typeof(PlayerControlCustom), "EvaluateMoveAndAttack"),
            nameof(Prefix_EvaluateMoveAndAttack),
            "PlayerControlCustom.EvaluateMoveAndAttack");

        // 注：曾在此注册 `CharacterControllerBase.DashCancel` 的拦截补丁来"钉住休息姿态"，
        //   **已拆除** —— `DashReady` 其实是左键长按蓄力姿态（用户指正），
        //   拦截 `DashCancel` 会破坏游戏正常的蓄力取消逻辑，风险高于收益。

        // 玩家弹药"没锁定就直线飞"（用户定稿）。
        // 挂在 InitShot 上是因为它自带 isPlayer 参数，能精确区分玩家/敌人弹药。
        // 判定依据：LockonTargetCharacter == 我们的锚点 ⇒ 没有真实敌人锁定。
        patched += PatchPostfix(harmony,
            AccessTools.Method(typeof(ShotController), "InitShot"),
            nameof(Postfix_InitShot),
            "ShotController.InitShot (no-lock straight flight)");

        // 动作流取证：找出"冲刺姿态"（每波战斗后自动进入）对应的流。
        // 见 Postfix_StartFlow / LogFlowSnapshot 的注释。
        patched += PatchPostfix(harmony,
            AccessTools.Method(typeof(ActionFlowManager), "StartFlow"),
            nameof(Postfix_StartFlow),
            "ActionFlowManager.StartFlow (flow logging)");
        // 波次结束出口 —— 上轮只挂 StartFlow 抓不到波次切换（它只在进章节时触发一次），
        // 这两个是"一波打完"的真正出口。
        patched += PatchPostfix(harmony,
            AccessTools.Method(typeof(ActionFlowManager), "ActionFinish"),
            nameof(Postfix_ActionFinish),
            "ActionFlowManager.ActionFinish (wave end logging)");
        patched += PatchPostfix(harmony,
            AccessTools.Method(typeof(ActionFlowManager), "ActionFinishForce"),
            nameof(Postfix_ActionFinishForce),
            "ActionFlowManager.ActionFinishForce (wave end logging)");

        // 冲刺取证：原版战斗里 DashStart 会不会被调用（纯只读）。
        // 先取证再动手 —— 上一个功能（休息姿态）就是因为没取证直接反射调用而搞错了对象。
        // 实测结果：**原版一条 [DASH] 都没有** ⇒ 冲刺不走 DashStart，因此追加下面两个探针。
        patched += PatchPostfix(harmony,
            AccessTools.Method(typeof(PlayerControlCustom), "DashStart"),
            nameof(Postfix_DashStart),
            "PlayerControlCustom.DashStart (dash logging)");

        // 位移管线挂钩（银龙奥卡提醒：去反编译逆向）——
        // 见 Postfix_HoldMoveForceAccu 的逆向结论：位移走 ApproachForm / HoldMoveForce*，
        // 不走 MoveControlHelper。挂钩为纯只读，用于确定加力实际走哪条路。
        patched += PatchMoveForcePipeline(harmony);

        // 冲刺取证（续）：DashIm（瞬时冲刺）与 DashWay（冲刺方向）——
        // DashStart 实测无命中，这两个是剩余最可能的按键入口。
        patched += PatchPostfix(harmony,
            AccessTools.Method(typeof(PlayerControlCustom), "DashIm"),
            nameof(Postfix_DashIm),
            "PlayerControlCustom.DashIm (dash logging)");
        patched += PatchPostfix(harmony,
            AccessTools.Method(typeof(PlayerControlCustom), "DashWay"),
            nameof(Postfix_DashWay),
            "PlayerControlCustom.DashWay (dash logging)");

        // 全屏循环光标：拦掉原版"鼠标一动就呼出光标"的两个调用。
        // 用户澄清：「原版游戏移动鼠标呼出光标!」—— 那是原版设计，
        // 但在我们的模式下会让光标乱飘误触 UI，所以非原版模式下一律拦掉。
        patched += PatchPrefix(harmony,
            AccessTools.Method(typeof(GameManagerBase), "CursorShow"),
            nameof(Prefix_CursorShow),
            "GameManagerBase.CursorShow (full-screen loop cursor)");
        patched += PatchPrefix(harmony,
            AccessTools.Method(typeof(GameManagerBase), "MoveCursorToBottomRight"),
            nameof(Prefix_MoveCursorToBottomRight),
            "GameManagerBase.MoveCursorToBottomRight (full-screen loop cursor)");

        // 锁定目标重定向：在候选列表产生之后调整顺序（见方法注释）
        patched += PatchPostfix(harmony,
            AccessTools.Method(typeof(PlayerControlCustom), "GetLockonEnemys"),
            nameof(Postfix_GetLockonEnemys),
            "PlayerControlCustom.GetLockonEnemys");

        // 相机俯仰修正：注册到 Camera.onPreCull（Unity 保证的渲染前最后回调）。
        //
        // 为什么不用 CinemachineBrain.LateUpdate 的 postfix：
        //   实测在 brain 的 postfix 里把相机转到 -0.937°，下一帧读回仍是 +0.063°
        //   —— 每帧被重置，说明 LateUpdate 期间还有别的东西写相机。
        //   onPreCull 在所有 Update/LateUpdate 之后触发，此处写入不被覆盖。
        int camHook = 0;
        try
        {
            // CameraCallback 是 IL2CPP 委托类型，其构造函数要的是原生方法指针（IntPtr），
            // 托管方法组无法直接构造。正规做法是经 DelegateSupport.ConvertDelegate 转换。
            // 同时必须由静态字段长期持有：委托若只被原生事件弱引用会被 GC 回收。
            var managed = new Action<UnityEngine.Camera>(OnPreCull);
            _preCullCallback =
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Camera.CameraCallback>(managed);
            if (_preCullCallback is null)
            {
                Log.LogError("ConvertDelegate returned null - camera aim correction not hooked.");
            }
            else
            {
                UnityEngine.Camera.onPreCull += _preCullCallback;
                camHook = 1;
                Log.LogInfo("registered: Camera.onPreCull (camera aim correction)");
            }
            // 有意不注销：OnPreCull 是静态方法，插件程序集在进程存活期间一直加载，
            // 回调不会悬空。BepInEx 插件也不支持运行期卸载，注销没有实际收益。
        }
        catch (Exception ex)
        {
            Log.LogError($"register onPreCull failed: {ex.Message}");
        }

        Log.LogInfo($"patched {patched}/2 harmony + cameraHook={camHook}");
        if (patched == 0)
        {
            Log.LogError("No patches applied - plugin is inert.");
        }
        else
        {
            Log.LogInfo("Hold SPACE to ascend, LEFT SHIFT to descend. Neither = vanilla.");
        }
    }

    private static int PatchPostfix(Harmony harmony, System.Reflection.MethodInfo? target,
                                    string patchName, string label)
    {
        if (target is null)
        {
            Log.LogError($"method not found: {label}");
            return 0;
        }
        try
        {
            harmony.Patch(target,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(FlightPlugin), patchName)));
            Log.LogInfo($"patched(postfix): {label}");
            return 1;
        }
        catch (Exception ex)
        {
            Log.LogError($"patch failed {label}: {ex}");
            return 0;
        }
    }

    private static int PatchPrefix(Harmony harmony, System.Reflection.MethodInfo? target,
                                   string patchName, string label)
    {
        if (target is null)
        {
            Log.LogError($"method not found: {label}");
            return 0;
        }
        try
        {
            harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(FlightPlugin), patchName)));
            Log.LogInfo($"patched(prefix): {label}");
            return 1;
        }
        catch (Exception ex)
        {
            Log.LogError($"patch failed {label}: {ex}");
            return 0;
        }
    }

    // ---- 垂直飞行键（用户 2026-10-16 给的最终键位表）----
    // 用户原话：
    //   「右键/闪避，长按防御」
    //   「左键/普攻，蓄力突进，抓取」
    //   「shift/冲刺」
    //   「ctrl/下降」
    //   「space/上升」
    // 键名外置到配置；这里只放默认值与解析后的 KeyCode。
    private const string DEFAULT_ASCEND_KEY = "Space";
    private const string DEFAULT_DESCEND_KEY = "LeftControl";
    private static ConfigEntry<string> _cfgAscendKey = null!;
    private static ConfigEntry<string> _cfgDescendKey = null!;
    private static KeyCode _ascendKeyCode = KeyCode.Space;
    private static KeyCode _descendKeyCode = KeyCode.LeftControl;

    // ---- 冲刺（用户键位表：Shift/冲刺）----
    // 实测事实（NOTES.md 的按键体检）：
    //   · 游戏原生的冲刺/闪避是 `Avoid` action，**绑在鼠标右键**（不是 Shift）
    //   · **Shift 在游戏里是空键** ⇒ 想让 Shift 触发冲刺，必须我们主动调游戏 API
    // 因此本键的默认值先设为 None（不接管），待 API 入口确认后再启用 ——
    // 避免在没搞清入口的情况下乱调，重犯"休息姿态"那种错。
    private const string DEFAULT_DASH_KEY = "LeftShift";
    private static ConfigEntry<string> _cfgDashKey = null!;
    private static KeyCode _dashKeyCode = KeyCode.LeftShift;

    /// <summary>
    /// 疾飞（用户键位表：`shift/冲刺`）—— **持续加力**，不是一次性冲刺。
    ///
    /// ── 用户实测反馈（决定了用哪个 API）───────────────────────────────────
    ///   用户原话：「不能长按，或者说速度太快了？总而言之**跟瞬移没区别，我要的是疾飞**」
    ///   ⇒ 第一版我用 `DashStart()`（一次性冲刺）——**方向错了**，那是瞬移。
    ///
    /// ── 反编译确认的"持续加力"系统 ─────────────────────────────────────────
    ///   `PlayerControlCustom`：
    ///       protected BOOST_STATUS BoostStatus;   // ★ 状态机（持续状态，非一次性动作）
    ///       protected int   Boostpoint;           // ★ 加力点数（燃料）
    ///       protected int   BoostpointMax / BoostpointMin;
    ///       protected float AssistSpeedMagni;     // ★ 速度倍率（决定"疾飞"多快）
    ///       public AudioSource HighboostLoopAudio; // ★ **循环**音 —— 持续状态的证据
    ///       public void ForceBooston();           // ★ 开始加力
    ///       public void ForceBoostoff();          // ★ 结束加力
    ///       public override void BoostPlus();     // 补充加力点
    ///       public void ZXBoostMode(bool expand);
    ///
    ///   ⇒ 正确做法 = **按住开着加力 / 松开关掉**（与用户"长按"的直觉一致），
    ///     速度由游戏自己的 `AssistSpeedMagni` 与加成点数决定，
    ///     我们**不自己造位移**，避免与游戏物理打架。
    ///
    /// ── 与第一版的关键区别 ─────────────────────────────────────────────────
    ///   第一版：`GetKeyDown` → `DashStart()`    = 一次性冲刺（瞬移感）
    ///   本版：  `GetKey`     → `ForceBooston()` = 持续加力（疾飞）
    ///          松开        → `ForceBoostoff()`
    ///   两者都是 `public`，不需要反射、不需要拦截任何东西。
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 用 `GetKey`（长按语义），配合 `_boostActive` 保证"开/关"各只调一次
    ///   · `DashKey = None` 时完全不接管
    ///   · 任一调用失败即停用并留痕，不每帧重试刷屏
    /// </summary>
    private static void UpdateDash(PlayerControlCustom player)
    {
        if (_dashKeyCode == KeyCode.None || _dashUnavailable)
        {
            return;   // 未接管 / 已失败
        }
        try
        {
            bool held = Input.GetKey(_dashKeyCode);
            if (held && !_boostActive)
            {
                // ★★★ 点火**只调一次** —— 这是修掉"特效每帧叠加"的关键。
                //
                // ── 实测证据（用户实测后我读日志算出来的）──────────────────────
                //   `[BOOST] ForceBoostoff() ... (re-applied 78 / 62 / 55 / 35 / 143 / 323 time(s))`
                //   ⇒ 一次按住最多调了 **323 次** `ForceBooston()`。
                //   而 `ForceBooston` 内部会生成喷气特效 ⇒ **每帧一个新的特效**，叠成一坨。
                //
                // ── 为什么上一版的"状态守卫"无效 ────────────────────────────────
                //   我加了 `if (BoostStatus != FORWARD_BOOST) ForceBooston();` 作为守卫，
                //   但探针实测 `BoostStatus` **永远是 `IDLE_NONBOOST`**（23/23 次采样）
                //   ⇒ 守卫恒为真 ⇒ 依然每帧点火。守卫等于没写。
                //
                // ── 正解（回退到银龙奥卡指出的那个干净版本）─────────────────────
                //   点火**只调一次**，守卫用**我们自己的** `_boostActive`
                //   （不依赖游戏的 `BoostStatus` —— 实测它恒为 IDLE_NONBOOST，守卫等于没写）。
                //   之后**不再有任何反复调用**：喷气特效只生成一次。
                //   加力是否能自行维持，由游戏决定；本插件不强行维持
                //   （强行维持就会重复生成特效，正是之前那个 bug）。
                player.ForceBooston();
                _boostActive = true;
                _boostOnCount++;
                Log.LogInfo($"[BOOST] fast flight engaged (session #{_boostOnCount})"
                            + " - ignition called ONCE");
                // 记录点火时的燃料，便于判断"烧光"是否影响持续
                _boostPointAtIgnition = ReflectionString(player, "Boostpoint");
            }
            else if (!held && _boostActive)
            {
                // 结束疾飞
                player.ForceBoostoff();
                _boostActive = false;
                Log.LogInfo($"[BOOST] ForceBoostoff() - fast flight OFF"
                            + $" (ignition was called once; fuel at ignition was"
                            + $" {_boostPointAtIgnition})");
            }

            // 加力速度取证（用户实测"移速没变化" —— 先定位是哪个字段控制位移速度）
            ProbeBoostSpeed(player);
        }
        catch (Exception ex)
        {
            // 调用失败就停用，避免每帧抛异常刷屏（但必须留痕）
            _dashUnavailable = true;
            _boostActive = false;
            Log.LogError($"[BOOST] ForceBooston/off failed, feature disabled: {ex}");
        }
    }

    // 注：曾有一个 `_boostReapplyCount` 用于统计"重施加"次数。
    //   该机制**已彻底移除**（重施加就是特效叠加的根因，见 UpdateDash 注释）。
    //   计数器的证据价值已由日志里的 `(re-applied N time(s))` 完成取证。

    /// <summary>
    /// 加力期间是否"暂停接管锁定"（用户指出的关键条件）。
    ///
    /// ── 用户原话 ───────────────────────────────────────────────────────────
    ///   「**加力/原版流程是在没有锁定没有敌人的情况下才有**」
    ///
    /// ── 为什么这是根因 ─────────────────────────────────────────────────────
    ///   F3 模式下我们一直把 `LockonTargetCharacter` 写成锚点
    ///   （日志证据：`reclaim(lock released): field=rewritten`）
    ///   ⇒ 游戏认为"有锁定目标" ⇒ **不进加力流程**
    ///   ⇒ `BoostStatus` 掉回 `IDLE_NONBOOST`（探针：43/45 次采样都是它）
    ///   ⇒ 特效一闪就没 / 位移没变化 / 反复转身。
    ///
    /// ── ⚠️ 为什么不是"把锁定置 null"（差点重犯的血泪错误）──────────────────
    ///   写 `LockonTargetCharacter = null` 在历史上**炸掉过整个战斗系统**
    ///   （敌人 AI 消失 / 锁定失效 / 战斗 UI 消失）。
    ///   ⇒ 本实现改用**只读标记**：加力期间 `ReclaimLockTarget` 直接返回、
    ///     一个字节都不写那个字段。游戏自然保持它自己的锁定状态。
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 纯标记，不碰任何游戏字段
    /// </summary>
    public static bool BoostLockPaused => _boostActive;

    private static bool _boostActive;
    private static int _boostOnCount;
    private static bool _dashUnavailable;
    // 点火那一刻的燃料值（诊断用）：`Boostpoint` 若被烧光，加力就维持不住。
    // 实测记录：点火前后曾观察到 7/7 → 6/7 → 2/7，说明反复点火会烧燃料。
    private static string _boostPointAtIgnition = "?";

    /// <summary>
    /// 加力位移管线的挂钩取证（银龙奥卡提醒：**去反编译逆向，不要猜**）。
    ///
    /// ── 逆向结论（静态可读的部分全部读出）────────────────────────────────
    ///   位移**不**走 `MoveControlHelper`（那个类的 `MoveSpeedMultiplier` 是 `private`
    ///   且玩家侧无任何写入点，且它是 `UnityStandardAssets` 的标准资源）。
    ///   真正承担出手位移的是 `CharacterControllerBase`：
    ///       public void ApproachForm(List&lt;Vector3&gt; pointList, float duration)  // 轨迹点位移
    ///       protected virtual void ApproachForm(ApproachFormParam param)       // 数据驱动版
    ///   `PlayerControlCustom`：
    ///       public void HoldMoveForceAccu(float duration)   // ★ 按住强制位移（Accumulate）
    ///       public void HoldMoveForceArk(float duration)    // ★ 按住强制位移（Ark）
    ///   ⇒ `HoldMoveForce*` 的名字与"按住 Shift 疾飞"完全对应，
    ///     且都是 **public**（可直接调用，不需要反射）。
    ///
    /// ── 为什么挂钩而不是直接调 ─────────────────────────────────────────────
    ///   方法体不可读（IL2CPP），无法静态确定"加力时到底走哪条"。
    ///   挂钩是**纯只读**的：只记录被调用的事实与参数，不改变行为。
    ///   这样用户按 Shift 时，日志会直接告诉我们加力走了哪条路 —— 用数据定位。
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 只记日志（节流），绝不改写参数、绝不跳过原方法
    /// </summary>
    private static void Postfix_HoldMoveForceAccu(PlayerControlCustom __instance, float duration)
    {
        LogMoveForce("HoldMoveForceAccu", duration);
    }

    private static void Postfix_HoldMoveForceArk(PlayerControlCustom __instance, float duration)
    {
        LogMoveForce("HoldMoveForceArk", duration);
    }

    /// <summary>位移管线命中的日志（节流，避免每帧刷屏）。</summary>
    private static void LogMoveForce(string which, float duration)
    {
        _moveForceHits[which] = _moveForceHits.TryGetValue(which, out int n) ? n + 1 : 1;
        int count = _moveForceHits[which];
        if (count == 1 || count % MOVE_FORCE_LOG_EVERY == 0)
        {
            Log.LogInfo($"[MOVEFORCE] {which}(duration={duration:F2}) hit #{count}"
                        + " - this is the displacement pipeline the boost uses");
        }
    }

    private static readonly Dictionary<string, int> _moveForceHits = new();
    // 位移管线日志节流（次）：可能每帧命中
    private const int MOVE_FORCE_LOG_EVERY = 30;

    /// <summary>
    /// 加力位移的挂钩（注册用）。返回补丁数。
    /// 见 `Postfix_HoldMoveForceAccu` 的逆向结论。
    /// </summary>
    private static int PatchMoveForcePipeline(Harmony harmony)
    {
        int n = PatchPostfix(harmony,
            AccessTools.Method(typeof(PlayerControlCustom), "HoldMoveForceAccu"),
            nameof(Postfix_HoldMoveForceAccu),
            "PlayerControlCustom.HoldMoveForceAccu (displacement pipeline)");
        n += PatchPostfix(harmony,
            AccessTools.Method(typeof(PlayerControlCustom), "HoldMoveForceArk"),
            nameof(Postfix_HoldMoveForceArk),
            "PlayerControlCustom.HoldMoveForceArk (displacement pipeline)");
        return n;
    }

    /// <summary>
    /// 加力速度取证（用户要求「直接改速度字段」—— 但先要知道**哪个字段**是速度）。
    ///
    /// ── 用户实测反馈（决定了必须先取证）────────────────────────────────────
    ///   「按住 shift 后，动画特效出现了，但是……**移速没有变化**」
    ///   ⇒ `ForceBooston()` 确实生效（特效/动画 5 次开关零异常），
    ///     但**位移速度没变** —— 说明加力的"视觉"与"位移"是两套东西，
    ///     位移速度由另一个字段控制。
    ///
    /// ── 候选字段（interop 权威字段表）──────────────────────────────────────
    ///   `PlayerControlCustom`：
    ///       int   Boostpoint / BoostpointMax / BoostpointMin   // 加力燃料
    ///       float AssistSpeedMagni                             // 辅助速度倍率
    ///       float GetMagniLogic() / GetMagniCommon(float val)  // 倍率计算
    ///   `CharacterControllerBase`：
    ///       float LookSpeed / LockonMoveSpeed                  // 移动速度
    ///
    /// ── 为什么不直接写字段 ─────────────────────────────────────────────────
    ///   方法体不可读（IL2CPP），无法静态确定哪个字段控制加力位移速度。
    ///   乱写 float 可能破坏物理 —— 历史上写 `Move.y` 直接导致"无法移动"。
    ///   ⇒ 每帧（节流）打出这些值，用户在**加力中**观察，哪个值异常/固定就是答案。
    ///     拿到数据再改，避免第三次"没定位就动手"。
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 纯只读（只记日志），**不写任何字段**
    ///   · 只在加力激活时输出（聚焦对比，减少噪声）
    ///   · 节流输出
    /// </summary>
    private static void ProbeBoostSpeed(PlayerControlCustom player)
    {
        if (!_boostActive)
        {
            return;   // 只在加力中输出 —— 用户只需对比加力时的数值
        }
        if (++_boostSpeedProbeCounter < BOOST_SPEED_PROBE_EVERY)
        {
            return;
        }
        _boostSpeedProbeCounter = 0;
        try
        {
            // 反射读候选字段：字段缺失不会导致编译失败（比直接字段访问稳）
            string boostPoint = ReflectionString(player, "Boostpoint");
            string boostPointMax = ReflectionString(player, "BoostpointMax");
            string magniLogic = ReflectionString(player, "GetMagniLogic");
            string speedMagni = ReflectionString(player, "AssistSpeedMagni");
            string lockonMove = ReflectionString(player, "LockonMoveSpeed");
            string lookSpeed = ReflectionString(player, "LookSpeed");
            string boostStatus = ReflectionString(player, "BoostStatus");

            Log.LogInfo($"[BOOSTSPD] status={boostStatus}"
                        + $" boostPoint={boostPoint}/{boostPointMax}"
                        + $" magniLogic={magniLogic} assistSpeedMagni={speedMagni}"
                        + $" lockonMoveSpeed={lockonMove} lookSpeed={lookSpeed}");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"[BOOSTSPD] probe failed: {ex.Message}");
        }
    }

    private static int _boostSpeedProbeCounter;
    // 加力速度探针节流间隔（帧）。30 帧 ≈ 0.5 秒 @60fps：
    // 足够看清数值变化，又不至于淹没日志。
    private const int BOOST_SPEED_PROBE_EVERY = 30;

    /// <summary>
    /// 读垂直输入：**上升键**=+1，**下降键**=-1，都不按或同时按=0。
    /// 用旧版 UnityEngine.Input（InputLegacyModule）——无状态、不依赖游戏输入资产。
    ///
    /// ── ⚠️ 为什么下降键从 Shift 改成了 C（用户裁定）────────────────────────
    ///   用户要「Shift 冲刺」。而 Shift 原本被这里当成"下降"消费掉了。
    ///   ⇒ 用户选择「下降改到 C 键」，把 Shift 完全腾出来给冲刺。
    ///
    ///   键名外置到配置（`AscendKey` / `DescendKey`），改键不需要重新编译。
    /// </summary>
    private static float ReadVerticalInput()
    {
        try
        {
            bool up = _ascendKeyCode != KeyCode.None && Input.GetKey(_ascendKeyCode);
            bool down = _descendKeyCode != KeyCode.None && Input.GetKey(_descendKeyCode);
            if (up == down)
            {
                return 0f;   // 都不按 或 同时按 → 视为无输入（避免歧义）
            }
            return up ? 1f : -1f;
        }
        catch (Exception ex)
        {
            // 输入失败必须说出来，否则表现为"按了没反应"难以定位
            Log.LogError($"input read failed: {ex.Message}");
            return 0f;
        }
    }

    /// <summary>
    /// 数据流：把输入轴（horizon, vertical）按**相机朝向**重新解释，改写进参数。
    ///
    /// 语义（用户确认的第三人称模式）：W 往相机前方走、A/D 往相机左右走。
    /// 原版是"相对锁定方向的坐标轴"，锁定被抹除后那套轴失去意义，
    /// 所以必须换成相机轴 —— 否则推 W 会走向一个与视线无关的固定世界方向。
    ///
    /// 为什么改**输入**而不是改 Move：输入是这条数据流的源头，
    /// 改源头让游戏自己的移动管线（速度、摩擦、动画）全部照常工作；
    /// 而直接写 Move 属于改中间结果，可能被后续计算覆盖
    /// （已吃过一次教训：写 Move.y 有效但角色高度不变）。
    ///
    /// 轴映射推导（反编译 PlayerControlCustom.Move 字段的实测数据）：
    ///   (h=1.000, v=0.000) -> Move(x=-0.156, z= 0.988)
    ///   (h=0.000, v=1.000) -> Move(x=-0.718, z= 0.696)
    /// 说明：h 正向 ≈ -right，v 正向 ≈ +forward（均在锁定坐标系下）。
    /// 因此在相机坐标系下的等价写法是：
    ///   horizon' = -dot(camRight, want)
    ///   vertical'=  dot(camForward, want)
    /// </summary>
    private static void Prefix_EvaluateMoveAndAttack(PlayerControlCustom __instance,
                                                     float horizon, float vertical)
    {
        bool changed = float.IsNaN(_lastHorizon)
                       || Math.Abs(horizon - _lastHorizon) > INPUT_EPSILON
                       || Math.Abs(vertical - _lastVertical) > INPUT_EPSILON;
        if (changed)
        {
            _lastHorizon = horizon;
            _lastVertical = vertical;
            if (_cfgPhaseLogInput.Value)
            {
                Log.LogInfo($"[IN ] horizon={horizon.ToString(NUM_FMT)} vertical={vertical.ToString(NUM_FMT)}");
            }
        }
        // 存下本帧输入供移动重映射使用（IL2CPP 的按值参数无法在 prefix 里改写）
        _curHorizon = horizon;
        _curVertical = vertical;

        // 移动管线观测（纯读，不改游戏状态）
        MoveTrace.Observe(__instance, horizon, vertical);
    }

    /// <summary>
    /// 【事故禁用 · 请勿启用】把移动方向改成"朝相机方向"。
    ///
    /// INCIDENT（2026-10-15）：此方法曾上线并与 SuppressLock 一起导致
    /// **整个战斗系统崩溃**（敌人 AI 消失、锁定失效、战斗 UI 消失）。
    ///
    /// 两处错误中的第二处 —— 公式推导错误：
    ///   原版实测（阶段 1 观测日志）：in=(1.000, 0.000) -> Move(x=-0.156, z=0.988)
    ///   本方法的输出（事故日志）：    in=(1.000, 0.000) -> Move=(-1.000, 0.000)
    ///   **两者不一致** → 说明 Move 并非与输入同构的向量，中间还有额外变换
    ///   （很可能是朝向/锁定坐标系相关的投影）。用错误公式覆盖它只会让移动乱掉。
    ///
    /// 重做前必须完成：重新反编译确认 EvaluateMoveAndAttack → MoveControl 的
    /// 数据流，弄清 Move 到底是"输入方向"还是"已变换后的方向"。
    /// </summary>
    private static void ApplyCameraRelativeMove(PlayerControlCustom player)
    {
        try
        {
            Camera? cam = Camera.main;
            if (cam is null)
            {
                return;
            }
            // 只用水平分量：否则抬头/低头会把移动方向也带成上下，
            // 而垂直移动已由空格/Shift 专门负责。
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            Vector3 right = cam.transform.right;
            right.y = 0f;
            if (fwd.sqrMagnitude < INPUT_EPSILON || right.sqrMagnitude < INPUT_EPSILON)
            {
                return;   // 相机几乎垂直朝下/朝上，水平基向量退化，跳过本帧
            }
            fwd.Normalize();
            right.Normalize();

            Vector3 want = fwd * _curVertical + right * _curHorizon;
            if (want.sqrMagnitude <= INPUT_EPSILON)
            {
                return;   // 无输入，不干预
            }
            float len = want.magnitude;
            want /= len;

            Vector3 mv = player.Move;
            float mag = new Vector2(mv.x, mv.z).magnitude;
            if (mag <= INPUT_EPSILON)
            {
                return;   // 原版没有水平移动意图（如出招/受击硬直），不干预
            }

            float h2 = -Vector3.Dot(right, want);
            float v2 = Vector3.Dot(fwd, want);
            float newMag = mag * Math.Min(1f, len);   // 保留原版的力度信息
            player.Move = new Vector3(h2 * newMag, mv.y, v2 * newMag);

            if (_cfgPhaseLogInput.Value && ++_moveRemapCount % LOCK_LOG_EVERY == 0)
            {
                Log.LogInfo($"[MOVE] camera-relative remap: in=({_curHorizon.ToString(NUM_FMT)},"
                            + $"{_curVertical.ToString(NUM_FMT)}) -> Move=({h2.ToString(NUM_FMT)},"
                            + $"{v2.ToString(NUM_FMT)}) mag={newMag.ToString(NUM_FMT)}");
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"camera-relative move failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 【事故禁用 · 请勿启用】抹除锁定：把锁定目标每帧置空。
    ///
    /// INCIDENT（2026-10-15）：此方法上线后**整个战斗系统崩溃** ——
    /// 敌人 AI 消失、爱迪尔锁定消失、战斗界面消失。
    /// 事故日志证据：[LOCK] target cleared (count=1) —— 只清了一次，战斗系统随即失效。
    ///
    /// 根因（我的判断错误）：
    ///   我依据"LockonTargetCharacter 是锁定目标的唯一持有者"就断定清空它只影响相机瞄准。
    ///   实际上它是**战斗系统追踪目标的枢纽**：置空等于向整个系统宣告"场上没有敌人"，
    ///   于是敌人 AI 停止工作、锁定无目标可锁、战斗 UI 因无目标而隐藏。
    ///
    /// 教训（写给未来的自己）：
    ///   判断一个字段能不能改之前，必须先查清**还有谁在读它**，
    ///   而不能只看它的语义名字。IL2CPP 的方法体是桩，静态看不到读取方，
    ///   所以这类"核心状态字段"默认应视为不可写。
    ///
    /// 另外：用户原本的需求是"**保留**原版锁定玩法"（战斗中一直锁定目标），
    /// 我从"抹除锁定"这个错误理解出发写了本方法 —— 需求理解错误也是原因之一。
    ///
    /// **本方法已改为"调用即抛异常"**：危险代码从物理上移除，只靠"记得别调"是不够的。
    /// 原来那行 `player.LockonTargetCharacter = null` 是导致战斗系统崩溃的直接原因，
    /// 只要代码还在，未来任何一次误调用都会重演事故。
    /// </summary>
    private static void SuppressLock(PlayerControlCustom player)
    {
        // 有意不保留任何实现：这里曾经把 LockonTargetCharacter 置 null，
        // 导致敌人 AI / 锁定 / 战斗 UI 全部停摆。详见本方法头部的事故记录。
        // 若将来确实需要干预锁定，请走 PhantomTarget 的"提供有效目标"路线，
        // 绝不能把它写成 null。
        throw new NotSupportedException(
            "SuppressLock is disabled: it nulled LockonTargetCharacter and broke the battle system. "
            + "Use the PhantomTarget approach (supply a VALID target) instead of nulling it.");
    }

#pragma warning disable CS0169 // 保留事故相关的字段声明会误导；这里仅用于记录
    // 事故相关字段已不再使用（危险实现已移除）。
    // 保留注释而非字段，避免后来人以为锁定仍被干预。
#pragma warning restore CS0169

    /// <summary>
    /// 【Harmony postfix】PlayerControlCustom.GetLockonEnemys()
    ///
    /// 在游戏算出"可锁定的敌人候选列表"之后、由它决定锁谁之前，
    /// 把最贴合相机视线的敌人换到首位 —— 于是锁定目标随相机方向变化。
    ///
    /// 为什么用 postfix 而不是 prefix 清空列表：
    ///   prefix 清空会让游戏自己的选取逻辑面对空列表，行为未知；
    ///   postfix 只是**调整既有元素的顺序**，游戏照常走它自己的流程，
    ///   干预面最小（这是从上次"清空 LockonTargetCharacter 炸掉战斗系统"吸取的教训）。
    /// </summary>
    private static void Postfix_GetLockonEnemys(Il2CppSystem.Collections.Generic.List<CharacterControllerBase> __result)
    {
        if (!_cfgLockRedirect.Value)
        {
            return;
        }
        LockTargetRedirect.ReorderByCameraAim(__result);
    }

    /// <summary>
    /// 核心：Update 之后驱动垂直位移，并（可选）输出相机诊断。
    /// </summary>
    private static void Postfix_Update(PlayerControlCustom __instance)
    {
        if (__instance is null)
        {
            return;
        }
        if (_instanceSeen++ == 0)
        {
            Log.LogInfo("PlayerControlCustom captured.");
        }
        // 刷新给相机修正用的实例引用（场景切换会换实例，所以每帧都写）
        _player = __instance;

        // ---- 全屏循环光标（用户指出的问题）----
        // 原版"鼠标一动就呼出光标"，我们的模式需要鼠标瞄准 ⇒ 必须在非原版模式下锁住。
        // 放在最前面：光标状态要早于任何鼠标读取逻辑被确定。
        UpdateCursorLock(__instance);

        // ---- 冲刺（用户键位表：shift/冲刺）----
        // 入口 = PlayerControlCustom.DashStart()，由探针实测命中确定（见 UpdateDash 注释）。
        // 放在模式判断之前：冲刺在 F2/F3 都要能用（与垂直飞行同层）。
        UpdateDash(__instance);

        float vertical = ReadVerticalInput();
        float applied = 0f;

        // ---- 自由视角（按住键）----
        // 独立于框架总开关：它是纯相机操作，不碰移动/锁定/攻击，
        // 因此即使框架关闭也可以单独使用（用户设计里它是独立功能）。
        if (_freeCam is not null)
        {
            bool held;
            try
            {
                held = Input.GetKey(_freeLookKeyCode);
            }
            catch (Exception ex)
            {
                Log.LogError($"read free-look key failed: {ex.Message}");
                held = false;
            }

            if (held && !_freeLookHeld)
            {
                // 按下沿：进入自由视角
                Transform? gameCam = Camera.main is not null ? Camera.main.transform : null;
                _freeCam.BeginFreeLook(GameCameras(__instance), __instance.transform, gameCam);
            }
            else if (!held && _freeLookHeld)
            {
                // 松开沿：交还相机
                _freeCam.EndFreeLook();
            }
            _freeLookHeld = held;

            if (held)
            {
                _freeCam.TickFreeLook(__instance.transform);

                // 阶段 1：幻影跟随视线（不接入锁定 —— 见 PhantomTarget 注释）
                if (_phantom is not null && _cfgPhantomEnabled.Value)
                {
                    _phantom.EnsureCreated();
                    Camera? c = Camera.main;
                    if (c is not null)
                    {
                        _phantom.FollowCameraAim(c);
                    }
                }
            }
            else if (_phantom is not null && _phantom.Exists)
            {
                // 松开键：销毁幻影，不给场景留垃圾对象
                _phantom.Destroy();
            }
        }

        // ---- 框架总开关与模式切换 ----
        // 配置关掉时，连切换键都不响应 —— 插件等同不存在（用户要求"默认关"）。
        // 用户指定键位：F1=原版 / F2=自由模式 / F3=第三人称脱离准心。
        if (_cfgFrameworkEnabled.Value)
        {
            // 三个键各自对应一个模式（直接指定，而非循环切换）——
            // 这样"我现在在哪个模式"一目了然，不会误切。
            FrameworkMode? want = null;
            if (PressedThisFrame(_modeKeyVanilla, "F1")) { want = FrameworkMode.Vanilla; }
            else if (PressedThisFrame(_modeKeyFreeFlight, "F2")) { want = FrameworkMode.FreeFlight; }
            else if (PressedThisFrame(_modeKeyThirdPerson, "F3")) { want = FrameworkMode.ThirdPerson; }

            if (want.HasValue && want.Value != _mode)
            {
                SwitchMode(__instance, want.Value);
            }
        }

        // 垂直飞行：自由模式与第三人称模式都支持（两者都是"3D 化"模式）。
        // 原版模式绝不允许 —— 那会污染纯粹的原版体验。
        if ((_mode == FrameworkMode.FreeFlight || _mode == FrameworkMode.ThirdPerson)
            && Math.Abs(vertical) > 0f)
        {
            try
            {
                // deltaTime 兜底：极端帧（加载卡顿）下防止单帧瞬移
                float dt = Math.Min(Time.deltaTime, MAX_FRAME_SECONDS);
                applied = vertical * _cfgSpeed.Value * dt;

                // 只改 Y：水平移动仍由游戏自己的系统负责（相对锁定方向的移动逻辑保持原样）
                Transform t = __instance.transform;
                Vector3 p = t.position;
                t.position = new Vector3(p.x, p.y + applied, p.z);
            }
            catch (Exception ex)
            {
                Log.LogError($"transform.position write failed: {ex.Message}");
            }
        }

        // ---- 技能配置探查（按键触发，只读）----
        // 用"按下沿"触发，避免长按每帧刷屏
        bool probeHeld;
        try
        {
            probeHeld = Input.GetKey(_selProbeKeyCode);
        }
        catch (Exception ex)
        {
            Log.LogError($"read probe key failed: {ex.Message}");
            probeHeld = false;
        }
        if (probeHeld && !_selProbeHeld)
        {
            SelectionProbe.Dump(__instance);
        }
        _selProbeHeld = probeHeld;

        // ---- 相机组件探查（按键触发，只读）----
        bool camProbeHeld;
        try
        {
            camProbeHeld = Input.GetKey(_camProbeKeyCode);
        }
        catch (Exception ex)
        {
            Log.LogError($"read camera probe key failed: {ex.Message}");
            camProbeHeld = false;
        }
        if (camProbeHeld && !_camProbeHeld)
        {
            CameraProbe.Dump(__instance);
        }
        _camProbeHeld = camProbeHeld;

        // ---- 锁定相机瞄准重定向（固定启用；只在自由/第三人称模式生效）----
        // 用户要求："把这个启用固定进入到自由模式中去"，同时原版模式不能被改。
        // 机理与实测依据见 CameraAimTuner.cs 顶部注释。
        if (_mode == FrameworkMode.FreeFlight || _mode == FrameworkMode.ThirdPerson)
        {
            // 把锁定相机的 LookAt 指向敌人本体 → Aim 阶段对齐敌人 → 三轴跟随（含垂直）。
            //
            // ⚠️ 血泪教训：第一版只改了四个"单锁"相机，实测发现它们在那一刻
            //    **优先级都是 0（根本没在工作）**；真正生效的是 prio=100 的
            //    LockonLong（激活 34 次）与"冲刺蓄力"用的 DashReady。
            //    那两个相机的 lookAt 原本是 **AIdealBattle（玩家自己）** ——
            //    从来就没指向过敌人，所以怎么调"瞄中点"都不可能跟随敌人。
            //    ⇒ 必须覆盖"实际会被激活的相机"，不能只看名字像不像锁定相机。
            ApplyLockCameraAim(__instance);
        }
        else
        {
            // 原版模式：确保所有被我们改过的 LookAt 都还原（幂等）
            RestoreLockCameraAim(__instance);
        }

        // ---- ★ 虚拟瞄准锚点：方向的唯一源头（只在 F3 模式）----
        // ⚠️ 关键修正（实测教训）：锚点**不能读相机朝向**。
        //    一旦"锁定目标 = 锚点"生效，链路变成
        //        相机 → 锚点 → 锁定目标 → 身体 → 相机        ← 闭环
        //    表现为锚点在爱迪尔头顶乱转（实测 dir 每两秒跳一次、dy 在 6~7 摆动）。
        //    现在锚点由【鼠标输入】直接驱动，成为链条唯一源头：
        //        鼠标 → 锚点 → 锁定目标 → 身体/相机            ← 单向下游
        if (_mode == FrameworkMode.ThirdPerson && _aimAnchor is not null)
        {
            // ---- 虚拟光标抑制（用户要求：战斗绝对不能有虚拟光标）----
            if (_cfgSuppressVirtualCursor.Value)
            {
                _virtualCursor?.Suppress();
            }

            // ---- 索敌与三档调距（用户玩法：中键锁敌 / Q·E 切远近）----
            UpdateAnchorTargeting(__instance);

            // ---- 按形态选锚点距离 ----
            // ⚠️ 已移除"形态切换自动重置距离"的逻辑（实测 bug）：
            //   它与 Q/E 三档调距**互相打架**，日志出现
            //     attack form -> Ranged, distance reset to 30.0
            //     attack form -> Melee,  distance reset to 12.0
            //     attack form -> Ranged, distance reset to 30.0   ← 无限震荡
            //   导致用户用 Q/E 切好的档位被反复改回去。
            //   ⇒ 距离的**唯一来源**是 Q/E 三档（SetTier），形态只用于日志观察。
            AttackForm form = AttackFormDetector.Detect(__instance);
            if (form != _lastForm)
            {
                _lastForm = form;
                Log.LogInfo($"[ANCHOR] attack form -> {form}"
                            + $" (distance stays at {TierName(_tierIndex)}"
                            + $" {_currentAnchorDistance:F1}m - controlled by Q/E only)");
            }

            // 种子角度取当前相机视角，避免进模式瞬间锚点从 0 度猛跳
            float seedYaw = 0f;
            float seedPitch = 0f;
            Camera? cam = Camera.main;
            if (cam is not null)
            {
                Vector3 e = cam.transform.eulerAngles;
                seedYaw = e.y;
                // 转成 [-180,180] 的俯仰表示
                seedPitch = e.x > 180f ? e.x - 360f : e.x;
            }
            // 三档距离（锁定时该参数不参与计算，见 VirtualAimAnchor.Tick）
            _aimAnchor.Tick(__instance.transform.position, _currentAnchorDistance,
                           seedYaw, seedPitch);

            // ---- 锁定渲染框重定向（可开关，见字段区注释）----
            if (_cfgMoveLockMarker.Value)
            {
                MoveLockMarkerToAnchor(_aimAnchor.Transform);
            }

            // 锁定渲染对象的位置取证（低频；用户实测"渲染框仍在敌人处"，
            // 需要数据确认渲染链跟随的到底是锚点还是敌人）
            if (++_lockVisualProbeCounter >= LOCK_VISUAL_PROBE_EVERY)
            {
                _lockVisualProbeCounter = 0;
                CameraProbe.DumpLockVisuals(__instance, _aimAnchor.Transform);
            }

            // ---- 相机拉近（用户实测："视角太远了，要离爱迪尔近一点"）----
            // 只缩放当前激活的 Transposer 相机；比例=1 时完全不干预。
            // 见 CameraDistanceTuner.cs（为什么不能改 camDist 写在那个文件顶部）。
            _camDistTuner?.Apply(AllCameras(), _cfgCamDistScale.Value);
            _camScaleApplied = true;
        }
        else if (_camScaleApplied)
        {
            // 刚离开 F3（或锚点不可用）：把相机距离还原成游戏原值，不留副作用
            _camDistTuner?.Restore(AllCameras());
            _camScaleApplied = false;
        }

        // ---- ★ 核心：把"锁定目标"固定到锚点（用户定稿设计）----
        // 用户原话：「把锁定固定到锚点那里去，然后锚点又是一个属于我们的可控准心，
        //           所以就做到了让锁定成为我们的准心」
        //
        // 实测依据（F11 朝向探针）：bodyFwd vs to LOCK TARGET = **0.0 度** ——
        // 身体精确朝锁定目标。因此把锁定目标换成锚点后，
        // 【朝向 / 准心 / 攻击 / 冲刺 / 大招】全部自动朝锚点，一致性由构造保证。
        //
        // ⚠️ 历史事故教训：**绝不能把它置 null**（那会导致敌人 AI / 锁定 / 战斗 UI 崩溃）。
        //   本实现只在 F3 模式下写入**有效实例**，且退出时恢复原值。
        // ---- 锁定目标接管：改为**事件驱动**，这里不再每帧夺回 ----
        // 用户原话：「那这个没意义了阿，不还是每帧渲染吗？你开启F3后去自动判定阿，
        //           以及长按中键结束后再判定一次阿，就2种情况可能出现被夺的情况」
        // ⇒ 夺回只发生在两处（见 ReclaimLockTarget 的调用点）：
        //     ① 进入 F3 模式时（SwitchMode 的 ThirdPerson 分支）
        //     ② 长按中键结束时（UpdateAnchorTargeting 的 LOCK OFF 分支）
        //   这里只负责：退出 F3 时把字段还回原值。
        if (_mode != FrameworkMode.ThirdPerson && _lockTargetOverridden)
        {
            RestoreLockTargetOverride(__instance);
        }

        // ---- 探针自动输出（用户要求：不必手动按键）----
        // 用帧计数器节流，与帧率解耦地"不刷屏"；技能表是静态数据所以间隔更长。
        if (_cfgAutoProbe.Value)
        {
            if (++_autoProbeSelCounter >= AUTO_PROBE_SEL_INTERVAL_FRAMES)
            {
                _autoProbeSelCounter = 0;
                SelectionProbe.Dump(__instance);
            }
            if (++_autoProbeCamCounter >= AUTO_PROBE_CAM_INTERVAL_FRAMES)
            {
                _autoProbeCamCounter = 0;
                CameraProbe.Dump(__instance);
            }
        }

        // ---- 模式 B：驱动自由相机（位置跟随玩家、朝向由鼠标控制）----
        if (_mode == FrameworkMode.FreeFlight && _freeCam is not null && _freeCam.IsActive)
        {
            _freeCam.Tick(__instance.transform, vertical);
        }

        // ---- 【已回滚】抹除锁定 ----
        // 严重事故记录：曾在此处每帧执行 LockonTargetCharacter = null，
        // 结果**整个战斗系统崩溃**：敌人 AI 消失、锁定失效、战斗 UI 消失。
        // 日志证据：清空 1 次后战斗系统即失效（[LOCK] target cleared count=1）。
        //
        // 根因（我的误判）：我依据"LockonTargetCharacter 是锁定目标的唯一持有者"
        // 就认为清空它只影响相机瞄准。实际它是**战斗系统追踪目标的枢纽**：
        // 置空等于宣告"场上没有敌人"，敌人 AI 与战斗 UI 因此全部停摆。
        // 教训：判断某字段能不能改之前，必须先查**还有谁在读它**，
        //       而不是只看它的语义名字。
        //
        // 锁定相关的需求（用户原话是"保留原版锁定玩法"）已由 SwitchMode 里的
        // LockonLock 保存/恢复处理，不需要动战斗系统的状态。

        // ---- 【已回滚】移动方向重映射 ----
        // 事故记录：曾在此处把 Move 改写成相机方向，
        // 但推导有误 —— 实测原版 in=(1.000,0.000) 对应 Move(x=-0.156, z=0.988)，
        // 而我的公式算出 (h2,v2)=(-1.000, 0.000)，与原版数据不符。
        // 推断 Move 不是与输入同构的向量（中间还有额外变换），因此不能这样重写。
        // 待重新反编译确认移动管线后再做，不能带着错误公式上线。

        // 可选：相机诊断（默认关，避免刷屏；排查相机问题时打开）
        if (_cfgCamDiag.Value)
        {
            ReportCamera(__instance);            // 虚拟相机配置对比：低频输出（每 10 个采样周期一次），
            // 因为它列的是"配置"而非逐帧状态，变化很慢
            if (++_vcamReportCounter % VCAM_REPORT_EVERY == 0)
            {
                ReportActiveVCam(__instance);
                ReportVCameras(__instance);
                ReportTargetGroups(__instance);
            }
        }

        // 高度差自适应的目标组权重修正（相机 Z 轴适配）。
        // 实测：锁定相机看向 TargetGroupSingle 的加权中心，而组里有玩家和敌人
        // 两个等权成员 → 相机在看"中点"。飞高后中点远离敌人，于是看不见人。
        // 这里随高度差把玩家权重降到 0，让组中心收敛到敌人身上。
        if (_cfgGroupFix.Value)
        {
            float diff = MeasureHeightDifference(__instance);
            if (!float.IsNaN(diff))
            {
                ApplyGroupWeightFix(__instance, diff);
                if (++_groupFixLogCounter % GROUPFIX_LOG_EVERY == 0)
                {
                    Log.LogInfo($"[GFIX] heightDiff={diff.ToString(NUM_FMT)}"
                                + $" playerWeight={_lastPlayerWeight.ToString(NUM_FMT)}"
                                + $" (fullAt={GROUP_WEIGHT_FULL_AT_ABS_DIFF.ToString(NUM_FMT)})");
                }
            }
        }

        // --- 节流观测 ---
        if (++_frameCounter % LOG_INTERVAL_FRAMES != 0)
        {
            return;
        }
        try
        {
            float worldY = __instance.transform.position.y;
            string delta = float.IsNaN(_lastWorldY)
                           ? "n/a"
                           : (worldY - _lastWorldY).ToString(NUM_FMT);
            _lastWorldY = worldY;

            // 同时读游戏自己的刚体字段（CharacterControllerBase.Rigid），
            // 不再用 GetComponent 猜 —— 这是确认刚体存在与否的权威方式
            string rbInfo = "Rigid=n/a";
            try
            {
                var rb = __instance.Rigid;
                rbInfo = rb is null
                    ? "Rigid=null"
                    : $"RigidObjY={rb.position.y.ToString(NUM_FMT)} kin={rb.isKinematic} grav={rb.useGravity}";
            }
            catch (Exception ex)
            {
                rbInfo = $"Rigid=err({ex.Message})";
            }

            Log.LogInfo($"[FLY] worldY={worldY.ToString(NUM_FMT)} d={delta}"
                        + $" | applied={applied.ToString(NUM_FMT)}"
                        + $" | {rbInfo}"
                        + $" | vertical={vertical.ToString(NUM_FMT)}");
        }
        catch (Exception ex)
        {
            Log.LogError($"read worldY failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <summary>
    /// 夺回锁定目标 —— **事件驱动**，只在两个时刻调用（用户定稿）。
    ///
    /// 用户原话：
    ///   「那这个没意义了阿，不还是每帧渲染吗？你开启F3后去自动判定阿，
    ///     以及长按中键结束后再判定一次阿，就2种情况可能出现被夺的情况」
    ///   「然后你每帧判定也没有成功夺回，我发现不知道为什么出现敌人又会直接夺回渲染了」
    ///
    /// ⇒ 结论一：不需要每帧判定。可能被夺的时机只有两个：
    ///      ① 进入 F3 模式时
    ///      ② 长按中键结束（松开）时
    ///
    /// ⇒ 结论二（**关键**）：只写 `LockonTargetCharacter` 字段**不会**让游戏重新绑定渲染 ——
    ///   这是"每帧判定也没成功夺回"的真正原因。必须调游戏自己的正门 API：
    ///      `BattleManager.GetInstance()`          —— 单例入口（L1046）
    ///      `BattleManager.LockonDispOpen(target)` —— 绑定锁定显示（L1208）
    ///      `BattleManager.LockonDispClose()`      —— 解绑（L1298）
    ///   本方法先写字段（供朝向/攻击读取），再调 `LockonDispOpen` 强制游戏把渲染绑到锚点。
    /// </summary>
    /// <param name="reason">触发原因（写日志用，便于区分两类事件）</param>
    private static void ReclaimLockTarget(string reason)
    {
        if (_mode != FrameworkMode.ThirdPerson || !_cfgLockTargetOverride.Value)
        {
            return;
        }
        // ★ 加力（疾飞）期间**暂停接管锁定**（用户指出的关键条件）：
        //   「加力/原版流程是在**没有锁定没有敌人**的情况下才有」。
        //   我们若在这时把锁定写成锚点，游戏就认为"有锁定目标" ⇒ 不进加力流程
        //   ⇒ `BoostStatus` 掉回 IDLE_NONBOOST（探针实测 43/45 次采样都是它）。
        //
        //   ⚠️ 这里**只是不写入**，绝不把 `LockonTargetCharacter` 置 null ——
        //     置 null 在历史上炸掉过整个战斗系统（敌人 AI 消失 / 锁定失效 / UI 消失）。
        if (BoostLockPaused)
        {
            return;
        }
        PlayerControlCustom? player = _player;
        CharacterControllerBase? anchor = _aimAnchor?.Controller;
        if (player is null || anchor is null)
        {
            Log.LogInfo($"[LOCKTARGET] reclaim({reason}) skipped:"
                        + $" player={(player is null ? "null" : "ok")}"
                        + $" anchor={(anchor is null ? "null" : "ok")}");
            return;
        }
        try
        {
            // ① 写字段：朝向 / 准心 / 攻击 / 冲刺都读它
            bool fieldChanged = player.LockonTargetCharacter != anchor;
            if (fieldChanged)
            {
                player.LockonTargetCharacter = anchor;
            }

            // ② 调游戏正门 API 强制重新绑定渲染 —— 只写字段是不够的（见方法注释）
            bool dispRebound = false;
            try
            {
                BattleManager? mgr = BattleManager.GetInstance();
                if (mgr is not null)
                {
                    mgr.LockonDispOpen(anchor);
                    dispRebound = true;
                }
                else
                {
                    Log.LogWarning($"[LOCKTARGET] reclaim({reason}): BattleManager instance is null"
                                   + " - cannot rebind the lock display (field was"
                                   + (fieldChanged ? " updated" : " already correct") + ")");
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[LOCKTARGET] reclaim({reason}): LockonDispOpen failed: {ex.Message}");
            }

            Log.LogInfo($"[LOCKTARGET] reclaim({reason}):"
                        + $" field={(fieldChanged ? "rewritten" : "already anchor")}"
                        + $", LockonDispOpen={(dispRebound ? "called" : "FAILED")}"
                        + $" -> '{SafeName(anchor.transform)}'");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"[LOCKTARGET] reclaim({reason}) failed: {ex.Message}");
        }
    }

    /// <summary>取名字的安全包装（对象可能已被销毁）。</summary>
    private static string SafeName(Transform? t)
    {
        try
        {
            return t is not null ? t.name : "null";
        }
        catch (Exception)
        {
            return "destroyed";
        }
    }

    /// 注册到 Camera.onPreCull 的相机俯仰修正。
    ///
    /// ── 为什么不用 CinemachineBrain.LateUpdate 的 postfix ─────────────────
    ///   实测：在 brain 的 postfix 里把相机转到 -0.937°，下一帧读回来仍是
    ///   +0.063° —— 每帧都被重置。说明 LateUpdate 期间还有别的东西写相机
    ///   （其它脚本的 LateUpdate 顺序不确定，或 Unity 的内部相机同步）。
    ///   而 Camera.onPreCull 是 Unity 保证的**渲染前最后回调**，
    ///   所有 Update/LateUpdate 都已跑完，此处写入不会被覆盖。
    ///
    /// ── 修正算法（目标角度式，非增量式）─────────────────────────────────
    ///   早前用「ndcY × 半FOV × blend」做增量修正，其最大值被半 FOV 卡死
    ///   （50° FOV 下约 25°），而高度差 18 米时实际需要低头约 52° —— 远远不够，
    ///   这是"没用"的真实原因。这里改为直接解出目标俯仰角：
    ///       idealPitch = asin(方向向量.y)      ← 看向目标真正需要的角度
    ///       newPitch   = MoveTowards(当前, ideal, 速率 × dt)   ← 平滑逼近
    ///   目标在画面纵向中心区内时不干预，保留原版构图。
    /// </summary>
    private static void OnPreCull(UnityEngine.Camera cam)
    {
        // 注：此处**不再做每帧锁定夺回**（用户定稿）。
        // 用户原话：「那这个没意义了阿，不还是每帧渲染吗？你开启F3后去自动判定阿，
        //           以及长按中键结束后再判定一次阿，就2种情况可能出现被夺的情况」
        // ⇒ 夺回改为**事件驱动**，只在两个时刻执行：
        //     ① 进入 F3 模式时（EnterThirdPerson 里）
        //     ② 长按中键结束（松开）时（UpdateAnchorTargeting 里）
        //   这两个是"锁定目标可能被游戏改掉"的唯二时机。

        float blend = _cfgAimBlend.Value;
        if (blend <= 0f)
        {
            return;   // 关闭（=原版行为）
        }
        try
        {
            Vector3? aimNullable = GetAimPoint();
            if (aimNullable is null)
            {
                return;   // 没锁定目标：完全不干预
            }
            Vector3 aim = aimNullable.Value;

            Transform camT = cam.transform;
            Vector3 toTarget = aim - camT.position;
            if (toTarget.sqrMagnitude < AIM_MIN_DISTANCE * AIM_MIN_DISTANCE)
            {
                return;   // 目标过近，方向噪声大
            }

            // 目标在画面纵向中心区内 → 原版构图没问题，不打扰
            Vector3 viewport = cam.WorldToViewportPoint(aim);
            if (viewport.z <= 0f)
            {
                return;   // 目标在相机背后，不属于"高度差"问题
            }
            float ndcY = viewport.y * 2f - 1f;
            if (Math.Abs(ndcY) <= AIM_VISIBLE_HALF)
            {
                return;
            }

            // ---- 目标俯仰角 ----
            Vector3 dir = toTarget.normalized;
            float idealPitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float curPitch = camT.eulerAngles.x;
            if (curPitch > 180f)
            {
                curPitch -= 360f;   // 折到 -180..180，避免与 idealPitch 比较时错位
            }
            float newPitch = Mathf.MoveTowards(curPitch, idealPitch,
                                               _cfgAimMaxRateDegPerSec.Value * Time.deltaTime);
            float deltaPitch = newPitch - curPitch;
            if (Math.Abs(deltaPitch) > AIM_PITCH_EPSILON_DEG)
            {
                camT.rotation = Quaternion.AngleAxis(deltaPitch, camT.right) * camT.rotation;
            }

            if (++_aimLogCounter % AIM_LOG_EVERY == 0)
            {
                Log.LogInfo($"[AIM] cam={cam.name} curPitch={curPitch.ToString(NUM_FMT)}"
                            + $" idealPitch={idealPitch.ToString(NUM_FMT)}"
                            + $" newPitch={newPitch.ToString(NUM_FMT)}"
                            + $" delta={deltaPitch.ToString(NUM_FMT)}deg"
                            + $" fwdY={camT.forward.y.ToString(NUM_FMT)}"
                            + $" ndcY={ndcY.ToString(NUM_FMT)}"
                            + $" dist={toTarget.magnitude.ToString(NUM_FMT)}");
            }
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"onPreCull aim correction failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 收集游戏自己创建的全部虚拟相机。
    /// 模式 B 需要把它们统一压低优先级，让 Cinemachine 切到我们的相机。
    /// 注意：这里**只读引用**，不修改它们的配置 —— 切回模式 A 时它们完全不受影响。
    /// </summary>
    private static Cinemachine.CinemachineVirtualCamera?[] GameCameras(PlayerControlCustom p)
    {
        return new Cinemachine.CinemachineVirtualCamera?[]
        {
            p.VCameraMain,
            p.VCameraLockonCrossSingle,
            p.VCameraLockonMiddleSingle,
            p.VCameraLockonCrossMulti,
            p.VCameraLockonCrossCross,
            p.VCameraLockonLong,
            p.VCameraDashReady,
            p.VCameraDash,
            p.VCameraFingerLine,
        };
    }

    /// <summary>
    /// 切换框架模式，并按新模式接管/交还各项控制权。
    ///
    /// 接管清单（启用即全部生效，见 FrameworkMode 的说明）：
    ///   ① 三维飞行     —— Vanilla 下不写角色高度
    ///   ② 自由相机     —— FreeFlight 下我们的 vcam 优先级最高
    ///   ③ 暂停锁定的朝向接管 —— **不是移除锁定功能**，而是让锁定暂时不参与朝向计算。
    ///
    /// 为什么必须暂停锁定（用户实测 + 代码结构共同指向）：
    ///   原版锁定会让角色持续面向锁定目标（爱迪尔"一直保持锁定目标"正是这个机制）。
    ///   而自由相机需要由鼠标决定偏航 —— 两者写的是同一个朝向，于是互相覆盖：
    ///   表现为 pitch 只涨一点就被改回去、yaw 被大幅改写、镜头"不听使唤"。
    ///
    /// 关键：切回 Vanilla 时**完整恢复锁定玩法**（锁定目标、相对锁定方向的移动都由
    ///   游戏自己继续管），框架不留下任何痕迹。
    /// </summary>
    private static void SwitchMode(PlayerControlCustom player, FrameworkMode target)
    {
        try
        {
            FrameworkMode from = _mode;
            if (from == target)
            {
                return;   // 已经在该模式，无需切换
            }

            // ---- 先离开当前模式（统一收尾，避免残留）----
            if (from == FrameworkMode.FreeFlight && _freeCam is not null && _cfgFreeCamEnabled.Value)
            {
                // 交还相机（仅当确实接管过）
                _freeCam.Toggle(GameCameras(player), player.transform);
            }
            if (from == FrameworkMode.ThirdPerson)
            {
                // 离开第三人称模式：销毁锚点，不留垃圾对象
                _aimAnchor?.Destroy();
            }
            // 离开第三人称模式时也要还原锁定相机瞄准（阶段 5 会用到；现在无害）
            RestoreLockCameraAim(player);

            // ---- 再进入目标模式 ----
            switch (target)
            {
                case FrameworkMode.Vanilla:
                    Log.LogInfo("[FRAMEWORK] mode -> Vanilla (game fully in control)");
                    break;

                case FrameworkMode.FreeFlight:
                    // 相机接管可独立关闭（用户要求"把自由操控相机先移出去，有点小 bug 后面修"）：
                    //   FreeCamEnabled=false 时，本模式**只提供高度升降**，相机完全交给游戏。
                    if (_freeCam is not null && _cfgFreeCamEnabled.Value)
                    {
                        // 接管方式：只调优先级，游戏那批相机只是"没轮到"，配置一个都不改
                        _freeCam.Toggle(GameCameras(player), player.transform);
                    }
                    Log.LogInfo("[FRAMEWORK] mode -> FreeFlight (vertical flight;"
                                + $" free cam {(_cfgFreeCamEnabled.Value ? "ENGAGED" : "DISABLED")};"
                                + " lock system untouched)");
                    break;

                case FrameworkMode.ThirdPerson:
                    // ⚠️ 原子改动：此模式要求"朝向/准心/接近/冲刺/大招/移动"全部朝准心。
                    _aimAnchor?.EnsureCreated();
                    Log.LogInfo("[FRAMEWORK] mode -> ThirdPerson (aim anchor active;"
                                + " lock target / reticle redirected to the anchor)");
                    // ★ 事件①：进入 F3 时夺回一次锁定目标（含强制重绑渲染）
                    //   用户要求：「你开启F3后去自动判定阿」
                    ReclaimLockTarget("enter F3");
                    break;
            }

            _mode = target;
        }
        catch (Exception ex)
        {
            // 边界显式记录；并退回安全模式，避免卡在"半接管"状态
            Log.LogError($"SwitchMode({target}) failed: {ex}");
            _mode = FrameworkMode.Vanilla;
        }
    }

    // 锚点实例不可用的告警只报一次（避免每帧刷屏）
    // 注：原先用于"锚点实例不可用"的一次性告警字段已移除 ——
    // 该诊断现在由 ReclaimLockTarget 统一输出（它会明确报告 player/anchor 的可用性）。

    /// <summary>只记录一次的日志工具（用于"不该每帧刷屏"的告警）。</summary>
    private static void LogOnce(string message, ref bool flag)
    {
        if (flag)
        {
            return;
        }
        flag = true;
        Log.LogWarning($"[LOCKTARGET] {message}");
    }

    /// <summary>
    /// 把玩家的锁定目标固定到锚点（用户定稿设计的核心动作）。
    ///
    /// 为什么只改这一个字段就能让整条链朝锚点：
    ///   实测 bodyFwd vs to LOCK TARGET = 0.0 度 ⇒ 身体朝锁定目标；
    ///   准心（LockonCursol）画到锁定目标；攻击/冲刺/大招都朝锁定目标。
    ///   所以"锁定目标 = 锚点"等价于"整条瞄准链 = 锚点"。
    ///
    /// 安全：只在 F3 模式调用；写入的是**有效实例**（绝不写 null —— 那是历史事故原因）。
    /// </summary>
    private static void ApplyLockTargetOverride(PlayerControlCustom player,
                                                CharacterControllerBase anchor)
    {
        try
        {
            CharacterControllerBase? cur = player.LockonTargetCharacter;
            if (!_lockTargetOverridden)
            {
                _savedLockTarget = cur;            // 记下原值，退出 F3 时恢复
                _lockTargetOverridden = true;
                Log.LogInfo($"[LOCKTARGET] override ON: lock target = anchor"
                            + $" '{(anchor.transform is not null ? anchor.transform.name : "?")}'"
                            + $" (was '{(cur is not null && cur.transform is not null ? cur.transform.name : "null")}')");
            }

            if (cur == anchor)
            {
                // 已经是锚点了 —— 说明本轮没有被游戏改回去
                return;
            }

            // 被改回了（或首次写入）：重新写，并统计"被抢回"的次数。
            // 这个计数是**客观证据**：若它每帧增长，说明游戏在 Update 之后重算锁定，
            // 我们就需要把覆盖移到更晚的时机（例如 LateUpdate 或协程之后）。
            if (_lockTargetOverridden && cur is not null)
            {
                _lockStolenCount++;
                if (_lockStolenCount == 1 || _lockStolenCount % LOCK_LOG_EVERY == 0)
                {
                    Log.LogInfo($"[LOCKTARGET] game re-locked to"
                                + $" '{(cur.transform is not null ? cur.transform.name : "?")}'"
                                + $" -> re-applying anchor (steal count={_lockStolenCount})");
                }
            }

            player.LockonTargetCharacter = anchor;
        }
        catch (Exception ex)
        {
            Log.LogError($"[LOCKTARGET] apply failed: {ex}");
        }
    }

    /// <summary>
    /// 锁定抢占的"保险"（用户要求：锚点渲染必须加保险）。
    ///
    /// 背景（用户实测）：「如果有角色先被挂着了，后面锚点就抢不过打架了」
    ///   —— 游戏的锁定标记一旦绑定某敌人，直到该敌人被击杀才转移；
    ///      若此时我们把锁定目标改成锚点，就会出现"两边都要争"的局面。
    ///
    /// 保险内容：
    ///   · 统计并如实报告"游戏夺回锁定的频率"（`steal count`）
    ///   · 超过阈值时给出**明确诊断**，而不是默默打架 —— 便于判断该换策略还是接受现状
    ///   · 提供配置开关，可在"持续夺回"与"放弃接管"之间选择
    /// </summary>
    private static void ReportLockContention()
    {
        if (_lockContentionReported)
        {
            return;
        }
        // 阈值：若在 F3 模式下被夺回超过这么多次，说明游戏在持续重算锁定
        if (_lockStolenCount < LOCK_CONTENTION_THRESHOLD)
        {
            return;
        }
        _lockContentionReported = true;
        Log.LogWarning($"[LOCKTARGET] contention detected: the game re-locked"
                       + $" {_lockStolenCount} times while we keep re-applying the anchor."
                       + " This means the game recalculates the lock AFTER our Update postfix."
                       + " Options: (a) accept it (we win every frame, works but wasteful),"
                       + " (b) set LockTargetOverride=false to stop competing,"
                       + " (c) move the override to a later hook (needs a new patch point).");
    }

    private static bool _lockContentionReported;
    // 判定"持续争夺"的累计次数阈值。取 600（≈10 秒 @60fps）——
    // 低于此值可能只是切换目标的瞬间抖动，不值得报警。
    private const int LOCK_CONTENTION_THRESHOLD = 600;

    // 是否接管游戏的"锁定目标"字段（用户要求："锚点渲染必须加保险"）。
    // 关掉后：锚点仍会跟随/锁定敌人（准心/朝向仍由锚点驱动），
    //         但**不再去争抢游戏的锁定字段** —— 专用于
    //         "已经有角色被挂着（标记已绑定），锚点抢不过、两边打架"的局面。
    private static ConfigEntry<bool> _cfgLockTargetOverride = null!;

    // ---- 相机距离缩放（用户实测："视角太远了，要离爱迪尔近一点"）----
    // 见 CameraDistanceTuner.cs：只缩放 Transposer.m_FollowOffset 的长度，
    // 方向不变、按比例而非绝对值（游戏自己调整时我们跟随，不硬对抗）。
    private static ConfigEntry<float> _cfgCamDistScale = null!;
    private const float DEFAULT_CAM_DIST_SCALE = 0.65f;   // 拉到原距离的 65%
    private static CameraDistanceTuner? _camDistTuner;
    // 是否已应用过相机缩放。用于"离开 F3 时还原一次" —— 避免每帧还原，也不在其它模式留副作用。
    private static bool _camScaleApplied;

    // 注：此处原有"休息姿态（长按 Ctrl → DashReady）"的字段与 UpdateRestPose 方法，**已拆除**。
    //   用户指出我搞错了对象：「那个是左键的长按姿态，不是我说的休息冲刺」。
    //   `DashReady` = 左键长按蓄力姿态，与"打赢每波后的休息姿态"无关。
    //   真正的休息姿态待定位（线索：以爱迪尔为中心、相机在身后，疑似 VCameraMain）。


    /// <summary>
    /// 冲刺触发取证（用户要求："目前把冲刺做出来"，绑定 Shift）。
    ///
    /// ── 为什么要先取证，而不是直接反射调用 `DashStart()` ─────────────────
    ///   血泪教训：上一个功能（休息姿态）我直接反射调了 `DashReadyFirst()`
    ///   并拦截 `DashCancel`，结果**搞错了对象** —— `DashReady` 其实是
    ///   **左键长按蓄力**姿态，拦截它的取消逻辑会破坏游戏正常玩法。
    ///   ⇒ 这次先看**原版战斗里 `DashStart()` 到底会不会被调用**：
    ///       · 会 → 说明冲刺是正常战斗动作，我们照原版触发方式做即可
    ///       · 不会 → 说明它是特殊条件下的动作，需要先搞清触发条件
    ///
    /// ── 反编译依据 ─────────────────────────────────────────────────────────
    ///   `PlayerControlCustom`：
    ///       public    override void DashNotify();   // L781
    ///       public    override void DashWay();      // L786
    ///       protected override void DashStart();    // L791 ★
    ///       protected override void Dash();         // L766
    ///       protected override void DashIm();       // L771
    ///       public    void ChangeDash(DASH key);    // L991
    ///   `DASH` 枚举：VECTOR_STRAIR / PAHNTOM_STRAIR / FAIRY_STRAIR
    ///   `DashList` 实测 3 项：LongRange 60/65/70，CountMax 35/32/30，FAIRY_STRAIR 的 DashAvoid=True
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 纯只读取证（只记日志），不改变任何游戏行为
    ///   · 节流：冲刺可能很频繁，每 N 次记录一次
    /// </summary>
    private static void Postfix_DashStart(PlayerControlCustom __instance)
    {
        try
        {
            _dashStartSeen++;
            if (_dashStartSeen == 1 || _dashStartSeen % DASH_START_LOG_EVERY == 0)
            {
                Log.LogInfo($"[DASH] DashStart observed (count={_dashStartSeen})"
                            + " - vanilla does trigger the dash");
            }
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"[DASH] Postfix_DashStart failed: {ex.Message}");
        }
    }

    private static int _dashStartSeen;
    // DashStart 日志节流（次）
    private const int DASH_START_LOG_EVERY = 10;

    /// <summary>
    /// 拦截游戏"呼出光标"的调用（用户指出的问题）。
    ///
    /// ── 用户实测与澄清 ─────────────────────────────────────────────────────
    ///   用户原话：「原版游戏移动鼠标呼出光标!」
    ///   ⇒ 这是**原版的既定行为**：鼠标一动，游戏就调 `CursorShow(true)` +
    ///     `MoveCursorToBottomRight()` 把光标亮出来（方便点 UI）。
    ///
    ///   但对我们的模式是灾难：F3 要用鼠标瞄准，光标就会满屏乱飘、
    ///   飘到 UI/敌人上造成误触（"自己打开菜单 / 切敌人信息"）。
    ///
    /// ── 对策：拦掉原版的"呼出"，而不是自己硬锁 ─────────────────────────────
    ///   直接不让 `CursorShow(true)` 生效，比抢着写 `Cursor.visible` 干净：
    ///   游戏仍能正常调 `CursorShow(false)`（我们放行），只是"呼出"被拦。
    ///
    ///   反编译依据（`GameManagerBase`）：
    ///       protected void CursorShow(bool show)          // L1169
    ///       protected void MoveCursorToBottomRight()      // L1164
    ///       private static extern bool SetCursorPos(int X, int Y);   // L636
    ///
    /// ── 放行条件（必须放行，否则玩家点不了菜单）────────────────────────────
    ///   · 原版模式（`_mode == Vanilla`）—— 原版行为一丝不改
    ///   · 菜单打开时（暂停 / 设置 / 弹窗）—— 否则没法用鼠标点菜单
    /// </summary>
    private static bool Prefix_CursorShow(bool show)
    {
        try
        {
            // 只在"呼出"时考虑拦截；"隐藏"（show=false）永远放行
            if (!show)
            {
                return true;
            }
            if (_mode == FrameworkMode.Vanilla)
            {
                return true;   // 原版模式：保持原版行为
            }
            if (IsGameMenuOpen())
            {
                return true;   // 菜单里需要鼠标
            }

            _cursorShowBlockedCount++;
            if (_cursorShowBlockedCount == 1
                || _cursorShowBlockedCount % CURSOR_BLOCK_LOG_EVERY == 0)
            {
                Log.LogInfo($"[CURSOR] blocked the game's CursorShow(true)"
                            + $" (count={_cursorShowBlockedCount})"
                            + " - full-screen loop cursor stays active");
            }
            return false;   // 跳过原方法 → 光标不会被呼出
        }
        catch (Exception ex)
        {
            // 出错时**放行**（让游戏照常运行），但必须留痕
            Log.LogError($"[CURSOR] Prefix_CursorShow failed, letting the game proceed: {ex.Message}");
            return true;
        }
    }

    private static int _cursorShowBlockedCount;
    // CursorShow 拦截日志节流（次）—— 鼠标每动一下都可能触发，必须节流
    private const int CURSOR_BLOCK_LOG_EVERY = 30;

    /// <summary>
    /// 拦截 `MoveCursorToBottomRight()` —— 它是"呼出光标"的配套动作
    /// （把光标挪到右下角，配合 CursorShow 形成"鼠标一动光标就出现"的效果）。
    /// 拦截条件与 `Prefix_CursorShow` 完全一致，保证两者不会只拦一半。
    /// </summary>
    private static bool Prefix_MoveCursorToBottomRight()
    {
        try
        {
            if (_mode == FrameworkMode.Vanilla || IsGameMenuOpen())
            {
                return true;
            }
            return false;   // 跳过：不要把光标挪出来
        }
        catch (Exception ex)
        {
            Log.LogError($"[CURSOR] Prefix_MoveCursorToBottomRight failed,"
                         + $" letting the game proceed: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 游戏内光标管理：**全屏循环光标**（用户指出的根因）。
    ///
    /// ── 问题（用户诊断，完全正确）────────────────────────────────────────
    ///   用户原话：「因为原版内容是不会移动鼠标的，所以应该是这两个鼠标移动的问题，
    ///             我鼠标移动光标就出现了，没做基础的全屏循环光标」
    ///
    ///   机制：
    ///     · 原版不消费鼠标位移 ⇒ 光标停在原地不显眼
    ///     · 我们的 F2/F3 读 `Input.mousePosition` 做差分 ⇒ **鼠标真的在屏幕上移动**
    ///     · 而我们从没做过 `Cursor.lockState = Locked` ⇒ 光标跑到屏幕边缘、
    ///       飘到 UI 上，于是"自己打开菜单 / 切敌人信息"
    ///
    /// ── 正解：`CursorLockMode.Locked` = 全屏循环光标 ───────────────────────
    ///   锁定后：
    ///     · 光标被固定在屏幕中心且**不可见**
    ///     · `Input.mousePosition` **仍然返回移动增量**（Unity 的既定行为）
    ///   ⇒ 我们照常读差分，但屏幕上不会出现光标，也不会误触 UI。
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 只在"我们的模式 + 游戏未暂停"时锁定；返回原版模式时还原
    ///   · 暂停/设置菜单打开时**不锁**（否则玩家没法点菜单）
    ///   · 自由摄像机（mode B）自己管光标，此时本方法让位（避免两处打架）
    /// </summary>
    private static void UpdateCursorLock(PlayerControlCustom player)
    {
        try
        {
            // 我们的模式在跑、且没有接管相机的自由摄像机 → 由本方法管光标
            bool ourMode = _mode != FrameworkMode.Vanilla;

            // 菜单打开时不锁（玩家要能用鼠标点菜单）
            bool menuOpen = IsGameMenuOpen();

            bool wantLock = ourMode && !menuOpen;

            if (wantLock)
            {
                if (Cursor.lockState != CursorLockMode.Locked)
                {
                    _savedCursorLock = Cursor.lockState;
                    _savedCursorVisible = Cursor.visible;
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                    _cursorLockedByUs = true;
                    Log.LogInfo("[CURSOR] full-screen loop cursor engaged"
                                + " (Locked + hidden) - mouse deltas still readable");
                }
            }
            else if (_cursorLockedByUs)
            {
                Cursor.lockState = _savedCursorLock;
                Cursor.visible = _savedCursorVisible;
                _cursorLockedByUs = false;
                Log.LogInfo("[CURSOR] cursor restored"
                            + $" (lock={_savedCursorLock}, visible={_savedCursorVisible})");
            }
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"[CURSOR] lock update failed: {ex.Message}");
        }
    }

    private static bool _cursorLockedByUs;
    private static CursorLockMode _savedCursorLock = CursorLockMode.None;
    private static bool _savedCursorVisible = true;

    /// <summary>
    /// 游戏菜单是否打开（暂停 / 设置）。
    /// 读 `BattleManager` 的公开字段，不猜名字以外的逻辑。
    /// 读失败时按"没打开"处理（保守：宁可不锁也不要让玩家点不了菜单）。
    /// </summary>
    private static bool IsGameMenuOpen()
    {
        try
        {
            BattleManager? mgr = BattleManager.GetInstance();
            if (mgr is null)
            {
                return false;
            }
            GameObject? pause = mgr.PauseScreen;
            if (pause is not null && pause.activeInHierarchy)
            {
                return true;
            }
            GameObject? setting = mgr.SettingMenu;
            if (setting is not null && setting.activeInHierarchy)
            {
                return true;
            }
            GameObject? modal = mgr.Modal;
            if (modal is not null && modal.activeInHierarchy)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            // 读不到就当作没打开（保守），但记录一次便于排查
            LogOnce($"IsGameMenuOpen failed: {ex.Message}", ref _menuCheckFailedLogged);
        }
        return false;
    }

    private static bool _menuCheckFailedLogged;

    ///
    /// ── 为什么换点（实测结果）──────────────────────────────────────────────
    ///   挂了 `DashStart` 之后实测：**原版战斗里一条 `[DASH]` 都没有** ——
    ///   说明冲刺不走 `DashStart()`。`PlayerControlCustom` 里冲刺相关的
    ///   可调用点还剩 `DashIm()` / `DashWay()` / `DashNotify()` / `Dash()`，
    ///   其中 `DashIm`（Instant）最可能是"按键立刻位移"的那个。
    ///
    ///   注意：`Dash()` 是每帧状态方法（冲刺"进行中"），挂它容易刷屏；
    ///   `DashIm` 是瞬时动作，命中率更高且日志更干净，所以先挂它。
    /// </summary>
    private static void Postfix_DashIm(PlayerControlCustom __instance)
    {
        try
        {
            _dashImSeen++;
            if (_dashImSeen == 1 || _dashImSeen % DASH_START_LOG_EVERY == 0)
            {
                Log.LogInfo($"[DASH] DashIm observed (count={_dashImSeen})");
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"[DASH] Postfix_DashIm failed: {ex.Message}");
        }
    }

    private static int _dashImSeen;

    /// <summary>
    /// 冲刺取证第三点：`DashWay`（冲刺方向/路径）。
    /// 与 `DashIm` 一起挂 —— 哪个有日志，哪个就是按键触发的入口。
    /// </summary>
    private static void Postfix_DashWay(PlayerControlCustom __instance)
    {
        try
        {
            _dashWaySeen++;
            if (_dashWaySeen == 1 || _dashWaySeen % DASH_START_LOG_EVERY == 0)
            {
                Log.LogInfo($"[DASH] DashWay observed (count={_dashWaySeen})");
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"[DASH] Postfix_DashWay failed: {ex.Message}");
        }
    }

    private static int _dashWaySeen;

    /// <summary>
    /// 动作流切换取证（找到"休息姿态"是哪个 ACTION_FLOW）。
    ///
    /// ── 为什么要这个 ───────────────────────────────────────────────────────
    ///   用户描述的"打赢每波敌人的休息姿态"有三个特征：
    ///     「那个状态下爱迪尔无法攻击，只能移动，锁定也消失了」
    ///   静态数据里查不到（`Standby`/`Victory`/`IdlePose` 等字符串全部缺失），
    ///   因为它是**运行时状态** —— 由 `ActionFlowManager` 的流程机驱动。
    ///
    /// ── 反编译依据 ─────────────────────────────────────────────────────────
    ///   `public enum ACTION_FLOW`：
    ///       READY_ENEMYS, TIMELINE, ACTION, LOADING, UNLOADING, CONVERSATION,
    ///       NEXT_SCENE, BGM_START, BGM_STOP, **WAIT**, COMMAND, BGM_CHANGE,
    ///       TIMELINE_ASYNC, DELETE_ENEMY, READY_ENEMYS_LOOP, GAME_END, DRAIN_ROOM
    ///   `ActionFlowContainer` 有 `public string FlowName;` —— 直接给出流名。
    ///   `ActionFlowManager.StartFlow(int flowCnt)` 是每次进入新流的入口。
    ///
    /// ⇒ 挂钩 `StartFlow` 把流名记下来。用户进入休息状态时，日志里最后一个流
    ///   就是答案 —— 之后即可按该流做条件（例如该流下不接管相机/锁定）。
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 只读 + 只记日志，**不改变任何游戏行为**（纯取证，零风险）
    ///   · 反射读取 `FlowName` 失败时明确记录，不静默
    /// </summary>
    private static void Postfix_StartFlow(ActionFlowManager __instance, int flowCnt)
    {
        try
        {
            string name = "?";
            try
            {
                // FlowName 是 ActionFlowManager 的公开字段
                name = __instance.FlowName ?? "null";
            }
            catch (Exception ex)
            {
                name = $"<read failed: {ex.Message}>";
            }
            Log.LogInfo($"[FLOW] StartFlow(flowCnt={flowCnt}) FlowName='{name}'");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"[FLOW] Postfix_StartFlow failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 波次结束取证：`ActionFlowManager.ActionFinish()`（用户描述的"冲刺姿态"入口）。
    ///
    /// ── 用户给的关键事实（反复澄清后的定论）─────────────────────────────────
    ///   「没有按键。在**每波战斗后自动进入**这个姿态」
    ///   三大特征：「**以爱迪尔为中心**，**无法攻击**，**失去锁定**」
    ///
    /// ── 反编译依据（波次切换管线）─────────────────────────────────────────
    ///   `ActionFlowManager`：
    ///       StartFlow(int flowCnt)       // 进入某个流（已挂探针，但只在进章节时触发）
    ///       MonitorFlow(int flowContCnt) // 协程：监视当前流
    ///       ConditionCheck()             // 每帧检查本波是否结束
    ///       IsExterminated()             // 敌人全灭
    ///       ActionFinish() / ActionFinishForce()   // ★ 波次结束 → 切下一个流
    ///       EventFinish() / ReadyNextEnemys(...)
    ///   `FINISH_CONDITION`：NONE/TIMEOUT/EXTERMINATION/EVENT/
    ///                       ALLHP_DECREASES/ALLNUM_DECREASES/BOSSHP_DECREASES/FLAG
    ///
    /// ⇒ 上轮只挂 `StartFlow` 抓不到波次切换（它只在章节开始时触发一次）。
    ///   本方法挂在 **波次结束** 的出口上，用户打完一波时日志会直接给出
    ///   当时的 `Flow` / `Condition` / 敌人数，从而定位那个姿态。
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 纯只读取证（只记日志），不改变任何游戏行为
    /// </summary>
    private static void Postfix_ActionFinish(ActionFlowManager __instance)
    {
        LogFlowSnapshot(__instance, "ActionFinish");
    }

    /// <summary>波次结束的另一条路径（强制结束）。</summary>
    private static void Postfix_ActionFinishForce(ActionFlowManager __instance)
    {
        LogFlowSnapshot(__instance, "ActionFinishForce");
    }

    /// <summary>
    /// 记录当前流的快照。
    ///
    /// ── ⚠️ 字段归属（我反复搞错的地方，最终定论）────────────────────────────
    ///   `Flow` / `Timeline` / `Condition` / `IsFinish` / `EnemyBaseList` / `NextFlow`
    ///   全部属于 **`ActionFlowManager.ActionFlowContainer`**（**嵌套类**）。
    ///   ILSpy 的 `-t ActionFlowManager` 会把嵌套类一起输出，导致我误读成 manager 的字段。
    ///   interop 存根的权威输出证实：这些成员挂在 `ActionFlowContainer` 上。
    ///
    ///   ⇒ 编译期**无法**写 `mgr.Flow` / `mgr.flowContCnt`（都不存在）。
    ///     所以本方法改为**运行时反射枚举** `ActionFlowManager` 的全部字段/属性：
    ///     凡是"元素类型为 ActionFlowContainer 的集合"或"ActionFlowContainer 实例"，
    ///     就把它的内容打出来。这样**不需要预先知道字段名**，也就不会再搞错归属。
    ///
    /// ── 边界 ───────────────────────────────────────────────────────────────
    ///   · 纯只读取证（只记日志），不改变任何游戏行为
    ///   · 只在第一次快照时枚举全部流（避免刷屏）
    /// </summary>
    private static void LogFlowSnapshot(ActionFlowManager mgr, string where)
    {
        try
        {
            Log.LogInfo($"[FLOW] {where} (reflection dump)");
            Type mt = mgr.GetType();
            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance;

            bool dumpedList = false;
            foreach (System.Reflection.FieldInfo f in mt.GetFields(Flags))
            {
                object? v;
                try
                {
                    v = f.GetValue(mgr);
                }
                catch (Exception)
                {
                    continue;   // 个别字段读不到就跳过，不影响其余
                }
                if (v is null)
                {
                    continue;
                }

                // 情形 A：字段本身是 ActionFlowContainer
                if (v is ActionFlowManager.ActionFlowContainer single)
                {
                    Log.LogInfo($"[FLOW]   {f.Name} = Flow:{single.Flow}"
                                + $" Condition:{single.Condition}"
                                + $" IsFinish:{single.IsFinish}"
                                + $" enemies:{(single.EnemyBaseList is not null ? single.EnemyBaseList.Count : -1)}");
                    continue;
                }

                // 情形 B：字段是 ActionFlowContainer 的集合
                if (v is System.Collections.IEnumerable seq && v is not string)
                {
                    var items = new List<ActionFlowManager.ActionFlowContainer>();
                    foreach (object? o in seq)
                    {
                        if (o is ActionFlowManager.ActionFlowContainer c)
                        {
                            items.Add(c);
                        }
                    }
                    if (items.Count == 0)
                    {
                        continue;
                    }
                    Log.LogInfo($"[FLOW]   {f.Name}: {items.Count} container(s)"
                                + $" | int fields: {IntFieldsOf(mgr, Flags)}");
                    if (!dumpedList)
                    {
                        dumpedList = true;
                        for (int i = 0; i < items.Count; i++)
                        {
                            Log.LogInfo($"[FLOW]     [{i}] Flow={items[i].Flow}"
                                        + $" Condition={items[i].Condition}"
                                        + $" NextFlow='{items[i].NextFlow ?? "null"}'");
                        }
                    }
                }
            }
            _flowListDumped = true;
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"[FLOW] LogFlowSnapshot({where}) failed: {ex.Message}");
        }
    }

    /// <summary>列出对象的全部 int 字段（用来定位"当前流索引"这类计数器）。</summary>
    private static string IntFieldsOf(object target, System.Reflection.BindingFlags flags)
    {
        var parts = new List<string>();
        try
        {
            foreach (System.Reflection.FieldInfo f in target.GetType().GetFields(flags))
            {
                if (f.FieldType == typeof(int))
                {
                    object? v = f.GetValue(target);
                    parts.Add($"{f.Name}={v}");
                }
            }
        }
        catch (Exception)
        {
            // 列不出来就返回空串，不拖垮调用方
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }

    private static bool _flowListDumped;

    /// <summary>
    /// 反射读一个字段/属性并转成字符串（读不到返回 "?"）。
    /// </summary>
    private static string ReflectionString(object target, string name)
    {
        try
        {
            Type t = target.GetType();
            var f = AccessTools.Field(t, name);
            if (f is not null)
            {
                object? v = f.GetValue(target);
                return v?.ToString() ?? "null";
            }
            var p = AccessTools.Property(t, name);
            if (p is not null && p.CanRead)
            {
                object? v = p.GetValue(target);
                return v?.ToString() ?? "null";
            }
        }
        catch (Exception)
        {
            // 读不到就是 "?"，由调用方决定怎么显示
        }
        return "?";
    }

    /// <summary>
    /// 玩家弹药"没锁定时强制直线飞"（用户定稿方案）。
    ///
    /// 用户原话：「没锁定就强制直线飞呗」
    ///
    /// ── 为什么用这个 patch 点 ──────────────────────────────────────────────
    ///   `ShotController.InitShot(bool isPlayer, SylpheedController player = null)`
    ///   是弹药的初始化入口，**自带 `isPlayer` 参数** —— 能精确区分玩家/敌人弹药，
    ///   不需要额外判断（这一点比在 `BattleManager.RegistShotInfo` 上挂钩好）。
    ///
    /// ── 判定"没锁定" ──────────────────────────────────────────────────────
    ///   `PlayerControlCustom.LockonTargetCharacter`：
    ///     · 等于我们的锚点 ⇒ F3 自由瞄准（**没有真正的敌人锁定**）⇒ 强制直线
    ///     · 等于某个敌人   ⇒ 有真实锁定 ⇒ 保持游戏原生制导
    ///
    /// ── 为什么调 `NoHoming()` 而不是直接改字段 ────────────────────────────
    ///   `NoHoming()` 是游戏自己的关制导方法，内部会把 `IsHoming` / `Target` /
    ///   `FixedTarget` 等一串状态一起处理。手改单个字段容易漏（吃过教训）。
    ///
    /// ── 边界 ──────────────────────────────────────────────────────────────
    ///   · 只在非原版模式动作（原版模式一行都不碰）
    ///   · 只处理 `isPlayer == true` 的弹药；敌人弹药保持游戏行为
    ///   · 失败记日志，不静默吞掉
    /// </summary>
    private static void Postfix_InitShot(ShotController __instance, bool isPlayer)
    {
        try
        {
            if (!isPlayer)
            {
                return;   // 敌人弹药：完全不动，保持游戏原生行为
            }
            if (_mode == FrameworkMode.Vanilla)
            {
                return;   // 原版模式：不介入
            }

            // 判定：是否有"真实敌人"锁定
            PlayerControlCustom? player = _player;
            CharacterControllerBase? anchor = _aimAnchor?.Controller;
            if (player is null)
            {
                return;
            }
            CharacterControllerBase? lockTarget = player.LockonTargetCharacter;
            bool realEnemyLocked = lockTarget is not null
                                   && anchor is not null
                                   && lockTarget != anchor;

            if (realEnemyLocked)
            {
                return;   // 锁着真敌人 → 让制导照常工作（能追移动目标）
            }

            // 没锁定（或锁定的是我们的锚点）⇒ 强制直线飞
            __instance.NoHoming();
            _noHomingAppliedCount++;
            if (_noHomingAppliedCount == 1 || _noHomingAppliedCount % NO_HOMING_LOG_EVERY == 0)
            {
                Log.LogInfo($"[SHOT] no lock -> forced straight flight via NoHoming()"
                            + $" (count={_noHomingAppliedCount})");
            }
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"[SHOT] Postfix_InitShot failed: {ex.Message}");
        }
    }

    private static int _noHomingAppliedCount;
    // NoHoming 日志节流：弹药可能很密集，每 N 发记录一次
    private const int NO_HOMING_LOG_EVERY = 20;

    /// <summary>
    /// 取场景里所有 vcam（给相机距离缩放用）。
    /// 与 GameCameras 的区别：那个返回固定的 6 个锁定相机；这里要的是全部，
    /// 因为当前激活的可能是任意一个（Dash / FingerLine 等也会渲染）。
    /// </summary>
    private static Cinemachine.CinemachineVirtualCameraBase[]? AllCameras()
    {
        try
        {
            return UnityEngine.Object.FindObjectsOfType<Cinemachine.CinemachineVirtualCameraBase>();
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            Log.LogError($"AllCameras() failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 恢复原锁定目标。幂等；没改过就直接返回。
    ///
    /// ⚠️ 只在"确实覆盖过"时才写回 —— 避免在原版模式下无谓地写这个敏感字段。
    /// </summary>
    private static void RestoreLockTargetOverride(PlayerControlCustom player)
    {
        if (!_lockTargetOverridden)
        {
            return;
        }
        try
        {
            player.LockonTargetCharacter = _savedLockTarget;
            Log.LogInfo($"[LOCKTARGET] override OFF: restored to"
                        + $" '{(_savedLockTarget is not null && _savedLockTarget.transform is not null ? _savedLockTarget.transform.name : "null")}'");
        }
        catch (Exception ex)
        {
            Log.LogError($"[LOCKTARGET] restore failed: {ex.Message}");
        }
        finally
        {
            _lockTargetOverridden = false;
            _savedLockTarget = null;
        }
    }

    /// <summary>
    /// ★ F3 索敌与距离调节（用户定稿玩法）。
    ///
    /// 用户原话：
    /// 「在F3模式下，滚轮调整锚点离爱迪尔的距离，
    ///   长按中键会用锚点位置会变成最近的一个敌人坐标，跟死」
    /// 「中键锁定敌人后，优先级会盖过爱迪尔。你可以理解为一个 while 里面的
    ///   if 中键锁定，而爱迪尔锚点是 else。这个也简单，就是判断当前锚点位置与
    ///   哪个敌人最近，然后将锚点位置锁定为敌人而不是爱迪尔。
    ///   这样的话也不会因为旋转而出问题了，因为旋转是爱迪尔锚点那里才会容许的」
    ///
    /// 实现要点：
    ///   ① 滚轮调 `_currentAnchorDistance`（就近/就远，夹在配置范围内）
    ///   ② 中键按住 → 找【离当前锚点位置最近】的敌人（注意：不是离爱迪尔最近 ——
    ///      锚点才是瞄准意向的载体）→ 让锚点锁定该敌人坐标
    ///   ③ 中键松开 → 解除锁定，锚点回到"绕爱迪尔轨道"分支
    /// </summary>
    private static void UpdateAnchorTargeting(PlayerControlCustom player)
    {
        try
        {
            // ---- ① 三档距离切换（Q 远 / E 近，循环；见字段区注释）----
            try
            {
                bool up = Input.GetKeyDown(_tierUpKeyCode);
                bool down = Input.GetKeyDown(_tierDownKeyCode);
                if (up && !down)
                {
                    SetTier(_tierIndex + 1);
                }
                else if (down && !up)
                {
                    SetTier(_tierIndex - 1);
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"read tier keys failed: {ex.Message}");
            }

            // ---- ② 中键：锁定 / 解锁（if 分支优先于 else 分支）----
            bool held;
            try
            {
                held = Input.GetKey(_lockAimKeyCode);
            }
            catch (Exception ex)
            {
                Log.LogError($"read lock-aim key failed: {ex.Message}");
                held = false;
            }

            if (held)
            {
                // 找离"当前锚点位置"最近的敌人
                Vector3 anchorPos = _aimAnchor?.Transform is not null
                    ? _aimAnchor.Transform!.position
                    : player.transform.position;
                CharacterControllerBase? nearest = FindNearestEnemy(player, anchorPos, out float dist);

                if (nearest is not null && nearest.transform is not null)
                {
                    _lockedEnemy = nearest;
                    _aimAnchor?.LockTo(nearest.transform.position, LOCK_SNAP_RATE);
                    if (!_lockKeyHeld)
                    {
                        Log.LogInfo($"[ANCHOR] LOCK ON '{nearest.transform.name}'"
                                    + $" distFromAnchor={dist:F1}m (middle mouse held)");
                    }
                }
                else if (!_lockKeyHeld)
                {
                    // 没有找到敌人：明确记录，不静默装作锁上了
                    Log.LogInfo($"[ANCHOR] lock requested but no enemy within"
                                + $" {TARGET_SEARCH_RADIUS:F0}m of the anchor"
                                + $" (anchorPos=({anchorPos.x:F1},{anchorPos.y:F1},{anchorPos.z:F1}))");
                }
            }
            else if (_lockKeyHeld)
            {
                // 刚松开：解除锁定，回到爱迪尔轨道分支
                _aimAnchor?.Unlock();
                _lockedEnemy = null;
                Log.LogInfo("[ANCHOR] LOCK OFF (released) -> back to character orbit");
                // ★ 事件②：长按中键结束时夺回一次锁定目标（含强制重绑渲染）
                //   用户要求：「长按中键结束后再判定一次阿，就2种情况可能出现被夺的情况」
                ReclaimLockTarget("lock released");
            }
            _lockKeyHeld = held;
        }
        catch (Exception ex)
        {
            Log.LogError($"UpdateAnchorTargeting failed: {ex}");
        }
    }

    // 距离调节改用 GetKeyDown（单击一档）后，每次变化都是一次独立事件，
    // 无需节流 —— 原先的 LogDistanceChange 与计数器已移除。

    /// <summary>
    /// 切换锚点距离档位（用户玩法：Q/E 循环切「远/中/近」）。
    /// 档位索引夹在 [0,2]，到端点就不再越界（不循环回绕 —— 避免"想拉远却突然变最近"）。
    /// </summary>
    private static void SetTier(int index)
    {
        int clamped = Math.Max(0, Math.Min(2, index));
        if (clamped == _tierIndex)
        {
            return;   // 已在端点，不重复记录
        }
        _tierIndex = clamped;
        _currentAnchorDistance = TierDistance(clamped);
        Log.LogInfo($"[ANCHOR] tier -> {TierName(clamped)}"
                    + $" ({_currentAnchorDistance:F1}m) [Q=far / E=near]");
    }

    /// <summary>档位索引对应的距离（从配置读，不写死数值）。</summary>
    private static float TierDistance(int index)
    {
        return index switch
        {
            0 => _cfgTierNear.Value,
            2 => _cfgTierFar.Value,
            _ => _cfgTierMid.Value,
        };
    }

    /// <summary>档位名（日志用）。</summary>
    private static string TierName(int index) => index switch
    {
        0 => "NEAR",
        2 => "FAR",
        _ => "MID",
    };

    /// <summary>
    /// 把游戏锁定标记移动到我们的锚点位置（用户问的"边框渲染"）。
    ///
    /// 为什么要这么做：实测（CAMPROBE）确认渲染链**不读**锁定目标字段 ——
    /// `LockonMarker` 挂在敌方 Spine 下、离锚点 15~39 米；
    /// 所以只改锁定目标无法让边框跟到锚点，只能自己搬它。
    ///
    /// 实现要点：
    ///   · 目标对象按名字查找并**缓存**（每帧 Find 会有明显开销）
    ///   · 找不到就明确记录一次，不静默失败
    ///   · 只写世界坐标；缩放/旋转仍由游戏的 Tweener 负责，减少冲突面
    /// </summary>
    private static void MoveLockMarkerToAnchor(Transform? anchor)
    {
        if (anchor is null)
        {
            return;
        }
        try
        {
            if (_lockMarkerCache is null)
            {
                _lockMarkerCache = FindTransformByName(_cfgLockMarkerName.Value);
                if (_lockMarkerCache is null)
                {
                    LogOnce($"lock marker object '{_cfgLockMarkerName.Value}' not found -"
                            + " the frame will keep following the game's own target",
                            ref _lockMarkerMissingLogged);
                    return;
                }
                Log.LogInfo($"[MARKER] found '{_lockMarkerCache.name}'"
                            + $" (parent={(_lockMarkerCache.parent is not null ? _lockMarkerCache.parent.name : "null")})"
                            + " - repositioning to the aim anchor");
            }
            _lockMarkerCache.position = anchor.position;
        }
        catch (Exception ex)
        {
            // 对象可能在切场景时被销毁 —— 清缓存以便下帧重找，并记录
            _lockMarkerCache = null;
            Log.LogError($"[MARKER] move failed, will re-lookup next frame: {ex.Message}");
        }
    }

    private static bool _lockMarkerMissingLogged;

    /// <summary>
    /// 按名字在场景里找一个 Transform。
    /// 用 FindObjectsOfType&lt;Transform&gt; 而不是 GameObject.Find ——
    /// 后者只找**激活**的对象，而锁定标记在未锁定时可能是隐藏的。
    /// </summary>
    private static Transform? FindTransformByName(string name)
    {
        try
        {
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] is not null && all[i].name == name)
                {
                    return all[i];
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"FindTransformByName('{name}') failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 找离给定位置最近的敌人（用游戏自己的可锁定敌人列表，不自己遍历场景）。
    ///
    /// 为什么用 `GetLockonEnemys()`：它是游戏判断"谁可锁定"的权威来源，
    /// 天然排除了不可锁定的对象（场景装饰、已死亡单位等）。
    /// 备选 `SemiLockonTargetList`（半锁定候选）在列表为空时兜底。
    /// </summary>
    private static CharacterControllerBase? FindNearestEnemy(
        PlayerControlCustom player, Vector3 from, out float bestDist)
    {
        bestDist = float.MaxValue;
        CharacterControllerBase? best = null;
        try
        {
            best = ScanList(player.GetLockonEnemys(), from, ref bestDist, best);
            if (best is null)
            {
                // 兜底：半锁定候选列表（游戏在扫描过程中的中间结果）
                best = ScanList(player.SemiLockonTargetList, from, ref bestDist, best);
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"FindNearestEnemy failed: {ex.Message}");
        }
        return best;
    }

    /// <summary>在敌人列表里找离 from 最近的一个（超过搜索半径忽略）。</summary>
    private static CharacterControllerBase? ScanList(
        Il2CppSystem.Collections.Generic.List<CharacterControllerBase>? list,
        Vector3 from, ref float bestDist, CharacterControllerBase? best)
    {
        if (list is null)
        {
            return best;
        }
        int n = list.Count;
        for (int i = 0; i < n; i++)
        {
            CharacterControllerBase? e = list[i];
            if (e is null || e.transform is null)
            {
                continue;
            }
            float d = Vector3.Distance(from, e.transform.position);
            if (d > TARGET_SEARCH_RADIUS)
            {
                continue;   // 超出搜索半径：不算"最近敌人"
            }
            if (d < bestDist)
            {
                bestDist = d;
                best = e;
            }
        }
        return best;
    }

    /// <summary>
    /// 把六个"实际会被激活的"锁定相机的 LookAt 指向敌人本体。
    /// 由每帧调用（幂等：已是敌人则不重复写）。
    ///
    /// 覆盖范围来自实测（见 CAMERA_STRUCTURE.md 3.5.2）：
    ///   主力 LockonLong（prio=100，激活 34 次）与 DashReady（冲刺蓄力）——
    ///   它们的 lookAt 原本是【玩家自己】，从没指向过敌人；
    ///   四个单锁相机的 lookAt 是【玩家+敌人的包围盒中心】（垂直分量是两人高度平均）。
    /// </summary>
    private static void ApplyLockCameraAim(PlayerControlCustom player)
    {
        try
        {
            Transform pt = player.transform;
            Transform? enemy = CameraAimTuner.ResolveLockTarget(player);

            _tunerCrossSingle?.AimAtGroupOrEnemy("LockonCrossSingle",
                player.VCameraLockonCrossSingle, player.TargetGroupSingle, pt, enemy);
            _tunerMiddleSingle?.AimAtGroupOrEnemy("LockonMiddleSingle",
                player.VCameraLockonMiddleSingle, player.TargetGroupSingle, pt, enemy);
            _tunerCrossCross?.AimAtGroupOrEnemy("LockonCrossCross",
                player.VCameraLockonCrossCross, player.TargetGroupSingle, pt, enemy);
            // TargetGroupMulti 没暴露在 PlayerControlCustom 上，传 null 由 vcam 自行解析
            _tunerCrossMulti?.AimAtGroupOrEnemy("LockonCrossMulti",
                player.VCameraLockonCrossMulti, null, pt, enemy);

            _tunerLong?.AimAt("LockonLong", player.VCameraLockonLong, enemy);
            _tunerDashReady?.AimAt("DashReady", player.VCameraDashReady, enemy);
        }
        catch (Exception ex)
        {
            Log.LogError($"ApplyLockCameraAim failed: {ex}");
        }
    }

    /// <summary>
    /// 还原所有被 ApplyLockCameraAim 改过的 LookAt 为最初值。
    /// 幂等：没改过就直接返回。原版模式 / 离开自由模式时调用。
    /// </summary>
    private static void RestoreLockCameraAim(PlayerControlCustom player)
    {
        try
        {
            _tunerCrossSingle?.Restore("LockonCrossSingle", player.VCameraLockonCrossSingle);
            _tunerMiddleSingle?.Restore("LockonMiddleSingle", player.VCameraLockonMiddleSingle);
            _tunerCrossCross?.Restore("LockonCrossCross", player.VCameraLockonCrossCross);
            _tunerCrossMulti?.Restore("LockonCrossMulti", player.VCameraLockonCrossMulti);
            _tunerLong?.Restore("LockonLong", player.VCameraLockonLong);
            _tunerDashReady?.Restore("DashReady", player.VCameraDashReady);
        }
        catch (Exception ex)
        {
            Log.LogError($"RestoreLockCameraAim failed: {ex}");
        }
    }

    /// <summary>
    /// 计算当前锁定目标的"瞄准点"世界坐标。
    ///
    /// 为什么不能直接用 transform.position：
    ///   角色根节点通常在脚底，而游戏自己的 CharacterControllerBase.MyCenterOffset
    ///   就是用来把瞄准点抬到身体中心的。忽略它会导致相机瞄向脚底。
    /// </summary>
    private static Vector3? GetAimPoint()
    {
        try
        {
            PlayerControlCustom? player = _player;
            if (player is null)
            {
                return null;
            }
            CharacterControllerBase? tgt = player.LockonTargetCharacter;
            if (tgt is null)
            {
                return null;
            }
            Vector3 pos = tgt.transform.position;
            float centerOffset;
            try
            {
                centerOffset = tgt.MyCenterOffset;
            }
            catch (Exception ex)
            {
                Log.LogError($"read MyCenterOffset failed: {ex.Message}");
                centerOffset = 0f;
            }
            return new Vector3(pos.x, pos.y + centerOffset, pos.z);
        }
        catch (Exception ex)
        {
            Log.LogError($"GetAimPoint failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 列出全部虚拟相机的配置，用于对比"平常状态"与"自动对准状态"的差异。
    ///
    /// 出发点（用户观察）：冲刺 / 开大 / 抓取时相机会自动对准敌人（有 Z 轴适配），
    ///   只有平常锁定状态没有。所以正确做法不是外部硬掰旋转，
    ///   而是找出前者配了什么、后者缺什么。
    ///
    /// 每个相机输出：
    ///   · LookAt / Follow 指向谁 —— 自动对准的相机很可能把 LookAt 指向了目标
    ///   · 装有哪些 Cinemachine 组件（Composer=瞄准 / Transposer=跟随位置 / FollowZoom=缩放）
    ///   · Priority 与 State 的落点
    /// </summary>
    private static void ReportVCameras(PlayerControlCustom player)
    {
        try
        {
            // 逐个读取游戏已建好的虚拟相机（字段名来自反编译 PlayerControlCustom）
            var cams = new (string Name, Cinemachine.CinemachineVirtualCamera? Cam)[]
            {
                ("Main", player.VCameraMain),
                ("LockonCrossSingle", player.VCameraLockonCrossSingle),
                ("LockonMiddleSingle", player.VCameraLockonMiddleSingle),
                ("LockonCrossMulti", player.VCameraLockonCrossMulti),
                ("LockonCrossCross", player.VCameraLockonCrossCross),
                ("LockonLong", player.VCameraLockonLong),
                ("DashReady", player.VCameraDashReady),
                ("Dash", player.VCameraDash),
                ("FingerLine", player.VCameraFingerLine),
            };

            foreach (var (name, vcam) in cams)
            {
                if (vcam is null)
                {
                    Log.LogInfo($"[VCAM] {name} = null");
                    continue;
                }
                string lookAt = SafeName(() => vcam.LookAt);
                string follow = SafeName(() => vcam.Follow);
                string comps = DescribeComponents(vcam);
                string state = "n/a";
                try
                {
                    var st = vcam.State;
                    Vector3 sp = st.RawPosition;
                    state = $"rawPos={sp.x.ToString(NUM_FMT)},{sp.y.ToString(NUM_FMT)},{sp.z.ToString(NUM_FMT)}"
                            + $" fov={st.Lens.FieldOfView.ToString(NUM_FMT)}";
                }
                catch (Exception ex)
                {
                    state = $"state=err({ex.Message})";
                }
                string prio;
                try
                {
                    prio = vcam.Priority.ToString();
                }
                catch (Exception ex)
                {
                    prio = $"err({ex.Message})";
                }
                // 注意：不用插值的对齐格式（{x,-20}），因为 IL2CPP 的
                // DefaultInterpolatedStringHandler 不支持 alignment 参数（CS1739）。
                // 改用 PadRight/PadLeft。
                Log.LogInfo($"[VCAM] {name.PadRight(20)} lookAt={lookAt.PadRight(24)} follow={follow.PadRight(24)}"
                            + $" prio={prio.PadRight(5)} comps=[{comps}] {state}");
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"vcam report failed: {ex.Message}");
        }
    }

    /// <summary>安全取 Transform 名字（为 null 时返回 "null"，异常时返回 err）。</summary>
    private static string SafeName(Func<Transform?> getter)
    {
        try
        {
            Transform? t = getter();
            return t is null ? "null" : t.name;
        }
        catch (Exception ex)
        {
            return $"err({ex.Message})";
        }
    }

    /// <summary>
    /// 列出相机装有哪些 Cinemachine 组件。
    /// 组件类型对应语义：Composer/POV=瞄准，Transposer/OrbitalTransposer=位置跟随，
    /// FollowZoom=距离缩放，SameAsFollowTarget 等为其它扩展。
    /// </summary>
    private static string DescribeComponents(Cinemachine.CinemachineVirtualCamera vcam)
    {
        var names = new List<string>();
        void Probe<T>(string label) where T : Cinemachine.CinemachineComponentBase
        {
            try
            {
                if (vcam.GetCinemachineComponent<T>() is not null)
                {
                    names.Add(label);
                }
            }
            catch (Exception ex)
            {
                names.Add($"{label}=err({ex.Message})");
            }
        }
        Probe<Cinemachine.CinemachineComposer>("Composer");
        Probe<Cinemachine.CinemachineTransposer>("Transposer");
        Probe<Cinemachine.CinemachineOrbitalTransposer>("OrbitalTransposer");
        Probe<Cinemachine.CinemachinePOV>("POV");
        try
        {
            if (vcam.GetComponent<Cinemachine.CinemachineFollowZoom>() is not null)
            {
                names.Add("FollowZoom");
            }
        }
        catch (Exception ex)
        {
            names.Add($"FollowZoom=err({ex.Message})");
        }
        return names.Count == 0 ? "none" : string.Join(",", names);
    }

    /// <summary>
    /// 报告"当前真正生效的虚拟相机"及其瞄准配置。
    ///
    /// 为什么必须查这个：此前只知道优先级（LockonCrossSingle/LockonMiddleSingle 在
    /// 战斗中 prio=100），但它们的组件列表是空的 [none]，与实测到的相机位置/朝向
    /// 相矛盾 —— 说明生效的可能不是它们。而"改哪个组件的哪个字段"完全取决于这一点，
    /// 所以必须先确定 active vcam，否则任何修改都是猜。
    ///
    /// 数据来源：CinemachineBrain.ActiveVirtualCamera（Cinemachine 自己的答案）。
    /// </summary>
    private static void ReportActiveVCam(PlayerControlCustom player)
    {
        try
        {
            // ① Cinemachine 自己认为谁在生效
            string activeName = "n/a";
            string activeComponents = "n/a";
            try
            {
                var brain = UnityEngine.Object.FindObjectOfType<Cinemachine.CinemachineBrain>();
                if (brain is null)
                {
                    activeName = "no-brain";
                }
                else
                {
                    var active = brain.ActiveVirtualCamera;
                    if (active is null)
                    {
                        activeName = "null";
                    }
                    else
                    {
                        activeName = active.Name;
                        // 若是虚拟相机，列出它的 Body/Aim 组件
                        if (active.TryCast<Cinemachine.CinemachineVirtualCamera>() is { } vc)
                        {
                            activeComponents = DescribeComponents(vc);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                activeName = $"err({ex.Message})";
            }

            // ② 各 vcam 的优先级（决定谁会生效）
            string prios;
            try
            {
                prios = $"Main={player.VCameraMain?.Priority}"
                        + $" CrossSingle={player.VCameraLockonCrossSingle?.Priority}"
                        + $" MiddleSingle={player.VCameraLockonMiddleSingle?.Priority}"
                        + $" CrossMulti={player.VCameraLockonCrossMulti?.Priority}"
                        + $" Long={player.VCameraLockonLong?.Priority}"
                        + $" Dash={player.VCameraDash?.Priority}";
            }
            catch (Exception ex)
            {
                prios = $"err({ex.Message})";
            }

            Log.LogInfo($"[ACTIVE] active={activeName} comps=[{activeComponents}]");
            Log.LogInfo($"[ACTIVE] prio: {prios}");
        }
        catch (Exception ex)
        {
            Log.LogError($"active vcam report failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 报告目标组（CinemachineTargetGroup）的成员与几何中心。
    ///
    /// 为什么关键：虚拟相机配置对比显示，"自动对准"的那些相机
    /// （Dash → TargetGroup、LockonCrossSingle → TargetGroupSingle）
    /// 都指向 CinemachineTargetGroup，而平常状态用的 Main 指向 AIdealBattle。
    /// CinemachineTargetGroup 会计算组内成员的**三维几何中心** ——
    /// 这正是"相机能跟着高度差对准"的机制来源。
    ///
    /// 所以只要看清各组里放了谁、中心在哪，就能确定：
    ///   · 是"组里根本没放敌人"，还是
    ///   · "放了但 Main 没指向它"
    /// 这两种情况的修法完全不同。
    /// </summary>
    private static void ReportTargetGroups(PlayerControlCustom player)
    {
        var groups = new (string Name, Cinemachine.CinemachineTargetGroup? Group)[]
        {
            ("Single", player.TargetGroupSingle),
            ("All", player.TargetGroupAll),
            ("Dash", player.TargetGroupDash),
            ("Group1", player.TargetGroup1),
        };
        foreach (var (name, g) in groups)
        {
            if (g is null)
            {
                Log.LogInfo($"[GRP] {name.PadRight(8)} = null");
                continue;
            }
            string info;
            try
            {
                // m_Targets 是 Il2CppReferenceArray<Target>（不是 List），用 Length + 索引
                var targets = g.m_Targets;
                int n = targets is null ? -1 : targets.Length;
                var members = new List<string>();
                if (targets is not null)
                {
                    // 最多列出前 4 个成员，避免刷屏
                    int limit = Math.Min(n, 4);
                    for (int i = 0; i < limit; i++)
                    {
                        var t = targets[i];
                        Transform? tr = t.target;
                        members.Add(tr is null
                            ? $"#{i}:null(w={t.weight.ToString(NUM_FMT)})"
                            : $"#{i}:{tr.name}(w={t.weight.ToString(NUM_FMT)},y={tr.position.y.ToString(NUM_FMT)})");
                    }
                }
                // m_AveragePos = Cinemachine 为这个组算出的**三维平均位置**，
                // 也就是相机看向的落点。这是判断"组里有没有含高度"的直接证据。
                string avg;
                try
                {
                    Vector3 a = g.m_AveragePos;
                    avg = $"avgPos={a.x.ToString(NUM_FMT)},{a.y.ToString(NUM_FMT)},{a.z.ToString(NUM_FMT)}"
                          + $" weightSum={g.m_WeightSum.ToString(NUM_FMT)}"
                          + $" posMode={g.m_PositionMode}";
                }
                catch (Exception ex)
                {
                    avg = $"avgPos=err({ex.Message})";
                }
                info = $"members={n} [{string.Join(" ", members)}] {avg}";
            }
            catch (Exception ex)
            {
                info = $"err({ex.Message})";
            }
            Log.LogInfo($"[GRP] {name.PadRight(8)} {info}");
        }

        // Main 的 lookAt/follow（判断它是否在运行时被换过）
        try
        {
            var main = player.VCameraMain;
            if (main is not null)
            {
                string la = SafeName(() => main.LookAt);
                string fo = SafeName(() => main.Follow);
                Log.LogInfo($"[GRP] VCameraMain lookAt={la} follow={fo}");
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"VCameraMain probe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 测量"玩家与锁定目标之间的高度差"（绝对值，Unity 单位）。
    ///
    /// 取值优先级：
    ///   ① LockonTargetCharacter（游戏的锁定目标字段），玩家自身为精确参照；
    ///   ② 若该字段为空（实测在部分场景下确实为 null），则回退到
    ///      TargetGroupSingle 内两个成员的 Y 之差 —— 与相机实际使用的数据同源，
    ///      所以即使锁定字段为空也能正确工作。
    /// 无法测量时返回 NaN，调用方据此跳过修正。
    /// </summary>
    private static float MeasureHeightDifference(PlayerControlCustom player)
    {
        try
        {
            float myY = player.transform.position.y;

            // ① 优先用锁定目标字段
            CharacterControllerBase? tgt = player.LockonTargetCharacter;
            if (tgt is not null)
            {
                float ty = tgt.transform.position.y;
                return Math.Abs(myY - ty);
            }

            // ② 回退：用目标组内"非玩家"成员的高度
            var g = player.TargetGroupSingle;
            if (g is null)
            {
                return float.NaN;
            }
            var targets = g.m_Targets;
            if (targets is null)
            {
                return float.NaN;
            }
            float? otherY = null;
            for (int i = 0; i < targets.Length; i++)
            {
                Transform? tr = targets[i].target;
                if (tr is null)
                {
                    continue;
                }
                if (tr.name != PLAYER_GROUP_MEMBER_NAME)
                {
                    otherY = tr.position.y;
                    break;
                }
            }
            if (otherY is null)
            {
                return float.NaN;
            }
            return Math.Abs(myY - otherY.Value);
        }
        catch (Exception ex)
        {
            Log.LogError($"MeasureHeightDifference failed: {ex.Message}");
            return float.NaN;
        }
    }

    /// <summary>
    /// 高度差自适应的目标组权重修正（相机 Z 轴适配的核心方案）。
    ///
    /// ── 根因（实测数据）──────────────────────────────────────────────────
    ///   TargetGroupSingle / TargetGroupDash / TargetGroup1 三个组各含 2 个成员，
    ///   权重均 1.0：
    ///       #0 = AIdealBattle    （玩家）
    ///       #1 = Estscena(Clone) （敌人 / 目标）
    ///   posMode = GroupCenter，因此 avgPos = 两者的**几何中心**：
    ///       玩家 y=-7.115, 敌人 y=-7.075  →  avgPos y=-7.095  （验证无误）
    ///   而战斗接管相机的 LockonCrossSingle / LockonMiddleSingle 的 lookAt/follow
    ///   就是 TargetGroupSingle。所以**相机看向的是"玩家与敌人的中点"**。
    ///
    ///   同高度时中点 ≈ 敌人位置 → 看不出问题；
    ///   玩家飞高后中点被抬高到敌人上方 → 相机看向敌人头顶的空气。
    ///   这精确对应用户描述："做出蓄力姿态，但视角看的敌人头顶上的空气"。
    ///
    /// ── 修法 ─────────────────────────────────────────────────────────────
    ///   avgPos 是**加权**平均，所以把"玩家"这一项的权重随高度差降到 0，
    ///   组中心就退化为"只有敌人"：
    ///       高度差小 → 权重不变 → 完全原版行为（同高度时相机本来就正常）
    ///       高度差大 → 玩家权重 → 0 → avgPos 收敛到敌人位置
    ///   这不是外部硬掰相机旋转，而是让游戏自己的目标组机制去对准敌人。
    ///
    ///   权重对玩家的影响仅限于"相机看向哪"，不改变玩家在组里的存在性，
    ///   因此不影响其它依赖该组的逻辑（如多目标构图）。
    /// </summary>
    private static void ApplyGroupWeightFix(PlayerControlCustom player, float heightDiff)
    {
        // 高度差在 [0, FULL_AT] 之间线性过渡：差值越大，玩家权重越低。
        // 用 GameCenter 权重而非 0/1 硬切，避免相机在阈值处跳变。
        float t = heightDiff / GROUP_WEIGHT_FULL_AT_ABS_DIFF;
        if (t > 1f)
        {
            t = 1f;
        }
        float playerWeight = 1f - t;   // 1.0（无高度差）→ 0.0（高度差达到阈值）

        var groups = new Cinemachine.CinemachineTargetGroup?[]
        {
            player.TargetGroupSingle,
            player.TargetGroupDash,
            player.TargetGroup1,
        };
        foreach (var g in groups)
        {
            if (g is null)
            {
                continue;
            }
            try
            {
                var targets = g.m_Targets;
                if (targets is null)
                {
                    continue;
                }
                for (int i = 0; i < targets.Length; i++)
                {
                    var t0 = targets[i];
                    Transform? tr = t0.target;
                    if (tr is null)
                    {
                        continue;
                    }
                    // 通过对象名区分玩家与敌人（实测玩家成员名为 AIdealBattle）。
                    // 名字来源：运行时观测，见 FLIGHT_CAMERA.md 的实测数据。
                    if (tr.name == PLAYER_GROUP_MEMBER_NAME)
                    {
                        t0.weight = playerWeight;
                        targets[i] = t0;   // Target 是值类型，必须写回数组
                        _lastPlayerWeight = playerWeight;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"group weight fix failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 相机诊断：输出"相机看向多高"与"敌人身体在哪"的偏差。
    ///
    /// 为什么放在这里而不是 patch LookTargetController：
    ///   之前单独 patch LookTargetController.Update / LookTarget 时游戏卡在关卡加载，
    ///   且日志零输出 —— 该 patch 是**未经验证的热路径**，有重入/性能风险。
    ///   而 PlayerControlCustom.Update 是本插件一直在用、已证明安全的挂载点。
    ///   数据一样能拿到：玩家对象上就有 ApproachAllow（LookTargetController 引用）。
    ///
    /// dY = aimY - tgtY 就是问题的量化指标：
    ///   dY ≈ 0  → 瞄准点正确
    ///   dY > 0  → 相机看向敌人**上方**（即用户报的"看头顶空气"）
    /// </summary>
    private static void ReportCamera(PlayerControlCustom player)
    {
        try
        {
            Camera? cam = Camera.main;
            if (cam is null)
            {
                Log.LogWarning("Camera.main is null - skipping camera report.");
                return;
            }
            Transform camT = cam.transform;
            Vector3 camPos = camT.position;
            Vector3 camFwd = camT.forward;

            // 锁定目标：用游戏自己的字段，不猜
            CharacterControllerBase? tgt = player.LockonTargetCharacter;
            string tgtInfo = "none";
            float tgtY = float.NaN;
            if (tgt is not null)
            {
                Vector3 tp = tgt.transform.position;
                tgtY = tp.y;
                tgtInfo = $"{tgt.transform.name}@{tp.x.ToString(NUM_FMT)},{tp.y.ToString(NUM_FMT)},{tp.z.ToString(NUM_FMT)}";
            }

            // 相机前向视线落到"目标 XZ 平面"时的高度
            float aimY = float.NaN;
            if (tgt is not null && Math.Abs(camFwd.y) > DIR_EPSILON)
            {
                Vector3 tp = tgt.transform.position;
                Vector3 toTgt = tp - camPos;
                float horiz = new Vector2(toTgt.x, toTgt.z).magnitude;
                float horizDir = new Vector2(camFwd.x, camFwd.z).magnitude;
                if (horizDir > DIR_EPSILON)
                {
                    float travel = horiz / horizDir;
                    if (travel <= AIM_RAY_LENGTH)
                    {
                        aimY = camPos.y + camFwd.y * travel;
                    }
                }
            }
            float dY = (float.IsNaN(aimY) || float.IsNaN(tgtY)) ? float.NaN : (aimY - tgtY);

            // ApproachAllow：玩家对象上的 LookTargetController 引用
            string lookInfo = "ApproachAllow=n/a";
            try
            {
                var lt = player.ApproachAllow;
                lookInfo = lt is null
                    ? "ApproachAllow=null"
                    : $"lookStep={lt.LookStep.ToString(NUM_FMT)} grav={lt.Gravity}"
                      + $" target={(lt.Target is null ? "null" : lt.Target.name)}";
            }
            catch (Exception ex)
            {
                lookInfo = $"ApproachAllow=err({ex.Message})";
            }

            Log.LogInfo($"[CAM] camY={camPos.y.ToString(NUM_FMT)} fwdY={camFwd.y.ToString(NUM_FMT)}"
                        + $" | aimY={aimY.ToString(NUM_FMT)} tgtY={tgtY.ToString(NUM_FMT)}"
                        + $" | dY={dY.ToString(NUM_FMT)}"
                        + $" | {lookInfo}"
                        + $" | fov={cam.fieldOfView.ToString(NUM_FMT)}"
                        + $" | target={tgtInfo}");
        }
        catch (Exception ex)
        {
            Log.LogError($"camera report failed: {ex.Message}");
        }
    }
}
