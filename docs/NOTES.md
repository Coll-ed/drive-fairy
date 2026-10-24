# 驱动妖精 AI-deal-Rays · 3D 空战改造前期取证

目标：把游戏从「2.5D 空战观感」改成真 3D 空战。本文件只记录**只读取证结论**，
不含任何游戏文件改动。

---

## 一、引擎与构建（实测定论）

| 项 | 值 | 取证来源 |
|---|---|---|
| 引擎 | **Unity 2022.3.62f2** | `globalgamemanagers` 头部 ASCII |
| 脚本后端 | **IL2CPP** | `GameAssembly.dll` 40.24 MB + `il2cpp_data\Metadata\global-metadata.dat` 9.79 MB |
| 托管程序集 | **无**（`Assembly-CSharp.dll` **不存在于磁盘**，而是编进 `GameAssembly.dll`） | 目录扫描 |
| 游戏名 | `AldealRaysR18` / 作者 `Riez-ON` | `app.info` |
| 打包程序集数 | 115 个，含 **`Assembly-CSharp.dll` + `Assembly-CSharp-firstpass.dll`** | `ScriptingAssemblies.json` |
| 场景 | `level0` ~ `level18`，最大 `level16`(404 MB)、`level14`(150 MB) | `*_Data` 目录 |
| 资产包 | 6 个 AssetBundle（56 MB ~ 804 MB）+ 大量 `sharedassets*.assets.resS` | `StreamingAssets\StandaloneWindows64\` |
| 渲染 | `nvngx_dlss.dll` + `NVUnityPlugin.dll` → DLSS 支持 | 根目录 |
| 加密 | **未发现资产加密迹象**（标准 Unity 格式） | 与 Fallen Doll 的加密 pak 对比 |

**结论：IL2CPP 意味着无法反编译出 C# 源码，但 metadata 保留了全部类型名/方法名/字段名。**
托管程序集打进 `GameAssembly.dll`，所以需要 Il2CppDumper 才能拿到带签名的 `dump.cs`。

---

## 二、自研代码清单（73 个脚本，`Assets/AidealRaysCustom/`）

`il2cpp_own_code_probe.py --self-dir AidealRaysCustom` 输出。按功能分层：

| 层 | 脚本 |
|---|---|
| 玩家控制/移动 | `PlayerControlCustom` · `MoveControlHelper` · `CharacterControllerBase` · `RotateController` · `ResetTransform` |
| 相机 | **`VCameraSelectManager`** · `CameraProjectionRenderingToolkit`(插件) |
| 锁定系统（完整一套） | `LockonCursol` · `LockonHUD` · `LockonLineController` · `LockonMarker` · `EnemyMarker` |
| 敌机 | `EnemyController` · **`EnemySylpheedController`** · `NavigationManager` · **`KDTree`** · `DirectionSelector` · `ApproachFormParam` |
| 战斗 | `BattleManager` · `ShotController` · `AttackParam` · `AttackSubParam` · `AttackForce` · `ActionParam` · `BurstParam` · `DrainManager` |
| 主角 | **`SylpheedController`**（主角名 Sylpheed） |
| 输入 | `AldealRaysInput` |
| 事件/剧情 | `EventManager` · `EventFlowManager` · `EventApproach` · `EventResources` |
| 调试 | **`DebugMonitor`** |
| 其他 | `GameDataStore` · `GameManagerBase` · `TopManager` · `QualityManager` · `MaterialManager` · `MeshCombiner` · `BundleAssetMap` · `LoadResourcesManager` · `LauncherController` |

**已更正的判断**：早期用通用关键词（`LockOn`/`EnemyAI`）搜索得出「锁定与敌人 AI 未命中」，
**该判断错误**——自研类名不使用通用英文词。实际为 `LockonCursol`/`AutoLockonScanExec`
与 `EnemyController`/`EnemyLogicRoot`。以完整目录树为准。

---

## 三、3D 能力证据（推翻「引擎只支持 2.5D」）

| 证据 | 说明 |
|---|---|
| `CinemachineOrbitalTransposer` / `CinemachineFreeLook` / `CinemachineHardLookAt` / `CinemachineConfiner2D` | 全向环绕相机框架 |
| `ComputeAdaptivePaniniProjFOV` / `ComputeStereosphericalProjFOV` | Panini 投影（飞行/竞速宽视场专用） |
| `Roll` / `Bank` / `GetRoll` / `AnimateRoll` / `GetBank` / `GetYaw` / `YawPitch` | 滚转与倾斜轴存在 |
| `FlightYoke` / `FlightStick` / `FlightControlStick` / `FlightSimulationDevice` / `AutoFlight` / `FBasic_FlyMovement` | 成套飞行模拟输入 |
| `SFX_PhysicsHomingMissile` + `Launcher` | 物理追踪导弹（需三维导引） |
| `AQUAS 2020` + `KriptoFX.WaterSystem` + `Caustic` + `Obi`/`Zibra` 流体 | 水下体积、焦散、流体模拟 |
| **`KDTree`**（自研） | **三维最近邻查询** —— 锁定/目标搜索本就在三维空间进行 |
| 470 个含 `Camera` 的标识符 | 相机系统规模庞大 |

→ **「2.5D」是玩法/镜头设计的产物，不是引擎能力上限。**

---

## 四、调试面挖掘（本轮主目标：`DebugMonitor`）

### 4.1 `DebugMonitor` 的调用现场（metadata @0x000C463E）

连续字符串段：

```
ComponentUtility / self / AddComponent / DebugMonitor
Input / Range / SELECT_DIRECTION / DOWN / RIGHT / LEFT / horizon / vertical
CheckDirection / ActivateAfter / direction / showName / disable / AddItem / UpdateItem
```

另外两处出现：类型名表（`|DebugMonitor`，说明它是**真类型**）与
文件路径 `\Assets\AidealRaysCustom\Script\DebugMonitor.cs`。

**判读**：`AddComponent` 出现在紧邻位置，且接口为 `showName`/`disable`/`AddItem`/`UpdateItem`
+ 方向输入处理（`SELECT_DIRECTION`/`DOWN`/`RIGHT`/`LEFT`/`horizon`/`vertical`）
→ 这是**运行时被动态挂载的调试叠加层**，具备条目增删与方向选择交互。

### 4.2 意外收获：内置自由相机与时间缩放

`FreeCameraSet` @0xC5757 的邻居串是一个**大类的成员名列表**：

```
ationSpeed / ClearMesh / ClearMeshImm / ClearMeshExec
SlideManClear / SlideDickClear / SlideMobClear / SlideMobDickClear
Retry / PlayEventAfterSelf / SetFlag
ChangeCostume4Script / ChangeCostumeKey4Script / ChangeCostumeRay4Script
SuperSlow / FlowFinishNotify / hide / HideAideal / HideRay
ChangePlayerCostume4Sctipt / Underwares4Script / Inner4Script
FreeCameraSet / FreeCameraReset / SearchActiveCameraTrm / motion
ChangeSexMotion / EnableFreeCameraV2
<AfterAssetbundleLoad>b__61_0 / <HideObjHCamera>b__178_0
ActionScroll / GameUI / AIdealAnimator / AidealController / MobAnimatorList
PlayerCostume / EventList / IndexAtstart / LongshotCamera / LifeButtons
Hevent / ActiveIndex / ActiveCameraIndex
```

**该游戏自带：自由相机开关/重置、相机搜索、长焦镜头、相机索引切换、
时间缩放（`ChangeTimeScaleExec` / `SimulationTimeScale` / `defaultTimeScale`）。**

这对 3D 空战改造是重大利好：**自由相机是最难自己写的一环，游戏里已经有了。**

### 4.3 关键方法的 metadata 存在性

| 标识符 | 在 metadata 中 |
|---|---|
| `DebugMonitor` | 命中 @0xC463E（3 次出现） |
| `FreeCameraSet` / `FreeCameraReset` | 命中 @0xC5757 |
| `EnableFreeCameraV2` | 命中 @0xC57A2 |
| `CancelFreeCamera` | 命中 @0xC540F |
| `CheckFreeCameraInput` | 命中 @0xC6DBD |
| `ChangeTimeScaleExec` | 命中 @0xC1D9F |
| `OnTimeScaleSliderValueChanged` | 命中 @0xE51FF |
| `DEVELOPMENT_BUILD` | 命中 @0x8FC341 |
| `OnDrawGizmos` | 命中 @0xCC4DB |

### 4.4 诚实的限制（不可越过的一步）

⚠️ **metadata 里存在某标识符，只证明「它被编进了程序」，不证明「发布版绑定到了按键」。**

- **打断的取证**：试图用「标识符出现次数」区分类型名与字符串（≥2 次算类型）。
  实测该判据**不可靠**：`EnableFreeCameraV2`/`CheckFreeCameraInput`/`LongshotCamera`
  等明显的方法名只出现 1 次，而 `ChangeTimeScaleExec` 出现 2 次。
  **故不以此下任何结论。**
- **`RuntimeInitializeOnLoads.json` 为 0 字节**（0 个类型、0 个运行时初始化方法）
  → 开发者未使用该机制，因此**无法**用它判断调试逻辑是否仍自动执行。
  这条权威路径在本作上走不通。
- 按键绑定在 `DefaultInput.ini` 里，位于资产包内，静态无法读出。

**要确认「DebugMonitor 是否可被激活」，只能动态验证：**
① 进游戏盲试组合键（罕见键/Ctrl+Shift+字母/F 系列）；
② 或装 **BepInEx 6 (IL2CPP)** + Harmony，在运行时主动 `AddComponent<DebugMonitor>()` 强制挂载。

---

## 八、相机架构与战斗平面约束（第二轮取证）

### 8.1 输入层实证：`Move` 是 Vector2，全表无高度轴

从 metadata 里提取出**完整的 InputActionAsset JSON**（44,262 字节，@0x7588F）：

`extract_inputactions.py` 输出，2 个 ActionMap / 76 条绑定。

**Player 地图（11 个 action）**：

| Action | 类型 | 绑定 |
|---|---|---|
| **Move** | **Value / Vector2** | `WASD` / 方向键 / 左摇杆 |
| **Look** | **Value / Vector2** | 右摇杆 / 鼠标 delta（`ScaleVector2(0.1,0.1)`） |
| Fire | Button | 鼠标左键 / 右扳机 |
| Fire Charge | Button **Hold** | 长按左键 |
| Avoid | Button | 鼠标右键 / 左扳机 |
| Avoid Charge | Button **Hold** | 长按右键 |
| Function | Button | 鼠标中键 / 左肩键 |
| **Burst** | Button | 鼠标左键+右键组合 |
| Mode1 / Mode2 | Button | `1` / `2` |
| Option | Button | `Esc` |

**针对性判定**：绑定表内 `ascend`/`descend`/`vertical`/`height`/`Altitude`/`Throttle`/`pitch`/`yaw` **全部 0 次**。

→ **游戏从输入层就没有表达三维移动的能力。平面约束的根源在输入设计，不在引擎。**

→ 输入系统确认为 **Unity 新版 Input System**（`InputActionAsset`/`InputActionMap`/`InputActionReference`），
   意味着按键映射**可改**，且有标准改法。

### 8.2 相机是「索引选择」式，且 H 场景已有可用自由相机

用户提供的关键事实：**H 场景里摄像机是自由的、玩家能自主操控鉴赏 CG；战斗里被约束在平面。**

metadata 里 `VCameraSelectManager` 的成员簇：

```
VCameraSelectManager
    SelectVCamera        ← 方法
    VCameraList          ← 虚拟相机列表
    mainCamera / uiCamera / CanvasTrs / CameraSelector
```

`Asset/AidealRaysCustom` 目录下另有 `VCameraSelectManager.cs`。
H 场景控制类（H 事件控制器）的成员簇里有：

```
CameraSelector / ActiveCameraIndex / LongshotCamera / MainCameraAnim
FreeCameraSet / FreeCameraReset / EnableFreeCameraV2 / SearchActiveCameraTrm
EroCameraIndex / ActiveHIndex
CameraCutEvent / SwitchEventCamera / HideObjHCamera
```

→ **相机机制是「从 VCameraList 按索引切换」**，`FreeCameraSet`/`FreeCameraReset` 是自由相机的设置/复位。

**这是重大利好**：自由相机（改造中最难自写的一环）**游戏里已有实现且可用**，
战斗相机只是列表里的另一项。**给战斗接一个自由相机，很可能是"加一项 + 切索引"，而非重写。**

### 8.3 一条**已推翻**的错误线索（如实记录）

曾注意到 `AutoFlight` / `HeightOffset` / `BattleExtraResources` 在相机选择器周边出现，
一度以为 `HeightOffset` = 战斗高度偏移。

**实测否定**：`HeightOffset` @0x66D37 的成员簇只有 14 个标识符，且全在一个
**下划线拼接的超长字符串**里（`_HeightFog_HeightOffset_HighlightSuppression_...`），
长度达数千字节 —— 这是 **Unity Shader 属性名表**，不是游戏字段。

→ `HeightOffset` 是 Shader 属性，**与战斗高度无关**。该线索作废。

`AutoFlight` 仍出现在战斗相关簇（邻近 `LaunchAfter`/`EnemyShot`/`AttackExec`/`BitController`），
但其**具体语义未证实**，不下结论。

### 8.4 `PlayerControlCustom` 的成员簇（含 3D 线索）

```
PlayerControlCustom
  ── 输入 ──
    buttonDown / buttonUp / stopWatch / pressed / keepTime / GetButtonStatus / ResetInput
  ── 3D 旋转与朝向 ──
    RotateXOffset / RotateYOffset / Dutch / SetForwardForce
  ── 锁定 ──
    lockonTarget / SilentLockon / ForceLockoff / AutoLock
    LockonChangeNext / IncrementSemilockonCnt / DecrementSemilockonCnt
    CameraTargetSwitch / RegistEnemy / Targetable / Selectable
  ── 攻击/爆发 ──
    GetBurstDirection / BurstSelectLogic / EvaluateMoveAndAttack / CheckActionAllBurst
    IsSingleAttack / ImpossibleAction / CheckStatus / FunctionChange
  ── 管理器 ──
    InitManagerTask / BindTimelines / VCameraInfo
```

**判读（标注为待验证假设）**：`RotateXOffset`/`RotateYOffset`/`Dutch` 表明相机跟随带有
俯仰/偏航偏移与滚转（`Dutch` 在 Cinemachine 里专指**镜头滚转**）。但配合 8.1 的
`Move` 为 Vector2，更可能是「**相机能三维旋转，移动仍限平面**」。

→ 这正好解释了用户的体感：**战斗相机被约束平面，但相机本身具备三维旋转能力。**
   两条证据（输入无垂直轴 + 相机有 RotateX/YOffset）互相印证。

## 九、改造路线（修订版）

| 层 | 现状 | 改造量 | 依据 |
|---|---|---|---|
| **相机** | `VCameraSelectManager` 索引切换；H 场景自由相机已可用 | **低~中**（加一项 + 切索引） | 8.2 |
| **输入** | `Move` = Vector2，无高度轴 | **中**（新增垂直轴，标准 Input System 改法） | 8.1 |
| **玩家移动** | `SetForwardForce` 力度式推进 + 相机旋转偏移 | **中**（把垂直分量接进力/位移） | 8.4 |
| **敌人 AI** | `EnemyController` + `EnemySylpheedController` + `KDTree`（三维查询已具备） | **高**（需让敌机在三维空间机动） | 二节 |
| **关卡边界** | `Boundary` / `ApplyTankBoundary` / `Ceiling` | 中 | — |

**最小可行第一步**：只做「输入加垂直轴 + 相机解放」，先看飞行手感是否成立。
敌人 AI 留到第二步 —— 否则一上来就啃最难的那层，容易半途而废。

## 十、`DebugMonitor` 最终结论

- 类型存在（类型名表有 `|DebugMonitor`，另有文件路径 `\Assets\AidealRaysCustom\Script\DebugMonitor.cs`）
- **但不在 InputActions 资产里** → 它不通过新版 Input System 绑定按键
- 其输入更可能走旧版 `UnityEngine.Input`（簇内有 `Input`/`Range`/`SELECT_DIRECTION`/`DOWN`/`RIGHT`/`LEFT`）
- **用户实测：全部热键组合无反应** → 该调试面在发布版**未绑定可用热键**

→ 结论：**没有免费午餐**，`DebugMonitor` 要走运行时主动 `AddComponent` 才能验证其价值。
   盲试热键路线已终止（有实测依据）。

## 十三、BepInEx 6 环境搭建（用户已定设计，准备实施）

### 13.1 用户确定的设计（范围收窄）

> 用户原话要点：相机**仍用第三人称跟随**（不改相机）；空格=上升、Shift=下降；
> 中键=切换攻击方式（原版锁定瞄准 ↔ 免锁定攻击）。
> 本轮范围：**只做玩家角色三维化，AI 不动。**
> 验收标准：**按空格能升、按 Shift 能降，就算成功。**

**这个设计把工作量砍掉一大块**：相机系统整个免改（第三视角保留即可）。

### 13.2 键位占用实测（`key_occupancy.py`）

在 76 条绑定路径内逐一核对：

| 拟用键 | 结果 |
|---|---|
| **空格** | **未被占用** ✓ |
| **Shift** | **未被占用** ✓ |
| 中键 | 已占用 → `Function`（Player 图）+ `MiddleClick`（UI 图） |

两个**假警报**（已澄清，非按键绑定）：
- `Space` 命中在 `Post FX/Motion BlurHidden/Post FX/...` —— 是后处理效果的 UI 文字
- `LeftShift`/`RightShift` 邻居是 `DwordMod|DwordDivMod|multiByteDivide|MultiplyByDword` —— 是 C# 位运算方法名

**关键利好**：`Function` action 本来就绑在鼠标中键上，且**没有 interactions**（按下即触发）。
→ 用户要的「中键切换攻击模式」**不需要新键位，`Function` 就是现成钩子**。

### 13.3 移动层：本来就是 3D 的

`MoveControlHelper` 的成员簇显示它基于 **Unity 标准第三人称角色控制器**：

```
transformMove / SetTransformMove / TransformMove
MovingTurnSpeed / StationaryTurnSpeed / MoveSpeedMultiplier
TurnAmount / ForwardAmount / MoveVector
```

`MoveVector` 是 `CharacterController` 体系的标准移动向量。

→ **角色控制器本来就能在 Y 轴移动，只是输入层没给 Y 分量。**
→ 所以"加垂直飞行"≈ **往 `MoveVector` 喂一个 Y 值**，不是新建飞行系统。

### 13.4 免锁定攻击模式：机制已存在

`PlayerControlCustom` 簇内有完整锁定/攻击机制：

```
SilentLockon / ForceLockoff / AutoLock / lockonTarget
AutoLockonScanExec / TargetListRecalc / LockonRecalc / LockonChangeNext
CameraTargetSwitch / GetBurstDirection / EvaluateMoveAndAttack / FunctionChange
```

→ 切换 `AutoLock` 状态 + `ForceLockoff` 基本即"免锁定攻击模式"。

### 13.5 工具链决策（**推翻了先前的建议**）

⚠️ **先前的建议（装 Il2CppDumper）是错的，此处更正：**

| 事实 | 来源 |
|---|---|
| 本作 **metadata 版本 = 31**（实测读文件头） | `global-metadata.dat` @偏移 4 |
| Unity **2022.3.62f2** | `globalgamemanagers` 头部 |
| 官方 Il2CppDumper README 声明仅支持 **Unity 5.3 - 2022.2** | Perfare/Il2CppDumper README |
| 社区实测报错 `Metadata file supplied is not a supported version[31]` | Il2CppDumper issues #797 / #841 / #802 |
| BepInEx 6.0.0-pre.1 发布页自带警告：老版本对新 IL2CPP 游戏基本无效，请用 bleeding-edge | 官方 release body |
| BepInEx 的 IL2CPP 路线用 **Cpp2IL**（非 Il2CppDumper）；社区有 Unity 2022.3 / v31 成功案例 | BepInEx issue #1231 |
| BepInEx 5.4.23.5 有专门修复：`unity 2022.3.62f3 not having get_graphicsDeviceID` | v5.4.23.5 release notes |

**结论：必须用 BepInEx 6 bleeding-edge（内含 Cpp2IL），不用 Il2CppDumper。**

### 13.6 本机环境核查（实测）

| 项 | 结果 |
|---|---|
| .NET SDK | **8.0.416 + 9.0.315** ✓（满足 BepInEx 6 的 .NET 6+ 要求） |
| git / tar | 已安装 ✓ |
| 游戏根可写 | ✓ |
| 现有 mod 加载器 | 无（干净） |

### 13.7 构建站实测（`builds.bepinex.dev`）

- 端点可达：HTTP 200，页面 575,471 字节
- **页面为构建号降序**（最大 788，最小 510，共 148 个）→ 第一条即最新
- 目标产物：`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`
  （注意是 **Unity.IL2CPP**，不是 Mono）
- 下载 URL（`+` 需转义为 `%2B`）实测：**HTTP 200，34,336,405 字节**

### 13.8 安装器 `install_bepinex6.ps1`

行为：预检 → 备份清单 → 实时解析最新构建 → 下载 → 解包 → 核对；支持 `-DryRun` 与 `-Uninstall`。
构建号**不写死**（每次实时解析，站点结构变化会显式报错而不是静默拿错版本）。

**DryRun 实测抓出并修掉的 3 个真 bug**（记录在案，供后续脚本借鉴）：

| # | 症状 | 根因 | 修法 |
|---|---|---|---|
| 1 | 魔数判定 `✗ 异常`（文件明明正确） | **PowerShell 把 `0xFAB11BAF` 解析为 Int32 负数 `-89056337`**；`[uint32]` 转换会溢出失败，与 `ToUInt32` 比较恒不等 | 改十进制无符号字面量 `[uint32]4205910959`（实测 `u` 后缀不被支持、`[uint32]` cast 也溢出；只有十进制字面量与 `BitConverter` 重解释可行） |
| 2 | 卡在"解析构建"（>120 秒未返回） | 对 575KB 页面收集**全部** 1400+ 匹配再 `Select-Object -Unique` | 改用 `[regex]::Match` 短路取第一条 |
| 3 | `winhttp.dll` 打印两次 | marker 清单与代理 dll 候选清单重叠，未去重 | 抽出 `$ALL_MARKERS` = 两清单并集去重，作为**单一来源** |

**DryRun 最终结果（第 3 次复跑）**：魔数 ✓ OK、6 个 marker 各一次、构建解析 3.8 秒完成、
**额外文件 0 个、备份目录 0 个（确认 DryRun 未写入任何东西）**。

### 13.10 首次安装失败与修复（实测记录，含 4 个新坑）

**第一次正式安装失败**：`exit code 1`，报错
`Unable to read data from the transport connection: An established connection was aborted by the software in your host machine`
—— 下载中途连接被掐断。

**排查得到的 4 条硬结论（都可复用）：**

| # | 结论 | 证据 |
|---|---|---|
| 1 | **`Invoke-WebRequest` 比 `curl.exe` 慢约 87 倍** | 同一 URL、同一时刻：`Invoke-WebRequest` 25 秒只拿到 295KB（≈0.01 MB/s）；`curl` 连续 3MB 用 3.43 秒（**0.87 MB/s**）。34MB 全量：IWR ≈1 小时 vs curl ≈39 秒 |
| 2 | **该源支持断点续传** | 带 `Range` 请求返回 **HTTP 206**，可 `curl --continue-at -` 续传 |
| 3 | **BepInEx bleeding-edge 不发 GitHub Release** | `api.github.com/repos/BepInEx/BepInEx/releases/tags/build-788` → **404**。故**所有"GitHub 加速镜像"都用不上**（它们只能代理 github.com 地址）。实测 `ghfast.top`/`ghproxy.net` 对本域返回 **403**、`gh-proxy.com` 返回 **404** |
| 4 | **PowerShell 的 `$args` 是自动变量，不可赋值** | 初版写成 `$args = @(...)` 传给 curl；已改名 `$curlArgs`。同类自动变量还有 `$input`/`$error`/`$host`/`$home` 等 |

**修复**：下载改用 `curl.exe`，并加三重防护 ——
① 产物大小预探测（HEAD 读 `Content-Length`）；
② 分轮重试（`$DOWNLOAD_MAX_ATTEMPTS = 5`，每轮独立限时 `180s`）；
③ 断点续传（`--continue-at -`）。
下载不完整会显式 `exit 6` 并提示可重跑续传，**不会静默解包一个残缺的 zip**。

**失败的安全性**：第一次失败时游戏根**零写入**（0 项安装痕迹）——因为失败发生在解包之前。
半截文件只留在 `%TEMP%`，已清理。

### 13.11 操作指南（待用户执行）

```powershell
# 0) 确认游戏没在运行
Get-Process AldealRaysR18 -ErrorAction SilentlyContinue

# 1) 预演（不下载不写入）
$env:AIDEAL_RAYS_ROOT = '<游戏根>'
powershell -NoProfile -ExecutionPolicy Bypass -File '<本目录>\install_bepinex6.ps1' -DryRun

# 2) 正式安装
powershell -NoProfile -ExecutionPolicy Bypass -File '<本目录>\install_bepinex6.ps1'

# 3) 启动一次游戏，再查日志
#    <游戏根>\BepInEx\LogOutput.log 中应出现 "Chainloader startup complete"

# 卸载（回滚）
powershell -NoProfile -ExecutionPolicy Bypass -File '<本目录>\install_bepinex6.ps1' -Uninstall
```

**首次启动会跑 Cpp2IL 生成 interop 程序集**（作用于 40MB 的 `GameAssembly.dll`），
耗时可能从数十秒到数分钟；之后启动走缓存会很快。

### 13.12 环境搭建完成（实测成功）

**结果：BepInEx 6.0.0-be.788 已成功注入，114 个 interop 程序集已生成。**

```
[Message:Preloader] BepInEx 6.0.0-be.788 - AldealRaysR18
[Info:BepInEx] Running under Unity 2022.3.62f2
[Info:BepInEx] Runtime version: 6.0.7 (.NET 6.0.7)
[Info:Cpp2IL] Initialized Metadata in 122ms / Binary in 95ms
[Info:Cpp2IL] Cpp2IL finished in 00:00:05.0976420
[Info:Il2CppInteropGen] Restored 14572 methods / Failed 972
[Info:Il2CppInteropGen] IL unstrip: 7823 successful, 1313 failed
[Info:Il2CppInteropGen] Done!
[Message:BepInEx] Chainloader startup complete      ← 成功标志
```

产物：`BepInEx\interop\` 114 个文件，其中
**`Assembly-CSharp.dll` 5116 KB**、`Assembly-CSharp-firstpass.dll` 2146.5 KB。

已验证目标类**全部在 interop 中可读**：
`PlayerControlCustom` / `MoveControlHelper` / `VCameraSelectManager` / `DebugMonitor` /
`SylpheedController` / `EnemyController` / `LockonCursol`

#### 坑 5：`unity-libs\<version>.zip` 被静默污染（最耗时的一个）

**症状**（看着像程序集问题，实际不是）：
```
[Error:InteropManager] Failed to generate Il2Cpp interop assemblies:
  System.IO.InvalidDataException: End of Central Directory record could not be found.
[Fatal:BepInEx] Unable to execute IL2CPP chainloader, no plugins will be loaded
[Error:BepInEx] FileNotFoundException: Could not load file or assembly
  'UnityEngine.CoreModule, Version=0.0.0.0 ...'
```

**根因链条**：
1. 首次启动时网络差 → `BepInEx\unity-libs\2022.3.62.zip` 下载被截断为
   **131072 字节（正好 128KB = 0x20000，缓冲区取整的典型特征）**
2. BepInEx 源码逻辑（`Il2CppInteropManager.cs`）明确写着：
   *"If a .zip file with the same filename as the URL already exists in unity-libs,
   it will be used instead of downloading a new copy."*
   → **坏文件被永久复用，永不重下**
3. zip 解不开 → 拿不到 `UnityEngine.CoreModule.dll` → `SetupUnityLogging` 抛异常

**权威来源（从源码读出，不是猜的）**：
```
https://unity.bepinex.dev/libraries/{VERSION}.zip
```
（注：`github.com/BepInEx/BepInEx.UnityLibs` 是 **404**，我最初猜错了这个。）

**修复**：删掉残片 → 从上述源重下 → 校验。
正确文件 **1,900,589 字节（1.81 MB）**，**66 个条目**，含
`UnityEngine.CoreModule.dll`(1,381,888) 与 `UnityEngine.IMGUIModule.dll`(180,736)。

**教训**：报错指向 A（缺程序集），根因在 B（zip 损坏）。
只有读源码里那句"已存在就不再下载"的注释才能定位，**靠猜会一直绕圈**。

#### 坑 6：Doorstop 代理不依赖 PE 导入表

该包用的代理是 `winhttp.dll`（26112 字节，内含 `doorstop`/`Doorstop`/`WinHttp` 特征串），
`version.dll`/`winmm.dll` 未使用。

**重要事实**：本作 `AldealRaysR18.exe` 的**导入表被抹（RVA=0）**，
字节扫描**找不到 winhttp.dll 引用** —— 但 Doorstop **依然生效**。
→ Doorstop 的加载不依赖 PE 导入表（在被保护/加壳的 exe 上是关键事实）。

#### 坑 7：Unity 的 `[UnityMemory]` 输出可忽略

启动时 stdout 会打印一大段 `[UnityMemory] Configuration Parameters`
（Unity 自身的显存分配参数回显），**与 BepInEx 无关**。

#### 安装后目录结构

```
<游戏根>\
  winhttp.dll                  ← Doorstop 代理
  doorstop_config.ini          ← enabled=true, target=BepInEx.Unity.IL2CPP.dll
  .doorstop_version            ← 4.5.0
  dotnet\                      ← 187 个文件，含 coreclr.dll / System.Private.CoreLib.dll
  BepInEx\
    core\                      ← BepInEx + Cpp2IL.Core.dll + 0Harmony + Il2CppInterop.*
    interop\                   ← 114 个 interop 程序集（含 Assembly-CSharp.dll）
    unity-libs\2022.3.62.zip   ← 1,900,589 字节（已修复）
    plugins\                   ← 插件放这里（当前为空）
    patchers\
    ErrorLog.log / LogOutput.log
  _backup_bepinex_*\           ← 安装前状态清单（可用 -Uninstall 回滚）
```

### 13.13 下一步：写第一个插件

**技术前提已全部满足**：`Assembly-CSharp.dll` 可读，目标类签名可见。

建议顺序（对齐"空格升 / Shift 降 = 成功"的验收标准）：

| 步骤 | 内容 | 验收 |
|---|---|---|
| 0 | 用 dnSpy/ILSpy 读 `PlayerControlCustom` / `MoveControlHelper` 的**字段与方法签名** | 找到消费 `Move` 的入口 |
| 1 | 第一个插件：**只打印**（确认 hook 生效、能拿到玩家对象） | 日志出现自定义输出 |
| 2 | 加垂直输入：Hook 移动计算，注入 Y 分量 | **空格升 / Shift 降** |
| 3 | 中键切 `AutoLock` 状态 → 免锁定攻击模式 | 可选，后续 |

**必须先做步骤 0**：interop 里的方法是**桩**（签名完整、**无 IL 体**），
真正要改的行为在原生代码里，需靠 Harmony patch 在运行时替换。
所以要先从 interop 读出准确签名，才能写 patch。

## 十四、工具

```powershell
$py = <python>
$env:IL2CPP_METADATA = '<游戏>\AldealRaysR18_Data\il2cpp_data\Metadata\global-metadata.dat'
$env:UNITY_DATA_DIR  = '<游戏>\AldealRaysR18_Data'

# 按功能层定位（镜头/飞行/姿态轴/锁定/边界）
& $py il2cpp_meta_probe.py

# 自研代码 vs 第三方插件分流；列出自研目录全部脚本
& $py il2cpp_own_code_probe.py --self-dir AidealRaysCustom

# 调试面挖掘（DebugMonitor / 自由相机 / 时间缩放 / 构建配置）
& $py il2cpp_debug_surface.py --top 12

# 提取完整 InputActions 按键绑定表
& $py extract_inputactions.py

# 按键占用体检（改键前先跑，避免与现有绑定冲突）
& $py key_occupancy.py --keys "space,shift,middle"

# 成员簇提取（还原某类型的接口）
& $py il2cpp_member_cluster.py --anchor PlayerControlCustom --window 450
& $py il2cpp_member_cluster.py --anchor VCameraSelectManager --filter Camera

# BepInEx 6 安装器
$env:AIDEAL_RAYS_ROOT = '<游戏根>'
powershell -NoProfile -ExecutionPolicy Bypass -File install_bepinex6.ps1 -DryRun
```

Python 工具全部只读（`rb` 打开）；安装器默认 DryRun 安全，写入前会先落备份清单。

## 十五、验证记录

- 六个 Python 脚本 `py_compile` 通过；无参数调用返回退出码 2。
- `il2cpp_meta_probe.py`：110,680 条字符串段，魔数 `0xFAB11BAF` 校验通过。
- `il2cpp_own_code_probe.py --self-dir AidealRaysCustom`：列出 73 个自研脚本。
- `il2cpp_debug_surface.py`：`DebugMonitor` 3 次出现；`ScriptingAssemblies.json` 含
  `Assembly-CSharp.dll`；`RuntimeInitializeOnLoads.json` 为 0 字节（如实记录）。
- `extract_inputactions.py`：提取 44,262 字节 InputActions JSON，2 图 76 绑定；垂直轴关键词 0 命中。
- `key_occupancy.py`：空格/Shift 未被占用；中键已被 `Function` 占用（并给出两个假警报的澄清）。
- `il2cpp_member_cluster.py`：还原 4 个类成员簇；**据此推翻了一条错误线索**（`HeightOffset` 实为 Shader 属性）。
- `install_bepinex6.ps1`：语法检查通过；**DryRun 三次迭代修掉 3 个真 bug**，最终干净通过（3.8 秒、零写入）；
  产物 URL 实测 HTTP 200 / 34,336,405 字节。
- **用户实测**：DebugMonitor 热键组合全部无反应 → 该路线终止（有实测依据）。
- **未运行游戏进程**；所有"能否实现"的结论均为静态推断 + 用户体感印证，待运行时验证。

