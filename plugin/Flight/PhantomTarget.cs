// 驱动妖精 AI-deal-Rays · 幻影锁定目标（Phantom Lock Target）
//
// ── 用户的设计意图 ──────────────────────────────────────────────────────
//   原版：锁定对象由"附近的敌人"决定 → 移动轴、朝向、攻击目标全部绑定在敌人身上。
//   目标：让锁定对象变成"相机看向的位置"，从而：
//     · 朝任意方向释放绝招（含三维方向）
//     · 准心 / 锁定相机 / TargetGroupSingle 全部联动
//     · 不重写战斗管线（避免上次那种炸掉整个战斗系统的风险）
//   用户明确接受代价："锁定幻影时攻击会打空"。
//
// ── 为什么用 CharacterControllerBase 基类 ───────────────────────────────
//   反编译确认（CharacterControllerBase.cs）：
//     · public class CharacterControllerBase : MonoBehaviour      L14
//     · **没有** override 任何 Unity 生命周期方法（无 Awake/Start/OnEnable）
//     · **没有** Update / LateUpdate / FixedUpdate
//   子类 SylpheedController 有 Awake（会跑重初始化）—— 所以我们用基类，不用子类。
//   这样创建出来的对象不会自动启动角色逻辑，是最轻的可行实例。
//
// ── 与上次事故的区别（务必看清）─────────────────────────────────────────
//   上次崩溃是因为把 LockonTargetCharacter 写成 **null**（宣告"场上没有敌人"，
//   于是敌人 AI / 锁定 / 战斗 UI 全部停摆）。
//   本模块**阶段 1 完全不碰锁定系统**，只创建一个"隐形、无 AI、无血量"的对象。
//   接入锁定放在阶段 2，且只在确认阶段 1 安全后才做。
//
// ── 阶段划分 ────────────────────────────────────────────────────────────
//   阶段 1（本文件当前状态）：只在按住自由视角键时创建幻影并跟随视线；
//                             **不设置任何锁定字段**。用于验证"创建它是否安全"。
//   阶段 2（尚未启用）：接入锁定（见 TryAttachLock 的注释说明）。

using System;
using BepInEx.Logging;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 幻影锁定目标的创建、跟随与销毁。
/// 阶段 1 只负责"存在并跟随视线"，不参与任何战斗/锁定状态。
/// </summary>
internal sealed class PhantomTarget
{
    // 幻影对象名（便于在场景/日志里识别）
    private const string ObjectName = "AidealRays_PhantomTarget";
    // 幻影放在相机视线前方多远（Unity 单位）。
    // 取值理由：太近会让"朝向目标"的角度变化过于剧烈（抖动），
    // 太远则接近"正前方固定点"失去瞄准意义。10 米处的小幅视角变化
    // 对应约 1~2 米的位置变化，与角色尺度匹配。
    private const float ForwardDistance = 10f;
    // 日志节流：每 N 帧报告一次位置，避免刷屏
    private const int LogEveryFrames = 60;

    private readonly ManualLogSource _log;
    private GameObject? _go;
    private int _frameCounter;
    private bool _loggedCreate;

    public PhantomTarget(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>幻影当前是否已创建。</summary>
    public bool Exists => _go is not null;

    /// <summary>只读用途：暴露给后续阶段（接入锁定时需要传实例）。</summary>
    public CharacterControllerBase? Instance { get; private set; }

    /// <summary>
    /// 创建幻影（幂等）。阶段 1 只创建对象本身，**不碰任何锁定字段**。
    /// </summary>
    public void EnsureCreated()
    {
        if (_go is not null)
        {
            return;
        }
        try
        {
            _go = new GameObject(ObjectName);
            // 不参与渲染/碰撞：幻影必须完全无形，
            // 否则会在画面里出现一个"看不见的敌人"导致的奇怪碰撞。
            try
            {
                _go.SetActive(true);
            }
            catch (Exception ex)
            {
                _log.LogError($"phantom SetActive failed: {ex.Message}");
            }

            // 用真实 IL2CPP 类型（不是注入的托管类型）——
            // 这是与上次失败做法（ClassInjector 注入）的关键区别。
            Instance = _go.AddComponent<CharacterControllerBase>();
            if (Instance is null)
            {
                _log.LogError("AddComponent<CharacterControllerBase> returned null - phantom unusable");
                UnityEngine.Object.Destroy(_go);
                _go = null;
                return;
            }

            // 挂到场景根，不设父物体（避免跟随玩家的位移）
            UnityEngine.Object.DontDestroyOnLoad(_go);

            _log.LogInfo($"[PHANTOM] created '{ObjectName}' with CharacterControllerBase"
                         + " - stage 1: no lock fields touched");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"phantom create failed: {ex}");
            _go = null;
            Instance = null;
        }
    }

    /// <summary>销毁幻影并清理引用（松开自由视角键时调用）。</summary>
    public void Destroy()
    {
        if (_go is null)
        {
            return;
        }
        try
        {
            UnityEngine.Object.Destroy(_go);
            _log.LogInfo("[PHANTOM] destroyed");
        }
        catch (Exception ex)
        {
            _log.LogError($"phantom destroy failed: {ex.Message}");
        }
        finally
        {
            _go = null;
            Instance = null;
            _loggedCreate = false;
            _frameCounter = 0;
        }
    }

    /// <summary>
    /// 每帧把幻影放到"相机视线前方"。
    /// 只写自己的 transform，不碰任何游戏对象。
    /// </summary>
    public void FollowCameraAim(Camera cam)
    {
        if (_go is null || cam is null)
        {
            return;
        }
        try
        {
            Transform t = cam.transform;
            Vector3 pos = t.position + t.forward * ForwardDistance;
            _go.transform.position = pos;

            if (!_loggedCreate)
            {
                _loggedCreate = true;
                _log.LogInfo($"[PHANTOM] first follow: camPos={t.position.x:F1},{t.position.y:F1},{t.position.z:F1}"
                             + $" -> phantom={pos.x:F1},{pos.y:F1},{pos.z:F1} (forward {ForwardDistance})");
            }
            else if (++_frameCounter % LogEveryFrames == 0)
            {
                _log.LogInfo($"[PHANTOM] pos={pos.x:F1},{pos.y:F1},{pos.z:F1}"
                             + $" exists={Exists} hasInstance={Instance is not null}");
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"phantom follow failed: {ex.Message}");
        }
    }

    // ── 阶段 2（尚未启用）─────────────────────────────────────────────────
    // 接入锁定的做法与风险（写在这里，避免以后忘记上下文）：
    //
    //   候选接口（反编译确认）：
    //     PlayerControlCustom.LockonChange(CharacterControllerBase)   L5208
    //       注：原生侧是 protected、CallerCount(0)（游戏自己的托管代码没调用过），
    //           所以调用可能失败 —— 必须 try/catch 并回退。
    //     PlayerControlCustom.TargetListRecalc(RECALC_METHOD)          L5390
    //     PlayerControlCustom.LockonRecalc()                           L5402
    //     PlayerControlCustom.GetLockonEnemys() : List<CharacterControllerBase>  L6680
    //       —— 返回**可变 List**，理论上可以注入幻影；但游戏是否重新读取未知。
    //
    //   绝对禁止（上次事故的根因）：
    //     ✗ 把 LockonTargetCharacter 写成 null —— 等于宣告"场上没有敌人"，
    //       会导致敌人 AI 消失、锁定失效、战斗 UI 消失（已实测）。
    //
    //   阶段 2 的正确做法：把幻影作为**有效目标**传入（非 null），
    //   并且必须先确认阶段 1 对游戏无副作用。
}
