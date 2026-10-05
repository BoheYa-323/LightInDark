using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using InnerNet;
using LightInDark.Core;
using UnityEngine;

namespace Light.UI.MainMenu
{
    /// <summary>
    /// 服务器列表管控
    /// </summary>
    public static class LightServerList
    {
        public const string BuiltInServerName = "<color=#ff7518>帆船服</color><color=#ffff00>[广州]</color>";

        public const string BuiltInServerIp = "as-gz.play.fcaugame.cn";
        public const ushort BuiltInServerPort = 443;

        public const bool BuiltInServerUseDtls = false;

        private static bool _injected;
        private static bool _logged;

        /// <summary>自带服务器是否已配置（IP 非空）。</summary>
        public static bool HasBuiltIn =>
            !string.IsNullOrWhiteSpace(BuiltInServerIp);

        /// <summary>判定一个区域是不是自定义服务器。和 Nebula 的 AmongUsUtil.IsCustomServer 同一套判据。</summary>
        public static bool IsCustomRegion(IRegionInfo? region)
        {
            try
            {
                if (region == null) return false;
                var tn = region.TranslateName;
                return tn == StringNames.NoTranslation || tn == null;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把自带服务器注入 <see cref="ServerManager"/> 的区域表。
        /// 只做一次；IP 为空时什么都不做。
        /// </summary>
        public static void EnsureInjected()
        {
            if (_injected) return;
            if (!HasBuiltIn) return;

            try
            {
                var sm = DestroyableSingleton<ServerManager>.Instance;
                if (sm == null) return;

                // 已经存在同名区域就不重复注入
                var existing = sm.AvailableRegions;
                if (existing != null && existing.Any(r => r != null && r.Name == BuiltInServerName))
                {
                    _injected = true;
                    return;
                }

                var region = new StaticHttpRegionInfo(
                    BuiltInServerName,
                    StringNames.NoTranslation,
                    BuiltInServerIp,
                    new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ServerInfo>(
                        new[]
                        {
                            new ServerInfo(BuiltInServerName, BuiltInServerIp, BuiltInServerPort, BuiltInServerUseDtls)
                        })).Cast<IRegionInfo>();

                var list = new List<IRegionInfo>();
                if (existing != null) list.AddRange(existing);
                list.Insert(0, region);          // 放最前面 → 下拉框第一个、也是默认选中的那个

                var arr = list.ToArray();
                ServerManager.DefaultRegions = arr;
                sm.AvailableRegions = arr;

                _injected = true;
                LightLogger.Log($"[LightServerList] 已注入自带服务器：{BuiltInServerName} → " +
                                $"{BuiltInServerIp}:{BuiltInServerPort} (dtls={BuiltInServerUseDtls})");
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[LightServerList.EnsureInjected]", ex);
            }
        }

        /// <summary>
        /// 拿"允许出现在下拉框里的区域"。
        ///
        /// 规则：
        ///   ① 先注入自带服务器（如果配了 IP）
        ///   ② 过滤掉官方区域（<c>TranslateName != NoTranslation</c>）
        ///   ③ **兜底：过滤后为空就返回原列表**（否则在线功能全废）
        /// </summary>
        public static List<IRegionInfo> AllowedRegions()
        {
            try
            {
                EnsureInjected();

                var sm = DestroyableSingleton<ServerManager>.Instance;
                if (sm == null) return new List<IRegionInfo>();

                var all = sm.AvailableRegions?.ToList() ?? new List<IRegionInfo>();
                var custom = all.Where(IsCustomRegion).ToList();

                if (custom.Count == 0)
                {
                    // ⚠️ 兜底：一个私服都没有 → 宁可显示官服，也不能让下拉框空掉
                    if (!_logged)
                    {
                        _logged = true;
                        LightLogger.LogWarning(
                            "[LightServerList] 过滤官服后没有任何可用区域（用户没配私服、也没配自带服务器），" +
                            "**已回退为显示全部区域** —— 否则在线功能会整个不可用。\n" +
                            "  想彻底屏蔽官服，请在 LightServerList.BuiltInServerIp 填一个服务器地址。");
                    }
                    return all;
                }

                if (!_logged)
                {
                    _logged = true;
                    LightLogger.Log($"[LightServerList] 服务器列表：共 {all.Count} 个，" +
                                    $"过滤官服后保留 {custom.Count} 个" +
                                    (HasBuiltIn ? $"（含自带 {BuiltInServerName}）" : "") +
                                    " · 官服已屏蔽（仅 UI 层，未动网络层）");
                }

                // 自带服务器排最前
                if (HasBuiltIn)
                {
                    custom = custom
                        .OrderByDescending(r => r != null && r.Name == BuiltInServerName)
                        .ToList();
                }

                return custom;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[LightServerList.AllowedRegions]", ex);
                try { return DestroyableSingleton<ServerManager>.Instance?.AvailableRegions?.ToList() ?? new List<IRegionInfo>(); }
                catch { return new List<IRegionInfo>(); }
            }
        }
    }
}
