using HarmonyLib;
using Light.UI.DebugTools;

namespace Light.Patches
{
    // ---------------------------------------------------------------------
    //  ⚠️⚠️ 暂时停用。类级 [HarmonyPatch] 被**故意注释掉**
    //      → PatchAll 会跳过整个类，这个每帧驱动目前**完全不生效**。
    //
    //  原因：用户报「进本地场景日志一直刷屏空引用，而且本地进不去」。
    //        排查时所有日志文件都停在部署之前、ErrorLog.log 是 0 字节
    //        —— 刷屏期间日志根本没落盘，说明游戏被拖住了。
    //        而本工程里**唯一在游戏内每帧跑**的代码就是下面这个 postfix，
    //        且这个功能从做出来到现在**从未被确认跑起来过**。
    //
    //  先摘掉，让用户验证刷屏是否消失：
    //    消失了 → 就是它，下一轮按下面的防护重做
    //    还在   → 换方向查，别在它身上耗
    //
    //  重做时必须带上的防护（这一版没有）：
    //    ① **不要在场景切换的头几帧跑** —— HudManager 刚出现时整棵场景树还在建，
    //       这时去 Create UI / 找字体容易踩到半初始化状态
    //    ② **熔断**：连续失败 N 次就永久停掉，绝不能每帧重试
    //    ③ 失败**只记一次日志**，不要每帧刷
    //    ④ 别在每帧路径上调 FindObjectsOfTypeIncludingAssets 这类全资源扫描
    // ---------------------------------------------------------------------
    // [HarmonyPatch(typeof(HudManager))]
    public static class RoleDebugPanelPatch
    {
        // [HarmonyPatch(nameof(HudManager.Update))]
        // [HarmonyPostfix]
        public static void Postfix()
        {
            RoleDebugPanel.Tick();
        }
    }
}
