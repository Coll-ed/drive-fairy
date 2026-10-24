# 驱动妖精 3D 空战 Mod（drive-fairy）

把《驱动妖精 / AI-deal-Rays》的 **2.5D 战斗改造成真正的 3D 空战** 的 BepInEx 插件。
当前阶段按用户定的优先级推进：**先把玩家操控角色三维化，敌人 AI 暂不动**。

> 本目录是**源码快照**（2026-10-16 状态）。原始工作区在另一处、未纳入版本控制，所以这里以快照形式归档。

---

## 目标游戏与环境

| 项 | 值 |
|---|---|
| 游戏 | 《驱动妖精 / アイディールレイズ / AI-deal-Rays》（`AldealRaysR18.exe`） |
| 引擎 | Unity **2022.3.62f2**，**IL2CPP**（旧式 IL→C++→机器码） |
| Mod 框架 | **BepInEx 6.0.0-be.788**（Il2CppInterop 生成 114 个 interop 程序集）+ Harmony |
| 插件运行时 | .NET 6.0 |
| 目标平台 | Windows x64 |

⚠️ IL2CPP 的关键限制（决定了本项目的工程方法）：**方法体不可读**。
Cpp2IL 转储能拿到完整的类型/字段/枚举/签名，但方法体全是 `ldnull / throw` 桩，
ilspycmd 也一样。⇒ 一切行为结论只能靠**运行期探针**或**原生机器码反汇编**取证，
"读代码猜逻辑"在这个项目里是不成立的。

---

## 这个模组做什么

用户已定稿的键位表（以此为准）：

```
F2 模式：右键/闪避，长按防御 ｜ 左键/普攻，蓄力突进，抓取 ｜ Shift/冲刺 ｜ Ctrl/下降 ｜ Space/上升
F3 模式：第三人称脱离准心（锚点 = 可控准心，锁定目标指向锚点）
         中键 = 锁定最近敌人（优先于锚点） ｜ Q/E = 锚点三档距离（远/中/近） ｜ 左Alt = 自由视角
```

- **F1 / F2 / F3 三个模式**：原版 / 自由飞行（垂直位移）/ 第三人称脱离准心。
- **垂直飞行**：Space 上升、Ctrl 下降，直接改 `Transform.position.y`（不动游戏的移动管线）。
- **锁定相机三轴跟随**：把 6 个锁定相机的 `LookAt` 指向敌人本体，解决"XY 锁住了 Z 没锁"。
- **锚点瞄准**（F3）：用可控锚点替代"锁定敌人"作为瞄准点，中键仍可强制锁最近敌人。
- **虚拟光标抑制**：拦掉原版"鼠标一动就呼出光标"，改 `CursorLockMode.Locked`（全屏循环光标）。
- **疾飞（Shift）**：调游戏自带的 `PlayerControlCustom.ForceBooston/ForceBoostoff`。

---

## 目录结构

```
drive-fairy/
├── plugin/                     插件源码（可直接编译）
│   ├── Common.props            ★ 公共构建配置：目标框架、dll 引用、部署逻辑
│   ├── Flight/                 ★ 主插件 AidealRays.Flight（14 个 .cs）
│   │   ├── FlightPlugin.cs     核心（约 3900 行）
│   │   ├── FreeFlightCamera.cs 自由摄像机接管
│   │   ├── VirtualAimAnchor.cs F3 锚点
│   │   ├── VirtualCursorSuppressor.cs  虚拟光标抑制
│   │   ├── MoveTrace.cs        移动管线观测（只读）
│   │   └── …                  相机调校、锁定重定向、各类探针
│   ├── BoostProbe/             加力取证探针（独立插件，默认关闭，不参与正式功能）
│   ├── CameraDiag/             相机诊断探针
│   ├── LoadProbe/              加载耗时探针
│   └── FlightDiag/             早期诊断稿（仅 .cs，未建独立工程）
├── docs/                       逆向取证文档与工作台账（★ 接手必读顺序见文末）
└── tools/                      构建部署、自动实机取证、会话日志解析
```

---

## 构建

前置：**.NET 6 SDK**；游戏已装 BepInEx 6 并**成功启动过一次**（否则没有 `BepInEx\interop\*.dll` 可引用）。

Common.props 里所有路径都从环境变量读，**工程内没有写死绝对路径**：

```powershell
$env:AIDEAL_RAYS_ROOT = '<游戏根>'          # 含 BepInEx\ 与 AldealRaysR18_Data\
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force   # 脚本未签名

# 构建 + 部署到 BepInEx\plugins + 校验"部署字节 == 构建字节"
.\tools\build-deploy.ps1 -Project Flight

# 只构建，不碰游戏目录
.\tools\build-deploy.ps1 -Project Flight -Deploy:$false
```

其他工程同理：`-Project BoostProbe | CameraDiag | LoadProbe`。

产物：`plugin/<工程>/bin/Release/net6.0/AidealRays.<工程>.dll` → `BepInEx\plugins\`。

> 部署脚本会先把旧 dll 备份到 `_backup\plugins\`，并在末尾打印**可复制的回滚命令**。

也可以直接 `dotnet build -c Release`（在 `AIDEAL_RAYS_ROOT` 已设的前提下），
但那样不会做哈希校验与备份 —— 建议走脚本。

### Windows 脚本的两个坑（踩过）

1. `.ps1` 必须是 **UTF-8 带 BOM**：本机 PowerShell 5.1 读无 BOM 脚本会按 GBK 解码 ⇒ 中文乱码 ⇒ 语法错误。
2. 执行策略：`CurrentUser = RemoteSigned`，未签名脚本会被拒 ⇒ 需要 `-Scope Process Bypass`。

---

## 自动实机取证（无人值守）

游戏需要"进战斗"这种交互才能复现状态，而工程纪律禁止"让人来当代测"。
`tools/` 里的这套脚本就是为了**自己做实验**：

```powershell
$env:AIDEAL_RAYS_ROOT = '<游戏根>'
.\tools\auto-d1-run.ps1          # 启动游戏 → 进战斗 → 模拟按键（W / W+Shift）→ 采日志 → 出对照表
```

- `game-io.ps1`：`shot`（用 `PrintWindow` 抓**后台窗口**，前台被遮挡也能抓）/ `focus` / `click` / `key`
- `summarize-d1.ps1`：把 `[BPROBE][DISP]` 的位置差按阶段对照成表（移速到底变没变的客观答案）
- `zst_frames.js` + `read_session_msgs.js`：读 DSH 会话日志（**每帧一个独立 zstd frame**，Node 内置 API 只能解第一帧，必须手动切帧）

---

## 当前状态（截至 2026-10-16，诚实标注）

### ✅ 已验证可用

| 功能 | 依据 |
|---|---|
| 起飞与下降（Space / Ctrl） | 用户实测验收 |
| 相机跟随与锁定（含 Z 轴） | 用户实测验收「完美！」 |
| 锚点 + 渲染框 + 中键锁定 | 用户实测验收 |
| 构建/部署可复现 | 部署 dll 与构建产物 SHA256 一致 |
| 加力点火"只调一次" | 唯一调用点 + `_boostActive` 守卫，不变量成立 |

### ❌ 未通过 / 未完成

| # | 项 | 现状 |
|---|---|---|
| D1 | **疾飞（Shift）姿态与速度** | 取证中。已确认：游戏加力状态机**能进**（`RIGHT_BOOST`），且加力时 `assistSpeedMagni` **恒为 4 不变** ⇒ "移速没变化"有数值证据；朝向与输入夹角 63°/147° ⇒ 正对应用户说的"不断转身" |
| D2 | 喷气特效每帧叠加 | 代码层不变量成立（一次按住只点火 1 次），**运行期实测未做** |
| D3 | 休息姿态冲刺（以爱迪尔为中心 / 无法攻击 / 失去锁定） | 未开始；`ACTION_FLOW` 枚举里没有该姿态，候选是 `READY_ENEMYS_LOOP` |
| D4 | 虚拟光标 | 静态已排除"手柄虚拟光标"这个错靶（`VirtualCursor` 是摇杆驱动的），真正来源待运行期判定 |
| D5 | 加载慢 | 已定性：**切屏会让游戏自冻结**，不是死锁、不是插件问题 |

### ⚠️ 高危陷阱（血泪教训，改代码前必看）

| 禁忌 | 后果 |
|---|---|
| 把 `LockonTargetCharacter` 写成 **null** | **整个战斗系统崩溃**：敌人 AI 消失、锁定失效、战斗 UI 消失 |
| 在 IL2CPP 下用 `ClassInjector` 注入托管 MonoBehaviour | 类型注册了但 `Start()` 从不被调用 |
| 凭字段名猜语义就改 | 曾写 `Move.y` 导致"无法移动" |
| 一次上线多个未验证改动 | 出问题无法定位 |

---

## 文档索引（`docs/`，按此顺序读）

| # | 文档 | 内容 |
|---|---|---|
| 1 | `README_MOD_KNOWLEDGE.md` | 知识库总索引与现状 |
| 2 | `LEDGER.md` | **主线台账**：目标验收标准、5 条缺陷逐条状态与证据 |
| 3 | `COMBAT_DATA_ATLAS.md` | 战斗数据结构完整图谱（技能表/攻击/位移/大招） |
| 4 | `MOVEMENT_STRUCTURE.md` | 移动系统分层（输入 → 运动学 → 游戏自研层） |
| 5 | `CAMERA_STRUCTURE.md` | 相机系统（GroupComposer 缩放机制） |
| 6 | `DESIGN_FREE_AIM.md` / `FREE_AIM_FINDINGS.md` | F3 设计 + 实测问题与对策 |
| 7 | `COMBAT_SYSTEM_MAP.md` / `ERRORS.md` / `NOTES.md` | 接口清单、事故记录、历史踩坑 |
| 8 | `FLIGHT_DONE.md` / `FLIGHT_PHASE1.md` | 飞行功能完成记录与参考实现 |
| 9 | `SELF_AUDIT_AND_REMEDIATION.md` | 前一任会话的自证与整改清单（工作纪律来源） |

---

## 说明

- 仅个人自用学习研究；**不含任何游戏本体素材**（贴图/模型/音频）。
- 代码只在运行时对游戏内存/对象做改写入，不修改游戏文件本身（部署仅新增 `BepInEx\plugins\*.dll`）。
- 工程目前**无 git 历史**：本快照是单一时间点，无中间提交可 diff。
