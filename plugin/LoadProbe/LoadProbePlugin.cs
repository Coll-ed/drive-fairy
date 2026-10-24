// 驱动妖精 AI-deal-Rays · 加载探针（独立诊断插件）
//
// 目的：回答"加载慢/卡住时，游戏内部的 Unity 主循环到底在不在跑"。
//
// 为什么需要它：BepInEx 日志只在插件钩子或 Unity 自己的 Debug.Log 被调用时才有输出，
// 而资源加载期间两者都可能长时间静默 —— 于是"主循环仍在推进但很慢"和"主循环真死了"
// 在日志上长得一模一样。用户报的"慢得和卡死没区别"正是这个歧义。
//
// 本探针的判据（关键）：
//   · 心跳协程每秒写一条：若它持续输出 → **主循环活着**（哪怕画面不动）→ 是"慢"不是"死"
//   · 若心跳停止输出且日志无异常 → 主循环被阻塞 → 才是"死"
//   · 同时记录帧时间、场景、内存、焦点、GC，用于定位时间花在哪
//
// 输出：独立日志文件，不污染游戏/BepInEx 主日志。
// 路径不写死：默认用 BepInEx 的根目录常量拼出文件名，并可由配置覆盖。

using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AidealRays.LoadProbe;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class LoadProbePlugin : BasePlugin
{
    public const string PluginGuid = "com.aidealrays.loadprobe";
    public const string PluginName = "AidealRays Load Probe";
    public const string PluginVersion = "0.1.0";

    // ---- 采样参数（具名 + 理由）----
    // 心跳间隔 1 秒：足够分辨"每秒都在动"与"完全停住"，
    // 又不会让日志膨胀到影响加载本身（每秒约 1 行）
    private const float SAMPLE_INTERVAL_SECONDS = 1f;
    // 判定"长帧"的阈值（毫秒）。500ms 以上人眼明显可见卡顿，值得单独记一行
    private const float LONG_FRAME_MS = 500f;
    // 判定"内存骤降"的阈值（MB）：资源卸载会造成明显下降，
    // 用它标记"刚完成一轮卸载"这个关键时刻
    private const long MEMORY_DROP_MB = 50;
    // 探针日志文件名（目录由 BepInEx 根目录常量提供，见 Load）
    private const string LOG_FILE_NAME = "AidealRaysLoadProbe.log";

    internal static new ManualLogSource Log = null!;

    private static ConfigEntry<string> _cfgLogPath = null!;
    private static StreamWriter? _writer;
    private static readonly object WriteLock = new();

    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"{PluginName} v{PluginVersion} loading");

        // 日志位置外置：默认落在 BepInEx 根目录下，可由配置覆盖
        string defaultPath = Path.Combine(Paths.BepInExRootPath, LOG_FILE_NAME);
        _cfgLogPath = Config.Bind(
            "Probe", "LogPath", defaultPath,
            "Where the load probe writes its samples. Defaults to a file under the " +
            "BepInEx root directory, named AidealRaysLoadProbe.log.");

        try
        {
            _writer = new StreamWriter(_cfgLogPath.Value, append: false, Encoding.UTF8)
            {
                AutoFlush = true,   // 崩溃/强杀时也能保住已写内容
            };
        }
        catch (Exception ex)
        {
            Log.LogError($"cannot open probe log '{_cfgLogPath.Value}': {ex.Message}");
            return;
        }

        Write($"=== load probe start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Write($"unity={Application.unityVersion} runInBackground={Application.runInBackground}");

        // 心跳由 Unity 主循环驱动的协程产出 —— 正是我们想观测的东西。
        // 用 ClassInjector 注册托管类型到 IL2CPP 域后才能 AddComponent。
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<LoadProbeBehaviour>();
            var go = new UnityEngine.GameObject("AidealRaysLoadProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<LoadProbeBehaviour>();
            Write("heartbeat behaviour attached (DontDestroyOnLoad)");
            Log.LogInfo($"probe attached, log -> {_cfgLogPath.Value}");
        }
        catch (Exception ex)
        {
            Log.LogError($"attach heartbeat failed: {ex.Message}");
            Write($"ERROR attach heartbeat failed: {ex}");
        }
    }

    /// <summary>写一行探针日志（线程安全）。</summary>
    internal static void Write(string line)
    {
        if (_writer is null)
        {
            return;
        }
        lock (WriteLock)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception ex)
            {
                // 边界显式记录：写失败要能被看见，但不能因为写日志再抛异常
                Log.LogError($"probe write failed: {ex.Message}");
            }
        }
    }

    internal static float SampleInterval => SAMPLE_INTERVAL_SECONDS;
    internal static float LongFrameMs => LONG_FRAME_MS;
    internal static long MemoryDropMb => MEMORY_DROP_MB;
}

/// <summary>
/// 心跳组件：每秒采一条，并单独记录长帧。
/// 它是否持续输出，就是"Unity 主循环是否活着"的判据。
/// </summary>
public sealed class LoadProbeBehaviour : UnityEngine.MonoBehaviour
{
    private float _accum;
    private float _lastSampleRealtime;
    private long _lastMemoryMb = -1;
    private int _lastFrameCount = -1;
    private bool _lastFocused = true;
    private string _lastScene = "";

    public LoadProbeBehaviour(IntPtr ptr) : base(ptr)
    {
    }

    private void Start()
    {
        LoadProbePlugin.Write($"first frame after attach: frameCount={Time.frameCount}");
        _lastSampleRealtime = Time.realtimeSinceStartup;
        _accum = 0f;
    }

    private void Update()
    {
        // 长帧单独记录：出现 >= LONG_FRAME_MS 的帧说明主线程被某件事卡住了
        float dtMs = Time.unscaledDeltaTime * 1000f;
        if (dtMs >= LoadProbePlugin.LongFrameMs)
        {
            LoadProbePlugin.Write($"LONGFRAME dt={dtMs:F0}ms frame={Time.frameCount}"
                                  + $" scene={SafeSceneName()}");
        }

        _accum += Time.unscaledDeltaTime;
        if (_accum < LoadProbePlugin.SampleInterval)
        {
            return;
        }
        _accum = 0f;

        float now = Time.realtimeSinceStartup;
        float sinceLast = now - _lastSampleRealtime;
        _lastSampleRealtime = now;

        long memMb = GC.GetTotalMemory(false) / (1024 * 1024);
        int frames = Time.frameCount;
        int frameDelta = _lastFrameCount < 0 ? 0 : frames - _lastFrameCount;

        // 内存骤降 = 刚完成一轮资源卸载，是加载流程里的关键节点
        string memNote = "";
        if (_lastMemoryMb >= 0 && _lastMemoryMb - memMb >= LoadProbePlugin.MemoryDropMb)
        {
            memNote = $" MEMDROP -{_lastMemoryMb - memMb}MB";
        }
        _lastMemoryMb = memMb;
        _lastFrameCount = frames;

        bool focused = Application.isFocused;
        string focusNote = "";
        if (focused != _lastFocused)
        {
            focusNote = $" FOCUS {(focused ? "gained" : "lost")}";
            _lastFocused = focused;
        }

        string scene = SafeSceneName();
        string sceneNote = scene != _lastScene ? $" SCENE->{scene}" : "";
        _lastScene = scene;

        LoadProbePlugin.Write($"t={now:F1} frames={frames} (+{frameDelta}/{(sinceLast <= 0f ? 1f : sinceLast):F1}s)"
                              + $" mem={memMb}MB scene={scene}"
                              + $" focused={focused} runInBg={Application.runInBackground}"
                              + sceneNote + focusNote + memNote);
    }

    /// <summary>取当前场景名。异常时返回明确标记，不静默吞掉。</summary>
    private static string SafeSceneName()
    {
        try
        {
            return SceneManager.GetActiveScene().name;
        }
        catch (Exception ex)
        {
            return $"err({ex.Message})";
        }
    }
}
