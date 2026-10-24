# 驱动妖精 · 3D 飞行改造 · 阶段 1 实测结论

**这一节是整条链路里最关键的实证**，因为它否决了一个看起来最自然的假设。

## 实测数据（`PlayerControlCustom` 运行期采样）

```
[IN ] horizon=0.000  vertical=0.000
[IN ] horizon=1.000  vertical=0.000
[IN ] horizon=0.707  vertical=0.707
[IN ] horizon=0.000  vertical=1.000
[IN ] horizon=0.000  vertical=-1.000

[MOVE] x=0.329 y=0.000 z=0.947   (输入 h=0.707 v=0.707)
[MOVE] x=-0.156 y=0.000 z=0.988  (输入 h=1.000 v=0.000)
[MOVE] x=-0.718 y=0.000 z=0.696  (输入 h=0.000 v=1.000)
[MOVE] x=0.914 y=0.000 z=0.407   (输入 h=0.000 v=-1.000)
[MOVE] x=0.225 y=0.000 z=0.974   (输入 h=0.707 v=-0.707)
```

## 结论 ①：`vertical` **不是恒为 0** —— 原假设被否决

改造前的方案文档里我预测：「`EvaluateMoveAndAttack` 的 `vertical` 恒为 0，
所以游戏根本没有垂直输入」。**实测否定了这个预测**：

| 原预测 | 实测 |
|---|---|
| `vertical` 恒为 0 | **在 -1.000 ~ +1.000 之间变化** |

## 结论 ②：`vertical` 是**摇杆前后轴**，映射到 **Z**，与高度无关

| 输入 `(horizon, vertical)` | 输出 `Move (x, y, z)` | x²+z² |
|---|---|---|
| `(1.000, 0.000)` | `(-0.156, 0, 0.988)` | 0.999 |
| `(0.707, 0.707)` | `( 0.329, 0, 0.947)` | 0.999 |
| `(0.000, 1.000)` | `(-0.718, 0, 0.696)` | 0.999 |
| `(0.000, -1.000)` | `( 0.914, 0, 0.407)` | 0.999 |

- **`horizon` → X，`vertical` → Z，`y` 全程 = 0.000**
- 游戏内部把"摇杆前后"命名为 `vertical` —— **这是命名陷阱，与"垂直高度"毫无关系**

**因此：绝不能通过改 `vertical` 参数来注入高度（会破坏前后移动）。**
幸好这个假设在阶段 1 就被否决，没有直接写进阶段 2。

## 结论 ③：`Move` 是**归一化方向向量**，不是速度

所有样本的 `x² + z² ≈ 1.000`。说明 `Move` 表示**方向**；
实际位移量由 `ApproachForm(pointList, duration)` 的 `duration` 控制。

**这反而是好消息**：Y 分量空着（恒 0），往里填值就得到一个合法的三维方向向量，
不必关心速度大小。

## 结论 ④：`Move` 在托管代码里**无人读取**

对 `PlayerControlCustom.cs`（822 KB）与 `CharacterControllerBase.cs`（673 KB）
全文检索 `Move` 的读取处 —— **零命中**。

→ 消费 `Move` 的是**原生实现**。这意味着：
- ✅ 改 `Move` 不会破坏其他托管逻辑（它是"纯输出"）
- ⚠️ **无法静态证明**改了 `Move.y` 就会让角色升空 —— 取决于原生侧何时读取该字段

这是阶段 2 的**已知不确定性**，如实记录。判别方式：看日志 `[MOVE]` 行的
`appliedY` 是否非 0（确认我们写进去了）；若非 0 但角色不动 → 原生侧没消费 →
改走路线 B（直接调 `CharacterController.Move()`）。

## 阶段 2 的实现（路线 A）

```
PlayerControlCustom.Update 之后：
    Move = (原x, 垂直输入 × VerticalAmount, 原z)
    空格 = +1，左Shift = -1，都不按 = 0（回到游戏原状）
```

- 水平分量原样保留 → 不影响游戏自己的移动
- 只往 Y 填值 → 新增"方向上的高度"
- 幅度做成**配置项** `VerticalAmount`（写在 `BepInEx\config\com.aidealrays.flight.cfg`），
  **调参不必重新编译**

## 编译踩到的坑（已解决，记录下来）

| 错误 | 根因 | 修法 |
|---|---|---|
| `CS0012: 类型"Object"在未引用的程序集中定义，必须添加对程序集"Il2Cppmscorlib"的引用` | IL2CPP 的 interop 类型体系基于 `Il2CppSystem` 而非标准 `System`；`KeyCode` 等类型定义在其中 | 引用 interop 目录下的 `Il2Cppmscorlib.dll` / `Il2CppSystem.dll` / `Il2CppSystem.Core.dll` |
| dll 被占用无法覆盖 | 游戏正在运行会锁住 `BepInEx\plugins\*.dll` | 部署前先关游戏 |

## 其他记录

- **日志读取编码**：BepInEx 写的是 UTF-8。用 PowerShell `Get-Content` 默认（ANSI/GBK）
  读会显示 `姝ｅ湪杞藉叆` 这类乱码 —— **是读取端的问题，日志本身正常**。
  必须用 `-Encoding UTF8` 读。（我此前多次误判为"游戏日志乱码"，实为自己读错。）
- 插件日志已改为**英文**，从根上避免编码歧义。
- 阶段 1 的完整观测日志已留存：`phase1_observation.log`（35,664 字节）
