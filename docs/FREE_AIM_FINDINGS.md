# 第三人称脱离准心 · 实测问题与对策（F3 模式）

> 记录 F3 模式实现过程中的实测问题、根因、对策与结论。
> 与 `DESIGN_FREE_AIM.md` 配套阅读：那份是**设计**，这份是**实测与修正**。

---

## 零、★ 用户定稿的锚点优先级设计（核心）

> 用户原话：
> 「中键锁定敌人后，优先级会盖过爱迪尔。你可以理解为一个 **while 里面的 if 中键锁定**，
>   而**爱迪尔锚点是 else**。这个也简单，就是**判断当前锚点位置与哪个敌人最近**，
>   然后将锚点位置锁定为敌人而不是爱迪尔。**这样的话也不会因为旋转而出问题了，
>   因为旋转是爱迪尔锚点那里才会容许的**」

```csharp
// 每帧（伪代码，对应用户的 while 语义）
if (中键按住) {
    // if 分支：锚点锁定敌人
    var enemy = 找【离当前锚点位置最近】的敌人;   // ★ 注意：不是离爱迪尔最近
    锚点位置 = enemy.position;                    // 脱离爱迪尔，不参与旋转
} else {
    // else 分支：锚点绕爱迪尔轨道
    锚点位置 = 爱迪尔位置 + 方向(鼠标) × 距离;    // ★ 旋转只在这里允许
}
```

### 这个设计的两个优点（用户指出，实现上已保证）

| 优点 | 实现保证 |
|---|---|
| **锁定时不受旋转影响** | 锁定时 `Tick()` **完全跳过** `Quaternion.Euler(pitch, yaw)` 方向合成 |
| **"最近的敌人"以锚点为准** | 用 `Vector3.Distance(from: 锚点位置, to: 敌人)` 比较，而不是到爱迪尔的距离 |

**第二条是用户的修正**：我最初写成"离爱迪尔最近的敌人"，
用户指出应为"判断当前锚点位置与哪个敌人最近" ——
因为**锚点才是瞄准意向的载体**，玩家把锚点转过去就表示想瞄那个方向。

### 配套操作（用户定稿）

| 操作 | 效果 | 目的 |
|---|---|---|
| **滚轮** | 调锚点距离（配置范围 4~120，每格 2 米） | 解决中/近切换 |
| **长按中键** | 锚点吸附到最近敌人坐标并跟死 | 解决远程瞄准 / 索敌 |
| **松开中键** | 回到绕爱迪尔轨道 | 恢复自由瞄准 |

### ⚠️ 制导弹药与锚点距离（**结论已反转，勿照旧版实现**）

> 用户最初的建议：「把远程的锚点**拉远**，**同步转角**」

**这个建议已实测作废** —— 详见 §2.2。原因：

```
不锁定时  锚点 = 爱迪尔 + 方向 × 距离      ← 锚点与发射者同步平移
弹药制导朝锚点                              ← 锚点以同样速度"逃走"
⇒ 永远追不上 ⇒ 绕圈。拉多远都一样（速度同步，与距离无关）。
```

**正确结论：锚点要贴近爱迪尔**（锚点 ≈ 爱迪尔位置 ⇒ 弹药"已到达"制导目标 ⇒
制导立即结束 ⇒ 沿瞄准方向直线飞出）。当前三档 = `1.5 / 6 / 14` 米。

**⇒ 距离越近，弹药越不会转圈；距离越远，越容易转圈。与直觉相反。**

---

## 一、已解决

### 1.1 ✅ 锚点绕爱迪尔头顶乱转

| 项 | 内容 |
|---|---|
| **现象** | 锚点在角色头顶转圈，不跟鼠标（用户实测） |
| **日志证据** | `dir` 每 2 秒跳一次：`(-0.32,0.32,0.89) → (-0.61,0.62,0.50) → (-0.83,0.55,0.01)`；`dy` 在 6~7 摆动 |
| **根因** | 锚点读的是 `Camera.main.transform.forward`，形成**闭环**：<br>`相机朝向 → 锚点 → 锁定目标 → 身体朝向 → 相机朝向` |
| **对策** | 锚点改为**独立累积鼠标角度**（`Input.mousePosition` 帧间差分，与 `FreeFlightCamera` 同源），成为链条唯一源头：<br>`鼠标 → 锚点 → 锁定目标 → 身体/相机`（单向下游） |
| **验证** | ✅ 鼠标不动时 `dir=(-0.71,-0.71,-0.01)` 连续 10 次完全一致；鼠标动才变 |

**教训**：一旦某个值进入反馈环，就不能再从环内读它作为输入 —— 必须找到环外的源头。

### 1.2 ✅ 锁定目标没有指向锚点

| 项 | 内容 |
|---|---|
| **现象** | 用户："锁定还是在敌人身上" |
| **根因** | 早期版本还没实现覆盖（用户测的是旧 dll）；且游戏有 `TargetListRecalc` / `LockonRecalc` / `AutoLockonScanExec` 持续重算 |
| **对策** | 每帧检测 + 强制重写 + 统计"被抢回"次数（客观证据） |
| **验证** | ✅ 日志：`override ON: lock target = anchor`，`steal count=1` 且不再增长 |

---

## 二、待解决

### 2.1 ✅ 已解决：锁定渲染框不跟随锚点 → **`LockonDispOpen` 就是正门**

| 项 | 内容 |
|---|---|
| **现象** | 用户：「锁定的那个渲染框没有渲染到锚点，而是渲染到敌人那里去了」 |
| **关键认知** | **只写 `LockonTargetCharacter` 字段，游戏不会重新绑定渲染** —— 这是"每帧判定也没成功夺回"的真正原因 |
| **正门 API** | `BattleManager.GetInstance()`（L1046）<br>`BattleManager.LockonDispOpen(CharacterControllerBase target)`（L1208）<br>`BattleManager.LockonDispClose()`（L1298） |
| **修法** | 先写字段（供朝向/攻击读取），再调 `LockonDispOpen(anchor)` 强制重绑渲染 |
| **验证** | ✅ 探针输出 `LockonMarker ... parent=AidealRays_AimAnchor`<br>（此前是 `parent=HPGauge` / `parent=Spine`） |

### 2.1b ✅ 已修正：夺回时机（用户定稿）

> 用户原话：「那这个没意义了阿，不还是每帧渲染吗？**你开启F3后去自动判定阿，
>   以及长按中键结束后再判定一次阿，就2种情况可能出现被夺的情况**」

⇒ 夺回改为**事件驱动**，只在两处调用 `ReclaimLockTarget(reason)`：
1. `SwitchMode` 的 `ThirdPerson` 分支 → `reason="enter F3"`
2. `UpdateAnchorTargeting` 的 `LOCK OFF` 分支 → `reason="lock released"`

**已删除** `onPreCull` 里的每帧夺回。

### 2.2 ✅ 已修正：远程制导弹药围着爱迪尔打转 → **锚点要拉近，不是拉远**

| 项 | 内容 |
|---|---|
| **现象** | 用户：「远距离的能量弹还是在转圈圈」；追问确认：**「只在不锁定的时候转圈」「弹药刚飞出去就绕圈」** |
| **机制（反编译）** | `ShotController`：`RotateMin/Max`（转向能力）、`HomingTimeMin/Max`（制导时长）、`HomingDisabled`、`Target`（制导目标） |
| **根因** | 不锁定时 `锚点 = 爱迪尔 + 方向 × 距离` ⇒ **锚点与发射者同步平移**<br>⇒ 弹药朝锚点制导，但锚点永远在以同样的速度"逃走"<br>⇒ **永远追不上 ⇒ 绕圈**。<br>⚠️ **拉多远都一样** —— 因为锚点的【速度】与弹药要追的方向同步。 |
| **正确修法** | **把锚点拉到爱迪尔身上**（不是拉远）：锚点 ≈ 爱迪尔位置 ⇒ 弹药"已到达"制导目标 ⇒ 制导立即结束 ⇒ 沿初始方向**直线飞出**。瞄准方向仍由身体朝向决定，而身体朝锚点 ⇒ 方向仍受我们控制。 |
| **参数修正** | `TierNearDistance` 8 → **1.5**；`TierMidDistance` 16 → **6**；`TierFarDistance` 32 → **14** |

> ⚠️ **这与 §零 里"远程必须拉远"的早期结论相反** —— 早期结论是在"锚点还不跟爱迪尔"的假设下得出的。
> 实测证明：只要锚点跟随爱迪尔移动，拉远就无法解决转圈。**"拉远"一条已作废。**

### 2.3 ✅ 已实现：视角太远 → `CinemachineTransposer.m_FollowOffset`

| 项 | 内容 |
|---|---|
| **现象** | 用户：「视角太远了，要离爱迪尔近一点」 |
| **关键（易踩坑）** | 激活的锁定相机是 `LockonLong` / `DashReady`，其组件为<br>`Composer=Y Transposer=Y FramingTransposer=N`<br>⇒ 距离来自 **`CinemachineTransposer.m_FollowOffset`（向量）**，<br>**不是** `FramingTransposer.m_CameraDistance`。<br>改 `camDist` 对这些相机**完全无效**。 |
| **实现** | `CameraDistanceTuner.cs`：只缩放 `m_FollowOffset` 的**长度**（方向不变，保持原生构图），<br>**按比例**（游戏自己调距离时我们跟随），只碰**当前激活**的相机，退出 F3 时还原。 |
| **参数** | `CameraDistanceScale = 0.65`（原距离的 65%） |

### 2.4 ✅ 已实现：休息姿态（长按 Ctrl）

> 用户原话：「长按 ctrl 键让爱迪尔进入冲刺状态。这个我记得没错应该有的，
>   是战斗中打赢每波敌人的休息姿态，不能攻击，视角锁定到爱迪尔身上」

**反编译确认（用户记忆正确）**：

```csharp
PlayerControlCustom
    public CinemachineVirtualCamera VCameraDashReady;   // L332 ★ 视角锁到爱迪尔
    public CinemachineVirtualCamera VCameraDash;        // L334
    protected CinemachineTargetGroup TargetGroupDash;   // L342
    public Animator DashHUD / DashCursol;               // L430 / L434
    protected override void DashReadyFirst();   // L756 ★ 进入
    protected override void DashReady();        // L761
    protected override void DashCancel();       // L796 ★ 退出
    protected void DashReadyCamera();           // L776
    public void ChangeDash(DASH key);           // L991
DashSelection
    public AnimationParam DashReadyParam;       // ★ 休息姿态动画
    public bool DashAvoid;                      // 回避（无敌帧）
```

**实现**：`DashReadyRest.cs` —— `AccessTools.Method` 反射调 `protected` 方法：
- 长按 `LeftControl` → `DashReadyFirst()` → 视角锁爱迪尔 + 不能攻击
- 松开 → `DashCancel()` → 退出
- 方法找不到时**明确报错并停用**（不每帧重试刷屏）

**验证**：✅ 启动日志 `[REST] rest pose ready: enter=PlayerControlCustom.DashReadyFirst, cancel=DashCancel`

### 2.5 ⏸ 按用户要求暂不处理

| 项 | 用户答复 |
|---|---|
| 形态在近/中距离高频震荡（`Ranged ⇄ Melee ⇄ Unknown`） | 用户选「不管了」（我的探针是纯只读的，不可能改变形态） |
| 受伤时概率卡死（无异常日志 ⇒ 逻辑死锁，怀疑"锚点锁定 vs 受伤转锁定"互抢） | 用户选「先放着」 |

### 2.6 ✅ 已解决：虚拟光标 —— **不是 UI 组件，是原版的"鼠标一动呼出光标"**

| 项 | 内容 |
|---|---|
| **用户澄清** | 「**原版游戏移动鼠标呼出光标!**」—— 这是**原版的既定行为**，不是我们引入的 bug |
| **为什么对我们的模式是灾难** | F3 要用鼠标瞄准 ⇒ 光标满屏飘 ⇒ 飘到 UI/敌人上误触（"自己打开菜单 / 切敌人信息"） |
| **用户给的修法** | 「没做基础的**全屏循环光标**」 |
| **反编译确认的宿主** | `GameManagerBase`：<br>`protected void CursorShow(bool show)`（L1169）<br>`protected void MoveCursorToBottomRight()`（L1164）<br>`private static extern bool SetCursorPos(int X, int Y)`（L636） |
| **实现** | Harmony prefix 拦掉这两个调用（**非原版模式 + 菜单未打开**时才拦）：<br>`Prefix_CursorShow` / `Prefix_MoveCursorToBottomRight`<br>放行条件：原版模式、暂停/设置/弹窗打开时 ⇒ 保证玩家能点菜单 |
| **为什么用"拦原版调用"而非自己硬锁** | 不让 `CursorShow(true)` 生效比抢着写 `Cursor.visible` 干净：<br>游戏仍能正常调 `CursorShow(false)`，只是"呼出"被拦 |

> ⚠️ 早期误判：我曾以为是 `Michsky.UI.Shift.VirtualCursor`（UI 包的虚拟光标），
> 探针实测该组件**在战斗场景里根本不存在**。真正原因是 `GameManagerBase` 的光标管理。

### 2.7 ✅ 已实现：冲刺（`shift/冲刺`）—— 入口是 `DashStart()`

| 项 | 内容 |
|---|---|
| **用户键位表（2026-10-16 定稿）** | 「右键/闪避，长按防御」<br>「左键/普攻，蓄力突进，抓取」<br>「**shift/冲刺**」<br>「**ctrl/下降**」<br>「**space/上升**」 |
| **冲刺三特征（用户给的定位依据）** | 「以爱迪尔为中心」「无法攻击」「失去锁定」 |
| **关键线索（用户截图）** | 进入该状态时屏幕显示 **`OUT RANGE`** |
| **反编译确认** | `BattleManager.GetRangeStr(float distance)` / `MonitorRange()` 协程<br>驱动 `RangeInfo1/RangeInfo2` + `RangeInfoTxt1/RangeInfoTxt2` 提示<br>⇒ `OUT RANGE` = 超出射程，**三条特征都是"超射程"的直接后果**<br>（打不了 / 锁不了 / 相机回角色） |
| **入口怎么定下来的** | 探针实测：用户按 Shift 时 `[DASH] DashStart observed (count=1)`<br>⇒ `PlayerControlCustom.DashStart()` 就是冲刺入口<br>（此前零命中只是因为**还没按过**，不是"不走这条路"） |
| **实现** | `UpdateDash()`：`GetKeyDown(DashKey)` → 反射调 `DashStart()`<br>默认 `DashKey = LeftShift`，可配置 |

> ⚠️ **与早期那次失败实现的关键区别**（那次把左键长按蓄力 `DashReady` 误当成冲刺）：
> 1. 上次**搞错对象**；这次由探针实测命中确认
> 2. 上次还**拦截了 `DashCancel`**（会破坏游戏正常的蓄力取消逻辑）——
>    **本次绝不拦截任何东西**，只在按键时调用游戏自己的入口，状态机怎么走我们就怎么走

---

## 附：已废弃的早期结论（避免后人被误导）

| 早期结论 | 现状 | 原因 |
|---|---|---|
| 「远程必须把锚点**拉远**」 | ❌ **作废** | 锚点跟随角色平移时，拉远无法解决制导弹药转圈；正确做法是**拉近**（见 2.2） |
| 「每帧判定夺回」（含 `onPreCull` 版本） | ❌ **作废** | 用户定稿：只有两个事件可能被夺（见 2.1b） |
| 「直接搬 `LockonMarker` 到锚点」 | ❌ **作废** | 它挂在敌人 `HPGauge` 下，写 `.position` 会被父物体拉回；正确做法是调 `LockonDispOpen`（见 2.1） |
| 「改 `FramingTransposer.m_CameraDistance` 拉近视角」 | ❌ **无效** | 激活相机用 `CinemachineTransposer`（见 2.3） |
| 「虚拟光标 = `Michsky.UI.Shift.VirtualCursor`」 | ❌ **错** | 该组件在战斗场景不存在；真身是 `GameManagerBase.CursorShow`（见 2.6） |
| 「`DashReady`/`DashReadyFirst` = 冲刺」 | ❌ **错** | 那是**左键长按蓄力**；冲刺入口是 `DashStart`（见 2.7） |
| 「`Flow`/`Condition` 是 `ActionFlowManager` 的字段」 | ❌ **错** | 它们属于**嵌套类** `ActionFlowManager.ActionFlowContainer`（interop 存根定论） |

---

## 三、当前参数（**以此表为准**）

### 键位（全部外置到配置，代码只引用符号）

**用户键位表原文（2026-10-16 定稿，以此为准）**：

```
右键 / 闪避，长按防御
左键 / 普攻，蓄力突进，抓取
shift / 冲刺
ctrl  / 下降
space / 上升
```

| 功能 | 键 | 配置项 | 归属 |
|---|---|---|---|
| **冲刺** | **Shift** | `DashKey` | 我们反射调 `DashStart()` |
| **下降** | **Ctrl** | `DescendKey` | 我们的垂直飞行 |
| **上升** | **Space** | `AscendKey` | 我们的垂直飞行 |
| 闪避 / 长按防御 | 右键 | — | **游戏原生**（`Avoid` / `Avoid Charge`），我们不占用 |
| 普攻 / 蓄力突进 / 抓取 | 左键 | — | **游戏原生**（`Fire` / `Fire Charge`），我们不占用 |
| 原版 / 飞行 / 第三人称 | F1 / F2 / F3 | `ModeKey*` | 我们 |
| 自由视角（按住） | 左Alt | `FreeLookHoldKey` | 我们 |
| 锁定最近敌人（长按） | 中键 | `LockAimKey` | 我们 |
| 三档距离：更远 / 更近 | Q / E | `TierFarKey` / `TierNearKey` | 我们 |
| 技能表 / 相机探针 | F9 / F10 | `SelectionProbeKey` / `CameraProbeKey` | 我们 |

| 休息姿态（长按） | 左Ctrl | `RestPoseKey` |
| 技能表 / 相机探针 | F9 / F10 | `SelectionProbeKey` / `CameraProbeKey` |

### 数值

| 配置项 | 当前值 | 依据 |
|---|---|---|
| `TierNearDistance` | **1.5** 米 | 贴近角色 ⇒ 制导弹药不绕圈（见 §2.2） |
| `TierMidDistance` | **6** 米 | 同上 |
| `TierFarDistance` | **14** 米 | 同上 |
| `CameraDistanceScale` | **0.65** | 视角拉近（见 §2.3） |
| `LockTargetOverride` | true | 是否接管游戏的锁定目标字段（保险开关） |
| `SuppressVirtualCursor` | true | 抑制 UI 包虚拟光标（战斗场景里未找到该组件，见 §2.6） |
| `MoveLockMarker` | false | 直接搬标记的 hack，已关闭（改用 `LockonDispOpen`） |
| `AnchorLerpSpeed` | 8 | 轻度平滑 |
| `AnchorPitchLimit` | 60 | 抄 `CameraHolder.yMinLimit/yMaxLimit` |
| 鼠标灵敏度 | 复用 `FreeCamMouseSensitivity` = 0.15 | 与自由相机同源，保证手感一致 |

> **已废弃的配置项**（代码里已移除，旧配置文件里残留也无害）：
> `AnchorDistance` / `AnchorDistanceRanged`（被三档取代）、
> `AnchorFarKey` / `AnchorNearKey`（R/F，被 Q/E 取代）、
> `AnchorScrollStep`（滚轮被游戏占用）、
> `FreeCamToggleKey`（被 F1/F2/F3 取代）、
> `FacingProbeKey` / `AnchorInstanceProbeKey`（探针已整合进 F3）

---

## 四、索敌实现（用的是游戏现成接口）

```
PlayerControlCustom.GetLockonEnemys() : List<CharacterControllerBase>   L1249  ← 主用
PlayerControlCustom.SemiLockonTargetList : List<CharacterControllerBase> L286   ← 兜底
```

**为什么用 `GetLockonEnemys()`**：它是游戏判断"谁可锁定"的**权威来源**，
天然排除不可锁定对象（场景装饰、已死亡单位）。不自己遍历场景。

**候选范围**：`TARGET_SEARCH_RADIUS = 100` 米（具名常量，理由见代码注释）。
超半径的敌人不算"最近敌人"，避免锁到地图另一端。

---

## 五、形态判定（已实测双向通过）

```
枪：currentAttackParam='ShotParam'(id=-49742)            → rangedSlot=[0]EXS_SHOT.Shot
刀：currentAttackParam='SingleAttackParam3'(id=-48250)   → meleeSlot=[0]EXS_BLADE.Single3
```

**方法**：`GetCurrentAttackParam()` 的返回值去 `AttackList`（10 槽位/套）
与 `ShotList`（1 槽位/套）做**引用比较**（`AttackParam` 是 MonoBehaviour）。
**不依赖任何数值推断**（`CrossRange` 语义未确认，基于它会重蹈"凭推断改坏游戏"的覆辙）。

**用途**：
- 形态切换时**自动重置锚点距离**（切枪用 30，切刀用 12），之后滚轮可调
- 未来可用于"锚点距离驱动近战形态"（`ChangeAttack`，见 `DESIGN_FREE_AIM.md` §3.3）
