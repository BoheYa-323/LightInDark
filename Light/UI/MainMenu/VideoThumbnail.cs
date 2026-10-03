using System;
using LightInDark.Core;
using UnityEngine;
using UnityEngine.Video;

namespace Light.UI.MainMenu;

/// <summary>
/// 给视频抽一张预览图：播放视频的**开头 60 帧**，取其中第一张"不是纯黑"的画面。
///
/// 为什么需要：视频没法像图片那样直接 <c>LoadImage</c> 出预览。用户要求
/// "视频预览截视频开头 60 帧中非纯黑部分" —— 因为很多视频开头是黑场/淡入，
/// 直接取第 0 帧会得到一张全黑的图，看起来像坏了。
///
/// 做法：
///   1. 建一个隐藏的 <see cref="VideoPlayer"/>，<c>renderMode = RenderTexture</c>，
///      渲染到一张小 RT（640×360，足够预览框用）；
///   2. 每帧把 RT 读回 CPU，算平均亮度；超过阈值就认为"不是纯黑"，留下这一帧；
///   3. 最多看 60 帧，都没有就放弃（返回 null，界面继续显示文字说明）。
///
/// ⚠️ 音量设成 <c>None</c>：这只是一次"取帧"，绝不能出声，
///    也不能和正在播放的背景视频抢音频设备。
/// </summary>
internal sealed class VideoThumbnail
{
    private const int MaxFrames = 60;
    private const int W = 640;
    private const int H = 360;

    /// <summary>平均亮度低于这个值就当成"纯黑"跳过。</summary>
    private const float BlackThreshold = 0.055f;

    private GameObject? _holder;
    private VideoPlayer? _player;
    private RenderTexture? _rt;
    private Texture2D? _readTex;
    private Texture2D? _result;

    private string _path = "";
    private int _framesSeen;
    private bool _finished;

    /// <summary>抽帧结束（成功或放弃）。</summary>
    public bool IsFinished => _finished;

    /// <summary>抽到的预览贴图；null = 没抽到（全黑或失败）。</summary>
    public Texture2D? Result => _result;

    /// <summary>正在处理的那个视频路径。</summary>
    public string Path => _path;

    public void Start(string path)
    {
        Stop();
        _result = null;      // 别把上一个视频的帧留下来（Stop 故意保留 result 给面板用）
        _path = path;
        _framesSeen = 0;
        _finished = false;

        try
        {
            _rt = new RenderTexture(W, H, 0) { name = "LightBgThumbRT" };
            _rt.Create();

            _readTex = new Texture2D(W, H, TextureFormat.RGB24, false);
            _readTex.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            _holder = new GameObject("LightBgThumbGrabber");
            _holder.hideFlags |= HideFlags.HideAndDontSave;

            _player = _holder.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.source = VideoSource.Url;
            _player.url = path;
            _player.isLooping = true;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _rt;
            _player.audioOutputMode = VideoAudioOutputMode.None;   // 取帧绝不出声
            _player.skipOnDrop = true;
            _player.waitForFirstFrame = false;
            _player.Play();

            LightLogger.Log($"[VideoThumbnail] 开始抽帧：{System.IO.Path.GetFileName(path)}（最多 {MaxFrames} 帧）");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[VideoThumbnail.Start] {ex.Message}");
            _finished = true;
        }
    }

    /// <summary>每帧驱动一次。面板打开期间由 BackgroundPanel.TickInput 调用。</summary>
    public void Tick()
    {
        if (_finished || _player == null || _rt == null || _readTex == null) return;

        try
        {
            if (!_player.isPrepared) return;

            if (_framesSeen >= MaxFrames)
            {
                LightLogger.Log("[VideoThumbnail] 开头 60 帧里没找到非纯黑画面，放弃抽帧");
                _finished = true;
                return;
            }

            _framesSeen++;

            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            _readTex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            _readTex.Apply();
            RenderTexture.active = prev;

            float brightness = AverageBrightness(_readTex);
            if (brightness <= BlackThreshold) return;     // 纯黑 → 看下一帧

            // 命中：把这一帧留下来
            _result = new Texture2D(W, H, TextureFormat.RGB24, false);
            _result.SetPixels32(_readTex.GetPixels32());
            _result.Apply();
            _result.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            _finished = true;
            LightLogger.Log($"[VideoThumbnail] 抽到预览帧：第 {_framesSeen} 帧，平均亮度 {brightness:F3}");
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[VideoThumbnail.Tick] {ex.Message}");
            _finished = true;
        }
    }

    /// <summary>平均亮度（0..1）。为了省时间只按步长采样。</summary>
    private static float AverageBrightness(Texture2D tex)
    {
        try
        {
            var px = tex.GetPixels32();
            if (px == null || px.Length == 0) return 0f;

            const int step = 7;    // 采样步长：640*360/7 ≈ 3.3 万次，够快也够准
            long sum = 0;
            int n = 0;
            for (int i = 0; i < px.Length; i += step)
            {
                var c = px[i];
                sum += c.r + c.g + c.b;
                n += 3;
            }
            return n == 0 ? 0f : sum / (float)n / 255f;
        }
        catch { return 0f; }
    }

    /// <summary>拆掉播放器与临时资源。可重复调用。</summary>
    public void Stop()
    {
        try
        {
            if (_player != null)
            {
                try { _player.Stop(); } catch { }
                UnityEngine.Object.Destroy(_player);
            }
            if (_holder != null) UnityEngine.Object.Destroy(_holder);
            if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); }
            if (_readTex != null) UnityEngine.Object.Destroy(_readTex);
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[VideoThumbnail.Stop] {ex.Message}");
        }

        _player = null;
        _holder = null;
        _rt = null;
        _readTex = null;
        _path = "";
        _framesSeen = 0;
        _finished = true;
    }

    /// <summary>只清掉临时资源，保留抽到的 <see cref="Result"/> 贴图（面板还要用）。</summary>
    public void StopKeepResult()
    {
        var keep = _result;
        _result = null;
        Stop();
        _result = keep;
        _finished = true;
    }
}
