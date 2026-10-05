using System;
using HarmonyLib;
using LightInDark.Core;

namespace Light.Patches
{
    /// <summary>
    /// 止住原版 <c>"Waiting for time stamp"</c> 的刷屏 —— 用**原版公开 API**,不碰 <c>Debug.Log</c>。
    ///
    /// ---- 刷屏从哪来(19.0 源码) ----
    /// <code>
    /// // PlayerParticles.cs:16-22
    /// private IEnumerator CoWaitForDateConfirmation()
    /// {
    ///     while (!DestroyableSingleton&lt;EOSManager&gt;.Instance.HasServerTimestamp)
    ///     {
    ///         Debug.Log("Waiting for time stamp");     // ← 每帧一行
    ///         yield return null;
    ///     }
    ///     this.fill = new RandomFill&lt;PlayerParticleInfo&gt;();   // ← 时间戳不来，这里永不执行
    ///     ...
    /// }
    ///
    /// // EOSManager.cs:202
    /// public bool HasServerTimestamp =&gt; this.serverTimeOnLaunch != DateTime.MinValue;
    ///
    /// // EOSManager.cs:1508  ← **public**
    /// public void SetServerTimeStamp(DateTime utcTime) { this.serverTimeOnLaunch = utcTime; }
    /// </code>
    ///
    /// 本机 EOS 登录异常（日志里的 <c>DeviceId access credentials not found</c>、
    /// <c>KWS GetPermissions 404 user_not_found</c>），时间戳永远不来 →
    /// 这个循环**每帧刷一行、永不停止**，把日志彻底淹掉；
    /// 同时粒子因为永远卡在等待，<c>fill</c> 从没被赋值 → 粒子本身也是坏的。
    ///
    /// ---- 为什么这次不像上次那样 patch Debug.Log ----
    /// <c>Debug.Log</c> 是 **BepInEx 日志系统的入口**：我们 patch 它以后，
    /// 自己打的每条日志又会回到 patch → **递归**，
    /// 导致 <c>Harmony.PatchAll()</c> 之后的初始化永远跑不完 → **游戏卡在启动页**（实测踩过）。
    /// 这条路彻底放弃。
    ///
    /// ---- 现在的做法 ----
    /// 在 <c>PlayerParticles.Start</c> 的 Postfix 里，如果发现时间戳还没到，
    /// 就**调用原版的公开方法补一个 <c>DateTime.UtcNow</c>**。
    ///
    /// 三个好处：
    /// ① <c>HasServerTimestamp</c> 变 true → 循环立刻退出 → **一帧之后不再刷屏**
    /// ② 粒子能正常初始化（<c>fill</c> 被赋值）→ **顺带修好粒子**（原本一直是坏的）
    /// ③ <c>ApproximateServerTime</c> = <c>serverTimeOnLaunch.AddSeconds(realtimeSinceStartup)</c>
    ///    在没有时间戳时是 <c>DateTime.MinValue + 秒数</c>，**可能直接抛
    ///    ArgumentOutOfRangeException**（<c>AprilFoolsMode.IsImpostorMonth</c> 走的正是这条）——
    ///    补上之后这个隐患也一并消失
    ///
    /// ⚠️ **是自愈的**：EOS 之后如果真拿到服务器时间戳，原版会再调一次
    /// <c>SetServerTimeStamp</c> 把我们的估值覆盖成真值，不需要我们清理。
    ///
    /// 时序说明：<c>StartCoroutine</c> 会**立刻**执行到第一个 <c>yield</c>，
    /// 所以 Postfix 跑的时候第一条日志已经打出去了 —— 也就是**最多残留 1 行**，之后干净。
    /// </summary>
    [HarmonyPatch(typeof(PlayerParticles))]
    public static class EosTimestampFixPatch
    {
        private static bool _reported;

        [HarmonyPatch(nameof(PlayerParticles.Start))]
        [HarmonyPostfix]
        public static void Start_Postfix(PlayerParticles __instance)
        {
            try
            {
                var eos = DestroyableSingleton<EOSManager>.Instance;
                if (eos == null) return;
                if (eos.HasServerTimestamp) return;     // 真拿到了，什么都不做

                // ⚠️ interop 里原版签名是 Il2CppSystem.DateTime（不是 System.DateTime）。
                //    它有 .ctor(Int64 ticks)，用 UtcNow 的 ticks 构造即可。
                eos.SetServerTimeStamp(new Il2CppSystem.DateTime(DateTime.UtcNow.Ticks));   // 原版公开 API

                if (!_reported)
                {
                    _reported = true;
                    LightLogger.Log(
                        "[EOSTimestampFix] EOS 服务器时间戳一直没到（本机 EOS 登录异常），" +
                        "已用原版公开 API SetServerTimeStamp(DateTime.UtcNow) 补了一个估值。\n" +
                        "  → 原版 PlayerParticles 的 \"Waiting for time stamp\" 刷屏到此为止\n" +
                        "  → 粒子也能正常初始化了（原本永远卡在等待、fill 从未赋值）\n" +
                        "  → 若 EOS 之后真拿到时间戳，原版会再调一次覆盖成真值，无需清理");
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogWarning($"[EOSTimestampFix] 失败(忽略): {ex.Message}");
            }
        }
    }
}
