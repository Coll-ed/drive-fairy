# 驱动妖精 · 相机系统结构（转储 + 用户实测现象对照）

> **来源**：Cpp2IL 转储的 `Assembly-CSharp.dll`（游戏侧）与 `Cinemachine.dll`（引擎包）
> **可靠度**：类型/字段/枚举 = **直接读出**；`⚠️推断` = 推测待验证
> **方法体**：Cinemachine 包的类型**方法体完整**（非 `throw null`），游戏侧类仍是 `throw null`

---

## 零、★ 验收目标（用户已裁定）

> **「要自动适应高度差」**

**具体含义**：当玩家飞得比敌人高（或低）时，
相机应**自动俯视 / 仰视**，把**玩家与敌人一起框进画面**，
而不是像现在这样「看着敌人头顶上的空气」。

**对照用户原话**：
- 「相机因为没有 Z 轴的调控，玩家视角则是看的敌人头顶上的空气」→ **要修的问题**
- 「相机飞高了也不会去转动 Z 轴视角锁定敌人，而是用缩放的方式去看人」→ **不满意的现状**
- 「要自动适应高度差」→ **目标**

**不采用的方案**（用户未选）：
- ❌ 只要能飞正上方往下打
- ❌ 只把缩放上限放宽

---

## 一、用户实测的三个现象（需求原文）

| # | 现象 | 原文 |
|---|---|---|
| 1 | 高度差下瞄准错位 | 「当我在高于敌人的情况下，进行冲刺锁定蓄力的时候，相机因为没有 Z 轴的调控，玩家视角则是看的敌人头顶上的空气」 |
| 2 | 缩放有上限 | 「在一定范围内，视角会缩放保持你可以看到人，但是如果离远了就会出现这个不再缩放视角，这个是游戏本身锁定机制问题」 |
| 3 | 用缩放代替转动 | 「相机飞高了也不会去转动 Z 轴视角锁定敌人，而是用缩放的方式去看人」 |

## 二、游戏侧相机结构（已取证）

```csharp
public class VCameraSelectManager : MonoBehaviour
{
    public List<CinemachineVirtualCamera> VCameraList;
    public void SelectVCamera(int no);            // 按序号切换相机
}

public class VCameraInfo
{
    public string Key;
    public float RotateXOffset;                   // ★ 旋转偏移
    public float RotateYOffset;
    public float Dutch;                           // ★ 荷兰角（倾斜）
}

public class LookTargetController : MonoBehaviour
{
    public float LookStep;                        // ★ 朝向变化步长
    public CharacterControllerBase.GRAVITY Gravity;
    public Transform Target;
    public void SetTarget(Transform target);     // ★ 设置瞄准目标
}
```

### ★ `PlayerControlCustom` 的完整相机字段（L318~L344，已取证）

```csharp
public CinemachineVirtualCamera VCameraMain;                 // L318  主相机
public CinemachineVirtualCamera VCameraLockonCrossSingle;    // L320  近战单锁
public CinemachineVirtualCamera VCameraLockonMiddleSingle;   // L322  中距单锁
public CinemachineVirtualCamera VCameraLockonCrossMulti;     // L324  近战多锁
public CinemachineVirtualCamera VCameraLockonCrossCross;     // L326  近战对近
public List<VCameraInfo>        VCameraSpecialList;          // L328  特殊相机表
public CinemachineVirtualCamera VCameraLockonLong;           // L330  远距锁
public CinemachineVirtualCamera VCameraDashReady;            // L332  ★ 冲刺蓄力（现象 1 发生处）
public CinemachineVirtualCamera VCameraDash;                 // L334  冲刺
public CinemachineVirtualCamera VCameraFingerLine;           // L336
protected CinemachineTargetGroup TargetGroupSingle;          // L338  ★ 单锁跟随组
protected CinemachineTargetGroup TargetGroup1;               // L340
protected CinemachineTargetGroup TargetGroupDash;            // L342  ★ 冲刺跟随组
protected CinemachineTargetGroup TargetGroupAll;             // L344
private void InitVCamera();                                  // L731  初始化
```

**优先级机制（历史实测）**：非战斗时 `VCameraMain` 优先级 100；
战斗中锁定相机优先级 100 → 谁优先级高谁生效（Cinemachine 标准行为）。

## 三、Cinemachine 侧结构（已取证，方法体完整）

### 3.1 CinemachineTargetGroup —— 相机跟随的"目标组"

```csharp
public class CinemachineTargetGroup : MonoBehaviour, ICinemachineTargetGroup
{
    public struct Target { public Transform target; public float weight; public float radius; }

    public enum PositionMode { GroupCenter, GroupAverage }   // ★ 只有两个
    public enum RotationMode { Manual, GroupAverage }
    public enum UpdateMethod { Update, FixedUpdate, LateUpdate }

    public PositionMode m_PositionMode;
    public RotationMode m_RotationMode;
    public UpdateMethod m_UpdateMethod;
    public Target[]     m_Targets;

    public Transform      Transform { get; }
    public Bounds         BoundingBox { get; }      // ★ 包围盒
    public BoundingSphere Sphere { get; }
    public bool           IsEmpty { get; }

    public void AddMember(Transform t, float weight, float radius);
    public void RemoveMember(Transform t);
    public int  FindMember(Transform t);
    public BoundingSphere GetWeightedBoundsForMember(int index);
    public Bounds GetViewSpaceBoundingBox(Matrix4x4 observer);
    public void DoUpdate();

    // ★★★ 与"Z 轴"问题直接相关的接口
    public void GetViewSpaceAngularBounds(Matrix4x4 observer,
        out Vector2 minAngles, out Vector2 maxAngles, out Vector2 zRange);
}
```

**关键**：
- `PositionMode.GroupCenter` = **轴对齐包围盒的中心**（不是加权平均）
- `GroupAverage` = 成员的**加权平均**位置
- 历史实测：`TargetGroupSingle` 含**两个等权成员**（玩家 `AIdealBattle` + 目标 `Estscena(Clone)`），
  `posMode = GroupCenter`

### 3.2 CinemachineGroupComposer —— "缩放看人"的实现 ★

```csharp
public class CinemachineGroupComposer : CinemachineComposer
{
    public enum FramingMode { ... }
    public enum AdjustmentMode { ... }

    public float  m_GroupFramingSize;      // 组框住的大小
    public FramingMode m_FramingMode;
    public float  m_FrameDamping;          // 阻尼（越大越慢）
    public AdjustmentMode m_AdjustmentMode;// ★ 决定"调 FOV"还是"调距离"

    public float m_MaxDollyIn;             // ★ 推进上限
    public float m_MaxDollyOut;            // ★ 拉远上限
    public float m_MinimumDistance;
    public float m_MaximumDistance;        // ★
    public float m_MinimumFOV;
    public float m_MaximumFOV;             // ★
    public float m_MinimumOrthoSize;
    public float m_MaximumOrthoSize;

    public Bounds     LastBounds;          // 上次的包围盒
    public Matrix4x4  LastBoundsMatrix;

    public override void MutateCameraState(ref CameraState curState, float deltaTime);
}
```

### ⚠️ 推断（待探针验证）：这解释了现象 2 和 3

| 现象 | 推测的原因 |
|---|---|
| 「离远了就不再缩放」 | `m_MaxDollyOut` / `m_MaximumDistance` / `m_MaximumFOV` **到达上限** |
| 「用缩放代替转动 Z 轴」 | `m_AdjustmentMode` 选择了"调整 FOV/距离"策略，而非转动 |
| 「看敌人头顶上的空气」 | `PositionMode.GroupCenter` 取包围盒中心，高度差大时该中心偏离双方 |

**验证方式**：`CameraProbe`（F10 触发）会把这些**运行时真实数值**打印出来。

---

## 第三·补 ★★ 运行时实测数据（F10 探针取证，2026-10-15）

> **来源**：`CameraProbe` 在真实战斗中打印的 `[CAMPROBE]`。以下为**实测**，非推断。

### 3.5 活跃相机与全部 vcam 状态

```
active cameras = 3
  cam[0] 'Main Camera'  fov=60.0  depth=-1.0
  cam[1] 'OutlineCamera' fov=1.0  depth=0.0    ← 描边相机
  cam[2] 'UICamera'      fov=60.0  depth=1.0

vcam Main               prio=0~100  fov=50.0  lookAt=AIdealBattle      follow=AIdealBattle
vcam LockonCrossSingle  prio=0       fov=50.0  lookAt=TargetGroupSingle follow=TargetGroupSingle
vcam LockonMiddleSingle prio=0       fov=50.0  lookAt=TargetGroupSingle follow=TargetGroupSingle
vcam LockonCrossMulti   prio=0       fov=50.0  lookAt=TargetGroupMulti  follow=TargetGroupMulti
vcam LockonCrossCross   prio=0       fov=50.0  lookAt=TargetGroupSingle follow=TargetGroupSingle
vcam LockonLong         prio=100     fov=60.0  lookAt=AIdealBattle      follow=AIdealBattle
vcam DashReady          prio=0~100   fov=50.0  lookAt=AIdealBattle      follow=AIdealBattle
vcam Dash               prio=0       fov=20.0  lookAt=TargetGroup        follow=TargetGroup
vcam FingerLine         prio=0       fov=35.0  lookAt=Spine             follow=null
```

### 3.5.1 ★★★ 每个相机的组件归属（决定改法）

```
Main.components:               Composer=Y  GroupComposer=N  Transposer=Y  FramingTransposer=N
LockonCrossSingle.components:  Composer=N  GroupComposer=N  Transposer=N  ★FramingTransposer=Y
LockonMiddleSingle.components: Composer=N  GroupComposer=N  Transposer=N  ★FramingTransposer=Y
LockonCrossMulti.components:   Composer=N  GroupComposer=N  Transposer=N  ★FramingTransposer=Y
LockonCrossCross.components:   Composer=N  GroupComposer=N  Transposer=N  ★FramingTransposer=Y
LockonLong.components:         Composer=Y  GroupComposer=N  Transposer=Y  FramingTransposer=N
DashReady.components:          Composer=Y  GroupComposer=N  Transposer=Y  FramingTransposer=N
Dash.components:               Composer=Y  ★GroupComposer=Y  Transposer=N  FramingTransposer=N
FingerLine.components:         Composer=Y  GroupComposer=N  Transposer=N  FramingTransposer=N
```

**⚠️ 重大教训**：早期版本的探针**只读 `Composer`**，于是四个锁定相机"什么都没报出来"，
被误判为"组件缺失"。**它们用的是 `FramingTransposer`（Cinemachine 的 Body+Aim 二合一组件）。**
⇒ 探查组件时必须显式报告"有/没有"，不能静默跳过，否则会把"用别的组件"误读成"没组件"。

### 3.5.2 ★★★ 各相机被激活的次数（谁才是主力）

```
LockonLong             激活 34 次   ★★★ 绝对主力
LockonCrossSingle      激活  7 次
LockonMiddleSingle     激活  7 次
Main                   激活  4 次   ← 正常状态（无锁定）才用它
DashReady              激活  1 次   ← 冲刺蓄力（用户最初报告问题的相机）
LockonCrossMulti       激活  1 次
```

**⇒ 排查"相机行为"时必须先确认【哪个相机在生效】。**
早期误判的根因就是：改了四个 `prio=0`（当时根本没在工作）的相机，
而真正生效的 `LockonLong` 完全没碰。

### 3.6 ★★★ Z 轴问题的真正原因（几何解释）

用户提问：「原版 XY 轴是已经做到了锁定敌人的，为什么 Z 轴不行呢？」
**这个不对称有明确的几何原因：**

| 相机 | `lookAt` 实际是 | 后果 |
|---|---|---|
| `LockonLong`（主力） | **`AIdealBattle`（玩家自己）** | 瞄准对象根本不是敌人 ⇒ 水平方向只是"相机在玩家身后、敌人在正前方"而**看起来**对准；玩家一飞高，垂直偏差立刻暴露 |
| `DashReady`（冲刺蓄力） | **`AIdealBattle`（玩家自己）** | 同上 |
| 四个单锁相机 | `TargetGroupSingle`（玩家+敌人，等权，`posMode=GroupCenter`） | 瞄准**包围盒中心**：水平分量两人贴身时≈敌人位置（所以 XY 像锁住了）；**垂直分量是两人高度的平均**（所以玩家飞高时瞄准点抬到两人之间 ⇒ 对着敌人头顶的空气） |

**⇒ 原版从未"锁定敌人"** —— 它锁的是"玩家自己"或"两人的中点"。
水平方向因为两人够近而看不出误差，垂直方向的误差则随高度差直接暴露。

### 3.7 四个单锁相机的 `FramingTransposer` 参数（实测）

```
LockonCrossSingle   screen=(0.500,0.700)  camDist=5.00  deadZone=(0,0,0)  softZone=(0.100,0.100)
   follow: damping=(3.00,0.50,1.50)  lookahead=(0,0)  ignoreY=False  targetMovementOnly=True
   group:  mode=HorizontalAndVertical  ★adjustment=ZoomOnly  size=0.90  fov=[30.0,60.0]
LockonMiddleSingle  与 CrossSingle 完全相同
LockonCrossMulti   screen=(0.500,0.700)  camDist=5.00  deadZone=(0,0,0)  softZone=(0.100,0.100)
   follow: damping=(2.00,0.10,2.00)  lookahead=(0,30)  ignoreMoveY=False  targetMovementOnly=False
   group:  mode=HorizontalAndVertical  adjustment=DollyThenZoom  size=0.80  dist=[6.0,30.0]  fov=[40.0,80.0]
LockonCrossCross   screen=(0.500,0.800)  camDist=3.00  deadZone=(0,0,0)  softZone=(0.100,0.100)
   follow: damping=(2.00,0.50,1.00)  lookahead=(0,0)  ignoreMoveY=False  targetMovementOnly=True
   group:  mode=HorizontalAndVertical  adjustment=ZoomOnly  size=1.20  fov=[25.0,60.0]
```

**已排除的嫌疑**（曾经怀疑、实测否定）：

| 嫌疑参数 | 实测值 | 结论 |
|---|---|---|
| `softZone` 太大导致不跟随 | **0.100**（很小） | ❌ 不是问题，软区小意味着敏感 |
| `damping.Y` 太大 | **0.10 ~ 0.50**（很小） | ❌ 不是问题，阻尼小 = 响应快 |
| `lookaheadIgnoreY` 忽略垂直 | **False** | ❌ 没忽略 |
| `dollyOut` / `maxDistance` 上限太小 | **5000**（极大） | ❌ **完全不是上限问题** |
| `deadZone` 挡住 | **全 0** | ❌ 无死区 |

### 3.8 `adjustment=ZoomOnly` 的含义（用户现象的原文解释）

```
LockonCrossSingle / LockonMiddleSingle / LockonCrossCross 全部 adjustment=ZoomOnly
                                                            ↑ 只改 FOV，绝不移动相机位置
```

**这正是用户描述的**：「相机飞高了也不会去转动 Z 轴视角锁定敌人，而是用**缩放**的方式去看人」。
**原版就是明文这么配的。**

对比：
- `LockonCrossMulti` → `DollyThenZoom`（先推拉再缩放）
- `Dash`（非锁定） → `DollyOnly`（只推拉）

### 3.9 `Main`（正常状态相机）—— 不需要改

```
vcam Main: lookAt=AIdealBattle  follow=AIdealBattle
  Composer:   screen=(0.500,0.770)  deadZone=(0,0)  softZone=(0.500,0.000)
  Transposer: binding=LockToTarget  offset=(0.00,1.00,-4.00)  damp=(0.01,1.00,1.50)
```

**它就是"第三人称跟玩家"的相机**，`lookAt`/`follow` 都是玩家自己。
**正常飞行时没有敌人要跟随，所以它不需要 Z 轴锁定 —— 保持不变是正确的。**

### 3.10 包围盒会随时间大幅变化（不代表故障）

```
TargetGroupSingle.BoundingBox center 采样序列（Y 与 Z 变化明显）：
  (9.3, 3.1, 24.1) → (-3.6, 3.2, 19.5) → (-12.3, -2.3, 7.6) → (-23.8, -2.4, 7.3)
  → (-21.5, -3.3, 1.3) → (-17.2, -4.5, -0.3) → (0.0, 0.0, 0.0)
```

- 前几组是真实的两人位置变化
- **最后一组 `(0,0,0)` 说明两组成员都是 `null`**（脱战/敌人销毁后组被清空）
⇒ 读到 `(0,0,0)` 时应判断为"组为空"，而不是"中心在原点"。
  探针已同时输出 `isEmpty` 与各成员 `target=null` 以便区分。



### 3.3 CinemachineComposer —— 屏幕对齐式瞄准（真实字段名）

```csharp
public class CinemachineComposer : CinemachineComponentBase
{
    public Vector3 m_TrackedObjectOffset;   // ★ 瞄准点偏移
    public float   m_LookaheadTime;         // 前视预测
    public float   m_LookaheadSmoothing;
    public bool    m_LookaheadIgnoreY;      // ★ 忽略 Y（垂直）—— 与 Z 轴问题相关！
    public float   m_HorizontalDamping;
    public float   m_VerticalDamping;       // ★ 垂直阻尼
    public float   m_ScreenX, m_ScreenY;    // 目标对齐到屏幕的位置
    public float   m_DeadZoneWidth, m_DeadZoneHeight;
    public float   m_SoftZoneWidth, m_SoftZoneHeight;
    public float   m_BiasX, m_BiasY;
    public bool    m_CenterOnActivate;

    public override CinemachineCore.Stage Stage;   // = Aim
    public Vector3 TrackedPoint { get; }
}
```

**⚠️ 注意**：该版本 Composer **没有 `m_Dutch` 字段**（我在实现探针时记错了这个字段名，
编译报错后从转储确认）。`Dutch` 在 `VCameraInfo` 上（游戏侧）。

**`m_LookaheadIgnoreY`** 值得关注 —— 它直接控制"垂直方向是否参与瞄准预测"。

### 3.4 CinemachineTransposer —— 位置跟随（真实字段名）

```csharp
public class CinemachineTransposer : CinemachineComponentBase
{
    public enum BindingMode { LockToTargetOnAssign, LockToTargetWithWorldUp,
                              LockToTargetNoRoll, LockToTarget, WorldSpace,
                              SimpleFollowWithWorldUp }
    public enum AngularDampingMode { Quaternion, Euler }

    public BindingMode m_BindingMode;
    public Vector3 m_FollowOffset;          // ★ 跟随偏移（相机在目标身后的位置）
    public float   m_XDamping, m_YDamping, m_ZDamping;   // ★ 三轴阻尼
    public AngularDampingMode m_AngularDampingMode;
    public float   m_PitchDamping, m_YawDamping, m_RollDamping, m_AngularDamping;

    public Vector3 EffectiveOffset { get; }
    public override CinemachineCore.Stage Stage;   // = Body
}
```

## 四、★ 已采用的修法（实测有效，已固定启用）

### 4.1 修法：把锁定相机的 `LookAt` 指向敌人本体

```csharp
// CameraAimTuner.AimAt(label, vcam, enemy)
vcam.LookAt = enemy.transform;    // 敌人本体
// vcam.Follow 保持不变（仍是原目标组）
```

**生效范围**（六个相机，覆盖所有实际会被激活的锁定相机）：

| 相机 | 原 `lookAt` | 改为 |
|---|---|---|
| **`LockonLong`**（主力，prio=100） | `AIdealBattle`（玩家自己） | **敌人** |
| **`DashReady`**（冲刺蓄力） | `AIdealBattle`（玩家自己） | **敌人** |
| `LockonCrossSingle` | `TargetGroupSingle`（中点） | **敌人** |
| `LockonMiddleSingle` | `TargetGroupSingle`（中点） | **敌人** |
| `LockonCrossCross` | `TargetGroupSingle`（中点） | **敌人** |
| `LockonCrossMulti` | `TargetGroupMulti`（中点） | **敌人** |

**为什么这样能三轴跟随敌人**：
`FramingTransposer` / `Composer` 的 **Aim 阶段**负责把 `LookAt` 的位置对齐到屏幕。
把它换成敌人 ⇒ 相机转向敌人 ⇒ **飞高时俯视、飞低时仰视**。

**为什么玩家仍可能在画面里**：
`Follow` 没动 ⇒ 相机位置逻辑仍是原版；且
`m_GroupFramingMode = HorizontalAndVertical` 会按目标组的包围盒自动缩放。

### 4.2 启用条件（用户要求：固定进自由模式）

```
FreeFlight 模式：LookAt → 敌人（三轴跟随）
Vanilla   模式：LookAt 还原为最初值（游戏原样）
无锁定目标时  ：LookAt 还原（脱战恢复原版行为）
```

**没有独立开关** —— 由模式判断控制，原版模式完全不介入。

### 4.3 ❌ 失败的尝试（留档，避免重犯）

**方案 B（权重法）**：把目标组 `posMode` 从 `GroupCenter` 改成 `GroupAverage`，
玩家 `weight` 设 0.2、敌人 1.0，想把瞄准点"用权重拉向敌人"。

**实测结果：无效。** 配置确实写成功了：

```
[AIMTUNE] TargetGroupSingle: posMode->GroupAverage playerWeight=0.20 members=2 weightsChanged=1
[CAMPROBE] [0] target=AIdealBattle weight=0.20
[CAMPROBE] [1] target=Estscena(Clone) weight=1.00
```

**为什么无效**：真正生效的相机（`LockonLong` / `DashReady`）**根本不读目标组**，
它们的 `lookAt` 就是**玩家自己**。改组的权重对它们毫无影响。

**⇒ 已删除**：`CameraAimTuner.Apply()`、配置项 `LockCameraAimTune` /
`LockCameraPlayerWeight` / `LockCameraAimAtEnemy` 全部移除。

**教训**：**先确认"哪个对象在生效"，再决定改哪里。**
我在没确认激活相机的情况下改了两轮，都是白做。

### 4.4 排查方法论（本次踩坑总结）

| 步骤 | 做法 | 本次教训 |
|---|---|---|
| 1 | **先确认哪个相机在生效**（比 prio，看激活次数） | 我改了四个 `prio=0` 的相机，白做一轮 |
| 2 | **确认组件归属**（`Composer` / `FramingTransposer` / `Transposer`） | 我只读 `Composer`，把 `FramingTransposer` 误读成"没组件" |
| 3 | **看 `lookAt` 到底指向谁** | 主力相机的 `lookAt` 是玩家自己，从没指向敌人 |
| 4 | 改一个、验一个 | 一次改多个会导致无法定位 |


## 五、★ 现有可用能力（已验证）

`FreeFlightCamera.cs` 已实现且用户确认可用：
- 独立的 `AidealRays_FreeFlightVcam`（`CinemachineTransposer` + `CinemachineComposer`）
- 借优先级接管相机（游戏相机配置一个不改）
- 中键自由视角（绕玩家旋转，不影响移动方向）
- 光标接管（`GameManagerBase.CursorShow`）

## 六、待验证项

| # | 待验证 | 方式 |
|---|---|---|
| 1 | 锁定相机上到底挂的是 `CinemachineComposer` 还是 `CinemachineGroupComposer` | F10 探针 |
| 2 | `m_MaxDollyOut` / `m_MaximumDistance` / `m_MaximumFOV` 的实际数值 | F10 探针 |
| 3 | `TargetGroupSingle` 的实际成员与权重 | F10 探针 |
| 4 | `m_AdjustmentMode` 的当前取值 | F10 探针 |
| 5 | `m_LookaheadIgnoreY` 是否开启 | F10 探针 |
| 6 | 高度差大时 `BoundingBox.center` 相对双方位置的偏移 | F10 探针（含包围盒输出） |

**⇒ 全部由 `CameraProbe.cs`（F10）一次覆盖。**
