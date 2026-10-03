using Il2CppInterop.Runtime;
using LightInDark;
using LightInDark.UI.Window;
using TMPro;
using Twitch;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using LightInDark.Core;
using System;
using Object = UnityEngine.Object;
using Button = UnityEngine.UI.Button;
using Color = LightInDark.Color;
using UnityHelper = Light.Utilities.UnityHelper;

namespace Light.UI.Window;

/// <summary>
/// Among Us 原生资源缓存。
/// 延迟加载，用户可自定义所有材质。
/// </summary>
public static class VanillaAsset
{
    private static Sprite? _popUpBackSprite;
    private static Sprite? _textButtonSprite;
    private static Sprite? _fullScreenSprite;
    private static Sprite? _closeButtonSprite;
    private static TextMeshPro? _standardTextPrefab;
    private static Material? _standardMaskedFontMaterial;
    private static Material? _oblongMaskedFontMaterial;
    private static TMP_FontAsset? _versionFont;
    private static TMP_FontAsset? _preSpawnFont;
    private static TMP_FontAsset? _brookFont;
    private static PlayerCustomizationMenu? _playerOptionsMenuPrefab;
    private static bool _triedInit;

    /// <summary>弹窗背景 Sprite</summary>
    public static Sprite PopUpBackSprite => TryGetSprite(ref _popUpBackSprite, FallbackPanelSprite);
    /// <summary>按钮背景 Sprite</summary>
    public static Sprite TextButtonSprite => TryGetSprite(ref _textButtonSprite, FallbackPanelSprite);
    /// <summary>全屏遮罩 Sprite</summary>
    /// <summary>
    /// 铺满全屏用的纯色 Sprite。
    ///
    /// ⚠️ **故意不返回原版那张**：它来自主菜单场景的弹窗预制体，
    ///    场景一卸载就可能被回收成"假 null"（IL2CPP 下 UnloadUnusedAssets
    ///    不认托管静态引用），结果所有用它的东西——分隔线、边框、黑幕——集体消失，
    ///    但碰撞盒还在 → "能按但没图"。
    ///    它的用途只是"一块纯色填充"（各处都配 drawMode = Sliced + size 拉伸），
    ///    所以换成我们自己造的 1×1 白图**视觉上等价**，而且永远死不了。
    /// </summary>
    public static Sprite FullScreenSprite => WhiteSprite;
    /// <summary>关闭按钮 Sprite</summary>
    public static Sprite CloseButtonSprite => TryGetSprite(ref _closeButtonSprite);

    /// <summary>标准文本预制体（TextMeshPro）</summary>
    public static TextMeshPro StandardTextPrefab
    {
        get
        {
            try
            {
                EnsureLoaded();
                return _standardTextPrefab!;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[VanillaAsset.get]", ex); return default;
            }
        }
    }

    /// <summary>遮罩字体材质</summary>
    public static Material StandardMaskedFontMaterial
    {
        get
        {
            try
            {
                EnsureLoaded();
                return _standardMaskedFontMaterial!;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[VanillaAsset.get]", ex); return default;
            }
        }
    }

    /// <summary>Barlow 字体</summary>
    public static TMP_FontAsset VersionFont
    {
        get
        {
            try
            {
                EnsureLoaded();
                return _versionFont;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[VanillaAsset.get]", ex); return default;
            }
        }
    }

    /// <summary>DIN_Pro 字体</summary>
    public static TMP_FontAsset PreSpawnFont
    {
        get
        {
            try
            {
                EnsureLoaded();
                return _preSpawnFont!;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[VanillaAsset.get]", ex); return default;
            }
        }
    }

    /// <summary>Brook 字体</summary>
    public static TMP_FontAsset BrookFont
    {
        get
        {
            try
            {
                EnsureLoaded();
                return _brookFont!;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[VanillaAsset.get]", ex); return default;
            }
        }
    }

    private static Sprite? _whiteSprite;
    /// <summary>白色降级 Sprite（程序生成，永不销毁 —— 它是所有兜底的最后一环）</summary>
    public static Sprite WhiteSprite
    {
        get
        {
            try
            {
                // ⚠️ 用 Unity 的 == null（走重载），不要用 ??
                if (_whiteSprite != null) return _whiteSprite;

                var tex = Texture2D.whiteTexture;
                _whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                // 兜底图必须活到最后，否则连它都没了 UI 就彻底空白
                try { _whiteSprite.hideFlags |= HideFlags.DontUnloadUnusedAsset; } catch { }
                return _whiteSprite;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[VanillaAsset.get]", ex); return default;
            }
        }
    }

    /// <summary>
    /// 给从原版抓来的资源打上"别被卸载"标记。
    ///
    /// ⚠️⚠️ 这是"重载场景后按钮贴图消失"的**根治点**：
    ///   原来这几个 Sprite 是**裸引用**原版资源的，场景一卸载就被销毁成 Unity 的"假 null"，
    ///   而 SpriteRenderer 拿到它什么都不画（碰撞盒还在 → 表现为"能按但没图"）。
    ///   打上 DontUnloadUnusedAsset 之后它们就再也不会被卸载。
    /// </summary>
    private static Sprite? Keep(Sprite? s)
    {
        try
        {
            if (s == null) return s;
            s.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            var tex = s.texture;
            if (tex != null) tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }
        catch { }
        return s;
    }

    /// <summary>从原版抓来的那几个 Sprite 是否都还活着（Unity 语义下）。</summary>
    private static bool VanillaSpritesAlive()
        => _fullScreenSprite != null && _textButtonSprite != null && _popUpBackSprite != null;

    /// <summary>
    /// 自绘的圆角面板九宫格（深灰底 + 略亮描边）。
    ///
    /// 用途：`PopUpBackSprite` 的**兜底**。
    /// 原版那张来自主菜单场景的弹窗预制体，场景卸载后可能被回收成"假 null"
    /// （IL2CPP 下 UnloadUnusedAssets 不认托管静态引用）→ 窗口底就没了。
    /// 退化成"一块纯白方片"太丑，所以自绘一个圆角的，视觉上接近原版弹窗底。
    /// 自己造的 + DontUnloadUnusedAsset → 永远死不了。
    /// </summary>
    private static Sprite? _fallbackPanel;
    public static Sprite FallbackPanelSprite
    {
        get
        {
            try
            {
                if (_fallbackPanel != null) return _fallbackPanel;

                const int S = 48;
                const int R = 12;
                const float RingW = 2.0f;

                var tex = new UnityEngine.Texture2D(S, S, UnityEngine.TextureFormat.ARGB32, false);
                tex.filterMode = UnityEngine.FilterMode.Bilinear;
                tex.wrapMode = UnityEngine.TextureWrapMode.Clamp;

                var px = new UnityEngine.Color32[S * S];
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        // 圆角矩形 SDF（和 GradientButton 同一套算法）
                        float dx = Mathf.Abs(x + 0.5f - S * 0.5f) - (S * 0.5f - R);
                        float dy = Mathf.Abs(y + 0.5f - S * 0.5f) - (S * 0.5f - R);
                        float outside = Mathf.Sqrt(Mathf.Max(dx, 0f) * Mathf.Max(dx, 0f) +
                                                   Mathf.Max(dy, 0f) * Mathf.Max(dy, 0f));
                        float d = outside + Mathf.Min(Mathf.Max(dx, dy), 0f) - R;

                        float insideA = Mathf.Clamp01(0.5f - d);
                        float ringA = Mathf.Clamp01(1f - Mathf.Abs(d + RingW * 0.5f) / (RingW * 0.5f + 0.5f));

                        UnityEngine.Color fill = new(0.11f, 0.11f, 0.13f, 1f);
                        UnityEngine.Color ring = new(0.42f, 0.40f, 0.36f, 1f);
                        UnityEngine.Color c = UnityEngine.Color.Lerp(fill, ring, Mathf.Clamp01(ringA));

                        px[y * S + x] = new UnityEngine.Color32(
                            (byte)(Mathf.Clamp01(c.r) * 255),
                            (byte)(Mathf.Clamp01(c.g) * 255),
                            (byte)(Mathf.Clamp01(c.b) * 255),
                            (byte)(Mathf.Clamp01(insideA) * 255));
                    }
                }
                tex.SetPixels32(px);
                tex.Apply(false, false);
                tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;

                float b = R + 2f;
                _fallbackPanel = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f),
                    100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
                _fallbackPanel.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                return _fallbackPanel;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[VanillaAsset.FallbackPanelSprite]", ex);
                return WhiteSprite;
            }
        }
    }

    private static Sprite TryGetSprite(ref Sprite? field)
        => TryGetSprite(ref field, null);

    private static Sprite TryGetSprite(ref Sprite? field, Sprite? fallback)
    {
        try
        {
            EnsureLoaded();

            // ⚠️⚠️ 必须用 `field != null`（会走 UnityEngine.Object 重载的 ==），
            //    **绝不能写 `field ?? WhiteSprite`** ——
            //    `??` 是**纯 C# 引用比较**，绕过 Unity 的重载，
            //    识别不出"假 null"（对象已销毁但 C# 引用还在）。
            //    用 `??` 的后果：把一个**已销毁的 Sprite** 赋给 SpriteRenderer.sprite
            //    → 什么都不画，但碰撞盒还在 → 正是用户报的"能按但没图"。
            if (field != null) return field;

            // 缓存死了（多半是场景重载把原版资源卸了）→ 再抓一次
            EnsureLoaded();
            if (field != null) return field;

            LightLogger.LogWarning("[VanillaAsset] 原版 Sprite 已失效且重新获取失败，退回白图");
            return WhiteSprite;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[VanillaAsset.TryGetSprite]", ex); return default;
        }
    }

    private static void EnsureLoaded()
    {
        try
        {
            // ⚠️ 不能只看 _standardTextPrefab！
            //    它是 Instantiate + DontDestroyOnLoad 出来的，**场景重载后依然活着**，
            //    所以原来那句 `if (_standardTextPrefab != null) return;` 会**永远提前返回**，
            //    里面那几个裸引用原版资源的 Sprite 一旦被卸载，就再也没机会补回来。
            //    现在改成"prefab 在 且 那几个 Sprite 都还活着"才跳过。
            if (_standardTextPrefab != null && VanillaSpritesAlive()) return;

            try
            {
                var twitch = TwitchManager.Instance;
                if (twitch == null) return;
                var popUp = twitch.transform.GetChild(0);

                // 抓到就打标记，从此不再被场景卸载回收
                _fullScreenSprite = Keep(popUp.GetChild(0).GetComponent<SpriteRenderer>().sprite);
                _textButtonSprite = Keep(popUp.GetChild(2).GetComponent<SpriteRenderer>().sprite);
                _popUpBackSprite = Keep(popUp.GetChild(3).GetComponent<SpriteRenderer>().sprite);

                // 文本预制体只在第一次克隆（它本来就是 DontDestroyOnLoad 的，重进场景还在）
                if (_standardTextPrefab == null)
                {
                    _standardTextPrefab = Object.Instantiate(popUp.GetChild(1).GetComponent<TextMeshPro>(), null);
                    _standardTextPrefab.gameObject.hideFlags = HideFlags.HideAndDontSave;
                    Object.Destroy(_standardTextPrefab.GetComponent<SpriteRenderer>());
                    Object.DontDestroyOnLoad(_standardTextPrefab.gameObject);
                }

                _triedInit = true;
                LightLogger.Log("[VanillaAsset] 已(重新)获取原版 Sprite / 文本预制体");
            }
            catch
            {
                // TwitchManager 未就绪或结构变化，下次重试
            }

            try
            {
                _closeButtonSprite = Keep(FindAsset<Sprite>("closeButton"));
            }
            catch { }

            try
            {
                _standardMaskedFontMaterial = FindAsset<Material>("LiberationSans SDF - BlackOutlineMasked");
            }
            catch { }

            try
            {
                _oblongMaskedFontMaterial = FindAsset<Material>("Brook Atlas Material Masked");
            }
            catch { }

            try { _versionFont = FindAsset<TMP_FontAsset>("Barlow-Medium SDF"); } catch { }
            try { _preSpawnFont = FindAsset<TMP_FontAsset>("DIN_Pro_Bold_700 SDF"); } catch { }
            try { _brookFont = FindAsset<TMP_FontAsset>("Brook SDF"); } catch { }
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[VanillaAsset.EnsureLoaded]", ex);
        }
    }

    /// <summary>游戏内兜底标准文本预制体（多级来源，任一可用即克隆）</summary>
    public static TextMeshPro FallbackTextPrefab
    {
        get
        {
            try
            {
                if (_fallbackTextPrefab != null) return _fallbackTextPrefab;

                // 来源1：左下角版本号文本
                TextMeshPro? src = null;
                try
                {
                    var vs = Object.FindObjectOfType<VersionShower>();
                    if (vs != null && vs.text != null) src = vs.text;
                }
                catch { }

                // 来源2：对话框目标文本（游戏中必然存在）
                if (src == null)
                {
                    try
                    {
                        var dialogue = HudManager.Instance?.Dialogue?.target;
                        if (dialogue != null) src = dialogue;
                    }
                    catch { }
                }

                // 来源3：场景内任意 TextMeshPro
                if (src == null)
                {
                    try
                    {
                        var any = Object.FindObjectOfType<TextMeshPro>();
                        if (any != null) src = any;
                    }
                    catch { }
                }

                if (src != null)
                {
                    try
                    {
                        _fallbackTextPrefab = Object.Instantiate(src, null);
                        _fallbackTextPrefab.gameObject.hideFlags = HideFlags.HideAndDontSave;
                        Object.DontDestroyOnLoad(_fallbackTextPrefab.gameObject);
                    }
                    catch { }
                }
                return _fallbackTextPrefab!;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[VanillaAsset.get]", ex); return default;
            }
        }
    }

    private static TextMeshPro? _fallbackTextPrefab;

    /// <summary>获取可用的标准文本预制体（优先 TwitchManager，失败时走多级兜底）</summary>
    public static TextMeshPro GetStandardTextPrefab()
    {
        try
        {
            var prefab = StandardTextPrefab;
            if (prefab != null) return prefab;
            return FallbackTextPrefab;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[VanillaAsset.GetStandardTextPrefab]", ex); return default;
        }
    }

    /// <summary>主菜单预加载（尽早缓存资源，避免游戏内首次打开时资源缺失）</summary>
    public static void Preload()
    {
        try
        {
            EnsureLoaded();
            _ = GetStandardTextPrefab();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[VanillaAsset.Preload]", ex);
        }
    }

    /// <summary>
    /// 按名字从**含未加载资产**的对象里找一个资产。
    /// （Nebula 同款方式：`FindObjectsOfTypeIncludingAssets`，否则部分材质/字体找不到。）
    /// </summary>
    public static T? FindAsset<T>(string name) where T : Object
    {
        var type = Il2CppType.Of<T>();
        // 含未加载资产（Nebula 同款方式），否则部分材质/字体找不到
        foreach (var obj in Object.FindObjectsOfTypeIncludingAssets(type))
        {
            if (obj.name == name)
                return obj.TryCast<T>();
        }
        return null;
    }

    /// <summary>
    /// 诊断：列出游戏里**所有** TMP_FontAsset（含未加载的）。
    /// 找简中字体用 —— 名字对不上时把这批名字打出来就能直接定位。
    /// </summary>
    public static System.Collections.Generic.List<TMP_FontAsset> ListFontAssets()
    {
        var res = new System.Collections.Generic.List<TMP_FontAsset>();
        try
        {
            foreach (var obj in Object.FindObjectsOfTypeIncludingAssets(Il2CppType.Of<TMP_FontAsset>()))
            {
                try
                {
                    if (obj == null) continue;
                    var f = obj.TryCast<TMP_FontAsset>();
                    if (f != null) res.Add(f);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[VanillaAsset.ListFontAssets] {ex.Message}");
        }
        return res;
    }

    /// <summary>玩家自定义菜单预制体（含原版滚动条），懒加载，找不到返回 null</summary>
    public static PlayerCustomizationMenu? PlayerOptionsMenuPrefab
    {
        get
        {
            try
            {
                if (_playerOptionsMenuPrefab == null)
                    _playerOptionsMenuPrefab = FindAsset<PlayerCustomizationMenu>("LobbyPlayerCustomizationMenu");
                return _playerOptionsMenuPrefab;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[VanillaAsset.get]", ex); return default;
            }
        }
    }

    public static AudioClip? FindSoundClip(string name)
    {
        try
        {
            var type = Il2CppType.Of<AudioClip>();
            foreach (var obj in Resources.FindObjectsOfTypeAll(type))
            {
                if (obj.name == name)
                    return obj.TryCast<AudioClip>();
            }
            return null;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[VanillaAsset.FindSoundClip]", ex); return default;
        }
    }

    /// <summary>获取可用按钮背景</summary>
    public static Sprite GetButtonSprite() => TextButtonSprite;
    /// <summary>获取可用窗口背景</summary>
    public static Sprite GetWindowSprite() => PopUpBackSprite;
    /// <summary>获取可用全屏遮罩</summary>
    public static Sprite GetFullScreenSprite() => FullScreenSprite;

    /// <summary>
    /// 创建原版滚动组件（克隆原版玩家菜单的滚动条）
    /// </summary>
    public static Scroller GenerateScroller(Vector2 size, Transform parent, Vector3 scrollBarLocalPos,
        Transform target, FloatRange bounds, float scrollerHeight)
    {
        try
        {
            var scroller = UnityHelper.CreateObject<Scroller>("Scroller", parent, new Vector3(0f, 0f, 5f));
            var collider = scroller.gameObject.AddComponent<BoxCollider2D>();
            collider.size = size;

            // 主菜单等场景无此 prefab 时跳过滚动条克隆，仅保留滚轮滚动
            var prefab = PlayerOptionsMenuPrefab;
            if (prefab != null)
            {
                var barBack = Object.Instantiate(prefab.transform.GetChild(4).FindChild("UI_ScrollbarTrack").gameObject, parent);
                var bar = Object.Instantiate(prefab.transform.GetChild(4).FindChild("UI_Scrollbar").gameObject, parent);
                barBack.transform.localPosition = scrollBarLocalPos + new Vector3(0.12f, 0f, 0f);
                bar.transform.localPosition = scrollBarLocalPos;

                var scrollBar = bar.GetComponent<Scrollbar>();

                scrollBar.parent = scroller;
                scrollBar.graphic = bar.GetComponent<SpriteRenderer>();
                scrollBar.trackGraphic = barBack.GetComponent<SpriteRenderer>();
                scrollBar.trackGraphic.size = new Vector2(scrollBar.trackGraphic.size.x, scrollerHeight);

                var ratio = scrollerHeight / 3.88f;
                scroller.ScrollbarYBounds = new FloatRange(-1.8f * ratio + scrollBarLocalPos.y + 0.4f, 1.8f * ratio + scrollBarLocalPos.y - 0.4f);
                scroller.ScrollbarY = scrollBar;
            }

            scroller.Inner = target;
            scroller.SetBounds(bounds, null);
            scroller.allowY = true;
            scroller.allowX = false;
            scroller.active = true;
            scroller.name = "Scroller";
            scroller.ScrollToTop();

            return scroller;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[VanillaAsset.GenerateScroller]", ex); return default;
        }
    }
}

/// <summary>
/// PassiveButton 设置扩展方法
/// </summary>
public static class ButtonExtensions
{
    /// <summary>
    /// 在 GameObject 上设置 PassiveButton
    /// </summary>
    public static PassiveButton SetUpButton(this GameObject obj, bool hasOnClickHandler,
        SpriteRenderer? renderer = null, Color? color = null, Color? selectedColor = null,
        bool playSound = false)
    {
        try
        {
            var normalColor = color?.ToUnityColor() ?? UnityEngine.Color.white;
            // 悬浮亮起：比正常色更亮
            var hoverColor = selectedColor?.ToUnityColor()
                ?? UnityEngine.Color.Lerp(normalColor, UnityEngine.Color.white, 0.5f);

            var button = obj.GetComponent<PassiveButton>();
            if (button == null)
                button = obj.AddComponent<PassiveButton>();

            button.OnMouseOver = new UnityEvent();
            button.OnMouseOut = new UnityEvent();
            button.OnClick = new Button.ButtonClickedEvent();

            if (renderer != null)
            {
                // 初始颜色即正常色，悬浮时切换为亮色
                renderer.color = normalColor;
            }

            if (renderer != null)
            {
                button.OnMouseOver.AddListener((UnityAction)(() => { renderer.color = hoverColor; }));
                button.OnMouseOut.AddListener((UnityAction)(() => { renderer.color = normalColor; }));
            }

            if (playSound)
            {
                button.OnClick.AddListener((UnityAction)(() =>
                {
                    try { SoundManager.Instance.PlaySound(VanillaAsset.FindSoundClip("UI_Select"), false, 0.8f); } catch { }
                }));

                if (renderer != null)
                {
                    button.OnMouseOver.AddListener((UnityAction)(() =>
                    {
                        try { SoundManager.Instance.PlaySound(VanillaAsset.FindSoundClip("UI_Hover"), false, 0.8f); } catch { }
                    }));
                }
            }

            return button;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[VanillaAsset.SetUpButton]", ex); return default;
        }
    }
}
