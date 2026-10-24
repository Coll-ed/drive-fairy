# 驱动妖精 · UI 与敌人 AI 结构（转储分析）

> **来源**：Cpp2IL 转储 `_dump\Assembly-CSharp.dll` 的真实类型定义
> **可靠度**：类型/字段/枚举 = **直接读出**；`⚠️推断` = 推测未取证
> **方法体**：全部为 `throw null`（IL 不可恢复，已多次取证）

---

# 第一部分：敌人 AI

## 一、AI 架构 = 【距离 × 情境】的 11 套逻辑 + 概率权重

```csharp
public class EnemyLogicsController : MonoBehaviour
{
    public float Interval;                  // 决策间隔
    public float IntervalEmergency;         // 紧急决策间隔
    public int   TargetPriority;            // 目标优先级

    // ★ 11 套行为逻辑，按【距离 + 情境】分派
    public EnemyActionLogics CrossLogics;                // 近距离
    public EnemyActionLogics CrossLogicsNolockon;        // 近・未锁定
    public EnemyActionLogics CrossFlashLogics;           // 近・闪光/闪避
    public EnemyActionLogics CrossCrossLogics;           // 近・对近
    public EnemyActionLogics MiddleLogics;               // 中距离
    public EnemyActionLogics MiddleLogicsNolockon;
    public EnemyActionLogics MiddleFlashLogics;
    public EnemyActionLogics LongLogics;                 // 远距离
    public EnemyActionLogics LongLogicsNolockon;
    public EnemyActionLogics OutLogics;                  // 射程外
    public EnemyActionLogics PlayerCrossAttackLogics;    // 玩家近战攻击时
    public EnemyActionLogics PlayerLongAttackLogics;     // 玩家远程攻击时
}
```

**★ 关键设计：敌人也按【距离】分派行为**（用 `RANGE { CROSS, MIDDLE, LONG, OUT }` 枚举，
实测已确认）——这与玩家侧的"攻击形态挑选"**在设计哲学上是同一套**。

> ⚠️ 注意区别：**敌人的 `RANGE` 距离分派是实测确认的**；
> 而玩家侧的挑选规则**尚未定位**（`CrossRange` 语义未确认，
> 见 `COMBAT_DATA_ATLAS.md` 第三节）。不要因为这里"看起来一致"就推断玩家侧也是按距离。


## 二、行为 = 概率加权的动作列表

```csharp
public class EnemyActionLogics : MonoBehaviour
{
    public List<EnemyActionLogic> LogicList;

    public class EnemyActionLogic
    {
        public ENEMY_ACTION_LOGICS Action;      // 做什么
        public float               Percent;     // ★ 概率权重
        public MoveParamater       MoveParamater;
        public AudioClip           SoundEffect;
    }

    public class MoveParamater
    {
        public float Speed;                     // 速度
        public float MoveTacticalX;             // 战术位移量
        public RANGE TargetRange;               // 目标距离
        public float Duration;                  // 持续时长
    }
}
```

**这是概率状态机**：每套逻辑里有一张动作表，每项带 `Percent` 权重，
决策时按权重随机选一个执行。

## 三、AI 能做的动作（`ENEMY_ACTION_LOGICS` 真实取值）

```csharp
public enum ENEMY_ACTION_LOGICS
{
    NONE,
    ATTACK,               // 攻击
    ATTACK_CONTINUE,      // 连续攻击
    AVOID,                // 闪避
    AVOID_ATTACK,         // 闪避反击
    AVOID_BACK,           // 后撤闪避
    HOLD,                 // 蓄力/待机
    DASH,                 // 冲刺
    BURST,                // 大招
    APPROACH,             // 接近
    APPROACH_DASH,        // 冲刺接近
    TACTICAL_MOVE,        // 战术移动
    UNIQUE,               // 特殊技
}

public enum RANGE { NONE, CROSS, MIDDLE, LONG, OUT }   // AI 的距离概念
```

## 四、敌人配置（EnemyControlSet）

```csharp
public class EnemyControlSet : MonoBehaviour
{
    public List<string> Keys;                       // 配置标识
    public int HitpointMax;                         // 最大血量
    public EnemyLogicsController NormalLogic;       // ★ 难度分级：普通
    public EnemyLogicsController HardLogic;         //   困难
    public EnemyLogicsController SwitchLogic;       //   转换
    public SkillConditions Condition;               // 技能条件
    public PlayableDirector HoldSingleTimeline;     // 演出时间轴
    public PlayableDirector HoldSingleSteamTimeline;
    public PlayableDirector HoldUnionTimeline;
    public int DrainPlusVal;                        // 吸取值
    public int DrainMinusVal;
    public PlayableDirector BurstLongSingleTimeline;
    public PlayableDirector BurstLongUnionLongTimeline;
    public BurstParam BurstLongSingleParam;         // ★ 敌人也有大招参数
    public BurstParam BurstLongUnionParam;
}
```

## 五、敌人本体（EnemyController）

```csharp
public class EnemyController : CharacterControllerBase   // ★ 与玩家共用基类
{
    public float delay;
    public LockonLineController LockonLine;      // 敌人的锁定线
    public float LogicInterval;
    public AttackParam ShotAttackParam;          // 远程攻击参数
    public AttackParam AttackCrossParam;         // 近战攻击参数
    public void SelfDestruction(ActionParam param);
    public override void Sortie(Vector3 pos);    // 出击/出场
    public override void LockonInstructions();   // 锁定指示
}
```

**⚠️ 重要**：`EnemyController` 继承 `CharacterControllerBase` —— **与玩家同一个基类**。
这解释了历史事故的机制：清空玩家基类上的 `LockonTargetCharacter`
会破坏**所有** `CharacterControllerBase` 派生对象的共享逻辑 → 敌人 AI 一起崩。

## 六、对"加强敌人 AI"的直接启示

由于 AI 是**数据驱动的概率状态机**：

| 想做的事 | 改哪里 |
|---|---|
| 让敌人会飞到空中追击 | 加 `APPROACH_FORM.RAISE` 的位移参数 + `ENEMY_ACTION_LOGICS.APPROACH` |
| 调整攻击欲望 | 改 `EnemyActionLogic.Percent` 权重 |
| 加新招 | 在 `LogicList` 里加一项 |
| 改难度 | 换 `NormalLogic` / `HardLogic` |

**不需要写 AI 逻辑代码** —— 但**这些都定义在游戏资产里**（MonoBehaviour 实例数据），
不在代码里。要改需要能读写这些资产（见"待解决"）。

---

# 第二部分：UI 系统

## 一、战斗 UI

```csharp
public class LockonHUD : MonoBehaviour          // ★ 锁定准心 HUD
{
    public GameObject HUDChildOrigin;
    // 含协程 <MarkerTranslate>d__6  ← 标记移动动画
}

public class UIFollow3DObject : MonoBehaviour, IPointerEnterHandler   // ★ 跟随 3D 的 UI
{
    public CharacterControllerBase target;      // 跟随目标（角色）
    public Camera mainCamera;
    public Camera uiCamera;
    public Transform CanvasTrs;
    public UnityEvent HoverEvent;               // 悬停事件
    public Slider HPgauge;                      // ★ 血条
    public RectTransform uiImageTrs;
    public CanvasGroup uiCanvasGroup;
    public void Init();
    public void OnPointerEnter(PointerEventData eventData);
    public void UpdateAlpha(float alpha);
}
```

**`UIFollow3DObject` 就是锁定时跟随敌人的血条/标记** ——
它直接持有 `CharacterControllerBase target`。**🔑 这是 UI 与角色系统之间的桥。**

## 二、通用 UI 管理

```csharp
public class UICanvasManager : MonoBehaviour
{
    public static UICanvasManager GlobalAccess;  // ★ 全局单例
    public bool MouseOverButton;
    public Text PENameText;
    public Text ToolTipText;
    public void UpdateToolTip(ButtonTypes toolTipType);
    public void ClearToolTip();
    public void UIButtonClick(ButtonTypes buttonTypeClicked);
}
```

`GlobalAccess` 是**静态单例** —— 易于访问。

## 三、第三方 UI 框架

`Michsky.UI.Shift.*` 是一整套现成的 UI 框架（**非游戏自研**）：

```
Michsky.UI.Shift.MainPanelButton / ChapterButton / SettingsButton / ...
Michsky.UI.Shift.VirtualCursor         ← 虚拟光标（手柄用）
Michsky.UI.Shift.GamepadChecker
Michsky.UI.Shift.HoldKeyEvent / PressKeyEvent / TimedEvent
Michsky.UI.Shift.SliderGamepadManager / ScrollGamepadManager
```

**含义**：游戏的菜单/按钮系统用的是现成的商用 UI 包。
**改菜单 UI 时应优先查这个框架的文档，而不是从零写。**

## 四、⚠️ 未找到：左上角的"距离提示文字"

用户提到战斗界面**左上角有英文文字提示**（远/中/近距离）。

**转储里没找到明显对应的类型**。可能的位置：
- 在 `LockonHUD` 的 `HUDChildOrigin` 子对象上（文本组件动态设置）
- 在某个未反编译出的 UI 类里
- 与 `CharacterControllerBase` 的距离字段有关（⚠️ **但 `CrossRange` 的语义未确认**，
  见 `COMBAT_DATA_ATLAS.md` 第三节警告 —— 不要假定"该文字由 CrossRange 驱动"）

**待查**：运行时用探针枚举 HUD 下的所有 `Text` / `TMP_Text` 组件。

---

# 第三部分：三系统汇总（可改性分档）

| 系统 | 可改性 | 依据 |
|---|---|---|
| **常规移动** | 🟢 可改数值 | `CharacterMotorMovement` 是纯数据类（`maxForwardSpeed` 等） |
| **出手位移** | 🟡 可改数据/选择 | `ApproachFormParam` 数据结构完整，但实例在资产里 |
| **玩家技能表** | 🟡 可读可改（运行时） | `player.Selection.*List` 是运行时对象，可直接读写 |
| **敌人 AI** | 🟡 可改数据 | 概率状态机，改 `Percent` / `LogicList` |
| **战斗 UI** | 🟢 可访问 | `UIFollow3DObject.target` / `UICanvasManager.GlobalAccess` |
| **任何方法逻辑** | 🔴 不可读 | IL 不可恢复（已多次取证） |

## 方法体不可恢复的取证汇总

| 类型 | `throw null` 数 | 真实语句行 |
|---|---|---|
| `PlayerControlCustom` | 193 | 4 |
| `EnemyController` | 31 | 2 |
| `CharacterMotor` | 30 | 1 |
| `UIFollow3DObject` | 6 | 0 |
| `EnemyActionLogics` | 3 | 0 |

**结论：数据结构 100% 可得，方法逻辑 0% 可得。**
