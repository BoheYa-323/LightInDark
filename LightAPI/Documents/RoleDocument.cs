using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using LightInDark.Core;

namespace LightInDark.Documents;

/// <summary>
/// 职业文档加载/渲染：按相对路径读嵌入资源（约定同 SfxManager："./Resources/Docs/x.html"），
/// Html / MarkDown 统一渲染成 TMP 富文本（块级标签转行、保留加粗斜体、解码实体）。结果按路径缓存。
/// </summary>
public static class RoleDocument
{
    // 缓存：程序集名|路径 → 渲染后的文本
    private static readonly Dictionary<string, string> _cache = new();

    /// <summary>读取并渲染文档；未找到/失败返回空串并告警。</summary>
    public static string Load(string relativePath, Assembly owner, Func<string, string> render)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "";
        var key = (owner?.GetName().Name ?? "?") + "|" + relativePath;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var raw = ReadEmbedded(relativePath, owner);
        if (raw == null)
        {
            LightLogger.LogWarning($"[RoleDocument] 未找到文档资源: {relativePath}");
            _cache[key] = "";
            return "";
        }

        string text;
        try { text = render(raw) ?? ""; }
        catch (Exception ex)
        {
            LightLogger.LogError($"[RoleDocument] 渲染失败 {relativePath}", ex);
            text = "";
        }
        _cache[key] = text;
        return text;
    }

    /// <summary>从嵌入资源读原文（先查 owner 程序集，再扫描全部已加载程序集）。</summary>
    private static string ReadEmbedded(string relativePath, Assembly owner)
    {
        // "./Resources/Docs/x.html" → "Resources.Docs.x.html"（与 SfxManager 同约定）
        var normalized = relativePath.Replace('\\', '/').TrimStart('.', '/').Replace('/', '.');
        var tail = ".Resources." + normalized;

        foreach (var asm in Candidates(owner))
        {
            string[] names;
            try { names = asm.GetManifestResourceNames(); }
            catch { continue; }

            foreach (var name in names)
            {
                if (!name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(normalized, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream == null) continue;
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    LightLogger.LogWarning($"[RoleDocument] 读取 {name} 失败: {ex.Message}");
                }
            }
        }
        return null;
    }

    private static IEnumerable<Assembly> Candidates(Assembly owner)
    {
        if (owner != null) yield return owner;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            if (asm != owner) yield return asm;
    }

    // ======================= 渲染 =======================

    /// <summary>HTML → TMP 富文本：块级标签转行，b/i/color 保留，其余标签剥离，解码常用实体。</summary>
    public static string RenderHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        // 块级标签 → 换行
        var s = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|h[1-6]|li|tr)>", "\n", RegexOptions.IgnoreCase);
        // 语义标签 → TMP 富文本
        s = Regex.Replace(s, @"<(b|strong)>", "<b>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(b|strong)>", "</b>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<(i|em)>", "<i>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(i|em)>", "</i>", RegexOptions.IgnoreCase);
        // 标题加粗（开标签）
        s = Regex.Replace(s, @"<h[1-6][^>]*>", "<b>", RegexOptions.IgnoreCase);
        // 剥离其余全部标签
        s = Regex.Replace(s, @"<[^>]+>", "");
        // 解码常用实体
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
             .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        // 压掉多余空行
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>Markdown → TMP 富文本：标题/加粗/斜体/行内代码/链接，保留换行结构。</summary>
    public static string RenderMarkdown(string md)
    {
        if (string.IsNullOrEmpty(md)) return "";

        var lines = md.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        foreach (var rawLine in lines)
        {
            var line = rawLine;
            // 标题 → 加粗
            var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (h.Success)
                line = $"<b>{h.Groups[2].Value}</b>";
            else if (Regex.IsMatch(line, @"^\s*([-*_])\1{2,}\s*$"))
                line = ""; // 水平分割线
            else
            {
                // 引用 → 去掉 > 保留文字
                line = Regex.Replace(line, @"^\s*>\s?", "");
                // 列表符号 → 圆点
                line = Regex.Replace(line, @"^(\s*)[-*+]\s+", "$1· ");
            }

            // 图片整段去掉，链接只留文字
            line = Regex.Replace(line, @"!\[([^\]]*)\]\([^)]*\)", "$1");
            line = Regex.Replace(line, @"\[([^\]]+)\]\(([^)]+)\)", "$1（$2）");
            // 行内代码 → 去反引号
            line = Regex.Replace(line, @"`([^`]+)`", "$1");
            // 加粗 → TMP
            line = Regex.Replace(line, @"\*\*([^*]+)\*\*", "<b>$1</b>");
            line = Regex.Replace(line, @"__([^_]+)__", "<b>$1</b>");
            // 斜体 → TMP
            line = Regex.Replace(line, @"(?<!\*)\*([^*]+)\*(?!\*)", "<i>$1</i>");
            line = Regex.Replace(line, @"(?<!_)_([^_]+)_(?!_)", "<i>$1</i>");

            sb.AppendLine(line);
        }
        var s = sb.ToString();
        s = Regex.Replace(s, @"(\n){3,}", "\n\n");
        return s.Trim();
    }
}
