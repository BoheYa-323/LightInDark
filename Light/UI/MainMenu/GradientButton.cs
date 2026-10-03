using System;
using Light.UI.HudUI;
using Light.UI.Window;
using LightInDark.Core;
using LightInDark.UI.Window;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Light.UI.MainMenu;

/// <summary>
/// **纯代码自绘**的按钮。
///
/// 外观（用户指定）：
///   · **边框** —— 辉光白 ⇒ 淡金 ⇒ 辉光白 的横向渐变（两端白、中间金）
///   · **内部** —— 保持"原来那样"：FS 主界面那套**半透明深色**底
///
/// 实现：**两层 SpriteRenderer**
///   ① <c>Fill</c> —— 实心圆角矩形，用 <c>color</c> 染成深色（常态暗、悬浮亮）
///   ② <c>Ring</c> —— 同样的圆角矩形，但**只画描边那一圈**、内部透明，渐变直接烘进贴图
///
/// 两张贴图用**完全相同的圆角几何**生成，所以边框和内部严丝合缝、不会错位。
///
/// 为什么不用 <see cref="HudUIButton"/>：它用的是程序集里那张 <c>Button.png</c>，
/// 形状和边框都画在图里，只能用 <c>color</c> 整体上色 → 想单独改边框颜色做不到。
///
/// 9 宫格（border）保证任意尺寸下圆角都不变形。
/// </summary>
internal sealed class GradientButton
{
    // ===== 配色 =====
    /// <summary>辉光白（与 MenuTextTemplate.GlowWhite 一致）。</summary>
    private static readonly Color GlowWhite = new(1f, 0.95f, 0.85f, 1f);
    /// <summary>淡金（边框中点色）。</summary>
    private static readonly Color PaleGold = new(0.95f, 0.78f, 0.36f, 1f);

    /// <summary>内部底色 —— 与 GameSettingMenuPatch.ApplyFsButtonStyle 同款 FS 半透明深色。</summary>
    private static readonly Color FillNormal = new(0.13f, 0.13f, 0.15f, 0.92f);
    private static readonly Color FillHover = new(0.24f, 0.24f, 0.28f, 0.98f);

    /// <summary>选中态文字（淡金，压在深色底上很清楚）。</summary>
    private static readonly Color SelectedText = new(1f, 0.86f, 0.45f, 1f);

    private const int TexSize = 48;
    private const int CornerRadius = 12;
    /// <summary>边框粗细（像素，贴图坐标系）。</summary>
    private const float RingWidth = 2.6f;

    // ===== 实例 =====
    public GameObject GameObject { get; private set; } = null!;
    /// <summary>内部底色那层（外部要改底色就动它）。</summary>
    public SpriteRenderer Renderer { get; private set; } = null!;
    /// <summary>渐变边框那层。</summary>
    public SpriteRenderer Ring { get; private set; } = null!;
    public TextMeshPro Text { get; private set; } = null!;
    public PassiveButton Button { get; private set; } = null!;
    public BoxCollider2D Collider { get; private set; } = null!;

    private Vector2 _size;
    private bool _selected;
    private bool _hovering;

    // =====================================================================
    //  贴图生成（两张：实心底 + 渐变描边）
    // =====================================================================

    private static Sprite? _fillSprite;
    private static Sprite? _ringSprite;

    /// <summary>圆角矩形的有符号距离：&lt;0 在内部，=0 在轮廓上。</summary>
    private static float RoundRectSdf(float x, float y, float half, float hx, float hy, float r)
    {
        float sx = x - half;
        float sy = y - half;
        float qx = Mathf.Abs(sx) - (hx - r);
        float qy = Mathf.Abs(sy) - (hy - r);
        float cx = Mathf.Max(qx, 0f), cy = Mathf.Max(qy, 0f);
        return Mathf.Min(Mathf.Max(qx, qy), 0f) + Mathf.Sqrt(cx * cx + cy * cy) - r;
    }

    /// <summary>边框渐变：t=0 辉光白 → t=0.5 淡金 → t=1 辉光白。</summary>
    private static Color RingGradient(float t)
        => Color.Lerp(PaleGold, GlowWhite, Mathf.Abs(2f * t - 1f));

    private static Sprite? GetFillSprite()
    {
        if (_fillSprite != null) return _fillSprite;
        try
        {
            const int S = TexSize;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var arr = new Color32[S * S];
            float half = S * 0.5f, hx = half - 0.5f, hy = half - 0.5f;

            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = RoundRectSdf(x + 0.5f, y + 0.5f, half, hx, hy, CornerRadius);
                float a = Mathf.Clamp01(0.5f - d);
                // 纯白，颜色交给 SpriteRenderer.color 染
                arr[y * S + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }

            tex.SetPixels32(arr);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            _fillSprite = MakeSlicedSprite(tex, S);
        }
        catch (Exception ex) { LightLogger.LogError("[GradientButton.GetFillSprite]", ex); }
        return _fillSprite;
    }

    private static Sprite? GetRingSprite()
    {
        if (_ringSprite != null) return _ringSprite;
        try
        {
            const int S = TexSize;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var arr = new Color32[S * S];
            float half = S * 0.5f, hx = half - 0.5f, hy = half - 0.5f;

            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float d = RoundRectSdf(x + 0.5f, y + 0.5f, half, hx, hy, CornerRadius);

                    // 只在轮廓内外 RingWidth 的范围内画：内部透明 → 露出下面的深色底
                    float dist = Mathf.Abs(d);
                    float a = Mathf.Clamp01(0.5f - (dist - RingWidth * 0.5f));
                    if (a <= 0.002f) { arr[y * S + x] = new Color32(0, 0, 0, 0); continue; }

                    var c = RingGradient(x / (float)(S - 1));
                    // 越靠轮廓中心越实，边缘柔一点，看起来像发光
                    arr[y * S + x] = new Color(c.r, c.g, c.b, a);
                }
            }

            tex.SetPixels32(arr);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            _ringSprite = MakeSlicedSprite(tex, S);
            LightLogger.Log($"[GradientButton] 已生成按钮贴图：{S}x{S} 圆角{CornerRadius} " +
                            $"边框{RingWidth}px（辉光白⇒淡金⇒辉光白）");
        }
        catch (Exception ex) { LightLogger.LogError("[GradientButton.GetRingSprite]", ex); }
        return _ringSprite;
    }

    /// <summary>切成 9 宫格，保证圆角不被拉伸。</summary>
    private static Sprite MakeSlicedSprite(Texture2D tex, int size)
    {
        int b = CornerRadius + 3;
        var spr = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        spr.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return spr;
    }

    // =====================================================================
    //  创建
    // =====================================================================

    public static GradientButton? Create(Transform parent, string text, Vector2 size,
        Action? onClick, bool selected = false, float fontSize = 1.65f)
    {
        try
        {
            var obj = new GameObject("GradientButton");
            obj.layer = LayerExpansion.GetUILayer();
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localScale = Vector3.one;

            var btn = new GradientButton { GameObject = obj, _size = size, _selected = selected };

            // ① 内部：半透明深色圆角块（z 稍大 = 在后面）
            var fillGo = new GameObject("Fill");
            fillGo.layer = LayerExpansion.GetUILayer();
            fillGo.transform.SetParent(obj.transform, false);
            fillGo.transform.localPosition = new Vector3(0f, 0f, 0.02f);
            var fill = fillGo.AddComponent<SpriteRenderer>();
            fill.sprite = GetFillSprite();
            fill.drawMode = SpriteDrawMode.Sliced;
            fill.tileMode = SpriteTileMode.Continuous;
            fill.size = size;
            fill.color = selected ? FillHover : FillNormal;
            btn.Renderer = fill;

            // ② 边框：渐变描边，压在内部之上（z 更小 = 更靠前）
            var ringGo = new GameObject("Ring");
            ringGo.layer = LayerExpansion.GetUILayer();
            ringGo.transform.SetParent(obj.transform, false);
            ringGo.transform.localPosition = Vector3.zero;
            var ring = ringGo.AddComponent<SpriteRenderer>();
            ring.sprite = GetRingSprite();
            ring.drawMode = SpriteDrawMode.Sliced;
            ring.tileMode = SpriteTileMode.Continuous;
            ring.size = size;
            ring.color = Color.white;          // 渐变已经烘在贴图里，不要再染
            btn.Ring = ring;

            // 碰撞盒挂在按钮根上
            var col = obj.AddComponent<BoxCollider2D>();
            col.isTrigger = true;
            col.size = size;
            btn.Collider = col;

            obj.AddComponent<UnityEngine.Rendering.SortingGroup>();

            // 文字（最前）
            // 用"简中字体模板"的字体（模板 #2）—— 面板/弹窗里的按钮文字统一走它
            var tmp = HudUITextHelper.Create(obj.transform,
                Light.UI.Window.MenuTextTemplate2.Font,
                Light.UI.Window.MenuTextTemplate2.FontMaterial);
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontSizeMax = fontSize;
            tmp.fontSizeMin = fontSize * 0.65f;
            tmp.enableAutoSizing = true;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = selected ? SelectedText : GlowWhite;
            tmp.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            tmp.rectTransform.localPosition = new Vector3(0f, 0f, -0.1f);
            tmp.rectTransform.sizeDelta = new Vector2(size.x * 0.94f, size.y * 1.1f);
            tmp.ForceMeshUpdate();
            btn.Text = tmp;

            // 交互
            var pb = obj.AddComponent<PassiveButton>();
            pb.OnMouseOver = new UnityEvent();
            pb.OnMouseOut = new UnityEvent();
            pb.OnClick = new Button.ButtonClickedEvent();
            btn.Button = pb;

            pb.OnMouseOver.AddListener((UnityAction)(() =>
            {
                btn._hovering = true;
                btn.ApplyFillColor();
                PlaySound("UI_Hover", 0.7f);
            }));
            pb.OnMouseOut.AddListener((UnityAction)(() =>
            {
                btn._hovering = false;
                btn.ApplyFillColor();
            }));
            pb.OnClick.AddListener((UnityAction)(() =>
            {
                PlaySound("UI_Select", 0.8f);
                try { onClick?.Invoke(); } catch (Exception ex) { LightLogger.LogError("[GradientButton.OnClick]", ex); }
            }));

            return btn;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[GradientButton.Create]", ex);
            return null;
        }
    }

    private void ApplyFillColor()
    {
        try { Renderer.color = (_selected || _hovering) ? FillHover : FillNormal; }
        catch { }
    }

    /// <summary>设置选中态：内部底提亮 + 文字转淡金。</summary>
    public void SetSelected(bool selected)
    {
        _selected = selected;
        try
        {
            Text.color = selected ? SelectedText : GlowWhite;
            ApplyFillColor();
        }
        catch { }
    }

    public void SetPosition(Vector3 localPos)
    {
        try { GameObject.transform.localPosition = localPos; } catch { }
    }

    public void SetText(string text)
    {
        try { Text.text = text; } catch { }
    }

    public void SetActive(bool active)
    {
        try { GameObject.SetActive(active); } catch { }
    }

    /// <summary>
    /// 自绘的**圆角矩形**九宫格贴图（白色，靠 color 染色）。
    /// 给"预览框 / 列表框 / 各种底"用 —— 这样它们就不用依赖原版弹窗贴图，
    /// 不会因为场景重载被回收而消失。已打 DontUnloadUnusedAsset。
    /// </summary>
    public static Sprite? PanelSprite => GetFillSprite();

    /// <summary>按钮尺寸（创建时给的）。</summary>
    public Vector2 Size => _size;

    // =====================================================================
    //  右键
    // =====================================================================

    /// <summary>
    /// 右键回调。null = 这个按钮不支持右键。
    ///
    /// ⚠️ 为什么不用 PassiveButton 的事件：原版的 <c>PassiveButtonManager</c> 只处理
    ///    "触点"，右键在它那套里没有区分（<c>OnClick</c> 左右键都会触发）。
    ///    所以这里自己用 Unity 的 <c>Input.GetMouseButtonDown(1)</c> + 碰撞盒命中判定，
    ///    由面板每帧驱动 <see cref="PollRightClick"/>。
    /// </summary>
    public Action? OnRightClick { get; set; }

    /// <summary>每帧调一次：鼠标右键按下的那一帧，如果指针在按钮范围内就触发回调。</summary>
    public bool PollRightClick(Vector2 worldPos)
    {
        if (OnRightClick == null) return false;
        try
        {
            if (Collider == null) return false;
            if (!Collider.OverlapPoint(worldPos)) return false;

            PlaySound("UI_Select", 0.7f);
            OnRightClick();
            return true;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[GradientButton.PollRightClick] {ex.Message}");
            return false;
        }
    }

    private static void PlaySound(string clipName, float volume)
    {
        try
        {
            var clip = VanillaAsset.FindSoundClip(clipName);
            if (clip != null) SoundManager.Instance?.PlaySound(clip, false, volume);
        }
        catch { }
    }

    /// <summary>给外部（例如关闭按钮）用的点击音效入口。</summary>
    public static void PlayClickSound() => PlaySound("UI_Select", 0.8f);
}
