using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using LightInDark.Core;

namespace Light.Audio
{
    /// <summary>
    /// 把嵌入的原生/托管依赖库释放到**游戏 exe 平级的 <c>Light_Libraries</c> 目录**,
    /// 并在调用 BASS 之前把它们加载起来。
    ///
    /// ---- 为什么要释放到磁盘 ----
    /// <c>bass.dll</c> 是**原生 DLL**。`ManagedBass` 内部是
    /// <c>[DllImport("bass")]</c>,Windows 按标准搜索路径找 —— 而我们的 dll 在
    /// 程序集资源里,不在搜索路径上,所以**必须先落盘、再让系统能按名字找到它**。
    /// 这是原生依赖的通用做法,没有"直接内存加载"的稳妥办法。
    ///
    /// ---- 为什么放在 exe 平级而不是 BepInEx 子目录 ----
    /// 用户明确要求 <c>Light_Libraries</c>。放在游戏根目录还有个好处:
    /// <c>SetDllDirectory</c> + <c>NativeLibrary.Load</c> 之后,Windows 的模块查找
    /// 一定能命中(同目录的已加载模块按 basename 解析)。
    ///
    /// ---- 版本 / 覆盖策略 ----
    /// 释放时**比对大小**:不一致才覆盖。这样升级模组时会自动更新,
    /// 又不会每次启动都重写(重写会让某些安全软件频繁告警)。
    /// ⚠️ 只比大小不比哈希是有意的取舍 —— 每次启动算 166KB 的 MD5 不划算,
    ///    而"同大小但内容不同"在发布流程里不会出现。
    /// </summary>
    public static class NativeLibraryLoader
    {
        /// <summary>释放目录:<c>&lt;游戏根目录&gt;\Light_Libraries</c>。</summary>
        public static string LibrariesDir
        {
            get
            {
                try { return Path.Combine(BepInEx.Paths.GameRootPath, "Light_Libraries"); }
                catch { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Light_Libraries"); }
            }
        }

        /// <summary>嵌入资源名 —— 对应 <c>Light\Resources\Native\*.dll</c>。</summary>
        private const string ResBass = "Light.Resources.Native.bass.dll";
        private const string ResManagedBass = "Light.Resources.Native.ManagedBass.dll";

        private static bool _extracted;
        private static bool _bassLoaded;
        private static bool _resolveHooked;

        /// <summary>
        /// 挂 <see cref="AppDomain.AssemblyResolve"/>,让 <c>ManagedBass.dll</c> 这类**托管**依赖
        /// 也能从 <see cref="LibrariesDir"/> 解析。
        ///
        /// ⚠️⚠️ **不加这个就会失败**（2026-10-06 实机日志）：
        /// <code>
        /// [Warning] [BassMusicPlayer] IL2CPP 类型注册失败:
        ///           Could not load file or assembly 'ManagedBass, Version=3.0.0.0, ...'
        ///           系统找不到指定的文件。
        /// </code>
        /// 原因:<c>Libs\*.dll</c> 是以 <c>&lt;Private&gt;false&lt;/Private&gt;</c> 引用的 ——
        /// **只用于编译,不会拷到运行时目录**。原生 <c>bass.dll</c> 靠
        /// <see cref="PrepareBass"/> 的 <c>NativeLibrary.Load</c> 解决了,但托管程序集
        /// 走的是 CLR 的程序集解析,**BepInEx 不看 <c>Light_Libraries</c>** →
        /// 必须自己挂解析器。
        ///
        /// ⚠️ **必须在任何 <c>ManagedBass</c> 类型被触碰之前挂上**。
        ///    <see cref="BassMusicPlayer"/> 的静态构造函数里有
        ///    <c>ClassInjector.RegisterTypeInIl2Cpp&lt;BassMusicPlayer&gt;()</c>,
        ///    它会反射该类型的所有字段 → 立刻需要 <c>ManagedBass</c>。
        ///    而那个静态构造函数在 <c>BassMusicPlayer.Ensure()</c> 里被触发,
        ///    <c>Ensure()</c> 又会先调 <see cref="PrepareBass"/> —— 顺序是对的。
        /// </summary>
        private static void HookAssemblyResolve()
        {
            if (_resolveHooked) return;
            _resolveHooked = true;      // 先置位,避免重入时挂两次

            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                {
                    try
                    {
                        var simpleName = new AssemblyName(args.Name).Name;
                        if (string.IsNullOrEmpty(simpleName)) return null;

                        var path = Path.Combine(LibrariesDir, simpleName + ".dll");
                        if (File.Exists(path))
                        {
                            var asm = Assembly.LoadFrom(path);
                            LightLogger.LogDebug($"[NativeLibraryLoader] 已解析程序集 {simpleName} → {path}");
                            return asm;
                        }
                    }
                    catch (Exception ex)
                    {
                        LightLogger.LogWarning($"[NativeLibraryLoader] 解析程序集失败 {args.Name}: {ex.Message}");
                    }
                    return null;
                };

                LightLogger.LogDebug($"[NativeLibraryLoader] 已挂上 AssemblyResolve（从 {LibrariesDir} 解析托管依赖）");
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[NativeLibraryLoader.HookAssemblyResolve]", ex);
            }
        }

        /// <summary>释放是否成功过(供诊断)。</summary>
        public static bool Extracted => _extracted;

        /// <summary>bass.dll 是否已加载(供诊断)。</summary>
        public static bool BassLoaded => _bassLoaded;

        /// <summary>
        /// 释放两个库到 <see cref="LibrariesDir"/>。幂等,可反复调用。
        /// </summary>
        public static bool EnsureExtracted()
        {
            if (_extracted) return true;

            try
            {
                var dir = LibrariesDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                bool a = ExtractOne(ResBass, Path.Combine(dir, "bass.dll"));
                bool b = ExtractOne(ResManagedBass, Path.Combine(dir, "ManagedBass.dll"));

                // ManagedBass.dll 是托管的 —— 它已经在 Libs 里被引用,
                // 运行期由 BepInEx 的程序集解析负责;这里释放它只是为了"目录内容完整"
                // (方便排查 / 以后要用 NativeLibrary 方式加载时也在)。
                // 所以它的失败不算致命,只有 bass.dll 必须成功。
                if (a)
                {
                    _extracted = true;
                    LightLogger.LogDebug($"[NativeLibraryLoader] 依赖库已就绪：{dir}" +
                                    $"（bass.dll={(a ? "OK" : "缺失")} ManagedBass.dll={(b ? "OK" : "缺失")}）");
                    return true;
                }

                LightLogger.LogError("[NativeLibraryLoader] bass.dll 释放失败 —— 音乐播放器的 mp3/flac 可能不可用（wav 不受影响）");
                return false;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[NativeLibraryLoader.EnsureExtracted]", ex);
                return false;
            }
        }

        /// <summary>
        /// 从嵌入资源抽一个文件出来。已存在且**大小一致**就跳过。
        /// </summary>
        private static bool ExtractOne(string resourceName, string targetPath)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    LightLogger.LogError($"[NativeLibraryLoader] 找不到嵌入资源 {resourceName}" +
                                         "（检查 csproj 的 <EmbeddedResource Include=\"Resources\\**\\*.*\" /> 与文件是否真的在 Light\\Resources\\Native 下）");
                    return false;
                }

                // 已存在且大小一致 → 认为是同一个版本,不重写
                var fi = new FileInfo(targetPath);
                if (fi.Exists && fi.Length == stream.Length) return true;

                // ⚠️ 覆盖前先删:如果 bass.dll 已经被上一次运行加载过,
                //    File.Create 会因文件被占用而失败。删掉再写能避开(同进程内已加载的模块
                //    仍留在内存里,不影响本次运行)。
                try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }

                using (var fs = File.Create(targetPath))
                    stream.CopyTo(fs);

                LightLogger.LogDebug($"[NativeLibraryLoader] 已释放 {Path.GetFileName(targetPath)} " +
                                $"({stream.Length:N0} B) → {targetPath}");
                return true;
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[NativeLibraryLoader.ExtractOne:{Path.GetFileName(targetPath)}] {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 在**第一次调用 BASS 之前**调这个:把 <c>Light_Libraries</c> 加进 DLL 搜索路径,
        /// 并把 bass.dll 显式加载进进程。
        ///
        /// 双保险的理由:
        ///   · <c>SetDllDirectory</c> 让**之后**的 <c>[DllImport("bass")]</c> 能按名字找到它;
        ///   · <c>NativeLibrary.Load</c> 直接把它加载进来 —— 模块一旦加载,
        ///     Windows 后续按 basename 解析 DllImport 时会直接命中已加载模块,
        ///     即使 SetDllDirectory 因为别的原因没生效。
        /// 任一条成功就够,两条都失败才真的会 <c>DllNotFoundException</c>。
        /// </summary>
        public static bool PrepareBass()
        {
            if (_bassLoaded) return true;

            try
            {
                if (!EnsureExtracted()) return false;

                // ⚠️⚠️ **托管依赖的解析器必须在这里挂上** —— 早于任何 ManagedBass 类型被触碰。
                //    详见 HookAssemblyResolve 的注释（不加就是 "Could not load file or assembly
                //    'ManagedBass'"）。
                HookAssemblyResolve();

                var dir = LibrariesDir;
                var bassPath = Path.Combine(dir, "bass.dll");

                // ① 加进搜索路径
                try { SetDllDirectory(dir); } catch (Exception ex) { LightLogger.LogWarning($"[NativeLibraryLoader] SetDllDirectory 失败(忽略): {ex.Message}"); }

                // ② 显式加载（.NET 5+ 的 NativeLibrary）
                try
                {
                    if (File.Exists(bassPath))
                    {
                        NativeLibrary.Load(bassPath);
                        _bassLoaded = true;
                        LightLogger.LogDebug($"[NativeLibraryLoader] bass.dll 已加载：{bassPath}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    // 已经加载过会抛"模块已存在",那不是错误 —— 当作成功
                    var msg = ex.Message ?? "";
                    if (msg.IndexOf("already loaded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        msg.IndexOf("已加载", StringComparison.Ordinal) >= 0)
                    {
                        _bassLoaded = true;
                        return true;
                    }
                    LightLogger.LogWarning($"[NativeLibraryLoader] NativeLibrary.Load 失败，改靠 SetDllDirectory: {msg}");
                }

                // ③ 靠 ① 的搜索路径也能跑起来
                _bassLoaded = true;
                return true;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[NativeLibraryLoader.PrepareBass]", ex);
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}
