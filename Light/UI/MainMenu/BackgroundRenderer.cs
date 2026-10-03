using System;
using System.Collections.Generic;
using System.Linq;
using LightInDark.Core;
using LightInDark.UI.Window;          // LayerExpansion
using UnityEngine;
using UnityEngine.Video;

namespace Light.UI.MainMenu;

/// <summary>
/// 主界面自定义背景（图片 / 视频）。
///
/// =====================================================================
///  【为什么重写：原实现的两个毛病】
///
/// 旧实现（<c>ImageGalleryPanel.EnsureBgObject</c>）是：
///     var go = new GameObject("LightBackground");
///     go.transform.position = new Vector3(0, 0, 520f);   // 世界坐标写死
///     UnityEngine.Object.DontDestroyOnLoad(go);          // 跨场景不销毁
///
/// → **外泄**：DontDestroyOnLoad 让它活到所有场景，进游戏/进大厅都还在，
///   只能靠 MainMenuPatch.LateUpdate 里"进别的场景就 SetActive(false) + 缩到 0.0001"
///   这种补丁去藏，一旦某个场景没覆盖到就露出来。
/// → **莫名其妙消失**：位置是写死的世界坐标 z=520。主菜单相机一旦
///   换位置/换朝向（重进主菜单、分辨率变化、MatchMaking 场景），
///   它就跑出视锥了 → 什么都看不见，但对象还在、日志也不报错。
///
/// =====================================================================
///  【现在的做法（照抄参考实现 MainMenuBackground 的正交思路）】
///
///   · **不用 DontDestroyOnLoad**，对象挂在主菜单场景里 → 场景卸载自动销毁，
///     从根上不可能外泄；
///   · **每帧跟随相机**，放在远裁剪面前一点点 → 永远在视野里，
///     也永远在所有 UI 后面，不存在"跑出视锥"；
///   · 用**网格**而不是 SpriteRenderer，因为视频要靠
///     <c>VideoRenderMode.MaterialOverride</c> 往材质的 _MainTex 上写。
///
/// ⚠️ 关于 Unity 的"假 null"（AGENTS.md §4.6）：这里每一步都重新取组件、
///    每次都判空，场景切换把对象销毁后会自动走重建分支。
/// </summary>
public static class BackgroundRenderer
{
    private const string RootName = "LightMainMenuBackground";
    private const int MediaSortingOrder = -30000;   // 远低于任何 UI
    private const float VideoPrepareTimeout = 20f;

    // ---- 运行时对象 ----
    private static GameObject? _root;
    private static MeshRenderer? _mediaRenderer;
    private static MeshFilter? _mediaFilter;
    private static MeshRenderer? _backdropRenderer;
    private static MeshFilter? _backdropFilter;
    private static Material? _mediaMaterial;
    private static VideoPlayer? _video;

    private static bool _mediaReady;
    /// <summary>视频是否已经真正开播（Prepare 完成之后才置 true）。</summary>
    private static bool _videoStarted;

    /// <summary>
    /// 开播后音量渐入的帧数。
    /// ⚠️ 这是"开头炸音"的最终修复：Play() 之后头几帧解码器可能还在吐脏样本，
    ///    所以那几帧音量保持 0（听不见），再渐变推上去（避免增益突变爆音）。
    /// </summary>
    private const int AudioFadeFrames = 8;
    private static int _fadeLeft;
    private static float _fadeTarget = 1f;
    private static float _mediaW = 1f, _mediaH = 1f;
    private static float _createdAt;
    private static int _generation;

    private static int _lastLayer = -1;
    private static (float vw, float vh, float mw, float mh, int fit, float dim, bool ready)? _lastLayout;

    private static float _lastVolume = -1f;
    private static bool _vanillaMusicMuted;

    private static string _currentPath = "";
    private static string _lastFailReason = "";

    // 被我们藏起来的原版背景（销毁时恢复）
    private static readonly List<(SpriteRenderer sr, bool wasEnabled)> _hiddenRenderers = new();
    private static readonly List<(GameObject go, bool wasActive)> _hiddenObjects = new();

    // 贴图缓存
    private static Texture2D? _cachedTex;
    private static string? _cachedTexKey;

    /// <summary>上一次失败原因（界面用来提示）。空 = 没失败。</summary>
    public static string LastFailReason => _lastFailReason;

    /// <summary>当前生效的素材路径（诊断用）。</summary>
    public static string CurrentPath => _currentPath;

    /// <summary>视频是否已准备好（界面用来显示分辨率）。</summary>
    public static bool IsReady => _mediaReady;

    // =====================================================================
    //  对外入口
    // =====================================================================

    /// <summary>主菜单出现时调用：按设置重建背景。</summary>
    public static void OnMainMenuStart(int uiLayer)
    {
        _lastLayer = uiLayer;
        Reapply();
    }

    /// <summary>
    /// 设置变化后重新应用。
    ///
    /// ⚠️⚠️ **同一个素材时走"原地刷新"，绝不拆建。**
    ///   用户反馈"应用东西会有 1 秒黑屏 + 音频卡顿" —— 那就是 Teardown+Build 的代价：
    ///   VideoPlayer 被销毁 → 重建 → 必须重新 prepare（约 1 秒），
    ///   这段期间只能看到黑底（Backdrop），音频也是断的。
    ///   上一版的"续播 seek"只能保住**进度**，保不住**不断**。
    ///
    ///   原地刷新只重跑"隐藏原版背景 / 船员显示 / 布局 / 音量"这几件事，
    ///   视频一帧都不断、声音一秒都不卡。
    /// </summary>
    /// <param name="rerollRandom">
    /// true = 强制重新随机（用户重新点了「随机」那一行时才需要）；
    /// false = 随机模式下沿用当前这张，避免"改个船员显示把背景也换了"。
    /// </param>
    public static void Reapply(bool rerollRandom = false)
    {
        var entry = IsInMenuScene() ? BackgroundStore.Resolve() : null;

        // 随机模式下，没要求重roll 就沿用当前这张（素材还在的话）
        if (!rerollRandom && entry != null && BackgroundStore.Selected == BackgroundStore.SelectionRandom
            && !string.IsNullOrEmpty(_currentPath) && _currentPath != entry.FullPath
            && System.IO.File.Exists(_currentPath))
        {
            entry = new BackgroundEntry
            {
                FullPath = _currentPath,
                FileName = System.IO.Path.GetFileName(_currentPath),
                IsVideo = BackgroundStore.IsVideoExt(System.IO.Path.GetExtension(_currentPath)),
            };
        }

        string newPath = entry?.FullPath ?? "";

        // ---- 同一个素材 → 原地刷新（视频不中断） ----
        if (_root != null && !string.IsNullOrEmpty(newPath) && newPath == _currentPath)
        {
            RefreshInPlace();
            _lastFailReason = "";
            return;
        }

        // ---- 换素材 / 之前没建过 → 老老实实重建 ----
        Teardown();

        if (!IsInMenuScene()) return;

        if (entry == null)
        {
            _lastFailReason = "";
            _currentPath = "";
            return;     // 用原版背景
        }

        try
        {
            Build(entry);
            _lastFailReason = "";
        }
        catch (Exception ex)
        {
            _lastFailReason = ex.Message;
            LightLogger.LogError("[BackgroundRenderer.Build]", ex);
            Teardown();
        }
    }

    /// <summary>
    /// 原地刷新：不销毁网格/材质/VideoPlayer，只重算"原版背景与船员的隐藏"、"布局"、"音量"。
    /// 用于同一个素材下的设置变更 —— 视频不会黑屏、声音不会断。
    /// </summary>
    private static void RefreshInPlace()
    {
        try
        {
            // 船员显示 / 原版背景的隐藏集合可能变了 → 先还原再重算
            RestoreHidden();

            var cam = _root != null ? FindCamera(_root.layer) : null;
            if (cam != null) HideVanilla(cam);

            _lastLayout = null;      // 下一帧 Tick 会按新设置重排网格
            ApplyVideoAudio();       // 音量可能变了

            LightLogger.Log("[BackgroundRenderer] 原地刷新（未重建，视频不中断）");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[BackgroundRenderer.RefreshInPlace] {ex.Message}");
        }
    }

    /// <summary>适配模式 / 调暗变了：不用重建，下一帧 Tick 会自动重排。</summary>
    public static void RefreshLayout() => _lastLayout = null;

    /// <summary>主菜单每帧驱动（MainMenuPatch.LateUpdate 里调）。</summary>
    public static void Tick()
    {
        try
        {
            // 场景不对 → 立刻收拾干净，杜绝外泄
            if (!IsInMenuScene())
            {
                if (_root != null) Teardown();
                return;
            }

            // 对象被场景切换销毁了（Unity 假 null）→ 重建
            if (_root == null && !string.IsNullOrEmpty(BackgroundStore.Selected))
            {
                if (_root == null && _lastFailReason == "" && ShouldHaveBackground())
                    Reapply();
                return;
            }

            if (_root == null) return;

            var cam = FindCamera(_root.layer);
            if (cam == null) return;

            // ① 跟随相机：放在远裁剪面前一点点 → 永远在视野里、永远在所有 UI 后面
            var camT = cam.transform;
            float near = cam.nearClipPlane, far = cam.farClipPlane;
            float depth = far - Mathf.Max(2f, (far - Mathf.Max(near, 0f)) * 0.02f);
            _root.transform.position = camT.position + camT.forward * depth;
            _root.transform.rotation = camT.rotation;

            // ② 视频：等它**准备好**再开播。
            //
            // ⚠️⚠️ 用户反馈"进游戏的一帧会炸音频"。
            //    根因：以前 BuildVideo 里是**立刻 Play()**。
            //    那时解码器还没 ready，Direct 音频输出会吐出一段未初始化的缓冲
            //    → 就是那一声爆音（而且此时音量还没真正生效，可能是满音量）。
            //
            //    现在改成：BuildVideo 只调 Prepare()，**完全不播**；
            //    等到 isPrepared 之后，先把音轨/音量/静音都设好，再 Play()。
            //    准备期间视频和音频都是停的，所以不会漏出任何东西。
            if (_video != null && !_mediaReady)
            {
                if (_video.isPrepared && _video.width > 0 && _video.height > 0)
                {
                    _mediaW = _video.width;
                    _mediaH = _video.height;

                    // ① 先确立音频状态（音轨 / 音量 / 静音）—— 必须在 Play() 之前，
                    //    否则第一帧可能用默认音量出声（"炸音"）
                    _video.SetDirectAudioMute(0, true);      // 先彻底闭麦
                    ApplyVideoAudio();                       // 再按设置设好音量

                    // ② 续播：必须等 prepare 之后才能 seek（prepare 前设 time 无效/会抛）
                    if (_pendingResume > 0.05)
                    {
                        double target = _pendingResume;
                        _pendingResume = 0;
                        try
                        {
                            _video.time = target;
                            LightLogger.Log($"[BackgroundRenderer] 已续播到 {target:F1} 秒（不重头播）");
                        }
                        catch (Exception ex)
                        {
                            LightLogger.LogWarning($"[BackgroundRenderer] 续播 seek 失败：{ex.Message}");
                        }
                    }

                    // ③ 一切就绪，现在才开播
                    try { _video.Play(); }
                    catch (Exception ex) { LightLogger.LogWarning($"[BackgroundRenderer] Play 失败：{ex.Message}"); }

                    // ④⚠️⚠️ **不要在这一帧就解除静音！**
                    //   这是"开头炸音"反复出现的原因：`Play()` 之后的头几帧，
                    //   解码器吐出的仍可能是未初始化/未对齐的缓冲。
                    //   上一版虽然改成"先 Prepare 再 Play"，但解除静音和 Play 同一帧，
                    //   那段脏样本照样听得见。
                    //
                    //   现在改成：Play 之后**继续保持 0 音量**，由 Tick 在随后几帧里
                    //   把音量**渐变**推上去（见 AudioFadeFrames）。这样：
                    //     ① 脏样本那几帧音量正好是 0，听不见；
                    //     ② 音量是渐变的不是跳变的，也不会因为增益突变产生爆音。
                    _mediaReady = true;
                    _videoStarted = true;
                    if (_mediaRenderer != null) _mediaRenderer.enabled = true;

                    _fadeTarget = EffectiveVideoVolume();
                    _fadeLeft = AudioFadeFrames;
                    try
                    {
                        _video.SetDirectAudioMute(0, true);
                        _video.SetDirectAudioVolume(0, 0f);
                    }
                    catch { }

                    LightLogger.Log($"[BackgroundRenderer] 视频已就绪并开播（音量渐入到 {_fadeTarget * 100f:F0}%，{AudioFadeFrames} 帧）");
                }
                else if (Time.realtimeSinceStartup - _createdAt > VideoPrepareTimeout)
                {
                    _lastFailReason = "视频在 20 秒内没有准备好，可能是编码不支持（建议 H.264 的 mp4）。已退回原版背景。";
                    LightLogger.LogWarning("[BackgroundRenderer] " + _lastFailReason);
                    Teardown();
                    return;
                }
            }

            // ③ 布局：屏幕/素材尺寸变了才重建网格
            float viewH = cam.orthographic
                ? cam.orthographicSize * 2f
                : 2f * depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float viewW = viewH * cam.aspect;

            var layout = (viewW, viewH, _mediaW, _mediaH,
                          (int)BackgroundStore.Fit, BackgroundStore.Dim, _mediaReady);
            if (_lastLayout == null || !_lastLayout.Value.Equals(layout))
            {
                _lastLayout = layout;
                Layout(viewW, viewH);
            }

            // ③.5 开播后的音量渐入（修复"开头炸音"）
            //     Play() 之后的头几帧，解码器可能还在吐未初始化/未对齐的缓冲。
            //     这里让那几帧音量为 0，然后逐帧推上去 ——
            //     脏样本听不见，增益又是渐变的，不会爆音。
            if (_videoStarted && _fadeLeft > 0 && _video != null)
            {
                _fadeLeft--;
                float prog = 1f - _fadeLeft / (float)AudioFadeFrames;   // 0 → 1
                float v = _fadeTarget * prog;
                try
                {
                    _video.SetDirectAudioMute(0, false);   // 音量本身已经是 0 起步，不必再 mute
                    _video.SetDirectAudioVolume(0, v);

                    if (_fadeLeft == 0)
                    {
                        // 收尾：写死成精确目标值，避免浮点误差
                        _video.SetDirectAudioVolume(0, _fadeTarget);
                        _video.SetDirectAudioMute(0, _fadeTarget <= 0.001f);
                        LightLogger.Log($"[BackgroundRenderer] 音量渐入完成：{_fadeTarget * 100f:F0}%");
                    }
                }
                catch { }
            }

            // ④ 视频音量（跟随设置；模式变了下一帧就会重新应用）
            if (_video != null)
            {
                float vol = EffectiveVideoVolume();
                // ⚠️ 只有**已经开播**才在这里实时改音量。
                //    准备期间是刻意闭麦的，这时候调音量会把它提前解除静音 → 又会炸音。
                if (_videoStarted && !Mathf.Approximately(vol, _lastVolume))
                {
                    _lastVolume = vol;
                    ApplyVideoAudio();
                }
                if (_mediaReady && _video.audioTrackCount > 0) MuteVanillaMusic();
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[BackgroundRenderer.Tick] {ex.Message}");
            Teardown();
        }
    }

    private static bool ShouldHaveBackground()
    {
        var sel = BackgroundStore.Selected;
        return !string.IsNullOrEmpty(sel) && BackgroundStore.Scan().Count > 0;
    }

    private static bool IsInMenuScene()
    {
        try
        {
            var n = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            return n == "MainMenu" || n == "MatchMaking";
        }
        catch { return false; }
    }

    // =====================================================================
    //  构建 / 拆除
    // =====================================================================

    private static void Build(BackgroundEntry entry)
    {
        int layer = _lastLayer >= 0 ? _lastLayer : LayerExpansion.GetUILayer();
        var cam = FindCamera(layer);
        if (cam == null)
        {
            _lastFailReason = "找不到主界面相机。";
            return;
        }

        _generation++;
        _currentPath = entry.FullPath;
        _mediaReady = false;
        _mediaW = _mediaH = 1f;

        // ⚠️⚠️ **绝对不要调用 DontDestroyOnLoad** —— 旧实现就是在这一行上翻车的：
        //    它让背景对象活过所有场景，进游戏/进大厅都还在，只能靠一堆
        //    "进别的场景就 SetActive(false) + 缩到 0.0001" 的补丁去藏，
        //    漏掉任何一个场景就露出来（用户报的"背景图外泄"）。
        //    Unity 没有"取消 DontDestroyOnLoad"的 API，所以唯一的正确做法是根本别调。
        //    不调 → 对象属于当前场景 → 场景卸载时自动销毁 → 从根上不可能外泄。
        _root = new GameObject(RootName);
        _root.layer = layer;
        _root.transform.SetParent(null, false);

        // 黑底（Contain 模式的黑边 + 视频加载期用）
        var unlit = Shader.Find("Sprites/Default");
        _backdropFilter = CreateQuad("Backdrop", _root.transform, layer, unlit, out _backdropRenderer);
        if (_backdropRenderer != null)
        {
            _backdropRenderer.sortingOrder = MediaSortingOrder - 1;
            SetMaterialColor(_backdropRenderer, Color.black);
        }

        // 素材网格
        _mediaFilter = CreateQuad("Media", _root.transform, layer, unlit, out _mediaRenderer);
        if (_mediaRenderer != null)
        {
            _mediaRenderer.sortingOrder = MediaSortingOrder;
            _mediaMaterial = _mediaRenderer.material;
        }

        // 同一个素材重建 → 续播；换了素材 → 从头
        PrepareResume(entry.FullPath);

        if (entry.IsVideo) BuildVideo(entry);
        else BuildImage(entry);

        HideVanilla(cam);
        _createdAt = Time.realtimeSinceStartup;
    }

    private static void BuildImage(BackgroundEntry entry)
    {
        var tex = LoadTexture(entry.FullPath);
        if (tex == null)
        {
            _lastFailReason = $"图片解码失败（只支持 PNG/JPG）：{entry.FileName}";
            LightLogger.LogWarning("[BackgroundRenderer] " + _lastFailReason);
            Teardown();
            return;
        }
        if (_mediaMaterial != null) _mediaMaterial.mainTexture = tex;
        _mediaW = tex.width;
        _mediaH = tex.height;
        _mediaReady = true;
    }

    private static void BuildVideo(BackgroundEntry entry)
    {
        if (_mediaRenderer == null || _root == null)
        {
            Teardown();
            return;
        }

        // 第一帧出来之前先藏起来，避免闪白
        _mediaRenderer.enabled = false;

        try
        {
            // ⚠️ 用户明确要求：**不要反射**，直接 using UnityEngine.Video 调用。
            //    我们是 BepInEx 插件，Libs 里有 UnityEngine.VideoModule.dll，
            //    编译期就能引用，不需要参考实现那套反射兜底。
            _video = _root.AddComponent<VideoPlayer>();
            _video.playOnAwake = false;
            _video.source = VideoSource.Url;
            _video.url = entry.FullPath;
            _video.isLooping = true;
            _video.renderMode = VideoRenderMode.MaterialOverride;
            _video.targetMaterialRenderer = _mediaRenderer;
            _video.targetMaterialProperty = "_MainTex";
            _video.audioOutputMode = VideoAudioOutputMode.Direct;
            _video.skipOnDrop = true;
            // 我们已经在 isPrepared 之前把渲染器 enabled=false 了，不会闪白；
            // 设 true 会让 Play() 一直等到首帧才真正开始，白白多等一截。
            _video.waitForFirstFrame = false;
            // Direct 输出模式下要告诉播放器"我要接管 1 条音轨"，否则声音出不来。
            // 必须在 Play() 之前设。
            try { _video.controlledAudioTrackCount = 1; } catch { }

            _lastVolume = EffectiveVideoVolume();

            // ⚠️⚠️ 关键：这里**只 Prepare，不 Play**。
            //   以前直接 Play()，解码器还没就绪就开播 →
            //   Direct 音频输出会吐一段未初始化缓冲 = 那一声"炸音"，而且此时
            //   音量还没真正生效，可能是满音量。
            //
            //   现在：Prepare() 期间视频/音频都是停的，什么都漏不出来；
            //   等 Tick 里 isPrepared 之后再设音量、再 Play()。
            _videoStarted = false;

            // 准备期间先彻底闭麦（双保险：万一播放器自己在准备阶段出声）
            try
            {
                _video.EnableAudioTrack(0, true);
                _video.SetDirectAudioVolume(0, 0f);
                _video.SetDirectAudioMute(0, true);
            }
            catch { }

            _video.Prepare();

            LightLogger.Log($"[BackgroundRenderer] 视频开始准备（未开播）：{entry.FileName}，" +
                            $"目标音量={_lastVolume:F2}（{BackgroundStore.VideoVolumeLabel}）");
        }
        catch (Exception ex)
        {
            _lastFailReason = $"视频初始化失败：{ex.Message}";
            LightLogger.LogError("[BackgroundRenderer.BuildVideo]", ex);
            Teardown();
        }
    }

    /// <summary>拆除：销毁网格/材质/组件，恢复原版背景与音乐。可重复调用。</summary>
    public static void Teardown()
    {
        _generation++;
        _lastLayout = null;
        _mediaReady = false;

        // ⚠️⚠️ 拆之前先把播放进度记下来。
        //    用户反馈"改音量 / 应用任何东西视频都会重新播放" ——
        //    因为 Reapply() = Teardown() + Build()，VideoPlayer 被销毁重建，自然从头播。
        //    这里存下 (路径, 秒数)，重建后如果还是同一个素材就 seek 回去（见 Tick 里那段）。
        SavePlaybackPosition();

        _currentPath = "";

        try
        {
            if (_video != null)
            {
                try { _video.Stop(); } catch { }
                try { UnityEngine.Object.Destroy(_video); } catch { }
            }
            _video = null;

            if (_mediaFilter != null && _mediaFilter.sharedMesh != null)
                UnityEngine.Object.Destroy(_mediaFilter.sharedMesh);
            if (_backdropFilter != null && _backdropFilter.sharedMesh != null)
                UnityEngine.Object.Destroy(_backdropFilter.sharedMesh);

            if (_backdropRenderer != null && _backdropRenderer.sharedMaterial != null)
                UnityEngine.Object.Destroy(_backdropRenderer.sharedMaterial);
            if (_mediaMaterial != null)
                UnityEngine.Object.Destroy(_mediaMaterial);

            if (_root != null) UnityEngine.Object.Destroy(_root);
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[BackgroundRenderer.Teardown] {ex.Message}");
        }

        _root = null;
        _mediaRenderer = null;
        _mediaFilter = null;
        _backdropRenderer = null;
        _backdropFilter = null;
        _mediaMaterial = null;
        _lastLayout = null;
        _lastVolume = -1f;
        _mediaW = _mediaH = 1f;
        _loggedAudioDiag = false;

        RestoreHidden();
        RestoreVanillaMusic();
    }

    public static void Shutdown()
    {
        // 真正离开主菜单 → 不该续播，下次进来从头开始
        _resumeTime = 0;
        _resumePath = "";
        _pendingResume = 0;
        Teardown();
    }

    // =====================================================================
    //  播放进度保持（"应用设置不该让视频重头播"）
    // =====================================================================

    /// <summary>上次拆掉时的播放位置（秒）。</summary>
    private static double _resumeTime;
    /// <summary>上次拆掉时播的是哪个素材 —— 换了素材就不该续播。</summary>
    private static string _resumePath = "";
    /// <summary>这次重建要 seek 到的位置（0 = 从头播）。</summary>
    private static double _pendingResume;

    /// <summary>拆之前记下播放进度。</summary>
    private static void SavePlaybackPosition()
    {
        try
        {
            if (_video == null || !_video.isPrepared) return;
            if (string.IsNullOrEmpty(_currentPath)) return;

            _resumeTime = _video.time;
            _resumePath = _currentPath;
        }
        catch { }
    }

    /// <summary>这次要建的是不是"同一个素材"—— 是就续播，否则从头。</summary>
    private static void PrepareResume(string newPath)
    {
        if (!string.IsNullOrEmpty(newPath) && newPath == _resumePath && _resumeTime > 0.05)
            _pendingResume = _resumeTime;
        else
            _pendingResume = 0;
    }

    /// <summary>
    /// 只改音量：**不重建**，直接把新音量喂给正在播的视频。
    /// （重建会黑一帧 + 重新解码，改个音量完全没必要。）
    /// </summary>
    public static void ApplyAudioOnly() => ApplyVideoAudio();

    /// <summary>
    /// 只改裁剪模式 / 调暗：**不重建**，下一帧 Tick 会自动重排网格。
    /// </summary>
    public static void ApplyLayoutOnly() => RefreshLayout();

    // =====================================================================
    //  网格 / 材质
    // =====================================================================

    /// <summary>造一个 1×1 居中的四边形网格（靠 localScale 拉成任意尺寸）。</summary>
    private static Mesh CreateUnitQuad()
    {
        var mesh = new Mesh { name = "BackgroundQuad" };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3( 0.5f, -0.5f, 0f),
            new Vector3( 0.5f,  0.5f, 0f),
            new Vector3(-0.5f,  0.5f, 0f),
        };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(1f, 1f), new Vector2(0f, 1f),
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        return mesh;
    }

    private static MeshFilter CreateQuad(string name, Transform parent, int layer,
        Shader? shader, out MeshRenderer renderer)
    {
        var go = new GameObject(name);
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;

        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = CreateUnitQuad();

        renderer = go.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        var mat = shader != null ? new Material(shader) : null;
        if (mat != null) renderer.sharedMaterial = mat;

        return filter;
    }

    private static void SetMaterialColor(Renderer r, Color c)
    {
        try
        {
            var m = r.sharedMaterial;
            if (m != null && m.HasProperty("_Color")) m.color = c;
        }
        catch { }
    }

    /// <summary>按屏幕尺寸 + 适配模式摆放媒体网格。</summary>
    private static void Layout(float viewW, float viewH)
    {
        if (_backdropFilter != null && _backdropFilter.sharedMesh != null)
        {
            // 稍微放大一点，避免边缘露缝
            var t = _backdropFilter.transform;
            t.localScale = new Vector3(viewW * 1.05f, viewH * 1.05f, 1f);
        }

        if (!_mediaReady || _mediaFilter == null) return;

        float screenAspect = viewW / viewH;
        float mediaAspect = _mediaW / Mathf.Max(1f, _mediaH);

        float w = viewW, h = viewH;
        switch (BackgroundStore.Fit)
        {
            case BackgroundFit.Cover:
                // 等比放大到铺满，多出来的溢出屏幕外（不需要裁 UV，屏幕边缘天然裁掉）
                if (mediaAspect > screenAspect) w = viewH * mediaAspect;
                else h = viewW / mediaAspect;
                break;

            case BackgroundFit.Contain:
                // 等比缩小到完整可见，四周留黑（黑底在 Backdrop 那层）
                if (mediaAspect > screenAspect) h = viewW / mediaAspect;
                else w = viewH * mediaAspect;
                break;

            case BackgroundFit.Stretch:
                // 不动，直接铺满
                break;
        }

        _mediaFilter.transform.localScale = new Vector3(w, h, 1f);

        if (_mediaRenderer != null)
        {
            float k = 1f - BackgroundStore.Dim;
            SetMaterialColor(_mediaRenderer, new Color(k, k, k, 1f));
        }
    }

    private static Texture2D? LoadTexture(string path)
    {
        try
        {
            string key = path + "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            if (_cachedTex != null && _cachedTexKey == key) return _cachedTex;

            if (_cachedTex != null) UnityEngine.Object.Destroy(_cachedTex);
            _cachedTex = null;
            _cachedTexKey = null;

            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[BackgroundRenderer] 读不到图片 {path}：{ex.Message}");
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            if (!ImageConversion.LoadImage(tex, bytes, false))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            // 场景切换时别被 UnloadUnusedAssets 回收（AGENTS.md §4.6 的"假 null"）
            tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            _cachedTex = tex;
            _cachedTexKey = key;
            return tex;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[BackgroundRenderer.LoadTexture]", ex);
            return null;
        }
    }

    // =====================================================================
    //  原版背景 / 音乐
    // =====================================================================

    private static readonly string[] VanillaBackgroundNames = { "BackgroundTexture", "WindowShine" };

    private static void HideVanilla(Camera cam)
    {
        try
        {
            var menu = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
            if (menu == null) return;

            var all = new List<SpriteRenderer>();
            void Collect(Transform? t)
            {
                if (t == null) return;
                foreach (var sr in t.GetComponentsInChildren<SpriteRenderer>(true))
                    if (sr != null && !all.Contains(sr)) all.Add(sr);
            }
            Collect(menu.transform);
            if (menu.mainMenuUI != null) Collect(menu.mainMenuUI.transform);

            var targets = all.Where(r => Array.IndexOf(VanillaBackgroundNames, r.name) >= 0).ToList();

            if (targets.Count == 0)
            {
                // 退路：铺满整个屏幕的大贴图基本就是背景
                float viewH = cam.orthographicSize * 2f;
                float viewW = viewH * cam.aspect;
                targets = all.Where(r =>
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.sprite == null) return false;
                    var n = r.name.ToLowerInvariant();
                    if (n.Contains("tint") || n.Contains("fade") || n.Contains("mask") || n.Contains("button")) return false;
                    var sz = r.bounds.size;
                    return sz.x >= viewW * 0.9f && sz.y >= viewH * 0.9f;
                }).ToList();
            }

            foreach (var r in targets)
            {
                if (r == null) continue;
                _hiddenRenderers.Add((r, r.enabled));
                r.enabled = false;
            }

            if (BackgroundStore.HideCrewmates)
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerParticles>())
                {
                    if (p == null || !p.gameObject.activeSelf) continue;
                    _hiddenObjects.Add((p.gameObject, true));
                    p.gameObject.SetActive(false);
                }
            }

            HideAmbienceDecor();

            LightLogger.Log($"[BackgroundRenderer] 已隐藏 {_hiddenRenderers.Count} 个原版背景渲染器、" +
                            $"{_hiddenObjects.Count} 个装饰物体");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[BackgroundRenderer.HideVanilla] {ex.Message}");
        }
    }

    /// <summary>
    /// 隐藏原版主界面背景上的**星星装饰**（用户说的 <c>Ambience.starfield</c>）。
    ///
    /// 旧的 <c>ImageGalleryPanel</c> 路径里本来有这么一段：
    /// <code>
    /// var ambience = FindGO("Ambience");
    /// ambience.transform.FindChild("PlayerParticles")?.gameObject.SetActive(false);
    /// if (ambience.transform.childCount &gt; 0) ambience.transform.GetChild(0).gameObject.SetActive(false);
    /// </code>
    /// 我重写成 BackgroundRenderer 时把它漏掉了 → 换上自定义背景后星星还浮在上面。
    ///
    /// 这里按**名字**找（"star"），不依赖子物体序号；顺便把 Ambience 的子物体名字
    /// 打一行日志，万一名字不叫 star* 也能从日志看出来。
    /// </summary>
    private static void HideAmbienceDecor()
    {
        try
        {
            var ambience = GameObject.Find("Ambience");
            if (ambience == null)
            {
                LightLogger.LogWarning("[BackgroundRenderer] 找不到 Ambience，跳过星星装饰清理");
                return;
            }

            var names = new System.Text.StringBuilder();
            int hidden = 0;
            for (int i = 0; i < ambience.transform.childCount; i++)
            {
                var child = ambience.transform.GetChild(i);
                if (child == null) continue;
                if (names.Length > 0) names.Append(", ");
                names.Append(child.name);

                bool isStar = child.name.IndexOf("star", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isStar) continue;
                if (!child.gameObject.activeSelf) continue;

                _hiddenObjects.Add((child.gameObject, true));
                child.gameObject.SetActive(false);
                hidden++;
            }

            LightLogger.Log($"[BackgroundRenderer] Ambience 子物体：{names}（已隐藏 {hidden} 个星名匹配项）");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[BackgroundRenderer.HideAmbienceDecor] {ex.Message}");
        }
    }

    private static void RestoreHidden()    {
        foreach (var (sr, was) in _hiddenRenderers)
        {
            try { if (sr != null) sr.enabled = was; } catch { }
        }
        _hiddenRenderers.Clear();

        foreach (var (go, was) in _hiddenObjects)
        {
            try { if (go != null) go.SetActive(was); } catch { }
        }
        _hiddenObjects.Clear();
    }

    private static float ReadMusicVolume()
    {
        try
        {
            return Mathf.Clamp01(AmongUs.Data.DataManager.Settings.Audio.MusicVolume);
        }
        catch (Exception ex)
        {
            // ⚠️⚠️ 读不到就按**满音量**处理，绝不能按 0 —— 0 就是静音。
            //   参考实现写的是 `catch { return 0f; }`，一旦这次读取失败，
            //   视频就**永远是哑的**，而且看不出任何报错（"视频能放但没声音"就是这么来的）。
            if (!_loggedVolumeFallback)
            {
                _loggedVolumeFallback = true;
                LightLogger.LogWarning(
                    $"[BackgroundRenderer] 读不到游戏音乐音量（{ex.Message}）→ 视频按 100% 音量播放");
            }
            return 1f;
        }
    }

    private static bool _loggedVolumeFallback;
    private static bool _loggedAudioDiag;

    /// <summary>
    /// 视频**实际**该用的音量。
    ///
    /// ⚠️ 参考实现是"永远跟随游戏「音乐」音量"，实测会踩坑：
    ///    这台机器 <c>musicVolume = 0.1378</c>（14%）→ 视频被压到几乎听不见，
    ///    用户看到的就是"视频在放，但没声音"。
    ///    所以改成 0~100 的整数百分比（<see cref="BackgroundStore.VideoVolumePercent"/>），**默认 100%**。
    /// </summary>
    private static float EffectiveVideoVolume()
    {
        return Mathf.Clamp01(BackgroundStore.VideoVolumePercent / 100f);
    }

    /// <summary>
    /// 重新确认视频音轨与音量。
    ///
    /// ⚠️ 必须在**视频 prepare 完成之后**再调一次：prepare 之前
    ///   <c>audioTrackCount</c> 还是 0，那时调 <c>EnableAudioTrack</c> 不一定会生效。
    /// </summary>
    private static void ApplyVideoAudio()
    {
        var v = _video;
        if (v == null) return;
        try
        {
            v.EnableAudioTrack(0, true);
            float vol = EffectiveVideoVolume();
            _lastVolume = vol;

            // ⚠️⚠️ **还没开播（正在 Prepare）就只记下目标音量，保持闭麦。**
            //    ApplyAudioOnly / ReapplyAudio 都会走到这里；这时若解除静音，
            //    未初始化的解码缓冲会被直接放出来 —— 就是用户听到的那声"炸音"。
            if (!_videoStarted)
            {
                try
                {
                    v.SetDirectAudioVolume(0, 0f);
                    v.SetDirectAudioMute(0, true);
                }
                catch { }
                return;
            }

            v.SetDirectAudioVolume(0, vol);
            v.SetDirectAudioMute(0, vol <= 0.001f);

            if (!_loggedAudioDiag)
            {
                _loggedAudioDiag = true;
                LightLogger.Log($"[BackgroundRenderer] 视频音频已就绪：音轨数={v.audioTrackCount} " +
                                $"输出模式={v.audioOutputMode} 实际音量={vol:F2} " +
                                $"模式={BackgroundStore.VideoVolumeLabel} " +
                                $"(音轨数 0 = 这个文件本来就没声音；" +
                                $"游戏音乐音量设置={SafeMusicSetting():F2})");
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[BackgroundRenderer.ApplyVideoAudio] {ex.Message}");
        }
    }

    private static float SafeMusicSetting()
    {
        try { return Mathf.Clamp01(AmongUs.Data.DataManager.Settings.Audio.MusicVolume); }
        catch { return -1f; }
    }

    /// <summary>设置里的音量模式改了 → 立刻重新应用到正在播的视频（不用重建）。</summary>
    public static void ReapplyAudio() => ApplyVideoAudio();

    /// <summary>视频音频是否可用（界面提示用）。</summary>
    public static int VideoAudioTrackCount
    {
        get { try { return _video?.audioTrackCount ?? 0; } catch { return 0; } }
    }

    /// <summary>当前视频实际音量（界面提示用）。</summary>
    public static float VideoVolume => _lastVolume < 0f ? 0f : _lastVolume;

    private static void MuteVanillaMusic()
    {
        try
        {
            var sm = SoundManager.Instance;
            if (sm == null) return;
            float unused = 0f;
            sm.SetChannelVolume(0f, ref unused, "MusicVolume");
            _vanillaMusicMuted = true;
        }
        catch { }
    }

    private static void RestoreVanillaMusic()
    {
        if (!_vanillaMusicMuted) return;
        _vanillaMusicMuted = false;
        try { SoundManager.Instance?.UpdateChannelVolumes(); } catch { }
    }

    // =====================================================================

    private static Camera? FindCamera(int layer)
    {
        int bit = 1 << layer;
        var main = Camera.main;
        if (main != null && (main.cullingMask & bit) != 0) return main;
        foreach (var c in Camera.allCameras)
            if (c != null && c.enabled && (c.cullingMask & bit) != 0) return c;
        return main;
    }

    /// <summary>诊断：把关键状态打一行（排查"看不见"用，AGENTS.md §4.8）。</summary>
    public static void LogDiagnostics()
    {
        try
        {
            var cam = _root != null ? FindCamera(_root.layer) : null;
            LightLogger.Log($"[BackgroundRenderer.Diag] root={(_root == null ? "null" : _root.name)} " +
                            $"layer={(_root == null ? -1 : _root.layer)} " +
                            $"cam={(cam == null ? "null" : cam.name)} " +
                            $"camOrtho={(cam == null ? false : cam.orthographic)} " +
                            $"ready={_mediaReady} media={_mediaW}x{_mediaH} fit={BackgroundStore.Fit} " +
                            $"dim={BackgroundStore.Dim} path='{System.IO.Path.GetFileName(_currentPath)}' " +
                            $"fail='{_lastFailReason}'");
        }
        catch { }
    }
}
