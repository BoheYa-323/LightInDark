global using HarmonyLib;
global using Light.Utilities;
global using System.Collections;
global using UnityEngine;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Light.ChatCommands;
using Light.Config;
using Light.News;
using Light.Patches;
using Light.Roles.Crewmates;
using Light.Roles.Impostors;
using LightInDark.Core;
using LightInDark.Events;
using LightInDark.Language;
using LightInDark.Roles;
using LightInDark.RPCs;
using System;
using System.Text.Json;
using UnityEngine.SceneManagement;

namespace Light;

[BepInPlugin(Id, Name, Version)]
[BepInProcess("Among Us.exe")]
[BepInDependency("cn.moonscar.lightapi",BepInDependency.DependencyFlags.HardDependency)]
[BepInIncompatibility("jp.dreamingpig.amongus.nebula.loader")]
[BepInIncompatibility("com.qin-qwq.townofnextedited")]
[BepInIncompatibility("jp.ykundesu.supernewroles")]
[BepInIncompatibility("com.ten.betteramongus")]
[BepInIncompatibility("com.gurge44.endlesshostroles")]
[BepInIncompatibility("com.emptybottle.townofhost")]
[BepInIncompatibility("cn.havenglow.finalsuspect")]
public partial class LightPlugin : BasePlugin
{
    public const string Id = "cn.moonscar.lid";
    public const string Name = "LightInTheDark";
    public const string Version = "0.0.1";

    public const string VisualVersion = "v0.0.1";

    public const string RichVersion = "<color=#4FD1C5>ver</color> <color=#38B2AC>0.0.1</color>";
    public static string AUVersion;
    public static string CursurDataPath = Application.persistentDataPath;
    public static MainColor.ModColorData ColorData;
    public static LightSettings.LightSettingsData LightSettingsData;
    public Harmony Harmony { get; } = new(Id);
    public static string LightUserDataPath => Path.Combine(Application.persistentDataPath, "LightInDark");
    public const string ModGuid = "1c3cba75-359d-4504-a4dc-b0916e3f3010"; // GUID，这辈子不能改。
    internal static ManualLogSource StaticLog { get; private set; } = null!;

    public override void Load()
    {
        try
        {
            //FirstChanceExceptionLogger.Initialize();
            StaticLog = Log; // BepInEx日志

            // 订阅 Unity 的日志回调，把异常的**完整堆栈**记进 LightLog.log。
            // 之前 BepInEx 控制台里只有一行 "NullReferenceException: Object reference..."，
            // 没有 at Xxx.Yyy()，刷屏几千行根本定位不到。见 ExceptionStackLogger 的注释。
            // ⚠️ 放在最前面：它只是订阅回调，不碰 Harmony，早挂早抓到。
            try { Light.Diagnostics.ExceptionStackLogger.Hook(); } catch { }

            // ⚠️⚠️ 顺序有意调整过：**先把设置读进来，再挂补丁**。
            //    原来 PatchAll() 在前、LightSettingsData 赋值在后，于是补丁一挂上
            //    就有可能在设置还是 null 的时候被调用（SplashManager.Update 每帧都会走
            //    LoadPatch.Prefix，而它要读 SkipLoadAnimation）。
            //    只要这两行之间任何一步抛异常，设置就永远是 null、补丁却已经在跑
            //    → 每帧 NRE、卡在启动页进不去游戏（实测踩过）。
            //    把赋值提前，从根上消掉这个时序窗口。
            LightSettingsData = LightSettings.LoadSettingData(); // 存设置（必须在 PatchAll 之前）

            Harmony.PatchAll(); // 鸿蒙
            // ⚠️⚠️ **暂时停用，用于排查「创建游戏连线区失败」**（2026-10-04）
            //
            //   实测现象：
            //     · 关掉主插件（只留 API）→ 在线 + 本地**全部正常**
            //     · 开着主插件 → 在线（除 NikoCN1 外全部）+ 本地**全部失败**
            //     · 开着主插件 + NikoCN1 → **建房成功**（日志里 GameId=-2000042491/NCNATE）
            //
            //   三次尝试的日志序列**完全相同**（UserIDToken → FindHost → 拿到服务器地址 →
            //   Client requesting new game → Client joining game），**只有 GameId 不同**：
            //     失败：GameId = 0          成功：GameId = -2000042491
            //   → 说明**不是网络不通，是服务器（或原版本地 InnerNetServer）拒绝分配 GameId**
            //     （见 InnerNetClient.CoCreateGame 的 WaitWithTimeout：15 秒拿不到就
            //       LastCustomDisconnect = "创建游戏连线区失败…" + EnqueueDisconnect("Couldn't connect")）
            //
            //   能同时解释「本地也失败」「只有 NikoCN1 能通」「关掉主插件就全好」的，
            //   只有 MCI 注册 GUID —— 它会进到建房/匹配请求里（HostGame 带 HostGameFilterOptions），
            //   支持 modded 约定的服（NikoCN1）放行，不支持的（含原版本地服务器）拒绝。
            //
            //   AGENTS.md §5.4 已记过相关风险：GUID「自己生成、无需申请；发布后不可更改」，
            //   且 README 声称的「自动用 HostModdedGame 标签开房」在 19.0 源码里**没有对应实现**。
            //
            //   → 若这次能建房，说明要把它做成**可配置项**（私服关、官服开），而不是直接删掉。
            // ⚠️⚠️ **暂不注册 MCI**（2026-10-04 实测结论，用户决定自研 MCI 后另行实现）
            //
            //   设置 CurrentModRegistration.ModRegistrationGuidString 之后，开房路径会从
            //   Tags.HostGame 切到 Tags.HostModdedGame(25)，而官方文档写明这个通道
            //   **只由官方服务器和匹配器实现**：
            //     "This allows **our game server and matchmakers** to mark all the games
            //      hosted by your mod..." —— Technical Information for Modding Among Us, L128
            //
            //   实测后果（开着它的时候）：
            //     · 第三方私服建房 → 「创建游戏连线区失败」（GameId 恒为 0，WaitWithTimeout 超时）
            //     · **本地游戏也失败**（内置 InnerNetServer 同样不认 MCI 注册）
            //     · 只有恰好兼容的服（如 NikoCN1）能建房
            //   注释掉之后：在线 + 本地**全部正常**。
            //
            //   → 用户确认过：**不要设置项**，之后自研 MCI 时再按自己的方案实现。
            //     在那之前保持这里为"不注册"，否则整个游戏建不了房。
            // CurrentModRegistration.ModRegistrationGuidString = ModGuid;   // [禁用-MCI]
            Log.LogInfo($"Mod Guid {CurrentModRegistration.ModRegistrationGuidString},解析{CurrentModRegistration.TryGetModRegistrationGuid(out _)},协议版本{Constants.GetBroadcastVersion()}");
            if (!VersionMaker.MakeVersion())
                Log.LogError($"VM json 加载失败。具体异常请查看Light.log。"); // 这将是重大问题。写版本号。
            LoadCommand(); // 加载指令。
            LightOptionsRegistry.Register(); // 注册设置Tab块。
            RegisterAllConfigHead();        // 注册配置块
            EventSystem.RegisterAssembly(typeof(LightPlugin).Assembly); // 注册事件
            ExtractLanguageFiles(); // 解压语言文件
            Language.Load(); // 加载语言文件
            LidRpcRegistry.ScanAndPatch(Harmony); // RPC注册
            ColorData = MainColor.LoadChatColor(); // 存颜色
            PaletteColorOverride.Apply(); // 我也不知道。
            LoadRole(); // 加载职业
            RoleConfigRegistrar.Register();   // 职业配置块 + 职业专属项
            Dispatcher.Initialize(); // 牛逼工具。
#if !DEBUG
            LightLogger.ClearLog();
#endif
            RpcDefinitions.OnFreeChatStateChanged += show => ShowChatPatch.NeedShowFreeChat = show; // 不知道喵呜写的。
            AddCursorComponent(); // 鼠标。
            RegisterShowModStampOnMainMenu(); // MOD STAMPPPPPPPPPPPPPPPPPPPPPPPPPPPPPP

            // 跨场景常驻的每帧驱动器。
            // ⚠️ 必须挂：原来所有每帧逻辑都挂在 MainMenuManager.LateUpdate 上，
            //    而 MainMenuManager 只在 MainMenu 场景存在 → MatchMaking / FindAGame 里从不运行
            //    （背景图/音频在那两个场景"没有"就是因为这个）。见 LightTicker 的注释。
            try { Light.Utilities.LightTicker.Ensure(); } catch { }

            // 依赖库释放：把嵌入的 bass.dll / ManagedBass.dll 抽到 <游戏根目录>\Light_Libraries，
            // 并在第一次用 BASS 之前把它加载起来。见 NativeLibraryLoader 的注释。
            // ⚠️ 必须在任何 Bass.* 调用之前 —— 所以放在这里（早于音乐播放器初始化）。
            try { Light.Audio.NativeLibraryLoader.PrepareBass(); } catch { }

            // BASS 播放器宿主（DontDestroyOnLoad → 切场景不断音）。
            // ⚠️ 它会先 PrepareBass() 再 Bass.Init()，顺序不能反 —— 见 BassMusicPlayer 类注释 ③。
            // ⚠️ 这个类的 IL2CPP 类型注册写在**静态构造函数**里（LightTicker 就是漏了那一步
            //    导致 AddComponent 抛异常、整个驱动器从没被创建过）。
            try { Light.Audio.BassMusicPlayer.Ensure(); } catch { }
            ChatHistoryLogUtils.Init(); // 聊天历史记录。

            // 握手验证暂停 2026-09-26
            // Handshake.HandshakeManager.Initialize();

            NewsManager.LoadNews(); // 加载新闻。
            InitializeMusicPlayer(); // 音乐播放器（F3 自绘窗口）；失败只记日志，绝不让 Load 抛
            Log.LogInfo($"模组 {Name} v{Version} 已加载！");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[LightPlugin.Load]", ex);
        }
    }
    private static void RegisterAllConfigHead()
    {
        DebugConfig.Register();
    }

    /// <summary>
    /// 音乐播放器（F3 窗口）。
    ///
    /// ⚠️ 这里只做"**把常驻宿主建起来**"这一件事：
    ///    · <c>MusicPlayer</c> 是一个 <c>DontDestroyOnLoad</c> 的 MonoBehaviour，
    ///      它自己负责音频源、解码调度、防爆音淡入 —— 切场景不会断音；
    ///    · <c>MusicPlayerWindow</c> 挂在同一个宿主下面，每帧自己轮询 F3，
    ///      因此**不需要任何 Harmony 补丁**，也就不会影响别的系统。
    ///
    ///    关键顺序：两个类内部都是"先 <c>ClassInjector.RegisterTypeInIl2Cpp</c> 再 AddComponent"，
    ///    这一步不能省（IL2CPP 下没注册的类型 AddComponent 会抛）。
    ///
    ///    整个函数包 try/catch —— 音乐播放器坏了不能让整个模组加载失败。
    /// </summary>
    private static void InitializeMusicPlayer()
    {
        try
        {
            UI.MusicPlayer.MusicPlayer.EnsureInitialized();

            var host = UI.MusicPlayer.MusicPlayer.Instance;
            UI.MusicPlayer.MusicPlayerWindow.TryCreate(host != null ? host.transform : null);

            // ⚠️ 这个方法是 static —— BepInEx 的 Log 是实例属性，所以只能用 StaticLog
            //    （Load() 一开始就把 Log 存进 StaticLog 了，见最上面）
            StaticLog.LogInfo("[LightPlugin] 音乐播放器已就绪（按 F3 开关）");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[LightPlugin.InitializeMusicPlayer]", ex);
        }
    }
    private static void RegisterShowModStampOnMainMenu()
    {
        try
        {
            SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)((scene, mode) =>
            {
                try
                {
                    if (scene.name == "MainMenu")
                        ModManager.Instance.ShowModStamp();
                }
                catch (Exception ex)
                {
                    LightLogger.LogError("[LightPlugin.ShowModStampOnMainMenu]", ex);
                }
            }));
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[LightPlugin.RegisterShowModStampOnMainMenu]", ex);
        }
    }

    private static void AddCursorComponent()
    {
        try
        {
            Light.Config.Cursor.Initialize();
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[LightPlugin.AddCursorComponent]", ex);
        }
    }

    /// <summary>
    /// 解压语言文件到游戏目录 Language 文件夹。
    /// 文件不存在 → 直接写出；已存在 → 只补充缺失的词条（不覆盖用户已有的翻译）。
    /// 这样新增职业的本地化词条会随版本更新自动合并进旧语言文件。
    /// </summary>
    private static void ExtractLanguageFiles()
    {
        try
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Language");
            Directory.CreateDirectory(folder);
            var asm = typeof(LightPlugin).Assembly;
            foreach (var res in new[] { "Light.Resources.Language.SChinese.json", "Light.Resources.Language.English.json" })
            {
                string fileName = res.Substring(res.LastIndexOf('.', res.LastIndexOf('.') - 1) + 1);
                string path = Path.Combine(folder, fileName);

                Dictionary<string, string> embedded;
                using (var stream = asm.GetManifestResourceStream(res))
                {
                    if (stream == null) continue;
                    using var reader = new StreamReader(stream);
                    embedded = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
                }
                if (embedded == null || embedded.Count == 0) continue;

                if (!File.Exists(path))
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(embedded, new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
                    continue;
                }

                // 已存在：合并缺失词条
                var disk = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, System.Text.Encoding.UTF8));
                if (disk == null) { disk = new Dictionary<string, string>(); }
                int added = 0;
                foreach (var kv in embedded)
                {
                    if (!disk.ContainsKey(kv.Key)) { disk[kv.Key] = kv.Value; added++; }
                }
                if (added > 0)
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(disk, new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
                    StaticLog.LogInfo($"语言文件 {fileName} 合并了 {added} 条新词条");
                }
            }
        }
        catch (Exception ex)
        {
            StaticLog.LogWarning($"解压语言文件失败：{ex.Message}");
        }
    }

    private static void LoadCommand()
    {
        try
        {
            var harmony = new Harmony("Light.cmd.harmony");
            var orig = AccessTools.Method(typeof(ChatController), "SendChat");
            if (orig == null) return;
            var prefixMethod = AccessTools.Method(typeof(PatchManager), nameof(PatchManager.OnSendChat));
            if (prefixMethod == null) return;
            harmony.Patch(orig, new HarmonyMethod(prefixMethod));
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[LightPlugin.LoadCommand]", ex);
        }
    }
    private void LoadRole()
    {
        try
        {
            RoleRegistry.RegisterAssembly(typeof(LightPlugin).Assembly);
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[LightPlugin.LoadRole]", ex);
        }
    }
    public static class FirstChanceExceptionLogger
    {
        static bool _init = false;
        static readonly object _lock = new();
        public static void Initialize()
        {
            if (_init) return;
            lock (_lock)
            {
                if (_init) return;
                AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
                {
                    try
                    {
                        string dir = @"D:\log";
                        if(!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        string name = $"FIRST_CHANCE{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt";
                        string pathF = Path.Combine(dir, name);
                        File.WriteAllText(pathF,e.Exception.ToString());
                    }
                    catch(Exception ex)
                    {
                        LightLogger.LogError($"FirstChance捕获失败",ex);
                    }
                };
                _init = true;
            }
        }
    }
}

/// <summary>
/// 当颜色配置无效时抛出。
/// </summary>
public class InvalidColorTypeException : Exception
{
    public InvalidColorTypeException(string message) : base(message) { }
    public InvalidColorTypeException(string message, Exception innerException) : base(message, innerException) { }
}

