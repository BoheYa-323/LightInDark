using System;
using System.Collections.Generic;
using System.Linq;
using Light.UI.Window;
using LightInDark.Core;
using UnityEngine;
using UnityEngine.Events;

namespace Light.UI;

/// <summary>
/// 模态遮罩：我们的窗口打开时，让**原版 UI 点不动**。
///
/// =====================================================================
///  【为什么"加一个碰撞盒"在这个游戏里没用】
///
/// 直觉做法是放一个铺满屏幕的 Collider2D。**但它拦不住任何东西。**
/// 原版 <c>PassiveButtonManager.Update()</c>（19.0 反编译，行 58-136）：
///
///     foreach (var button in this.Buttons)          // 按 CachedZ 升序
///     {
///         if (!button.isActiveAndEnabled) continue;
///         for (int l = 0; l < button.Colliders.Length; l++)
///         {
///             var col = button.Colliders[l];
///             if (!col || !col.isActiveAndEnabled) continue;
///             this.HandleMouseOver(button, col);      // ← 只有这里比较 z（行 245）
///             switch (this.controller.CheckDrag(col)) // ← 点击判定，**完全不看 z**
///             {
///                 case DragState.Released:
///                     button.ReceiveClickUp();        // ← 命中就触发
///             }
///         }
///     }
///
/// 每个注册过的控件都会被**独立**判定一次，谁在前面不影响点击。
/// 所以遮罩碰撞盒和原版按钮的碰撞盒同时盖住鼠标点时，两边都会收到
/// <c>ReceiveClickUp()</c> → 两边的 OnClick 都会跑。
/// 这就是"老是能点到原版的东西"的真正原因。
///
/// ⚠️ 也不能用 <c>PassiveUiElement.ClickMask</c>：那个判定在行 72，条件是
///    <c>touch.IsDown</c>；松手那一帧 IsDown 已经是 false，守卫不生效，
///    点击照样会触发。
///
/// =====================================================================
///  【也不能用 Harmony 去 prefix 输入方法 —— 会把游戏打崩】
///
/// 第一版就是那么写的（prefix <c>PassiveUiElement.ReceiveClickUp/ReceiveClickDown/
/// ReceiveRepeatDown</c>），结果启动直接**致命崩溃**：
///
///     Fatal error. System.AccessViolationException
///       at DynamicClass.DMD&lt;PassiveUiElement::ReceiveRepeatDown&gt;(PassiveUiElement)
///       at ChatController..cctor()
///       at HarmonyLib.PatchClassProcessor.Patch()
///       at Light.LightPlugin.Load()
///
/// 原因：给**抽象基类** `PassiveUiElement` 打补丁时，Harmony 要解析它的 native
/// class pointer → 触发类初始化 → 初始化链里 `ChatController..cctor()` 又会去调
/// `ReceiveRepeatDown` → **重入到一半装好的补丁上** → 访问冲突。
/// 另外基类那几个方法体本来就是空的（`public virtual void ReceiveClickUp() { }`），
/// 打了也拦不住子类的 override —— 既危险又无效。
///
/// =====================================================================
///  【最终做法：临时禁用原版控件】
///
/// 管理器第一行就判 <c>isActiveAndEnabled</c>（行 66）。所以只要把"不属于我们窗口"
/// 的控件临时 <c>enabled = false</c>，它们当帧就被整个跳过 —— 点击、悬浮全都没了。
///
/// 为什么这样是安全的（都核对过 19.0 源码）：
///   · 注册只发生在 <c>PassiveUiElement.Start()</c> 里；<c>OnEnable</c> **不会**重复注册
///     → 反反复复开关不会让 Buttons 列表膨胀；
///   · <c>PassiveButton.OnDisable</c> 只调 <c>SetPassiveButtonHoverStateInactive()</c>
///     （纯视觉），没有副作用；
///   · 原版自己就是用 <c>enabled</c> 来表达"这个按钮现在不可用"的
///     （<c>SetButtonEnableState</c>），所以这是官方支持的开关方式。
///
/// 复原时对 PassiveButton 走它自己的 <c>SetButtonEnableState(true)</c>，
/// 保证视觉状态和原版一致。
/// </summary>
public static class UiModalGuard
{
    /// <summary>
    /// 当前打开的模态窗口根节点（**栈**）。
    /// 颜色选择窗口是叠在「更换背景图」面板之上的第二个窗口，
    /// 所以这里必须是列表：只要某个控件在**任意一个**我们的窗口里就放行。
    /// </summary>
    private static readonly List<Transform> _roots = new();

    /// <summary>被我们临时关掉的控件（记住原样，关闭时全部还原）。</summary>
    private static readonly Dictionary<int, PassiveUiElement> _disabled = new();

    /// <summary>已确认"属于我们窗口"的控件，避免每帧重复向上遍历父链。</summary>
    private static readonly HashSet<int> _knownOurs = new();

    private static int _lastListCount = -1;

    /// <summary>登记模态窗口根节点（窗口显示时调用）。</summary>
    public static void Push(Transform? root)
    {
        if (root == null) return;
        foreach (var r in _roots) if (r == root) return;

        _roots.Add(root);
        _knownOurs.Clear();       // 多了一个窗口，缓存全作废
        _lastListCount = -1;
    }

    /// <summary>注销（窗口关闭/销毁时调用）。全部关完才还原被禁用的控件。</summary>
    public static void Pop(Transform? root)
    {
        if (root != null)
        {
            for (int i = _roots.Count - 1; i >= 0; i--)
                if (_roots[i] == root) _roots.RemoveAt(i);
        }
        else
        {
            _roots.Clear();
        }

        _knownOurs.Clear();
        _lastListCount = -1;

        // ⚠️ 只有**没有**任何模态窗口时才还原。
        //    否则关掉上面那个颜色窗口会把下面那个面板的遮罩也一起撤掉。
        if (_roots.Count == 0) RestoreAll();
    }

    /// <summary>清空（场景切换兜底，防止跨场景残留把新场景的按钮全锁死）。</summary>
    public static void Clear() => Pop(null);

    /// <summary>当前是否有**真正在显示**的模态窗口。</summary>
    public static bool AnyOpen
    {
        get
        {
            bool any = false;
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                var r = _roots[i];
                bool alive;
                try { alive = r != null && r.gameObject.activeInHierarchy; }
                catch { alive = false; }
                if (!alive) { _roots.RemoveAt(i); _knownOurs.Clear(); continue; }
                any = true;
            }
            if (!any && _roots.Count == 0) RestoreAll();
            return any;
        }
    }

    /// <summary>
    /// 每帧调用（窗口显示期间）：把不属于我们窗口的原版控件全部临时禁用。
    /// 不在模态状态时会自动还原。
    /// </summary>
    public static void Sweep()
    {
        if (!AnyOpen) return;

        var mgr = PassiveButtonManager.Instance;
        if (mgr == null) return;

        var list = mgr.Buttons;
        if (list == null) return;

        // 数量没变且都处理过 → 这一帧不用再遍历（省开销）
        if (list.Count == _lastListCount && _disabled.Count > 0) return;
        _lastListCount = list.Count;

        for (int i = 0; i < list.Count; i++)
        {
            PassiveUiElement? el;
            try { el = list[i]; } catch { continue; }
            if (el == null) continue;

            int id;
            try { id = el.GetInstanceID(); } catch { continue; }

            if (_knownOurs.Contains(id)) continue;

            if (IsUnderAnyRoot(el.transform))
            {
                _knownOurs.Add(id);
                continue;
            }

            // 不是我们的 → 关掉
            bool wasEnabled;
            try { wasEnabled = el.enabled; } catch { continue; }
            if (!wasEnabled) continue;                  // 本来就关着的别动，免得关闭时错误地打开它

            try
            {
                el.enabled = false;
                _disabled[id] = el;
            }
            catch { }
        }

        // 一次性诊断：确认遮罩真的拦到了东西（别只是"看起来在工作"）
        if (_disabled.Count > 0 && !_loggedFirstSweep)
        {
            _loggedFirstSweep = true;
            LightLogger.Log($"[UiModalGuard] 遮罩生效：已临时禁用 {_disabled.Count} 个原版控件，" +
                            $"放行我方 {_knownOurs.Count} 个（模态窗口 {_roots.Count} 个）");
        }
    }

    private static bool _loggedFirstSweep;

    /// <summary>
    /// <b>强制</b>做一次完整扫描（把 <see cref="Sweep"/> 里"按钮数量没变就短路"的优化跳过）。
    ///
    /// 为什么需要它：<see cref="Sweep"/> 只在 <c>Buttons.Count</c> 变化时才真正遍历一遍；
    /// 而原版会在很多地方自己把按钮 <c>enabled</c> 回来（大厅 HUD 的按钮随状态开关尤其常见）。
    /// 一旦数量没变，短路会让遮罩漏掉那些刚被重新启用的按钮 → 又变成"点击穿透"。
    ///
    /// 调用者：音乐播放器窗口（F3，任何场景都能开）。它没有别的每帧驱动者
    /// —— 主菜单里 <c>MainMenuPatch</c> 会调 <see cref="Sweep"/>，但大厅/游戏内没有。
    /// 为了不每帧都全扫，那边是低频调用的。
    /// </summary>
    public static void ForceSweep()
    {
        _lastListCount = -1;   // 让下一次 Sweep 认为"数量变了"，从而完整遍历
        Sweep();
    }

    /// <summary>在**任意一个**我们的模态窗口底下就算"我们的"。</summary>
    private static bool IsUnderAnyRoot(Transform? t)
    {
        if (t == null) return false;
        for (int i = 0; i < _roots.Count; i++)
        {
            var r = _roots[i];
            if (r == null) continue;
            try { if (IsUnder(t, r)) return true; } catch { }
        }
        return false;
    }

    /// <summary>还原所有被我们关掉的控件。</summary>
    private static void RestoreAll()
    {
        _loggedFirstSweep = false;
        if (_disabled.Count == 0) return;

        foreach (var kv in _disabled)
        {
            var el = kv.Value;
            try
            {
                if (el == null) continue;
                // 走原版自己的启用路径，视觉状态才对
                if (el.TryCast<PassiveButton>() is { } pb) pb.SetButtonEnableState(true);
                else el.enabled = true;
            }
            catch { }
        }
        _disabled.Clear();
    }

    private static bool IsUnder(Transform? t, Transform root)
    {
        var cur = t;
        for (int i = 0; i < 64 && cur != null; i++)
        {
            if (cur == root) return true;
            cur = cur.parent;
        }
        return false;
    }

    /// <summary>诊断：当前登记的根节点数与它们的名字。</summary>
    public static string Describe()
        => _roots.Count == 0
            ? $"(无模态；已禁用 {_disabled.Count} 个)"
            : $"{string.Join(" + ", _roots.Select(r => { try { return r == null ? "<已销毁>" : r.name; } catch { return "<异常>"; } }))}" +
              $"（已禁用 {_disabled.Count} 个，已知我方 {_knownOurs.Count} 个）";

    // =====================================================================
    //  「点窗口**以外**才关窗」的点击区
    // =====================================================================

    /// <summary>
    /// 装一套"窗口外面才响应"的点击区，用来实现「点空白处关窗」。
    ///
    /// ⚠️⚠️ 为什么不能直接用 HudUI 自带的 ClickGuard：
    ///   它是一个**铺满全屏**的 100×100 碰撞盒。而原版点击判定
    ///   （<c>PassiveButtonManager.Update</c>）是"每个注册控件各自判一次、**不看 z**"，
    ///   所以窗口**内部**的空白也会命中它 → 表现为"点窗口里任何非按钮的地方都会关窗"。
    ///   用户报的就是这个 bug。
    ///
    /// 做法：把 HudUI 那个整块的 ClickGuard 关掉，改成 **四条边框带子**
    /// （上/下/左/右），四条带子拼起来正好把「窗口矩形」挖空 ——
    /// 于是只有点在窗口**外面**才会触发关闭。
    ///
    /// 四条带子的几何（挖出的洞 = 窗口本身，向外扩到 ±<paramref name="big"/>）：
    /// <code>
    ///        ┌─────────── 上带 ───────────┐
    ///        │  ┌──────────────────────┐  │
    ///        左 │                      │ 右
    ///        带 │        窗口           │ 带
    ///        │  └──────────────────────┘  │
    ///        └─────────── 下带 ───────────┘
    /// </code>
    /// 用 <c>localPosition</c>/<c>localScale</c> 挂在窗口根下，窗口整体缩放时自动跟着缩放。
    /// </summary>
    public static void InstallOutsideClickGuard(GameObject windowObj, Vector2 windowSize,
        Action onOutsideClick, float big = 60f)
    {
        try
        {
            if (windowObj == null || onOutsideClick == null) return;
            var parent = windowObj.transform;

            // ① 干掉 HudUI 那个铺满全屏的整块 ClickGuard
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c != null && c.name == "ClickGuard")
                {
                    c.gameObject.SetActive(false);
                    LightLogger.Log("[UiModalGuard] 已关闭 HudUI 的整屏 ClickGuard，改装四条边框带子");
                    break;
                }
            }

            float hx = windowSize.x * 0.5f;
            float hy = windowSize.y * 0.5f;

            // ② 四条带子（洞 = ±hx / ±hy）
            MakeBand(parent, windowObj.layer, new Vector3(0f, (hy + big) * 0.5f, 0.3f),
                new Vector2(big * 2f, big - hy), onOutsideClick, "Top");
            MakeBand(parent, windowObj.layer, new Vector3(0f, -(hy + big) * 0.5f, 0.3f),
                new Vector2(big * 2f, big - hy), onOutsideClick, "Bottom");
            MakeBand(parent, windowObj.layer, new Vector3(-(hx + big) * 0.5f, 0f, 0.3f),
                new Vector2(big - hx, hy * 2f), onOutsideClick, "Left");
            MakeBand(parent, windowObj.layer, new Vector3((hx + big) * 0.5f, 0f, 0.3f),
                new Vector2(big - hx, hy * 2f), onOutsideClick, "Right");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[UiModalGuard.InstallOutsideClickGuard]", ex);
        }
    }

    private static void MakeBand(Transform parent, int layer, Vector3 pos, Vector2 size,
        Action onClick, string name)
    {
        try
        {
            var go = new GameObject("OutsideClick_" + name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one;

            var col = go.AddComponent<BoxCollider2D>();
            col.isTrigger = true;
            col.size = size;

            // SetUpButton(false, ...) 只建组件、不挂音效
            var pb = go.SetUpButton(false, playSound: false);
            pb.OnClick.AddListener((UnityAction)(() =>
            {
                try { onClick(); } catch (Exception ex) { LightLogger.LogWarning($"[UiModalGuard] {ex.Message}"); }
            }));
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[UiModalGuard.MakeBand] {name}: {ex.Message}");
        }
    }
}
