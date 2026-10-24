# 第三人称脱离准心 · 设计方案（F3 模式）

> **状态**：设计定稿，**尚未实现**。
> 用户明确警告："你改了准心就一定要一口气去把其他移动做完，不然要出事的" —— 本方案按**原子改动**设计。

---

## 一、需求（用户原话汇总）

> 「下一步就是一次新大改了，你想想**第三人称的脱离准心**到底怎么去搞呢」
> 「F1改为原版模式，改自由模式键位为F2，这个是第三人称版本的F3，
>   你改了准心就一定要一口气去把其他移动做完，不然要出事的」
> 「能不能固定一个锚点?然后锚点自己相对爱迪尔静止即可」
> 「类似 2，但是有所不同，鼠标的移动也会让锚点移动的，
>   鼠标控制锚点以爱迪尔为中心的四周旋转，爱迪尔控制锚点的位移」
> 「1（直接用相机朝向），但是你要注意这个相机是自由相机」
> 「枪无限距离的好像，主要刀不好搞中距离与近距离切换，有 2 套绝招要考虑」

### 1.1 键位（已实现）

| 键 | 模式 | 状态 |
|---|---|---|
| **F1** | **Vanilla** 原版模式，框架完全不介入 | ✅ 已上线 |
| **F2** | **FreeFlight** 自由模式（空格升/Shift降 + 相机） | ✅ 已上线 |
| **F3** | **ThirdPerson** 第三人称脱离准心 | ⚠️ 键位已建，**按下只记日志，不改任何东西** |

### 1.2 瞄准基准

**相机正前方**（用户指定），但锚点**不直接读相机位置** —— 见 §3.2。

---

## 二、为什么这是原子改动

"准心"不是独立 UI 元素，而是**整条瞄准链的可视化末端**：

```
准心画在哪   ← LockonCursol.SetFinishPoint(目标位置)
身体转向哪   ← LookTargetController.SetTarget(目标)
攻击朝哪打   ← 目标方向
接近/冲刺点  ← SetApproachPoint(目标)
大招冲哪     ← BurstApproach(目标)
移动参考     ← 目标 / 相机
```

**若只改准心而不改下游**：
```
准心指着 A  →  身体转向 B（仍读旧锁定）  →  攻击朝 B 打
                = 视觉与实际不一致 = 出事
```

**⇒ 必须把整条链一起换，不能逐处打补丁。**

---

## 三、核心设计

### 3.1 洞察：整条链归结为**一个 Transform**

| 机制 | 类型 | 是否只需 Transform |
|---|---|---|
| `LookTargetController.Target` | `Transform` | ✅ |
| `LockonCursol.SetFinishPoint(Vector3)` | 任意点 | ✅ |
| `PlayerControlCustom.SetApproachPoint(Transform)` | `Transform` | ✅ |
| `LookTargetController.SetTarget(Transform)` | `Transform` | ✅ |
| `CharacterControllerBase.LockonTargetCharacter` | **`CharacterControllerBase`** | ❌ 需要角色实例 |

**⇒ 除 `LockonTargetCharacter` 外，其余全部只需一个 `Transform`。**
这是"路径 A / B"的分界点。

### 3.2 ★ 锚点模型：轨道式（用户定稿）

```
锚点角度  ← 鼠标控制（绕爱迪尔旋转），与相机朝向同步
锚点原点  ← 爱迪尔位置（她移动，锚点平移）
锚点位置  = 爱迪尔位置 + 球坐标(鼠标 yaw/pitch, Distance)
```

**为什么不直接读相机位置**：

| 方案 | 抖动风险 | 结论 |
|---|---|---|
| 锚点 = 相机位置 + 相机朝向 × D | 高（相机有阻尼/缩放会摆） | ❌ 我最初的方案 |
| **锚点 = 爱迪尔位置 + 方向(鼠标角) × D** | **低** | ✅ **用户方案，采纳** |

用户方案把"角度来源"与"位置来源"解耦：角度来自鼠标（稳定），位置来自爱迪尔（稳定），**不经过相机** ⇒ 无累积误差。

**参考实现 —— 游戏自己的场景鉴赏自由相机（真实存在，实测确认）**：

```csharp
public class CameraHolder : MonoBehaviour
{
    public Transform Holder;
    public float currDistance;              // 距离
    public float xRotate;                   // 水平角
    public float yRotate;                   // 垂直角
    public float yMinLimit / yMaxLimit;     // ★ 俯仰限制（该抄）
    public float prevDistance;
}
public class CameraOrbit : MonoBehaviour
{
    public Transform target;
    public float distance / minDist / maxDist;
    public float lerpSpeed;                 // ★ 平滑（该抄）
    public float scrollSpeed;
    public bool raycastedDistance;          // 射线防穿墙（需确认要不要）
}
public class FreeCamera : MonoBehaviour     // 完全自由飞（WASD），非轨道式
{
    public bool enableInputCapture;
    public bool holdRightMouseCapture;
    public float lookSpeed / moveSpeed / sprintSpeed;
}
```

**⇒ 锚点照 `CameraHolder` 实现：两个角度 + 距离 + 俯仰限制 + 平滑（`lerpSpeed`）。**

### 3.3 ★★ 用【锚点距离】驱动近战形态（解决用户的刀难点）

用户指出的问题：
> 「主要刀不好搞中距离与近距离切换，有 2 套绝招要考虑」
> 「枪无限距离的好像」

**⇒ 让锚点距离成为形态选择的依据**：

| 锚点距离 | 对应近战套 | `CrossRange` 实测 | 配置完整度 |
|---|---|---|---|
| **< 8** | `EXS_BLADE` | 8.00 | singles 4/5 |
| **8 ~ 12** | `QUINTERD` | 10.00 | singles 4/5 |
| **> 12** | `RAYS_BUTE` | 12.00 | singles 5/5 |

**可行性已全部实测验证**：
- ✅ 能**实时判定**当前形态：`GetCurrentAttackParam()` + 身份比对 —— **双向通过**
  ```
  枪：currentAttackParam = 'ShotParam'(id=-49742)  → rangedSlot=[0]EXS_SHOT.Shot
  刀：currentAttackParam = 'SingleAttackParam3'(id=-48250) → meleeSlot=[0]EXS_BLADE.Single3
  ```
- ✅ 能**主动切换**形态：`PlayerControlCustom.ChangeAttack(ATTACK_CROSS key)` 是公开方法

**⇒ 链路**：`锚点距离 → 选出对应 AttackSelection → ChangeAttack(key)`

**枪**：用户说"无限距离" ⇒ 枪形态下锚点距离**不影响形态选择**，只影响准心指向。

---

## 四、距离参数（尚未定稿）

用户要求「等一下游戏实测吧，按照游戏数据里面阈值来」。当前实测：

| 项 | 实测值 | 备注 |
|---|---|---|
| 近战 `CrossRange` | **8 / 10 / 12** | 三个近战套各自的阈值 |
| 远程 `ExtendRange` | **0（EXS_SHOT）/ 15（QUINTERD_SHOT）** | ⚠️ `EXS_SHOT` 为 0 ⇒ 射程可能由 `AttackParam.Beam` 决定 |
| 相机 `CamDist` | 5 / 6 | 每套配置自带 |
| 战斗实测两人距离 | **约 30~32** | 由包围盒 `size` 反推 |

⚠️ **矛盾**：战斗时两人相距 ~30，但 `ExtendRange` 最大 15
⇒ **`ExtendRange` 不是"有效射程"**，锚点距离**没有现成的单一数字可抄**，
只能按 §3.3 的"近战阈值分组"来定。

---

## 五、受影响的方法全清单（原子边界）

> 来源：Cpp2IL 转储的 `PlayerControlCustom` 真实方法名

### 移动 / 位移
```
protected void MoveControl()
protected Vector3 Move                                  （字段）
protected Vector3 DashCameraPositionOrg                 （字段）
```

### 冲刺
```
protected override void DashReadyFirst() / DashReady()
protected override void Dash() / DashIm()
public    override void DashNotify() / DashWay() / DashStart() / DashCancel()
protected void DashCursolControl(bool active)
protected void DashReadyCamera()
```

### 接近 / 目标点
```
public void SetApproachPoint(Transform target)
public void ResetApproachPoint()
protected void LockonChange(CharacterControllerBase lockonTarget)
```

### 攻击 / 大招
```
public  override bool CrossPrimary()
protected override void Cross() / CrossAttack()
public  override void BurstValid(int no)
public  override void BurstApproach(int no)
public  override void BurstApproachForce(int no)
public  override void BurstApproachForceSuper(int no)
protected override void Burst() / BurstExterminate()
protected void BurstSelectLogic()
```

### 目标选择
```
protected bool Targetable()
protected void TargetListRecalc(RECALC_METHOD recalcMethod)
protected void CameraTargetSwitch()
public    void TargetChangeHover(Transform target)
```

### 输入汇聚（总入口）
```
protected void EvaluateMoveAndAttack(float horizon, float vertical,
                                     BUTTON_STATUS atk, BUTTON_STATUS assist, BUTTON_STATUS function)
```

---

## 六、与历史事故的关系（必须避开）

| 历史事故 | 本方案如何避开 |
|---|---|
| 把 `LockonTargetCharacter` 置 **null** → 战斗系统崩溃 | 方案**永不置 null**（路径 A 根本不碰它） |
| 改写 `Move` 向量推导错误 → 无法移动 | 不动 `Move` 向量，只改"目标来源" |
| 一次上线多个未验证改动 → 无法定位 | 用阶段门：F3 在全部完成前不暴露 |

---

## 七、分步实施计划（阶段门保证原子性）

| 阶段 | 内容 | 验收 | 风险 | 状态 |
|---|---|---|---|---|
| **0** | 键位：F1/F2/F3 | 三键切换正确、旧功能不受影响 | 🟢 | ✅ **已完成** |
| **1** | 形态判定探针（`GetCurrentAttackParam` 身份比对） | 刀/枪双向判定正确 | 🟢 | ✅ **已完成（实测通过）** |
| **2** | **锚点本体**：轨道式跟随（两个角度+距离+俯仰限制+平滑），**不接入任何游戏系统** | 日志确认位置正确、零副作用 | 🟢 | ⬅ **下一步** |
| **3** | 接入朝向：`LookTargetController.SetTarget(锚点)` | 身体转向锚点 | 🟡 | |
| **4** | 接入准心：`LockonCursol` 终点 = 锚点 | 准心画在锚点上 | 🟡 | |
| **5** | 接入接近点 + 冲刺（原子组，一起上线） | 位移朝锚点 | 🔴 | |
| **6** | 移动参考改为锚点方向（`MoveControl`） | 移动方向一致 | 🔴 | |
| **7** | 锚点距离驱动近战形态（`ChangeAttack`） | 刀的中近切换 | 🟡 | |
| **8** | 全链路回归 + 文档矫正 | 一致性验证 | —— | |

**阶段 3~7 必须连续完成才对外暴露 F3** —— 这是"一口气做完"的工程实现方式。
（当前 F3 按下只记日志，不做任何改动。）

---

## 八、待确认 / 待实测

| # | 事项 | 怎么定 |
|---|---|---|
| 1 | 锚点距离（刀 / 枪各一个） | 用户要求"按游戏实测阈值"；§4 显示没有现成单一值，需按 §3.3 分组 |
| 2 | 锚点是否要射线检测（防穿进地形） | 参考 `CameraOrbit.raycastedDistance`；需确认游戏里锚点穿地形会怎样 |
| 3 | 俯仰限制范围 | 参考 `CameraHolder.yMinLimit/yMaxLimit`（具体值需读运行时） |
| 4 | 平滑速度（`lerpSpeed`） | 参考 `CameraOrbit.lerpSpeed`（需读运行时） |
| 5 | 枪形态下锚点距离是否也随距离变 | 用户说"枪无限距离" ⇒ 应该不变 |
