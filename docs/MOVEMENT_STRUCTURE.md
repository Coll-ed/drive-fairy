# 驱动妖精 · 移动系统结构（转储 + 公开源码交叉分析）

> **来源**：Cpp2IL 转储 `_dump\Assembly-CSharp.dll` 的真实类型定义
> **可靠度**：类型/字段/方法签名 = **直接读出**；
> 标注 `⚠️推断` = 从结构或公开资料推测，**未取证**；
> 标注 `📖公开` = 来自 Unity 官方公开源码，非本游戏私有。

---

## 一、移动系统的分层（已取证）

```
① 输入层
   InputController : MonoBehaviour          （全局命名空间 = 从 Standard Assets 复制）
       private void Awake()
       private void Start()
       public  void UpdateKey()             ★ 输入更新入口
       public  void KeyBindingStart()
       protected void KeyBindingFinish()
       private void Update()

   AldealRaysInput                          （游戏的输入资产名，见 InputActions 提取）

② 运动学层（Standard Assets 的 CharacterMotor 体系，被复制进本程序集）
   CharacterMotor : MonoBehaviour
       public bool canControl;
       public bool useFixedUpdate;
       public Vector3 inputMoveDirection;   ★ 输入方向
       public bool inputJump;
       public bool grounded;
       public Vector3 groundNormal;
       public virtual void Awake()
       public virtual void Update()
       public virtual void FixedUpdate()
       private void UpdateFunction()
       private Vector3 ApplyInputVelocityChange(Vector3 velocity)
       private Vector3 ApplyGravityAndJumping(Vector3 velocity)
       private Vector3 GetDesiredHorizontalVelocity()
       private Vector3 AdjustGroundVelocityToNormal(Vector3 hVelocity, Vector3 groundNormal)
       private bool MoveWithPlatform()
       private IEnumerator SubtractNewPlatformVelocity()
       public virtual void OnControllerColliderHit(ControllerColliderHit hit)
       public virtual float GetMaxAcceleration(bool grounded)
       public virtual float CalculateJumpVerticalSpeed(float targetJumpHeight)
       public virtual bool IsJumping() / IsSliding() / IsTouchingCeiling()
       public virtual bool IsGrounded() / TooSteep()
       public virtual Vector3 GetDirection()
       public virtual void SetControllable(bool controllable)

   CharacterMotorMovement                   （纯数据类，被 CharacterMotor 持有）
       public float maxForwardSpeed;
       public float maxSidewaysSpeed;
       public float maxBackwardsSpeed;
       public float maxGroundAcceleration;
       public float maxAirAcceleration;
       public float gravity;
       public float maxFallSpeed;
       public Vector3 velocity;             ★ 当前速度
       public Vector3 frameVelocity;
       public Vector3 hitPoint;
       public Vector3 lastHitPoint;

③ 游戏自研层
   PlayerControlCustom.EvaluateMoveAndAttack(horizon, vertical, atk, assist, function)
       ↑ 唯一的每帧输入汇聚点
   PlayerControlCustom.MoveControl()
   CharacterControllerBase.ApproachForm / ApproachFormEvent / ApproachFormEventNorigid
   ApproachFormParam（数据，见战斗图谱）
```

## 二、★ 关键结论：移动有两种，走的是**两条完全不同的通路**

| 类型 | 载体 | 数据形式 | 谁能改 |
|---|---|---|---|
| **常规移动**（推摇杆走） | `CharacterMotor` + `CharacterMotorMovement` | **速度**（`maxForwardSpeed` / `velocity`） | 改 `CharacterMotorMovement` 的数值 |
| **出手位移**（冲刺/乱舞绕敌/绝招突进） | `ApproachFormParam` | **轨迹点列表**（`List<Vector3> LoaclPoint` + `Duration`） | 改数据或替换选择逻辑 |

### ⚠️ 这纠正了我历史上的一个根本性错误

**我当时以为"位移只有一个通路"**（`PlayerControlCustom.Move` → 实际位置），
于是去写 `Move.y` 想要垂直飞行 —— **改错了对象**：

```
Move（PlayerControlCustom）       = 摇杆输入处理结果（移动意图）
CharacterMotorMovement.velocity   = 常规移动的实际速度载体
ApproachFormParam.LoaclPoint      = 出手位移的轨迹
```

**三者是不同层次的三个东西，我之前把它们混为一谈。**

### 垂直飞行（空格/Shift）应该接在哪

**正确落点 = `CharacterMotorMovement.velocity.y`**（或 `CharacterMotor` 的重力/跳跃通路），
而**不是** `Move.y`。

⚠️ 需实测确认：
- `CharacterMotor` 是否真的在驱动爱迪尔（可能被游戏自研逻辑接管而空置）
- 若空置，则垂直位移应落在 `CharacterControllerBase` 的某条通路

## 三、📖 公开源码可参考（Standard Assets）

因为 `CharacterMotor` 是从 Unity Standard Assets 复制来的，
**它的逻辑可以从官方公开源码读到**（不必反编译）：

| 类 | 来源 | 用途 |
|---|---|---|
| `CharacterMotor` | Standard Assets → `Characters/FirstPerson` 或 `ThirdPerson` | 运动学主逻辑 |
| `CharacterMotorMovement` | 同上 | 速度/加速度参数 |
| `FPSInputController` | Standard Assets | 输入→移动方向的示例映射 |
| `PlatformInputController` | Standard Assets | 相机相对移动的官方实现 |

**★ `PlatformInputController` 值得特别注意** ——
它是 Standard Assets 里"**按相机朝向移动**"的官方实现。
用户要的"朝相机方向移动"，**官方源码里就有现成做法**，
不需要我自创公式（我之前自创的公式正是导致"无法移动"的原因）。

## 四、⚠️ 未取证项（下一步要验证的）

| # | 待验证 | 怎么验 |
|---|---|---|
| 1 | 爱迪尔是否真由 `CharacterMotor` 驱动 | 运行时探针：读 `CharacterMotor.inputMoveDirection` 与角色实际位移是否相关 |
| 2 | `CharacterMotorMovement.velocity.y` 是否可安全写入 | 只读探针先看数值，再试小幅写入 |
| 3 | `Move` 的精确语义（是不是纯输入意图） | 对比 `Move` 与 `inputMoveDirection` 的数值关系 |
| 4 | 出手位移期间常规移动是否被暂停 | 观察 `CharacterMotor.canControl` / `SetControllable()` 的调用时机 |

## 五、输入资产（已取证，来自 InputActions 提取）

```
ActionMap: Player（11 个 action）
  Move          WASD / 方向键 / 左摇杆
  Look          鼠标 delta / 右摇杆
  Fire          左键
  Fire Charge   左键长按
  Avoid         右键
  Avoid Charge  右键长按
  Function      中键
  Burst         左键+右键
  Mode1         数字键 1
  Mode2         数字键 2
  Option        Escape
```

**注意**：游戏用的是 **Unity Input System**（新输入系统，资产名 `AldealRaysInput`），
但 `InputController` / `CharacterMotor` 用的是**旧版 `Input`** ——
⚠️ 说明两者并存，需确认实际生效的是哪一套。

## 六、其它移动相关类型（待分类）

```
OrbitMovement              轨道运动（可能是场景相机/装饰物）
Turn_Move                  转向+移动
AutoMove                   自动移动（可能是过场）
VariousTranslateMove       位移变体
ObjectMove / ObjectMoveDestroy / ObjectMoveParts
TargetMoveController       朝目标移动（★ 可能用于锁定移动）
NavMeshAgentControlBehaviour  NavMesh 控制（Timeline 里的行为）
```

**`TargetMoveController`** 值得查 —— 名字暗示"朝目标移动"，可能与锁定移动直接相关。

---

## 七、下一步（按用户要求：全部吃透后写产出入档）

1. 查 `TargetMoveController` / `Turn_Move` / `OrbitMovement` 的真实字段
2. 查 UI 系统（`LockonHUD` / `UIFollow3DObject` / `UICanvasManager` + 距离提示文字）
3. 查敌人 AI（`EnemyLogicsController` / `EnemyActionLogics` / `EnemyControlSet` / `EnemyBaseContainer`）
4. 汇总成统一文档，并标注"可改 / 需验证 / 不可碰"三档
