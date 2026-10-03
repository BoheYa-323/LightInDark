using System;
using HarmonyLib;
using LightInDark.Configuration;
using LightInDark.Core;
using TMPro;
using UnityEngine;

namespace Light.UI.Config
{
    /// <summary>
    /// 把克隆行的原版交互接回我们的配置项。
    ///
    /// ⚠️ 与上一版的区别（重要）：
    ///   现在每行都通过 <c>SetUpFromData(合成的 BaseGameSetting)</c> 初始化过，
    ///   所以原版自己的 <c>Initialize</c> / <c>FixedUpdate</c> / <c>UpdateValue</c> 是**正确工作**的，
    ///   **不能再拦截它们** —— 拦了反而什么都不显示（上一版"建了行但看不见"的原因之一）。
    ///
    ///   这里只做两件事：
    ///     1. 取得模板源（GameOptionsMenu 持有那些私有预制体字段）；
    ///     2. 用户点 +/−/勾选 时，把值同步回 <see cref="ConfigItem"/>（原版只会改它自己的 setting）。
    /// </summary>
    [HarmonyPatch]
    public static class ConfigRowPatches
    {
        // =====================================================================
        //  模板源：GameOptionsMenu 持有那些私有预制体字段
        // =====================================================================

        [HarmonyPatch(typeof(GameOptionsMenu), nameof(GameOptionsMenu.Initialize))]
        [HarmonyPostfix]
        public static void InitializePostfix(GameOptionsMenu __instance)
        {
            try
            {
                ConfigUIPanel.SetTemplateSource(__instance);
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigRowPatches.InitializePostfix]", ex);
            }
        }

        /// <summary>
        /// 【已废弃的补丁，保留说明】之前为修栈溢出拦过 OpenMenu/CloseMenu。
        ///
        /// 后来按 TONE 的做法**直接接管 GameSettingMenu.ChangeTab**（Prefix + return false，
        /// 见 GameSettingMenuPatch.ChangeTabPrefix），原版 ChangeTab 整个不执行，
        /// 它内部对 GameOptionsMenu.OpenMenu / OnDisable→CloseMenu 的调用链
        /// **根本不会被触发** —— 栈溢出从根上消失了。
        ///
        /// 所以这里不再需要拦 OpenMenu/CloseMenu：
        ///   拦它们只是治标（而且我上一版用名字判定还失效了），
        ///   接管 ChangeTab 才是治本（TONE\Patches\GameSettingMenuPatch.cs:325-411）。
        ///
        /// 登记表保留，供"我们克隆的菜单"识别使用。
        /// </summary>
        private static readonly HashSet<int> _ourMenuIds = new();

        /// <summary>登记一个"我们克隆的"GameOptionsMenu（创建时调用）。</summary>
        public static void RegisterOurMenu(GameOptionsMenu menu)
        {
            if (menu == null) return;
            _ourMenuIds.Add(menu.GetInstanceID());
        }

        /// <summary>取消登记（菜单销毁时调用）。</summary>
        public static void UnregisterOurMenu(GameOptionsMenu menu)
        {
            if (menu == null) return;
            _ourMenuIds.Remove(menu.GetInstanceID());
        }

        private static bool IsOurMenu(GameOptionsMenu? menu)
            => menu != null && _ourMenuIds.Contains(menu.GetInstanceID());

        // 说明：不再有 OpenMenu/CloseMenu 的 Prefix。
        // 栈溢出的根治方式是接管 GameSettingMenu.ChangeTab（见 GameSettingMenuPatch），
        // 而不是在外围拦 OpenMenu/CloseMenu（那样治标不治本）。

        // =====================================================================
        //  【本次修复】拦住原版对"我们的行"的写值/刷值
        //
        //  用户反馈两个症状，根因是同一处：
        //    ① BE 控制台 `Could not update value of 9001`
        //    ② 调数值时"显示数字那块会闪字「生成假人的数量」"
        //
        //  原版源码：
        //    NumberOption.UpdateValue()（NumberOption.cs:131-144）
        //        if (floatOptionName != Invalid) SetFloat(...)
        //        if (intOptionName   != Invalid) SetInt(...)
        //        Debug.LogError("Could not update value of " + Title.ToString());   ← ①
        //    NumberOption.FixedUpdate()（NumberOption.cs:83-90）
        //        if (oldValue != Value) ValueText.text = data.GetValueString(Value);
        //
        //  我们的行是"合成"的 setting，OptionName 必然是 Invalid（我们**故意**不给它
        //  真实名字，否则 SetInt/SetBool 会把我们的调试数值写进**真实的游戏规则**里）。
        //  所以 UpdateValue 每次都走 Debug.LogError → 就是那条 9001（9001 是
        //  Title 的 StringNames 槽位号，Title.ToString() 得到的就是槽位数字）。
        //
        //  而 FixedUpdate 会拿 `data.GetValueString()` 去写 ValueText，与
        //  ConfigRowDriver 写的值**互相抢同一个 TMP** → 表现为"闪字"。
        //
        //  做法（ConfigRowDriver 的注释里本来就写了"显示完全由这里负责"，
        //  但补丁一直没做全）：对**我们的行**直接跳过 UpdateValue 与 FixedUpdate，
        //  让显示与取值 100% 由我们的驱动器负责。原版行完全不受影响。
        //
        //  ⚠️ 只在 RowMap 里登记过的实例才拦（ItemOf 判定），原版设置行照旧。
        // =====================================================================

        private static bool IsOurRow(Component behaviour)
            => behaviour != null && ConfigUIPanel.ItemOf(behaviour.GetInstanceID()) != null;

        [HarmonyPatch(typeof(ToggleOption), "UpdateValue")]
        [HarmonyPrefix]
        public static bool ToggleUpdateValuePrefix(ToggleOption __instance) => !IsOurRow(__instance);

        [HarmonyPatch(typeof(NumberOption), "UpdateValue")]
        [HarmonyPrefix]
        public static bool NumberUpdateValuePrefix(NumberOption __instance) => !IsOurRow(__instance);

        [HarmonyPatch(typeof(StringOption), "UpdateValue")]
        [HarmonyPrefix]
        public static bool StringUpdateValuePrefix(StringOption __instance) => !IsOurRow(__instance);

        // FixedUpdate：原版会往 ValueText 抢写 → 跳过（显示由 ConfigRowDriver 负责）
        [HarmonyPatch(typeof(ToggleOption), nameof(ToggleOption.FixedUpdate))]
        [HarmonyPrefix]
        public static bool ToggleFixedUpdatePrefix(ToggleOption __instance) => !IsOurRow(__instance);

        [HarmonyPatch(typeof(NumberOption), nameof(NumberOption.FixedUpdate))]
        [HarmonyPrefix]
        public static bool NumberFixedUpdatePrefix(NumberOption __instance) => !IsOurRow(__instance);

        [HarmonyPatch(typeof(StringOption), nameof(StringOption.FixedUpdate))]
        [HarmonyPrefix]
        public static bool StringFixedUpdatePrefix(StringOption __instance) => !IsOurRow(__instance);

        // =====================================================================
        //  【本次修复】拦住原版 Initialize —— 它会拿**真实游戏规则**覆盖我们的值
        //
        //  用户反馈："里面的字依旧是生成假人的数量，还是要点两次才能变成未选中"。
        //
        //  原版源码（这三个 Initialize 都是被 Start() 调的，也就是**在我们驱动器写完之后**跑）：
        //    ToggleOption.Initialize()（ToggleOption.cs:29-33）
        //        TitleText.text = GetString(this.Title);
        //        CheckMark.enabled = GameOptionsManager.Instance.CurrentGameOptions
        //                                 .GetValue(this.data) == 1f;      // ← 读真实游戏规则！
        //    NumberOption.Initialize()（NumberOption.cs:72-80）
        //        TitleText.text = GetString(this.Title);
        //        if (this.data) this.Value = GameOptionsManager.Instance
        //                                 .CurrentGameOptions.GetValue(this.data);   // ← 同样覆盖我们的值
        //        AdjustButtonsActiveState();
        //
        //  而 `GetValue(this.data)` 是按"我们合成的 setting"去真实规则里取值 ——
        //  我们又给勾选框塞了 OptionName = VisualTasks 来消日志，于是它读到的
        //  就是**真实的可视任务开关**，而不是"启用调试模式"的值。结果：
        //    · 勾选框显示的状态来自真实游戏规则（不是我们的配置）→ 点一下才对不上，
        //      要点两次才回到"未选中"（就是用户报的这个现象）；
        //    · TitleText 被原版按 Title 又写了一遍 —— 如果这一行的 TitleText 与
        //      ValueText 指向同一个 TMP，数字框里就会显示成标题文字
        //      （"里面的字依旧是生成假人的数量"）。
        //
        //  处理：对我们的行整个跳过 Initialize。行的一切（标题、勾选态、数值、
        //  加减范围）全部由 ConfigRowDriver + SetUpFromData 决定，不再受真实规则影响。
        //  原版行不受影响。
        // =====================================================================

        [HarmonyPatch(typeof(ToggleOption), nameof(ToggleOption.Initialize))]
        [HarmonyPrefix]
        public static bool ToggleInitializePrefix(ToggleOption __instance) => !IsOurRow(__instance);

        [HarmonyPatch(typeof(NumberOption), nameof(NumberOption.Initialize))]
        [HarmonyPrefix]
        public static bool NumberInitializePrefix(NumberOption __instance) => !IsOurRow(__instance);

        [HarmonyPatch(typeof(StringOption), nameof(StringOption.Initialize))]
        [HarmonyPrefix]
        public static bool StringInitializePrefix(StringOption __instance) => !IsOurRow(__instance);

        /// <summary>
        /// 【借鉴 TONE】劫持原版 <c>GameOptionsMenu.CreateSettings</c>。
        ///
        /// 这是 ToN 的核心手法（TONE\Patches\GameOptionsMenuPatch.cs:54-63）：
        /// 原版在"打开某个设置页"时会调 CreateSettings 去铺行。ToN 用 Prefix
        /// **返回 false 跳过原版**，然后自己把行建到 <c>__instance.settingsContainer</c> 里。
        ///
        /// 为什么必须在这里做（我踩了很久的坑）：
        ///   · 我们之前挂在 ROLES TAB 下自建容器 → 该容器从未被原版初始化/滚动/布局，
        ///     父链 z 与缩放都不在原版体系内 → 行建出来了但**屏幕上什么都没有**；
        ///   · CreateSettings 是**原版唯一会铺行的时机**，此时 settingsContainer 已就绪、
        ///     页签也已被原版 SetActive(true) → 在这里建的行必然可见。
        ///
        /// 注意：我们**只在"MOD 页签"对应的情况下接手**（由 <see cref="ShouldHandleCreateSettings"/>
        /// 判断），其余情况原样跑原版，不影响原版三个页签。
        /// </summary>
        [HarmonyPatch(typeof(GameOptionsMenu), nameof(GameOptionsMenu.CreateSettings))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool CreateSettingsPrefix(GameOptionsMenu __instance)
        {
            try
            {
                ConfigUIPanel.SetTemplateSource(__instance);

                if (!ConfigUIPanel.ShouldHandleCreateSettings(__instance))
                    return true;   // 原版页签 → 跑原版逻辑

                LightLogger.Log($"[ConfigRowPatches] 接手 CreateSettings（{__instance.name}）");
                ConfigUIPanel.BuildIntoVanilla(__instance);
                return false;      // 跳过原版铺行，用我们自己的
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigRowPatches.CreateSettingsPrefix]", ex);
                return true;       // 出错就让原版自己跑，至少别把界面搞没
            }
        }

        // =====================================================================
        //  值变化 → 写回配置项
        //  用 Postfix：让原版先正常更新自己的 setting 与显示，我们再抄回配置项。
        // =====================================================================

        [HarmonyPatch(typeof(ToggleOption), nameof(ToggleOption.Toggle))]
        [HarmonyPostfix]
        public static void TogglePostfix(ToggleOption __instance)
        {
            var item = ConfigUIPanel.ItemOf(__instance.GetInstanceID());
            if (item == null) return;

            // 诊断：Toggle postfix 里 CheckMark 到底是什么状态。
            // 原版 Toggle() 先翻转 CheckMark 再调 UpdateValue/OnValueChanged，
            // 我们的 postfix 在其后执行，所以这里读到的应当是"翻转后"的值。
            if (_toggleLogs < 12)
            {
                _toggleLogs++;
                LightLogger.Log($"[ConfigRowPatches.Diag] TogglePostfix 行 {item.Key} " +
                                $"CheckMark={( __instance.CheckMark == null ? "null" : __instance.CheckMark.enabled.ToString())} " +
                                $"GetBool={__instance.GetBool()} " +
                                $"ItemValueBefore={item.Value}");
            }

            item.SetValueSilently(__instance.GetBool() ? 1f : 0f);
            AfterChange(item, __instance);
        }

        private static int _toggleLogs;

        [HarmonyPatch(typeof(NumberOption), nameof(NumberOption.Increase))]
        [HarmonyPostfix]
        public static void NumberIncreasePostfix(NumberOption __instance)
        {
            var item = ConfigUIPanel.ItemOf(__instance.GetInstanceID());
            if (item == null) return;

            item.SetValueSilently(__instance.GetFloat());
            AfterChange(item, __instance);
        }

        [HarmonyPatch(typeof(NumberOption), nameof(NumberOption.Decrease))]
        [HarmonyPostfix]
        public static void NumberDecreasePostfix(NumberOption __instance)
        {
            var item = ConfigUIPanel.ItemOf(__instance.GetInstanceID());
            if (item == null) return;

            item.SetValueSilently(__instance.GetFloat());
            AfterChange(item, __instance);
        }

        [HarmonyPatch(typeof(StringOption), nameof(StringOption.Increase))]
        [HarmonyPostfix]
        public static void StringIncreasePostfix(StringOption __instance)
        {
            var item = ConfigUIPanel.ItemOf(__instance.GetInstanceID());
            if (item == null) return;

            item.SetValueSilently(__instance.Value);
            AfterChange(item, __instance);
        }

        [HarmonyPatch(typeof(StringOption), nameof(StringOption.Decrease))]
        [HarmonyPostfix]
        public static void StringDecreasePostfix(StringOption __instance)
        {
            var item = ConfigUIPanel.ItemOf(__instance.GetInstanceID());
            if (item == null) return;

            item.SetValueSilently(__instance.Value);
            AfterChange(item, __instance);
        }

        // =====================================================================
        //  标题：显示名由 ConfigRowDriver 负责，这里不再需要 FixedUpdate 的 Postfix。
        //
        //  （原来这里挂了三个 FixedUpdate Postfix 调 FixTitle，但上面已经把
        //    FixedUpdate 对"我们的行"整个跳过了 → prefix 返回 false 时 Postfix 不会执行，
        //    属于死代码，故删除。标题一致性由两件事保证：
        //      ① ConfigRowDriver.RefreshVisual() 直接写 DisplayName；
        //      ② 即使原版 Initialize 后用 GetString(Title) 覆盖，我们的
        //         ConfigTranslationPatch 也会把那个槽位翻译成同一个显示名。）
        // =====================================================================

        // =====================================================================
        //  共用
        // =====================================================================

        private static void AfterChange(ConfigItem item, OptionBehaviour behaviour)
        {
            try
            {
                // 诊断：这一行是谁、容器里同名行有几个、会话内共实例化过几次。
                LogRowIdentity(behaviour, "AfterChange");

                // ⚠️ 顺序很关键：**先让驱动器把值同步到显示与可见性判定所依赖的状态**，
                // 再 Refresh。否则 Refresh 里的 NeedRebuild 会读到"值还没写回"的旧状态，
                // 判定为"应可见却没建" → 触发整页重建（实测每次点击都重建）。
                // 显示与取值一律由 ConfigRowDriver 负责 —— 原版 UpdateValue/FixedUpdate/
                // Initialize 已被上面的 Prefix 对我们的行跳过。
                ConfigUIPanel.RefreshRow(behaviour);

                // 可见性可能变了（如"启用调试模式"控制数量行）→ 用集合比较决定是否重建
                ConfigUIPanel.Refresh();

                ConfigSync.RaiseAndSync(item);
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[ConfigRowPatches.AfterChange]", ex);
            }
        }

        /// <summary>
        /// 诊断：打印这一行的实例 ID、名字，以及容器里**同名行有几个**。
        /// 若同名 &gt; 1，说明同一配置项被实例化了多次（重复建行）——
        /// 那正是"复选框成对出现""点两下才生效"的根因。
        /// </summary>
        internal static void LogRowIdentity(OptionBehaviour behaviour, string where)
        {
            try
            {
                if (behaviour == null) return;
                var go = behaviour.gameObject;

                int duplicates = 0;
                var container = ConfigUIPanel.CurrentContainer;
                if (container != null)
                {
                    for (int i = 0; i < container.childCount; i++)
                    {
                        var c = container.GetChild(i);
                        if (c != null && c.name == go.name) duplicates++;
                    }
                }

                LightLogger.Log($"[ConfigRowPatches.Diag] {where} 行 '{go.name}' id={behaviour.GetInstanceID()} " +
                                $"容器内同名 {duplicates} 个 | 会话内共实例化 {ConfigUIPanel.InstantiatedCount} 次" +
                                (duplicates > 1 ? "  ← ⚠️ 重复建行！" : ""));
            }
            catch { }
        }
    }
}
