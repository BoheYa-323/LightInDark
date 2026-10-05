using HarmonyLib;
using InnerNet;
using LightInDark.Utilities;

namespace LightInDark.Patches;

// ⚠️ 暂时停用（类级特性注释掉 → PatchAll 跳过整个类）：见下方说明
// [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal))]
public static class DisconnectInternalPatch
{
    public static void Prefix(InnerNetClient __instance, ref DisconnectReasons reason)
    {
        if (reason == DisconnectReasons.Kicked)
        {
            string pendingReason = KickHelper.ConsumePendingReason(__instance.ClientId);
            if (!string.IsNullOrEmpty(pendingReason))
            {
                __instance.LastCustomDisconnect = pendingReason;
                reason = DisconnectReasons.Custom;
            }
        }
    }
}