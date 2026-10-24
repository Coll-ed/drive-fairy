# 驱动妖精 · 战斗数据结构完整图谱（Cpp2IL 转储提取）

> **来源**：Cpp2IL 转储的 `_dump\Assembly-CSharp.dll`
> **取证方式**：ilspycmd 读取真实类型定义（非 interop 存根）
> **可靠度**：类型名/字段名/枚举取值 = **直接读出，可靠**；
> 标注 `⚠️推断` 的 = 从命名规律推测，**尚未取证**。

---

## 一、总控：SelectionManager（玩家的全部技能配置）

```csharp
public class SelectionManager : MonoBehaviour
{
    public List<AttackSelection>         AttackList;          // 近战
    public List<ShotSelection>           ShotList;            // 远程射击
    public List<SonicBurstSelection>     SonicBurstList;      // 大招·近战路
    public List<SonicBurstLongSelection> SonicBurstLongList;  // 大招·远程路
    public List<CounterBurstSelection>   CounterBurstList;    // 反击技
    public List<ExBurstSelection>        ExBurstList;         // 终结技
    public List<DrainSelection>          DrainList;           // 吸取
    public List<DashSelection>           DashList;            // 冲刺
    public List<AssistSelection>         AssistList;          // 闪避/辅助
}
```

**★ 玩家身上的持有关系（已取证）**：

```csharp
// PlayerControlCustom  L366
public SelectionManager Selection;
```

### 这一句是整个改造的关键

```csharp
player.Selection.AttackList       // 近战配置表（5 段普攻 + 5 个绝招/套）
player.Selection.ShotList         // 远程配置表
player.Selection.DashList         // 冲刺
player.Selection.AssistList       // 闪避
```

**含义**：玩家的所有战斗行为都由这些**列表数据**定义。
改造玩法（如"右键切换刀/枪"）= **切换使用哪个列表/哪一项**，
而不是重写攻击逻辑。这从根上降低了改造难度。

---

## 二、每个类别的完整枚举取值（已取证）

### 枚举层面：每个类别都有 **3 个取值**

> ⚠️ **这是"枚举定义里有 3 个"，不等于"运行时加载了 3 项"** ——
> 实测见第二·补节：`ShotList` 运行时**只有 2 项**。

| 类别 | 取值 A | 取值 B | 取值 C |
|---|---|---|---|
| `ATTACK_CROSS` 近战 | `EXS_BLADE` | `QUINTERD` | `RAYS_BUTE` |
| `ATTACK_LONG` 远程 | `EXS_SHOT` | `QUINTERD_SHOT` | `RAYS_SHOT` |
| `DASH` 冲刺 | `VECTOR_STRAIR` | `PAHNTOM_STRAIR` | `FAIRY_STRAIR` |
| `ASSIST` 闪避/辅助 | `MIRAGE_LINE` | `PHANTOM_LINE` | `FAIRY_LINE` |
| `DRAIN` 吸取 | `CLUSTER_DRAIN` | `CHAIN_DRAIN` | `ETHER_DRAIN` |
| `BURST_CROSS` 近战大招 | `SYLPHILIA_BLAST` | `PHANTOM_BLAST` | `OVER_BLAST` |
| `BURST_LONG` 远程大招 | **`HEITHER_CANNON`** | `OCTLOUD_FORMATION` | `ANGEL_ROAR` |
| `BURST_EXTERMINATE` 终结技 | `GROUND_ZERO` | `AIR_BUSTER` | `GLAM_SIGHT` |
| `BURST_COUNTER` 反击技 | `STRAIR_EDGE` | `PHANTOM_EDGE` | `SPIRAL_EDGE` |

### ❌ 已被实测否定的推断：「3 个变体 = 3 种形态」

**曾经的推断**（现已作废，保留在此以免重复犯错）：

```
变体 A（爱迪尔本系）：  EXS_BLADE / EXS_SHOT / SYLPHILIA_BLAST / VECTOR_STRAIR
变体 B（Phantom 系）：  QUINTERD / PHANTOM_BLAST / PHANTOM_EDGE / PAHNTOM_STRAIR
变体 C（Rays/Fairy 系）：RAYS_BUTE / RAYS_SHOT / FAIRY_STRAIR / ANGEL_ROAR
```

**为什么被否定**（F9 实测数据）：

| 否定的点 | 实测证据 |
|---|---|
| 不等于 3 种形态 | `AttackList` 的 3 项 `CrossRange` 是**递增档位**（8→10→12）、`HP+` 也递增（0→300→600）⇒ 更像**同一形态的成长阶段** |
| 各列表项数不一致 | `ShotList` 只有 **2 项**（无 `RAYS_SHOT`）；其余列表 3 项 |
| 各套配置完整度不同 | `EXS_BLADE`/`QUINTERD` 是 `4/5`，`RAYS_BUTE` 是 `5/5` |

**⇒ 不要再用"3 变体 = 3 形态"这个说法。正确的表述见第二·补节。**


---

## 二·补 ★ 运行时实测数据（F9 探针取证，2026-10-15）

> **来源**：`SelectionProbe` 在真实战斗中打印的 `[SELPROBE]`（非推断）

```
AttackList: 3 项
  [0] EXS_BLADE    CrossRange=8.00   CamDist=5.00  HP+0    singles=4/5  exs=4/5
  [1] QUINTERD     CrossRange=10.00  CamDist=6.00  HP+300  singles=4/5  exs=4/5
  [2] RAYS_BUTE    CrossRange=12.00  CamDist=6.00  HP+600  singles=5/5  exs=5/5

ShotList: 2 项
  [0] EXS_SHOT      ExtendRange=0.00   ArmorPoint=5   HP+0    hasShotParam=True
  [1] QUINTERD_SHOT ExtendRange=15.00  ArmorPoint=12  HP+600  hasShotParam=True

DashList: 3 项
  [0] VECTOR_STRAIR   LongRange=60.00  CountMax=35  DashAvoid=False
  [1] PAHNTOM_STRAIR  LongRange=65.00  CountMax=32  DashAvoid=False
  [2] FAIRY_STRAIR    LongRange=70.00  CountMax=30  DashAvoid=True
  （三项均 hasForm=True hasTimeline=True）

AssistList: 3 项
  [0] MIRAGE_LINE   MiddleRange=15.00  LockonMoveSpeed=0.16  SpeedMagni=4.00
  [1] PHANTOM_LINE  MiddleRange=16.00  LockonMoveSpeed=0.18  SpeedMagni=4.20
  [2] FAIRY_LINE    MiddleRange=18.00  LockonMoveSpeed=0.20  SpeedMagni=4.80

SonicBurstList / SonicBurstLongList / CounterBurstList / ExBurstList / DrainList: 各 3 项
```

### ★ 实测矫正了三条之前的推断

| # | 之前的推断 | **实测结果** |
|---|---|---|
| 1 | `CrossRange` 是"攻击生效的最大距离" | ❌ **错**。它是**递增的距离档位阈值**（8 → 10 → 12），`HP+` 也递增（0 → 300 → 600）⇒ 这三项更像**同一形态的成长阶段**，而非三种形态 |
| 2 | 每套都有 5 段普攻 + 5 个绝招 | ❌ **不成立**。`EXS_BLADE` 与 `QUINTERD` 都是 `4/5`，只有 `RAYS_BUTE` 是 `5/5` ⇒ 印证用户所说"初始形态只有部分绝招" |
| 3 | `ShotList` 应有 3 项（与前几类对应） | ❌ **只有 2 项**（`EXS_SHOT` / `QUINTERD_SHOT`），**没有 `RAYS_SHOT`** |

### 新增发现：`EXS_SHOT.ExtendRange = 0.00`

**射程为 0** —— 说明它的射程不由 `ExtendRange` 决定，
很可能由 `AttackParam.Beam`（光束参数：`BeamStatics/Delay/Duration`）定义。
**待验证**：读 `ShotParam.Beam` 的实际内容。

### 新增发现：`CamDist`（相机距离）随档位变化

```
EXS_BLADE  CamDist=5.00
QUINTERD   CamDist=6.00
RAYS_BUTE  CamDist=6.00
```

**说明每套配置自带"相机拉多远"** —— 这是改造相机时需要一并考虑的关联参数。


**这与用户描述对应**：用户说"X2 初始形态只有 3 个绝招"
（远距离激光 / 近距离乱舞 / 中距离能量爆发）。

**待验证**：需读运行时 `SelectionManager` 实例里各列表的实际内容与顺序。
**不要**把上表当作形态映射使用。

---

## 三、单套攻击配置（AttackSelection）

```csharp
[Serializable]
public class AttackSelection
{
    public ATTACK_CROSS Key;                // 本套属于哪个变体
    public AttackParam SingleAttack1Param;  // ★ 普攻第 1 段
    public AttackParam SingleAttack2Param;  // 第 2 段
    public AttackParam SingleAttack3Param;  // 第 3 段
    public AttackParam SingleAttack4Param;  // 第 4 段
    public AttackParam SingleAttack5Param;  // ★ 第 5 段（对应用户说的"乱舞 5 段"）
    public AttackParam ExAttack1Param;      // 绝招 1
    public AttackParam ExAttack2Param;      // 绝招 2
    public AttackParam ExAttack3Param;      // 绝招 3
    public AttackParam ExAttack4Param;      // 绝招 4
    public AttackParam ExAttack5Param;      // 绝招 5
    public int HitpointPlus;                // 血量加成
    public float CrossRange;                // ⚠️ 语义未确认（见本节警告，勿当射程用）
    public float CameraDiostance;           // 相机距离（原文拼写如此）
}
```

**注意 `CrossRange` 在这里** —— 说明距离阈值是**每套配置自带**的，而不是全局的。

### ⚠️ `CrossRange` 的语义：**尚未确认**（曾经的推断已被实测削弱）

**曾经的推断**（不可再直接使用）：
> 「`CrossRange` = 攻击生效的最大距离；原版按距离与各套的 `CrossRange` 比较来选」

**实测数据**（F9）：

```
EXS_BLADE    CrossRange=8.00    HP+0
QUINTERD     CrossRange=10.00   HP+300
RAYS_BUTE    CrossRange=12.00   HP+600
```

**为什么这个推断站不住**：数值**递增**，且 `HP+` 同步递增（0→300→600）。
若它是"攻击射程"，那么排在前面的短射程配置会在超过 8 单位后失效，
但实测 `singles=4/5` 说明这些配置**本身是完整的攻击套**，不是距离分支。

**目前的两种候选解释（均未验证）**：
1. **成长阶段阈值** —— 每套代表一个成长阶段，`CrossRange` 是解锁/适用条件
2. **距离档位** —— 由当前距离决定用哪一套，但选择逻辑在不可读的方法体里

**⇒ 在读到决策逻辑之前，不要基于 `CrossRange` 下结论。**


---

## 四、单次攻击定义（AttackParam）

```csharp
public class AttackParam : MonoBehaviour
{
    public AnimationParam AnimParam;              // 动画参数
    public AttackForce Force;                     // 力度
    public ParticleSystem AttackEffect;           // 攻击特效
    public List<ParticleSystem> ExtraEffects;     // 额外特效
    public AudioClip AttackSound;
    public float AttackSoundDelay;
    public AttackSubParam SubParam;               // 伤害参数
    public ApproachFormParam FormParam;           // ★ 位移参数（见下）
    public bool IsLookLock;                       // 是否锁定朝向
    public bool TrailShadow;
    public HomingParam Homing;                    // 追踪：IsHoming / HomingDelay / HomingInterval
    public BeamParam Beam;                        // ★ 光束：BeamStatics / Delay / Duration
    public bool CrossDisabled;                    // 禁用近战
    public int ShotRepeat;                        // 连射次数
    public float ShotInterval;                    // 连射间隔
    public bool Boost;                            // 冲刺
    public bool SuperArmor;                       // 霸体
    public bool SuperSuperArmor;
    public bool Emergency;
}
```

**`Beam`（光束）就是远程攻击/激光的实现** —— 用户说的"远距离激光"对应
`BURST_LONG.HEITHER_CANNON` + `BeamParam`。

---

## 五、位移的真实载体（★ 解释历史失败）

```csharp
public class ApproachFormParam : MonoBehaviour
{
    public APPROACH_FORM Form;          // 位移形态
    public float Duration;              // 时长
    public float Distance;              // 距离
    public List<Vector3> LoaclPoint;    // ★★ 位移轨迹点列表（原文拼写 Loacl）
    public float ConditionVal;
    public AudioClip Sound;
    public ParticleSystem Effect;
    public bool Rolling;                // 翻滚
    public bool AllowLong;
}
```

### ⚠️ 这纠正了一个重大误解

**位移 = "沿 `LoaclPoint` 点列表在 `Duration` 内走完"**，
而**不是**"方向向量 × 速度"。

| 我当时的做法 | 为什么必然失败 |
|---|---|
| 往 `PlayerControlCustom.Move.y` 写值 | **改错对象** —— 位移指令在 `ApproachFormParam.LoaclPoint` |
| 推导 `Move` 的变换公式 | `Move` 是摇杆输入处理结果，**不是位移指令** |
| 猜 `ApproachForm(pointList, duration)` 的语义 | 方向对了，但没意识到参数来自 `ApproachFormParam` 数据 |

**`Move` 的真实作用**（⚠️推断）：只承载玩家的**移动意图/朝向**，
出手类位移（冲刺、乱舞绕敌、绝招突进）全走 `ApproachFormParam`。

---

## 六、伤害定义（AttackSubParam）

```csharp
public class AttackSubParam : MonoBehaviour
{
    public int DamagePoint;
    public int DamagePointOrg;            // 原始值（用于还原）
    public ATTACK_LEVEL AttackLevel;      // 攻击分级
    public AudioClip AttackSound;
    public AudioClip HitSound;
    public BLOW_PTN BlowPtn;              // 击退模式
    public Vector3 BlowForce;             // 击退力
    public float BlowDuration;            // 击退时长
    public ARMOR_BREAK ArmorBreak;        // 破甲
    public HITSTOP Hitstop;               // 顿帧
    public bool Paralyze;                 // 麻痹
    public bool AbsoluteHit;              // 绝对命中
    public bool SwitchNextTarget;         // 切换下一目标
    public ParticleSystem Effect;
}
```

---

## 七、其它类别的结构

```csharp
public class ShotSelection          // 远程射击
{
    public ATTACK_LONG Key;
    public AttackParam ShotParam;
    public int HitpointPlus;
    public int ArmorPoint;
    public float ExtendRange;
}

public class DashSelection          // 冲刺
{
    public DASH Key;
    public AttackParam DashAttackParam;
    public AnimationParam DashReadyParam;
    public ApproachFormParam DashFormParam;   // ★ 冲刺也是位移数据驱动
    public PlayableDirector DashTimeline;     // ★ 时间轴驱动演出
    public int CountMax;
    public float LongRange;                   // 冲刺的射程
    public List<ParticleSystem> ChargeEffect;
    public AudioClip ChargeSE;
    public bool DashAvoid;
}

public class AssistSelection       // 闪避/辅助
{
    public ASSIST Key;
    public AnimationParam AvoidParam;
    public ApproachFormParam AvoidFormParam;  // ★ 同样是位移数据
    public float LockonMoveSpeed;
    public float SpeedMagni;
    public float MiddleRange;
}

public class DrainSelection        // 吸取
{
    public DRAIN Key;
    public PlayableDirector DrainTimeline;
    public PlayableDirector DrainSteamTimeline;
    public int CountMax;
    public int DrainPlusVal;
    public int DrainMinusVal;
    public bool AllowMultiDrain;
}

public class SonicBurstSelection   // 大招（近战路）
{
    public BURST_CROSS Key;
    public PlayableDirector BurstTimeline;    // ★ Timeline 资产驱动
    public BurstParam BurstParam;
    public int BurstPlus;
}

public class BurstParam : MonoBehaviour
{
    public AttackParam Param;
    public List<AttackSubParam> AttackSubParamList;
    public List<ApproachFormParam> FormSubParamList;   // ★ 大招也有位移列表
}
```

---

## 八、对"右键切换刀/枪"的实施路径（依据以上结构）

```
原版：某种规则（具体逻辑在不可读的方法体里）从
      AttackList / ShotList 里挑一套当前生效的配置
改造：右键 → 手动指定用哪套配置

可行的挂载层级（由浅入深）：
  ① 数据层：读/改 player.Selection.*List 的内容与顺序   ← 最安全，不碰逻辑
  ② 选择层：找到"挑配置"的方法，改其决策输入（★ 该方法尚未定位）
  ③ 替换层：MonoMod 整体替换决策方法（架构已验证可行）
```

### ✅ 第一步已完成（F9 实测）

`SelectionManager` 的运行时数据**已读到**（见第二·补节）：
`AttackList` 3 项 / `ShotList` 2 项 / `DashList` 3 项 / `AssistList` 3 项，
各套的 `Key`、范围值、血量加成、配置完整度均已取得。

### 尚未解决的关键问题

**"哪一套是当前生效的"这个决策逻辑还没找到。** 因为：
- 方法体不可读（IL 已丢弃）
- `CrossRange` 的语义未确认（见第三节的警告）

**⇒ 下一步要做的不是改代码，而是取证"当前生效的是哪一套"** ——
例如用探针读取 `CharacterControllerBase.CurrentAttackParam`（`get` 方法存在），
再与 `AttackList` 的各套 `Key` 对照，即可知道游戏选了哪套、以及选择是否随距离变化。


---

## 九、方法体不可恢复（已取证，不再重试）

| 证据 | 结果 |
|---|---|
| `GameAssembly.dll` CLR Runtime Header | **RVA=0x0, Size=0** |
| 全文件搜 CLI 元数据签名 `BSJB` | **未找到** |
| `AldealRaysR18_Data\Managed\` | **不存在** |
| Cpp2IL Native Method Detection | 跑 8.9 秒反汇编后**恢复失败**，方法体 = `throw null` |

**结论**：旧式 IL2CPP（IL → C++ → 机器码），**原始 IL 已丢弃**。
数据结构完整可得，**方法逻辑只能靠运行时观测或反汇编机器码**。
