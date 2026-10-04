using System;
using LightInDark;
using LightInDark.Configuration;
using LightInDark.Core;
using LightInDark.Roles;

namespace Light.Config;

/// <summary>
/// 职业配置自动注册器：为每个可分配职业生成配置块，
/// 含通用"数量/概率"配置与职业专属配置（RoleConfiguration 轻量项，键自动加前缀）。
/// </summary>
internal static class RoleConfigRegistrar
{
    private static bool _registered;

    /// <summary>注册全部职业配置块（幂等，插件启动时调用一次）。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        try
        {
            foreach (var role in RoleRegistry.AllRoles)
            {
                if (!role.CanBeAssigned) continue;   // 兜底职业（普通船员/内鬼）不出配置

                var block = new ConfigBlock(
                    $"lid.role.{role.CodeName}", role.Name, ToCategory(role.RoleCategory))
                    .SetHeaderColor(role.Color.ToUnityColor());

                // 通用配置：出现数量 / 出现概率
                block.AddConfiguration(
                    $"role.{role.CodeName}.count", role.Allocation.MaxCount, 0, 15, 1,
                    $"{role.Name} 数量", $"{role.Name} 的最大出现数量");
                block.AddConfiguration(
                    $"role.{role.CodeName}.chance", role.Allocation.Chance, 0, 100, 5,
                    $"{role.Name} 概率", $"{role.Name} 的出现概率")
                    .WithSuffix(ConfigSuffix.Percent);

                // 职业专属配置（轻量项）
                foreach (var item in role.RoleConfiguration)
                    AddItem(block, role, item);
            }
            LightLogger.Log("[RoleConfigRegistrar] 职业配置注册完成");
        }
        catch (Exception ex)
        {
            LightLogger.LogError("[RoleConfigRegistrar.Register]", ex);
        }
    }

    /// <summary>把轻量配置项展开为 ConfigBlock 配置（键自动加 role.&lt;CodeName&gt;. 前缀）。</summary>
    private static void AddItem(ConfigBlock block, RoleTemplate role, RoleConfigItem item)
    {
        try
        {
            string key = item.FullKey(role);
            string label = item.ResolveLabel(role);
            ConfigItem added = item.Type switch
            {
                ConfigType.Bool => block.AddConfiguration(key, item.Default is bool b && b, label, item.Detail),
                ConfigType.Float => block.AddConfiguration(key, ToFloat(item.Default), item.Min, item.Max, item.Step, label, item.Detail),
                ConfigType.Value => block.AddConfiguration(key, item.Default as string[] ?? Array.Empty<string>(), label, item.Detail),
                _ => block.AddConfiguration(key, ToInt(item.Default), (int)item.Min, (int)item.Max, (int)item.Step, label, item.Detail),
            };
            if (item.Suffix != ConfigSuffix.None) added.WithSuffix(item.Suffix);
        }
        catch (Exception ex)
        {
            LightLogger.LogWarning($"[RoleConfigRegistrar] 配置项 {role.CodeName}.{item.Key} 注册失败: {ex.Message}");
        }
    }

    private static int ToInt(object v) => v is int i ? i : Convert.ToInt32(v ?? 0);
    private static float ToFloat(object v) => v is float f ? f : Convert.ToSingle(v ?? 0f);

    /// <summary>阵营枚举映射到配置分类。</summary>
    private static ConfigCategory ToCategory(RoleCategory category) => category switch
    {
        RoleCategory.Crewmate => ConfigCategory.Crewmate,
        RoleCategory.Impostor => ConfigCategory.Impostor,
        _ => ConfigCategory.Neutral,
    };
}
