using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace LightInDark.Core;

/// <summary>日志级别。</summary>
public enum LightLogLevel
{
    /// <summary>调试 —— **只在 Debug 构建下输出**（Release 编译期就没了）。</summary>
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// 本模组日志（<c>&lt;游戏目录&gt;\LightLog.log</c>），并把非 Info 级别**同步**转给 BepInEx 日志。
///
/// ⚠️ **这个类是「主插件」和「API」两个程序集共用的**（定义在 API 里，主插件引用它），
///    所以它**不能直接引用主插件的 <c>ManualLogSource</c>** —— 那会形成循环依赖。
///    做法是暴露 <see cref="BepInExInfo"/> 等**委托钩子**，由某一侧在 <c>Load()</c> 里接上。
///
/// ---- 分级规则（用户 2026-10-06 要求）----
/// | 方法 | 写 LightLog | 写 BepInEx | Release 构建 |
/// |---|---|---|---|
/// | <see cref="Log"/>（<c>toBepInEx:true</c>） | ✅ | ✅ | ✅ |
/// | <see cref="Log"/>(<c>toBepInEx:false</c>) | ✅ | ❌ | ✅ |
/// | <see cref="LogWarning"/> | ✅ | ✅（写死） | ✅ |
/// | <see cref="LogError"/> | ✅ | ✅（写死） | ✅ |
/// | <see cref="LogDebug"/> | ✅ | ✅（写死） | **❌ 整段不编译** |
///
/// ---- 自动限流（解决"日志太多"）----
/// 不去改那 1252 个调用点，而是**按调用点限流**：用 <c>[CallerFilePath]</c> + <c>[CallerLineNumber]</c>
/// 拿到"是哪一行打的"，同一行**每秒最多 <see cref="MaxPerSecondPerSite"/> 条**，超出的丢弃并累计。
/// 被丢弃的条数会在下一次允许输出时附在消息尾部（<c>（本秒另有 N 条同类日志被省略）</c>），
/// 所以**不会静默丢信息**。
///
/// ⚠️ 这一条专治"每帧打一条"这类问题 —— 本工程历史上多次被自己刷屏困住
///    （背景图诊断 / AspectScaler 诊断 / 各种"保图守卫"日志）。
///    **限流是按行号算的，所以不同调用点互不影响**，不需要手工挑哪条该留。
/// </summary>
public static class LightLogger
{
    // =====================================================================
    //  配置
    // =====================================================================

    /// <summary>
    /// 是否输出 <see cref="LogDebug"/>。
    /// **Debug 构建为 true，Release 为 false** —— 而且 Release 下 <see cref="LogDebug"/>
    /// 整个方法体在编译期就被 <c>#if</c> 去掉了，连字符串插值都不会发生。
    /// 运行期也可以改（比如做个"详细日志"开关）。
    /// </summary>
    public static bool DebugEnabled { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>同一个调用点每秒最多输出多少条（超出丢弃并计数）。</summary>
    public const int MaxPerSecondPerSite = 5;

    /// <summary>总开关：关掉后连 LightLog 都不写（给极端性能场景留的后路）。</summary>
    public static bool Enabled { get; set; } = true;

    // =====================================================================
    //  BepInEx 转发钩子（由主插件 / API 在 Load() 里接上）
    // =====================================================================

    public static Action<string>? BepInExInfo;
    public static Action<string>? BepInExWarning;
    public static Action<string>? BepInExError;

    private static void Forward(Action<string>? hook, string message)
    {
        if (hook == null) return;
        try { hook(message); }
        catch { }   // 转发失败绝不影响主流程
    }

    // =====================================================================
    //  文件
    // =====================================================================

    private static readonly string LogFilePath;
    private static readonly string StartupTime;

    /// <summary>
    /// ⚠️ 常驻写入器。
    /// 原来每条日志都 <c>File.AppendAllText</c> —— **每条开关一次文件**，
    /// 1252 个调用点里有不少是每帧打的，那是实打实的性能损耗。
    /// 现在用常驻 <see cref="StreamWriter"/>（<c>AutoFlush = true</c>，崩了也不丢最后几行）。
    /// </summary>
    private static StreamWriter? _writer;
    private static readonly object WriteLock = new();

    static LightLogger()
    {
        StartupTime = DateTime.Now.ToString("yyyy/M/d HH:mm:ss");

        string gameRootPath = Path.Combine(
            Directory.GetParent(Application.dataPath).FullName, "LightLog.log");
        string determinedPath;

        try
        {
            using (File.Open(gameRootPath, FileMode.OpenOrCreate, FileAccess.Write)) { }
            determinedPath = gameRootPath;
        }
        catch
        {
            string localLow = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "..", "LocalLow", "Innersloth", "Among Us");
            determinedPath = Path.Combine(Path.GetFullPath(localLow), "GiftLog.log");
            string directory = Path.GetDirectoryName(determinedPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }

        LogFilePath = determinedPath;

        try
        {
            _writer = new StreamWriter(LogFilePath, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        catch { _writer = null; }

        AppendRaw("=====Log Started " + StartupTime + "=====");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    // =====================================================================
    //  限流
    // =====================================================================

    private sealed class SiteState
    {
        public double WindowStart;
        public int Count;
        public int Dropped;
    }

    private static readonly Dictionary<string, SiteState> Sites = new(StringComparer.Ordinal);

    /// <summary>返回 true = 允许输出；<paramref name="droppedNote"/> 是"本秒被省略了几条"的提示（可能为空）。</summary>
    private static bool Allow(string site, out string droppedNote)
    {
        droppedNote = "";
        try
        {
            double now = Time.realtimeSinceStartup;

            if (!Sites.TryGetValue(site, out var st))
            {
                st = new SiteState { WindowStart = now, Count = 0, Dropped = 0 };
                Sites[site] = st;
            }

            if (now - st.WindowStart >= 1.0)
            {
                // 新的一秒：把上一秒丢掉的条数报出来，避免"静默丢信息"
                if (st.Dropped > 0)
                    droppedNote = $"（上一秒另有 {st.Dropped} 条同类日志被省略）";
                st.WindowStart = now;
                st.Count = 0;
                st.Dropped = 0;
            }

            if (st.Count >= MaxPerSecondPerSite)
            {
                st.Dropped++;
                return false;
            }

            st.Count++;
            return true;
        }
        catch
        {
            return true;    // 限流自己出错就一律放行
        }
    }

    // =====================================================================
    //  对外 API
    // =====================================================================

    /// <summary>
    /// 普通信息。
    /// </summary>
    /// <param name="toBepInEx">
    /// 是否**同时**写到 BepInEx 日志（默认 true）。
    /// 那些"只在 LightLog 里有意义、BepInEx 里看着啰嗦"的条目传 false。
    /// </param>
    public static void Log(string message = "",
        bool toBepInEx = true,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        message ??= "日志值为null";
        if (!Enabled) return;

        if (!Allow(Site(file, line), out var note)) return;
        if (note.Length > 0) message += note;

        AppendRaw($"[{DateTime.Now:yyyy/M/d HH:mm:ss}]: \"{message}\"");
        if (toBepInEx) Forward(BepInExInfo, message);
    }

    /// <summary>警告。**写死转发到 BepInEx**（不看任何开关）。</summary>
    public static void LogWarning(string message = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        message ??= "日志值为null";
        if (!Enabled) return;

        if (!Allow(Site(file, line), out var note)) return;
        if (note.Length > 0) message += note;

        AppendRaw($"[Warning-{DateTime.Now:yyyy/M/d HH:mm:ss}]: \"{message}\"");
        Forward(BepInExWarning, message);
    }

    /// <summary>错误。**写死转发到 BepInEx**（不看任何开关）。</summary>
    public static void LogError(string message = "", Exception? ex = null,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        message ??= "日志值为null";
        if (!Enabled) return;

        // ⚠️ 错误**不限流** —— 出错时宁可刷屏也要让人看见
        string detail = message;
        if (ex != null) detail += $"\n堆栈->{ex}";

        AppendRaw($"[Error-{DateTime.Now:yyyy/M/d HH:mm:ss}]: \"{message}\"\n堆栈->{ex}");
        Forward(BepInExError, detail);
    }

    /// <summary>
    /// 调试。**只在 Debug 构建下存在** —— Release 编译时整个方法体（含字符串插值）都被去掉。
    /// 写死转发到 BepInEx。
    /// </summary>
    [Conditional("DEBUG")]
    public static void LogDebug(string message = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        // ⚠️ [Conditional("DEBUG")] 让**调用点**在 Release 下整条被编译器删掉 ——
        //    连 $"..." 插值都不会执行。这比"运行期判 if (DebugEnabled)"更彻底。
        if (!DebugEnabled) return;
        message ??= "日志值为null";

        if (!Allow(Site(file, line), out var note)) return;
        if (note.Length > 0) message += note;

        AppendRaw($"[Debug-{DateTime.Now:yyyy/M/d HH:mm:ss}]: \"{message}\"");
        Forward(BepInExInfo, "[Debug] " + message);
    }

    /// <summary>把"文件+行号"拼成限流用的站点 key。</summary>
    private static string Site(string file, int line)
    {
        if (string.IsNullOrEmpty(file)) return "?:" + line;
        int slash = file.LastIndexOfAny(new[] { '\\', '/' });
        string name = slash >= 0 ? file.Substring(slash + 1) : file;
        return name + ":" + line;
    }

    // =====================================================================
    //  写入
    // =====================================================================

    static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        string exceptionName = (e.ExceptionObject as Exception)?.GetType().Name ?? "UnknownException";
        AppendRaw($"Error<{DateTime.Now:HH:mm:ss}>: {exceptionName}");
    }

    /// <summary>直接写一行（不经过级别/限流）。启动横幅、未处理异常走这里。</summary>
    private static void AppendRaw(string content)
    {
        try
        {
            lock (WriteLock)
            {
                if (_writer != null)
                {
                    _writer.WriteLine(content);
                }
                else
                {
                    File.AppendAllText(LogFilePath, content + Environment.NewLine);   // 兜底
                }
            }
        }
        catch { }
    }

    /// <summary>清空日志文件（Release 启动时用）。</summary>
    public static void ClearLog()
    {
        try
        {
            lock (WriteLock)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = new StreamWriter(LogFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
            }
        }
        catch { }
    }
}
