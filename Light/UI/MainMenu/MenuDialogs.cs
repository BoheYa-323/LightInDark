using System;
using System.Collections.Generic;
using Light.UI.HudUI;
using LightInDark.Core;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Light.UI.MainMenu;

/// <summary>
/// 主界面里的小弹窗（提示 / 是-否选择）。
///
/// ⚠️ 为什么不用现成的 <c>HudUI.OpenMessageDialog / OpenConfirmDialog</c>：
///   那两个走的是 <c>HudUIWindow.Create(title, size)</c>，内部
///    <c>parent ??= HudManager.Instance?.transform</c> —— **主界面里 HudManager 是 null**，
///    会直接抛 <c>InvalidOperationException("HudManager 未就绪")</c>，弹不出来。
///    （AGENTS.md 里也写了 HudUI 是"大厅内"用的。）
///
///    所以这里用同一个 <c>HudUIWindow</c>（就是"用 HudUI"），但**显式传主界面的 parent**。
///
/// ⚠️ 还有两件必须自己补的事：
///   ① <c>HudUIWindow.Create</c> 内部 <c>sortingGroupOrder</c> 用的是默认值 100，
///      比「更换背景图」面板（300）和颜色窗口（400）都低 → **会被盖住**。这里抬到 900。
///   ② 必须把弹窗登记进 <see cref="UiModalGuard"/>，否则会被遮罩的 Sweep 当成
///      "原版控件"禁用掉，按钮点不动。
/// </summary>
internal static class MenuDialogs
{
    /// <summary>弹窗层级：高于面板(300)和颜色窗口(400)。</summary>
    private const int DialogSortingOrder = 900;

    /// <summary>还活着的弹窗（用来判断"现在有没有弹窗挡着"）。</summary>
    private static readonly List<GameObject> _live = new();

    /// <summary>
    /// 当前是否有弹窗正开着。
    ///
    /// ⚠️ 这个判断很关键：原版点击判定是"每个控件各自判一次、**不看 z**"，
    ///    所以弹窗开着的时候，点在弹窗外会**同时**命中面板的"点外面关窗"带子，
    ///    导致弹窗还在、面板却在背后被关掉。面板/颜色窗口的带子必须先问这里。
    /// 顺手把已销毁的剔掉，不需要手动维护计数。
    /// </summary>
    public static bool AnyOpen
    {
        get
        {
            bool any = false;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var go = _live[i];
                bool alive;
                try { alive = go != null && go; } catch { alive = false; }
                if (!alive) { _live.RemoveAt(i); continue; }
                any = true;
            }
            return any;
        }
    }

    private static HudUIWindow? CreateWindow(Vector2 size)
    {
        try
        {
            var menu = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
            if (menu == null) return null;

            var w = HudUIWindow.Create("", size, menu.transform);
            if (w == null) return null;

            var go = w.Screen.transform.parent.gameObject;

            // ① 抬层级
            var sg = go.GetComponent<SortingGroup>();
            if (sg == null) sg = go.AddComponent<SortingGroup>();
            sg.sortingOrder = DialogSortingOrder;

            // ② 登记遮罩（不然弹窗自己的按钮会被 Sweep 禁用）
            UiModalGuard.Push(go.transform);

            // ③ 记下来，供 AnyOpen 判断"有没有弹窗挡着"
            _live.Add(go);

            return w;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MenuDialogs.CreateWindow]", ex);
            return null;
        }
    }

    /// <summary>只有「确定」的提示框。red=true 时文字用红字。</summary>
    public static void ShowMessage(string message, bool red = false, Action? onClose = null)
    {
        try
        {
            var w = CreateWindow(new Vector2(5.4f, 1.9f));
            if (w == null) return;

            var tmp = w.AddText(message, 1.5f, TextAlignmentOptions.Center);
            if (tmp != null)
            {
                tmp.color = red ? WarnRed : new Color(1f, 0.95f, 0.85f, 1f);
                tmp.enableWordWrapping = true;
                tmp.ForceMeshUpdate();
            }
            w.AddMargin(0.15f);

            w.AddButton("确定", () =>
            {
                try { onClose?.Invoke(); } catch { }
                w.Close();
            }, new Vector2(2.0f, 0.5f), null);

            w.ApplyCjkFont();   // ⚠️ 必须放在所有 AddText/AddButton 之后
            LightLogger.Log($"[MenuDialogs] 提示弹窗：{message}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MenuDialogs.ShowMessage]", ex);
        }
    }

    /// <summary>是 / 否 选择框。red=true 时文字用红字。</summary>
    public static void ShowConfirm(string message, Action onYes, Action? onNo = null, bool red = true)
    {
        try
        {
            var w = CreateWindow(new Vector2(6.4f, 2.5f));
            if (w == null) return;

            var tmp = w.AddText(message, 1.5f, TextAlignmentOptions.Center);
            if (tmp != null)
            {
                tmp.color = red ? WarnRed : new Color(1f, 0.95f, 0.85f, 1f);
                tmp.enableWordWrapping = true;
                tmp.ForceMeshUpdate();
            }
            w.AddMargin(0.18f);

            w.AddButton("是", () =>
            {
                try { onYes?.Invoke(); } catch (Exception ex) { LightLogger.LogError("[MenuDialogs.Yes]", ex); }
                w.Close();
            }, new Vector2(2.0f, 0.5f), null);

            w.AddButton("否", () =>
            {
                try { onNo?.Invoke(); } catch (Exception ex) { LightLogger.LogError("[MenuDialogs.No]", ex); }
                w.Close();
            }, new Vector2(2.0f, 0.5f), null);

            w.ApplyCjkFont();   // ⚠️ 必须放在所有 AddText/AddButton 之后
            LightLogger.Log($"[MenuDialogs] 选择弹窗：{message}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MenuDialogs.ShowConfirm]", ex);
        }
    }

    /// <summary>警示红（和 DebugModePatch 用的同一档）。</summary>
    private static readonly Color WarnRed = new(0.93f, 0.26f, 0.22f, 1f);

    /// <summary>
    /// 文本输入框弹窗（填具体数值，比如视频音量）。
    ///
    /// 输入框用项目里现成的 <c>GUITextField</c>（回车提交）。
    /// 它是**手动摆位**的，不是 HudUIWindow 的自动流式布局 ——
    /// 因为 HudUIWindow 没有"插入输入框"的 API，只能自己挂上去。
    /// </summary>
    public static void ShowInput(string title, string initial, Action<string> onSubmit)
    {
        try
        {
            var w = CreateWindow(new Vector2(5.6f, 2.6f));
            if (w == null) return;

            var tmp = w.AddText(title, 1.5f, TextAlignmentOptions.Center);
            if (tmp != null) tmp.color = new Color(1f, 0.95f, 0.85f, 1f);

            w.AddMargin(1.0f);   // 给输入框留纵向空间

            try
            {
                var field = Light.UI.Window.GUITextField.Create(
                    w.Screen.transform, new Vector2(2.8f, 0.55f), initial,
                    text =>
                    {
                        try { onSubmit?.Invoke(text ?? ""); }
                        catch (Exception ex) { LightLogger.LogError("[MenuDialogs.InputSubmit]", ex); }
                        w.Close();
                    });

                if (field != null)
                    field.GameObject.transform.localPosition = new Vector3(0f, 0.22f, -1f);
                else
                    LightLogger.LogWarning("[MenuDialogs] 输入框创建失败");
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[MenuDialogs] 输入框异常：{ex.Message}");
            }

            w.AddButton("确定（回车）", () => w.Close(), new Vector2(2.2f, 0.5f), null);
            w.AddButton("取消", () => w.Close(), new Vector2(2.2f, 0.5f), null);

            w.ApplyCjkFont();   // ⚠️ 必须放在所有 AddText/AddButton 之后
            LightLogger.Log($"[MenuDialogs] 输入弹窗：{title}（当前 {initial}）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MenuDialogs.ShowInput]", ex);
        }
    }
}
