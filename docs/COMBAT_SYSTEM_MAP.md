# 驱动妖精 · 战斗系统接口清单（反编译整理）

> 用途：为"自建战斗框架"提供底图。**所有内容来自反编译确认**（ilspycmd 读取
> `BepInEx\interop\Assembly-CSharp.dll`），不是猜测。带 ⚠️ 的是我踩过的坑。

## 一、战斗系统的分层（已确认）

```
BattleManager                       战斗规则总控（含 _MonitorBurst 协程）
  ├─ LauncerController             大招 / 蓄力发射
  ├─ ShotController                远程子弹实体
  ├─ UIFollow3DObject              3D 跟随 UI（锁定标记、血条、目标框）
  └─ CharacterControllerBase       角色基类（位移 + 瞄准 + 锁定目标）
        ├─ SylpheedController      角色控制器（另一个实现）
        └─ PlayerControlCustom     玩家控制（含冲刺状态机、锁定系统）
```

## 二、玩家战斗接口（PlayerControlCustom）

### 输入汇聚点（唯一的每帧输入入口）

```csharp
public unsafe void EvaluateMoveAndAttack(
    float horizon, float vertical,          // 移动轴
    BUTTON_STATUS atk,                      // 攻击键
    BUTTON_STATUS assist,                   // 辅助键
    BUTTON_STATUS function)                 // 功能键（中键原绑定于此）
```

### 攻击 / 技能方法（L 为 PlayerControlCustom.cs 行号）

```
L5932  ChangeAttack(ATTACK_CROSS key)        近战切换（刀）
L5956  ChangeShot(ATTACK_LONG key)           远程切换（枪）
L5921  ChangeQuinterdShot()
L5968  ChangeSonicBurst(BURST_CROSS key)     大招·近战路
L5980  ChangeSonicBurstLong(BURST_LONG key)  大招·远程路
L6029  ChangeExBurst(BURST_EXTERMINATE key)  终结技
L6041  ChangeCounterBurst(BURST_COUNTER key) 反击技
L6111  Attack()                              攻击执行
L5163  BurstSelectLogic()                    大招选择逻辑
L5716~ BurstValid / BurstApproach / BurstApproachForce /
       BurstApproachForceSuper / BurstExterminate / Burst /
       BurstCounter / BurstFinish / ExBurstFinish
```

### 位移 / 冲刺

```
MoveControl()                     L5413   移动执行
DashCursolControl(bool)           L5514
DashReadyFirst / DashReady / Dash / DashIm
DashReadyCamera / DashNotify / DashWay / DashStart
```

### 锁定系统（⚠️ 高危，见文末事故）

```
LockonLock : bool                 L4092
LockonChange(target)              L5208
LockonChangeNext(bool reverse)    L5090
ForceLockon()                     L5320
SilentLockon()                    L5331
ForceLockoff()                    L5342
LockonRecalc()                    L5402
LockonAllTarget()                 L6536
TargetChangeHover(Transform)      L6852
LockonLine : LockonCursol         L3128   锁定准星
SemiLockonTargetList / CrossList  L3098/3113
VCameraLockonCrossSingle/MiddleSingle/CrossMulti/CrossCross/Long
TargetGroupSingle / TargetGroup1 / TargetGroupDash / TargetGroupAll
```

## 三、角色基类（CharacterControllerBase）

```
位移执行（三条路径）
  ApproachForm(List<Vector3>, float duration)           L5082
  ApproachFormEvent(...)                                L5095    带事件（走刚体）
  ApproachFormEventNorigid(...)                         L5108    不走刚体
  ApproachWay() / ApproachEventComplete()               L5156/5133

状态与参数
  Lockon : bool                                 L2308   该角色是否被锁定
  LockonTargetCharacter : CharacterControllerBase  L2347  ★ 锁定目标（战斗枢纽）
  IsLookLock : bool                             L2095
  LockonDistance / LockonMoveSpeed              L2416/3194
  InputDirect : Vector3                         L2828   输入方向
  ApproachPosition : Vector3                    L3341
  MyCenterOffset / MyOffset / TargetOffset      L2321/2334/2403
  Rigid : Rigidbody                             L2701   刚体（非运动学、不受重力）

朝向
  LookTarget(float step, GRAVITY gravity)       L4596
  LookTargetForce() / LookTargetUpdate()        L4585/4609
  CalcAngle() / CalcAngle(Transform)            L5581/5593

枚举
  GRAVITY { NONE=1, ZERO=2, HALF=4, FULL=6 }   ← 按位，非递增计数
```

## 四、BattleManager 暴露的战斗机制（由 lambda 命名反推）

| Lambda | 机制 |
|---|---|
| `_BurstStart_b__146_*` | 大招启动（LauncerController + 角色） |
| `_HoldStart_b__148_*` | 蓄力/长按启动（发射器 + 角色 + UI） |
| `_LockonDispOpen_b__119_*` | 锁定提示开启（粒子） |
| `_RemarkerTartget_b__122_*` | 目标标记（角色 + 3D 跟随 UI） |
| `_FlashMoveAdvantage_b__161_*` | 闪避/瞬移优势判定（ShotController） |
| `_DeleteShotAll` / `_DefenceShotAll` | 清空 / 防御所有子弹 |
| `_BurstPoolSync` / `_SetBurstFrame` | 大招对象池同步 |
| `_AfterAssetbundleLoad_b__92_*` | 资源加载后的战斗初始化（5 步） |

## 五、⚠️ 已确认的高危项（事故记录）

### `LockonTargetCharacter` 不可写

**事故（2026-10-15）**：把它置 null 以"抹除锁定"，导致**整个战斗系统崩溃**：
敌人 AI 消失、锁定消失、战斗 UI 消失。
日志证据：`[LOCK] target cleared (count=1)` —— 只清 1 次，系统随即失效。

**原因**：它不只是相机瞄准用的字段，而是**战斗系统追踪目标的枢纽**。
置空等于宣告"场上没有敌人"，敌人 AI 与战斗 UI 因此停摆。

**教训**：IL2CPP 方法体是桩，**静态看不到谁在读该字段** →
核心状态字段默认视为**不可写**，除非能验证依赖方。

### `Move` 的变换关系未知

**事故**：按"输入与 Move 同构"推导公式改写 `Move.x/z`，导致**无法移动**。
证据：
```
原版实测   in=(1.000, 0.000) -> Move(x=-0.156, z=0.988)
我的公式   in=(1.000, 0.000) -> Move(-1.000, 0.000)     ← 不一致
```
**结论**：`Move` 不是输入的直映，中间还有额外变换（很可能与锁定/朝向坐标系有关）。
**重新动手前必须先观测**（`MoveTrace.cs` 已写好，用于测"输入→Move→世界位移"三者的真实关系）。

## 六、原版就有的"双形态"结构（对刀枪切换设计的意义）

游戏本身把攻击分成了**两条并列的路**：

```
近战（CROSS）             远程（LONG）
  ATTACK_CROSS              ATTACK_LONG
  BURST_CROSS               BURST_LONG
  ChangeAttack()           ChangeShot()
  ChangeSonicBurst()       ChangeSonicBurstLong()
```

**含义**：刀 / 枪的切换在原版架构里**已有对应位置**，不是要凭空另造一套。
自建框架时，可以**保留这两条路的入口**，只替换其内部行为。
