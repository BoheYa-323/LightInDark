using HarmonyLib;
using Light.UI.DebugTools;

namespace Light.Patches
{
    /// <summary>
    /// 每帧驱动 <see cref="RoleDebugPanel"/>（自由模式里的「选择职业」按钮）。
    ///
    /// ⚠️ 挂在 <c>HudManager.Update</c> 上：这个按钮只在**游戏内**（自由模式）才有意义，
    ///    而 HudManager 正好是"在游戏里"才存在、且每帧都会跑的对象。
    ///    主界面那套 LateUpdate 驱动（MainMenuPatch）只在主菜单有效，这里用不上。
    ///
    /// AGENTS.md §4.1：类级必须有 [HarmonyPatch]，否则整个类会被 PatchAll 静默跳过。
    /// </summary>
    [HarmonyPatch(typeof(HudManager))]
    public static class RoleDebugPanelPatch
    {
        [HarmonyPatch(nameof(HudManager.Update))]
        [HarmonyPostfix]
        public static void Postfix()
        {
            RoleDebugPanel.Tick();
        }
    }
}
