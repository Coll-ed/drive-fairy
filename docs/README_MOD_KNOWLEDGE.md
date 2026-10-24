# 驱动妖精 · 3D 空战 Mod —— 知识库总索引

> 本文件是全部调研产出的入口。新会话接手请**按顺序读**。

---

## 一、现状速览

| 项 | 状态 |
|---|---|
| 游戏 | 《驱动妖精 / AI-deal-Rays》，Unity **2022.3.62f2**，**IL2CPP**（旧式：IL→C++→机器码） |
| Mod 框架 | BepInEx **6.0.0-be.788**（Il2CppInterop 114 个 interop 程序集） |
| 插件 | `BepInEx\plugins\AidealRays.Flight.dll`（源码在 `plugin\Flight\`） |
| 已验证可用 | ✅ 空格上升 / Shift 下降（框架内）<br>✅ 中键自由视角（相机绕转，不影响移动）<br>✅ 左Alt 切换模式<br>✅ 光标接管 |
| 未验证/已关 | ⚪ `PhantomTargetEnabled`（幻影目标，阶段 1）<br>⚪ `LockRedirectEnabled`（锁定重定向） |

## 二、核心结论（最重要的一条）

# 🎯 **本游戏是"数据驱动"架构**

**玩家的全部战斗行为、敌人的全部 AI 行为，都由【配置数据】定义，不是硬编码逻辑。**

```
玩家：PlayerControlCustom.Selection : SelectionManager
        ├─ AttackList      （近战：5段普攻 + 5绝招/套）
        ├─ ShotList        （远程）
        ├─ SonicBurstList / SonicBurstLongList  （大招两路）
        ├─ DashList / AssistList / DrainList
        └─ CounterBurstList / ExBurstList

敌人：EnemyControlSet
        ├─ NormalLogic / HardLogic / SwitchLogic : EnemyLogicsController
        │     └─ 11 套 EnemyActionLogics（按距离×情境分派）
        │           └─ List<EnemyActionLogic> { Action, Percent(概率权重), MoveParamater }
        └─ BurstLongSingleParam / BurstLongUnionParam
```

**⇒ 改造玩法主要是"换配置/改权重"，而不是"重写逻辑"。**

## 三、文档阅读顺序

| # | 文档 | 内容 | 何时读 |
|---|---|---|---|
| 1 | **本文件** | 索引与现状 | 接手第一步 |
| 2 | [COMBAT_DATA_ATLAS.md](COMBAT_DATA_ATLAS.md) | ★ **战斗数据结构完整图谱**（玩家技能表/攻击/伤害/位移/大招） | 要做玩法改造时 |
| 3 | [MOVEMENT_STRUCTURE.md](MOVEMENT_STRUCTURE.md) | ★ **移动系统分层**（输入→运动学→游戏自研层） | 要改移动/飞行时 |
| 4 | [UI_AND_AI_STRUCTURE.md](UI_AND_AI_STRUCTURE.md) | ★ **UI 与敌人 AI 结构**（含 AI 的 11 套逻辑、概率状态机） | 要改 UI/AI 时 |
| 4b | [CAMERA_STRUCTURE.md](CAMERA_STRUCTURE.md) | ★ **相机系统**（GroupComposer 缩放机制 = "Z 轴"问题的来源） | 要改相机时 |
| 4c | [DESIGN_FREE_AIM.md](DESIGN_FREE_AIM.md) | ★ **第三人称脱离准心设计**（F3 模式；原子改动说明） | 要做 F3 时 |
| 4d | [FREE_AIM_FINDINGS.md](FREE_AIM_FINDINGS.md) | ★ **F3 实测问题与对策**（含用户定稿的锚点优先级设计） | 做 F3 遇到问题时 |
| 5 | [COMBAT_REAL_STRUCTURE.md](COMBAT_REAL_STRUCTURE.md) | 战斗结构（含"位移真相"纠正） | 交叉参考 |
| 6 | [COMBAT_SYSTEM_MAP.md](COMBAT_SYSTEM_MAP.md) | 接口清单 + **事故记录** | 动手前必读 |
| 7 | [ERRORS.md](ERRORS.md) / [NOTES.md](NOTES.md) | 历史错误与笔记 | 排障时 |
| 8 | [FLIGHT_DONE.md](FLIGHT_DONE.md) / [FLIGHT_PHASE1.md](FLIGHT_PHASE1.md) | 飞行功能完成记录 | 参考实现 |

## 四、关键机制速查

### 4.1 位移有**两条完全不同的通路**

| 类型 | 载体 | 数据形式 |
|---|---|---|
| 常规移动（推摇杆） | `CharacterMotor` + `CharacterMotorMovement` | **速度**（`velocity` / `maxForwardSpeed`） |
| 出手位移（冲刺/乱舞/绝招） | `ApproachFormParam` | **轨迹点列表**（`List<Vector3> LoaclPoint` + `Duration`） |

**⚠️ 历史重大错误**：曾以为位移只有一条通路，去写 `PlayerControlCustom.Move.y` —— **改错了对象**。

### 4.2 `APPROACH_FORM` 是"位移形态库"（22+ 种）

```csharp
LINEAR / CIRCLE_HALF / CIRCLE_FULL / RAISE(★上升) / LOCAL_POINT / ZIGZAG /
TELEPO(瞬移) / HIT_AND_AWAY / BACK / CENTER / ROLLOING / ...
```

**★ `RAISE` = 上升形态** —— 游戏**原生支持垂直位移**，三维空战应复用它而非自创。

### 4.3 攻击形态的挑选规则（用户要改成手动）

```
原版：某种规则（★ 具体判据尚未定位 —— 方法体不可读）→ 挑一套 AttackSelection
改造：右键 → 手动指定
```

**⚠️ 已作废的断言（本知识库早期版本曾如此声称，勿再沿用）**：
> 「原版按距离自动选择攻击形态，依据 `CrossRange` / `MiddleRange` / `LongRange`
>   三个阈值」

**为什么作废**：F9 实测 `AttackList` 的三项是
`CrossRange = 8 / 10 / 12` 且 `HP+ = 0 / 300 / 600`（**两者同步递增**），
更像**同一形态的成长阶段**而非射程分支。详见 `COMBAT_DATA_ATLAS.md` 第二·补节。

**"按距离切换"这个说法只来自用户对原版玩法的口述描述，不是代码取证结论。**
在定位到挑选逻辑之前，**任何基于"距离阈值"的实现都不要做**。

**用户口述的"按距离改变"是设计意图层面的描述**，具体实现的判据仍需取证。

### 4.4 敌人 AI 是概率状态机

```csharp
EnemyActionLogic { ENEMY_ACTION_LOGICS Action; float Percent; MoveParamater ... }
ENEMY_ACTION_LOGICS = ATTACK / AVOID / DASH / BURST / APPROACH / TACTICAL_MOVE / UNIQUE ...
```

**加强 AI = 改 `Percent` 权重 / 扩充 `LogicList`**，不写 AI 代码。

## 五、技术边界（已多次取证，不要再试）

| 尝试 | 结论 |
|---|---|
| 反编译成 C# 再重编译 | ❌ **物理不可行** —— CLR Header 为空、无 `BSJB` 签名、`Managed\` 目录不存在 |
| Cpp2IL 恢复方法体 | ❌ **失败** —— 方法体全为 `throw null`（30~193 处/类） |
| **数据结构/枚举取值** | ✅ **完整可得** |

**⇒ 能读"是什么"，读不到"怎么做"。逻辑只能靠运行时观测或反汇编机器码。**

## 六、工具链

| 工具 | 位置 | 用途 |
|---|---|---|
| **Cpp2IL** | `_tools\Cpp2IL\Cpp2IL.exe` | 转储真实类型信息 |
| **转储产物** | `_dump\`（80 个 dll，6.3 MB） | 查类型/字段/枚举 |
| ilspycmd | `C:\Users\34294\.dotnet\tools\` | 反编译查看 |
| 旧反编译缓存 | `_decompiled\` | interop 存根（**已被 `_dump` 取代**） |

**重新转储**：

```powershell
& '<工作区>\_tools\Cpp2IL\Cpp2IL.exe' --game-path '<游戏根>' `
    --output-as dll_il_recovery --output-to '<工作区>\_dump' `
    --use-processor attributeanalyzer,nativemethoddetector
```

## 七、高危禁忌（血泪教训）

| 禁忌 | 后果 | 记录 |
|---|---|---|
| ❌ 把 `LockonTargetCharacter` 写成 **null** | **整个战斗系统崩溃**（敌人 AI / 锁定 / 战斗 UI 全消失） | `COMBAT_SYSTEM_MAP.md` 事故节 |
| ❌ 在 IL2CPP 下用 `ClassInjector` 注入托管 MonoBehaviour | 类型注册了但 `Start()` 从不被调用 | `ERRORS.md` |
| ❌ 凭语义猜字段含义就改 | `Move.y` 那次导致"无法移动" | 见 4.1 |
| ❌ 一次上线多个未验证改动 | 出问题无法定位 | 工作纪律 |

**原因**：IL2CPP 方法体是桩，**静态看不到谁在读某字段** → 核心状态字段默认视为**不可写**
（除非能验证所有依赖方）。

## 八、下一步待办

### ✅ 已完成（F9 / F10 实测取证）

| 事项 | 结果 |
|---|---|
| F9 技能表探查 | ✅ 已取证：`AttackList` 3 / `ShotList` 2 / `DashList` 3 / `AssistList` 3 |
| F10 相机探查 | ✅ 已取证：9 个 vcam + 4 个 TargetGroup 的真实参数 |
| 矫正"3 变体 = 3 形态"推断 | ✅ 已否定并更正（见 `COMBAT_DATA_ATLAS.md` 第二·补） |
| 矫正"`CrossRange` = 射程" | ✅ 已标注为"语义未确认"（同上，第三节） |
| 矫正"缩放上限太小" | ✅ 已否定：实测上限 **5000**，根因是瞄准点选中点 |

### 相机：Z 轴问题**已解决**（实测有效）

**根因**（实测）：原版**从未锁定敌人** —— 主力相机 `LockonLong`（prio=100，激活 34 次）
与 `DashReady` 的 `lookAt` 是 **`AIdealBattle`（玩家自己）**；
四个单锁相机的 `lookAt` 是**两人包围盒中心**（垂直分量 = 两人高度平均）。

**为什么 XY 看起来锁住了、Z 没有**：水平方向两人贴身时中点≈敌人位置；
垂直方向的中点是高度平均 → 玩家一飞高，瞄准点就抬到两人之间 ⇒ 对着敌人头顶的空气。

**修法**：把六个锁定相机的 `LookAt` 指向**敌人本体**（`Follow` 不动），
使 Aim 阶段把敌人对齐到屏幕 ⇒ 三轴跟随。
**固定启用于 FreeFlight 模式**；Vanilla 模式与无锁定目标时自动还原。

**❌ 失败的尝试（已删除，留档避免重犯）**：
用"目标组 `posMode=GroupAverage` + 玩家权重 0.2"想把瞄准点拉向敌人 —— **实测无效**，
因为真正生效的 `LockonLong`/`DashReady` 根本不读目标组。
**教训：先确认"哪个对象在生效"，再决定改哪里。**

详见 `CAMERA_STRUCTURE.md` 第三·补 与 第四节。


### 待办

| 优先级 | 事项 | 依据 |
|---|---|---|
| 1 | **取证"当前生效的是哪一套 AttackSelection"** —— 读 `CharacterControllerBase.CurrentAttackParam` 并与各套 `Key` 对照 | `COMBAT_DATA_ATLAS.md` 第八节 |
| 2 | 移动：查 `TargetMoveController` / `Turn_Move` / `OrbitMovement` | `MOVEMENT_STRUCTURE.md` 第六节 |
| 3 | UI：找左上角"距离提示"文本组件 | `UI_AND_AI_STRUCTURE.md` 待查项 |
| 4 | 实现：右键切刀/枪 | 依赖第 1 项 |
| 5 | 自由相机的已知小 bug（用户说"后面修"） | `FreeCamEnabled=false` 中，暂不影响 |


## 九、工作纪律（用户明确要求）

1. **先反编译看明白再写计划，最后执行** —— 不要凭记忆写
2. **一次只改一处**，验证通过再改下一处
3. **不写死**：路径/端口/键名/阈值外置到配置
4. **不魔法数**：≥3 位数字、超时、阈值 → 具名常量 + 注明理由
5. **边界显式**：该抛异常抛、该记日志记，禁止静默吞掉
6. **收尾验证**：跑一次能证明工作的命令，如实报告（失败就说失败）
7. **改动最小且集中**：不顺手重构

## 十、相关会话

| 会话 | 用途 |
|---|---|
| 银龙奥卡开发 `session-441b78c5-2e9b-409f-8093-2800ceddbf40` | 长期 mod 迭代经验（已提供 7 条经验，见 `NOTES.md` 或信箱记录） |
| AI信箱维护者 `session-4f42d732-3f48-414c-8e72-a440303855a5` | 信箱本身的问题 |

**本会话 id**：`session-f50f8e08-d988-4209-a053-413a3274b9f8`（标题"反编译老游戏做私服mod"）
