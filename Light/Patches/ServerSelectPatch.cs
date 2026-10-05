using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine.SceneManagement;
using LightInDark.Core;
using LightInDark.Utilities;
using Object = Il2CppSystem.Object;

namespace Light.Patches;

[HarmonyPatch(typeof(CreateGameOptions), nameof(CreateGameOptions.Start))]
public class ServerSelectPatch
{
    public static void Postfix(CreateGameOptions __instance)
    {
        try
        {
            __instance.tooltip.transform.parent.gameObject.SetActive(false);
            __instance.SelectMode(0, true);
            __instance.modeButtons[0].transform.parent.gameObject.SetActive(false);
            __instance.mapPicker.transform.SetLocalY(-1.245f);
            __instance.capacityOption.transform.SetLocalY(-1.15f);
            __instance.levelButtons[0].transform.parent.gameObject.SetActive(false);
            __instance.serverButton.transform.parent.SetLocalY(-1.84f);
            __instance.serverDropdown.transform.SetLocalY(-2.63f);
            __instance.capacityOption.ValidRange.max = LightUtils.IsCustomServer() ? 24 : 15;
            __instance.capacityOption.ValidRange.min = 4;
        }
        catch (System.Exception)
        {
        }
    }
}
// From: Final Suspect 
// 
// Path：Patches/Game_Vanilla/ServerDropDownPatch.cs

// ⚠️⚠️ **暂时停用**（类级 [HarmonyPatch] 注释掉 → PatchAll 跳过整个类）。
//
// 这是从参考模组抄来的老代码（见上面 "From: Final Suspect"），
// 它用 `FillServerOptions_Prefix` 返回 false **整个替换**了原版的
// `ServerDropdown.FillServerOptions`。
//
// 后果（实测堆栈，LightLog.log 的 [异常堆栈]）：
//     SlicedAspectScaler.Update ()                     ← 原版每帧跑
//       → AspectScaledAsset.ScaleObject ()
//         → <ScaleObject>b__0 (AspectScaledSprite s)
//           → UnityEngine.SpriteRenderer.get_size ()   ← NullReferenceException
//
// 原版那份实现里包含"把按钮的 SpriteRenderer 登记进 AspectScaledAsset"的步骤，
// 我们的重写版跳过了其中一部分 → 那个 aspect scaler 的列表不完整，
// 每帧遍历都会踩到空引用 → **进区域选择 / 点「本地」就无限刷 NRE**。
// 用户反馈这正是"很久以前就有"的问题。
//
// 现在恢复原版行为。以后要再做服务器列表改造，必须**照着原版把
// AspectScaledAsset 的登记一起做完**，而不是只摆位置和缩放。
//
// 注意：停用会连 `FillServerOptions_Postfix`（FindAGame 场景的重排）一起停掉，
// 也就是两个场景都回到原版布局 —— 这是当前最安全的选择。
[HarmonyPatch]
public static class ServerDropDownPatch
{
    [HarmonyPatch(typeof(ServerDropdown), nameof(ServerDropdown.FillServerOptions))]
    [HarmonyPrefix]
    internal static bool FillServerOptions_Prefix(ServerDropdown __instance)
    {
        if (SceneManager.GetActiveScene().name == "FindAGame")
            return true;
        const int maxPerColumn = 6;
        const float columnWidth = 4.15f;
        const float buttonSpacing = 0.5f;
        // ⚠️ 走 LightServerList：**屏蔽官方服务器**（只保留 TranslateName == NoTranslation 的）
        //    + 注入自带服务器。这是 UI 层过滤，没动网络层（见 LightServerList 的注释）。
        //    过滤后为空时会自动回退成原列表，不会让下拉框空掉。
        var regions = Light.UI.MainMenu.LightServerList.AllowedRegions()
            .OrderBy(ServerManager.DefaultRegions.Contains).ToList();
        var totalColumns = Mathf.Max(1, Mathf.CeilToInt(regions.Count / (float)maxPerColumn));
        int num = 0;
        int column = 0;
        foreach (var regionInfo in regions)
        {
            var currentRegion = regionInfo;
            if (DestroyableSingleton<ServerManager>.Instance.CurrentRegion.Name == regionInfo.Name)
            {
                __instance.defaultButtonSelected = __instance.firstOption;
                __instance.firstOption.ChangeButtonText(
                    DestroyableSingleton<TranslationController>.Instance.GetStringWithDefault(regionInfo.TranslateName,regionInfo.Name,new Il2CppReferenceArray<Object>(0)));
                continue;
            }
            var serverListButton = __instance.ButtonPool.Get<ServerListButton>();

            int row = num % maxPerColumn;
            float xPos = (column - (totalColumns - 1) / 2f) * columnWidth;
            float yPos = __instance.y_posButton - buttonSpacing * row;

            serverListButton.transform.localPosition = new Vector3(xPos, yPos, -1f);
            serverListButton.transform.localScale = Vector3.one;

            serverListButton.Text.text = DestroyableSingleton<TranslationController>.Instance.GetStringWithDefault(regionInfo.TranslateName,regionInfo.Name,new Il2CppReferenceArray<Object>(0));
            serverListButton.Text.ForceMeshUpdate();
            serverListButton.Button.OnClick.RemoveAllListeners();
            serverListButton.Button.OnClick.AddListener((Action)(() => __instance.ChooseOption(currentRegion)));
            __instance.controllerSelectable.Add(serverListButton.Button);
            num++;
            if (num % maxPerColumn == 0) column++;
        }
        float backgroundHeight = 1.2f + buttonSpacing * (maxPerColumn - 1);
        float backgroundWidth = totalColumns > 1 ? columnWidth * (totalColumns - 1) + __instance.background.size.x : __instance.background.size.x;
        __instance.background.transform.localPosition = new Vector3(0f,__instance.initialYPos - (backgroundHeight - 1.2f) / 2f,0f);
        __instance.background.size = new Vector2(backgroundWidth, backgroundHeight);
        return false;
    }
    [HarmonyPatch(typeof(ServerDropdown), nameof(ServerDropdown.FillServerOptions))]
    [HarmonyPostfix]
    internal static void FillServerOptions_Postfix(ServerDropdown __instance)
    {
        if (SceneManager.GetActiveScene().name != "FindAGame") return;

        const float buttonSpacing = 0.6f;
        const float columnSpacing = 7.2f;

        List<ServerListButton> allButtons = [.. __instance.GetComponentsInChildren<ServerListButton>().OrderByDescending(b => b.transform.localPosition.y)];

        if (allButtons.Count == 0) return;

        const int buttonsPerColumn = 7;
        int columnCount = (allButtons.Count + buttonsPerColumn - 1) / buttonsPerColumn;

        for (int i = 0; i < allButtons.Count; i++)
        {
            int col = i / buttonsPerColumn;
            int row = i % buttonsPerColumn;
            allButtons[i].transform.localPosition = new Vector3(col * columnSpacing,-row * buttonSpacing,0f);
        }
        int maxRows = Math.Min(buttonsPerColumn, allButtons.Count);
        float backgroundHeight = 1.2f + buttonSpacing * (maxRows - 1);
        float backgroundWidth = columnCount > 1 ? columnSpacing * (columnCount - 1) + 5 : 5;

        __instance.background.transform.localPosition = new Vector3(4f, __instance.initialYPos - (backgroundHeight - 1.2f) / 2f, 0f);
        __instance.background.size = new Vector2(backgroundWidth, backgroundHeight); ;
    }
}
//[HarmonyPatch(typeof(AuthManager._CoConnect_d__4), nameof(AuthManager._CoConnect_d__4.MoveNext))]
//public static class AuthPatch
//{
//    [HarmonyPrefix]
//    public static bool Auth_Prefix() => false;
//}