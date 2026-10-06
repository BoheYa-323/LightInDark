using System;
using System.Collections.Generic;
using System.Text;
using LightInDark.Core;
using UnityEngine;

namespace Light.Diagnostics;

/// <summary>
/// 把 Unity 抛出的异常**连堆栈一起**写进 <c>LightLog.log</c>。
///
/// ---- 为什么需要它 ----
/// BepInEx 控制台里那种
/// <code>
/// [Error : Unity] NullReferenceException: Object reference not set to an instance of an object.
/// </code>
/// **只有一行消息、没有 `at Xxx.Yyy()` 的堆栈** —— 刷屏几千行时根本定位不到是哪一行代码。
/// `Player.log` / `LogOutput.log` 里虽然跟着堆栈，但要在一堆刷屏里翻出"第一次"那条，
/// 实测非常费劲（用户已经贴了好几轮都不带堆栈）。
///
/// ---- 做法：订阅 Unity 官方的日志回调，**完全不碰 Harmony** ----
/// <c>Application.logMessageReceived</c> 的第三个参数之外还有 <c>stackTrace</c>，
/// 那就是完整堆栈。用它取代"patch <c>Debug.Log</c>"那种危险做法 ——
/// 上一轮 patch `Debug.Log` 直接把 `Harmony.PatchAll()` 之后的初始化搞崩、
/// 导致游戏卡在启动页（见 SuppressTimestampSpamPatch 的注释）。
/// 订阅回调则**零风险**：挂不上最多是没日志，绝不会影响启动。
///
/// ---- 去重 ----
/// 同一条堆栈只打印一次（首次），之后只累加计数。
/// 否则这个记录器自己就会变成新的刷屏源。
/// </summary>
public static class ExceptionStackLogger
{
    private static readonly Dictionary<string, int> Seen = new();
    private static readonly object Gate = new();
    private static bool _hooked;

    /// <summary>最多记录多少种不同的堆栈（防止真出大问题时日志爆掉）。</summary>
    private const int MaxUnique = 40;

    public static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        try
        {
            // ⚠️ interop 里 `Application.logMessageReceived` **不是 C# 事件**，
            //    而是暴露成 add/remove 访问器方法（Cecil 查 UnityEngine.CoreModule.dll：
            //    `Void add_logMessageReceived(LogCallback)`），所以不能用 `+=`，
            //    必须直接调 add_ 方法。参数类型 LogCallback 支持从 Action<...> 隐式转换。
            Action<string, string, LogType> handler = OnLog;
            Application.add_logMessageReceived(handler);

            LightLogger.LogDebug("[异常堆栈] 已订阅 Application.logMessageReceived —— " +
                            "之后任何异常都会连完整堆栈写进本日志（每种只打一次）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[ExceptionStackLogger.Hook]", ex);
        }
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        try
        {
            // 只看异常。警告/普通日志不管（`Waiting for time stamp` 那种是 Message，不会进这里）
            if (type != LogType.Exception) return;
            if (string.IsNullOrEmpty(condition)) return;

            string frame = FirstFrame(stackTrace);
            string key = condition + " │ " + frame;

            lock (Gate)
            {
                if (Seen.TryGetValue(key, out int n))
                {
                    Seen[key] = n + 1;
                    return;                       // 已经打过，只计数
                }

                if (Seen.Count >= MaxUnique)
                {
                    Seen["<已达上限，后续堆栈不再记录>"] = 1;
                    return;
                }

                Seen[key] = 1;
            }

            LightLogger.LogError(
                $"[异常堆栈] {condition}\n" +
                $"---------- 完整堆栈 ----------\n{stackTrace}\n" +
                $"------------------------------\n" +
                "（这是该堆栈的**第一次**出现；之后相同堆栈只累加计数、不再重复打印）");
        }
        catch
        {
            // 记录器自己绝不能抛
        }
    }

    /// <summary>取堆栈里的第一帧（用来做去重的 key，也方便一眼看出是谁）。</summary>
    private static string FirstFrame(string st)
    {
        if (string.IsNullOrEmpty(st)) return "(无堆栈)";

        int i = st.IndexOf("at ", StringComparison.Ordinal);
        if (i < 0)
        {
            int nl0 = st.IndexOf('\n');
            string l0 = nl0 > 0 ? st.Substring(0, nl0) : st;
            return l0.Length > 100 ? l0.Substring(0, 100) : l0;
        }

        int j = st.IndexOf('\n', i);
        string line = j > i ? st.Substring(i, j - i) : st.Substring(i);
        line = line.Trim();
        return line.Length > 140 ? line.Substring(0, 140) : line;
    }

    /// <summary>每种堆栈各出现多少次（诊断用）。</summary>
    public static string Summary()
    {
        var sb = new StringBuilder();
        lock (Gate)
        {
            foreach (var kv in Seen) sb.Append($"  x{kv.Value,-6} {kv.Key}\n");
        }
        return sb.Length == 0 ? "  （没有记录到异常）" : sb.ToString();
    }
}
