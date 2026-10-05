using System;
using System.Collections.Generic;
using System.IO;
using LightInDark.Core;
using UnityEngine;

namespace Light.UI.MusicPlayer;

/// <summary>
/// 一首歌（其实就是一个音频文件）。
///
/// 为什么用 <c>class</c> 而不是 <c>record struct</c>：
/// 列表刷新/分页会频繁搬运元素，class 引用复制零成本，也不会出现"结构体拷贝后两个列表不同步"。
/// </summary>
public sealed class MusicTrack
{
    /// <summary>完整文件路径（加载音频时用）。</summary>
    public string FullPath { get; }

    /// <summary>不带扩展名的文件名（列表里显示这个）。</summary>
    public string DisplayName { get; }

    public MusicTrack(string fullPath, string displayName)
    {
        FullPath = fullPath ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
    }

    /// <summary>小写扩展名（含点，例如 <c>.mp3</c>）。</summary>
    public string Extension
    {
        get
        {
            try { return (Path.GetExtension(FullPath) ?? string.Empty).ToLowerInvariant(); }
            catch { return string.Empty; }
        }
    }
}

/// <summary>
/// **音乐库** —— 负责"扫目录 / 排序 / 分页 / 取当前页"这几件事，不碰任何 UI 和音频。
///
/// 目录:<c>&lt;persistentDataPath&gt;\LightInDark\Music</c>（与设置/颜色/光标同一层）。
/// 首次访问由 <see cref="Reload"/> 建目录 —— 这样用户点「打开配置文件夹」时
/// 目录一定已经存在，不会出现"资源管理器打开一个不存在的路径"。
///
/// ⚠️ 扫描/分页**全部自己算**，不递归进子目录（见 <see cref="Reload"/> 的注释）。
/// </summary>
public sealed class MusicLibrary
{
    /// <summary>本页最多显示几首（与窗口里的行池数量一致）。</summary>
    public const int PageSize = 8;

    /// <summary>
    /// 支持的扩展名 —— **直接引用加载器的那份清单**（<see cref="MusicClipLoader.SupportedExtensions"/>），
    /// 这样"列表里能看到的" 和 "真的解得开的" 永远是同一套，不会出现
    /// "列表显示 4 种、点开 2 种报不支持"的错位。
    /// </summary>
    private static string[] SupportedExtensions => MusicClipLoader.SupportedExtensions;

    private readonly List<MusicTrack> _tracks = new();

    /// <summary>扫描到的全部歌曲（只读视图给 UI 用）。</summary>
    public IReadOnlyList<MusicTrack> Tracks => _tracks;

    /// <summary>总曲目数。</summary>
    public int Count => _tracks.Count;

    /// <summary>总页数（空列表 = 1 页，避免 UI 显示"第 1 / 0 页"）。</summary>
    public int TotalPages => Mathf.Max(1, Mathf.CeilToInt(_tracks.Count / (float)PageSize));

    private int _currentPage;

    /// <summary>
    /// 当前页（0 基）。setter **永远夹紧**，这是"分页越界不崩"的第一道保险
    /// —— 刷新后列表变短、上一页/下一页点太快，都不会越界。
    /// </summary>
    public int CurrentPage
    {
        get => _currentPage;
        set => _currentPage = Mathf.Clamp(value, 0, TotalPages - 1);
    }

    /// <summary>给 UI 显示的"第 X 页"（1 基）。</summary>
    public int CurrentPageDisplay => _currentPage + 1;

    /// <summary>音乐目录。</summary>
    public static string MusicDir
    {
        get
        {
            try { return Path.Combine(LightPlugin.LightUserDataPath, "Music"); }
            catch
            {
                // 极端情况下 persistentDataPath 拿不到 → 退到游戏目录，至少不会 NRE
                try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LightInDark", "Music"); }
                catch { return "LightInDark\\Music"; }
            }
        }
    }

    /// <summary>
    /// 重新扫描目录。
    ///
    /// ⚠️ **不递归子目录**：用户自己往 Music 里丢文件夹是很常见的，
    ///    一旦递归，某个人放了个大文件夹就会卡住主线程（扫描是同步的）。
    ///    只扫顶层 = 可控、可预期。
    /// </summary>
    public void Reload()
    {
        try
        {
            _tracks.Clear();

            var dir = MusicDir;
            try { Directory.CreateDirectory(dir); } catch { /* 建不出来也得继续，下面会判 Exists */ }

            var found = new List<MusicTrack>();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        try
                        {
                            var ext = (Path.GetExtension(file) ?? string.Empty).ToLowerInvariant();
                            if (Array.IndexOf(SupportedExtensions, ext) < 0) continue;
                            found.Add(new MusicTrack(file, Path.GetFileNameWithoutExtension(file)));
                        }
                        catch (Exception ex) { LightLogger.LogWarning($"[MusicLibrary] 跳过文件 {file}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[MusicLibrary] 枚举目录失败 {dir}: {ex.Message}");
            }

            // 按文件名排序（OrdinalIgnoreCase：和资源管理器看起来的顺序一致）
            try
            {
                found.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            }
            catch { }

            _tracks.AddRange(found);

            // 刷新后页数可能变少 → 夹紧（这里再显式做一次，因为 setter 依赖 TotalPages 的即时值）
            _currentPage = Mathf.Clamp(_currentPage, 0, TotalPages - 1);

            LightLogger.Log($"[MusicLibrary] 扫描完成：{_tracks.Count} 首（{TotalPages} 页）｜目录 {dir}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicLibrary.Reload]", ex);
        }
    }

    /// <summary>取第 <paramref name="page"/> 页（0 基）的曲目；越界页返回空列表，绝不抛。</summary>
    public IReadOnlyList<MusicTrack> GetPage(int page)
    {
        try
        {
            var result = new List<MusicTrack>(PageSize);
            if (_tracks.Count == 0) return result;

            int p = Mathf.Clamp(page, 0, TotalPages - 1);
            int start = p * PageSize;
            for (int i = start; i < start + PageSize && i < _tracks.Count; i++)
                result.Add(_tracks[i]);
            return result;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicLibrary.GetPage] {ex.Message}");
            return new List<MusicTrack>();
        }
    }

    /// <summary>按全局下标取曲目（越界返回 null）。</summary>
    public MusicTrack? GetAt(int index)
    {
        try
        {
            if (index < 0 || index >= _tracks.Count) return null;
            return _tracks[index];
        }
        catch { return null; }
    }

    /// <summary>某个曲目的全局下标（找不到返回 -1）。</summary>
    public int IndexOf(MusicTrack? track)
    {
        try
        {
            if (track == null) return -1;
            for (int i = 0; i < _tracks.Count; i++)
                if (string.Equals(_tracks[i].FullPath, track.FullPath, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        catch { return -1; }
    }

    /// <summary>上/下一页（内部自动夹紧；返回值表示"页真的变了没"）。</summary>
    public bool PrevPage()
    {
        try
        {
            int before = _currentPage;
            _currentPage = Mathf.Clamp(_currentPage - 1, 0, TotalPages - 1);
            return _currentPage != before;
        }
        catch { return false; }
    }

    /// <summary>下一页（同上）。</summary>
    public bool NextPage()
    {
        try
        {
            int before = _currentPage;
            _currentPage = Mathf.Clamp(_currentPage + 1, 0, TotalPages - 1);
            return _currentPage != before;
        }
        catch { return false; }
    }

    /// <summary>「打开配置文件夹」：建目录 + 用资源管理器打开。失败只记日志。</summary>
    public static void OpenFolder()
    {
        try
        {
            var dir = MusicDir;
            try { Directory.CreateDirectory(dir); } catch { }

            // 照抄 BackgroundPanel.OpenFolder 的成熟写法（UseShellExecute = true 才会交给系统处理）
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });

            LightLogger.Log($"[MusicLibrary] 已请求打开音乐文件夹：{dir}");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicLibrary.OpenFolder] {ex.Message}");
        }
    }
}
