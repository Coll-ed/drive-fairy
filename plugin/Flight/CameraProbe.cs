// 驱动妖精 AI-deal-Rays · 相机组件探针（只读诊断）
//
// ── 要解决的问题（用户实测描述）─────────────────────────────────────────
//   1. "当我在高于敌人的情况下，进行冲刺锁定蓄力的时候，相机因为没有 Z 轴的调控，
//       玩家视角则是看的敌人头顶上的空气"
//   2. "在一定范围内，视角会缩放保持你可以看到人，但是如果离远了就会出现
//       这个不再缩放视角"
//   3. "相机飞高了也不会去转动 Z 轴视角锁定敌人，而是用缩放的方式去看人"
//
// ── 反编译已确认的机制（Cinemachine 转储）───────────────────────────────
//   锁定相机的瞄准组件很可能是 CinemachineGroupComposer（继承 CinemachineComposer），
//   它把 CinemachineTargetGroup 里的成员（玩家 + 敌人）框进画面：
//
//     m_GroupFramingSize    组框住的大小
//     m_FrameDamping        阻尼
//     m_AdjustmentMode      调整策略（调 FOV 还是调距离）★ 决定"缩放"还是"转动"
//     m_MaxDollyIn/Out      推进/拉远上限          ★ 到上限就不再缩放（对应现象 2）
//     m_Minimum/MaximumDistance, m_Minimum/MaximumFOV
//     PositionMode { GroupCenter, GroupAverage }  ★ GroupCenter = 包围盒中心
//
//   而 CinemachineTargetGroup 提供：
//     GetViewSpaceAngularBounds(observer, out minAngles, out maxAngles, out zRange)
//     ★ 该方法直接返回该组在观察者视角下的角度范围与 Z 轴深度范围
//
// ── 本探针的作用 ────────────────────────────────────────────────────────
//   把这些组件的**运行时真实数值**打印出来 —— 在读到数据之前不改任何东西。
//   这与 SelectionProbe 同一思路：先取证，再决定改哪一层。
//
// ── 安全性 ──────────────────────────────────────────────────────────────
//   **纯只读**：只读组件与字段、只写日志。不设置任何游戏状态。

using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Cinemachine;
using UnityEngine;

namespace AidealRays.Flight;

/// <summary>
/// 相机组件探针：读取所有 vcam 的瞄准/跟随组件与参数。
/// </summary>
internal static class CameraProbe
{
    // 每个 vcam 最多报告多少个子组件
    private const int MaxComponentsPerVcam = 8;
    // 只在按住自由视角键时读仰角，避免干扰
    private static ManualLogSource? _log;

    public static void Init(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>打印全部相机与目标组的状态。只读。</summary>
    public static void Dump(PlayerControlCustom player)
    {
        if (_log is null || player is null)
        {
            return;
        }
        try
        {
            _log.LogInfo("[CAMPROBE] ===== camera dump begin =====");

            // ① 所有 vcam：优先级、看谁、跟随谁、以及管线上的瞄准组件
            Camera[] allCams = Camera.allCameras;
            _log.LogInfo($"[CAMPROBE] active cameras = {allCams.Length}");
            for (int i = 0; i < allCams.Length; i++)
            {
                Camera c = allCams[i];
                if (c is null)
                {
                    continue;
                }
                _log.LogInfo($"[CAMPROBE]   cam[{i}] '{c.name}' fov={c.fieldOfView:F1}"
                             + $" depth={c.depth:F1} enabled={c.enabled}");
            }

            // ② 玩家身上那批 vcam（字段名来自反编译，已确认存在）
            DumpVcam("Main", player.VCameraMain);
            DumpVcam("LockonCrossSingle", player.VCameraLockonCrossSingle);
            DumpVcam("LockonMiddleSingle", player.VCameraLockonMiddleSingle);
            DumpVcam("LockonCrossMulti", player.VCameraLockonCrossMulti);
            DumpVcam("LockonCrossCross", player.VCameraLockonCrossCross);
            DumpVcam("LockonLong", player.VCameraLockonLong);
            DumpVcam("DashReady", player.VCameraDashReady);
            DumpVcam("Dash", player.VCameraDash);
            DumpVcam("FingerLine", player.VCameraFingerLine);

            // ③ 目标组：成员权重与位置模式
            DumpGroup("TargetGroupSingle", player.TargetGroupSingle);
            DumpGroup("TargetGroup1", player.TargetGroup1);
            DumpGroup("TargetGroupDash", player.TargetGroupDash);
            DumpGroup("TargetGroupAll", player.TargetGroupAll);

            _log.LogInfo("[CAMPROBE] ===== dump end =====");
        }
        catch (Exception ex)
        {
            // 边界显式记录，不静默吞掉
            _log.LogError($"[CAMPROBE] dump failed: {ex}");
        }
    }

    /// <summary>打印单个 vcam 的关键状态与管线组件参数。</summary>
    private static void DumpVcam(string label, CinemachineVirtualCamera? vcam)
    {
        if (_log is null)
        {
            return;
        }
        if (vcam is null)
        {
            _log.LogInfo($"[CAMPROBE] vcam {label} = null");
            return;
        }
        try
        {
            Transform? lookAt = vcam.LookAt;
            Transform? follow = vcam.Follow;
            LensSettings lens = vcam.m_Lens;
            _log.LogInfo($"[CAMPROBE] vcam {label}: prio={vcam.Priority} enabled={vcam.enabled}"
                         + $" fov={lens.FieldOfView:F1} ortho={lens.Orthographic}"
                         + $" lookAt={(lookAt is not null ? lookAt.name : "null")}"
                         + $" follow={(follow is not null ? follow.name : "null")}");

            // 管线组件是动态创建的，逐个类型探测（AddCinemachineComponent 会在
            // 需要时创建；这里只做 GetCinemachineComponent 读取，不创建）
            ReportComposer(label, vcam);
            ReportTransposer(label, vcam);
            ReportGroupComposer(label, vcam);
            // ★ FramingTransposer 是"身体+瞄准"二合一组件 —— 四个锁定相机用的就是它。
            // 早期版本的探针只读 Composer，于是锁定相机"什么都没报出来"，
            // 导致误判为"组件缺失"。补上这个缺口才能看到真正的参数。
            ReportFramingTransposer(label, vcam);

            // 组件存在性总览 —— 显式报告缺失，避免"静默跳过"导致看不到全貌。
            // （早期版本的探针只打印找到的组件，于是"没报出来"到底是
            //   "组件不存在"还是"读失败"无法区分 —— 这会误导判断。）
            bool hasComposer = vcam.GetCinemachineComponent<CinemachineComposer>() is not null;
            bool hasGroupComposer = vcam.GetCinemachineComponent<CinemachineGroupComposer>() is not null;
            bool hasTransposer = vcam.GetCinemachineComponent<CinemachineTransposer>() is not null;
            bool hasFraming = vcam.GetCinemachineComponent<CinemachineFramingTransposer>() is not null;
            bool hasOrbital = vcam.GetCinemachineComponent<CinemachineOrbitalTransposer>() is not null;
            _log.LogInfo($"[CAMPROBE]   {label}.components:"
                         + $" Composer={(hasComposer ? "Y" : "N")}"
                         + $" GroupComposer={(hasGroupComposer ? "Y" : "N")}"
                         + $" Transposer={(hasTransposer ? "Y" : "N")}"
                         + $" FramingTransposer={(hasFraming ? "Y" : "N")}"
                         + $" OrbitalTransposer={(hasOrbital ? "Y" : "N")}");
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE] vcam {label} read failed: {ex.Message}");
        }
    }

    /// <summary>报告普通 Composer（屏幕对齐式瞄准）。</summary>
    private static void ReportComposer(string label, CinemachineVirtualCamera vcam)
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            CinemachineComposer? comp = vcam.GetCinemachineComponent<CinemachineComposer>();
            if (comp is null)
            {
                return;
            }
            // 字段名来自转储的 Cinemachine.dll（Cinemachine.CinemachineComposer）：
            //   m_ScreenX/Y, m_DeadZoneWidth/Height, m_SoftZoneWidth/Height,
            //   m_HorizontalDamping / m_VerticalDamping, m_TrackedObjectOffset, m_BiasX/Y
            // 注意：该版本 Composer **没有** m_Dutch 字段。
            Vector3 tracked = comp.m_TrackedObjectOffset;
            _log.LogInfo($"[CAMPROBE]   {label}.Composer:"
                         + $" screenX={comp.m_ScreenX:F3} screenY={comp.m_ScreenY:F3}"
                         + $" deadZone=({comp.m_DeadZoneWidth:F3},{comp.m_DeadZoneHeight:F3})"
                         + $" softZone=({comp.m_SoftZoneWidth:F3},{comp.m_SoftZoneHeight:F3})"
                         + $" bias=({comp.m_BiasX:F3},{comp.m_BiasY:F3})");
            _log.LogInfo($"[CAMPROBE]   {label}.Composer follow:"
                         + $" trackedOffset=({tracked.x:F2},{tracked.y:F2},{tracked.z:F2})"
                         + $" dampingH={comp.m_HorizontalDamping:F2} dampingV={comp.m_VerticalDamping:F2}"
                         + $" lookaheadTime={comp.m_LookaheadTime:F2}"
                         + $" lookaheadIgnoreY={comp.m_LookaheadIgnoreY}");
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE]   {label}.Composer read failed: {ex.Message}");
        }
    }

    /// <summary>报告 Transposer（位置跟随与偏移）。</summary>
    private static void ReportTransposer(string label, CinemachineVirtualCamera vcam)
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            CinemachineTransposer? tr = vcam.GetCinemachineComponent<CinemachineTransposer>();
            if (tr is null)
            {
                return;
            }
            Vector3 off = tr.m_FollowOffset;
            _log.LogInfo($"[CAMPROBE]   {label}.Transposer:"
                         + $" binding={tr.m_BindingMode}"
                         + $" offset=({off.x:F2},{off.y:F2},{off.z:F2})"
                         + $" damp=({tr.m_XDamping:F2},{tr.m_YDamping:F2},{tr.m_ZDamping:F2})");
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE]   {label}.Transposer read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 报告 GroupComposer —— ★ 这是"缩放看人"行为的来源。
    /// 重点看上限参数：到达上限后就不再缩放（对应现象 2）。
    /// </summary>
    private static void ReportGroupComposer(string label, CinemachineVirtualCamera vcam)
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            CinemachineGroupComposer? gc = vcam.GetCinemachineComponent<CinemachineGroupComposer>();
            if (gc is null)
            {
                return;
            }
            _log.LogInfo($"[CAMPROBE]   ★{label}.GroupComposer:"
                         + $" framingSize={gc.m_GroupFramingSize:F3}"
                         + $" framing={gc.m_FramingMode}"
                         + $" adjustment={gc.m_AdjustmentMode}"
                         + $" frameDamping={gc.m_FrameDamping:F2}");
            _log.LogInfo($"[CAMPROBE]   ★{label}.GroupComposer limits:"
                         + $" dollyIn={gc.m_MaxDollyIn:F2} dollyOut={gc.m_MaxDollyOut:F2}"
                         + $" dist=[{gc.m_MinimumDistance:F2},{gc.m_MaximumDistance:F2}]"
                         + $" fov=[{gc.m_MinimumFOV:F1},{gc.m_MaximumFOV:F1}]");
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE]   {label}.GroupComposer read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// ★ 报告 FramingTransposer —— 四个锁定相机（LockonCrossSingle / LockonMiddleSingle /
    /// LockonCrossMulti / LockonCrossCross）用的就是这个组件。
    ///
    /// 它是 Cinemachine 的"身体+瞄准"二合一组件：同时决定相机位置与朝向。
    /// 与"相机能否自动适应高度差"直接相关的参数：
    ///   m_DeadZoneHeight / m_SoftZoneHeight —— 垂直方向的目标容差区
    ///       目标在死区内时相机【完全不跟随】；超出软区才平滑跟随。
    ///       若软区高度很大，目标上下移动时相机就不会翻转。
    ///   m_YDamping / m_ZDamping —— 垂直/深度阻尼，越大跟随越迟钝。
    ///   m_LookaheadIgnoreY —— 是否忽略垂直方向的预测。
    ///   m_TrackedObjectOffset —— 瞄准点偏移。
    /// </summary>
    private static void ReportFramingTransposer(string label, CinemachineVirtualCamera vcam)
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            CinemachineFramingTransposer? ft = vcam.GetCinemachineComponent<CinemachineFramingTransposer>();
            if (ft is null)
            {
                return;
            }
            Vector3 tracked = ft.m_TrackedObjectOffset;
            _log.LogInfo($"[CAMPROBE]   ★{label}.FramingTransposer:"
                         + $" screen=({ft.m_ScreenX:F3},{ft.m_ScreenY:F3})"
                         + $" camDist={ft.m_CameraDistance:F2}"
                         + $" deadZone=({ft.m_DeadZoneWidth:F3},{ft.m_DeadZoneHeight:F3},{ft.m_DeadZoneDepth:F3})"
                         + $" softZone=({ft.m_SoftZoneWidth:F3},{ft.m_SoftZoneHeight:F3})"
                         + $" unlimitedSoftZone={ft.m_UnlimitedSoftZone}");
            _log.LogInfo($"[CAMPROBE]   ★{label}.FramingTransposer follow:"
                         + $" trackedOffset=({tracked.x:F2},{tracked.y:F2},{tracked.z:F2})"
                         + $" damping=({ft.m_XDamping:F2},{ft.m_YDamping:F2},{ft.m_ZDamping:F2})"
                         + $" lookahead=({ft.m_LookaheadTime:F2},{ft.m_LookaheadSmoothing:F2})"
                         + $" lookaheadIgnoreY={ft.m_LookaheadIgnoreY}"
                         + $" targetMovementOnly={ft.m_TargetMovementOnly}"
                         + $" bias=({ft.m_BiasX:F2},{ft.m_BiasY:F2})");
            _log.LogInfo($"[CAMPROBE]   ★{label}.FramingTransposer group:"
                         + $" mode={ft.m_GroupFramingMode} adjustment={ft.m_AdjustmentMode}"
                         + $" size={ft.m_GroupFramingSize:F2}"
                         + $" dollyIn={ft.m_MaxDollyIn:F1} dollyOut={ft.m_MaxDollyOut:F1}"
                         + $" dist=[{ft.m_MinimumDistance:F1},{ft.m_MaximumDistance:F1}]"
                         + $" fov=[{ft.m_MinimumFOV:F1},{ft.m_MaximumFOV:F1}]");
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE]   {label}.FramingTransposer read failed: {ex.Message}");
        }
    }

    /// <summary>报告目标组的成员与位置模式。</summary>
    private static void DumpGroup(string label, CinemachineTargetGroup? group)
    {
        if (_log is null)
        {
            return;
        }
        if (group is null)
        {
            _log.LogInfo($"[CAMPROBE] group {label} = null");
            return;
        }
        try
        {
            _log.LogInfo($"[CAMPROBE] group {label}: posMode={group.m_PositionMode}"
                         + $" rotMode={group.m_RotationMode} update={group.m_UpdateMethod}"
                         + $" isEmpty={group.IsEmpty}");

            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<CinemachineTargetGroup.Target>? targets = group.m_Targets;
            if (targets is null)
            {
                _log.LogInfo($"[CAMPROBE]   {label}.m_Targets = null");
                return;
            }
            int n = targets.Length;
            _log.LogInfo($"[CAMPROBE]   {label}.m_Targets: {n} member(s)");
            int limit = Math.Min(n, MaxComponentsPerVcam);
            for (int i = 0; i < limit; i++)
            {
                CinemachineTargetGroup.Target t = targets[i];
                _log.LogInfo($"[CAMPROBE]     [{i}] target={(t.target is not null ? t.target.name : "null")}"
                             + $" weight={t.weight:F2} radius={t.radius:F2}");
            }
            if (n > limit)
            {
                _log.LogInfo($"[CAMPROBE]     ... ({n - limit} more omitted)");
            }

            // 包围盒：Z 轴问题的直接证据
            Bounds bb = group.BoundingBox;
            _log.LogInfo($"[CAMPROBE]   {label}.BoundingBox center=({bb.center.x:F1},{bb.center.y:F1},{bb.center.z:F1})"
                         + $" size=({bb.size.x:F1},{bb.size.y:F1},{bb.size.z:F1})");
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE] group {label} read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 报告"锁定渲染"相关对象的实际位置：锁定标记、准心线、以及参考点。
    ///
    /// 为什么要这个（实测问题）：把锁定目标改成锚点后，
    /// **锁定确实锁在锚点上**，但**渲染框仍画在敌人那里** ——
    /// 说明渲染那条链读的不是 LockonTargetCharacter，而是别的东西。
    /// 本方法把可疑对象的**世界坐标**与"到锚点/到敌人"的距离一起打出来，
    /// 谁的距离接近 0，谁就是渲染跟随的对象 —— 用数据定位，不靠猜。
    /// </summary>
    public static void DumpLockVisuals(PlayerControlCustom player, Transform? anchor)
    {
        if (_log is null || player is null)
        {
            return;
        }
        try
        {
            _log.LogInfo("[CAMPROBE] ----- lock visual objects -----");
            // 三个互相独立的参考点（见 ReportDistances 的说明）：
            Transform self = player.transform;                       // 爱迪尔本体
            Transform? enemy = CameraAimTuner.ResolveLockTarget(player);
            // ⚠️ 注意：F3 模式下 enemy 已经是锚点（我们把锁定目标改成了锚点），
            // 所以额外找敌人身上的 Spine 骨骼作为独立参考。
            Transform? enemySpine = FindEnemySpine(enemy);

            // 玩家身上的准心线（LockonCursol）—— 它自己有 Start/Finish 两组 Transform
            ReportCursol("player.LockonLine", player.LockonLine, self, anchor, enemySpine);

            BattleManager? mgr = null;
            try
            {
                mgr = BattleManager.GetInstance();
            }
            catch (Exception ex)
            {
                _log.LogError($"[CAMPROBE]   BattleManager.GetInstance failed: {ex.Message}");
            }
            if (mgr is null)
            {
                _log.LogInfo("[CAMPROBE]   BattleManager = null");
            }
            else
            {
                ReportLockMarker(mgr, self, anchor, enemySpine);
                ReportLockDisp(mgr, self, anchor, enemySpine);
            }

            _log.LogInfo("[CAMPROBE] ----- end lock visual objects -----");
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE] DumpLockVisuals failed: {ex.Message}");
        }
    }

    /// <summary>报告锁定标记（渲染框本体）的位置。</summary>
    private static void ReportLockMarker(BattleManager mgr, Transform? self,
                                         Transform? anchor, Transform? enemySpine)
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            LockonMarker? marker = mgr.LockonMarker;
            if (marker is null)
            {
                _log.LogInfo("[CAMPROBE]   BattleManager.LockonMarker = null");
                return;
            }
            Transform mt = marker.transform;
            _log.LogInfo($"[CAMPROBE]   LockonMarker '{mt.name}'"
                         + $" active={marker.gameObject.activeInHierarchy}"
                         + $" pos=({mt.position.x:F1},{mt.position.y:F1},{mt.position.z:F1})"
                         + $" parent={(mt.parent is not null ? mt.parent.name : "null")}");
            ReportDistances("    LockonMarker", mt.position, self, anchor, enemySpine);
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE]   read LockonMarker failed: {ex.Message}");
        }
    }

    /// <summary>报告锁定显示对象与其 HUD 子对象的位置。</summary>
    private static void ReportLockDisp(BattleManager mgr, Transform? self,
                                       Transform? anchor, Transform? enemySpine)
    {
        if (_log is null)
        {
            return;
        }
        try
        {
            Animator? disp = mgr.LockonDisp;
            if (disp is null)
            {
                _log.LogInfo("[CAMPROBE]   BattleManager.LockonDisp = null");
                return;
            }
            LockonHUD? hud = disp.GetComponent<LockonHUD>();
            if (hud is null)
            {
                _log.LogInfo("[CAMPROBE]   LockonDisp has no LockonHUD component");
                return;
            }
            if (hud.HUDChildOrigin is null)
            {
                _log.LogInfo("[CAMPROBE]   LockonHUD.HUDChildOrigin = null");
                return;
            }
            Transform ht = hud.HUDChildOrigin.transform;
            _log.LogInfo($"[CAMPROBE]   HUDChildOrigin '{ht.name}'"
                         + $" active={hud.HUDChildOrigin.activeInHierarchy}"
                         + $" pos=({ht.position.x:F1},{ht.position.y:F1},{ht.position.z:F1})"
                         + $" parent={(ht.parent is not null ? ht.parent.name : "null")}");
            ReportDistances("    HUDChildOrigin", ht.position, self, anchor, enemySpine);
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE]   read LockonDisp/HUD failed: {ex.Message}");
        }
    }

    /// <summary>报告一个 LockonCursol 的起止点位置与距离关系。</summary>
    private static void ReportCursol(string label, LockonCursol? cursol,
                                     Transform? self, Transform? anchor, Transform? enemySpine)
    {
        if (_log is null)
        {
            return;
        }
        if (cursol is null)
        {
            _log.LogInfo($"[CAMPROBE]   {label} = null");
            return;
        }
        try
        {
            _log.LogInfo($"[CAMPROBE]   {label} active={cursol.gameObject.activeInHierarchy}"
                         + $" playerSet={(cursol.Player is not null)}");
            ReportTransformList($"{label}.StartTrf", cursol.StartTrf, self, anchor, enemySpine);
            ReportTransformList($"{label}.FinishTrf", cursol.FinishTrf, self, anchor, enemySpine);
        }
        catch (Exception ex)
        {
            _log.LogError($"[CAMPROBE]   {label} read failed: {ex.Message}");
        }
    }

    /// <summary>报告一组 Transform 的位置（准心线的端点通常是一组）。</summary>
    private static void ReportTransformList(string label,
        Il2CppSystem.Collections.Generic.List<Transform>? list,
        Transform? self, Transform? anchor, Transform? enemySpine)
    {
        if (_log is null)
        {
            return;
        }
        if (list is null)
        {
            _log.LogInfo($"[CAMPROBE]     {label} = null");
            return;
        }
        int n = list.Count;
        _log.LogInfo($"[CAMPROBE]     {label}: {n} item(s)");
        int limit = Math.Min(n, 6);
        for (int i = 0; i < limit; i++)
        {
            Transform? t = list[i];
            if (t is null)
            {
                continue;
            }
            _log.LogInfo($"[CAMPROBE]       [{i}] '{t.name}'"
                         + $" pos=({t.position.x:F1},{t.position.y:F1},{t.position.z:F1})");
            ReportDistances($"          [{i}]", t.position, self, anchor, enemySpine);
        }
    }

    /// <summary>
    /// 报告某点到各候选参考的距离 —— 谁接近 0，渲染对象就跟着谁。
    ///
    /// ⚠️ 修正（实测教训）：早期版本只比"锚点 / 锁定目标"两项，但
    /// **锁定目标已经等于锚点**（F3 模式下我们把锁定目标设成了锚点），
    /// 于是两列数值恒等，无法区分渲染对象到底跟谁 —— 等于白测。
    /// 现在改为对比三个**互相独立**的参考：
    ///   player  —— 爱迪尔本体
    ///   anchor  —— 我们的瞄准锚点
    ///   spined  —— 敌人的 Spine 骨骼（LockonMarker 的 parent 就叫 Spine，
    ///              很可能渲染框挂在敌人的骨骼子节点上 —— 这是重点怀疑对象）
    /// </summary>
    private static void ReportDistances(string label, Vector3 pos,
                                        Transform? player, Transform? anchor, Transform? enemySpine)
    {
        if (_log is null)
        {
            return;
        }
        string dPlayer = player is not null ? Vector3.Distance(pos, player.position).ToString("F2") : "n/a";
        string dAnchor = anchor is not null ? Vector3.Distance(pos, anchor.position).ToString("F2") : "n/a";
        string dSpine = enemySpine is not null ? Vector3.Distance(pos, enemySpine.position).ToString("F2") : "n/a";
        _log.LogInfo($"[CAMPROBE] {label} dist: player={dPlayer} anchor={dAnchor} enemySpine={dSpine}");
    }

    /// <summary>
    /// 在敌人身上找名为 Spine 的骨骼 —— `LockonMarker.parent` 实测就叫这个名字，
    /// 强烈暗示渲染框挂在敌人骨骼下（那样它当然跟着敌人而不是锚点）。
    /// 找不到返回 null（不抛异常，由调用方处理 n/a）。
    /// </summary>
    private static Transform? FindEnemySpine(Transform? enemyRoot)
    {
        if (enemyRoot is null)
        {
            return null;
        }
        try
        {
            // 先看自身
            if (enemyRoot.name == "Spine")
            {
                return enemyRoot;
            }
            // 再在子层级里找（深度有限，避免大场景遍历开销）
            return FindChildByName(enemyRoot, "Spine", 0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>递归查找指定名字的子节点（带深度上限）。</summary>
    private static Transform? FindChildByName(Transform root, string name, int depth)
    {
        // 深度上限 8：角色骨骼层级通常不超过这个深度，避免极端情况下遍历爆炸
        const int MaxDepth = 8;
        if (depth > MaxDepth)
        {
            return null;
        }
        int n = root.childCount;
        for (int i = 0; i < n; i++)
        {
            Transform c = root.GetChild(i);
            if (c is null)
            {
                continue;
            }
            if (c.name == name)
            {
                return c;
            }
            Transform? deeper = FindChildByName(c, name, depth + 1);
            if (deeper is not null)
            {
                return deeper;
            }
        }
        return null;
    }
}
