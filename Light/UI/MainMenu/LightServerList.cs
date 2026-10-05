using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using InnerNet;
using LightInDark.Core;

namespace Light.UI.MainMenu
{
    /// <summary>
    /// 一条自带服务器配置。
    ///
    /// 用法见下面 <see cref="LightServerList.BuiltInServers"/> 的注释 —— 一行一个，加几行就是几个服务器。
    /// </summary>
    public readonly struct LightServerEntry
    {
        /// <summary>显示名（会出现在服务器下拉框按钮上）。允许用 TMP 富文本，例如 &lt;color=#ff7518&gt;帆船服&lt;/color&gt;。</summary>
        public readonly string Name;

        /// <summary>主机名或 IP。**只写主机名，不要带 `http://` 前缀、也不要带端口。**</summary>
        public readonly string Ip;

        /// <summary>端口。HTTP 服务用 443，UDP 游戏服一般是 22023。</summary>
        public readonly ushort Port;

        /// <summary>是否启用 DTLS。私服基本都是 false。</summary>
        public readonly bool UseDtls;

        public LightServerEntry(string name, string ip, ushort port = 22023, bool useDtls = false)
        {
            Name = name;
            Ip = ip;
            Port = port;
            UseDtls = useDtls;
        }

        /// <summary>是否可用（名字和地址都填了才算）。</summary>
        public bool IsValid =>
            !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Ip);
    }

    /// <summary>
    /// 服务器列表管控：**屏蔽官方服务器** + **额外列出自带服务器**。
    ///
    /// ---- 思路来源 ----
    /// 参考 Nebula 的做法（`NebulaPluginNova\Patches\Old\CustomServerPatch.cs`
    /// + `Modules\Online\CustomServer.cs`）：
    ///   · 判定官方/私服用的是 <c>IRegionInfo.TranslateName == StringNames.NoTranslation</c>
    ///     —— 官服的 <c>TranslateName</c> 是真实的 <c>StringNames</c> 值，自定义服是 <c>NoTranslation</c>。
    ///   · 它的 <c>ServerDropdown.FillServerOptions</c> Prefix 直接把区域列表
    ///     <c>Where(r =&gt; r.TranslateName == NoTranslation)</c>，官服就整个不出现在下拉框里了。
    ///
    /// ⚠️⚠️ **只读 <see cref="ServerManager"/>，绝不写它。**（2026-10-04 踩坑修正）
    ///
    ///   上一版在注入时顺手做了 <c>ServerManager.DefaultRegions = arr;</c> 和
    ///   <c>sm.AvailableRegions = arr;</c> —— **把两个全局列表整个覆盖了**。
    ///   后果：如果这段代码在 <c>ServerManager.LoadServers()</c> 读完 <c>regionInfo.json</c>
    ///   **之前**执行，我拿到的 <c>AvailableRegions</c> 里只有官方区域，
    ///   一覆盖就把用户存在 `regionInfo.json` 里的私服**全挤掉了**（实测：用户报「私服全没了」）。
    ///
    ///   我们只是要往下拉框里多列两个服务器，**根本不需要改全局状态**。
    ///   现在自带服务器只存在 <see cref="_builtIn"/> 这个我们自己的列表里，
    ///   由 <see cref="AllowedRegions"/> 拼给下拉框用 —— `ServerManager` 一个字段都不动。
    ///
    ///   （新版 Nebula 也是这个思路：`CustomServer.cs` 的注释写着
    ///    「Nebula のリージョンは **AvailableRegions に居ない**ため名前が復元できない」，
    ///     即**故意不把自带区域放进 AvailableRegions**。）
    ///
    /// ⚠️ **必须保留兜底**：如果过滤之后一个可用区域都不剩（用户没配任何私服、
    ///    也没配自带服务器），**必须回退成原列表** ——
    ///    否则下拉框直接空了、在线功能全废，用户会认为是 MOD 把游戏弄坏了。
    /// </summary>
    public static class LightServerList
    {
        // ═══════════════════════════════════════════════════════════════════
        //  TODO ★★★ 自带服务器列表：在这里加，一行一个 ★★★
        //
        //  参数：new LightServerEntry(显示名, 地址, 端口, 是否启用DTLS)
        //    · 显示名 —— 允许 TMP 富文本，例如 "<color=#ff7518>帆船服</color>"
        //    · 地址   —— **只写主机名，不要带 http:// 或端口**
        //    · 端口   —— HTTP 服务用 443；UDP 游戏服一般是 22023（默认值）
        //    · DTLS   —— 私服基本都是 false（默认值），可省略不写
        //
        //  它们会按这里的顺序排在服务器下拉框的**最前面**。
        //  ⚠️ 地址留空("")的那条会被**自动跳过**，不会注入、也不会报错。
        //
        //  ⚠️ 去重和排序**都用 IP**，不用显示名 —— 显示名带颜色标签，
        //     一旦调色就等于换了名字，用名字当键会重复注入。
        // ═══════════════════════════════════════════════════════════════════

        public static readonly LightServerEntry[] BuiltInServers =
        {
             new("<color=#ff7518>帆船服</color><color=#ffff00>[广州]</color>", "as-gz.play.fcaugame.cn", 443),
             new("<color=#FFFF00>hvt</color><color=#FF80FF>PIG</color><color=#00FF00>服</color>",  "hvtpigimpostorserver.torv.site", 443),
        };

        // ───────────────────────────────────────────────────────────────────

        /// <summary>我们自己构造的自带区域对象。**不进 ServerManager**，只给下拉框用。</summary>
        private static List<IRegionInfo>? _builtIn;

        private static bool _loggedList;
        private static bool _loggedFallback;

        /// <summary>有没有配置任何可用的自带服务器。</summary>
        public static bool HasBuiltIn =>
            BuiltInServers != null && BuiltInServers.Any(e => e.IsValid);

        /// <summary>
        /// 取一个区域的主服务器地址（<c>IRegionInfo.Servers[0].Ip</c>），用作去重和排序的键。
        ///
        /// ⚠️ **用 IP 而不是 Name 做键**：Name 允许带 TMP 富文本颜色标签
        ///    （如 <c>&lt;color=#ff7518&gt;帆船服&lt;/color&gt;</c>），一旦调色就等于换了名字，
        ///    会被当成新服务器重复注入。IP 不会因为改显示效果而变化。
        /// </summary>
        private static string? RegionIp(IRegionInfo? region)
        {
            try
            {
                var servers = region?.Servers;
                if (servers == null || servers.Length == 0) return null;
                return servers[0]?.Ip;
            }
            catch { return null; }
        }

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
        /// 构造自带区域对象（只构造一次，缓存在 <see cref="_builtIn"/>）。
        ///
        /// ⚠️ **完全不碰 <see cref="ServerManager"/>** —— 见类注释里那段踩坑说明。
        /// </summary>
        private static List<IRegionInfo> EnsureBuiltIn()
        {
            if (_builtIn != null) return _builtIn;

            _builtIn = new List<IRegionInfo>();
            if (!HasBuiltIn) return _builtIn;

            try
            {
                foreach (var e in BuiltInServers)
                {
                    if (!e.IsValid) continue;

                    var region = new StaticHttpRegionInfo(
                        e.Name,
                        StringNames.NoTranslation,
                        e.Ip,
                        new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ServerInfo>(
                            new[] { new ServerInfo(e.Name, e.Ip, e.Port, e.UseDtls) })).Cast<IRegionInfo>();

                    _builtIn.Add(region);
                    LightLogger.Log($"[LightServerList] 已准备自带服务器：{e.Name} → {e.Ip}:{e.Port} (dtls={e.UseDtls})");
                }
            }
            catch (Exception ex)
            {
                LightLogger.LogError("[LightServerList.EnsureBuiltIn]", ex);
            }

            return _builtIn;
        }

        /// <summary>
        /// 拿"允许出现在下拉框里的区域" = **自带服务器（按声明顺序）+ 用户自己的私服**，官服被过滤掉。
        ///
        /// ⚠️ 只**读** <see cref="ServerManager.AvailableRegions"/>，绝不写。
        /// ⚠️ 过滤后为空 → 回退成原列表（否则在线功能全废）。
        /// </summary>
        public static List<IRegionInfo> AllowedRegions()
        {
            try
            {
                var sm = DestroyableSingleton<ServerManager>.Instance;
                if (sm == null) return new List<IRegionInfo>();

                // 用户自己的区域（regionInfo.json 读进来的那些）—— 只读，一个字段都不改
                var all = sm.AvailableRegions?.ToList() ?? new List<IRegionInfo>();
                var userCustom = all.Where(IsCustomRegion).ToList();

                // 自带服务器
                var builtIn = EnsureBuiltIn();

                // 用户列表里已经有同 IP 的，就不重复列（用 IP 比，不用显示名）
                var builtInIps = new HashSet<string>(
                    builtIn.Select(RegionIp).Where(ip => !string.IsNullOrEmpty(ip))!,
                    StringComparer.OrdinalIgnoreCase);

                var merged = new List<IRegionInfo>(builtIn);
                merged.AddRange(userCustom.Where(r =>
                {
                    var ip = RegionIp(r);
                    return ip == null || !builtInIps.Contains(ip);
                }));

                if (merged.Count == 0)
                {
                    // ⚠️ 兜底：一个私服都没有、自带服务器也没配
                    //    → 宁可显示官服，也不能让下拉框空掉
                    if (!_loggedFallback)
                    {
                        _loggedFallback = true;
                        LightLogger.LogWarning(
                            "[LightServerList] 过滤官服后没有任何可用区域（用户没配私服、也没配自带服务器），" +
                            "**已回退为显示全部区域** —— 否则在线功能会整个不可用。\n" +
                            "  想彻底屏蔽官服，请在 LightServerList.BuiltInServers 里填至少一个服务器地址。");
                    }
                    return all;
                }

                if (!_loggedList)
                {
                    _loggedList = true;
                    var names = string.Join(", ", merged.Take(14).Select(r => r?.Name ?? "?"));
                    LightLogger.Log($"[LightServerList] 下拉框列表：官方 {all.Count} 个（已屏蔽）· " +
                                    $"自带 {builtIn.Count} 个 · 你的私服 {merged.Count - builtIn.Count} 个\n" +
                                    $"  共列出 {merged.Count} 个：{names}");
                }

                return merged;
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
