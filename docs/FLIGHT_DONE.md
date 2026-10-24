# 驱动妖精 · 3D 飞行改造 · 已完成（阶段 3）

**状态：✅ 成功。用户实测确认「完美!成功起飞与下降」。**

## 最终有效方案

**直写 `Transform.position`**（插件 `AidealRays.Flight` v0.4.0）

```csharp
// PlayerControlCustom.Update 之后
float dt = Min(Time.deltaTime, MAX_FRAME_SECONDS);
float dy = vertical * VerticalSpeed * dt;
Transform t = player.transform;
Vector3 p = t.position;
t.position = new Vector3(p.x, p.y + dy, p.z);   // 只动 Y，水平交给游戏自己
```

- 空格 = +1，左Shift = -1，都不按/同时按 = 0（回到原版）
- `VerticalSpeed` 是配置项（默认 6.0），写在 `BepInEx\config\com.aidealrays.flight.cfg`，
  **调手感不必重新编译**

## 实测验证（`routeD_success.log`，118 个采样）

对日志做分组统计（按 `vertical` 值分组，看平均帧间高度变化 `d`）：

| `vertical` | 样本数 | 平均 `d` | 高度范围 | 判定 |
|---|---|---|---|---|
| **+1.000**（空格） | 35 | **+1.104** | -0.046 ~ 34.490 | **上升 ✓** |
| **-1.000**（Shift） | 6 | **-1.458** | -1.022 ~ 11.941 | **下降 ✓** |
| 0.000 | 77 | — | — | 无输入（含场景切换的跳变，见下方说明） |

高度连续爬升的例子（同一轮按空格）：

```
worldY=1.148  d=1.136   | applied=0.067 | RigidObjY=1.082
worldY=2.792  d=1.644   | applied=0.033 | RigidObjY=2.626
worldY=4.300  d=1.508   | applied=0.033 | RigidObjY=4.267
worldY=6.836  d=1.156   | applied=0.033 | RigidObjY=6.670
worldY=8.149  d=1.312   | applied=0.033 | RigidObjY=7.982
worldY=9.042  d=0.894   | applied=0.067 | RigidObjY=8.976
```

**关键：`d` 持续为正且量级稳定 → 写入的高度没有被原生代码覆盖。**

### 数字口径说明（避免误读）

- `applied` = **单帧**位移量（≈ `VerticalSpeed × deltaTime` ≈ 0.03~0.07）
- `d` = **相邻两个采样点**（间隔 30 帧）之间的高度差（≈ 1.0~1.6）

两者时间尺度不同，不是同一量纲，别拿大小直接比。

## 用户实测反馈（比日志更重要）

> **「完美!成功起飞与下降」**
> **「冲刺锁定，远距离攻击是自动锁定，所以在有高度差的情况下依然可以正常玩的」
> 「抓取在高度差也能搞定」**

**这条比技术验证更有价值**：说明**高度差不破坏战斗系统** ——
锁定、远距离攻击、抓取在三维高度差下都能正常工作。

→ **3D 空战在玩法层成立**，不只是"能飞"而已。这消除了改造前最大的顾虑
（我早先担心过"只有你能飞会让战斗失去张力"，实测表明锁定机制能跟上高度差）。

## 为什么前三条路线失败（记录下来，避免重犯）

| 路线 | 做法 | 失败原因 | 教训 |
|---|---|---|---|
| A | 写 `PlayerControlCustom.Move.y` | 原生移动实现**不消费** `Move` 的 Y 分量（Move 是纯输出字段，只在 XZ 平面被用） | 改了字段 ≠ 改了行为 |
| B | `CharacterController.Move()` | 玩家对象上**根本没有 `CharacterController`**（实测 6 次 not found） | **猜组件存在是错的** |
| C | `GetComponent<Rigidbody>()` | 同样是猜；而且应该用游戏自己的 `Rigid` 字段而非 `GetComponent` | 有现成字段就别猜 |
| **D** | **直写 `Transform.position`** | **成功** | 最基础的 API 最可靠 |

**共同的错误：前三条都是"看起来合理"的假设，没有先取证组件/字段是否真实存在。**
正确做法是先问"这个对象上实际有什么"（查反编译字段 + 运行时探测），再决定改什么。

## 若干观察

### `Rigid` 字段确实存在，且与 Transform 同步

```
RigidObjY=8.976  kin=False  grav=False
```

`CharacterControllerBase.Rigid`（`Rigidbody` 类型）是游戏自己的字段。
实测 `RigidObjY` 始终随 `worldY` 一起变化（略低 0.07~0.13），
说明 **Transform 与刚体位置保持同步，没有发生"两边争夺"** —— 这是路线 D 能稳定的原因。

`kin=False grav=False`（非运动学、不受重力）也解释了为什么能持续爬升而不下落。

### 一处需要留意的异常值（如实记录）

在 `vertical=0` 的分组里，高度范围是 `-500.000 ~ 35.524`。
**`-500.000` 这个值极可能是场景切换/角色重生时的传送**，不是我们的逻辑造成的
（该帧 `vertical=0`，插件不做任何写入）。同理 `35.524` 可能是大招飞升。
这些点混在统计里，所以 `vertical=0` 那组的"平均 d"没有参考意义，**该组数据不要用于判断**。

### 尚未处理

- **中键切换免锁定攻击模式**：用户设计里有这一条（`Function` action 绑在中键），
  机制（`SilentLockon`/`ForceLockoff`/`AutoLock`）已定位，但本轮未实现
- **高级手感**：加速度、惯性、上升上限、落地检测 —— 目前是恒定速度直推
- **敌机垂直机动**：本轮范围为"只做玩家三维化"，AI 未动（用户已确认 AI 不急）

## 环境与工具链（可复用）

| 项 | 值 |
|---|---|
| 游戏 | Unity 2022.3.62f2 / IL2CPP / metadata v31 |
| 加载器 | BepInEx 6.0.0-be.788（bleeding edge，`winhttp.dll` Doorstop 代理） |
| interop | 114 个程序集，`Assembly-CSharp.dll` 5116 KB |
| 插件工程 | `plugin/Flight/AidealRays.Flight.csproj`（引用本地 dll，不用 NuGet） |
| 部署 | 设 `AIDEALRAYS_DEPLOY=1` 后构建即复制到 `BepInEx\plugins` |
| 观测日志 | `routeD_success.log` / `routeB_worldY.log` / `phase1_observation.log` |

### 编译踩坑（已解决）

| 错误 | 根因 | 修法 |
|---|---|---|
| `CS0012: 类型"Object"在未引用的程序集中定义` | IL2CPP interop 基于 `Il2CppSystem` 而非标准 `System` | 引用 `Il2Cppmscorlib.dll` / `Il2CppSystem.dll` / `Il2CppSystem.Core.dll` |
| dll 被占用 | 游戏运行中会锁 `BepInEx\plugins\*.dll` | 部署前先关游戏 |
| 日志中文乱码 | BepInEx 写 UTF-8，PowerShell `Get-Content` 默认按 ANSI/GBK 读 —— **是读取端问题** | 读日志一律加 `-Encoding UTF8`；插件日志改用英文 |
