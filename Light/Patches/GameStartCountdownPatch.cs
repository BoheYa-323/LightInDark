using System;
using HarmonyLib;
using LightInDark;
using LightInDark.Core;
using LightInDark.Events;
using LightInDark.Utilities;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Color = LightInDark.Color;

namespace Light.Patches;

/// <summary>
/// 开始游戏倒计时增强：
/// 点击开始后 ——
///   · “取消”与“跳过”两按钮重叠在原开始按钮位置，外形各占一半，
///     分割线顶部在按钮中心、底部向左偏移，形成斜向划分；
///   · 按钮上方显示原版倒计时文本（GameStartText）；
///   · 取消=红色，跳过=淡金色（ModGolden）。
/// 跳过使用与原版倒计时归零完全一致的开局路径（BeginGame），避免绕过主机校验导致黑屏卡死。
/// </summary>
[HarmonyPatch(typeof(GameStartManager))]
public static class GameStartCountdownPatch
{
    private static PassiveButton _cancelButton;
    private static PassiveButton _skipButton;
    private static Vector3 _gameStartTextOriginalPos;

    [HarmonyPatch(nameof(GameStartManager.Start))]
    [HarmonyPostfix]
    public static void StartPostfix(GameStartManager __instance)
    {
        try
        {
            if (!AmongUsClient.Instance?.AmHost ?? true) return;

            _gameStartTextOriginalPos = __instance.GameStartText.transform.localPosition;

            // 关键：与开始按钮同父级，localPosition 才在同一坐标系，不会错位
            Transform startT = __instance.StartButton.transform;
            Transform btnParent = startT.parent != null ? startT.parent : __instance.transform;
            Vector3 startLocalPos = startT.localPosition;
            Vector3 startScale = startT.localScale;

            // 原按钮世界尺寸，用于生成异形贴图
            Vector2 btnSize = new Vector2(2.6f, 0.7f);
            var startSr = __instance.StartButton.GetComponent<SpriteRenderer>();
            if (startSr != null && startSr.sprite != null)
                btnSize = startSr.sprite.bounds.size;

            // ---------- 取消按钮：红色，斜切左半（右下角向左缩） ----------
            _cancelButton = Object.Instantiate(__instance.StartButton, btnParent);
            var cancelLabel = _cancelButton.buttonText;
            if (cancelLabel != null)
            {
                cancelLabel.DestroyTranslator();
                cancelLabel.text = "取消";
            }
            _cancelButton.transform.localPosition = startLocalPos;
            _cancelButton.transform.localScale = startScale;

            _cancelButton.inactiveSprites.GetComponent<SpriteRenderer>().color =
                new UnityEngine.Color(0.8f, 0f, 0f, 1f);
            _cancelButton.activeSprites.GetComponent<SpriteRenderer>().color = UnityEngine.Color.red;
            var cancelShine = _cancelButton.inactiveSprites.transform.Find("Shine");
            if (cancelShine != null) cancelShine.gameObject.SetActive(false);

            _cancelButton.activeTextColor = _cancelButton.inactiveTextColor = UnityEngine.Color.white;
            _cancelButton.OnClick = new Button.ButtonClickedEvent();
            _cancelButton.OnClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                try
                {
                    SoundManager.Instance.StopSound(GameStartManager.Instance.gameStartSound);
                    // OnLobbyCancelStart 由 LobbyCancelStartPatch 在 ResetStartState 时触发
                    GameStartManager.Instance.ResetStartState();
                }
                catch (Exception ex)
                {
                    LightLogger.LogError("[GameStartCountdownPatch.Cancel]", ex);
                }
            }));
            _cancelButton.gameObject.SetActive(false);

            // ---------- 跳过按钮：淡金（ModGolden），斜切右半（左下角向左伸） ----------
            _skipButton = Object.Instantiate(__instance.StartButton, btnParent);
            var skipLabel = _skipButton.buttonText;
            if (skipLabel != null)
            {
                skipLabel.DestroyTranslator();
                skipLabel.text = "跳过";
            }
            _skipButton.transform.localPosition = startLocalPos;
            _skipButton.transform.localScale = startScale;

            var golden = Color.ModGolden.ToUnityColor();
            _skipButton.inactiveSprites.GetComponent<SpriteRenderer>().color = golden;
            _skipButton.activeSprites.GetComponent<SpriteRenderer>().color = golden;
            var skipShine = _skipButton.inactiveSprites.transform.Find("Shine");
            if (skipShine != null) skipShine.gameObject.SetActive(false);

            _skipButton.activeTextColor = _skipButton.inactiveTextColor = UnityEngine.Color.white;
            _skipButton.OnClick = new Button.ButtonClickedEvent();
            _skipButton.OnClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                try
                {
                    SoundManager.Instance.StopSound(GameStartManager.Instance.gameStartSound);
                    var gsm = GameStartManager.Instance;
                    if (gsm == null) return;

                    int playerCount = PlayerControl.AllPlayerControls?.Count ?? 0;

                    // 愚人节恶作剧：使用旧的 ReallyBegin(false) 逻辑（会触发原生流转把
                    // 倒计时重置回 5 秒，假装跳过了但其实没有）。
                    if (LightUtils.IsAprilDay())
                    {
                        gsm.ReallyBegin(false);
                        return;
                    }

                    // 触发“房主跳过倒计时”事件
                    EventTriggers.OnLobbySkipCountdown(playerCount);

                    // 正常逻辑：直接调用 BeginGame()，这与原版倒计时归零时原生 Update 所走的
                    // 完整开局路径完全一致（BeginGame -> 主机校验 -> ReallyBegin -> CoStartGame -> FinallyBegin）。
                    // 不要单独调用 ReallyBegin/FinallyBegin：那样会跳过主机校验与船只生成，
                    // 导致“进了游戏但没进”（黑屏、设置键仍可用）。
                    gsm.BeginGame();
                }
                catch (Exception ex)
                {
                    LightLogger.LogError("[GameStartCountdownPatch.Skip]", ex);
                }
            }));
            // 两按钮就位后替换为斜切异形：贴图 + 点击区（多边形碰撞体）同步成型
            var cancelShape = CreateDiagonalButtonSprite(true, btnSize);
            var skipShape = CreateDiagonalButtonSprite(false, btnSize);
            ApplyDiagonalShape(_cancelButton, cancelShape, true);
            ApplyDiagonalShape(_skipButton, skipShape, false);

            _skipButton.gameObject.SetActive(false);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[GameStartCountdownPatch.Start]", ex);
        }
    }

    /// <summary>生成斜切按钮贴图：圆角矩形 + 斜向分割边（左块右下缩 / 右块左下伸）。</summary>
    private static Sprite CreateDiagonalButtonSprite(bool isLeft, Vector2 worldSize)
    {
        const float Ppu = 100f;
        int tw = Mathf.Clamp(Mathf.RoundToInt(worldSize.x * Ppu), 16, 1024);
        int th = Mathf.Clamp(Mathf.RoundToInt(worldSize.y * Ppu), 8, 1024);
        float cx = (tw - 1) * 0.5f, cy = (th - 1) * 0.5f;
        float shift = tw * 0.2f;          // 分割线底部相对顶部的左移量
        float radius = th * 0.24f;        // 圆角半径
        float hw = tw * 0.5f, hh = th * 0.5f;

        var tex = new Texture2D(tw, th, TextureFormat.ARGB32, false);
        var pixels = new Color32[tw * th];
        for (int y = 0; y < th; y++)
        {
            float t = th > 1 ? y / (th - 1f) : 0f;      // 0=底 1=顶
            float seam = cx - shift * (1f - t);          // 顶部在中心，底部左移
            for (int x = 0; x < tw; x++)
            {
                // 圆角矩形覆盖度（SDF，1px 抗锯齿）
                float dx = Mathf.Abs(x - cx) - (hw - radius);
                float dy = Mathf.Abs(y - cy) - (hh - radius);
                float ox = Mathf.Max(dx, 0f), oy = Mathf.Max(dy, 0f);
                float dist = Mathf.Sqrt(ox * ox + oy * oy)
                           + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;
                float cover = Mathf.Clamp01(0.5f - dist);

                // 斜缝遮罩（2px 过渡）
                float seamMask = isLeft
                    ? Mathf.Clamp01((seam - x) * 0.5f + 0.5f)
                    : Mathf.Clamp01((x - seam) * 0.5f + 0.5f);

                byte a = (byte)Mathf.RoundToInt(255f * cover * seamMask);
                pixels[y * tw + x] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);

        var sp = Sprite.Create(tex, new Rect(0, 0, tw, th),
            new Vector2(0.5f, 0.5f), Ppu, 0, SpriteMeshType.FullRect);
        sp.name = isLeft ? "LightDiagBtnL" : "LightDiagBtnR";
        return sp;
    }

    /// <summary>替换按钮常态/悬停贴图为异形，并把点击区换成与形状一致的多边形碰撞体。</summary>
    private static void ApplyDiagonalShape(PassiveButton btn, Sprite shape, bool isLeft)
    {
        foreach (var go in new[] { btn.inactiveSprites, btn.activeSprites })
        {
            if (go == null) continue;
            var r = go.GetComponent<SpriteRenderer>();
            if (r != null) r.sprite = shape;
        }

        var box = btn.GetComponent<BoxCollider2D>();
        if (box != null) Object.Destroy(box);

        var poly = btn.gameObject.AddComponent<PolygonCollider2D>();
        poly.isTrigger = true;

        // 贴图尺寸按父级缩放换算回本地坐标
        var ls = btn.transform.lossyScale;
        float hw = shape.bounds.extents.x / ls.x;
        float hh = shape.bounds.extents.y / ls.y;
        float sh = hw * 0.4f;             // 分割线底部左移量（与贴图 shift 一致）

        poly.points = isLeft
            ? new[] { new Vector2(-hw, -hh), new Vector2(-sh, -hh), new Vector2(0f, hh), new Vector2(-hw, hh) }
            : new[] { new Vector2(-sh, -hh), new Vector2(hw, -hh), new Vector2(hw, hh), new Vector2(0f, hh) };

        // 关键：PassiveButtonManager 命中测试用的是注册时缓存的 Colliders 数组，
        // 销毁原碰撞体后必须重写该数组指向新碰撞体，否则点击永远无效
        try
        {
            btn.Colliders = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider2D>(
                new Collider2D[] { poly });
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning("[DiagonalButton] 刷新 Colliders 失败: " + ex.Message);
        }

        // 文字：缩小到 0.8 倍并移到各自区块中心，两段文字不再互相重叠
        if (btn.buttonText != null)
        {
            var label = btn.buttonText;
            try
            {
                label.enableAutoSizing = false;
                label.fontSize *= 0.8f;
            }
            catch { }
            var pos = label.transform.localPosition;
            pos.x = isLeft ? -hw * 0.5f : hw * 0.5f;
            label.transform.localPosition = pos;
        }
    }

    [HarmonyPatch(nameof(GameStartManager.Update))]
    [HarmonyPostfix]
    public static void UpdatePostfix(GameStartManager __instance)
    {
        try
        {
            if (!AmongUsClient.Instance?.AmHost ?? true) return;
            if (_cancelButton == null || _skipButton == null) return;

            bool counting = __instance.startState == GameStartManager.StartingStates.Countdown;

            _cancelButton.gameObject.SetActive(counting);
            _skipButton.gameObject.SetActive(counting);
            __instance.StartButton.gameObject.SetActive(!counting);

            // 原版倒计时文本移到开始按钮上方（y 轴正方向）；结束时还原位置
            if (counting)
            {
                __instance.GameStartText.transform.localPosition =
                    __instance.StartButton.transform.localPosition + Vector3.up * 1.6f;
            }
            else
            {
                __instance.GameStartText.transform.localPosition = _gameStartTextOriginalPos;
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning("[GameStartCountdownPatch.Update] " + ex.Message);
        }
    }

    private static void DestroyTranslator(this MonoBehaviour mb)
    {
        try
        {
            var tr = mb.GetComponent<TextTranslatorTMP>();
            if (tr != null) Object.Destroy(tr);
        }
        catch { }
    }
}

/// <summary>
/// 大厅生命周期事件：房主开启/取消倒计时时触发 Lobby 事件。
/// （“跳过倒计时”由上方跳过按钮点击里触发 <see cref="EventTriggers.OnLobbySkipCountdown"/>。）
/// </summary>
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.SetStartCounter))]
public static class LobbyCountdownStartPatch
{
    public static void Postfix(GameStartManager __instance, sbyte sec)
    {
        try
        {
            if (sec <= 0) return; // -1=重置/取消，正数=倒计时开始
            if (!AmongUsClient.Instance?.AmHost ?? true) return;

            int playerCount = PlayerControl.AllPlayerControls?.Count ?? 0;
            if (EventTriggers.OnLobbyStartGame(playerCount))
                EventTriggers.OnLobbyCountdownStart(playerCount, sec);
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning("[LobbyCountdownStartPatch.Postfix] " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.ResetStartState))]
public static class LobbyCancelStartPatch
{
    public static void Postfix(GameStartManager __instance)
    {
        try
        {
            if (!AmongUsClient.Instance?.AmHost ?? true) return;
            int remaining = 0;
            if (__instance.startState == GameStartManager.StartingStates.Countdown)
                remaining = (int)Mathf.Ceil(__instance.countDownTimer);
            EventTriggers.OnLobbyCancelStart(remaining);
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning("[LobbyCancelStartPatch.Postfix] " + ex.Message);
        }
    }
}

#if DEBUG
/// <summary>
/// DEBUG 下允许使用 1 名玩家即可开始游戏（参考 Nebula Free-Play / TownOfNext）。
/// 仅在 DEBUG 编译构建时启用。
/// </summary>
[HarmonyPatch(typeof(GameStartManager))]
public static class DebugMinPlayersPatch
{
    [HarmonyPatch(nameof(GameStartManager.Start))]
    [HarmonyPostfix]
    public static void StartPostfix(GameStartManager __instance)
    {
        try
        {
            if (!AmongUsClient.Instance?.AmHost ?? true) return;
            __instance.MinPlayers = 1;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning("[DebugMinPlayersPatch.Start] " + ex.Message);
        }
    }

    [HarmonyPatch(nameof(GameStartManager.Update))]
    [HarmonyPostfix]
    public static void UpdatePostfix(GameStartManager __instance)
    {
        try
        {
            if (!AmongUsClient.Instance?.AmHost ?? true) return;
            __instance.MinPlayers = 1;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning("[DebugMinPlayersPatch.Update] " + ex.Message);
        }
    }
}
#endif
