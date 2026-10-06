using System;
using Il2CppInterop.Runtime.Injection;
using LightInDark.Core;
using UnityEngine;

namespace Light.Utilities
{
    /// <summary>
    /// **跨场景常驻的每帧驱动器。**
    ///
    /// ---- 为什么需要它（2026-10-05 实机定位）----
    /// 之前所有"每帧要做的事"（背景图跟随相机、视频音量淡入、按钮贴图守卫、
    /// 模态遮罩扫描、按钮悬浮效果…）全部挂在
    /// <code>
    /// [HarmonyPatch(typeof(MainMenuManager), "LateUpdate")]
    /// [HarmonyPostfix]
    /// public static void LateUpdate()
    /// </code>
    /// 这一个补丁上。而 **<c>MainMenuManager</c> 只存在于 <c>MainMenu</c> 场景** ——
    /// <c>MatchMaking</c>（"本地"/区域选择）的主控是 <c>MMOnlineManager</c>，
    /// <c>FindAGame</c>（"搜索游戏"）又是另一个。
    ///
    /// 后果：那个 <c>LateUpdate</c> 里写的
    /// <c>sceneName == "MatchMaking" || sceneName == "FindAGame"</c> 全是**死代码** ——
    /// 补丁在那两个场景里根本不会触发。用户报的
    /// 「打开本地/搜索游戏后没有背景图、也没有音频」就是这么来的：
    /// **不是被关掉了，是从没启动过。**
    ///
    /// ---- 做法 ----
    /// 挂一个 <c>DontDestroyOnLoad</c> 的 MonoBehaviour，在它自己的 <c>Update()</c> 里
    /// 调 <see cref="Light.Patches.MainMenuPatch.TickAll"/>。
    /// 这样无论当前是哪个场景都有人驱动。
    ///
    /// ⚠️ **不会和原来那个补丁重复执行**：<see cref="Light.Patches.MainMenuPatch.TickAll"/>
    ///    开头有"同一帧只跑一次"的守卫（<c>Time.frameCount</c> 比对），
    ///    所以 MainMenu 场景里两个驱动源同时调也只生效一次。
    ///
    /// ⚠️ 用 <c>Update</c> 而不是 <c>LateUpdate</c>：原来那个是 <c>LateUpdate</c> 的
    ///    Postfix（在原版逻辑之后跑）。这里两种时机都试过——用 <c>LateUpdate</c> 更贴近原行为，
    ///    但 <c>Update</c> 能保证"背景图先摆好、其他系统的 LateUpdate 再覆盖"的顺序更稳。
    ///    实测若出现"背景一帧闪"，把这里改成 <c>LateUpdate</c> 即可。
    /// </summary>
    public sealed class LightTicker : MonoBehaviour
    {
        private static LightTicker? _instance;

        /// <summary>常驻对象名（重复创建时用它去重）。</summary>
        private const string ObjectName = "LID_Ticker";

        /// <summary>创建常驻驱动器。重复调用只创建一次。</summary>
        public static void Ensure()
        {
            try
            {
                if (_instance != null) return;

                // 场景里可能已经有（例如热重载后），先找一遍
                var existing = GameObject.Find(ObjectName);
                if (existing != null)
                {
                    _instance = existing.GetComponent<LightTicker>();
                    if (_instance != null) return;
                }

                // ⚠️⚠️ **必须先把这个托管类型注册进 IL2CPP，否则 AddComponent 会抛。**
                //
                //  2026-10-05 实机日志（LightLog.log）里的真实堆栈：
                //      [Error] "[LightTicker.Ensure]"
                //      System.TypeInitializationException: The type initializer for
                //        'MethodInfoStoreGeneric_AddComponent_Public_T_0`1' threw an exception.
                //       ---> System.NullReferenceException
                //         at UnityEngine.GameObject.AddComponent[T]()
                //         at Light.Utilities.LightTicker.Ensure() ... LightTicker.cs:line 66
                //
                //  后果：`Ensure()` 被自己的 try/catch 吃掉、`LightPlugin` 那层
                //  `try { ... } catch { }` 再看不出异常 → **驱动器从来没被创建过**，
                //  日志里也永远没有「[LightTicker] 跨场景每帧驱动器已启动」这一行。
                //  MatchMaking / FindAGame 里 MainMenuManager 不存在 → `TickAll()` 一次都不跑
                //  → 用户报的「本地 / 搜索游戏没有背景图」。
                //
                //  本工程其它自建 MonoBehaviour（MusicPlayer / MusicPlayerWindow / Dispatcher /
                //  TextFieldBehaviour / ConfigRowDriver / MetaScreen）**都是先注册再 AddComponent**，
                //  只有 LightTicker 漏了这一步 —— 这就是本次的根因。
                try
                {
                    ClassInjector.RegisterTypeInIl2Cpp<LightTicker>();
                }
                catch (Exception ex)
                {
                    // 已注册过会抛，忽略即可（和 MusicPlayer 一样的写法）
                    LightLogger.LogDebug($"[LightTicker] 类型注册（可能已注册）：{ex.Message}");
                }

                var go = new GameObject(ObjectName);
                UnityEngine.Object.DontDestroyOnLoad(go);   // ← 跨场景常驻的关键

                try
                {
                    _instance = go.AddComponent<LightTicker>();
                }
                catch (Exception ex)
                {
                    // 补注册重试一次（写法同 LightUtils.AttachComponent / ConfigUIPanel.AddComponentSafe）
                    LightLogger.LogWarning($"[LightTicker] AddComponent 失败（{ex.Message}），补注册后重试");
                    try { ClassInjector.RegisterTypeInIl2Cpp<LightTicker>(); } catch { }
                    try
                    {
                        _instance = go.AddComponent<LightTicker>();
                    }
                    catch (Exception ex2)
                    {
                        // 第二次也失败：这里**不再往上抛**，走下面的显式报错，
                        // 免得只留一条光秃秃的 TypeInitializationException 看不出后果。
                        LightLogger.LogError("[LightTicker] AddComponent 重试仍失败", ex2);
                    }
                }

                if (_instance == null)
                {
                    LightLogger.LogError("[LightTicker] AddComponent 返回 null —— 跨场景每帧驱动器**未启动**，" +
                                         "MatchMaking / FindAGame 场景里的背景与每帧逻辑都不会运行");
                    return;
                }

                LightLogger.LogDebug("[LightTicker] 跨场景每帧驱动器已启动");
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[LightTicker.Ensure]", ex);
            }
        }

        /// <summary>诊断：驱动器是否真的活着（排查"每帧逻辑在别的场景不跑"用）。</summary>
        public static bool IsRunning
        {
            get
            {
                try { return _instance != null; }
                catch { return false; }
            }
        }

        private void Update()
        {
            try
            {
                // 带上驱动源名字 —— 诊断日志要能区分"补丁驱动"和"常驻驱动器"到底谁在跑
                Light.Patches.MainMenuPatch.TickAll("LightTicker.Update");
            }
            catch (Exception ex)
            {
                // 绝不让异常逃出去打断游戏主循环
                LightLogger.LogError("[LightTicker.Update]", ex);
            }
        }

        private void OnDestroy()
        {
            // 只有游戏退出/主动销毁才会走到这
            if (_instance == this) _instance = null;
        }
    }
}
