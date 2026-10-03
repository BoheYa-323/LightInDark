using System;
using System.Collections.Generic;
using Light.UI.HudUI;
using Light.UI.Window;
using LightInDark.Core;
using LightInDark.UI.Window;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using MetaScreen = Light.UI.HudUI.MetaScreen;

namespace Light.UI.MainMenu;

/// <summary>
/// 「按钮颜色」窗口 —— 叠在「更换背景图」面板**之上**的第二个窗口。
///
/// 层级（用户特别强调"注意层级！"）：
///   · 本窗口 <c>sortingGroupOrder = 400</c>，面板是 300 → 一定盖在面板上面；
///   · <see cref="UiModalGuard"/> 现在维护一个**窗口栈**，两个窗口都登记，
///     所以两个窗口里的按钮都能点、原版 UI 依然点不动；
///   · 关掉本窗口**不会**把面板的遮罩一起撤掉（Pop 只在栈空时才还原）。
///
/// 功能：选一个按钮（或"全部"）→ 点 18 个原版色之一 / 输入 #Hex → 立刻生效。
/// 颜色存在 <c>BackgroundSettings.json</c> 的 buttonColors 里（按钮名 → #RRGGBB）。
/// </summary>
public class ButtonColorPanel
{
    private static readonly Vector2 WindowSize = new(8.6f, 5.4f);
    private const float PanelScale = 0.78f;
    private const int VisibleRows = 8;

    private static readonly Color GlowWhite = new(1f, 0.95f, 0.85f, 1f);
    private static readonly Color TextDim = new(0.78f, 0.75f, 0.72f, 1f);
    private static readonly Color BorderGlow = new(1f, 0.95f, 0.85f, 0.80f);

    private MetaScreen? _screen;
    private GameObject? _windowObj;
    private bool _built;
    private bool _showing;

    private readonly List<GradientButton> _rowButtons = new();
    private readonly List<Transform> _swatches = new();
    private int _scrollTop;
    private string _selectedKey = "";      // "" = 全部
    private SpriteRenderer? _currentSwatch;
    private TextMeshPro? _currentText;
    private GUITextField? _hexField;

    public static ButtonColorPanel? Active { get; private set; }

    public bool IsAlive
    {
        get { try { return _windowObj != null && _windowObj; } catch { return false; } }
    }

    public bool IsShown => _showing && IsAlive;

    // =====================================================================
    //  显示 / 隐藏
    // =====================================================================

    public void Show()
    {
        try
        {
            if (_showing && IsAlive) return;
            Build();
            if (_windowObj == null) return;

            _windowObj.SetActive(true);
            _showing = true;
            Active = this;
            UiModalGuard.Push(_windowObj.transform);

            RefreshList();
            RefreshCurrent();
            LightLogger.Log($"[ButtonColorPanel] 已打开（{UiModalGuard.Describe()}）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[ButtonColorPanel.Show]", ex);
        }
    }

    public void Hide()
    {
        try
        {
            _showing = false;
            if (Active == this) Active = null;
            if (_windowObj != null)
            {
                UiModalGuard.Pop(_windowObj.transform);
                _windowObj.SetActive(false);
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[ButtonColorPanel.Hide]", ex);
        }
    }

    // =====================================================================
    //  构建
    // =====================================================================

    private void Build()
    {
        if (_built && IsAlive) return;
        DestroyPartial();

        var parent = DestroyableSingleton<MainMenuManager>.Instance;
        if (parent == null) return;

        try
        {
            // ⚠️ sortingGroupOrder 必须**高于**面板的 300，否则会被面板盖住
            _screen = MetaScreen.GenerateWindow(
                WindowSize, parent.transform, new Vector3(0f, 0f, -40f),
                withBlackScreen: true, closeOnClickOutside: false,
                background: BackgroundSetting.Modern, withCloseButton: true,
                sortingGroupOrder: 400);

            if (_screen == null) return;
            _windowObj = _screen.transform.parent.gameObject;
            _windowObj.transform.localScale = Vector3.one * PanelScale;

            // 点**窗口以外**才关（和主面板一样的做法：HudUI 那块整屏 ClickGuard 会把
            // 窗口内部的空白也吃掉，所以换成四条边框带子）
            UiModalGuard.InstallOutsideClickGuard(_windowObj, WindowSize, () =>
            {
                if (MenuDialogs.AnyOpen) return;
                Hide();
            });

            RewireClose();
            var root = _screen.transform;

            MakeText(root, "Title", "按钮颜色", new Vector3(-3.9f, 2.3f, -0.1f),
                1.7f, FontStyles.Bold, TextAlignmentOptions.Left, GlowWhite, 3.5f);
            MakeText(root, "Hint", "选按钮 → 点颜色    （颜色存在 BackgroundSettings.json）",
                new Vector3(-3.9f, 1.95f, -0.1f), 0.95f, FontStyles.Normal,
                TextAlignmentOptions.Left, TextDim, 7.6f);
            MakeLine(root, new Vector3(0f, 1.78f, -0.05f), new Vector2(8.1f, 0.022f));
            MakeLine(root, new Vector3(0f, -1.95f, -0.05f), new Vector2(8.1f, 0.022f));

            BuildLeft(root);
            BuildRight(root);

            // 底部：当前选中 + 清除
            _currentText = MakeText(root, "Current", "", new Vector3(-3.9f, -2.2f, -0.1f),
                1.05f, FontStyles.Normal, TextAlignmentOptions.Left, GlowWhite, 4.6f);

            var clear = GradientButton.Create(root, "清除该按钮颜色", new Vector2(2.2f, 0.5f),
                ClearSelected, false, 1.15f);
            if (clear != null) clear.SetPosition(new Vector3(3.0f, -2.2f, -0.1f));

            var close = GradientButton.Create(root, "关闭", new Vector2(1.3f, 0.5f),
                Hide, false, 1.3f);
            if (close != null) close.SetPosition(new Vector3(0.2f, -2.2f, -0.1f));

            _built = true;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[ButtonColorPanel.Build]", ex);
            DestroyPartial();
        }
    }

    private void BuildLeft(Transform root)
    {
        const float cx = -2.9f;
        MakeText(root, "ListLabel", "选择按钮", new Vector3(cx, 1.55f, -0.1f),
            1.2f, FontStyles.Bold, TextAlignmentOptions.Center, GlowWhite, 3.0f);

        // "全部" + 各按钮，共用一个滚动列表
        for (int i = 0; i < VisibleRows; i++)
        {
            int idx = i;
            var b = GradientButton.Create(root, "", new Vector2(3.0f, 0.4f),
                () => OnRowClicked(idx), false, 1.15f);
            if (b == null) continue;
            b.SetPosition(new Vector3(cx, 1.05f - idx * 0.46f, -0.1f));
            _rowButtons.Add(b);
        }

        var up = GradientButton.Create(root, "▲", new Vector2(0.5f, 0.34f), () => ScrollBy(-1), false, 1.1f);
        if (up != null) up.SetPosition(new Vector3(cx - 0.7f, -1.05f, -0.1f));

        var down = GradientButton.Create(root, "▼", new Vector2(0.5f, 0.34f), () => ScrollBy(1), false, 1.1f);
        if (down != null) down.SetPosition(new Vector3(cx + 0.7f, -1.05f, -0.1f));
    }

    private void BuildRight(Transform root)
    {
        const float cx = 1.9f;
        MakeText(root, "PaletteLabel", "原版 18 色", new Vector3(cx - 1.3f, 1.55f, -0.1f),
            1.2f, FontStyles.Bold, TextAlignmentOptions.Left, GlowWhite, 3.0f);

        // 18 色：3 行 × 6 列
        var colors = GetVanillaColors();
        for (int i = 0; i < colors.Count && i < 18; i++)
        {
            int row = i / 6, col = i % 6;
            MakeSwatch(root, new Vector3(cx - 1.85f + col * 0.72f, 0.95f - row * 0.72f, -0.1f),
                new Vector2(0.62f, 0.62f), colors[i], colors[i]);
        }

        // 当前颜色预览
        MakeText(root, "CurLabel", "当前", new Vector3(cx - 1.85f, -1.35f, -0.1f),
            1.0f, FontStyles.Normal, TextAlignmentOptions.Left, TextDim, 1.2f);
        var swatchGo = new GameObject("CurrentSwatch");
        swatchGo.layer = LayerExpansion.GetUILayer();
        swatchGo.transform.SetParent(root, false);
        swatchGo.transform.localPosition = new Vector3(cx - 1.2f, -1.35f, -0.1f);
        _currentSwatch = swatchGo.AddComponent<SpriteRenderer>();
        _currentSwatch.sprite = Light.UI.Window.VanillaAsset.FullScreenSprite;
        _currentSwatch.drawMode = SpriteDrawMode.Sliced;
        _currentSwatch.size = new Vector2(0.6f, 0.42f);
        _currentSwatch.color = Color.white;

        // #Hex 输入
        MakeText(root, "HexLabel", "#Hex", new Vector3(cx - 0.75f, -1.35f, -0.1f),
            1.0f, FontStyles.Normal, TextAlignmentOptions.Left, TextDim, 0.9f);
        try
        {
            _hexField = GUITextField.Create(root, new Vector2(2.3f, 0.44f), "#RRGGBB", OnHexEntered);
            if (_hexField != null)
                _hexField.GameObject.transform.localPosition = new Vector3(cx + 1.5f, -1.35f, -0.1f);
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[ButtonColorPanel] Hex 输入框创建失败：{ex.Message}");
        }

        MakeText(root, "RgbHint", "也可以直接在上面的 18 色里点一个",
            new Vector3(cx - 1.85f, -1.72f, -0.1f), 0.9f, FontStyles.Normal,
            TextAlignmentOptions.Left, TextDim, 4.4f);
    }

    // =====================================================================
    //  颜色
    // =====================================================================

    /// <summary>原版 18 个玩家色。取不到就退回一组手写的近似色。</summary>
    private static List<Color> GetVanillaColors()
    {
        var list = new List<Color>();
        try
        {
            var pc = Palette.PlayerColors;
            if (pc != null)
                foreach (var c in pc) list.Add(new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f));
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[ButtonColorPanel] 读 Palette.PlayerColors 失败：{ex.Message}");
        }

        if (list.Count == 0)
        {
            list.AddRange(new[]
            {
                new Color(0.78f,0.08f,0.08f), new Color(0.11f,0.24f,0.78f), new Color(0.10f,0.51f,0.16f),
                new Color(0.90f,0.28f,0.55f), new Color(0.98f,0.55f,0.10f), new Color(1.00f,0.98f,0.30f),
                new Color(0.20f,0.20f,0.20f), new Color(0.90f,0.90f,0.90f), new Color(0.55f,0.30f,0.78f),
                new Color(0.45f,0.28f,0.16f), new Color(0.20f,0.85f,0.85f), new Color(0.55f,0.85f,0.20f),
                new Color(0.94f,0.60f,0.68f), new Color(1.00f,0.86f,0.72f), new Color(0.94f,0.94f,0.60f),
                new Color(0.45f,0.45f,0.45f), new Color(0.62f,0.90f,0.65f), new Color(0.30f,0.60f,0.90f),
            });
        }
        return list;
    }

    /// <summary>建一个纯色小方块（可点）。不用 GradientButton —— 那块要显示的是颜色本身。</summary>
    private void MakeSwatch(Transform root, Vector3 pos, Vector2 size, Color color, Color _)
    {
        try
        {
            var go = new GameObject("Swatch");
            go.layer = LayerExpansion.GetUILayer();
            go.transform.SetParent(root, false);
            go.transform.localPosition = pos;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Light.UI.Window.VanillaAsset.FullScreenSprite;
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = size;
            sr.color = color;

            var col = go.AddComponent<BoxCollider2D>();
            col.isTrigger = true;
            col.size = size;

            var pb = go.SetUpButton(true, sr, playSound: true);
            var captured = color;
            pb.OnClick.AddListener((UnityAction)(() => ApplyColor(captured)));

            // 描一圈辉光白，不然深色块在深底上看不出边界
            AddBorder(root, pos, size, BorderGlow, 0.03f);
            _swatches.Add(go.transform);
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[ButtonColorPanel.MakeSwatch] {ex.Message}");
        }
    }

    private void OnHexEntered(string text)
    {
        var c = AppearanceSettings.ParseHex(text);
        if (c == null) { LightLogger.LogWarning($"[ButtonColorPanel] 颜色格式不对：{text}"); return; }
        ApplyColor(c.Value);
    }

    private void ApplyColor(Color color)
    {
        try
        {
            if (string.IsNullOrEmpty(_selectedKey))
            {
                MainMenuButtonStyler.SetAllColors(color);      // 批量
                LightLogger.Log($"[ButtonColorPanel] 批量设为 {AppearanceSettings.ToHex(color)}");
            }
            else
            {
                AppearanceSettings.SetButtonColor(_selectedKey, color);
                MainMenuButtonStyler.Apply(force: true);
            }

            if (!MainMenuButtonStyler.ColorsSelectable)
                LightLogger.Log("[ButtonColorPanel] 提示：当前不是「亚克力样式」，颜色要切到亚克力才看得出来");

            RefreshCurrent();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[ButtonColorPanel.ApplyColor]", ex);
        }
    }

    private void ClearSelected()
    {
        try
        {
            if (string.IsNullOrEmpty(_selectedKey))
            {
                AppearanceSettings.ClearButtonColors();
                MainMenuButtonStyler.Apply(force: true);
            }
            else
            {
                MainMenuButtonStyler.ClearColor(_selectedKey);
            }
            RefreshCurrent();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[ButtonColorPanel.ClearSelected]", ex);
        }
    }

    // =====================================================================
    //  列表
    // =====================================================================

    private int TotalRows => MainMenuButtonStyler.Entries.Count + 1;   // 第 0 行是"全部"

    private string KeyOfRow(int row)
        => row <= 0 ? "" : MainMenuButtonStyler.Entries[row - 1].Key;

    private string LabelOfRow(int row)
        => row <= 0 ? "★ 全部按钮" : MainMenuButtonStyler.Entries[row - 1].Label;

    private void ScrollBy(int d)
    {
        _scrollTop = Mathf.Clamp(_scrollTop + d, 0, Mathf.Max(0, TotalRows - VisibleRows));
        RefreshList();
    }

    private void OnRowClicked(int rowIndex)
    {
        int row = _scrollTop + rowIndex;
        if (row < 0 || row >= TotalRows) return;
        _selectedKey = KeyOfRow(row);
        RefreshList();
        RefreshCurrent();
    }

    private void RefreshList()
    {
        try
        {
            for (int i = 0; i < _rowButtons.Count; i++)
            {
                var b = _rowButtons[i];
                if (b == null) continue;

                int row = _scrollTop + i;
                bool has = row >= 0 && row < TotalRows;
                b.SetActive(has);
                if (!has) continue;

                string key = KeyOfRow(row);
                bool sel = key == _selectedKey;
                string mark = sel ? "▶ " : "   ";
                string colorTag = "";
                var oc = row <= 0 ? (Color?)null : AppearanceSettings.GetButtonColor(key);
                if (oc.HasValue) colorTag = $"  {AppearanceSettings.ToHex(oc.Value)}";

                b.SetText($"{mark}{LabelOfRow(row)}{colorTag}");
                b.SetSelected(sel);
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[ButtonColorPanel.RefreshList]", ex);
        }
    }

    private void RefreshCurrent()
    {
        try
        {
            var c = string.IsNullOrEmpty(_selectedKey)
                ? (Color?)null
                : AppearanceSettings.GetButtonColor(_selectedKey);

            if (_currentText != null)
            {
                string who = string.IsNullOrEmpty(_selectedKey) ? "全部按钮" : _selectedKey;
                _currentText.text = c.HasValue
                    ? $"选中：{who}    颜色：{AppearanceSettings.ToHex(c.Value)}"
                    : $"选中：{who}    颜色：（未单独设置）";
            }

            if (_currentSwatch != null)
                _currentSwatch.color = c ?? new Color(0.25f, 0.25f, 0.28f, 1f);
        }
        catch { }
    }

    // =====================================================================
    //  小工具
    // =====================================================================

    private static TextMeshPro MakeText(Transform parent, string name, string text, Vector3 pos,
        float fontSize, FontStyles style, TextAlignmentOptions align, Color color, float width)
    {
        var obj = new GameObject(name);
        obj.layer = LayerExpansion.GetUILayer();
        obj.transform.SetParent(parent, false);

        var tmp = obj.AddComponent<TextMeshPro>();
        try
        {
            // 简中字体（模板 #2）
            var scFont = Light.UI.Window.MenuTextTemplate2.Font;
            if (scFont != null)
            {
                tmp.font = scFont;
                var scMat = Light.UI.Window.MenuTextTemplate2.FontMaterial;
                if (scMat != null) tmp.fontSharedMaterial = scMat;
            }
            else
            {
                HudUIFont.EnsureLoaded();
                if (HudUIFont.FontAsset != null)
                {
                    tmp.font = HudUIFont.FontAsset;
                    tmp.fontSharedMaterial = HudUIFont.FontMaterial;
                }
            }
        }
        catch { }

        tmp.rectTransform.sizeDelta = new Vector2(width, 0.4f);
        if (align is TextAlignmentOptions.Left or TextAlignmentOptions.TopLeft)
            pos.x += width * 0.5f;
        obj.transform.localPosition = pos;

        tmp.text = text;
        tmp.alignment = align;
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.color = color;
        tmp.raycastTarget = false;
        return tmp;
    }

    private static void MakeLine(Transform parent, Vector3 pos, Vector2 size)
    {
        var obj = new GameObject("Divider");
        obj.layer = LayerExpansion.GetUILayer();
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = pos;
        var sr = obj.AddComponent<SpriteRenderer>();
        sr.sprite = Light.UI.Window.VanillaAsset.FullScreenSprite;
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = size;
        sr.color = BorderGlow;
    }

    private static void AddBorder(Transform parent, Vector3 center, Vector2 size, Color color, float t)
    {
        try
        {
            float z = center.z - 0.02f, hx = size.x * 0.5f, hy = size.y * 0.5f;
            MakeLine(parent, new Vector3(center.x, center.y + hy, z), new Vector2(size.x + t * 2f, t));
            MakeLine(parent, new Vector3(center.x, center.y - hy, z), new Vector2(size.x + t * 2f, t));
            MakeLine(parent, new Vector3(center.x - hx, center.y, z), new Vector2(t, size.y + t * 2f));
            MakeLine(parent, new Vector3(center.x + hx, center.y, z), new Vector2(t, size.y + t * 2f));
        }
        catch { }
    }

    private void RewireClose()
    {
        try
        {
            if (_windowObj == null) return;
            var parent = _windowObj.transform;
            Transform? child = null;
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c != null && c.name == "CloseButton") { child = c; break; }
            }
            if (child == null) return;

            child.localScale = Vector3.one * 0.95f;
            var pb = child.GetComponent<PassiveButton>();
            if (pb == null) return;
            pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            pb.OnClick.AddListener((UnityAction)(() => GradientButton.PlayClickSound()));
            pb.OnClick.AddListener((UnityAction)(() => Hide()));
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[ButtonColorPanel.RewireClose] {ex.Message}");
        }
    }

    private void DestroyPartial()
    {
        try
        {
            if (_windowObj != null)
            {
                UiModalGuard.Pop(_windowObj.transform);
                UnityEngine.Object.Destroy(_windowObj);
            }
        }
        catch { }
        _windowObj = null;
        _screen = null;
        _rowButtons.Clear();
        _swatches.Clear();
        _currentSwatch = null;
        _currentText = null;
        _hexField = null;
        _built = false;
    }
}
