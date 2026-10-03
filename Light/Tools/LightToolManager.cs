using System;
using System.Collections;
using System.IO;
using System.Text.Json;
using LightInDark.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Light.Tools;

/// <summary>
/// 模组外部工具（<c>&lt;游戏根目录&gt;\Light_Data\Tools</c>）的检查 / 下载 / 远端版本比对。
///
/// ⚠️ 两个 URL 目前的实测状态（2026-10-03）：
///    - <c>https://lid.moonscar.cn/</c>          → 200，标题「维护中」
///    - <c>.../light/version.json</c>            → **404**
///    - <c>.../download/LightUpdater.exe</c>     → **404**
///    所以 <see cref="RemoteVersion"/> 的 JSON 字段名是**我猜的**：
///    这里刻意做成"多种键名都认"的容错解析，等站点上线后如果对不上，
///    日志里会打出原始 JSON，照着改 <see cref="PickStr"/> 的候选表即可。
/// </summary>
public static class LightToolManager
{
    // ======================= 常量 =======================

    /// <summary>工具可执行文件名（用户本轮指定，注意和旧的 LightInDarkUpdater.exe 不是一个）。</summary>
    public const string UpdaterExeName = "LightUpdater.exe";

    /// <summary>
    /// 下载地址。用户给的是**带尾斜杠**的 <c>.../LightUpdater.exe/</c>，
    /// 这个写法在大多数静态服务器上会 404，所以两个都试（先去尾斜杠）。
    /// </summary>
    private static readonly string[] DownloadUrls =
    {
        "https://lid.moonscar.cn/download/" + UpdaterExeName,
        "https://lid.moonscar.cn/download/" + UpdaterExeName + "/",
    };

    /// <summary>远端版本清单。</summary>
    public const string VersionUrl = "https://lid.moonscar.cn/light/version.json";

    /// <summary>按 Ctrl 打开的地址（只要这一个仓库，用户明确说了）。</summary>
    public const string GitHubLatestUrl = "https://github.com/Moon-Scar-Studio/LightInDark/releases/latest";

    /// <summary>version.json 很小，用总时长超时即可。</summary>
    private const int VersionTimeoutSeconds = 20;

    /// <summary>
    /// 下载更新器的**卡死**判定：连续这么多秒没有新字节才算失败。
    ///
    /// ⚠️⚠️ 这里**不能用 UnityWebRequest.timeout** —— 那个是「整个请求的总时长」，
    ///    不是空闲超时。实测：12.8 MB 的 exe 只要速度低于 ~850 KB/s，
    ///    就会在下到 10% 左右被硬掐断（用户报的就是这个）。
    ///    所以下载请求一律 timeout=0（不限总时长），改用下面这个"多久没新数据"来判卡死。
    /// </summary>
    private const float DownloadStallSeconds = 30f;

    /// <summary>每个 URL 最多试几次（失败后从头再来）。</summary>
    private const int MaxAttemptsPerUrl = 3;

    // ======================= 路径 =======================

    public static string ToolsDir
    {
        get
        {
            try { return Path.Combine(BepInEx.Paths.GameRootPath, "Light_Data", "Tools"); }
            catch { return "Light_Data\\Tools"; }
        }
    }

    public static string UpdaterPath
    {
        get
        {
            try { return Path.Combine(ToolsDir, UpdaterExeName); }
            catch { return UpdaterExeName; }
        }
    }

    /// <summary>工具是否已就位（文件存在且不是 0 字节）。</summary>
    public static bool UpdaterExists
    {
        get
        {
            try
            {
                var fi = new FileInfo(UpdaterPath);
                return fi.Exists && fi.Length > 0;
            }
            catch { return false; }
        }
    }

    // ======================= 下载 =======================
    //
    // ⚠️⚠️ **这里刻意不用 UnityWebRequest。**
    //    实测日志原文：
    //      `Marshal 路径失败 ObjectCollectedException: Object was garbage collected in IL2CPP domain
    //       ｜隐式转换也失败 ObjectCollectedException: ...`
    //    两条取字节的路都在**读 `handler.data` 那一行本身**抛 ——
    //    说明这个 IL2CPP 构建里 `DownloadHandler.data` 根本拿不到原生数组
    //    （自己 new DownloadHandlerBuffer 并持有强引用也无效，试过了）。
    //    改用 .NET 自带的 HttpClient：**流式写盘，一个字节都不经过 interop 封送**，
    //    顺带也不用把 13 MB 全塞进内存。

    /// <summary>下载共享状态：后台线程写，主线程读。</summary>
    private sealed class DownloadState
    {
        public string Url = "";
        public string Target = "";
        public long Received;            // 已收到字节（后台写，主线程轮询）
        public long Total = -1;          // 总字节，-1 = 服务端没给
        private System.Threading.CancellationTokenSource? _cts;

        public System.Threading.CancellationToken Token
        {
            get { _cts ??= new System.Threading.CancellationTokenSource(); return _cts.Token; }
        }

        public void Cancel() { try { _cts?.Cancel(); } catch { } }
    }

    /// <summary>后台把 URL 流式下载到 <see cref="DownloadState.Target"/>。</summary>
    private static async System.Threading.Tasks.Task HttpDownloadAsync(DownloadState st)
    {
        using var http = new HttpClient();
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;   // 卡死由主循环判定

        using var resp = await http.GetAsync(st.Url,
            HttpCompletionOption.ResponseHeadersRead, st.Token).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        st.Total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(st.Token).ConfigureAwait(false);
        await using var dst = new FileStream(st.Target, FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 16, useAsync: true);

        var buf = new byte[1 << 16];
        int n;
        while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), st.Token).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), st.Token).ConfigureAwait(false);
            st.Received += n;
        }
    }

    public static IEnumerator DownloadUpdater(Action<long, long, float, float> onTick,
        Action<bool, string> onDone)
    {
        bool ok = false;
        string message = "";

        try { Directory.CreateDirectory(ToolsDir); }
        catch (Exception ex)
        {
            onDone?.Invoke(false, $"创建 Tools 目录失败：{ex.Message}");
            yield break;
        }

        string tmp = UpdaterPath + ".download";
        string bestError = "";
        long bestProgress = 0;

        foreach (var url in DownloadUrls)
        {
            for (int attempt = 1; attempt <= MaxAttemptsPerUrl && !ok; attempt++)
            {
                var st = new DownloadState { Url = url, Target = tmp };
                var task = HttpDownloadAsync(st);

                float lastTime = Time.realtimeSinceStartup;
                float lastDataTime = lastTime;
                float speed = 0f;
                bool stalled = false;

                // ⚠️⚠️ 测速的基线和"卡死检测"的基线**必须是两个变量**。
                //    之前我用同一个 `lastSeen`：先 `lastSeen = got` 再算 `(got - lastSeen)`，
                //    减出来恒为 0 → 速度永远显示 0 kb/s（百分比走 st.Received 所以照常动）。
                long stallBaseline = 0;      // 卡死检测用
                long speedBaseline = 0;      // 测速用

                // ↓↓↓ 这个循环里有 yield，绝不能包在 try/catch 里（CS1626）↓↓↓
                while (!task.IsCompleted)
                {
                    long got = st.Received;
                    long tot = st.Total;

                    if (got > stallBaseline)
                    {
                        stallBaseline = got;
                        lastDataTime = Time.realtimeSinceStartup;
                    }

                    float now = Time.realtimeSinceStartup;
                    float dt = now - lastTime;
                    if (dt >= 0.25f)
                    {
                        speed = (got - speedBaseline) / 1024f / dt;
                        speedBaseline = got;
                        lastTime = now;
                    }

                    onTick?.Invoke(got, tot, tot > 0 ? Mathf.Clamp01(got / (float)tot) : 0f, speed);

                    if (now - lastDataTime > DownloadStallSeconds)
                    {
                        stalled = true;
                        st.Cancel();
                        break;
                    }

                    yield return null;
                }
                // ↑↑↑

                // 取消是异步的，给它一点时间收尾
                if (stalled)
                {
                    float w = 0f;
                    while (!task.IsCompleted && w < 2f) { w += Time.deltaTime; yield return null; }
                }

                // ---- 判结果（无 yield，可以 try/catch）----
                string err = "";
                try
                {
                    if (task.IsFaulted)
                        err = task.Exception?.InnerException?.Message ?? task.Exception?.Message ?? "未知错误";
                    else if (stalled)
                        err = $"下载卡住（{DownloadStallSeconds:F0} 秒没有新数据，只到 {st.Received / 1024} KB）";
                }
                catch (Exception ex) { err = ex.Message; }

                if (err.Length == 0 && !stalled)
                {
                    ok = true;
                    message = "";
                    LightLogger.Log($"[LightTool] 下载完成（{st.Received / 1024} KB）：{url}");
                }
                else
                {
                    message = err.Length > 0 ? err : "未知失败";
                    if (st.Received > bestProgress) { bestProgress = st.Received; bestError = $"{url} → {message}"; }
                    LightLogger.LogWarning($"[LightTool] 第 {attempt}/{MaxAttemptsPerUrl} 次失败：{url} → {message}");
                }

                if (ok)
                {
                    ok = CommitDownload(tmp, out message);
                    break;
                }

                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (attempt < MaxAttemptsPerUrl) yield return new WaitForSeconds(1f);
            }

            if (ok) break;
        }

        if (!ok && string.IsNullOrEmpty(message))
            message = bestError.Length > 0 ? bestError : "所有下载地址都失败了";
        if (!ok) LightLogger.LogWarning($"[LightTool] {UpdaterExeName} 下载失败：{message}");

        onDone?.Invoke(ok, message);
    }

    /// <summary>把临时文件改成正式文件名。</summary>
    private static bool CommitDownload(string tmp, out string message)
    {
        message = "";
        try
        {
            long len = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
            if (len <= 0) { message = "下载到的内容为空"; return false; }

            if (File.Exists(UpdaterPath)) File.Delete(UpdaterPath);
            File.Move(tmp, UpdaterPath);

            LightLogger.Log($"[LightTool] 已落盘 {len / 1024} KB → {UpdaterPath}");
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入失败：{ex.Message}";
            LightLogger.LogError("[LightTool.CommitDownload]", ex);
            return false;
        }
    }

    // ======================= 远端版本比对 =======================

    /// <summary>远端清单解析结果。</summary>
    public sealed class RemoteVersion
    {
        public bool Fetched;
        public string LightVersion = "";
        public string ApiVersion = "";
        public string Raw = "";
        public string Error = "";
    }

    private sealed class TextState
    {
        public string Url = "";
        public string Text = "";
        private System.Threading.CancellationTokenSource? _cts;
        public System.Threading.CancellationToken Token
        {
            get { _cts ??= new System.Threading.CancellationTokenSource(); return _cts.Token; }
        }
        public void Cancel() { try { _cts?.Cancel(); } catch { } }
    }

    private static async System.Threading.Tasks.Task HttpGetTextAsync(TextState st)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(VersionTimeoutSeconds);

        using var resp = await http.GetAsync(st.Url, st.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        st.Text = await resp.Content.ReadAsStringAsync(st.Token).ConfigureAwait(false);
    }

    /// <summary>拉取并解析 version.json（同样走 HttpClient）。</summary>
    public static IEnumerator FetchRemoteVersion(RemoteVersion result)
    {
        var st = new TextState { Url = VersionUrl };
        LightLogger.Log($"[LightTool] 拉取版本清单：{VersionUrl}");

        var task = HttpGetTextAsync(st);
        float t0 = Time.realtimeSinceStartup;
        int timeout = VersionTimeoutSeconds + 5;

        // ↓ yield 循环，不能包 try/catch ↓
        while (!task.IsCompleted)
        {
            if (Time.realtimeSinceStartup - t0 > timeout)
            {
                st.Cancel();
                result.Error = $"超时（{timeout} 秒）";
                LightLogger.LogWarning("[LightTool] 版本清单拉取超时");
                yield break;
            }
            yield return null;
        }
        // ↑

        try
        {
            if (task.IsFaulted)
                result.Error = task.Exception?.InnerException?.Message ?? task.Exception?.Message ?? "未知错误";
            else
                result.Raw = st.Text ?? "";
        }
        catch (Exception ex) { result.Error = ex.Message; }

        if (result.Error.Length > 0)
        {
            LightLogger.LogWarning($"[LightTool] 版本清单拉取失败：{result.Error}");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(result.Raw))
        {
            result.Error = "空响应";
            yield break;
        }

        ParseVersionJson(result.Raw, result);
        yield break;
    }
    /// <summary>
    /// 解析版本清单。
    ///
    /// 用户确认：**格式和本地插件那个 version.json 相同** ——
    /// 也就是 <c>VersionMaker.MakeVersion</c> 写出来的：
    /// <code>
    /// { "Light": "x.x.x", "LightInDark": "x.x.x" }
    /// </code>
    /// 这里仍然保留多键名容错（万一以后加了字段），但主键就是这两个。
    /// </summary>
    private static void ParseVersionJson(string json, RemoteVersion result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            result.LightVersion = PickStr(root, "Light", "light", "LightVersion", "lightVersion", "plugin", "Plugin");
            result.ApiVersion = PickStr(root, "LightInDark", "lightInDark", "LightAPI", "lightApi", "api", "Api", "API");

            // 兜底：整个文件只有一个 version 字段时，当作主插件版本
            if (result.LightVersion.Length == 0 && result.ApiVersion.Length == 0)
                result.LightVersion = PickStr(root, "version", "Version", "latest", "Latest");

            result.Fetched = true;
            LightLogger.Log($"[LightTool] 远端版本：Light={result.LightVersion} LightInDark={result.ApiVersion}");
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            LightLogger.LogWarning($"[LightTool] 版本清单解析失败：{ex.Message}｜原文={Truncate(result.Raw, 300)}");
        }
    }

    /// <summary>
    /// 按候选键名取值。支持 <c>"Light": "1.0.0"</c> 和 <c>"Light": { "version": "1.0.0" }</c> 两种写法。
    /// </summary>
    private static string PickStr(JsonElement root, params string[] keys)
    {
        foreach (var k in keys)
        {
            try
            {
                if (!root.TryGetProperty(k, out var el)) continue;

                if (el.ValueKind == JsonValueKind.String) return (el.GetString() ?? "").Trim();
                if (el.ValueKind == JsonValueKind.Number) return el.GetRawText().Trim();

                // 嵌套对象：往里再找一层 version
                if (el.ValueKind == JsonValueKind.Object)
                {
                    foreach (var inner in new[] { "version", "Version", "latest", "Latest", "value" })
                        if (el.TryGetProperty(inner, out var iv))
                            return iv.ValueKind == JsonValueKind.String
                                ? (iv.GetString() ?? "").Trim()
                                : iv.GetRawText().Trim();
                }
            }
            catch { }
        }
        return "";
    }

    /// <summary>当前版本比远端旧？解析不了就返回 false（宁可漏报，不要误报）。</summary>
    public static bool IsOutdated(string current, string remote)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(remote)) return false;

            string a = current.Trim().TrimStart('v', 'V');
            string b = remote.Trim().TrimStart('v', 'V');

            if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
                return vb > va;

            // 解析不了就比字符串（不相等 = 视为有新版）
            return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
