# 驱动妖精 · 运行期报错诊断报告

来源：`C:\Users\34294\AppData\LocalLow\Riez-ON\AldealRaysR18\Player.log`
规模：**253,429 行**，其中 **550 条 Warning、2 条 Exception、0 条 Error**

## 结论先行

**这些报错全部是游戏自身或第三方 Unity 插件的问题。与 BepInEx 无关，与我的插件无关
（`BepInEx\plugins` 为空，从未部署任何插件）。它们也不影响 3D 飞行改造。**

---

## 逐条诊断（按严重度排序）

### ① `NullReferenceException` ×2 —— 第三方插件 TimelineEvents

```
NullReferenceException: Object reference not set to an instance of an object.
  at Tantawowa.TimelineEvents.EventInvocationInfo.Invoke (Boolean isSingleArg, String value)
  at Tantawowa.TimelineEvents.TimelineEventBehaviour.OnBehaviourPlay (Playable playable, FrameData info)
```

**性质**：游戏自身资产配置错误（非 BepInEx）

**根因链**：
- `Tantawowa.TimelineEvents` 是第三方 Unity 插件，为 Timeline 提供"事件节点"
- `TimelineEventBehaviour.OnBehaviourPlay` 在**时间轴播放到该节点时**被调用
- 它调用 `EventInvocationInfo.Invoke` 去执行配置好的目标方法 → **目标方法为空（null）**
- 于是抛 NRE

**即：作者在 Timeline 里放了事件节点，但没有绑定要调用的方法。**

**位置线索**：这两条出现在 `Unloading 229 unused Assets` / `Unloading 140565 unused Assets`
**之间**，即章节切换时的资产卸载过程中 → 对应**过场/章节切换的 Timeline**。

**影响**：该事件静默不生效。游戏照常继续（日志里紧接着就恢复了）。

**为何不能"修"**：事件绑定关系存在 Timeline 资产里，而资产在**加密的 bundle 内**
（熵 7.997，`IKA9nt.Encrypter` AES-256）。要改必须先解密资产——这是另一个工程量。

---

### ② `DOTween` safe mode 捕获 1 个错误

```
<color=#0099bc><b>DOTWEEN ► </b></color>Target or field is missing/null
    (UnityEngine.Vector3 get_localPosition()) ► Object reference not set to an instance of an object.
  at UnityEngine.Transform.get_localPosition ()
  at DG.Tweening.ShortcutExtensions+<>c__DisplayClass39_0.<DOLocalMoveZ>b__0 ()
  at DG.Tweening.Plugins.Vector3Plugin.EvaluateAndApply (...)
  ...
  at DG.Tweening.Core.DOTweenComponent.Update ()
```

**性质**：游戏自身生命周期 bug

**根因**：一个 `DOLocalMoveZ` 补间（对 Transform 的 Z 轴做本地移动动画）在**目标物体已被销毁后**仍被驱动。
`get_localPosition()` 拿到 null → NRE。

**关键**：`SAFE MODE` 正是 DOTween 用来兜住这类错误的机制，所以
**它被安全捕获，没有崩溃**。日志自己也这么说：

```
DOTween's safe mode captured 1 errors. This is usually ok
- 1 missing target or field errors
```

**影响**：**无**。这是设计内的容错路径。

---

### ③ 字体缺字 ×9 —— MSYH SDF 字体集不含"脑"字

```
The character with Unicode value \u8111 was not found in the [MSYH SDF] font asset
or any potential fallbacks. It was replaced by Unicode character \u25A1 in text object [Description].
```

- `\u8111` = **脑**，`\u25A1` = **□**
- 字体资产 `MSYH SDF`（微软雅黑，烘焙成 Static SDF 图集）
- **Static SDF 图集在构建时就固定了字符集**，运行时遇到图集里没有的字 → 替换成 □

**已排除的原因**：**没有装任何汉化/翻译插件**
（`Translation` / `AutoTranslator` / `Config` / `ReiPatcher` 目录均不存在）

→ **是游戏自带的字体集不全**，出现在 `[Description]` 这个文本对象上（角色描述）。

**影响**：纯显示问题，只影响个别字的显示。

---

### ④ `Animator.GotoState: State could not be found` ×14 + `Invalid Layer Index '-1'` ×14

**性质**：游戏自身代码 bug

**根因**：代码调用了 `Animator.Play(stateHash, layerIndex, ...)` 或 `CrossFade`，
传入的 `layerIndex = -1` —— **-1 不是合法的层索引**，说明：
- 要么代码里写死了 -1（占位没改）
- 要么 Animator 尚未初始化就调用

**影响**：对应的那一次动画切换不会执行。出现 14 次，是**同一个逻辑被反复触发**。

---

### ⑤ `PlayOneShot was called with a null AudioClip` ×1

```
  at UnityEngine.AudioSource:PlayOneShot(AudioClip)
```

**性质**：游戏自身资源配置缺失

**根因**：某个 `AudioSource.PlayOneShot(clip)` 的 `clip` 字段没赋值。
典型的作者漏配。**影响：那一次音效不响。**

---

### ⑥ `Failed Allocations. Bucket layout: ... Failed count: 1859236` —— 不是错误

**这一条要特别说明，因为它看起来最吓人。**

它出现在 Unity 退出时打印的 `[ALLOC_DEFAULT]` 内存分配器统计里：

```
Peak Allocated bytes 4.0 MB
  [ALLOC_BUCKET]
    Large Block size 4.0 MB
    Used Block count 1
    Peak Allocated bytes 4.0 MB
      Failed Allocations. Bucket layout:
        16B: 12 Subsections = 12288 buckets. Failed count: 1859236
```

**`Failed count` 在此语境下是分配器内部计数**（bucket 满了之后走大块分配的次数），
**不是"内存分配失败导致崩溃"**。证据：峰值分配仅 4.0 MB，游戏跑完整个流程没崩。

**影响：无。** 这是诊断统计信息，不是报错。

### ⑦ 其他次要提示

| 消息 | 性质 | 影响 |
|---|---|---|
| `JobTempAlloc has allocations that are more than the maximum lifespan of 4 frames old - this is not allowed and likely a leak` | Unity 建议性提示 | 建议用 `-diag-job-temp-memory-leak-validation` 排查，但**非致命** |
| `Color primaries 0 is unknown or unsupported by WindowsMediaFoundation...` | 视频播放在 WMF 上的色域回退 | 可能有轻微色偏 |
| `Material 'FloatingWindow' with Shader 'Knife/Hologram Shader Unlit' doesn't have a texture property '_MainTex'` | 材质用了不含 `_MainTex` 的 shader | 外观类，游戏自带 |
| `Input System module state changed to: Shutdown` | 正常关闭流程 | 无 |

---

## 与 3D 飞行改造的关系

**零影响。** 理由：

1. 全部报错来自 **Timeline 事件配置、字体图集、Animator 层索引、音频字段** —— 都是
   资产/配置层问题，与**玩家移动代码**（`PlayerControlCustom` → `InputDirect` → `ApproachForm`）无关
2. 2 条 Exception 都被容错路径兜住（DOTween safe mode / Timeline 事件静默失败），**未崩溃**
3. 这些报错在**装 BepInEx 之前就存在**，属于游戏原生状态

## 是否值得修

| 报错 | 能否修 | 建议 |
|---|---|---|
| Timeline NRE | 需解密 bundle 改 Timeline 资产 | **不建议**（性价比极低） |
| DOTween safe mode | 已被容错 | **不需要** |
| 字体缺字 | 需换字体资产（在加密 bundle 内） | **不建议** |
| Animator 层索引 -1 | 需改 IL2CPP 原生代码 | 可用 Harmony patch，但**收益极小** |
| 空 AudioClip | 需改资产 | **不建议** |

**结论：这五类报错都不影响功能，且修复成本远高于收益。**
它们唯一的实际价值是——**确认了游戏资产是加密的**（因为要改它们都得先解密）。

## 工具

```powershell
$env:UNITY_PLAYER_LOG = '<LocalLow>\Riez-ON\AldealRaysR18\Player.log'
& $py player_log_diag.py --max-samples 3 --stack-lines 16
```

只读（`rb` 打开），按行扫描 + 异常归类 + 堆栈提取。不含路径字面量。

## 验证记录

- `player_log_diag.py` 语法检查通过；无参数调用返回退出码 2
- 实跑：253,429 行 → 归类出 2 条 NullReferenceException（含完整堆栈），
  关键字命中统计正确（字体 9 次 / Animator 14 次 / 音频 1 次 / 材质 1 次）
- **交叉验证**：另用 PowerShell 独立统计一遍，Top 频次与工具输出一致
- 已确认 `BepInEx\plugins` 为空目录（无任何插件被部署）
