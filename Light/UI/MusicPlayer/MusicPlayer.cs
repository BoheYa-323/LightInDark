using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;   // WrapToIl2Cpp()：把托管 IEnumerator 交给 IL2CPP 的 StartCoroutine
using Il2CppInterop.Runtime.Injection;    // ClassInjector
using LightInDark.Core;
using UnityEngine;

namespace Light.UI.MusicPlayer;

/// <summary>
/// 循环模式。按钮上显示的**就是这四个字本身**，不加任何前缀（用户明确要求）。
///
/// 点击顺序（用户允许自定，这里定成）：
///     <c>列表循环 → 单曲循环 → 随机播放 → 列表循环 …</c>
/// </summary>
public enum MusicLoopMode
{
    /// <summary>列表循环：放完一首自动下一首，到末尾回到第一首。</summary>
    ListLoop = 0,

    /// <summary>单曲循环：靠 <c>AudioSource.loop = true</c> 原地重播。</summary>
    SingleLoop = 1,

    /// <summary>随机播放：放完一首随机挑一首（不会立刻重复同一首）。</summary>
    Shuffle = 2,
}

/// <summary>
/// **音乐播放器核心** —— 一个常驻（<c>DontDestroyOnLoad</c>）的 MonoBehaviour。
///
/// 它负责：
///   · 自己的 <see cref="AudioSource"/>（**不用 SoundManager**，那是游戏音效系统，
///     会被 <c>StopAllSound</c> / 场景切换清掉）
///   · 加载协程调度（解码在后台线程，<c>AudioClip.Create</c> 在主线程）
///   · 防爆音的"先静音 → Play → 逐帧淡入"
///   · 播放 / 暂停 / 上一首 / 下一首 / 循环模式
///
/// 窗口（<see cref="MusicPlayerWindow"/>）持有一个静态引用，按钮回调直接调这里的方法；
/// 每帧的 F3 轮询和音频状态推进都在 <see cref="Update"/> 里，**都包了 try/catch**。
///
/// ⚠️ 这里**没有任何 Harmony 补丁** —— 全部靠 Update 轮询解决，不碰补丁就不影响别的系统。
/// </summary>
public sealed class MusicPlayer : MonoBehaviour
{
    /// <summary>默认音量（0~1）。</summary>
    public const float DefaultVolume = 0.7f;

    /// <summary>
    /// 淡入帧数。8 帧 ≈ 0.13 秒（60fps），与
    /// <c>Light\UI\MainMenu\BackgroundRenderer.cs</c> 里视频音轨用的 <c>AudioFadeFrames</c> 保持一致。
    /// </summary>
    private const int FadeFrames = 8;

    // ===== 单例（懒加载 + 幂等）=====
    private static MusicPlayer? _instance;
    private static GameObject? _host;

    /// <summary>当前实例（没初始化过就是 null，调用方自己判空）。</summary>
    public static MusicPlayer? Instance => _instance;

    // ===== 运行时状态 =====
    private AudioSource? _source;

    /// <summary>
    /// BASS 后端（流式播放）。**非 null 时优先用它**,原来那套
    /// <c>AudioSource</c> + 整段 <c>AudioClip</c> 解码作为回退。
    ///
    /// ⚠️ 为什么要有回退:BASS 依赖原生 <c>bass.dll</c>,而那条链路上任何一环
    ///    （嵌入资源缺失 / 释放失败 / <c>Bass.Init</c> 失败 / 版本不匹配）出问题,
    ///    整个播放器就哑了。wav 那条纯托管自解码路径**不依赖任何原生库**,
    ///    是最后的保命线 —— 所以这里判活失败时静默退回原路径,不报错、不打扰用户。
    ///
    /// ⚠️ **每次读都判活**,不要在字段里缓存:`BassMusicPlayer` 的宿主是
    ///    <c>DontDestroyOnLoad</c> 的,但初始化是异步完成的,启动早期它还没 ready。
    /// </summary>
    private static Light.Audio.BassMusicPlayer? Bass
    {
        get
        {
            try
            {
                var b = Light.Audio.BassMusicPlayer.Instance;
                if (b == null)
                {
                    LogBassUnavailableOnce("BassMusicPlayer.Instance == null（宿主没建起来 —— 看有没有 'BASS 已就绪' 那行）");
                    return null;
                }
                if (!b.IsReady)
                {
                    LogBassUnavailableOnce($"宿主在但 IsReady=false（LastError={b.LastError}）");
                    return null;
                }
                return b;
            }
            catch (Exception ex)
            {
                LogBassUnavailableOnce($"读取 BassMusicPlayer 抛异常: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    private static bool _loggedBassUnavailable;

    /// <summary>
    /// 只打一次的"BASS 为什么用不了"诊断。
    ///
    /// ⚠️ 加它的原因(2026-10-06):日志里明明有 `[BassMusicPlayer] BASS 已就绪（版本 2.4.18.3）`,
    ///    但播放走的一直是 AudioClip 路径的日志 —— 说明 <see cref="Bass"/> 返回了 null。
    ///    这个属性原来是**静默** catch 的,失败时没有任何痕迹,只能靠猜。
    ///    打一次就够,不会刷屏。
    /// </summary>
    private static void LogBassUnavailableOnce(string reason)
    {
        if (_loggedBassUnavailable) return;
        _loggedBassUnavailable = true;
        LightLogger.LogWarning($"[MusicPlayer] BASS 后端不可用，已回退 AudioClip 路径：{reason}");
    }

    /// <summary>音频源所在的常驻物体（<c>DontDestroyOnLoad</c>）。</summary>
    private GameObject? _audioGo;

    private readonly MusicLibrary _library = new();

    /// <summary>已解码的 clip 缓存，key = 文件完整路径（刷新列表后下标会变，路径不会）。</summary>
    private readonly Dictionary<string, AudioClip> _clips = new(StringComparer.OrdinalIgnoreCase);

    private int _currentIndex = -1;
    private int _pendingIndex = -1;
    private int _lastRandom = -1;

    private Coroutine? _loadCo;
    private Coroutine? _preloadCo;

    private bool _playing;
    private bool _paused;
    private bool _muted;

    /// <summary>正在解码（UI 用它显示"加载中"）。</summary>
    private bool _loading;

    /// <summary>
    /// 本次切歌是否以"加载失败"告终。用来**阻止自动往下跳**：
    /// 列表里连着几个坏文件时，自动跳会一首接一首地解码，白占主线程。
    /// 用户重新点任意一首（<see cref="PlayAt"/>）会清掉它。
    /// </summary>
    private bool _failedThisStep;

    private float _targetVolume = DefaultVolume;
    private int _fadeLeft;
    private bool _fadeIn;

    private float _cooldownUntil;
    private float _lastLoadFailAt = -99f;

    private MusicLoopMode _loopMode = MusicLoopMode.ListLoop;

    /// <summary>最近一次失败原因（UI 显示用）。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>加载失败后，同一个文件 1.5 秒内不重复解码（防连点把主线程拖住）。</summary>
    private const float LoadFailCooldown = 1.5f;

    // =====================================================================
    //  只读状态（给 UI 用）
    // =====================================================================

    public MusicLibrary Library => _library;
    public MusicLoopMode LoopMode => _loopMode;

    /// <summary>循环模式的中文名 —— 就这四个字。</summary>
    public static string LoopModeLabel(MusicLoopMode mode)
    {
        try
        {
            switch (mode)
            {
                case MusicLoopMode.SingleLoop: return "单曲循环";
                case MusicLoopMode.Shuffle: return "随机播放";
                default: return "列表循环";
            }
        }
        catch { return "列表循环"; }
    }

    public bool IsPlaying => _playing && !_paused;
    public bool IsPaused => _paused;
    public bool IsLoading => _loading;
    public bool IsMuted => _muted;
    public float Volume => _targetVolume;

    /// <summary>
    /// **当前播放位置（秒）**。没挂 clip / 没在播 → 0。
    ///
    /// 2026-10-05 新增（窗口右上角要显示 `1:00 / 2:30` 的进度，原来只读状态里没有这一项）。
    /// ⚠️ <c>AudioSource.time</c> 对 <c>stream: false</c>（整段解码进内存）的 clip **同样有效**
    ///    —— 工程里 <c>BackgroundRenderer</c> 就是用它做视频续播 seek 的。
    ///    暂停时它停在暂停那一刻，正是我们想显示的读数。
    /// </summary>
    public float Position
    {
        get
        {
            try
            {
                // BASS 路径：它原生就有时间轴，不用我们自己算。
                // ⚠️ **必须要求"BASS 正在驱动"**（IsPlaying 或 IsPaused）——
                //    宿主 ready 不等于"当前这一首是 BASS 在放"。播放路径还没切过来时，
                //    只要宿主 ready 就读 BASS 会拿到 0 → 进度条一直是 0:00（回归）。
                var b = Bass;
                if (b != null && (b.IsPlaying || b.IsPaused))
                {
                    double bt = b.Position;
                    return (double.IsNaN(bt) || bt < 0d) ? 0f : (float)bt;
                }

                if (_source == null || _source.clip == null) return 0f;
                float t = _source.time;
                return (float.IsNaN(t) || t < 0f) ? 0f : t;
            }
            catch { return 0f; }
        }
    }

    /// <summary>
    /// **当前曲目总时长（秒）**。没挂 clip → 0。2026-10-05 新增（同上，进度显示用）。
    /// </summary>
    public float Duration
    {
        get
        {
            try
            {
                // BASS 路径：流式播放时 clip.length 是不存在的，只能问 BASS。
                // ⚠️ 同 Position：必须"BASS 正在驱动"才读，否则会盖掉 NAudio 路径的真实时长。
                var b = Bass;
                if (b != null && (b.IsPlaying || b.IsPaused))
                {
                    double bd = b.Duration;
                    return (double.IsNaN(bd) || bd < 0d) ? 0f : (float)bd;
                }

                if (_source == null || _source.clip == null) return 0f;
                float len = _source.clip.length;
                return (float.IsNaN(len) || len < 0f) ? 0f : len;
            }
            catch { return 0f; }
        }
    }

    /// <summary>当前曲目（没有就 null）。</summary>
    public MusicTrack? CurrentTrack
    {
        get
        {
            try { return _currentIndex < 0 ? null : _library.GetAt(_currentIndex); }
            catch { return null; }
        }
    }

    /// <summary>
    /// 正在播/刚选中的曲目名字。给 UI 显示"正在播放：xxx"用。
    /// 没在播时返回空串（UI 自己决定显示什么）。
    /// </summary>
    public string NowPlayingName
    {
        get
        {
            try
            {
                var t = CurrentTrack;
                return t == null ? string.Empty : t.DisplayName;
            }
            catch { return string.Empty; }
        }
    }

    // =====================================================================
    //  初始化
    // =====================================================================

    /// <summary>
    /// 幂等初始化：重复调用**不会**创建第二份。
    ///
    /// ⚠️ 为什么要独立成一个 GameObject 并 <c>DontDestroyOnLoad</c>：
    ///    切场景（主菜单 ↔ 大厅 ↔ 游戏内）时，场景里的物体全被销毁，
    ///    普通的 AudioSource 会在切场景那一刻被连带销毁 → 音乐**直接断掉**。
    ///    打了 DontDestroyOnLoad 之后音频源活在"DontDestroyOnLoad 场景"里，
    ///    切场景对它没有任何影响 —— 这是"切场景不断音"的唯一正解。
    /// </summary>
    public static void EnsureInitialized()
    {
        try
        {
            if (_instance != null && _host != null) return;

            // 类型必须先注册进 IL2CPP，否则 AddComponent 会抛
            try { ClassInjector.RegisterTypeInIl2Cpp<MusicPlayer>(); }
            catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayer] 注册类型失败（可能已注册）: {ex.Message}"); }

            var go = new GameObject("LID_MusicPlayer");
            go.hideFlags = HideFlags.HideAndDontSave;   // 不出现在 Hierarchy 里，也不会被场景保存
            UnityEngine.Object.DontDestroyOnLoad(go);

            _host = go;
            _instance = go.AddComponent<MusicPlayer>();
            _instance?.Initialize();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.EnsureInitialized]", ex);
        }
    }

    /// <summary>初始化（只跑一次）。</summary>
    private void Initialize()
    {
        try
        {
            // AudioSource 的配置全部按"把音质风险降到最低"来：
            //   playOnAwake = false  —— 我们自己控制什么时候 Play（见防爆音）
            //   loop        = false  —— 循环由我们自己管（列表循环/随机都要在 Update 里检测结束）
            //   spatialBlend= 0      —— 纯 2D，不受位置/朝向影响，切场景不会忽然变远
            //   priority    = 64     —— 比游戏音效高（0 最高），别被 SFX 挤掉
            //   dopplerLevel= 0 / bypassReverbZones=true —— 关掉一切会"改造"这段音频的效果
            var go = new GameObject("LID_MusicSource");
            go.transform.SetParent(_host != null ? _host.transform : null, false);
            _audioGo = go;

            _source = go.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f;
            _source.volume = 0f;          // ⚠️ 一开始就是 0，等真正 Play 时再淡入
            _source.mute = true;
            _source.priority = 64;
            _source.dopplerLevel = 0f;
            _source.bypassReverbZones = true;
            _source.bypassEffects = true;
            _source.bypassListenerEffects = true;   // ⚠️ 不被 AudioListener 的音量/低通滤镜影响，避免"糊/炸"
            _source.playOnAwake = false;

            // ⚠️ BASS 播完的自动下一首**必须靠这个事件**：
            //    `TickPlayback` 里判"是否播完"用的是 `_source.isPlaying` / `_source.time`，
            //    而 BASS 路径下 `_source.clip` 是 null → 那一句 `if (_source.clip == null) return;`
            //    会直接返回，**永远判不到结束** → 列表循环/随机播放会卡在最后一首不动。
            //    BASS 自己知道什么时候放完，所以订阅它的 MediaEnded。
            try
            {
                var bassHost = Light.Audio.BassMusicPlayer.Instance;
                if (bassHost != null)
                {
                    bassHost.MediaEnded -= OnBassMediaEnded;   // 防重复订阅
                    bassHost.MediaEnded += OnBassMediaEnded;
                    LightLogger.Log("[MusicPlayer] 已订阅 BASS 播放结束事件（自动下一首）");
                }
            }
            catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayer] 订阅 BASS 事件失败: {ex.Message}"); }

            _library.Reload();

            LightLogger.Log($"[MusicPlayer] 已初始化（音量 {_targetVolume * 100f:F0}%，" +
                            $"循环模式 {LoopModeLabel(_loopMode)}，曲库 {_library.Count} 首，目录 {MusicLibrary.MusicDir}）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.Initialize]", ex);
        }
    }

    // =====================================================================
    //  对外操作（全部被 UI 的按钮调用；每个方法体都包 try/catch）
    // =====================================================================

    /// <summary>播放 / 暂停切换（UI 上那个 ▶ / ❚❚ 按钮）。</summary>
    public void TogglePlayPause()
    {
        try
        {
            if (_library.Count == 0)
            {
                LastError = "Music 文件夹里没有歌曲";
                return;
            }

            // 暂停中 → 继续
            if (_paused && _playing)
            {
                Resume();
                return;
            }

            // 正在播 → 暂停
            // ⚠️ BASS 路径下 `_source.isPlaying` 永远是 false（音乐不走 AudioSource），
            //    只判它会导致"按一下不暂停"。所以补上"BASS 后端在驱动"这个条件。
            if (_playing && ((_source != null && _source.isPlaying) || Bass != null))
            {
                Pause();
                return;
            }

            // 其它情况（从没播过 / 播完了）→ 从当前选中（或第一首）开始
            int idx = _currentIndex >= 0 ? _currentIndex : 0;
            PlayAt(idx, idx == _currentIndex && _playing);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.TogglePlayPause]", ex);
        }
    }

    /// <summary>暂停。用 <c>Pause()</c>（淡出后暂停）而不是 Stop+重载，切回来不会重头解码。</summary>
    public void Pause()
    {
        try
        {
            if (_source == null) return;

            // ⚠️ BASS 路径要放在 `if (_source == null) return;` **之后但早于所有 _source 操作**：
            //    BASS 播放时我们没给 _source 挂 clip，但 _source 对象本身是在的
            //    （Initialize 里建的），所以能走到这里。真正要避开的是下面那串
            //    对 _source 的音量/Pause 操作 —— 它们对 BASS 毫无作用。
            var bass = Bass;
            if (bass != null && (bass.IsPlaying || bass.IsPaused))
            {
                try { bass.Pause(); } catch { }
                _fadeIn = false;
                _fadeLeft = 0;
                _paused = true;
                LightLogger.Log($"[MusicPlayer] 已暂停（BASS）：{NowPlayingName}");
                return;
            }

            try { _source.volume = 0f; _source.mute = true; } catch { }
            try { _source.Pause(); } catch { }

            _fadeIn = false;
            _fadeLeft = 0;
            _paused = true;

            LightLogger.Log($"[MusicPlayer] 已暂停：{NowPlayingName}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.Pause]", ex);
        }
    }

    /// <summary>继续播放：**同样先静音再 UnPause，然后逐帧淡入** —— 暂停恢复也可能有咔哒声。</summary>
    public void Resume()
    {
        try
        {
            // ⚠️ BASS 路径优先：它内部已有"先归零再 Resume 再淡入"的防爆音处理，
            //    不能落到下面那串对 _source 的操作（那些对 BASS 无效）。
            var bass = Bass;
            if (bass != null && bass.IsPaused)
            {
                try { bass.SetLoop(_loopMode == MusicLoopMode.SingleLoop); } catch { }
                try { bass.SetVolume(_muted ? 0f : _targetVolume); } catch { }
                try { bass.Resume(); } catch { }
                _paused = false;
                _playing = true;
                LightLogger.Log($"[MusicPlayer] 已继续（BASS）：{NowPlayingName}");
                return;
            }

            if (_source == null || _source.clip == null)
            {
                // 还没加载过任何东西 → 当成"从头播"
                int idx = _currentIndex >= 0 ? _currentIndex : 0;
                PlayAt(idx, false);
                return;
            }

            try { _source.volume = 0f; _source.mute = true; } catch { }
            try { _source.UnPause(); } catch { }

            _paused = false;
            _playing = true;
            StartFadeIn();

            LightLogger.Log($"[MusicPlayer] 已继续：{NowPlayingName}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.Resume]", ex);
        }
    }

    /// <summary>上一首（direction = -1）。</summary>
    public void Prev() => Step(-1);

    /// <summary>下一首（direction = +1）。</summary>
    public void Next() => Step(1);

    /// <summary>
    /// 上/下一首。
    /// 时间窗（0.6 秒）是为了防连点：每次切歌都要解码，连点会排一长串后台任务。
    /// </summary>
    private void Step(int direction)
    {
        try
        {
            if (_library.Count == 0)
            {
                LastError = "Music 文件夹里没有歌曲";
                return;
            }

            float now = Time.unscaledTime;
            if (now < _cooldownUntil) return;
            _cooldownUntil = now + 0.6f;

            int target = _currentIndex < 0
                ? (direction < 0 ? _library.Count - 1 : 0)
                : NextIndex(direction, false);

            PlayAt(target, true);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.Step]", ex);
        }
    }

    /// <summary>循环模式按钮：<c>列表循环 → 单曲循环 → 随机播放 → 列表循环</c>。</summary>
    public void CycleLoopMode()
    {
        try
        {
            int next = ((int)_loopMode + 1) % 3;
            _loopMode = (MusicLoopMode)next;
            ApplyLoopMode();

            LightLogger.Log($"[MusicPlayer] 循环模式 → {LoopModeLabel(_loopMode)}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.CycleLoopMode]", ex);
        }
    }

    /// <summary>
    /// 把循环模式落到 <see cref="AudioSource.loop"/>：
    ///   · 单曲循环 = <c>loop = true</c>（原地无缝重播，最省事也最稳）
    ///   · 列表循环 / 随机播放 = <c>loop = false</c>，由 <see cref="TickPlayback"/> 在
    ///     <c>!isPlaying</c>（或播到尾巴）时切下一首
    /// </summary>
    private void ApplyLoopMode()
    {
        try
        {
            // BASS 路径：它的 Loop 是流式的，单曲循环同样无缝
            Bass?.SetLoop(_loopMode == MusicLoopMode.SingleLoop);

            if (_source == null) return;
            _source.loop = _loopMode == MusicLoopMode.SingleLoop;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayer.ApplyLoopMode] {ex.Message}");
        }
    }

    /// <summary>静音开关（预留；UI 暂时没放这个按钮，但状态是完整的）。</summary>
    public void ToggleMute()
    {
        try
        {
            _muted = !_muted;

            // BASS 路径：它没有 mute 属性，用音量 0 表达（和 AudioSource.mute 等效）
            Bass?.SetVolume(_muted ? 0f : _targetVolume);

            if (_source != null) _source.mute = _muted || _fadeLeft > 0;
            LightLogger.Log($"[MusicPlayer] 静音 = {_muted}");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.ToggleMute]", ex);
        }
    }

    /// <summary>
    /// **设置音量（0~1）** —— 音乐播放器窗口那条音量滑条（拖动 / `-` `+` 步进）唯一的写入口。
    ///
    /// 2026-10-05 新增。原来这个类只有**只读**的 <see cref="Volume"/> 和 <see cref="ToggleMute"/>，
    /// UI 拿不到任何写入口，所以要加音量滑条就必须补一个 —— 这里取**最小改动**：
    /// 只加这一个方法，其它逻辑一行没动。
    ///
    /// ⚠️ 它**只写 <see cref="_targetVolume"/>**，故意不直接写 <c>AudioSource.volume</c>：
    ///    · 真正的落地在 <see cref="TickFade"/> —— 它每帧把 <c>source.volume</c> 推向目标值，
    ///      所以这里改完**最多一帧**就生效；
    ///    · 直接写 <c>source.volume</c> 会被下一帧的 TickFade 覆盖回去（白写），
    ///      而且会绕过 <see cref="_muted"/> 和"淡入"两条既有路径 —— 那两条正是**防爆音**的关键。
    ///
    /// ⚠️ 这里**故意不打日志**：拖动时每帧都会被调用，打日志会把 LightLog 刷爆。
    ///    需要日志的地方（窗口的 ± 按钮 / 拖动松手）自己打。
    /// </summary>
    public void SetVolume(float value)
    {
        try
        {
            _targetVolume = Mathf.Clamp01(value);

            // ⚠️ BASS 路径**必须在这里立刻落地**，不能像 AudioSource 那样只写目标值等 TickFade。
            //    原因：BASS 的淡入/音量是完全独立的一套（在 BassMusicPlayer 内部），
            //    TickFade 只推 `_source.volume`，对 BASS 一点作用都没有 —— 只写目标值的话
            //    拖动滑条会"只动数字不出声"。
            //    同理 `_muted` 也要一起考虑，否则静音状态下拖音量会把声音放出来。
            var bass = Bass;
            if (bass != null) bass.SetVolume(_muted ? 0f : _targetVolume);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.SetVolume]", ex);
        }
    }

    /// <summary>
    /// 上一页（UI 按钮 / 滚轮）。**只动分页，不动音频** —— 翻页绝不该影响正在放的歌。
    /// </summary>
    public bool PrevPage()
    {
        try { return _library.PrevPage(); }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayer.PrevPage] {ex.Message}");
            return false;
        }
    }

    /// <summary>下一页（同上）。</summary>
    public bool NextPage()
    {
        try { return _library.NextPage(); }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayer.NextPage] {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 播放列表里第 <paramref name="index"/> 首（全局下标，来自 UI 的点击）。
    /// 越界自动夹紧；点的是当前这首时相当于"从头播"。
    /// </summary>
    public void PlayAt(int index, bool restartIfSame = true)
    {
        try
        {
            if (_library.Count == 0)
            {
                LastError = "Music 文件夹里没有歌曲";
                return;
            }

            index = Mathf.Clamp(index, 0, _library.Count - 1);

            if (index == _currentIndex && !restartIfSame && (_playing || _paused))
                return;   // 同一首又在播 → 什么都不做

            // 同一首刚失败过 → 冷却期内不再解一次（连点坏文件不会反复占用主线程）
            if (index == _currentIndex && !string.IsNullOrEmpty(LastError))
            {
                if (Time.unscaledTime - _lastLoadFailAt < LoadFailCooldown) return;
            }

            _currentIndex = index;
            _pendingIndex = index;
            _paused = false;
            LastError = string.Empty;
            _failedThisStep = false;

            RestartLoad();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.PlayAt]", ex);
        }
    }

    /// <summary>
    /// 重新扫描目录（UI 上的「刷新列表」）。
    ///
    /// ⚠️ **不打断正在播的歌** —— 只重建列表、把当前播的这首在新列表里重新定位，
    ///    这样用户一边听一边整理文件夹也不会"刷新一下就断音"。
    /// </summary>
    public void ReloadLibrary()
    {
        try
        {
            var playingPath = CurrentTrack?.FullPath;

            _library.Reload();

            if (!string.IsNullOrEmpty(playingPath))
            {
                int newIndex = -1;
                for (int i = 0; i < _library.Count; i++)
                {
                    if (string.Equals(_library.GetAt(i)?.FullPath, playingPath, StringComparison.OrdinalIgnoreCase))
                    {
                        newIndex = i;
                        break;
                    }
                }
                _currentIndex = newIndex;   // 找不到就是 -1（文件被删了），音频继续放完当前这首
            }
            else
            {
                _currentIndex = -1;
            }

            if (_currentIndex < 0 && _library.Count > 0) _currentIndex = 0;
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.ReloadLibrary]", ex);
        }
    }

    /// <summary>
    /// BASS 路径的加载 + 播放。**不解码**,直接把文件交给 BASS 流式播。
    ///
    /// 与 AudioClip 路径的分工：
    ///   · 静音/淡入/换曲防爆音 → 全在 <see cref="Light.Audio.BassMusicPlayer"/> 内部做
    ///     （它也是"先归零再操作"，和这里原来那套是同一个思路，不用重复一遍）
    ///   · 这里只负责**状态机**：`_loading` / `_playing` / `_paused` / `_currentIndex` / `LastError`
    ///     以及失败时的处理
    /// </summary>
    private IEnumerator CoLoadAndPlayBass(Light.Audio.BassMusicPlayer bass, int index)
    {
        try
        {
            var lib = _library;
            var track = lib.GetAt(index);
            if (track == null)
            {
                _loading = false;
                yield break;
            }

            _currentIndex = index;
            _loading = true;
            _failedThisStep = false;
            LastError = string.Empty;

            bool done = false;
            bool ok = false;

            try
            {
                bass.SetLoop(_loopMode == MusicLoopMode.SingleLoop);
                bass.LoadAndPlay(track.FullPath, _loopMode == MusicLoopMode.SingleLoop,
                    _muted ? 0f : _targetVolume,
                    r => { ok = r; done = true; });
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[MusicPlayer.CoLoadAndPlayBass]", ex);
                done = true; ok = false;
            }

            // 等回调（BASS 的 LoadAsync 是异步的；用轮询而不是 await，避免同步上下文死锁）
            while (!done) yield return null;

            if (!ok)
            {
                _loading = false;
                _playing = false;
                _paused = false;
                _failedThisStep = true;
                LastError = bass.LastError;
                LightLogger.LogWarning($"[MusicPlayer] BASS 播放失败：{track.DisplayName}（{LastError}）");
                yield break;
            }

            _playing = true;
            _paused = false;
            _fadeIn = false;      // 淡入由 BassMusicPlayer 负责，这里的淡入标记保持关
            _fadeLeft = 0;
            _loading = false;

            LightLogger.Log($"[MusicPlayer] 开始播放（BASS 流式）#{index + 1} {track.DisplayName}");
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// BASS 播完一首 → 按循环模式决定下一步。
    ///
    /// 语义要和 `TickPlayback` 那条老路径**完全一致**（那边是 AudioClip 路径的判断）：
    ///   · 单曲循环 → 不处理（BASS 的 `Loop = true` 会自己原样重播，天然无缝）
    ///   · 否则取下一首；取不到就停
    /// </summary>
    private void OnBassMediaEnded()
    {
        try
        {
            if (_loopMode == MusicLoopMode.SingleLoop) return;   // 交给 BASS 的 Loop

            // ⚠️⚠️ **防抖 + 状态守卫**（2026-10-06，用户报"切歌抽搐、只在非单曲循环下发生"）
            //
            //   日志里每 0.5~1 秒 #1/#2 互相切换 —— 因为 **LoadAsync 换曲时会为上一首抛 MediaEnded**，
            //   我们这个处理器把它当成"这一首播完了" → RestartLoad() → 又抛 → **死循环**。
            //   而单曲循环下第一句就 return 了，所以只有列表循环/随机播放会犯 ✓ 正是用户描述的现象。
            //
            //   三道守卫：
            //     ① 正在加载中 → 忽略（换曲过程中抛出来的都是噪声）
            //     ② 暂停中 → 忽略
            //     ③ 距上次开始加载不到 1 秒 → 忽略（BASS 的 MediaEnded 可能在 LoadAsync 时立刻冒出来，
            //        而一首歌不可能 1 秒内放完）
            if (_loading) return;
            if (_paused) return;
            if (UnityEngine.Time.realtimeSinceStartup - _lastLoadStartTime < 1.0f) return;

            if (_library.Count == 0) { _playing = false; return; }

            int next = NextIndex(1, false);
            if (next < 0)
            {
                _playing = false;
                LightLogger.Log("[MusicPlayer] BASS 播放结束，没有下一首了");
                return;
            }

            LightLogger.Log($"[MusicPlayer] 本曲播完 → 下一首 #{next + 1}（{LoopModeLabel(_loopMode)}）");
            _pendingIndex = next;
            RestartLoad();
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayer.OnBassMediaEnded] {ex.Message}");
        }
    }

    /// <summary>最近一次开始加载的时刻（<c>Time.realtimeSinceStartup</c>），用于 MediaEnded 防抖。</summary>
    private float _lastLoadStartTime = -999f;

    /// <summary>切到上下首（或首播）时重启加载协程。</summary>
    private void RestartLoad()
    {
        try
        {
            // ⚠️ 防抖用的时间戳 + 先置 _loading：OnBassMediaEnded 靠这两个忽略
            //    "换曲瞬间冒出来的 MediaEnded"（不加就是 #1/#2 每秒互切 = 抽搐）。
            //    必须在起协程**之前**做，否则协程第一帧才置位，时间戳反而落后。
            _lastLoadStartTime = UnityEngine.Time.realtimeSinceStartup;
            _loading = true;

            if (_loadCo != null)
            {
                try { StopCoroutine(_loadCo); } catch { }
                _loadCo = null;
            }
            _loadCo = StartCoroutine(CoLoadAndPlay().WrapToIl2Cpp());
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer.RestartLoad]", ex);
        }
    }

    // =====================================================================
    //  加载 + 播放
    // =====================================================================

    /// <summary>
    /// 加载当前 <see cref="_pendingIndex"/> 并开播。
    ///
    /// 顺序（**防爆音的关键**）：
    ///   ① 立刻把音量设 0 + mute，并 <c>Stop()</c> 掉上一首（避免两个声音重叠/尾音被截断成"啪"）
    ///   ② 缓存里有这首 → 直接用（换歌 <b>零解码延迟</b>，不会"卡一下再响"）
    ///   ③ 缓存里没有 → 交给 <see cref="MusicClipLoader"/> 后台解码；**加载期间保持静音**
    ///   ④ <c>Play()</c> 之后由 <see cref="TickFade"/> 逐帧把音量 0 → 目标值抬上来
    /// </summary>
    private IEnumerator CoLoadAndPlay()
    {
        int index = _pendingIndex;

        // ①' **BASS 路径：流式加载，完全跳过 AudioClip 解码。**
        //
        //   原来那条路要把整首歌展开成 float[] 全塞进内存（`AudioClip.Create(..., stream:false)`
        //   + `SetData`）—— 一首无损几百 MB。那既是用户报的"NAudio 容易炸"的来源，
        //   也是大文件根本放不动的根因。BASS 是**流式**的，内存恒定。
        //
        //   ⚠️⚠️ **这一段必须放在"解码"之前**。放到后面（比如 `_source.clip = clip` 那一带）
        //      就等于还是先把整首解码了一遍再换后端 —— 白花时间白占内存，
        //      等于没换。这是这次改造的核心点。
        var bass = Bass;
        if (bass != null)
        {
            yield return CoLoadAndPlayBass(bass, index);
            yield break;
        }

        // ① 先安静下来
        try
        {
            if (_source != null)
            {
                _source.volume = 0f;
                _source.mute = true;
                _source.Stop();
            }
            _fadeIn = false;
            _fadeLeft = 0;
        }
        catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayer] 切换前静音失败: {ex.Message}"); }

        yield return null;   // 让上面的 Stop 真正生效一帧

        var track = _library.GetAt(index);
        if (track == null)
        {
            _loading = false;
            _playing = false;
            LastError = "曲目不存在（列表可能已被刷新）";
            yield break;
        }

        AudioClip? clip = null;

        // ② 缓存命中
        try
        {
            if (_clips.TryGetValue(track.FullPath, out var cached) && cached != null)
                clip = cached;
        }
        catch { }

        // ③ 缓存未命中 → 解码（失败原因要显示出来，但**不要**让 UI 崩）
        if (clip == null)
        {
            _loading = true;

            string fail = string.Empty;
            // ⚠️ 必须 .WrapToIl2Cpp()：加载器返回的是托管 IEnumerator，
            //    IL2CPP 的 StartCoroutine 只认 Il2CppSystem.Collections.IEnumerator。
            //    这里作为**子协程** yield，同样要包一层。
            yield return MusicClipLoader.CoLoadClip(track.FullPath, c => clip = c, r => fail = r).WrapToIl2Cpp();

            _loading = false;

            if (clip == null)
            {
                _playing = false;
                _failedThisStep = true;
                LastError = string.IsNullOrEmpty(fail) ? "未知原因" : fail;

                // 同一个文件短时间内重复失败就不再解码（防连点 / 防"列表里全是坏文件时空转"）
                _lastLoadFailAt = Time.unscaledTime;
                LightLogger.LogWarning($"[MusicPlayer] 播放失败 {track.DisplayName}: {LastError}");
                yield break;
            }

            try { _clips[track.FullPath] = clip; } catch { }
        }

        // 加载途中用户又点了别的 → 这次结果作废
        if (index != _pendingIndex)
        {
            _loading = false;
            yield break;
        }

        // ④ 挂上 clip 并开播（音量仍是 0）
        try
        {
            if (_source == null) { _loading = false; yield break; }

            _source.clip = clip;
            ApplyLoopMode();

            // ⚠️ 顺序不能反：先 mute=true + volume=0，**再** Play()。
            //    Play() 之后的头几帧，解码器/音频输出还在吐未初始化或未对齐的缓冲，
            //    那一刻的音量如果是目标值，就是用户听到的"啪/炸"。
            _source.mute = true;
            _source.volume = 0f;
            _source.time = 0f;
            _source.Play();

            _playing = true;
            _paused = false;
            StartFadeIn();

            LightLogger.Log($"[MusicPlayer] 开始播放 #{index + 1} {track.DisplayName}" +
                            $"（{(clip == null ? "?" : clip.length.ToString("F1"))}秒，" +
                            $"模式 {LoopModeLabel(_loopMode)}，{FadeFrames} 帧淡入）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[MusicPlayer] Play 失败", ex);
            _playing = false;
            LastError = $"播放失败: {ex.Message}";
            yield break;
        }

        _loadCo = null;

        // ⑤ 顺手预解码下一首（后台线程，不占主线程；只是让下次切歌更快）
        try { StartPreload(); } catch { }
    }

    /// <summary>
    /// 预解码"下一首"。
    /// 只在**没有正在跑的预解码**时启动；失败也无所谓（真的切到它时会再解一次）。
    /// </summary>
    private void StartPreload()
    {
        try
        {
            if (_preloadCo != null) return;
            if (_library.Count < 2) return;

            int idx = NextIndex(1, true);
            var track = _library.GetAt(idx);
            if (track == null) return;

            if (_clips.ContainsKey(track.FullPath)) return;

            _preloadCo = StartCoroutine(CoPreload(track).WrapToIl2Cpp());
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayer.StartPreload] {ex.Message}");
        }
    }

    private IEnumerator CoPreload(MusicTrack track)
    {
        AudioClip? clip = null;
        string fail = string.Empty;

        yield return MusicClipLoader.CoLoadClip(track.FullPath, c => clip = c, r => fail = r).WrapToIl2Cpp();

        try
        {
            if (clip != null && !_clips.ContainsKey(track.FullPath))
            {
                _clips[track.FullPath] = clip;
                LightLogger.Log($"[MusicPlayer] 已预解码下一首：{track.DisplayName}");
            }
            else if (clip == null)
            {
                LightLogger.LogWarning($"[MusicPlayer] 预解码失败 {track.DisplayName}: {fail}");
            }
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayer.CoPreload] {ex.Message}");
        }

        _preloadCo = null;
    }

    // =====================================================================
    //  防爆音：淡入
    // =====================================================================

    private void StartFadeIn()
    {
        _fadeIn = true;
        _fadeLeft = FadeFrames;
    }

    private float EffectiveVolume()
    {
        if (_muted) return 0f;

        // 淡入期间线性上升（0 → 目标值）
        if (_fadeIn && _fadeLeft > 0)
        {
            float progress = 1f - _fadeLeft / (float)FadeFrames;   // 0 → 1
            return Mathf.Clamp01(_targetVolume * progress);
        }

        return Mathf.Clamp01(_targetVolume);
    }

    /// <summary>
    /// 每帧推一次音量。**这就是防爆音的落点**：
    /// <c>Play()</c> 之后的头 8 帧，音量从 0 线性抬到目标值，
    /// 解码器吐脏样本的那一瞬间音量≈0 → 听不到爆音。
    /// </summary>
    private void TickFade()
    {
        try
        {
            if (_source == null) return;

            if (_fadeIn && _fadeLeft > 0)
            {
                _fadeLeft--;

                var v = EffectiveVolume();
                _source.volume = v;
                _source.mute = v <= 0.001f;      // 只有真的接近 0 才静音，避免"声音出不来"

                if (_fadeLeft == 0)
                {
                    _fadeIn = false;
                    _source.volume = Mathf.Clamp01(_targetVolume);
                    _source.mute = _muted;
                }
                return;
            }

            // 非淡入期：音量没被别的东西改过就不写（少写少出意外）
            var target = _muted ? 0f : Mathf.Clamp01(_targetVolume);
            if (_source.mute != _muted) _source.mute = _muted;
            if (!Mathf.Approximately(_source.volume, target) && !_muted)
                _source.volume = target;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayer.TickFade] {ex.Message}");
        }
    }

    // =====================================================================
    //  每帧
    // =====================================================================

    private void Update()
    {
        // 注意：Update 里**绝不**能抛异常 —— 那会中断 MonoBehaviour 的消息链。
        //       所以每一段都单独 try/catch。
        try { TickFade(); }
        catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayer.TickFade] {ex.Message}"); }

        try { TickPlayback(); }
        catch (Exception ex) { LightLogger.LogWarning($"[MusicPlayer.TickPlayback] {ex.Message}"); }
    }

    /// <summary>
    /// 检测"这首放完了"，然后按循环模式决定下一步。
    ///
    /// ⚠️ 为什么不用 <c>AudioSource.loop</c> 一把梭：
    ///    列表循环/随机播放都需要"放完 → 换 clip"，只能在 Update 里检测。
    ///    只有单曲循环交给 <c>loop = true</c>（它天然无缝，且不需要我们插手）。
    /// </summary>
    private void TickPlayback()
    {
        if (_source == null || !_playing) return;
        if (_paused) return;
        if (_loading) return;
        if (_source.clip == null) return;
        if (_loopMode == MusicLoopMode.SingleLoop) return;      // 交给 AudioSource.loop
        if (_fadeIn || _fadeLeft > 0) return;                   // 淡入还没完成，先别判"结束"

        // ⚠️ 上一首是"加载失败"结束的 → 别再自动往下跳。
        //    否则列表里连续几个坏文件时，会自动一首接一首地解码，白白占满主线程。
        //    用户重新点任意一首（PlayAt）会清掉这个标记。
        if (_failedThisStep) { _playing = false; return; }

        if (_library.Count == 0) { _playing = false; return; }

        float len = 0f;
        try { len = _source.clip.length; } catch { }

        bool ended;
        try
        {
            // 留 50ms 余量：直接等 isPlaying=false 会漏掉最后一小截，
            // 而用 time >= length 判定可以提前一点点接上，听感更连续。
            ended = !_source.isPlaying || (len > 0.2f && _source.time >= len - 0.05f);
        }
        catch { return; }

        if (!ended) return;

        int next = NextIndex(1, false);
        if (next < 0) { _playing = false; return; }

        PlayAt(next, true);
    }

    /// <summary>
    /// 算"下一首/上一首"的下标。
    ///
    /// · <paramref name="random"/>（预解码）+ 随机模式 → 随机挑一首
    /// · 列表末尾的下一首 → 回到 0；开头的上一首 → 回到末尾（列表循环）
    /// · <b>随机模式永远避开"刚放过的这一首"</b>，否则会连着放两遍同一首
    ///   （只有一首歌时允许重复）
    /// </summary>
    private int NextIndex(int direction, bool random)
    {
        try
        {
            int n = _library.Count;
            if (n <= 0) return -1;
            if (n == 1) return 0;

            if (_loopMode == MusicLoopMode.Shuffle)
            {
                int idx = UnityEngine.Random.Range(0, n);
                if (idx == _currentIndex && n > 1) idx = (idx + 1) % n;
                _lastRandom = idx;
                return idx;
            }

            int cur = _currentIndex < 0 ? (direction < 0 ? 0 : -1) : _currentIndex;
            int next = cur + direction;
            if (next >= n) next = 0;
            if (next < 0) next = n - 1;
            return next;
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[MusicPlayer.NextIndex] {ex.Message}");
            return -1;
        }
    }
}
