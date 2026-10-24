# 驱动妖精 · 战斗系统真实结构（Cpp2IL 转储提取）

> **来源**：Cpp2IL 转储的 `Assembly-CSharp.dll`（`_dump\Assembly-CSharp.dll`）。
> 与之前用的 `BepInEx\interop\Assembly-CSharp.dll`（**占位符存根**）不同，
> 转储版本保留了**真实字段名、类型、枚举取值**。
>
> **重要限制**：本游戏用旧式 IL2CPP 编译（IL → C++ → 机器码），
> **IL 已被丢弃**，因此方法体不可恢复（显示为 `throw null`）。
> 但**数据结构完整可见** —— 而这恰恰是我们最需要的部分。

## 一、核心结论：整个攻击系统是【数据驱动】的

**攻击行为不是硬编码在方法体里的，而是由一组 ScriptableObject 风格的配置对象定义。**
这意味着要改造玩法（如"右键切换刀/枪"），**主要是切换/替换配置数据，而不是重写逻辑**。

```
AttackSelection（一套完整攻击配置）
├─ Key : ATTACK_CROSS              近战/远程标识
├─ SingleAttack1Param ~ 5Param     普攻最多 5 段（实测并非每套都填满，见下）
├─ ExAttack1Param ~ 5Param         绝招最多 5 个（同上）
├─ HitpointPlus : int
├─ CrossRange : float              ⚠️ 语义未确认（见 COMBAT_DATA_ATLAS 第三节警告）
└─ CameraDiostance : float         （原文拼写如此）
```

> ⚠️ **本文件早期版本称 `CrossRange` 为"近战范围阈值（原版自动切换的依据）"，
> 该说法已被 F9 实测削弱** —— 实测数值递增（8/10/12）且 `HP+` 同步递增（0/300/600），
> 更像成长阶段。**以 `COMBAT_DATA_ATLAS.md` 的实测节为准。**


## 二、攻击的两条路（真实枚举取值）

```csharp
public enum ATTACK_CROSS      // 近战（刀）
{
    EXS_BLADE,                // 剑刃
    QUINTERD,                 // 乱舞（Quinterd）
}

public enum ATTACK_LONG       // 远程（枪）
{
    EXS_SHOT,                 // 射击
    QUINTERD_SHOT,            // 乱射
}

public enum BURST_CROSS       // 近战大招
{
    SYLPHILIA_BLAST,
    PHANTOM_BLAST,
}

public enum BURST_LONG        // 远程大招
{
    HEITHER_CANNON,           // 激光炮（用户说的"远距离激光"）
    OCTLOUD_FORMATION,
}
```

**用户描述的 3 个绝招与枚举的对应**：

| 用户描述 | 枚举 | 说明 |
|---|---|---|
| 远距离激光 | `BURST_LONG.HEITHER_CANNON` | 远程大招 |
| 近距离乱舞 | `ATTACK_CROSS.QUINTERD` | 5 段乱舞 |
| 中距离能量爆发 | `BURST_CROSS.SYLPHILIA_BLAST` 或 `PHANTOM_BLAST` | 待实测确认 |

## 三、单次攻击的定义（AttackParam）

```csharp
public class AttackParam : MonoBehaviour
{
    public AnimationParam AnimParam;        // 动画参数
    public AttackForce Force;               // 力度
    public ParticleSystem AttackEffect;     // 攻击特效
    public List<ParticleSystem> ExtraEffects;
    public AudioClip AttackSound;
    public float AttackSoundDelay;
    public AttackSubParam SubParam;         // 伤害参数（见下）
    public ApproachFormParam FormParam;     // ★★ 位移参数（见下）
    public bool IsLookLock;                 // 是否锁定朝向
    public bool TrailShadow;
    public HomingParam Homing;              // 追踪：IsHoming/HomingDelay/HomingInterval
    public BeamParam Beam;                  // ★ 光束：BeamStatics/Delay/Duration
    public bool CrossDisabled;              // 禁用近战
    public int ShotRepeat;                  // 连射次数
    public float ShotInterval;              // 连射间隔
    public bool Boost;                      // 冲刺
    public bool SuperArmor;                 // 霸体
    public bool SuperSuperArmor;
    public bool Emergency;
}
```

## 四、位移的真实载体（ApproachFormParam）★ 关键

```csharp
public class ApproachFormParam : MonoBehaviour
{
    public APPROACH_FORM Form;          // 位移形态（枚举）
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

### ⚠️ 这解释了历史上的重大失败

**位移 = "沿点列表在 duration 内走完"，而不是"方向向量 × 速度"。**

| 我当时的做法 | 为什么必然失败 |
|---|---|
| 往 `PlayerControlCustom.Move.y` 写值 | **改错了对象** —— 位移指令在 `ApproachFormParam.LoaclPoint`，不在 `Move` |
| 推导 `Move` 的变换公式 | `Move` 是摇杆输入的处理结果，**不是位移指令** |
| 从 `CharacterControllerBase.ApproachForm(pointList, duration)` 猜语义 | 方向对了，但没意识到**它的参数来自 `ApproachFormParam` 数据** |

## 五、伤害定义（AttackSubParam）

```csharp
public class AttackSubParam : MonoBehaviour
{
    public int DamagePoint;
    public int DamagePointOrg;          // 原始值（用于还原）
    public ATTACK_LEVEL AttackLevel;    // 攻击分级
    public AudioClip AttackSound;
    public AudioClip HitSound;
    public BLOW_PTN BlowPtn;            // 击退模式
    public Vector3 BlowForce;           // 击退力
    public float BlowDuration;          // 击退时长
    public ARMOR_BREAK ArmorBreak;      // 破甲
    public HITSTOP Hitstop;             // 顿帧
    public bool Paralyze;               // 麻痹
    public bool AbsoluteHit;            // 绝对命中
    public bool SwitchNextTarget;       // 切换下一目标
    public ParticleSystem Effect;
}
```

## 六、大招定义（BurstSelection / BurstParam）

```csharp
public class SonicBurstSelection
{
    public BURST_CROSS Key;
    public PlayableDirector BurstTimeline;   // ★ 时间轴（Timeline 资产驱动演出）
    public BurstParam BurstParam;
    public int BurstPlus;
}

public class BurstParam : MonoBehaviour
{
    public AttackParam Param;
    public List<AttackSubParam> AttackSubParamList;
    public List<ApproachFormParam> FormSubParamList;   // ★ 大招也有位移参数列表
}
```

**注意**：大招用 `PlayableDirector`（Unity Timeline）驱动演出 ——
说明大招的视觉表现是**时间轴资产**，不是代码。

## 七、对"右键切换刀/枪"设计的直接启示

**原版逻辑（修正版）**：从 `AttackList` / `ShotList` 里**挑一套当前生效的配置**，
但**具体挑选规则尚未定位**（方法体不可读）。

> ⚠️ 本文件早期版本断言「原版按距离自动选择，依据 `CrossRange`/`MiddleRange`/`LongRange`
> 三个阈值」—— **该断言未经验证，且 `CrossRange` 的语义已被实测削弱**
> （数值递增 + `HP+` 同步递增，更像成长阶段而非射程）。
> 用户口述的"按距离改变"是**设计意图层面的描述**，具体实现的判据仍需取证。

用户的改造：**用右键手动切换**。由于攻击系统是数据驱动的：

```
原版：某种规则（未定位）→ 挑一套 AttackSelection
改造：右键 → 手动指定用哪一套
```

**这意味着改造的核心是"替换选择逻辑"，而不是"重写攻击行为"。**
具体来说需要找到"根据距离挑 AttackSelection"的那个方法并替换它 ——
这比之前设想的"重写移动管线"简单得多。

## 八、工具与产物位置

| 项 | 路径 |
|---|---|
| Cpp2IL 工具 | `_tools\Cpp2IL\`（2022.1.0-pre-release.21） |
| **完整转储** | `_dump\`（80 个程序集，含真实类型信息） |
| 本次关键类 | `AttackSelection` / `AttackParam` / `ApproachFormParam` / `AttackSubParam` / `BurstParam` |

### 重新转储的命令

```powershell
$env:AIDEALRAYS_ROOT = '<游戏根>'
& '<工作区>\_tools\Cpp2IL\Cpp2IL.exe' --game-path $env:AIDEALRAYS_ROOT `
    --output-as dll_il_recovery --output-to '<工作区>\_dump' `
    --use-processor attributeanalyzer,nativemethoddetector
```

## 九、方法体不可恢复（已取证）

| 证据 | 结果 |
|---|---|
| `GameAssembly.dll` 的 CLR Runtime Header | **RVA=0x0, Size=0**（空） |
| 全文件搜 CLI 元数据签名 `BSJB` | **未找到** |
| `AldealRaysR18_Data\Managed\` 目录 | **不存在**（Mono 模式才有） |
| Cpp2IL 的 Native Method Detection | 跑了 8.9 秒反汇编，**恢复失败** → 方法体为 `throw null` |

**结论**：此游戏 IL2CPP 走的是"IL → C++ → 机器码"旧路径，
**原始 IL 在编译期被丢弃，物理上不存在**。要读逻辑只能反汇编机器码（Ghidra/IDA）。
