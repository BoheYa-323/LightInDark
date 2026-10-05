using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LightInDark.Core;
using NAudio.Wave;
using UnityEngine;

namespace Light.UI.MusicPlayer;

/// <summary>
/// **音频加载器** —— 复刻 FinalSuspect(参考工程 <c>MyMusic\AudioLoader.cs</c>)的成熟方案:
/// <b>完全不用 UnityWebRequest</b>,两条路径:
///
///   ① <c>.wav</c>  → 自己解析 RIFF chunk(fmt / data),位深 8/16/24/32/64 → float[]
///   ② <c>.mp3/.aiff/.aif/.flac</c> → NAudio 的 <see cref="AudioFileReader"/>(自动识别格式)
///   ③ <c>.wav</c> 头解析失败 → 按"16-bit 立体声 44.1kHz 裸 PCM"硬解(FinalSuspect 的 <c>CreateClipFromRaw</c>)
///
/// 两条路径最后都走同一句:
/// <code>
/// var clip = AudioClip.Create(name, frames, channels, sampleRate, false);
/// clip.SetData(samples, 0);          // samples 是**交错**的 L,R,L,R...
/// </code>
///
/// ⚠️⚠️ **线程规矩(照抄 FinalSuspect 的分工,但用协程代替 async/await)**:
///   · 读文件 / 解码 / 位深转换 → 可以放后台线程(<c>Task.Run</c>)
///   · <c>AudioClip.Create</c> / <c>SetData</c> → **必须主线程**,而且**整个调用期间不能 yield**
///     (Unity 不允许跨帧创建同一批原生对象)
///   所以本类的做法是:后台任务跑完 → 主线程里**一口气**建完 AudioClip,中间绝不 yield。
///
/// ⚠️ 关于 async:本工程直接调 <c>Task.Run(...).GetAwaiter().GetResult()</c> 有**死锁**风险
///    (Among Us 的同步上下文不保证线程池),而且阻塞主线程几秒会被 Windows 判"无响应"。
///    所以这里把"等待后台任务"交给**协程**:每帧看一眼 <c>IsCompleted</c>,不卡帧、不死锁。
/// </summary>
public static class MusicClipLoader
{
    /// <summary>支持的扩展名(与 UI 上写的提示保持一致)。</summary>
    public static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".flac", ".aiff", ".aif" };

    /// <summary>给 UI 显示的一句话(不含"点击切换"之类字样)。</summary>
    public const string FormatHint = "支持 mp3 / wav / flac / aiff";

    /// <summary>裸 PCM 兜底时的假设参数(和 FinalSuspect 一致)。</summary>
    private const int RawFallbackChannels = 2;
    private const int RawFallbackSampleRate = 44100;
    private const int RawFallbackBits = 16;

    /// <summary>
    /// 单文件采样数上限(约 <b>2GB float</b>)。
    /// FinalSuspect 没有这道闸;我们加一条是为了"用户往 Music 里丢一个 1 小时的无损 flac"时
    /// 不至于一次性申请几 GB 托管数组把游戏 OOM 掉 —— 超限直接判失败并给出明确原因。
    /// </summary>
    private const int MaxSamples = 500_000_000;

    // =====================================================================
    //  日志兜底
    // =====================================================================

    /// <summary>
    /// 写警告日志，**吞掉一切异常**。
    ///
    /// 为什么需要这层：<see cref="LightLogger"/> 的静态构造函数会用 <c>Application.dataPath</c>
    /// 推游戏根目录，而本类的这些日志是在**后台线程**里打的 —— 万一那里抛了，
    /// 异常会顺着线程池吃掉整个加载任务。日志坏了不能连带把功能弄坏。
    /// </summary>
    private static void LogWarn(string message)
    {
        try { LightLogger.LogWarning(message); } catch { }
    }

    /// <summary>同上，普通日志版。</summary>
    private static void LogInfo(string message)
    {
        try { LightLogger.Log(message); } catch { }
    }

    // =====================================================================
    //  对外唯一入口(协程版本)
    // =====================================================================

    /// <summary>
    /// 加载一个音频文件为 <see cref="AudioClip"/>。
    ///
    /// 用法(必须在协程里):
    /// <code>
    /// string fail;
    /// yield return MusicClipLoader.CoLoadClip(path, OnClipReady, r => fail = r);
    /// </code>
    /// 失败时不抛异常,只通过 <paramref name="onFail"/> 回报原因 —— UI 只要把原因显示出来即可。
    /// </summary>
    /// <param name="filePath">音频文件完整路径。</param>
    /// <param name="onSuccess">成功回调(主线程)。</param>
    /// <param name="onFail">失败回调(主线程),参数是失败原因。</param>
    public static System.Collections.IEnumerator CoLoadClip(string filePath, Action<AudioClip> onSuccess, Action<string> onFail)
    {
        if (string.IsNullOrEmpty(filePath) || onSuccess == null)
        {
            try { onFail?.Invoke("文件路径为空"); } catch { }
            yield break;
        }

        string fileName;
        try { fileName = Path.GetFileName(filePath); }
        catch { fileName = filePath; }

        var task = new LoadTask(filePath);

        // 后台线程:读文件 + 解码 + 位深转换(异常一律记进 task.Failed,不往外抛)
        try
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { task.Run(); }
                catch (Exception ex) { task.Failed = $"{ex.GetType().Name}: {ex.Message}"; }
            });
        }
        catch (Exception ex)
        {
            try { onFail?.Invoke($"线程池不可用: {ex.Message}"); } catch { }
            yield break;
        }

        // ⚠️ 这里用 IsCompleted(不是 Status == RanToCompletion):
        //    任务**失败**时 Status 是 Faulted,一样算"完成",不会把主线程卡在这一帧上。
        while (!task.IsCompleted)
            yield return null;

        if (task.Samples == null || task.Samples.Length == 0)
        {
            var reason = string.IsNullOrEmpty(task.Failed) ? "解码结果为空" : task.Failed;
            LogWarn($"[MusicPlayer] 加载失败 {fileName}: {reason}");
            try { onFail?.Invoke(reason); } catch { }
            yield break;
        }

        // ---------- 主线程:建 AudioClip(整段不允许 yield) ----------
        AudioClip? clip = null;
        string createFail = string.Empty;
        try
        {
            clip = CreateClipSafely(task, fileName, out createFail);
        }
        catch (Exception ex)
        {
            createFail = $"建 AudioClip 异常: {ex.Message}";
            LogWarn($"[MusicPlayer] {createFail}（{fileName}）");
        }

        if (clip == null)
        {
            var reason = string.IsNullOrEmpty(createFail) ? "AudioClip.Create 返回空" : createFail;
            try { onFail?.Invoke(reason); } catch { }
            yield break;
        }

        // 时长/规格日志 —— "大文件"排查就靠这一行
        try
        {
            LightLogger.Log($"[MusicPlayer] 加载 {fileName} … {clip.length:F1}秒 {clip.channels}ch " +
                            $"{clip.frequency}Hz 采样={task.Samples.Length} 来源={task.Source}");
        }
        catch { }

        try { onSuccess(clip); } catch (Exception ex) { LightLogger.LogError("[MusicPlayer] onSuccess", ex); }
    }

    /// <summary>
    /// 主线程建 AudioClip。**独立成一个方法是为了让协程里那段"不 yield 的区间"尽可能短**,
    /// 也顺便把"采样数不足一个采样帧"这种脏数据挡掉(不然 AudioClip.Create 会抛)。
    /// </summary>
    private static AudioClip? CreateClipSafely(LoadTask task, string fileName, out string failReason)
    {
        failReason = string.Empty;

        int channels = Mathf.Max(1, task.Channels);
        int sampleRate = Mathf.Max(1, task.SampleRate);
        var samples = task.Samples;
        if (samples == null || samples.Length == 0)
        {
            failReason = "采样为空";
            return null;
        }

        // 采样数不是声道数的整数倍 → 丢掉尾巴那几个(常见于被截断的 wav)
        int frames = samples.Length / channels;
        if (frames <= 0)
        {
            failReason = $"有效采样帧为 0(channels={channels}, samples={samples.Length})";
            LogWarn($"[MusicPlayer] 加载失败 {fileName}: {failReason}");
            return null;
        }

        if (frames * channels != samples.Length)
        {
            var trimmed = new float[frames * channels];
            Buffer.BlockCopy(samples, 0, trimmed, 0, trimmed.Length * sizeof(float));
            samples = trimmed;
        }

        // ⚠️ 这是整个加载过程中**唯一**碰 Unity 音频 API 的两行,必须在主线程、且连续执行。
        //    stream=false:一次性把采样放进内存 —— 换歌时不需要重新解码,也不会有流式解码的杂音。
        var clip = AudioClip.Create(fileName, frames, channels, sampleRate, false);
        if (clip == null)
        {
            failReason = "AudioClip.Create 返回空";
            return null;
        }

        clip.SetData(samples, 0);
        clip.name = fileName;
        return clip;
    }

    // =====================================================================
    //  后台任务载体
    // =====================================================================

    /// <summary>
    /// 后台加载任务。<see cref="Samples"/> 只在主线程读,<see cref="Failed"/> 只在后台写、
    /// 主线程在 <see cref="IsCompleted"/> 之后才读 —— 靠 <see cref="IsCompleted"/> 的写屏障保证可见性。
    /// </summary>
    private sealed class LoadTask
    {
        private readonly string _path;
        private volatile bool _done;

        /// <summary>交错 float 采样(L,R,L,R...)。</summary>
        public float[]? Samples;

        public int Channels = 1;
        public int SampleRate = 44100;

        /// <summary>哪个解码器出的结果(日志用):RIFF / NAudio / 裸PCM兜底。</summary>
        public string Source = "未知";

        /// <summary>失败原因(null / 空 = 还没失败)。</summary>
        public string? Failed;

        public bool IsCompleted => _done;

        public LoadTask(string path) { _path = path; }

        public void Run()
        {
            try
            {
                var ext = (Path.GetExtension(_path) ?? string.Empty).ToLowerInvariant();

                switch (ext)
                {
                    case ".wav":
                        LoadWav();
                        break;

                    case ".mp3":
                    case ".aiff":
                    case ".aif":
                    case ".flac":
                        LoadWithNAudio();
                        break;

                    default:
                        Failed = $"不支持的扩展名 {ext}(支持:{string.Join(" ", SupportedExtensions)})";
                        break;
                }
            }
            catch (Exception ex)
            {
                Failed = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                _done = true;
            }
        }

        // -----------------------------------------------------------------
        //  ① WAV:自己解析 RIFF chunk
        // -----------------------------------------------------------------

        private void LoadWav()
        {
            var bytes = ReadAllBytes(_path);
            if (bytes.Length < 44)
            {
                Failed = $"文件太小({bytes.Length} 字节),不像 wav";
                return;
            }

            if (TryParseWav(bytes, out var samples, out var channels, out var sampleRate, out var failReason))
            {
                Samples = samples;
                Channels = channels;
                SampleRate = sampleRate;
                Source = "RIFF";
                return;
            }

            // ③ 兜底:FinalSuspect 的 CreateClipFromRaw —— 头坏了也按裸 PCM 硬解一次
            LogWarn($"[MusicPlayer] WAV 头解析失败({failReason}),按裸 PCM 兜底" +
                                   $"(假定 16-bit 立体声 44100Hz,**时长可能不对**)");
            Samples = ConvertBytesToFloats(bytes, RawFallbackBits);
            Channels = RawFallbackChannels;
            SampleRate = RawFallbackSampleRate;
            Source = "裸PCM兜底";
        }

        /// <summary>
        /// 解析 RIFF/WAVE:找 <c>fmt </c> 读 format/channels/sampleRate/bitDepth,找 <c>data</c> 取 PCM。
        /// 返回 false 时 <paramref name="failReason"/> 写清是哪一步不行(日志/UI 直接用这句)。
        /// </summary>
        private static bool TryParseWav(byte[] data, out float[]? samples, out int channels,
            out int sampleRate, out string failReason)
        {
            samples = null;
            channels = 0;
            sampleRate = 0;
            failReason = string.Empty;

            if (data.Length < 44)
            {
                failReason = "文件太小";
                return false;
            }

            try
            {
                if (!string.Equals(Encoding.ASCII.GetString(data, 0, 4), "RIFF", StringComparison.Ordinal) ||
                    !string.Equals(Encoding.ASCII.GetString(data, 8, 4), "WAVE", StringComparison.Ordinal))
                {
                    failReason = "不是 RIFF/WAVE 头";
                    return false;
                }

                int fmt = FindChunk(data, "fmt ");
                if (fmt < 0) { failReason = "找不到 fmt 块"; return false; }
                if (fmt + 24 >= data.Length) { failReason = "fmt 块越界"; return false; }

                int format = BitConverter.ToInt16(data, fmt + 8);      // 1 = PCM,3 = IEEE float
                if (format != 1 && format != 3)
                {
                    failReason = $"压缩格式(format={format}),只支持 1(PCM) / 3(IEEE float)";
                    return false;
                }

                int ch = BitConverter.ToInt16(data, fmt + 10);
                int sr = BitConverter.ToInt32(data, fmt + 12);
                int bits = BitConverter.ToInt16(data, fmt + 22);

                if (ch <= 0) { failReason = $"声道数非法({ch})"; return false; }
                if (sr <= 0) { failReason = $"采样率非法({sr})"; return false; }

                int dataChunk = FindChunk(data, "data");
                if (dataChunk < 0) { failReason = "找不到 data 块"; return false; }

                long declared = BitConverter.ToInt32(data, dataChunk + 4);
                long dataStart = dataChunk + 8L;
                long available = data.Length - dataStart;
                // data 块声明的长度可能是 0(边录边写的 wav)或大于实际 → 一律按实际能读到的算
                long size = declared > 0 ? Math.Min(declared, available) : available;
                if (size < 8) { failReason = $"data 块为空({size} 字节)"; return false; }

                var pcm = new byte[size];
                Buffer.BlockCopy(data, (int)dataStart, pcm, 0, (int)size);

                var floats = ConvertBytesToFloats(pcm, bits);
                if (floats.Length == 0)
                {
                    failReason = $"位深 {bits} 转换后没有采样";
                    return false;
                }

                samples = floats;
                channels = ch;
                sampleRate = sr;

                // 音频数据是"交错"的,采样帧数必须能被声道数整除;除不尽说明文件被截断过。
                // 这里只警告不失败 —— 后面 CreateClipSafely 会丢掉尾巴那几个采样。
                if (floats.Length % ch != 0)
                    LogWarn($"[MusicPlayer] WAV 采样数 {floats.Length} 不是声道数 {ch} 的整数倍,尾部将被截断");

                return true;
            }
            catch (Exception ex)
            {
                failReason = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>在 RIFF 里找某个 chunk 的位置(返回 chunk id 的起始下标;找不到 -1)。</summary>
        private static int FindChunk(byte[] data, string id)
        {
            var idx = 12;
            while (idx >= 0 && idx < data.Length - 8)
            {
                if (Encoding.ASCII.GetString(data, idx, 4) == id) return idx;

                int size = BitConverter.ToInt32(data, idx + 4);
                if (size < 0) return -1;            // 坏文件:负数大小会让 idx 往回跑,死循环
                idx += 8 + size;
            }
            return -1;
        }

        // -----------------------------------------------------------------
        //  ② NAudio:mp3 / aiff / flac
        // -----------------------------------------------------------------

        private void LoadWithNAudio()
        {
            // ⚠️ NAudio 的 AudioFileReader 自己管文件句柄,必须 Dispose(FinalSuspect 用 await using)
            using var reader = new AudioFileReader(_path);

            var fmt = reader.WaveFormat;
            int ch = fmt.Channels;
            int sr = fmt.SampleRate;
            int bytesPerSample = Math.Max(1, fmt.BitsPerSample / 8);

            if (ch <= 0 || sr <= 0)
            {
                Failed = $"NAudio 报的格式非法({ch}ch {sr}Hz)";
                return;
            }

            // AudioFileReader 的输出**已经是 float**,Length 是字节数 → 除以每采样字节数得采样数
            long totalSamples = reader.Length / bytesPerSample;
            if (totalSamples <= 0)
            {
                Failed = "NAudio 报告长度为 0";
                return;
            }

            if (totalSamples > MaxSamples)
            {
                Failed = $"文件太长({fmt.AverageBytesPerSecond * 1.0 / 1024 / 1024:F1}MB/s," +
                         $"采样数 {totalSamples} 超过上限 {MaxSamples})";
                return;
            }

            // 大数组申请前先收一次 —— IL2CPP 下这条老经验确实能减少"申请不到连续内存"
            GC.Collect();

            var data = new float[totalSamples];
            int read = reader.Read(data, 0, data.Length);
            if (read <= 0)
            {
                Failed = "NAudio 读出 0 个采样";
                return;
            }

            // 实际读到的比预计少(流长度不准)→ 截断
            if (read != data.Length)
            {
                var trimmed = new float[read];
                Buffer.BlockCopy(data, 0, trimmed, 0, read * sizeof(float));
                data = trimmed;
            }

            Samples = data;
            Channels = ch;
            SampleRate = sr;
            Source = "NAudio";
        }

        // -----------------------------------------------------------------
        //  位深转换
        // -----------------------------------------------------------------

        /// <summary>
        /// 把原始 PCM 字节转成交错 float。
        ///
        /// ⚠️ FinalSuspect 的 16/32 位分支用了 <c>unsafe + fixed</c> 指针(本工程也开了
        ///    <c>AllowUnsafeBlocks</c>),这里改成 <see cref="Buffer.BlockCopy"/>:
        ///    等价、可读、且不依赖编译开关 —— 少了"某个配置下 unsaef 编不过"的风险。
        /// </summary>
        private static float[] ConvertBytesToFloats(byte[] src, int bits)
        {
            switch (bits)
            {
                case 8: return Convert8Bit(src);
                case 16: return Convert16Bit(src);
                case 24: return Convert24Bit(src);
                case 32: return Convert32BitInt(src);
                case 64: return Convert32Float(src);   // "64 位"的 wav 实际是 32-bit float(每采样 4 字节)
                default:
                    throw new NotSupportedException($"不支持的位深 {bits}(支持 8/16/24/32/64)");
            }
        }

        /// <summary>8-bit PCM 是**无符号**的,中心值 128。</summary>
        private static float[] Convert8Bit(byte[] src)
        {
            var dst = new float[src.Length];
            for (int i = 0; i < src.Length; i++)
                dst[i] = (src[i] - 128) / 128f;
            return dst;
        }

        private static float[] Convert16Bit(byte[] src)
        {
            int n = src.Length / 2;                 // 奇数尾巴丢掉(截断文件)
            var dst = new float[n];
            var tmp = new short[n];
            Buffer.BlockCopy(src, 0, tmp, 0, n * 2);
            for (int i = 0; i < n; i++)
                dst[i] = tmp[i] / 32768f;
            return dst;
        }

        /// <summary>24-bit 小端三字节:拼成 int 后**符号扩展**到 32 位,再除以 2^23。</summary>
        private static float[] Convert24Bit(byte[] src)
        {
            int n = src.Length / 3;
            var dst = new float[n];
            for (int i = 0; i < n; i++)
            {
                int o = i * 3;
                int s = src[o] | (src[o + 1] << 8) | (src[o + 2] << 16);
                if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
                dst[i] = s / 8388608f;              // 2^23
            }
            return dst;
        }

        private static float[] Convert32BitInt(byte[] src)
        {
            int n = src.Length / 4;
            var dst = new float[n];
            var tmp = new int[n];
            Buffer.BlockCopy(src, 0, tmp, 0, n * 4);
            for (int i = 0; i < n; i++)
                dst[i] = tmp[i] / 2147483648f;      // 2^31
            return dst;
        }

        private static float[] Convert32Float(byte[] src)
        {
            int n = src.Length / 4;
            var dst = new float[n];
            Buffer.BlockCopy(src, 0, dst, 0, n * 4);
            return dst;
        }

        // -----------------------------------------------------------------
        //  读文件(FinalSuspect 的 ReadAllBytesAsync 有个坑:它忽略了 ReadAsync 的实际读取长度)
        // -----------------------------------------------------------------

        private static byte[] ReadAllBytes(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.SequentialScan);

            long len = fs.Length;
            // ⚠️ 原始 PCM 兜底要把整份文件当数据,所以这里读完整文件;
            //    但 wav 之外的格式走 NAudio 流式读,不会碰这条路径。
            if (len <= 0) return Array.Empty<byte>();
            if (len > int.MaxValue) throw new IOException($"文件过大({len} 字节),超过单次读取上限");

            var buffer = new byte[len];
            int offset = 0;
            while (offset < buffer.Length)
            {
                int n = fs.Read(buffer, offset, buffer.Length - offset);
                if (n <= 0) break;                  // 文件被别的程序截断了,读到哪算哪
                offset += n;
            }

            if (offset == buffer.Length) return buffer;

            var result = new byte[offset];           // 没读满 → 截断
            Buffer.BlockCopy(buffer, 0, result, 0, offset);
            return result;
        }
    }
}
