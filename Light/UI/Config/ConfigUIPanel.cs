using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Light.Config;
using Light.Patches;
using Light.UI.Window;
using LightInDark.Configuration;
using LightInDark.Core;
using LightInDark.Language;
using LightInDark.UI.Window;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using UColor = UnityEngine.Color;

namespace Light.UI.Config
{
    /// <summary>
    /// 配置项 UI —— 在现有「MOD 设置」页签里，用**原版控件**渲染配置块。
    ///
    /// 结构（每个块）：
    ///   金色分类头（克隆原版 <see cref="CategoryHeaderMasked"/>，可调色）
    ///     └ 若干配置行
    ///        · Bool  → 克隆原版 ToggleOption（勾选框）
    ///        · Int/Float → 克隆原版 NumberOption（- 值 +）
    ///        · Value/Filter → 克隆原版 StringOption（循环切换）
    ///
    /// ⚠️ 关键坑（必须遵守，否则行会被原版逻辑覆盖）：
    ///   1. 原版 NumberOption/ToggleOption/StringOption 都是**每帧/每 FixedUpdate**
    ///      从 <c>data.GetValueString(Value)</c>、<c>data.GetValue()</c> 回写显示，
    ///      而自定义配置项没有 <c>BaseGameSetting</c> → 必须
    ///      <c>option.enabled = false</c> 停掉它们的 Update/FixedUpdate，
    ///      再由我们自己的 <see cref="ConfigRowDriver"/> 驱动显示。
    ///   2. 克隆出来的 <c>PassiveButton.OnClick</c> **自带原版点击逻辑**，
    ///      必须 <c>new ButtonClickedEvent()</c> 整体替换（不是 AddListener 追加）。
    ///   3. <c>z = -2f</c>（与原版行一致），分类头 <c>localScale = 0.63</c>。
    /// </summary>
    public static class ConfigUIPanel
    {
        // ---- 版面对齐原版 GameOptionsMenu.CreateSettings 的常量 ----
        private const float StartY = 0.713f;
        private const float RowX = 0.952f;
        private const float HeaderX = -0.903f;
        private const float HeaderHeight = 0.63f;
        private const float SpacingY = 0.45f;
        private const int MaskLayer = 20;
        private const float RowZ = -2f;

        /// <summary>本页承载的容器（挂在 MOD 设置页下）。</summary>
        private static GameObject _page;
        private static Transform _container;
        /// <summary>当前分类过滤（null = 全部显示）。</summary>
        private static ConfigCategory[]? _categoryFilter;
        /// <summary>单职业模式：只渲染这一个块（职业配置页）。</summary>
        private static ConfigBlock? _singleBlock;
        /// <summary>单职业模式：返回按钮的回调（回职业列表页）。</summary>
        private static Action? _onBack;
        private static readonly List<GameObject> _spawned = new();
        private static readonly Dictionary<ConfigItem, ConfigRowDriver> _drivers = new();
        /// <summary>真正建成功的行数（不是 _spawned.Count，那个在失败时也会加）。</summary>
        private static int _rowCount;

        public static bool Built => _page != null;

        /// <summary>按分类过滤显示配置块（切换 MOD 设置页的标签时调用）。</summary>
        public static void Show(ConfigCategory[] categories, Transform parent)
        {
            _categoryFilter = categories;
            _singleBlock = null;
            _onBack = null;
            Rebuild(parent);
        }

        /// <summary>单职业模式：只渲染一个职业块，顶部带返回按钮（点击回职业列表）。</summary>
        public static void ShowRole(ConfigBlock block, Transform parent, Action onBack)
        {
            Clear();
            _categoryFilter = null;
            _singleBlock = block;
            _onBack = onBack;
            Build(parent);
        }

        /// <summary>
        /// 把托管 MonoBehaviour 注册进 IL2CPP。
        ///
        /// ⚠️⚠️ 这一步**必须有**，否则 <c>AddComponent&lt;ConfigRowDriver&gt;()</c> 直接抛：
        ///   System.TypeInitializationException:
        ///     The type initializer for 'MethodInfoStoreGeneric_AddComponent_Public_T_0`1' threw
        ///   ---> System.NullReferenceException
        ///   at UnityEngine.GameObject.AddComponent[T]()
        /// 托管类型没有对应的 IL2CPP 类 → interop 查不到 method info → 泛型静态构造炸掉。
        /// 表现极具迷惑性：**物体建出来了、层级位置全对、activeInHierarchy 也是 true，
        /// 但部件一个都不显示**（因为异常让后续 Bind/RefreshVisual 全都没跑）。
        /// 本工程其它自定义 MonoBehaviour 都走同样做法：Dispatcher / MetaScreen /
        /// TextFieldBehaviour / LightUtils.AttachComponent。
        /// </summary>
        private static bool _driverRegistered;

        private static void EnsureDriverRegistered()
        {
            if (_driverRegistered) return;
            _driverRegistered = true;
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<ConfigRowDriver>();
            }
            catch (Exception ex)
            {
                // 已注册过会抛，忽略即可
                LightLogger.Log($"[ConfigUIPanel] ConfigRowDriver 注册（可能已注册）：{ex.Message}");
            }
        }

        /// <summary>安全挂组件：类型未注册时补注册重试一次。</summary>
        private static T? AddComponentSafe<T>(GameObject go) where T : MonoBehaviour
        {
            try
            {
                return go.AddComponent<T>();
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel] AddComponent<{typeof(T).Name}> 失败：{ex.Message}，补注册后重试");
                try { ClassInjector.RegisterTypeInIl2Cpp<T>(); } catch { }
                try { return go.AddComponent<T>(); }
                catch (Exception ex2)
                {
                    LightLogger.LogError($"[ConfigUIPanel] AddComponent<{typeof(T).Name}> 重试仍失败", ex2);
                    return null;
                }
            }
        }

        // =====================================================================
        //  外部 API —— 供其它模块往"原版设置页"里叠自己的配置 UI
        //  【借鉴 TONE】ToN 把所有设置项抽象成 OptionItem 列表，再由
        //  GameOptionsMenuPatch 统一铺到原版 settingsContainer 里
        //  （TONE\Patches\GameOptionsMenuPatch.cs:54-185）。
        //  这里给出等价的对外接口，外部只需注册 ConfigBlock 即可，不用碰 UI。
        // =====================================================================

        /// <summary>
        /// 本面板当前是否应该接手某个 <see cref="GameOptionsMenu"/> 的铺行。
        ///
        /// 判定依据：这个 menu 是否就是"MOD 设置"页签背后的那个。
        /// 我们把它记在 <see cref="_hostMenu"/>（由 GameSettingMenuPatch 在切页时登记）。
        /// 未登记 → 返回 false → 原版页签完全不受影响。
        /// </summary>
        public static bool ShouldHandleCreateSettings(GameOptionsMenu menu)
        {
            if (menu == null) return false;
            if (_hostMenu == null) return false;
            return menu.GetInstanceID() == _hostMenu.GetInstanceID();
        }

        /// <summary>登记"MOD 设置"页签背后的那个 GameOptionsMenu（切页时调用）。</summary>
        public static void SetHostMenu(GameOptionsMenu? menu)
        {
            _hostMenu = menu;
        }

        /// <summary>
        /// 【对外主入口】把当前过滤条件下的配置块铺进原版 <c>settingsContainer</c>。
        ///
        /// 由 <c>ConfigRowPatches.CreateSettingsPrefix</c> 在原版铺行时机调用。
        /// 外部模块**不需要**调用这个 —— 只要 <see cref="ConfigRegistry"/> 里注册了
        /// <see cref="ConfigBlock"/>，切到对应页签时就会自动出现。
        /// </summary>
        public static void BuildIntoVanilla(GameOptionsMenu menu)
        {
            try
            {
                // ⚠️ 必须用 DestroySpawned（会真的销毁物体），不能用 _spawned.Clear()：
                //    后者只清列表 → 行变成孤儿留在容器里 → 和 Build() 那条路径叠成两份。
                DestroySpawned();

                var container = menu.settingsContainer;
                if (container == null)
                {
                    LightLogger.LogWarning("[ConfigUIPanel] settingsContainer 为 null，无法铺行");
                    return;
                }

                _container = container;

                // 先清掉原版自己会画的东西（地图预览 + 原版设置行 + 分类头），否则两层叠在一起
                ClearVanillaContent(menu);

                // y 从原版起点开始（可能已收回地图预览的高度，见 StartYFor）
                float startY = StartYFor(menu);
                float y = startY;

                foreach (var block in ConfigRegistry.Blocks)
                {
                    if (!MatchesFilter(block)) continue;
                    y = BuildBlock(block, y);
                }

                LightLogger.Log($"[ConfigUIPanel] 已铺入原版容器 '{container.name}'：行 {_rowCount} 个");
                LogContainerState(menu, "BuildIntoVanilla");

                UpdateScrollBounds(menu, startY, y);
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigUIPanel.BuildIntoVanilla]", ex);
            }
        }

        /// <summary>
        /// 【自动检测滚动条】按"内容高度 vs 可视高度"设置滚动范围。
        ///
        /// 原版 Scroller 自己就会隐藏滚动条（Scroller.cs:310-320）：
        ///     if (!showY || ContentYBounds.min >= ContentYBounds.max) ScrollbarY.Toggle(false);
        /// 条件是 **min >= max**。
        ///
        /// 我之前的实现只调了 SetYBoundsMax，**从来没设过 min** →
        /// ContentYBounds 保持字段默认值 `new FloatRange(-10f, 10f)` 的 min = -10，
        /// 于是永远 min &lt; max → 滚动条永远显示（用户："这个滚动条就不应该有"）。
        ///
        /// 现在：
        ///   · 内容装得下 → min = max = 0 → 原版自动隐藏，且滚不动；
        ///   · 内容装不下 → min = 0、max = 溢出高度 → 正常滚动。
        /// 可视高度从 menu.MaskArea（原版的遮罩矩形）量出来，不靠猜。
        /// </summary>
        private static void UpdateScrollBounds(GameOptionsMenu menu, float startY, float lastY)
        {
            try
            {
                var sb = menu.scrollBar;
                if (sb == null) return;

                float contentHeight = Mathf.Max(0f, startY - lastY) + 0.35f;   // 内容总高
                float viewport = MeasureViewportHeight(menu);
                float overflow = Mathf.Max(0f, contentHeight - viewport);
                bool needBar = overflow > 0.01f;

                sb.SetYBoundsMin(0f);
                sb.SetYBoundsMax(needBar ? overflow : 0f);

                // 内容从顶部开始（Inner.y = 0）
                var inner = sb.Inner;
                if (inner != null)
                {
                    var lp = inner.localPosition;
                    inner.localPosition = new Vector3(lp.x, 0f, lp.z);
                }

                sb.UpdateScrollBars();   // 让原版按 min/max 决定 Toggle

                LightLogger.Log($"[ConfigUIPanel] 滚动条自动检测：内容高 {contentHeight:F2} / 可视高 {viewport:F2} " +
                                $"→ {(needBar ? $"需要（溢出 {overflow:F2}）" : "不需要，已隐藏")}");
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.UpdateScrollBounds] {ex.Message}");
            }
        }

        /// <summary>
        /// 量出滚动可视区高度（容器局部单位）。
        /// 用原版的遮罩 sprite `MaskArea` 的高度作为可视高度；取不到就退回一个保守值。
        /// </summary>
        private static float MeasureViewportHeight(GameOptionsMenu menu)
        {
            try
            {
                var mask = menu.MaskArea;
                if (mask == null || mask.sprite == null) return 3.2f;   // 保守兜底

                float hWorld = mask.bounds.size.y;
                var inner = menu.scrollBar?.Inner;
                if (inner != null && hWorld > 0.0001f)
                {
                    // 世界高度 → 容器局部高度（容器没有缩放时两者相同）
                    float hLocal = Mathf.Abs(inner.InverseTransformVector(new Vector3(0f, hWorld, 0f)).y);
                    if (hLocal > 0.01f && hLocal < 30f) return hLocal;
                }
                return hWorld > 0.01f ? hWorld : 3.2f;
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.MeasureViewportHeight] {ex.Message}");
                return 3.2f;
            }
        }

        /// <summary>
        /// 起始 y：把被我们藏掉的**地图预览**占的高度收回来。
        ///
        /// 用户反馈："上面会空一大块，可能是原版选地图的地方"。
        /// 就是它 —— 原版把 MapPicker 放在设置列表最上面（GameOptionsMenu.cs:85
        /// `Children.Add(this.MapPicker)`），我们为了不重叠把它 SetActive(false) 了，
        /// 那块空间就空着。这里量出它的高度并补回起始高度，让内容顶上来。
        /// （量不到就维持原版起点，不会更糟。）
        /// </summary>
        private static float StartYFor(GameOptionsMenu menu)
        {
            try
            {
                // ⚠️ 只量一次并缓存。
                // 重建时地图预览已经被我们 SetActive(false)，未激活渲染器的 bounds 可能量到 0
                // → 偏移丢失 → 顶上那块空缺又回来（用户报的正是这个）。
                // 地图预览的高度是预制体属性，量一次就够了。
                if (_mapPickerHeight < 0f)
                    _mapPickerHeight = MeasureMapPickerHeight(menu);

                if (_mapPickerHeight <= 0.01f) return StartY;

                float startY = StartY + _mapPickerHeight;
                return startY;
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.StartYFor] {ex.Message}");
                return StartY;
            }
        }

        /// <summary>缓存的地图预览高度（&lt;0 表示还没量过）。</summary>
        private static float _mapPickerHeight = -1f;

        /// <summary>量出地图预览占的高度（容器局部单位）。必须在隐藏它**之前**调用。</summary>
        private static float MeasureMapPickerHeight(GameOptionsMenu menu)
        {
            try
            {
                var mp = menu.MapPicker;
                if (mp == null) return 0f;

                // 取地图预览里最高的渲染器高度
                float h = 0f;
                foreach (var sr in mp.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (sr == null || sr.sprite == null) continue;
                    float sy = sr.bounds.size.y;
                    if (sy > h) h = sy;
                }
                if (h <= 0.01f) return 0f;

                var inner = menu.scrollBar?.Inner;
                float hLocal = h;
                if (inner != null)
                    hLocal = Mathf.Abs(inner.InverseTransformVector(new Vector3(0f, h, 0f)).y);

                // 合理性检查：0.2~3 之间才采用（避免量错把内容顶飞）
                if (hLocal < 0.2f || hLocal > 3f) return 0f;

                LightLogger.Log($"[ConfigUIPanel] 收回地图预览高度 {hLocal:F2} → 起始 y = {StartY + hLocal:F2}");
                return hLocal;
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.MeasureMapPickerHeight] {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// 一次性诊断：打印容器里"我们的"子物体数量，用于验证行有没有重复。
        /// 若这个数 &gt; _spawned.Count，说明有孤儿行没被销毁。
        /// </summary>
        private static void LogContainerState(GameOptionsMenu menu, string where)
        {
            try
            {
                var inner = menu.scrollBar?.Inner ?? menu.settingsContainer;
                if (inner == null) return;

                int ours = 0;
                for (int i = 0; i < inner.childCount; i++)
                {
                    var c = inner.GetChild(i);
                    if (c != null && c.name.StartsWith(OurPrefix, StringComparison.Ordinal)) ours++;
                }

                LightLogger.Log($"[ConfigUIPanel.Diag] {where}：容器 '{inner.name}' 子物体 {inner.childCount} 个，" +
                                $"其中我们的 {ours} 个（登记 {_spawned.Count} 个）{(ours > _spawned.Count ? " ← ⚠️ 有孤儿行！" : "")}");
            }
            catch { }
        }

        /// <summary>
        /// 【唯一销毁入口】彻底清掉我们建过的一切（行、分类头、返回按钮、页面对象）。
        ///
        /// ⚠️ 存在的意义（这是"行重复"的根因）：
        ///   之前有三处各自清理，其中 <see cref="BuildIntoVanilla"/> 里只写了
        ///   <c>_spawned.Clear()</c> —— **只清列表、不销毁物体**！
        ///   于是那些行变成没人引用的孤儿留在容器里，再也清不掉。
        ///   而我们的页签有**两条建行路径**：
        ///     ① 原版 CreateSettings 时机 → CreateSettingsPrefix → BuildIntoVanilla
        ///     ② 切页签 Show() → Rebuild() → Build()
        ///   两条都往同一个 settingsContainer 里加行 → 用户看到的就是"行重复"
        ///   （"正常 4 行、现在 6 行"），并且第二次之后可见性判定被污染
        ///   （"只有第一次打勾会出现附带项"）。
        ///
        ///   现在所有清理都走这一个函数，并且 <see cref="Build"/> 开头**无条件**先调它，
        ///   所以无论从哪条路径进来，容器里永远只有一套我们的行。
        /// </summary>
        private static void DestroySpawned()
        {
            try
            {
                foreach (var go in _spawned)
                    if (go != null) Object.Destroy(go);
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.DestroySpawned] {ex.Message}");
            }

            _spawned.Clear();
            _drivers.Clear();
            _rowCount = 0;
            RowMap.Clear();

            if (_page != null) { Object.Destroy(_page); _page = null; _container = null; }
        }

        /// <summary>宿主 GameOptionsMenu（"MOD 设置"页签背后的那个）。</summary>
        private static GameOptionsMenu? _hostMenu;

        /// <summary>
        /// 只清掉已生成的行，保留面板对象与宿主登记。
        /// 切页签时用：让下一次 CreateSettings 在干净的容器里重铺。
        /// </summary>
        public static void ClearRowsOnly() => DestroySpawned();
        ///
        /// 【借鉴 TONE】ToN 的做法是把行挂到**原版 GameOptionsMenu 自己的 settingsContainer**
        /// （TONE\Patches\GameOptionsMenuPatch.cs:129-135），而不是自己新建一个容器。
        /// 这样做的原因（实测踩到）：
        ///   · settingsContainer 的原版层级关系、缩放、遮罩、以及它父级的 z 都是对的；
        ///   · 我们自己 new 一个物体挂在 ROLES TAB 下时，父链 z 会累加偏离，
        ///     结果被菜单自己的不透明背景盖住 → 表现为"构建日志一切正常、屏幕上一片空白"。
        ///
        /// 因此：能用原版容器就用原版容器，取不到才退回自建容器。
        /// </summary>
        private static Transform ResolveRowContainer(Transform fallback)
        {
            try
            {
                var host = _hostMenu ?? _templates;

                // ⚠️ 必须用 Scroller.Inner，**不能**直接读 settingsContainer 字段。
                //
                // 原版 GameOptionsMenu.settingsContainer 是 [SerializeField] 私有字段，
                // 在**没跑过 Start/Initialize 的克隆体**上读它拿到的是错的
                // （实测拿到 'SliderInner'——一个滑块，childCount=22，完全不是行容器）。
                // 而 Scroller.Inner 是同一个物体的**公开字段**，任何克隆体上都能正确读到。
                // 原版自己也是把行 Instantiate 到 settingsContainer（== Scroller.Inner）下的。
                var inner = TryGetScrollerInner(host);
                if (inner != null && inner.gameObject.activeInHierarchy)
                {
                    LightLogger.Log($"[ConfigUIPanel] 行容器使用 Scroller.Inner '{inner.name}' " +
                                    $"(localPos={inner.localPosition}, childCount={inner.childCount})");
                    return inner;
                }
                if (inner != null)
                    LightLogger.LogWarning($"[ConfigUIPanel] Scroller.Inner '{inner.name}' 未激活，继续找备选");

                var hostSc = host?.settingsContainer;
                if (hostSc != null && hostSc.gameObject.activeInHierarchy)
                {
                    LightLogger.Log($"[ConfigUIPanel] 行容器使用 settingsContainer '{hostSc.name}' " +
                                    $"(childCount={hostSc.childCount})");
                    return hostSc;
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.ResolveRowContainer] {ex.Message}");
            }
            return fallback;
        }

        /// <summary>从 GameOptionsMenu 的 Scroller 上取 Inner（行真正该挂的容器）。</summary>
        private static Transform? TryGetScrollerInner(GameOptionsMenu? menu)
        {
            try
            {
                var scroller = menu?.scrollBar;
                return scroller?.Inner;
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.TryGetScrollerInner] {ex.Message}");
                return null;
            }
        }

        /// <summary>在给定父级下构建配置面板（幂等：已建则先彻底清掉再重铺）。</summary>
        public static void Build(Transform parent)
        {
            try
            {
                // ⚠️ 开头**无条件**清一次，而不是 `if (_page != null) { Refresh(); return; }`。
                //
                // 原来那个提前返回有个致命组合：BuildIntoVanilla（原版 CreateSettings 路径）
                // 不设 _page，所以它铺完行之后 _page 仍是 null → Build 认为"没建过" → 又铺一遍
                // → 同一个容器里出现两套行（用户看到的"行重复"）。
                // 现在 Build 永远从一个干净的容器开始，两条路径谁先谁后都只留一套。
                DestroySpawned();

                EnsureDriverRegistered();

                _page = NewUIObject("LightConfigPage", parent, new Vector3(0f, 0f, RowZ));

                // 行容器优先用原版 settingsContainer（TONE 同做法，见 ResolveRowContainer 注释）
                _container = ResolveRowContainer(_page.transform);

                // ⚠️ 顺序很重要：**先量地图预览的高度，再隐藏它**。
                //    反过来（先隐藏再量）在重建时会量到 0 → 收回的偏移丢失 →
                //    用户看到的"点击一次配置项后那块被地图占的空缺又出现了"。
                var hostForClean = _hostMenu ?? _templates;
                float startY = StartY;
                if (hostForClean != null)
                {
                    startY = StartYFor(hostForClean);
                    ClearVanillaContent(hostForClean);
                }

                float lastY;
                if (_singleBlock != null)
                {
                    // 单职业模式（独立新页面，标签行已隐藏）：返回按钮在原标签行位置，仅渲染这一个块
                    float y = AddBackButton(0.8f);
                    startY = y;
                    lastY = BuildBlock(_singleBlock, y);
                }
                else
                {
                    // 渲染所有已注册配置块（按分类过滤：调试块/职业块均在 ConfigRegistry 中）
                    float y = startY;
                    foreach (var block in ConfigRegistry.Blocks)
                    {
                        if (!MatchesFilter(block)) continue;
                        y = BuildBlock(block, y);
                    }
                    lastY = y;
                }

                LightLogger.Log($"[ConfigUIPanel] 已构建配置面板：行 {_rowCount} 个，子物体 {_spawned.Count} 个");
                if (hostForClean != null)
                {
                    LogContainerState(hostForClean, "Build");
                    UpdateScrollBounds(hostForClean, startY, lastY);
                }
                LogDiagnostics(parent);
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigUIPanel.Build]", ex);
            }
        }

        /// <summary>
        /// 打印原版**可见**设置行的实际位置，作为我们内容的对齐基准。
        /// 这是"我们的行看不见"最直接的对照物：同样的相机、同样的 layer，
        /// 原版行能看见 → 差别只可能在 position / 父容器 / sortingOrder。
        /// </summary>
        private static void DumpVisibleVanillaRow(Camera uiCam)
        {
            try
            {
                // 原版 GameOptionsMenu 的真正内容容器 settingsContainer
                var menu = _templates;
                if (menu != null)
                {
                    var sc = menu.settingsContainer;
                    if (sc != null)
                    {
                        var vp = uiCam.WorldToViewportPoint(sc.position);
                        LightLogger.Log($"[ConfigUIPanel.Diag] 原版 settingsContainer '{sc.name}' " +
                                        $"localPos={sc.localPosition} worldPos={sc.position} " +
                                        $"lossyScale={sc.lossyScale} layer={sc.gameObject.layer} " +
                                        $"childCount={sc.childCount} → viewport=({vp.x:F3},{vp.y:F3},{vp.z:F3})");
                    }

                    var children = menu.Children;
                    if (children != null && children.Count > 0)
                    {
                        for (int i = 0; i < children.Count && i < 3; i++)
                        {
                            var ob = children[i];
                            if (ob == null) continue;
                            var vp = uiCam.WorldToViewportPoint(ob.transform.position);
                            var sr = ob.GetComponentInChildren<SpriteRenderer>(true);
                            LightLogger.Log($"[ConfigUIPanel.Diag] 原版行[{i}] {ob.name} " +
                                            $"localPos={ob.transform.localPosition} worldPos={ob.transform.position} " +
                                            $"lossyScale={ob.transform.lossyScale} active={ob.gameObject.activeInHierarchy} " +
                                            $"→ viewport=({vp.x:F3},{vp.y:F3},{vp.z:F3}) " +
                                            $"sr={(sr == null ? "无" : $"{sr.name}/{sr.sortingLayerName}#{sr.sortingOrder}")}");
                        }
                    }
                    else
                    {
                        LightLogger.Log("[ConfigUIPanel.Diag] 原版 GameOptionsMenu.Children 为空（它还没建过行）");
                    }
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.DumpVisibleVanillaRow] {ex.Message}");
            }
        }

        /// <summary>找绘制 UI 层(5)的那台相机。</summary>
        private static Camera FindUICamera()
        {
            try
            {
                int uiLayer = LayerExpansion.GetUILayer();
                Camera best = null;
                foreach (var cam in Camera.allCameras)
                {
                    if (cam == null) continue;
                    if ((cam.cullingMask & (1 << uiLayer)) == 0) continue;
                    if (best == null || cam.depth > best.depth) best = cam;
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>
        /// 递归打印层级 + 每个渲染器状态。
        /// 排查"物体在、但画不出来"必须看到这一层：渲染器到底存不存在、
        /// sprite 是否为 null（原版资源被卸载会变假 null）、sortingOrder 是否被盖。
        /// </summary>
        private static void DumpHierarchy(Transform t, int depth)
        {
            try
            {
                if (t == null || depth > 4) return;
                string pad = new string(' ', depth * 2);

                var srs = t.GetComponents<SpriteRenderer>();
                foreach (var sr in srs)
                {
                    LightLogger.Log($"[ConfigUIPanel.Diag] {pad}SR {t.name} active={sr.enabled}/{sr.gameObject.activeInHierarchy} " +
                                    $"sprite={(sr.sprite == null ? "null" : sr.sprite.name)} color={sr.color} " +
                                    $"size={sr.size} drawMode={sr.drawMode} sortingLayer={sr.sortingLayerName}#{sr.sortingOrder} " +
                                    $"sortingLayerID={sr.sortingLayerID} bounds={sr.bounds.size}");
                }

                var tmps = t.GetComponents<TextMeshPro>();
                foreach (var tmp in tmps)
                {
                    var mr = tmp.GetComponent<MeshRenderer>();
                    LightLogger.Log($"[ConfigUIPanel.Diag] {pad}TMP {t.name} active={tmp.enabled}/{tmp.gameObject.activeInHierarchy} " +
                                    $"text=\"{tmp.text}\" color={tmp.color} fontSize={tmp.fontSize} " +
                                    $"mr={(mr == null ? "无" : $"enabled={mr.enabled} sortingLayer={mr.sortingLayerName}#{mr.sortingOrder}")}");
                }

                for (int i = 0; i < t.childCount; i++)
                    DumpHierarchy(t.GetChild(i), depth + 1);
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.DumpHierarchy] {ex.Message}");
            }
        }

        /// <summary>
        /// 一次性诊断：把"看不见"相关的关键事实全打出来（照 AGENTS.md §4.8 的做法，别猜）。
        /// 打印层级 / 父链 / activeInHierarchy / layer / 渲染器状态 / 相机 cullingMask。
        /// </summary>
        private static void LogDiagnostics(Transform parent)
        {
            try
            {
                LightLogger.Log($"[ConfigUIPanel.Diag] 注册块数={ConfigRegistry.Blocks.Count} 配置项数={ConfigRegistry.All.Count} " +
                                $"可见块={System.Linq.Enumerable.Count(ConfigRegistry.Blocks, b => MatchesFilter(b))}");

                foreach (var b in ConfigRegistry.Blocks)
                {
                    var items = new System.Text.StringBuilder();
                    foreach (var it in b.Items)
                        items.Append($"[{it.Key} {it.Type} 值={it.GetValueText()} vis={it.IsVisible}] ");
                    LightLogger.Log($"[ConfigUIPanel.Diag] 块 {b.Key} 分类={b.Category} " +
                                    $"过滤通过={MatchesFilter(b)} 项={items}");
                }

                if (_page == null) { LightLogger.Log("[ConfigUIPanel.Diag] _page 为 null"); return; }

                // ⚠️ 决定性检查：我们的内容在相机前方吗？
                // WorldToViewportPoint 的 z 为负 = 在相机**背后** → 一定不渲染。
                var uiCam = FindUICamera();
                if (uiCam != null)
                {
                    var vp = uiCam.WorldToViewportPoint(_page.transform.position);
                    LightLogger.Log($"[ConfigUIPanel.Diag] 视口检查(UI相机 {uiCam.name}) cam.worldPos={uiCam.transform.position} " +
                                    $"cam.near={uiCam.nearClipPlane} cam.far={uiCam.farClipPlane} | " +
                                    $"page.worldPos={_page.transform.position} → viewport=({vp.x:F3},{vp.y:F3},{vp.z:F3}) " +
                                    $"在相机前方={vp.z > 0f} 在视口内={(vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f && vp.z > 0f)}");

                    // 对照：原版角色页自己的内容在哪（同相机下）
                    if (_templates != null)
                    {
                        var vpT = uiCam.WorldToViewportPoint(_templates.transform.position);
                        LightLogger.Log($"[ConfigUIPanel.Diag] 对照原版 {_templates.name} worldPos={_templates.transform.position} " +
                                        $"→ viewport=({vpT.x:F3},{vpT.y:F3},{vpT.z:F3}) 在相机前方={vpT.z > 0f}");
                    }

                    // ⚠️ 决定性对照：原版**可见**的设置行在哪（它就是我们要对齐的目标）。
                    // 抓原版 GameOptionsMenu 已建好的子行，看它们的 worldPos / 父容器 z。
                    DumpVisibleVanillaRow(uiCam);
                }

                // 把所有"可能盖住我们"的渲染器按 z 排序打出来（同层竞争者）
                try
                {
                    var parentRoot = _page.transform.root;
                    var peers = parentRoot.GetComponentsInChildren<SpriteRenderer>(true);
                    int shown = 0;
                    var list = new List<(float z, string info)>();
                    foreach (var sr in peers)
                    {
                        if (sr == null) continue;
                        var wp = sr.transform.position;
                        list.Add((wp.z, $"{sr.name}@{sr.transform.parent?.name} z={wp.z:F2} sprite={(sr.sprite == null ? "null" : sr.sprite.name)} order={sr.sortingOrder}"));
                    }
                    list.Sort((a, b) => a.z.CompareTo(b.z));
                    foreach (var e in list)
                    {
                        LightLogger.Log($"[ConfigUIPanel.Diag] 同层SR(z排序) {e.info}");
                        if (++shown >= 30) break;
                    }
                }
                catch (Exception ex2)
                {
                    LightLogger.LogWarning($"[ConfigUIPanel.Diag] 同层SR枚举失败：{ex2.Message}");
                }

                // 父链 4 层
                var cur = _page.transform;
                for (int d = 0; cur != null && d < 5; d++, cur = cur.parent)
                    LightLogger.Log($"[ConfigUIPanel.Diag] 父链[{d}] {cur.name} active={cur.gameObject.activeSelf} " +
                                    $"activeInHierarchy={cur.gameObject.activeInHierarchy} layer={cur.gameObject.layer} " +
                                    $"localPos={cur.localPosition} worldPos={cur.position} localScale={cur.localScale}");

                LightLogger.Log($"[ConfigUIPanel.Diag] _page activeInHierarchy={_page.activeInHierarchy} " +
                                $"childCount={_page.transform.childCount}");

                // 逐个打印我们建的子物体
                for (int i = 0; i < _page.transform.childCount; i++)
                {
                    var c = _page.transform.GetChild(i);
                    if (c == null) continue;
                    var sr = c.GetComponent<SpriteRenderer>();
                    var tmp = c.GetComponentInChildren<TextMeshPro>(true);
                    LightLogger.Log($"[ConfigUIPanel.Diag]  子[{i}] {c.name} active={c.gameObject.activeInHierarchy} " +
                                    $"layer={c.gameObject.layer} localPos={c.localPosition} " +
                                    $"worldPos={c.position} lossyScale={c.lossyScale} " +
                                    $"sr={(sr == null ? "无" : $"sprite={(sr.sprite == null ? "null" : sr.sprite.name)} size={sr.size} color={sr.color} sortingOrder={sr.sortingOrder}")} " +
                                    $"tmp={(tmp == null ? "无" : $"\"{tmp.text}\" enabled={tmp.enabled}")}");

                    // 递归打印行的完整层级（含每个渲染器）—— 定位"为什么画不出来"
                    DumpHierarchy(c, 1);
                }

                // 相机 cullingMask（layer 5 是否被渲染）
                foreach (var cam in Camera.allCameras)
                {
                    if (cam == null) continue;
                    bool has5 = (cam.cullingMask & (1 << 5)) != 0;
                    LightLogger.Log($"[ConfigUIPanel.Diag] 相机 {cam.name} depth={cam.depth} cullingMask={cam.cullingMask} 含layer5={has5} " +
                                    $"ortho={cam.orthographic} size={cam.orthographicSize}");
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.LogDiagnostics] {ex.Message}");
            }
        }

        /// <summary>块是否通过当前分类过滤。</summary>
        private static bool MatchesFilter(ConfigBlock block)
        {
            if (_categoryFilter == null || _categoryFilter.Length == 0) return true;
            foreach (var c in _categoryFilter)
                if (block.Category == c) return true;
            return false;
        }

        /// <summary>清空重建（值变化导致可见性变化时调用）。</summary>
        public static void Rebuild(Transform parent)
        {
            Clear();
            Build(parent);
        }

        /// <summary>清空（销毁一切并复原状态）。</summary>
        public static void Clear() => DestroySpawned();

        // =====================================================================
        //  构建
        // =====================================================================

        private static float BuildBlock(ConfigBlock block, float startY = StartY)
        {
            if (block == null) return startY;

            float y = startY;
            y = AddCategoryHeader(block, y);

            foreach (var item in block.Items)
            {
                if (!item.IsVisible) continue;         // 依赖项未满足 → 不建行
                y = AddConfigRow(item, y);
            }

            return y;   // 返回最后用掉到哪个 y，供滚动条算高度
        }

        /// <summary>单职业模式顶部的返回按钮，返回下一个 y。</summary>
        private static float AddBackButton(float y)
        {
            var go = NewUIObject("LightConfigBack", _container, new Vector3(HeaderX + 0.35f, y, RowZ));
            _spawned.Add(go);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = GetRoundedSprite();
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = new Vector2(0.9f, 0.32f);
            sr.color = new UColor(0.2f, 0.2f, 0.2f, 0.9f);

            MenuTextTemplate.Create(go.transform, new Vector3(0f, 0f, -0.1f), "< 返回", 0.8f);

            var col = go.AddComponent<BoxCollider2D>();
            col.isTrigger = true;
            col.size = new Vector2(0.9f, 0.32f);

            var pb = go.SetUpButton(true, null, null, null, false);
            pb.OnClick.AddListener((UnityAction)(() => _onBack?.Invoke()));

            return y - 0.42f;
        }

        /// <summary>金色分类头：克隆原版 CategoryHeaderMasked。</summary>
        private static float AddCategoryHeader(ConfigBlock block, float y)
        {
            try
            {
                var origin = FindHeaderTemplate();
                if (origin == null)
                {
                    LightLogger.LogWarning("[ConfigUIPanel] 找不到原版分类头模板(CategoryHeaderMasked)，跳过分类头");
                    return y;
                }

                var header = Object.Instantiate(origin, Vector3.zero, Quaternion.identity, _container);
                header.name = $"LightConfigHeader_{block.Key}";
                header.transform.localScale = Vector3.one * HeaderHeight;
                header.transform.localPosition = new Vector3(HeaderX, y, RowZ);
                header.gameObject.SetActive(true);
                _spawned.Add(header.gameObject);

                // 头文字：SetHeader 走 StringNames，我们用翻译槽位塞自定义文本
                SetHeaderText(header, block);

                y -= HeaderHeight;
                return y;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigUIPanel.AddCategoryHeader]", ex);
                return y;
            }
        }

        /// <summary>
        /// 设置分类头文字与颜色。
        /// 原版 SetHeader 只接受 StringNames，所以我们直接写它的 Title 文本
        /// （比挪用翻译槽位更直接，且不会影响别处）。
        /// </summary>
        private static void SetHeaderText(CategoryHeaderMasked header, ConfigBlock block)
        {
            try
            {
                header.SetHeader(StringNames.None, MaskLayer);   // 先走一遍原版：设 mask/stencil

                // 再覆盖文字
                var tmp = header.GetComponentInChildren<TextMeshPro>(true);
                if (tmp != null)
                {
                    var tr = tmp.GetComponent<TextTranslatorTMP>();
                    if (tr != null) tr.enabled = false;          // 别被翻译器改回去
                    tmp.text = block.DisplayName;
                }

                // 调色：分类头的背景/分隔线是 SpriteRenderer
                if (block.HeaderColor.HasValue)
                {
                    var color = block.HeaderColor.Value;
                    foreach (var sr in header.GetComponentsInChildren<SpriteRenderer>(true))
                    {
                        if (sr == null) continue;
                        sr.color = color;
                    }
                    if (tmp != null) tmp.color = color;
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.SetHeaderText] {ex.Message}");
            }
        }

        // =====================================================================
        //  原版行控件渲染（方案 B）
        //
        //  克隆源不是场景里现成的行，而是 GameOptionsMenu 上的私有 [SerializeField]
        //  预制体字段（interop 里是属性）：
        //     checkboxOrigin / numberOptionOrigin / stringOptionOrigin / categoryHeaderOrigin
        //  ⚠️ 别再回退成 GetComponentInChildren<ToggleOption>()：规则页的行要到
        //     CreateSettings() 才存在，那之前场景里一个都没有（这是上一版"啥也没看到"的原因之一）。
        //
        //  原版控件的 Initialize/FixedUpdate/Increase/Decrease/UpdateValue 都依赖
        //  data(BaseGameSetting)，自定义配置项没有 → 由 ConfigRowPatches 按实例 ID 拦截。
        // =====================================================================

        /// <summary>实例 ID → 配置项。原版控件的 prefix 靠它认出"这行是我的"。</summary>
        internal static readonly Dictionary<int, ConfigItem> RowMap = new();

        /// <summary>模板源（提供私有预制体字段的原版菜单）。</summary>
        private static GameOptionsMenu _templates;

        /// <summary>模板源是否已就绪。</summary>
        public static bool TemplatesReady => _templates != null;

        /// <summary>登记模板源；到位后若面板已建但一行都没成，补建一次。</summary>
        internal static void SetTemplateSource(GameOptionsMenu menu)
        {
            if (menu == null) return;
            bool wasNull = _templates == null;
            _templates = menu;

            if (wasNull && _page != null && RowMap.Count == 0)
            {
                var parent = _container != null ? _container.parent : null;
                LightLogger.Log("[ConfigUIPanel] 模板源到位，重建配置面板");
                if (parent != null) Rebuild(parent);
            }
        }

        /// <summary>
        /// 合成一个原版 BaseGameSetting，喂给 SetUpFromData。
        ///
        /// ⚠️ 这是 ToN 的核心做法，也是之前"行建了但什么都不显示"的关键：
        ///   原版 ToggleOption/NumberOption/StringOption 的一切显示都由 <c>Data</c> 驱动
        ///   （Initialize/FixedUpdate 读 <c>data.GetValueString(...)</c>、<c>data.GetValue()</c>）。
        ///   只 Instantiate 而不 SetUpFromData → <c>Data</c> 为 null → 原版逻辑要么空引用、
        ///   要么一个部件都不点亮 → 界面上什么都看不到。
        ///   做法：ScriptableObject.CreateInstance&lt;XxxGameSetting&gt;() 造一个真 setting 交出去，
        ///   之后**不需要**任何 prefix 拦截，原版自己就会正确显示。
        /// </summary>
        private static BaseGameSetting? BuildSetting(ConfigItem item)
        {
            try
            {
                BaseGameSetting setting = item.Type switch
                {
                    ConfigType.Bool => ScriptableObject.CreateInstance<CheckboxGameSetting>(),
                    ConfigType.Value or ConfigType.Filter => ScriptableObject.CreateInstance<StringGameSetting>(),
                    _ => item.Type == ConfigType.Float
                        ? ScriptableObject.CreateInstance<FloatGameSetting>()
                        : (BaseGameSetting)ScriptableObject.CreateInstance<IntGameSetting>(),
                };

                if (setting == null) return null;

                // ⚠️ Title 用**我们借来的翻译槽位**，不要用 StringNames.Accept。
                // 之前写 Accept 导致点开关时原版 Toggle() 回查 `Accept` 这个 title，
                // 触发 "Could not update value of Accept" + ToggleOption.Toggle 的 NRE。
                setting.Title = ConfigTranslationPatch.SlotFor(item);

                switch (setting)
                {
                    case CheckboxGameSetting cb:
                        cb.Type = OptionTypes.Checkbox;
                        // ⚠️ OptionName **保持 Invalid**（不要给 VisualTasks 之类）。
                        // 之前为了消 "Could not update value of ..." 日志塞了
                        // `BoolOptionNames.VisualTasks`，结果被原版 Initialize 用来
                        // 去**真实游戏规则**里读值 → 勾选框显示的是"可视任务"的真值，
                        // 不是我们的配置（用户报的"要点两次才能变成未选中"）。
                        // 现在 UpdateValue 已被 Prefix 跳过，日志问题不存在了，
                        // 这里就不再借用任何真实选项名，避免误读误写真实规则。
                        break;

                    case IntGameSetting i:
                        i.Type = OptionTypes.Int;
                        i.Value = item.GetInt();
                        i.Increment = (int)Math.Max(1f, item.Step);
                        i.ValidRange = new IntRange((int)item.Min, (int)item.Max);
                        i.ZeroIsInfinity = false;
                        i.FormatString = item.SuffixText();
                        i.SuffixType = ToSuffix(item.SuffixText());
                        break;

                    case FloatGameSetting f:
                        f.Type = OptionTypes.Float;
                        f.Value = item.GetFloat();
                        f.Increment = item.Step;
                        f.ValidRange = new FloatRange(item.Min, item.Max);
                        f.ZeroIsInfinity = false;
                        f.FormatString = item.SuffixText();
                        f.SuffixType = ToSuffix(item.SuffixText());
                        break;

                    case StringGameSetting s:
                        s.Type = OptionTypes.String;
                        int n = item.Selections != null && item.Selections.Length > 0 ? item.Selections.Length : 1;
                        s.Values = new StringNames[n];
                        s.Index = Mathf.Clamp(item.GetInt(), 0, n - 1);
                        break;
                }

                return setting;
            }
            catch (Exception ex)
            {
                LightLogger.LogError($"[ConfigUIPanel.BuildSetting] {item.Key}", ex);
                return null;
            }
        }

        /// <summary>
        /// 后缀 → NumberSuffixes。
        /// ⚠️ 19.0 的枚举只有 None / Multiplier / Seconds（**没有 Percent**）。
        /// 其余后缀（"%"/"次" 等）由 FormatString 负责，原版只认这两个。
        /// </summary>
        private static NumberSuffixes ToSuffix(string suffix) => suffix switch
        {
            "s" => NumberSuffixes.Seconds,
            "x" => NumberSuffixes.Multiplier,
            _ => NumberSuffixes.None,
        };

        /// <summary>
        /// 清掉克隆菜单里**原版自己**会画的内容，只留我们的行。
        ///
        /// 用户反馈："原版的界面会叠在上面"。原因是克隆出来的 GameOptionsMenu
        /// 自带原版的整套设置（地图预览 MapPicker + 原版所有设置行），
        /// 我们只是在它上面又加了自己的行 → 两层叠在一起。
        ///
        /// 照 TONE 的做法（GameOptionsMenuPatch.cs:30-33）：
        ///   · MapPicker 直接关掉
        ///   · Children 清空（原版就是按这个列表铺行与刷新的）
        /// </summary>
        private static void ClearVanillaContent(GameOptionsMenu menu)
        {
            try
            {
                // ① 地图预览（TONE 同做法：GameOptionsMenuPatch.cs:32）
                var mapPicker = menu.MapPicker;
                if (mapPicker != null && mapPicker.gameObject.activeSelf)
                {
                    mapPicker.gameObject.SetActive(false);
                    LightLogger.Log("[ConfigUIPanel] 已关闭克隆菜单的 MapPicker");
                }

                // ② 原版设置行：Children 里记的都是原版行，全部隐藏
                var children = menu.Children;
                if (children != null)
                {
                    int hidden = 0;
                    for (int i = 0; i < children.Count; i++)
                    {
                        var ch = children[i];
                        if (ch == null) continue;
                        if (ch.gameObject.activeSelf) { ch.gameObject.SetActive(false); hidden++; }
                    }
                    if (hidden > 0)
                        LightLogger.Log($"[ConfigUIPanel] 已隐藏 {hidden} 个原版设置行");
                }

                // ③ ★★ 关键修复：把 settingsContainer 里**所有不是我们的**子物体全部隐藏。
                //
                //  为什么只清 Children 不够（用户反馈"header 还是会叠"）：
                //    原版 GameOptionsMenu.CreateSettings（GameOptionsMenu.cs:22-26）
                //    创建分类头（"伪装者"/"任务"那些）时是这样的：
                //        Instantiate(categoryHeaderOrigin, ..., this.settingsContainer);
                //        categoryHeaderMasked.transform.localPosition = ...;
                //    它**只 Instantiate 进 settingsContainer，从不 Add 到 Children**；
                //    只有设置行才 Add 到 Children（:37/:46/:56/:65）。
                //    所以 Children 里根本没有分类头 → 只清 Children 永远清不掉它们。
                //
                //  按名字前缀"LightConfig"排除我们自己的行/分类头（AddConfigRow/AddCategoryHeader
                //  都以此开头），其余一律隐藏 —— 这样无论是分类头、地图预览还是将来
                //  原版新增的任何东西，都不会再叠在我们的内容上。
                int foreign = HideForeignChildren(TryGetScrollerInner(menu));
                foreign += HideForeignChildren(menu.settingsContainer);
                if (foreign > 0)
                    LightLogger.Log($"[ConfigUIPanel] 已隐藏 {foreign} 个非本模组的原版子物体（含分类头）");

                // ④ ⚠️ 原版背景板/遮罩：克隆体自带原版整个视觉外壳
                HideByName(menu.transform, "Background");
                HideByName(menu.transform, "BG_Gradient");
                HideByName(menu.transform, "LabelBackground");

                // ⑤ 原版"什么情况？"说明区 + 返回按钮也属于原版外壳
                var back = menu.BackButton;
                if (back != null && back.gameObject.activeSelf)
                {
                    back.gameObject.SetActive(false);
                    LightLogger.Log("[ConfigUIPanel] 已关闭克隆菜单的 BackButton");
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.ClearVanillaContent] {ex.Message}");
            }
        }

        /// <summary>我们的行/分类头统一用 "LightConfig" 前缀命名（见 AddConfigRow / AddCategoryHeader）。</summary>
        internal const string OurPrefix = "LightConfig";

        /// <summary>
        /// 隐藏容器里所有**不是我们创建的**子物体（原版分类头、原版设置行、地图预览等），
        /// 保留我们自己以 <see cref="OurPrefix"/> 开头命名的行与分类头。
        /// </summary>
        private static int HideForeignChildren(Transform? container)
        {
            if (container == null) return 0;

            int hidden = 0;
            try
            {
                // 倒序遍历：隐藏不改变顺序，但倒序是改层级时的安全习惯
                for (int i = container.childCount - 1; i >= 0; i--)
                {
                    var child = container.GetChild(i);
                    if (child == null) continue;
                    if (child.name.StartsWith(OurPrefix, StringComparison.Ordinal)) continue;   // 我们自己的
                    if (!child.gameObject.activeSelf) continue;

                    child.gameObject.SetActive(false);
                    hidden++;
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.HideForeignChildren] {ex.Message}");
            }
            return hidden;
        }

        /// <summary>
        /// 供 GameSettingMenuPatch 在**每次切页签**时调用（不依赖面板是否重建）。
        /// 克隆菜单每次 SetActive(true) 都会走 OnEnable→Initialize，原版内容可能回来，
        /// 所以清理必须挂在切页动作上，而不只是挂在 Build 上。
        /// </summary>
        internal static void CleanMenu(GameOptionsMenu? menu)
        {
            if (menu == null) return;
            ClearVanillaContent(menu);
        }

        /// <summary>按名字（含直接子级递归一层）隐藏物体。</summary>
        private static void HideByName(Transform root, string name)
        {
            try
            {
                var t = root.Find(name);
                if (t != null && t.gameObject.activeSelf)
                {
                    t.gameObject.SetActive(false);
                    LightLogger.Log($"[ConfigUIPanel] 已隐藏原版外壳 '{name}'");
                }
            }
            catch { }
        }

        /// <summary>
        /// 行内布局调整 —— 照抄 TONE 的 <c>OptionBehaviourSetSizeAndPosition</c>
        /// （TONE\Patches\GameOptionsMenuPatch.cs:217-260）。
        ///
        /// 为什么要做：原版这些行是给**原版那种窄列**设计的，
        /// 直接放到我们更宽的一页里会「挤在左边一小块」。
        /// TONE 的做法是：
        ///   · LabelBackground 拉宽（localScale.x +1、y −0.2）并左移 −0.6
        ///   · Title Text 左移 −0.7，sizeDelta 设成 (5.7, 0.37)，左对齐 + 加粗 + 描边
        ///   · Checkbox 的 Toggle 挪到 (1.46, −0.042)
        ///   · 数值行的 +/− 与数值框各自右移
        ///
        /// ⚠️ 这些增量是 TONE 的实测值，**照抄**即可；不要再自己推算
        ///    （之前"猜部件名 + 硬编码尺寸"就是因为没照抄才做坏的）。
        /// </summary>
        private static void ApplyRowLayout(OptionBehaviour row, ConfigItem item)
        {
            try
            {
                var t = row.transform;

                // ① 灰色标签底：**只把高度收一点点**，让相邻两行之间留出一条缝。
                //
                // 用户反馈："原版布局旁边那个灰色条在一起不好看，给每个配置项之间稍微加一点点距离，
                //            上一轮就是那样"。
                // 上一轮之所以有缝，是因为 TONE 的那句 `localScale += (1, -0.2, 0)` 把标签底
                // 压矮了 0.2；我上一轮为了修"减号穿模"把整句删掉，于是标签底恢复原高、
                // 两行的灰条就贴在一起了。
                // 现在只保留**垂直**方向的收缩（-0.2），水平方向与位置一律不动 ——
                // 这样既有缝，又不会像之前那样加宽后盖住左侧减号。
                var labelBg = row.LabelBackground != null
                    ? row.LabelBackground.transform
                    : t.Find("LabelBackground");
                if (labelBg != null)
                    labelBg.localScale += new Vector3(0f, -0.2f, 0f);

                // ② 字体与文字：只改这些，绝不改位置/尺寸。
                //
                // 用户反馈过两次布局问题："数字那个框是歪的，加减号也是"、
                // "加减号位置还是错的，减号跑到左边穿模了"。
                // 根因都是之前照抄 TONE 的 OptionBehaviourSetSizeAndPosition：
                //     LabelBackground 水平 +1 且左移 -0.6
                //     Title 左移 -0.7 且 sizeDelta = (5.7, 0.37)
                //     PlusButton +1.7 / MinusButton +0.9 / ValueBox +1.3
                //   —— 全是 TONE 为**它自己**的行宽校准的数值。
                //      我们的行就是原版预制体、原版缩放、原版坐标，套上去就歪/穿模。
                // 标题文本：优先用原版控件的**强类型字段**，名字查找只作兜底
                // （字段指向的对象名不保证叫 "Title Text"，用名字找可能静默找不到 → 字体没换上去）
                TextMeshPro? tmp = row switch
                {
                    ToggleOption tg => tg.TitleText,
                    NumberOption nm => nm.TitleText,
                    StringOption so => so.TitleText,
                    _ => null,
                };
                if (tmp == null)
                {
                    var title = t.Find("Title Text");
                    if (title != null) tmp = title.GetComponent<TextMeshPro>();
                }

                if (tmp != null)
                {
                    tmp.fontStyle = FontStyles.Bold;
                    tmp.outlineWidth = 0.17f;

                    var menuFont = MenuTextTemplate.MenuFont;   // 辉光白那套字体
                    if (menuFont != null) tmp.font = menuFont;
                    tmp.color = MenuTextTemplate.GlowWhite;
                    tmp.text = item.DisplayName ?? item.Key;
                    tmp.ForceMeshUpdate();
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.ApplyRowLayout] {ex.Message}");
            }
        }

        // 【已删除】ShiftValueBox / Shift 两个辅助方法。
        // 它们把数值框 +1.3、加号 +1.7、减号 +0.9 硬挪，是"数字框/加减号是歪的"的根因；
        // 现在一律保留原版位置，故不再需要。
        // 说明：TONE 的 OptionBehaviourSetSizeAndPosition 里有这些位移，但那是为
        // TONE 自己的行宽校准的，直接套到原版行上就会歪 —— 不要照抄坐标。

        /// <summary>
        /// 一次性诊断：打印标题/数值 TMP 的实例 ID 与所在物体名。
        /// 用途：验证"闪字"是否因为两者指向同一个 TMP（原版写值 → 标题被顶掉）。
        /// 只打一次（_loggedRows），避免刷屏。
        /// </summary>
        private static readonly HashSet<string> _loggedRows = new();

        private static void LogRowTextOnce(OptionBehaviour row, ConfigItem item)
        {
            try
            {
                if (!_loggedRows.Add(item.Key)) return;

                string titleInfo = "无", valueInfo = "无", same = "n/a";

                var t = row is ToggleOption tt ? tt.TitleText
                      : row is NumberOption nn ? nn.TitleText
                      : row is StringOption ss ? ss.TitleText : null;

                TextMeshPro? v = row is NumberOption n2 ? n2.ValueText
                               : row is StringOption s2 ? s2.ValueText : null;

                if (t != null) titleInfo = $"{t.name}#{t.GetInstanceID()} text='{t.text}'";
                if (v != null)
                {
                    valueInfo = $"{v.name}#{v.GetInstanceID()} text='{v.text}'";
                    same = (t != null && t.GetInstanceID() == v.GetInstanceID()) ? "★同一个TMP！" : "不同对象";
                }

                LightLogger.Log($"[ConfigUIPanel.Diag] 行 {item.Key} 标题TMP={titleInfo} | " +
                                $"数值TMP={valueInfo} | {same}");
            }
            catch { }
        }

        /// <summary>驱动器挂不上时的兜底：至少把标题写对。</summary>
        private static void FixTitleFallback(OptionBehaviour row, ConfigItem item)
        {
            try
            {
                TextMeshPro? title =
                    row is ToggleOption t ? t.TitleText :
                    row is NumberOption n ? n.TitleText :
                    row is StringOption s ? s.TitleText : null;

                if (title == null) return;

                var tr = title.GetComponent<TextTranslatorTMP>();
                if (tr != null) tr.enabled = false;
                title.text = item.DisplayName ?? item.Key;
                if (item.NameColor.HasValue) title.color = item.NameColor.Value;
            }
            catch { }
        }

        /// <summary>建一行配置项（克隆原版控件 + 合成 setting），返回下一个 y。</summary>
        private static float AddConfigRow(ConfigItem item, float y)
        {
            try
            {
                if (_templates == null)
                {
                    LightLogger.LogWarning($"[ConfigUIPanel] 模板源未就绪，跳过 {item.Key}");
                    return y;
                }

                // 按类型挑模板
                OptionBehaviour? origin;
                switch (item.Type)
                {
                    case ConfigType.Bool:      origin = _templates.checkboxOrigin;     break;
                    case ConfigType.Value:
                    case ConfigType.Filter:    origin = _templates.stringOptionOrigin; break;
                    default:                   origin = _templates.numberOptionOrigin; break;
                }

                if (origin == null)
                {
                    LightLogger.LogWarning($"[ConfigUIPanel] 预制体 {item.Type} 为空，跳过 {item.Key}");
                    return y;
                }

                var clone = Object.Instantiate(origin, Vector3.zero, Quaternion.identity, _container);
                clone.gameObject.name = $"LightConfigRow_{item.Key}";
                clone.transform.localPosition = new Vector3(RowX, y, RowZ);

                // ⚠️ 绝对不要把 localScale 设成 one！
                // 原版行预制体（GameOption_Number(Clone) 等）自身是 0.60 的缩放，
                // 强制设成 1 会让行**放大 1.67 倍** → 用户看到的"设置项太大了"。
                // TONE 从头到尾都没碰过行的 localScale（GameOptionsMenuPatch.cs 里只改部件）。
                // 这里保留预制体自带缩放，只在它异常（0 或 1.0 明显不对）时才不动它。
                // （不写任何 localScale 赋值 = 保留 Instantiate 出来的原值。）

                clone.gameObject.SetActive(true);
                _spawned.Add(clone.gameObject);

                // 合成 setting 并交给原版初始化（这一步让原版部件全部就位）
                var setting = BuildSetting(item);
                if (setting != null)
                {
                    clone.SetClickMask(_templates.ButtonClickMask);
                    clone.SetUpFromData(setting, MaskLayer);
                }
                else
                {
                    LightLogger.LogWarning($"[ConfigUIPanel] {item.Key} 未能合成 BaseGameSetting");
                }

                // 登记：只用于我们自己的显示刷新与悬浮，不再靠它拦截原版逻辑
                RowMap[clone.GetInstanceID()] = item;

                // ⚠️ NRE 根因：原版 ToggleOption.Toggle() 最后一行是
                //      this.OnValueChanged(this);
                //    克隆体的 OnValueChanged 是 **null** → 点一下就 NRE。
                //    TONE 在 GameOptionsMenuPatch.cs:179 显式赋值：
                //      optionBehaviour.OnValueChanged = new Action<OptionBehaviour>(__instance.ValueChanged);
                //    我们不需要原版的联动，但**必须给它一个非 null 的委托**。
                clone.OnValueChanged = new Action<OptionBehaviour>(_ => { });

                // 清掉克隆自带的原版点击逻辑（AGENTS.md §4.5：必须整体替换）
                var pb = clone.GetComponent<PassiveButton>();
                if (pb != null) pb.OnClick = new Button.ButtonClickedEvent();

                // 行内布局照抄 TONE 的 OptionBehaviourSetSizeAndPosition：
                // 拉宽标签底、标题左对齐加粗、右侧控件右移，并统一换成辉光白字体
                ApplyRowLayout(clone, item);

                // 驱动器：修正标题文字（原版会按 setting.Title 写）
                var driver = AddComponentSafe<ConfigRowDriver>(clone.gameObject);
                if (driver != null)
                {
                    driver.Bind(item, clone);
                    _drivers[item] = driver;
                    driver.RefreshVisual();
                }
                else
                {
                    // 驱动器失败也要保证标题正确，否则会显示原版的 "Confirm Ejects?" 之类
                    FixTitleFallback(clone, item);
                }

                // 一次性诊断：确认"标题 TMP"与"数值 TMP"是否是同一个对象。
                // 若是同一个，原版往数值框写值就会把标题顶掉（表现为"闪字"）——
                // 这是我对用户报告的一个假设，先用日志证实/证伪再继续改。
                LogRowTextOnce(clone, item);

                WireHover(clone.gameObject, item);

                _rowCount++;
                return y - SpacingY;
            }
            catch (Exception ex)
            {
                LightLogger.LogError($"[ConfigUIPanel.AddConfigRow] {item.Key}", ex);
                return y;
            }
        }

        /// <summary>
        /// 悬浮显示详情（走本工程已有的 DetailPopup）。
        /// 原版行自带 BoxCollider2D + PassiveButton，所以这里只追加监听，不新增碰撞区。
        /// </summary>
        private static void WireHover(GameObject row, ConfigItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Detail)) return;

            try
            {
                var pb = row.GetComponent<PassiveButton>();
                if (pb == null) return;

                pb.OnMouseOver ??= new Button.ButtonClickedEvent();
                pb.OnMouseOut ??= new Button.ButtonClickedEvent();

                var text = item.Detail;
                pb.OnMouseOver.AddListener((UnityAction)(() => DetailPopup.Show(text, true, row.transform)));
                pb.OnMouseOut.AddListener((UnityAction)(() => DetailPopup.Hide()));
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[ConfigUIPanel.WireHover] {ex.Message}");
            }
        }

        // =====================================================================
        //  刷新
        // =====================================================================

        /// <summary>
        /// 【Nebula 风格】每次值变化都重新查一遍可见性。
        ///
        /// 用户反馈："调试模式打勾后，应该显示的假人要重新开一遍菜单才能看到（取消勾选却立即消失）"。
        ///
        /// 原因（原来这里是遍历 `_drivers` 判定）：
        ///   勾选前"生成假人的数量"是**不可见**的，Build 时就被 `if (!item.IsVisible) continue;`
        ///   跳过了 → 它**根本没有被建出来** → `_drivers` 里没有它 → 原来的循环
        ///   遍历不到任何"可见性变了"的项 → 不触发重建 → 行永远不出现，只能重开菜单。
        ///   而取消勾选时，行**已经存在**于 `_drivers` 里，所以能被发现并立即隐藏 —— 这就是那个不对称。
        ///
        /// 修法：判定基准从"已建的行"换成**注册表里的全部配置项**，
        /// 拿 `item.IsVisible` 和"这一项现在有没有被建出来"对比。
        /// 任何一项只要"该显示却没建"或"已建却不该显示"，就整页重建。
        /// 这样勾选/取消都立即生效，且和 Nebula 的 predicate 语义一致
        /// （`ConfigItem.SetVisibleWhen(Func<bool>)` / `SetDependsOn(item)` 就是那个"可选 lambda"）。
        /// </summary>
        public static void Refresh()
        {
            try
            {
                if (VisibilityChanged())
                {
                    var parent = _container != null ? _container.parent : null;
                    if (parent != null)
                    {
                        Rebuild(parent);
                        return;
                    }
                }

                foreach (var kv in _drivers) kv.Value.RefreshVisual();
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigUIPanel.Refresh]", ex);
            }
        }

        /// <summary>
        /// 当前过滤条件下，"应显示"与"已建出来"是否不一致（不一致 → 需要重建）。
        /// 以注册表为准，所以"尚未被建出来的项变可见"也能被发现。
        /// </summary>
        private static bool VisibilityChanged()
        {
            // 单职业模式：只看那一个块，别把其它分类的项也算进来（否则会误判重建）
            if (_singleBlock != null)
                return BlockNeedsRebuild(_singleBlock);

            foreach (var block in ConfigRegistry.Blocks)
            {
                if (block == null || !MatchesFilter(block)) continue;
                if (BlockNeedsRebuild(block)) return true;
            }
            return false;
        }

        private static bool BlockNeedsRebuild(ConfigBlock block)
        {
            foreach (var item in block.Items)
            {
                if (item == null) continue;
                if (item.IsVisible != _drivers.ContainsKey(item)) return true;
            }
            return false;
        }

        /// <summary>取某行对应的配置项（供原版控件的 prefix 使用）。</summary>
        internal static ConfigItem ItemOf(int instanceId)
            => RowMap.TryGetValue(instanceId, out var it) ? it : null;

        /// <summary>
        /// 重排所有已建行（可见性变化后调用）：只改 active 与 y，不销毁重建。
        /// 顺序与 Build 一致，保证 y 的计算完全对应。
        /// </summary>
        public static void Relayout()
        {
            try
            {
                if (_container == null) return;

                float y = StartY;
                foreach (var block in ConfigRegistry.Blocks)
                {
                    if (block == null || !MatchesFilter(block)) continue;

                    // 块内一项都不可见时，分类头也一起藏起来
                    bool anyVisible = false;
                    foreach (var it in block.Items)
                        if (it.IsVisible) { anyVisible = true; break; }

                    var headerGo = FindSpawned($"LightConfigHeader_{block.Key}");
                    if (!anyVisible)
                    {
                        if (headerGo != null) headerGo.SetActive(false);
                        continue;
                    }

                    if (headerGo != null)
                    {
                        headerGo.SetActive(true);
                        headerGo.transform.localPosition = new Vector3(HeaderX, y, RowZ);
                    }
                    y -= HeaderHeight;

                    foreach (var item in block.Items)
                    {
                        var rowGo = FindSpawned($"LightConfigRow_{item.Key}");
                        if (rowGo == null) continue;

                        bool vis = item.IsVisible;
                        rowGo.SetActive(vis);
                        if (!vis) continue;

                        rowGo.transform.localPosition = new Vector3(RowX, y, RowZ);
                        y -= SpacingY;
                    }
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigUIPanel.Relayout]", ex);
            }
        }

        private static GameObject FindSpawned(string name)
        {
            foreach (var go in _spawned)
                if (go != null && go.name == name) return go;
            return null;
        }

        // =====================================================================
        //  模板查找
        // =====================================================================

        private static Sprite? _roundedSprite;

        /// <summary>圆角长方形 sprite（带 9 宫格边框，Sliced 任意拉伸；列表/返回按钮共用）。</summary>
        internal static Sprite GetRoundedSprite()
        {
            if (_roundedSprite != null) return _roundedSprite;

            const int w = 64, h = 32, r = 10;
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // 像素到圆角矩形边缘的距离 → 1px 抗锯齿
                    float dx = Mathf.Max(Mathf.Abs(x + 0.5f - w * 0.5f) - (w * 0.5f - r), 0f);
                    float dy = Mathf.Max(Mathf.Abs(y + 0.5f - h * 0.5f) - (h * 0.5f - r), 0f);
                    float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy));
                    tex.SetPixel(x, y, new UColor(1f, 1f, 1f, a));
                }
            }
            tex.Apply();

            _roundedSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f,
                0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            return _roundedSprite;
        }

        /// <summary>
        /// 分类头模板：优先用 GameOptionsMenu 的私有预制体字段 categoryHeaderOrigin；
        /// 取不到再退回场景里现成的分类头（例如角色设置页的）。
        /// </summary>
        private static CategoryHeaderMasked FindHeaderTemplate()
        {
            try
            {
                var origin = _templates?.categoryHeaderOrigin;
                if (origin != null) return origin;

                var menu = GameSettingMenu.Instance;
                if (menu != null)
                {
                    var h = menu.GetComponentInChildren<CategoryHeaderMasked>(true);
                    if (h != null) return h;
                }
                return Object.FindObjectOfType<CategoryHeaderMasked>(true);
            }
            catch { return null; }
        }

        private static GameObject NewUIObject(string name, Transform parent, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.layer = LayerExpansion.GetUILayer();
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = Vector3.one;
            return go;
        }
    }

    /// <summary>
    /// 一行原版控件的驱动器：把配置值写进原版控件的显示部件。
    /// 原版控件自身的 Initialize/FixedUpdate/UpdateValue 由 <c>ConfigRowPatches</c> 拦截，
    /// 显示完全由这里负责。
    /// </summary>
    public class ConfigRowDriver : MonoBehaviour
    {
        private ConfigItem _item;
        private OptionBehaviour _behaviour;

        /// <summary>上一次的可见性（用于检测是否需要重建面板）。</summary>
        public bool WasVisible { get; private set; }

        /// <summary>绑定一行原版控件。</summary>
        public void Bind(ConfigItem item, OptionBehaviour behaviour)
        {
            _item = item;
            _behaviour = behaviour;
            WasVisible = item.IsVisible;
        }

        /// <summary>按当前值刷新所有显示部件。</summary>
        public void RefreshVisual()
        {
            try
            {
                if (_item == null || _behaviour == null) return;

                // 标题：我们的显示名（原版走翻译键，这里直接写）
                var title = GetTitleText();
                if (title != null)
                {
                    var tr = title.GetComponent<TextTranslatorTMP>();
                    if (tr != null) tr.enabled = false;
                    title.text = _item.DisplayName ?? _item.Key;
                    if (_item.NameColor.HasValue) title.color = _item.NameColor.Value;
                }

                if (_behaviour is ToggleOption toggle)
                {
                    // Bool：勾选状态就是值
                    if (toggle.CheckMark != null) toggle.CheckMark.enabled = _item.GetBool();
                }
                else
                {
                    var valueText = GetValueText();
                    if (valueText != null) valueText.text = _item.GetValueText();
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigRowDriver.RefreshVisual]", ex);
            }
        }

        /// <summary>增减一步并同步。</summary>
        public void Step(int dir)
        {
            try
            {
                if (_item == null) return;

                if (_item.Type == ConfigType.Bool) _item.Toggle();
                else if (dir > 0) _item.Increase();
                else _item.Decrease();

                RefreshVisual();
                ConfigSync.RaiseAndSync(_item);

                // 值变了可能影响别的项的可见性（如"启用调试模式"控制数量项）
                ConfigUIPanel.Refresh();
                ConfigUIPanel.Relayout();
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigRowDriver.Step]", ex);
            }
        }

        /// <summary>取标题文本部件（三种原版行各自的字段名不同）。</summary>
        private TextMeshPro GetTitleText()
        {
            if (_behaviour is ToggleOption t) return t.TitleText;
            if (_behaviour is NumberOption n) return n.TitleText;
            if (_behaviour is StringOption s) return s.TitleText;
            return _behaviour != null ? _behaviour.GetComponentInChildren<TextMeshPro>(true) : null;
        }

        /// <summary>取数值文本部件（Bool 行没有）。</summary>
        private TextMeshPro GetValueText()
        {
            if (_behaviour is NumberOption n) return n.ValueText;
            if (_behaviour is StringOption s) return s.ValueText;
            return null;
        }
    }
}
