using HarmonyLib;
using Light.Utilities;
using LightInDark.Core;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Light.Patches;

/// <summary>
/// 主菜单右下角的「弹射」按钮（原版 EjectMainMenu）：
///  - HideEjectButton = true  → 彻底隐藏按钮（下面的动画参数不生效）
///  - HideEjectButton = false → 保留按钮，按下面的参数改造飞出去的那个船员动画
///
/// 放到 Light/Patches/ 下即可，Harmony.PatchAll() 会自动扫到。
/// 注意：需要先删掉 MainMenuPatch.cs 里旧的 SuppressEjectMenu_Postfix 那一段，否则它会一直把按钮关掉。
/// </summary>
[HarmonyPatch]
public static class EjectMenuPatch
{
    // ───────────────────────── 配置 ─────────────────────────

    /// <summary>true = 隐藏按钮；false = 显示按钮并应用下面的动画修改。</summary>
    public static bool HideEjectButton = true;

    /// <summary>飞行速度倍率。1 = 原版。</summary>
    public static float SpeedMultiplier = 1f;

    /// <summary>船员大小倍率。1 = 原版。</summary>
    public static float ScaleMultiplier = 1f;

    /// <summary>自转速度（度/秒，方向随机）。null = 保持原版。例：360 = 每秒转一圈。</summary>
    public static float? SpinDegPerSec = null;

    /// <summary>轨迹弯曲程度（度/秒，每个船员在 ±此值 之间随机）。0 = 原版直线。NoS 背景粒子用的是 8。</summary>
    public static float CurveDegPerSec = 0f;

    /// <summary>
    /// 自定义贴图的嵌入资源名，null = 用原版船员贴图。
    /// 例：把图片放到 Light/Resources/MainMenu/EjectBean.png，这里填 "Light.Resources.MainMenu.EjectBean.png"
    /// </summary>
    public static string? CustomSpriteResource = null;

    /// <summary>贴图的 PixelsPerUnit，数值越大图越小。</summary>
    public static float CustomSpritePixelsPerUnit = 100f;

    /// <summary>
    /// false：普通彩色图片，切换成默认材质，按原图颜色显示（一般选这个）。
    /// true ：贴图按 AU 船员规范绘制（纯红=身体、纯蓝=阴影、纯绿=面罩），保留玩家材质，像原版一样随机上色。
    /// </summary>
    public static bool CustomSpriteUsesPlayerColors = false;

    // ───────────────────────── 隐藏按钮 ─────────────────────────

    private static bool _loggedHide;
    private static bool _loggedHideError;

    /// <summary>
    /// 每帧检查一次。LateUpdate 在渲染前执行，所以不管原版从哪条路径把它打开，都不会被画出来。
    /// 直接用 MainMenuManager.ejectMenu 字段拿对象，不依赖 GameObject 名字，也不需要每帧遍历层级。
    /// </summary>
    [HarmonyPatch(typeof(MainMenuManager), "LateUpdate")]
    [HarmonyPostfix]
    public static void HideEjectMenu_Postfix(MainMenuManager __instance)
    {
        if (!HideEjectButton) return;
        try
        {
            var menu = __instance.ejectMenu;
            if (menu == null) return;

            var go = menu.gameObject;
            if (!go.activeSelf) return;

            go.SetActive(false);
            if (!_loggedHide)
            {
                _loggedHide = true;
                LightLogger.Log($"[EjectMenuPatch] 已隐藏弹射按钮：{GetPath(go.transform)}");
            }
        }
        catch (Exception ex)
        {
            if (!_loggedHideError)
            {
                _loggedHideError = true;
                LightLogger.LogWarning($"[EjectMenuPatch.Hide] {ex.Message}");
            }
        }
    }

    /// <summary>隐藏状态下不让原版启动按钮的出场协程，避免在未激活对象上 StartCoroutine 报错。</summary>
    [HarmonyPatch(typeof(EjectMainMenu), nameof(EjectMainMenu.StartEjectButton))]
    [HarmonyPrefix]
    public static bool StartEjectButton_Prefix() => !HideEjectButton;

    // ───────────────────────── 修改动画 ─────────────────────────

    private static readonly Dictionary<int, float> _curve = new();
    private static readonly Dictionary<int, Vector3> _lastScale = new();
    private static Sprite? _customSprite;
    private static bool _customSpriteFailed;

    /// <summary>每次进主菜单清一下缓存（旧粒子已随场景销毁）。</summary>
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPostfix]
    public static void MainMenuStart_Postfix()
    {
        _curve.Clear();
        _lastScale.Clear();
    }

    /// <summary>
    /// 原版每弹出一个船员，都会从对象池取一个 PlayerParticle 再调用 PlacePlayer 设置位置/速度/贴图。
    /// 在它之后改，就能覆盖原版的数值。
    /// （已核对 Libs/Assembly-CSharp.dll：PlacePlayer(PlayerParticle part, bool initial)，CallerCount=3，未被内联，可以挂补丁）
    /// </summary>
    [HarmonyPatch(typeof(EjectMainMenu), nameof(EjectMainMenu.PlacePlayer))]
    [HarmonyPostfix]
    public static void PlacePlayer_Postfix([HarmonyArgument(0)] PlayerParticle part)
    {
        if (HideEjectButton || part == null) return;
        try
        {
            int id = part.GetInstanceID();

            // 1) 速度：原版每次都会重新给 velocity 赋值，直接乘倍率即可
            if (SpeedMultiplier != 1f)
                part.velocity *= SpeedMultiplier;

            // 2) 大小：防止原版没有重置 localScale 时被反复乘、越飞越大
            if (ScaleMultiplier != 1f)
            {
                var cur = part.transform.localScale;
                if (!_lastScale.TryGetValue(id, out var last) || cur != last)
                {
                    cur *= ScaleMultiplier;
                    part.transform.localScale = cur;
                    _lastScale[id] = cur;
                }
            }

            // 3) 自转
            if (SpinDegPerSec.HasValue)
                part.angularVelocity = (UnityEngine.Random.value < 0.5f ? -1f : 1f) * SpinDegPerSec.Value;

            // 4) 贴图
            var spr = GetCustomSprite();
            if (spr != null && part.myRend != null)
            {
                part.myRend.sprite = spr;
                if (!CustomSpriteUsesPlayerColors && HatManager.InstanceExists)
                    part.myRend.sharedMaterial = HatManager.Instance.DefaultShader;
            }

            // 5) 弯曲轨迹（真正的旋转在下面的 Update 补丁里做）
            if (CurveDegPerSec != 0f)
                _curve[id] = UnityEngine.Random.Range(-CurveDegPerSec, CurveDegPerSec);
            else
                _curve.Remove(id);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[EjectMenuPatch.PlacePlayer]", ex);
        }
    }

    /// <summary>
    /// 写法参考 NoS 的 Patches/Old/PlayerParticlesPatch.cs（PlayerParticleUpdatePatch）：
    /// 每帧把速度向量旋转一点，直线就变成弧线。
    /// PlayerParticle 也被背景漂浮船员使用，所以只处理在上面登记过的弹射粒子。
    /// </summary>
    [HarmonyPatch(typeof(PlayerParticle), nameof(PlayerParticle.Update))]
    [HarmonyPrefix]
    public static void ParticleUpdate_Prefix(PlayerParticle __instance)
    {
        if (_curve.Count == 0) return;
        if (!_curve.TryGetValue(__instance.GetInstanceID(), out var deg)) return;

        float rad = deg * Mathf.Deg2Rad * Time.deltaTime;
        float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
        var v = __instance.velocity;
        __instance.velocity = new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static Sprite? GetCustomSprite()
    {
        if (_customSprite != null) return _customSprite;
        if (_customSpriteFailed || string.IsNullOrEmpty(CustomSpriteResource)) return null;

        var spr = ResourceHelper.LoadSpriteFromResource(CustomSpriteResource!, CustomSpritePixelsPerUnit);
        if (spr == null)
        {
            _customSpriteFailed = true;
            LightLogger.LogWarning($"[EjectMenuPatch] 找不到贴图资源 {CustomSpriteResource}，改用原版贴图");
            return null;
        }

        // 同 NoS 的 MarkDontUnload：防止切场景时被 UnloadUnusedAssets 回收
        spr.texture.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        spr.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        _customSprite = spr;
        return spr;
    }

    private static string GetPath(Transform t)
    {
        var path = t.name;
        var p = t.parent;
        while (p != null)
        {
            path = p.name + "/" + path;
            p = p.parent;
        }
        return path;
    }
}
