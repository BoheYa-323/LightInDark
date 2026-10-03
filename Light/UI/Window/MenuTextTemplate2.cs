using System;
using System.Collections.Generic;
using LightInDark.Core;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Light.UI.Window;

/// <summary>
/// **第二个 TMP 模板**:简中字体版。
///
/// 和 <see cref="MenuTextTemplate"/> 的区别只在字体:
///   - 模板 #1(`MenuTextTemplate`):主界面"本地/在线"卡片那套字体(英文字形好看,中文靠 fallback)
///   - 模板 #2(本类):游戏内置的**简中字体 `NotoSansSC-Regular SDF`**,中文是本体自带字形
///
/// ⚠️ 字体来源照抄 Nebula(`Nebula\Language\Language.cs:12-24`):
///    ```
///    var fonts = Object.FindObjectsOfTypeIncludingAssets(Il2CppType.Of<TMP_FontAsset>());
///    if (font.name == "NotoSansSC-Regular SDF") FontSC = ...;
///    ```
///    Nebula **没有自带字体文件**,它就是按名字从游戏资源里把简中字体捞出来的。
///    本工程 `VanillaAsset.FindAsset&lt;T&gt;` 是同一套手法,所以直接复用。
///
/// 用法:
///     var tmp = MenuTextTemplate2.Create(parent, localPos, "更换背景图", 1.5f);
///     var tmp2 = MenuTextTemplate2.Create(parent, localPos, "红字", 1.2f, Color.red);
/// </summary>
public static class MenuTextTemplate2
{
    /// <summary>和模板 #1 共用同一个"辉光白"。</summary>
    public static readonly Color GlowWhite = MenuTextTemplate.GlowWhite;

    /// <summary>Nebula 用的那个简中字体名(首选)。</summary>
    public const string PrimaryFontName = "NotoSansSC-Regular SDF";

    /// <summary>
    /// 候选名字。游戏不同版本/不同打包方式下后缀可能不一样,
    /// 逐个试,并且失败时会把**所有** TMP_FontAsset 的名字打进日志(见 <see cref="DumpAllFontNames"/>)。
    /// </summary>
    private static readonly string[] Candidates =
    {
        PrimaryFontName,
        "NotoSansSC SDF",
        "NotoSansSC-Regular",
        "NotoSansSC",
        "NotoSansSC Regular SDF",
    };

    private static TMP_FontAsset? _font;
    private static Material? _material;
    private static bool _resolved;
    private static bool _dumped;

    /// <summary>简中字体(懒加载;解析失败返回 null,调用方自行兜底)。</summary>
    public static TMP_FontAsset? Font
    {
        get { EnsureResolved(); return _font; }
    }

    /// <summary>简中字体对应的图集材质。</summary>
    public static Material? FontMaterial
    {
        get { EnsureResolved(); return _material; }
    }

    /// <summary>字体是否解析成功(诊断用)。</summary>
    public static bool IsReady => Font != null;

    /// <summary>
    /// 建一段"简中字体"文字。
    ///
    /// ⚠️ 换字体必须**同时换材质**:TMP 的字形是从字体自己的图集材质里取的,
    ///    只写 `font` 不写 `fontSharedMaterial` 会继续用旧材质 →
    ///    轻则字看不见,重则满是方块/乱码。
    /// </summary>
    public static TextMeshPro? Create(Transform parent, Vector3 localPos, string text,
        float fontSize, Color? color = null)
    {
        try
        {
            var prefab = VanillaAsset.GetStandardTextPrefab();
            if (prefab == null)
            {
                LightLogger.LogWarning("[MenuTextTemplate2] 原版标准文本预制体不可用，跳过建字");
                return null;
            }

            var tmp = Object.Instantiate(prefab, parent);
            tmp.transform.localPosition = localPos;

            ApplyFont(tmp);

            tmp.fontSize = fontSize;
            tmp.color = color ?? GlowWhite;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            tmp.text = text;
            tmp.ForceMeshUpdate();
            return tmp;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MenuTextTemplate2.Create]", ex);
            return null;
        }
    }

    /// <summary>把一个已有的 TMP 换成本模板的字体(给"不是 Instantiate 预制体"的场景用)。</summary>
    public static void ApplyFont(TMP_Text? tmp)
    {
        if (tmp == null) return;
        try
        {
            var f = Font;
            if (f == null) return;

            tmp.font = f;

            var m = FontMaterial;
            if (m != null) tmp.fontSharedMaterial = m;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MenuTextTemplate2.ApplyFont] {ex.Message}");
        }
    }

    // =====================================================================
    //  字体解析
    // =====================================================================

    private static void EnsureResolved()
    {
        if (_resolved) return;

        try
        {
            // ① 按候选名字找（和后缀无关的部分最稳）
            foreach (var name in Candidates)
            {
                try
                {
                    var f = VanillaAsset.FindAsset<TMP_FontAsset>(name);
                    if (f != null) { Adopt(f, $"按名字命中「{name}」"); return; }
                }
                catch { }
            }

            // ② 名字对不上 → 把游戏里所有 TMP 字体捞出来，挑名字里像简中的
            foreach (var f in ListAll())
            {
                var n = SafeName(f);
                if (n.Length == 0) continue;
                if (n.Contains("SC") || n.Contains("Chinese") || n.Contains("Hans") || n.Contains("CN"))
                {
                    Adopt(f, $"按名字含 SC/Chinese/Hans/CN 命中「{n}」");
                    return;
                }
            }

            // ③ 都没有 → 退回主界面那套字体（至少和模板 #1 一致，不会更糟）
            var fallback = MenuTextTemplate.MenuFont;
            if (fallback != null)
            {
                Adopt(fallback, $"退回主界面字体「{fallback.name}」");
                return;
            }

            DumpAllFontNames();
            LightLogger.LogWarning("[MenuTextTemplate2] 找不到简中字体，也没有可退回的字体");
            _resolved = true;   // 别每帧重试
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MenuTextTemplate2.EnsureResolved]", ex);
            _resolved = true;
        }
    }

    private static void Adopt(TMP_FontAsset f, string how)
    {
        _font = f;
        try { _material = f.material; } catch { }

        _resolved = true;
        LightLogger.Log($"[MenuTextTemplate2] 采用简中字体：{f.name}（{how}，" +
                        $"材质={(_material != null ? _material.name : "无")}）");
    }

    private static List<TMP_FontAsset> ListAll()
    {
        var res = new List<TMP_FontAsset>();
        try
        {
            foreach (var obj in VanillaAsset.ListFontAssets())
            {
                try { if (obj != null) res.Add(obj); } catch { }
            }
        }
        catch { }
        return res;
    }

    private static string SafeName(TMP_FontAsset? f)
    {
        try { return f == null ? "" : (f.name ?? ""); } catch { return ""; }
    }

    /// <summary>
    /// 诊断:把游戏里所有 TMP_FontAsset 的名字打进日志(只打一次)。
    /// 如果简中字体没找到,让用户把这行发过来就能直接确认正确名字。
    /// </summary>
    private static void DumpAllFontNames()
    {
        if (_dumped) return;
        _dumped = true;
        try
        {
            var names = new List<string>();
            foreach (var f in ListAll()) names.Add(SafeName(f));
            LightLogger.LogWarning($"[MenuTextTemplate2] 游戏里的 TMP 字体清单（{names.Count} 个）：" +
                                   string.Join(" | ", names));
        }
        catch { }
    }
}
