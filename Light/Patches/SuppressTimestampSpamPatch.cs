using HarmonyLib;
using LightInDark.Core;

namespace Light.Patches
{
    /// <summary>
    /// 过滤掉原版主界面粒子的刷屏日志 <c>"Waiting for time stamp"</c>。
    ///
    /// ---- 它从哪来 ----
    /// Among Us 19.0 <c>PlayerParticles.cs</c>：
    /// <code>
    /// private IEnumerator CoWaitForDateConfirmation()
    /// {
    ///     while (!DestroyableSingleton&lt;EOSManager&gt;.Instance.HasServerTimestamp)
    ///     {
    ///         Debug.Log("Waiting for time stamp");     // ← L20，每帧一行
    ///         yield return null;
    ///     }
    ///     this.fill = new RandomFill&lt;PlayerParticleInfo&gt;();
    ///     ...
    /// }
    /// </code>
    /// <c>EOSManager.HasServerTimestamp</c>（EOSManager.cs:202）就是
    /// <c>serverTimeOnLaunch != DateTime.MinValue</c> —— **EOS 服务器时间戳到了才为 true**。
    ///
    /// 本机 EOS 登录异常（日志里的 `DeviceId access credentials not found`、
    /// `KWS GetPermissions 404 user_not_found`），时间戳永远不来 →
    /// 这个循环**每帧刷一行，永不停止**，把日志彻底淹掉。
    ///
    /// ---- 为什么不直接把 HasServerTimestamp 写死成 true ----
    /// 它还有两个使用点：<c>AprilFoolsMode.cs:23</c> 和 <c>:67</c>。
    /// 写死 true 会顺带改变愚人节模式的判定行为，副作用不可控。
    /// 而且 <c>HasServerTimestamp</c> 是**属性**，Harmony 在 IL2CPP 下对属性访问器的
    /// patch 也不一定稳。
    ///
    /// ---- 所以 ----
    /// 只拦 **`Debug.Log(object)` 里那一条**：字符串完全相等才吞掉，其它日志一律放行。
    /// 行为零改动（那个协程该怎么等还怎么等），只是日志干净了。
    ///
    /// ⚠️ `Debug.Log` 是热路径（每次日志都会过这里），所以 prefix 里只做一次字符串比较，
    ///    并且整个包在 try 里 —— 出任何意外都放行，绝不让过滤本身成为新问题。
    /// </summary>
    // ⚠️⚠️ **暂时停用**：类级 [HarmonyPatch] 被故意注释掉 → PatchAll 会跳过整个类。
    //    它 patch 的是 `UnityEngine.Debug.Log(object)`，**这是 Unity 日志系统的入口**。
    //    实测：一挂上它，`LightPlugin.Load()` 就在 Harmony.PatchAll() 之后抛异常，
    //    导致 `LightSettingsData` 永远没被赋值 → LoadPatch.Prefix 每帧 NRE
    //    → **卡在启动页进不去游戏**。
    //    换句话说：拿日志过滤去换游戏启动，不值。等找到不碰 Debug.Log 的办法再说。
    // [HarmonyPatch(typeof(UnityEngine.Debug))]
    public static class SuppressTimestampSpamPatch
    {
        /// <summary>要吞掉的那条原版日志（一字不差）。</summary>
        private const string SpamMessage = "Waiting for time stamp";

        /// <summary>已经吞掉多少条（只为日志里报一次，避免自己又刷屏）。</summary>
        private static int _suppressed;
        private static bool _reported;

        // [HarmonyPatch(nameof(UnityEngine.Debug.Log), new[] { typeof(object) })]
        // [HarmonyPrefix]
        public static bool Prefix(object message)
        {
            try
            {
                // 绝大多数日志走的是"不是这个字符串"的快路径
                if (message is not string s || s.Length != SpamMessage.Length) return true;
                if (s != SpamMessage) return true;

                _suppressed++;

                // 第一次吞掉时报一行，让用户知道过滤生效了（之后不再打扰）
                if (!_reported)
                {
                    _reported = true;
                    LightLogger.Log("[LogFilter] 已开始过滤原版刷屏日志 \"Waiting for time stamp\"" +
                                    "（EOS 服务器时间戳没到，原版 PlayerParticles 每帧刷一行）");
                }

                return false;   // 吞掉
            }
            catch
            {
                return true;    // 过滤本身出错就放行，绝不吞掉正常日志
            }
        }

        /// <summary>被过滤掉的条数（诊断用）。</summary>
        public static int SuppressedCount => _suppressed;
    }
}
