# 驱动妖精 3D 空战 mod · 主线台账

> 建立时间：2026-10-16（新任会话接手）
> 约束来源：`C:\Users\34294\Desktop\work\AI会话工作约束.md`（13 条硬约束）
> 交接来源：`C:\Users\34294\Desktop\work\驱动妖精mod-交接说明.md`
> 本文件是**唯一的进度真相**。每轮回复开头必须回填状态（约束一·2）。
> 状态取值只能是：`未开始` / `进行中` / `已验证` / `被阻塞`。
> 「已验证」的定义：有**机器可复现的证据**（构建哈希 / 探针日志 / 截图以外的客观数据）。
> 用户主观手感（"像不像疾飞"）只能标 `待用户验收`，不能标 `已验证`。

## 时间线补记（2026-10-24）

- **2026-10-16 04:10 的自动实机取证跑批没有产出**：`_logs\d1-20261016-041043\` 只有一个空 `shots\`
  目录，没有 report.txt。前一轮把 `auto-d1-run.ps1` 作为后台任务启动后，会话在跑批中途中断
  （后台任务随会话一起没了，`job_list` 现已为空）⇒ **探针 v0.2.0 从未真正跑过**。
  D1 的下一步（有移动输入条件下的 A/B 对照）仍未完成，不是"已失败"，是"没跑成"。
- **源码已交付作品集合集**：`C:\Users\34294\Desktop\work\作品\其他游戏模组源码\drive-fairy\`
  （51 个文件 / 788K，只含源码 + 文档 + 工具，无 bin/obj/成品 dll；已在副本内实跑构建验证 0 错误）。
  回执已投给「维护作品集」（m0096）。
  **2026-10-24 21:42 路径变更**：用户裁定「MC 合集里不混入非 MC 项目」，该目录已从
  `MC开发用的各种妙妙小工具\mods\drive-fairy` 迁到 `作品\其他游戏模组源码\drive-fairy`，
  文件原样未改（已复核：51 文件 / 788K，旧路径已不存在）。

---

## 〇、工程事实（接手时实测，非转述）

| 项 | 实测值 | 取证方式 |
|---|---|---|
| 游戏根 | `D:\BaiduNetdiskDownload\AldealRaysR18` | `Get-ChildItem` |
| 工程根 | `D:\AI\AI工具箱\game\aideal_rays` | 同上 |
| 插件源码 | `plugin\Flight\`（14 个 .cs，主文件 `FlightPlugin.cs` 3938 行） | `Get-ChildItem` |
| 部署产物 | `BepInEx\plugins\AidealRays.Flight.dll`（144384 字节） | `Get-FileHash` |
| 构建产物 | `plugin\Flight\bin\Release\net6.0\AidealRays.Flight.dll`（144384 字节） | 同上 |
| **两者 SHA256** | **完全一致** `89778151…DED6BE9` | `Get-FileHash -Algorithm SHA256` ×2 |
| 最后一次构建 | 2026-10-16 03:46:53 | 文件时间戳 |
| 最后一次运行游戏 | 2026-10-16 03:46:58（只到 `Chainloader startup complete`） | `BepInEx\LogOutput.log` |
| 游戏当前是否在运行 | **否** | `Get-Process AldealRaysR18` |
| 版本控制 | **无**（`D:\AI\AI工具箱` 不是 git 仓库） | `git status` → fatal: not a git repository |

**⇒ 结论（可用作基线）**：当前部署的 dll **确实**包含 `FlightPlugin.cs` 3:46 那版源码，
不存在"源码改了没编译"的偏差。后续每次改动都必须重新做这个哈希比对。

---

## 一、目标与验收标准（用户原话为准）

> 用户第 9 轮：「我一直想把驱动妖精从 **2.5D 变成真正的 3D 空战**」
> 用户第 14 轮：「**不急，先把玩家操控角色进行 3 维，AI 其实还好的**」

| 编号 | 需求 | 验收标准（什么现象算通过） | 状态 |
|---|---|---|---|
| **G1** | 战斗场景彻底三维；H 场景保持原版自由摄像机 | 战斗可用 3 维位移（含垂直）；H 场景未被改动 | 进行中 |
| **G2** | 相机绑定第三人称 + 玩家 3 维操控 | 空格上升 / ctrl 下降；WASD 相对相机 | 待用户验收 |
| **G3** | F3 模式：脱离准心（锚点）+ 中键锁定最近敌人优先 + Q/E 三档距离 + 左Alt 自由视角 | 按第 151 轮键位表逐条可操作 | 未开始 |

### 用户最终确认的键位表（第 151 轮，**以此为准**）

```
F2 模式：右键/闪避，长按防御 ｜ 左键/普攻，蓄力突进，抓取 ｜ shift/冲刺 ｜ ctrl/下降 ｜ space/上升
F3 模式：第三人称脱离准心（锚点 = 可控准心，锁定目标指向锚点）
         中键 = 锁定最近敌人（优先级高于锚点）｜ Q/E = 锚点三档距离（远/中/近）｜ 左Alt = 自由视角
```

⚠️ **已发现的口径冲突（必须处理）**：`FlightPlugin.cs:1403` 的注释写「用户裁定：下降改到 **C** 键」，
但实际默认值是 `LeftControl`，游戏日志也打印 `descend=LeftControl`。
⇒ 注释与实现不一致，属**文档缺陷**（T-D1）。以用户第 151 轮键位表 `ctrl/下降` 为准。

---

## 二、用户已验收通过的功能（可放心复用，来自交接说明）

| 功能 | 出处 | 用户原话 |
|---|---|---|
| 起飞与下降 | 第 31 轮 | 「完美!成功起飞与下降」 |
| 幻影敌人 + 锁定联动方案 | 第 86 轮 | 「OK，成功」 |
| 相机跟随与锁定（含 Z 轴） | 第 112 轮 | 「完美！非常的好」 |
| 锚点 + 渲染框 + 中键锁定 | 第 121 轮 | 「看吧，这次成功了，但是有点小问题」 |

---

## 三、未通过缺陷清单（接手第一优先）

> ⚠️ 状态栏里的「前任说法」未经我复核，一律标 `待复核`。

### D1 · 疾飞（Shift）姿态与速度 —— **未通过**

- **验收标准（用户原话，第 159/160/161/163 轮）**：
  - 保持 W 的姿势；**ASD 实际是 W 旋转**（不是"转身向后飞"）
  - **移速必须变化**（加力要真的更快）
- **用户原话证据**（从原始会话日志 `session-f50f8e08…jsonl.zstd` 逐帧解压提取）：
  - #498：「按住 shift 后，动画特效出现了，但是还是有点问题——因为在这个状态下，
    原版按 S 不是面对敌人后退而是**直接转身向后飞**，而且**移速没有变化**」
  - #502：「而且不止 S，**AD 都是 W 向前的样子 + 喷气的特效，不断转身**」
  - #504：「喷气特效存在！**是动作不对**！」
- **状态**：`未开始`（先取证，证据不足不动手）
- **待回答**：
  - Q1 `ForceBooston()` 是否真的改变移动速度？（决定"移速没变化"是**我们的 bug** 还是**游戏设计**）
  - Q2 加力期间"朝向"由谁决定？（决定"转身向后飞"能不能在不碰游戏核心状态的前提下改）
- **✅ 已完成的取证准备（接手后）**：
  - `BOOST_STATUS` 枚举**完整可得**（4 个值）：
    ```
    IDLE_NONBOOST, FORWARD_BOOST, LEFT_BOOST, RIGHT_BOOST
    ```
    ⇒ 加力是**三向**的（前/左/右），不是单一"前进加力"。
    ⇒ 前任探针"恒为 IDLE_NONBOOST"意味着**我们点了火但游戏没进加力态**，
    与用户"移速没有变化"的反馈**互相吻合**（这条是①已取证的事实链，不是猜测）。
  - `ForceBooston` 的 **IL2CPP token = 100665153**（`_decompiled\PlayerControlCustom.cs:4825`），
    `ForceBoostoff` = 100665154，`BoostonCommon` = 100665155，`BoostoffCommon` = 100665156。
    interop 侧实现只是 `il2cpp_runtime_invoke(token)` 转发 ⇒ **托管侧无逻辑可读**。
  - 静态路线**已试尽**：`ilspycmd --ilcode` 对 `_dump\Assembly-CSharp.dll` 也不出方法体
    （方法体已被 Cpp2IL 替换为 `throw null` 桩），故**静态无法回答 Q1/Q2**。
- **⇒ 结论：Q1/Q2 只能走运行期探针**（本机可自测，不需用户按键）。

- **★★★ 运行期实测结果（2026-10-16 04:05–04:07，自动探针 BoostProbe 第一次实机）★★★**

  取证条件：探针在 `PlayerControlCustom.Update` 后自动点火（**当时无移动输入**），
  持续 600 帧后自动关火；同一场战斗里用户自己按了 W/Shift（对照）。

  **① 我自己的判断错误（先认）**：探针第二次尝试部署时被占用，实机跑的是 v0.1.0；
  我在中途一度写下"`ForceBooston()` 是空操作、加力在战斗里物理上不可能"——
  **这个结论被同一份日志里的反证推翻，已作废**。当时的对照条件是 `Move=(0,0,0)`（没有移动输入），
  不能代表"有输入"的情形。这正是《约束》五·10 说的"不许把推理写成已取证"。

  **② 已取证（一条命令可复现，日志 `BepInEx\LogOutput.log`）**：

  | 观测 | 数据 | 出处 |
  |---|---|---|
  | 探针自动点火（无输入）后 | `status=IDLE_NONBOOST`，**125/125 次采样全是它**，`FORWARD/LEFT/RIGHT_BOOST` 出现 **0 次** | `[BPROBE][...] status=` |
  | 用户自己按住移动+Shift 后 | **`status=RIGHT_BOOST`**，`boostPoint 3/7→7/7` 在燃烧/回充 | `[BOOSTSPD] status=RIGHT_BOOST` ×12 |
  | 加力期间速度倍率 | **`assistSpeedMagni` 恒为 4，加力前后都没变** | 同上 |
  | 点火前后位置 | `dPos=(0,0,0)`（当时无输入，无位移） | `[BPROBE][DISP]` |
  | 角色朝向 vs 输入方向 | `inputVsFacing=` **63.157° / 146.881°** | `[TRACE]` |
  | 输入方向 vs 实际位移方向 | `inputDir=(-26.163,-44.163)`，`dispDir=(-0.241,-0.971)`，`dispLen=6.498` | `[TRACE]` |

  **③ 由此确立的两条事实链（这才是 D1 的真正入口）**：
  - **事实 A**：游戏的加力状态机**确实能被激活**（`RIGHT_BOOST`），
    但**它是在"有移动输入"时才进入的** ⇒ 无输入时调 `ForceBooston()` 看不出效果，
    不能据以判定该方法无效。**待做**：在有移动输入的条件下做同条件对照（探针已改成"等有输入再点火"）。
  - **事实 B**：`assistSpeedMagni` 在加力期间**不变（恒 4）** ⇒
    用户说的「移速没有变化」**有直接数值证据**，"加力改变位移速度"这件事
    **不是靠这个字段实现的**（或该字段与本作的加速无关）。**待定位**：真正承担加速的字段/通路。
  - **事实 C**：`inputVsFacing` 高达 63°/147°，且 `dispDir` 与 `inputDir` 方向明显不一致 ⇒
    **"朝向"与"实际位移方向"都不是由我们的输入直接决定的** ——
    这正对应用户说的「AD 都是 W 向前的样子 + 喷气特效，不断转身」。
    **待做**：在"有输入 + 加力"条件下采一整段 TRACE，做 boost / 非 boost 的成对对照。

  **④ 已定位的工具缺陷（我自己探针的 bug，已修、待重部署）**：
  `AccessTools.Field(typeof(PlayerControlCustom),"BoostStatus")` 找不到字段
  （它是 **protected**，`AccessTools.Field` 只按 public 找），
  导致 `ReflectionString` 读到的是 `?`（日志里 `magniLogic=?`）。
  ⇒ 那条"`BoostStatus` 是 `?`"的读数**不可用**，已被 `[BPROBE]` 直接属性读取取代。
  探针 v0.2.0 已改：沿继承链 `GetField(NonPublic)`、`GetComponentsInChildren`、目标帧率兜底、
  "等有移动输入再点火"、加力窗头 60 帧每帧点火做 A/B 对照。
  **但它需要重启游戏才能加载** —— 用户正在实机试玩，**重启时机交给用户**（不擅自打断）。


### D2 · 喷气特效每帧叠加 —— **前任声称已修，我复核为"待实机验证"**

- **验收标准**：一次按住期间 `ForceBooston()` 调用次数 **恰好 = 1**。
- **前任给出的量化证据**（会话日志统计）：一次按住期间调用 **35 / 55 / 62 / 78 / 143 / 323** 次。
- **前任的修法**（`FlightPlugin.cs:1179-1206`，已随 3:46 构建部署）：
  ```csharp
  if (held && !_boostActive)       { player.ForceBooston();  _boostActive = true;  }
  else if (!held && _boostActive)  { player.ForceBoostoff(); _boostActive = false; }
  ```
- **不变量证明（我复核源码后的结论：成立）**：
  1. `UpdateDash` 是 `ForceBooston()` 的**唯一调用点**（已 grep 全工程：仅 `FlightPlugin.cs:1199` 一处）。
  2. `_boostActive` 是 `static bool`，初值 `false`（C# 静态字段默认值），只在上面两个分支里被赋值。
  3. 进入第一个分支的条件是 `held && !_boostActive`；分支内立刻置 `_boostActive = true`。
  4. ⇒ 第二次及以后同一次按住期间，`!_boostActive` 恒为 false ⇒ **物理上不可能再点火**。
  5. `held` 为假且 `_boostActive` 为真时关火并复位 ⇒ 下一次按住可重新点火（且仅一次）。
  - ⚠️ **唯一缺口**：`catch` 块会把 `_boostActive` 置 false（`FlightPlugin.cs:1224`）。
    若 `ProbeBoostSpeed` 抛异常，会出现"开→复位→下帧再开"的**每帧重试**路径，
    但此时 `_dashUnavailable` 已置 true，`UpdateDash` 会在入口直接 return ⇒ 不会重试。
    ⇒ 缺口已被 `_dashUnavailable` 关闭。**不变量成立。**
- **状态**：`进行中`（代码证明成立；**运行期实测尚未做** —— 现有 `LogOutput.log` 只有 58 行、
  `[BOOST]` 只有启动横幅 1 条，说明部署后**从未真正按住过 shift**）
- **下一步**：自动探针实测计数（不劳烦用户按键）

### D3 · 休息姿态冲刺（三大特征）—— **未开始**

- **验收标准（用户第 154 轮定义，三大特征缺一不可）**：
  **① 以爱迪尔为中心 ② 无法攻击 ③ 失去锁定**
- **触发场景**：**打赢每波敌人后的休息姿态**（第 140 轮明确纠正：不是左键长按那个姿态）
- **用户来回 12+ 轮未达标** ⇒ 属于高风险项，必须先取证姿态枚举再做
- **✅ 已取证（接手后第一份新证据）**：`ACTION_FLOW` 枚举**没有**"休息/躲避"这类姿态名，
  全部取值只有 17 个（ilspycmd `_dump\Assembly-CSharp.dll -t ACTION_FLOW`）：
  ```
  READY_ENEMYS, TIMELINE, ACTION, LOADING, UNLOADING, CONVERSATION, NEXT_SCENE,
  BGM_START, BGM_STOP, WAIT, COMMAND, BGM_CHANGE, TIMELINE_ASYNC, DELETE_ENEMY,
  READY_ENEMYS_LOOP, GAME_END, DRAIN_ROOM
  ```
  ⇒ **"休息姿态"不是 `ACTION_FLOW` 的一个值**。
  **待验证的候选**：`READY_ENEMYS` / `READY_ENEMYS_LOOP`（"每波敌人之间"的时间窗，
  名字直译 = "准备敌人"⇒ 打完一波、下一波未出的间隙）。
  ⇒ 下一步取证：在 `ActionFlowManager` 的流程切换点打探针，看打赢一波后
  当前 flow 是不是 `READY_ENEMYS_LOOP`，**不要凭名字猜**。
- **状态**：`进行中`（枚举已取证，姿态归属待运行期验证）

### D4 · 虚拟光标（原版移动鼠标呼出光标）—— **进行中**

- **用户自己定位的根因**（第 144 轮）：「**原版游戏移动鼠标呼出光标**」
- **✅ 已取证（接手后）**：全程序集里带 "Cursor" 的类型**只有两个**
  （`ilspycmd _dump\Assembly-CSharp.dll -l c | Select-String Cursor`）：
  ```
  Class Michsky.UI.Shift.VirtualCursor
  Class Michsky.UI.Shift.VirtualCursorAnimate
  ```
  且 `GameManagerBase` 只 `using Michsky.UI.ModernUIPack`（**不是** `.Shift`）。
  `VirtualCursor` 是**手柄虚拟光标**（字段 `horizontalAxis`/`verticalAxis`/`speed`/`pointer`），
  由摇杆驱动，**不是**鼠标闪念出来的那个光标。
- **⚠️ 由此得出的判断（属②推理，不是①）**：`VirtualCursorSuppressor` 打的是**手柄光标**，
  很可能**打错了目标**；用户看到的"移动鼠标就出现"的光标更可能是
  `GameManagerBase.CursorShow(true)` + `MoveCursorToBottomRight()` 这条链（前任已拦），
  或 `Cursor.lockState` 未锁（前任的 `UpdateCursorLock` 已处理）。
  ⇒ **要判定谁在生效，必须运行期取证**（对象是否存在 / `Cursor.visible` / `lockState`）。
- **状态**：`进行中`（静态已排除一个错误目标，运行期判定待做）

### D5 · 加载慢 / 卡加载界面 —— **已定性，非 bug**

- **用户自己实测出真相**（第 54 轮）：**切屏会让游戏自冻结** ⇒ 不是死锁、不是插件问题
- 前任据此让用户反复重启了好几轮，并自认「我把'加载慢'当成了'死锁'…这是我的判断错误」
- **状态**：`已验证`（性质已定：环境行为，无代码可修）
- **保留动作**：如果将来复现"切屏冻结"，按用户结论处理，**不再要求重启**

---

## 四、技术约束（已取证，不许再试）

| 尝试 | 结论 | 来源 |
|---|---|---|
| 反编译成 C# 再重编译 | ❌ 物理不可行（CLR Header 空、无 BSJB 签名） | `README_MOD_KNOWLEDGE.md` 第五节 |
| Cpp2IL 恢复方法体 | ❌ 方法体全为 `throw null`（我用 ilspycmd 8.2 复核 `PlayerControlCustom` 确认） | `_recovered/PlayerControlCustom.decompiled.cs` |
| 数据结构 / 枚举取值 | ✅ 完整可得 | `_dump\`（80 个 dll） |
| 把 `LockonTargetCharacter` 写成 `null` | ❌ **整个战斗系统崩溃**（敌人 AI / 锁定 / 战斗 UI 全消失） | `COMBAT_SYSTEM_MAP.md` 事故节 |
| 在 IL2CPP 下用 `ClassInjector` 注入托管 MonoBehaviour | ❌ 类型注册了但 `Start()` 从不被调用 | `ERRORS.md` |

**⇒ 方法行为只能靠：① 运行期探针 ② 原生机器码反汇编。二者都没有时，不许改。**

---

## 五、工具与基础设施（接手后现状）

| 工具 | 位置 | 用途 | 备注 |
|---|---|---|---|
| Cpp2IL | `_tools\Cpp2IL\Cpp2IL.exe` | 类型转储 | 2026.2.22 版 |
| 转储产物 | `_dump\` | 查类型/字段/枚举 | 80 个 dll |
| ilspycmd | `C:\Users\34294\.dotnet\tools\ilspycmd.exe` | 看声明（**方法体是桩**） | 8.2.0 |
| 读多帧 zstd 会话日志 | `_tools\zst_frames.js` | ★ **接手后我新写的** | 见下 |
| 读用户真实发言 | `_tools\read_session_msgs.js` | ★ 新写 | `node read_session_msgs.js <log> user 8` |
| 按关键字回溯会话 | `_tools\grep_session.js` | ★ 新写 | `node grep_session.js <log> "<正则>" 0 2` |

### ⚠️ 环境坑（接手时踩到的，记下来免得后人再踩）

1. **`python` 是 Windows Store 存根，静默失败（退出码 1、无输出）**。
   本机可用的是 `py`（3.13，`C:\Users\34294\AppData\Local\Python\`）。
   `py` **没有** `zstandard` 模块 ⇒ 前任的 `_tools\read_session_tail.py` 在本机跑不起来。
2. **`.ps1` 脚本必须带 UTF-8 BOM**。本机 `powershell.exe` 是 **5.1**（`pwsh` 不存在），
   它读无 BOM 的 `.ps1` 会按 GBK 解码 ⇒ 中文全乱码 ⇒ **解析器直接报语法错误**。
   我新建 `_tools\build-deploy.ps1` 时踩到了，加 BOM 后通过。
   ⇒ 以后本仓库所有 `.ps1` 一律带 UTF-8 BOM。
3. **执行策略禁止直接跑脚本**（`CurrentUser = RemoteSigned`，文件未签名）：
   ```
   & script.ps1   →  SecurityError: 未对文件进行数字签名
   ```
   解决办法（只需一次，进程级）：
   ```powershell
   Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
   ```
4. **游戏根路径没有环境变量**（实测 `AIDEAL_RAYS_ROOT` / `AIDEALRAYS_ROOT` 的 User/Machine 级都没设）。
   `_tools\build-deploy.ps1` 因此**不内置默认路径**（路径属外部配置），
   首次使用先设：`$env:AIDEAL_RAYS_ROOT='<游戏根>'`。
5. **DSH 会话日志 `session.v4.jsonl.zstd` 是"每帧一个独立 zstd frame"的顺序拼接**
   （实测本文件 **6937 帧**）。Node 的 `zlib.zstdDecompressSync` 只解第一帧（199 字节），
   `createZstdDecompress` 也提前 end ⇒ 都必须按 magic `28 B5 2F FD` 手动切帧。
   已封装进 `_tools/zst_frames.js`。

---

## 六、待办与文档债

| 编号 | 事项 | 依据 | 状态 |
|---|---|---|---|
| T-D1 | `FlightPlugin.cs:1403` 注释说"下降改到 C 键"与实现 `LeftControl` 冲突 | 源码 vs 游戏日志 | 未开始 |
| T-D2 | `README_MOD_KNOWLEDGE.md` 第一节写「Shift 下降」，与当前实现（Shift=冲刺）冲突 | 文档 vs 日志 | 未开始 |
| T-D3 | `README_MOD_KNOWLEDGE.md` 末尾"本会话 id"仍指向已弃用的 `session-f50f8e08` | 文档 | 未开始 |
| T-D4 | 空目录 `_dump\Cpp2IL*` 残留、无用 `_tools\_probe_sess*.js` 清理 | 卫生 | 未开始 |
| T-D5 | 引入版本控制（当前**无 git**，一次误改无法回滚） | 约束七·13「动游戏/工程目录前先备份」 | 未开始 |

---

## 七、工作纪律（写给每一轮的我）

1. 每轮回复开头回填本文状态（约束一·2）。
2. 改任何行为前，先在本文或专项文档里写出**权威来源**；写不出就不改（约束二·4）。
3. 需要"看效果"时，先加只读探针 / 自动化，**绝不要求用户替我产生数据**（约束三·6、7）。
4. 只有"手感 / 画面"才允许问人，且**一次问清 + 写明为什么机器无法自证**（约束三·7）。
5. 修 bug 必须给"物理上只能发生 N 次"的不变量证明（约束四·8），并清理死代码（约束四·9）。
6. 每轮区分 ①已验证 ②只能推理 ③只有人能确认（约束五·10）。
7. 一轮要么推进一个**已验证**的步骤，要么停下来出**根因报告**（约束七·12）。
8. 动游戏目录/工程目录前先备份，并给出**可复制的回滚命令**（约束七·13）。
