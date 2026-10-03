using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LightInDark.Core;
using UnityEngine;

namespace Light.UI.MainMenu;

/// <summary>适配模式（对应界面上的 自适应 / 原图 / 拉伸）。</summary>
public enum BackgroundFit
{
    /// <summary>等比填满屏幕，多出来的裁掉（默认）。</summary>
    Cover = 0,
    /// <summary>等比完整显示，留黑边。</summary>
    Contain = 1,
    /// <summary>拉伸到满屏，不保持比例。</summary>
    Stretch = 2,
}

/// <summary>一个背景素材（图片或视频）。</summary>
public sealed class BackgroundEntry
{
    public string FullPath = "";
    public string FileName = "";
    public bool IsVideo;
    public long SizeBytes;
    public string Ext => Path.GetExtension(FileName).ToLowerInvariant();

    public override string ToString() => FileName;
}

/// <summary>
/// 背景素材的来源与本地设置。
///
/// 目录约定（用户指定）：
///   &lt;游戏根目录&gt;\Light_Data\MainBackGround\Image\*.png|jpg|jpeg
///   &lt;游戏根目录&gt;\Light_Data\MainBackGround\Video\*.mp4|avi|webm|mov
///
/// 第一次运行时会把程序集里内嵌的 BG_*.png **解压**到 Image 目录，
/// 之后就不再覆盖 —— 用户可以自由增删自己的图。
/// </summary>
public static class BackgroundStore
{
    // ---- 路径 ----

    public static string RootDir =>
        Path.Combine(BepInEx.Paths.GameRootPath, "Light_Data", "MainBackGround");

    public static string ImageDir => Path.Combine(RootDir, "Image");
    public static string VideoDir => Path.Combine(RootDir, "Video");

    private const string ResourcePrefix = "Light.Resources.MainMenuBackground.BG_";

    public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg" };
    // ⚠️ avi 用户已确认"可以不支持"：Windows 上走 Media Foundation，
    //    能不能放取决于里面的编码，放不了会按"准备超时"退回图片并提示。
    public static readonly string[] VideoExts = { ".mp4", ".avi", ".webm", ".mov", ".m4v" };

    public static bool IsVideoExt(string ext) =>
        Array.IndexOf(VideoExts, ext.ToLowerInvariant()) >= 0;

    public static bool IsImageExt(string ext) =>
        Array.IndexOf(ImageExts, ext.ToLowerInvariant()) >= 0;

    // ---- 设置 ----
    // ⚠️ 设置真正的存储搬去了 AppearanceSettings（<LightUserDataPath>\BackgroundSettings.json）。
    //    这里全部改成**转发**，对外 API 一个字没变，调用方不用动。
    //    为什么要合并：按钮样式/按钮颜色和背景设置都属于"外观"，放一个文件里更好维护。

    /// <summary>选中的素材文件名。空 = 用原版背景；"@random" = 随机。</summary>
    public const string SelectionOff = AppearanceSettings.SelectionOff;
    public const string SelectionRandom = AppearanceSettings.SelectionRandom;

    public static readonly float[] DimLevels = { 0f, 0.2f, 0.4f, 0.6f };

    public static string Selected
    {
        get { AppearanceSettings.EnsureLoaded(); return AppearanceSettings.Selected; }
        set
        {
            AppearanceSettings.EnsureLoaded();
            if (AppearanceSettings.Selected == value) return;
            AppearanceSettings.Selected = value ?? "";
            AppearanceSettings.Save();
        }
    }

    public static BackgroundFit Fit
    {
        get { AppearanceSettings.EnsureLoaded(); return (BackgroundFit)Mathf.Clamp(AppearanceSettings.Fit, 0, 2); }
        set
        {
            AppearanceSettings.EnsureLoaded();
            int v = Mathf.Clamp((int)value, 0, 2);
            if (AppearanceSettings.Fit == v) return;
            AppearanceSettings.Fit = v;
            AppearanceSettings.Save();
        }
    }

    public static float Dim
    {
        get { AppearanceSettings.EnsureLoaded(); return DimLevels[Mathf.Clamp(AppearanceSettings.Dim, 0, DimLevels.Length - 1)]; }
    }

    public static int DimIndex
    {
        get { AppearanceSettings.EnsureLoaded(); return Mathf.Clamp(AppearanceSettings.Dim, 0, DimLevels.Length - 1); }
        set
        {
            AppearanceSettings.EnsureLoaded();
            int v = Mathf.Clamp(value, 0, DimLevels.Length - 1);
            if (AppearanceSettings.Dim == v) return;
            AppearanceSettings.Dim = v;
            AppearanceSettings.Save();
        }
    }

    public static bool HideCrewmates
    {
        get { AppearanceSettings.EnsureLoaded(); return AppearanceSettings.HideCrewmates; }
        set
        {
            AppearanceSettings.EnsureLoaded();
            if (AppearanceSettings.HideCrewmates == value) return;
            AppearanceSettings.HideCrewmates = value;
            AppearanceSettings.Save();
        }
    }

    // ---- 视频音量 ----

    /// <summary>
    /// 视频音量百分比 0~100（步长 10）。
    ///
    /// ⚠️ 之前是 5 档枚举（100% / 跟随音乐 / 70% / 40% / 静音），用户反馈"颗粒度太粗"。
    ///    现在改成 0~100 的整数百分比，左键 +10 循环、右键输入具体值。
    ///    注意字段名也换成了 <c>videoVolumePercent</c> —— 老字段 <c>videoVolume</c> 是枚举语义，
    ///    同名会歧义（新值 3 到底是"3%"还是老的"40%"？），所以直接换名、老值忽略。
    /// </summary>
    public static int VideoVolumePercent
    {
        get { AppearanceSettings.EnsureLoaded(); return Mathf.Clamp(AppearanceSettings.VideoVolumePercent, 0, 100); }
        set
        {
            AppearanceSettings.EnsureLoaded();
            int v = Mathf.Clamp(value, 0, 100);
            if (AppearanceSettings.VideoVolumePercent == v) return;
            AppearanceSettings.VideoVolumePercent = v;
            AppearanceSettings.Save();
        }
    }

    public static string VideoVolumeLabel => $"视频音量 {VideoVolumePercent}%";

    /// <summary>左键 +10（100 → 0 循环）。</summary>
    public static int NextVideoVolume()
    {
        int next = VideoVolumePercent >= 100 ? 0 : VideoVolumePercent + 10;
        VideoVolumePercent = next;
        return next;
    }
    // ---- 主界面按钮样式（转发） ----

    public static MainButtonStyle ButtonStyle
    {
        get { AppearanceSettings.EnsureLoaded(); return AppearanceSettings.ButtonStyle; }
        set
        {
            AppearanceSettings.EnsureLoaded();
            if (AppearanceSettings.ButtonStyle == value) return;
            AppearanceSettings.ButtonStyle = value;
            AppearanceSettings.Save();
        }
    }

    public static MainButtonStyle NextButtonStyle()
    {
        var next = (MainButtonStyle)(((int)ButtonStyle + 1) % 3);
        ButtonStyle = next;
        return next;
    }

    public static string ButtonStyleLabel => ButtonStyle switch
    {
        MainButtonStyle.Vanilla => "按钮样式 原版",
        MainButtonStyle.Acrylic => "按钮样式 亚克力",
        _ => "按钮样式 MOD",
    };
    // ---- 首次解压内嵌素材 ----

    private static bool _extracted;

    /// <summary>把程序集里内嵌的 BG_*.png 释放到 Image 目录（已存在则跳过，不覆盖用户的文件）。</summary>
    public static int EnsureExtracted()
    {
        if (_extracted) return 0;
        _extracted = true;

        int written = 0;
        try
        {
            Directory.CreateDirectory(ImageDir);
            Directory.CreateDirectory(VideoDir);

            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                if (!res.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;

                string tail = res.Substring(ResourcePrefix.Length);
                if (tail.EndsWith("_preview.png", StringComparison.OrdinalIgnoreCase)) continue;

                string dest = Path.Combine(ImageDir, tail);
                if (File.Exists(dest)) continue;      // 不覆盖

                using var stream = asm.GetManifestResourceStream(res);
                if (stream == null) continue;
                using var fs = File.Create(dest);
                stream.CopyTo(fs);
                written++;
                LightLogger.Log($"[BackgroundStore] 释放内嵌背景 → {dest}");
            }

            if (written > 0)
                LightLogger.Log($"[BackgroundStore] 共释放 {written} 张内嵌背景到 {ImageDir}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[BackgroundStore.EnsureExtracted]", ex);
        }
        return written;
    }

    // ---- 扫描 ----

    private static List<BackgroundEntry> _cache = new();
    private static float _cacheTime = -999f;
    private const float CacheSeconds = 1f;

    /// <summary>扫描素材（图片 + 视频）。结果缓存 1 秒，避免每帧读盘。</summary>
    public static List<BackgroundEntry> Scan(bool force = false)
    {
        if (!force && Time.realtimeSinceStartup - _cacheTime < CacheSeconds)
            return _cache;

        _cacheTime = Time.realtimeSinceStartup;
        var list = new List<BackgroundEntry>();
        try
        {
            Directory.CreateDirectory(ImageDir);
            Directory.CreateDirectory(VideoDir);

            Collect(list, ImageDir, isVideo: false);
            Collect(list, VideoDir, isVideo: true);

            list = list.OrderBy(e => e.IsVideo).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[BackgroundStore.Scan]", ex);
            list = new List<BackgroundEntry>();
        }

        _cache = list;
        return _cache;
    }

    private static void Collect(List<BackgroundEntry> into, string dir, bool isVideo)
    {
        foreach (var path in Directory.GetFiles(dir))
        {
            string ext = Path.GetExtension(path);
            bool ok = isVideo ? IsVideoExt(ext) : IsImageExt(ext);
            if (!ok) continue;

            try
            {
                var fi = new FileInfo(path);
                into.Add(new BackgroundEntry
                {
                    FullPath = path,
                    FileName = fi.Name,
                    IsVideo = isVideo,
                    SizeBytes = fi.Length,
                });
            }
            catch { /* 单个文件读不到就跳过 */ }
        }
    }

    /// <summary>按当前设置解析出这次要用的素材；返回 null 表示用原版背景。</summary>
    public static BackgroundEntry? Resolve()
    {
        var sel = Selected;
        if (string.IsNullOrEmpty(sel)) return null;

        var files = Scan();
        if (files.Count == 0) return null;

        if (sel != SelectionRandom)
        {
            var hit = files.FirstOrDefault(f =>
                string.Equals(f.FileName, sel, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            // 选中的文件被删了 → 退回随机（与参考实现一致：不覆盖已保存的文件名）
        }

        return files[UnityEngine.Random.Range(0, files.Count)];
    }

    /// <summary>把字节数格式化成人看的大小。</summary>
    public static string HumanSize(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    /// <summary>文件 SHA-256（界面上要显示；大文件也算得动，几毫秒）。失败返回 "—"。</summary>
    public static string Sha256(string path)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch { return "—"; }
    }
}
