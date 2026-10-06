using System;
using System.Collections;
using System.Threading.Tasks;
using BepInEx.Unity.IL2CPP.Utils.Collections;   // WrapToIl2Cpp()
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using LightInDark.Core;
using ManagedBass;
using UnityEngine;

namespace Light.Audio
{
    /// <summary>
    /// BASS 播放器 —— 流式播放本地音频文件,替代原来的「NAudio 解码成 AudioClip + Unity AudioSource」。
    ///
    /// ---- 为什么要换 ----
    /// 用户实测 NAudio 那条路「容易炸」,而且它必须**把整首歌解码进内存**
    /// （<c>AudioClip.Create(..., stream:false)</c> + <c>SetData</c>）——
    /// 一首无损就是几百 MB,大文件直接爆内存。BASS 是**流式**的,内存恒定。
    ///
    /// ---- 来源与改动 ----
    /// 骨架来自参考实现（`D:\audios\AudioLoader.cs`, 作者 Oldsix / slok7565）,
    /// 但**去掉了它的宿主依赖**并补齐了我们需要的接口:
    ///   · 去掉 `LobbyBehaviour.Instance` 判断 —— 那是它自己模组的门控,
    ///     我们要在任何场景都能放（主菜单/大厅/游戏内）
    ///   · 去掉 `PDebug` / `Virial.Helpers` / `NebulaAPI` 依赖 → 改用 <see cref="LightLogger"/>
    ///   · 补上 暂停/继续、时间/时长、防爆音淡入、播放结束回调
    ///
    /// ---- ⚠️ 三个必须记住的坑 ----
    /// ① **`ClassInjector.RegisterTypeInIl2Cpp&lt;T&gt;()` 必须调**,否则
    ///    <c>AddComponent&lt;T&gt;()</c> 会抛 <c>TypeInitializationException</c>。
    ///    本工程在 <c>LightTicker</c> 上就漏过这一步,表现是"整个东西从来没被创建过"
    ///    而且被两层 catch 吃成一条不起眼的日志。参考实现是写在**静态构造函数**里的,
    ///    那样最稳（第一次碰到这个类型时自动注册）,这里照抄。
    /// ② **`MediaPlayer.Handle` 是 internal 的** —— 拿不到句柄就没法用
    ///    <c>Bass.ChannelGetInfo</c> 等底层 API,所以要用反射取（参考实现也是这么干的）。
    /// ③ **`bass.dll` 必须先落盘并加载**才能 <c>Bass.Init()</c> ——
    ///    见 <see cref="NativeLibraryLoader.PrepareBass"/>。顺序错了就是 <c>DllNotFoundException</c>。
    ///
    /// ⚠️ **未经实机验证**(2026-10-05)。
    /// </summary>
    public sealed class BassMusicPlayer : MonoBehaviour
    {
        // ------------------------------------------------------------------
        //  单例 / 宿主
        // ------------------------------------------------------------------

        private static BassMusicPlayer? _instance;

        public static BassMusicPlayer? Instance => _instance;

        /// <summary>
        /// ⚠️⚠️ **必须在静态构造函数里注册 IL2CPP 类型** —— 见类注释 ①。
        /// 静态构造函数在"第一次访问这个类型的任何成员"时执行,所以 <see cref="Ensure"/>
        /// 被调用的那一刻它一定已经跑过了。
        /// </summary>
        static BassMusicPlayer()
        {
            try { ClassInjector.RegisterTypeInIl2Cpp<BassMusicPlayer>(); }
            catch (Exception ex) { LightLogger.LogWarning($"[BassMusicPlayer] IL2CPP 类型注册失败: {ex.Message}"); }
        }

        /// <summary>创建常驻宿主（DontDestroyOnLoad → 切场景不断音）。幂等。</summary>
        public static void Ensure()
        {
            try
            {
                if (_instance != null) return;

                // 先准备好原生库 —— 必须在 Bass.Init() 之前
                if (!NativeLibraryLoader.PrepareBass())
                    LightLogger.LogWarning("[BassMusicPlayer] 原生库准备失败，BASS 可能初始化不起来（wav 路径不受影响）");

                var go = new GameObject("LID_BassMusicPlayer");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _instance = go.AddComponent<BassMusicPlayer>();
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[BassMusicPlayer.Ensure]", ex);
            }
        }

        // ------------------------------------------------------------------
        //  状态
        // ------------------------------------------------------------------

        private MediaPlayer? _player;
        private bool _bassReady;
        private int _requestId;             // 用于丢弃"过期的加载"（快速连点切歌）

        /// <summary>BASS 是否可用。false 时调用方应回退到别的播放路径。</summary>
        public bool IsReady => _bassReady && _player != null;

        /// <summary>最后一次失败原因（诊断用）。</summary>
        public string LastError { get; private set; } = "";

        // ---- 防爆音淡入（照搬工程里 BackgroundRenderer 的做法：绝不"一加载完就满音量"）----
        private const int FadeFrames = 8;
        private int _fadeLeft;
        private float _fadeTarget = 1f;

        /// <summary>目标音量 0~1（淡入会朝它逼近）。</summary>
        public float Volume { get; private set; } = 0.7f;

        /// <summary>当前是否在播放。</summary>
        public bool IsPlaying
        {
            get
            {
                try { return IsReady && _player!.State == PlaybackState.Playing; }
                catch { return false; }
            }
        }

        /// <summary>当前是否暂停。</summary>
        public bool IsPaused
        {
            get
            {
                try { return IsReady && _player!.State == PlaybackState.Paused; }
                catch { return false; }
            }
        }

        /// <summary>当前播放位置（秒）。</summary>
        public double Position
        {
            get
            {
                try { return IsReady ? _player!.Position.TotalSeconds : 0d; }
                catch { return 0d; }
            }
        }

        /// <summary>当前曲目总时长（秒）。</summary>
        public double Duration
        {
            get
            {
                try { return IsReady ? _player!.Duration.TotalSeconds : 0d; }
                catch { return 0d; }
            }
        }

        /// <summary>播放自然结束时触发（用于"列表循环/随机播放"的自动下一首）。</summary>
        public event Action? MediaEnded;

        // ------------------------------------------------------------------
        //  Unity 生命周期
        // ------------------------------------------------------------------

        private void Awake()
        {
            try
            {
                if (_instance != null && _instance != this) { Destroy(gameObject); return; }
                _instance = this;
                UnityEngine.Object.DontDestroyOnLoad(gameObject);

                // ---- 初始化 BASS ----
                // 注意 Bass.Init() 走 [DllImport("bass")] → 必须先 PrepareBass()（Ensure 里已做）
                if (!Bass.Init())
                {
                    LastError = $"Bass.Init 失败，Bass.LastError={Bass.LastError}";
                    LightLogger.LogError($"[BassMusicPlayer] {LastError}（依赖库在 {NativeLibraryLoader.LibrariesDir}）");
                    _bassReady = false;
                    return;
                }

                _player = new MediaPlayer();
                _player.MediaEnded += OnMediaEnded;
                _player.MediaFailed += OnMediaFailed;

                _bassReady = true;
                LightLogger.Log($"[BassMusicPlayer] BASS 已就绪（版本 {Bass.Version}），播放器宿主 = LID_BassMusicPlayer（DontDestroyOnLoad）");
            }
            catch (Exception ex)
            {
                _bassReady = false;
                LastError = ex.Message;
                LightLogger.LogError("[BassMusicPlayer.Awake]", ex);
            }
        }

        private void OnMediaEnded(object? sender, EventArgs e)
        {
            try
            {
                // 自动下一首由上层决定（列表循环 / 随机 / 单曲）；单曲循环由 Loop 属性自己处理，
                // 所以这里只在"非 Loop"时通知。
                bool loop = false;
                try { loop = _player != null && _player.Loop; } catch { }
                if (!loop) MediaEnded?.Invoke();
            }
            catch (Exception ex) { LightLogger.LogWarning($"[BassMusicPlayer.OnMediaEnded] {ex.Message}"); }
        }

        private void OnMediaFailed(object? sender, EventArgs e)
        {
            LightLogger.LogWarning($"[BassMusicPlayer] 媒体播放失败（Bass.LastError={Bass.LastError}）");
        }

        private void OnDestroy()
        {
            try
            {
                if (_instance == this) _instance = null;
                Stop();
                // ⚠️ 这里**不** Bass.Free()：宿主理论上会活到游戏退出，
                //    而 Free 之后再次 Init 有状态残留风险。真要释放交给进程退出。
            }
            catch { }
        }

        private void Update()
        {
            try
            {
                // 防爆音淡入：每帧把实际音量朝目标推
                if (_fadeLeft > 0 && IsReady)
                {
                    _fadeLeft--;
                    float t = 1f - (_fadeLeft / (float)FadeFrames);
                    float v = Mathf.Lerp(0f, _fadeTarget, Mathf.Clamp01(t));
                    try { _player!.Volume = v; } catch { }
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        //  播放控制
        // ------------------------------------------------------------------

        /// <summary>
        /// 加载并播放。<paramref name="onResult"/> 收到 true 表示成功。
        ///
        /// ⚠️ 用协程而不是 <c>await</c>：Unity 的同步上下文里 await Task 有死锁风险
        ///    （工程的 AudioLoader 参考实现也是 `while (!task.IsCompleted) yield return null`）。
        /// </summary>
        public void LoadAndPlay(string path, bool loop, float volume, Action<bool>? onResult = null)
        {
            try
            {
                if (!IsReady)
                {
                    LightLogger.LogWarning($"[BassMusicPlayer] 未就绪，无法播放 {System.IO.Path.GetFileName(path)}：{LastError}");
                    onResult?.Invoke(false);
                    return;
                }
                StartCoroutine(CoLoadAndPlay(path, loop, volume, onResult).WrapToIl2Cpp());
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[BassMusicPlayer.LoadAndPlay]", ex);
                onResult?.Invoke(false);
            }
        }

        private IEnumerator CoLoadAndPlay(string path, bool loop, float volume, Action<bool>? onResult)
        {
            int id = ++_requestId;

            // ---- 先静音再换曲：避免"上一首的尾巴"或"新曲第一帧"爆音 ----
            try { _player!.Volume = 0d; } catch { }

            Task<bool>? task = null;
            try { task = _player!.LoadAsync(path); }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[BassMusicPlayer] LoadAsync 抛异常 {System.IO.Path.GetFileName(path)}: {ex.Message}");
                onResult?.Invoke(false);
                yield break;
            }

            while (task != null && !task.IsCompleted) yield return null;

            // 期间又点了别的歌 → 这次结果作废
            if (id != _requestId) yield break;

            bool ok = false;
            try { ok = task != null && task.IsCompletedSuccessfully && task.Result; }
            catch (Exception ex) { LightLogger.LogWarning($"[BassMusicPlayer] LoadAsync 结果异常: {ex.Message}"); }

            if (!ok)
            {
                LastError = $"加载失败: {System.IO.Path.GetFileName(path)}（Bass.LastError={Bass.LastError}）";
                LightLogger.LogWarning($"[BassMusicPlayer] {LastError}");
                onResult?.Invoke(false);
                yield break;
            }

            try
            {
                // ---- 采样率对齐（参考实现的做法，避免变调/变速）----
                try
                {
                    int handle = GetHandle();
                    if (handle != 0 && Bass.ChannelGetInfo(handle, out var info))
                    {
                        if (!Mathf.Approximately((float)info.Frequency, (float)_player!.Frequency))
                            _player.Frequency = info.Frequency;
                    }
                }
                catch (Exception ex) { LightLogger.LogWarning($"[BassMusicPlayer] 采样率对齐失败(忽略): {ex.Message}"); }

                _player!.Loop = loop;
                Volume = Mathf.Clamp01(volume);
                _fadeTarget = Volume;
                _fadeLeft = FadeFrames;          // ← 从这里开始淡入（Update 里推）

                _player.Play();
                onResult?.Invoke(true);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LightLogger.LogError("[BassMusicPlayer.CoLoadAndPlay:Play]", ex);
                onResult?.Invoke(false);
            }
        }

        /// <summary>暂停。⚠️ 同样先归零音量再暂停 —— 恢复时的咔哒声也是这么来的。</summary>
        public void Pause()
        {
            try
            {
                if (!IsReady) return;
                _fadeLeft = 0;
                try { _player!.Volume = 0d; } catch { }
                _player!.Pause();
            }
            catch (Exception ex) { LightLogger.LogWarning($"[BassMusicPlayer.Pause] {ex.Message}"); }
        }

        /// <summary>从暂停处继续（带淡入）。</summary>
        public void Resume()
        {
            try
            {
                if (!IsReady) return;
                try { _player!.Volume = 0d; } catch { }
                _fadeTarget = Volume;
                _fadeLeft = FadeFrames;
                _player!.Play();
            }
            catch (Exception ex) { LightLogger.LogWarning($"[BassMusicPlayer.Resume] {ex.Message}"); }
        }

        /// <summary>停止（保留播放器实例，下次可继续用）。</summary>
        public void Stop()
        {
            try
            {
                _fadeLeft = 0;
                _requestId++;                    // 让还在飞的加载作废
                if (_player == null) return;
                try { _player.Stop(); } catch { }
                try { _player.Volume = 0d; } catch { }
            }
            catch (Exception ex) { LightLogger.LogWarning($"[BassMusicPlayer.Stop] {ex.Message}"); }
        }

        /// <summary>设置音量 0~1（立即生效，不走淡入）。</summary>
        public void SetVolume(float v)
        {
            try
            {
                Volume = Mathf.Clamp01(v);
                _fadeTarget = Volume;
                _fadeLeft = 0;
                if (IsReady) _player!.Volume = Volume;
            }
            catch (Exception ex) { LightLogger.LogWarning($"[BassMusicPlayer.SetVolume] {ex.Message}"); }
        }

        /// <summary>设置是否单曲循环。</summary>
        public void SetLoop(bool loop)
        {
            try { if (IsReady) _player!.Loop = loop; }
            catch (Exception ex) { LightLogger.LogWarning($"[BassMusicPlayer.SetLoop] {ex.Message}"); }
        }

        /// <summary>
        /// 取 BASS 通道句柄。
        /// ⚠️ <c>MediaPlayer.Handle</c> 是 **internal** 的（Cecil 查过：get=False），
        ///    必须反射取 —— 参考实现也是这么做的。
        /// </summary>
        private int GetHandle()
        {
            try
            {
                if (_player == null) return 0;
                var prop = typeof(MediaPlayer).GetProperty("Handle", AccessTools.all);
                if (prop == null) return 0;
                var v = prop.GetValue(_player);
                return v is int i ? i : 0;
            }
            catch { return 0; }
        }
    }
}
