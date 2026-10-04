using System;
using System.Collections.Generic;
using LightInDark.Configuration;
using LightInDark.Core;
using LightInDark.RPCs;

namespace LightInDark.Roles.Assignment
{
    /// <summary>
    /// 职业预定管理器：玩家通过 /up 指令预定下一局强制分配的职业。
    /// 仅房主维护预定表；按 PlayerId 记录，分配时消耗。
    /// 玩家未上局时预定保留，直到其参加的下一局。
    /// </summary>
    public static class RolePinManager
    {
        // playerId -> 职业 CodeName（原文保存，分配时再解析）
        private static readonly Dictionary<byte, string> _pins = new();

        /// <summary>处理预定请求（仅房主调用）。校验职业存在且已开启，成功后回执。</summary>
        public static void HandleRequest(byte senderPlayerId, string roleName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(roleName)) return;
                var sender = FindPlayer(senderPlayerId);
                if (sender == null) return;

                var role = ResolveRole(roleName);
                if (role == null)
                {
                    RpcDefinitions.ShowSystemMessage(senderPlayerId, $"未找到职业「{roleName}」");
                    return;
                }
                if (GetMaxCount(role) <= 0)
                {
                    RpcDefinitions.ShowSystemMessage(senderPlayerId, $"职业「{role.Name}」未开启，无法预定");
                    return;
                }

                _pins[senderPlayerId] = role.CodeName;
                RpcDefinitions.ShowSystemMessage(senderPlayerId, $"已预定下一局职业：{role.Name}");
                LightLogger.Log($"[RolePin] {sender.Data.PlayerName} 预定 {role.CodeName}");
            }
            catch (Exception ex)
            {
                LightLogger.LogError("RolePinManager.HandleRequest", ex);
            }
        }

        /// <summary>处理取消预定（仅房主调用）。</summary>
        public static void HandleCancel(byte senderPlayerId)
        {
            try
            {
                if (_pins.Remove(senderPlayerId))
                    RpcDefinitions.ShowSystemMessage(senderPlayerId, "已取消职业预定");
            }
            catch (Exception ex)
            {
                LightLogger.LogError("RolePinManager.HandleCancel", ex);
            }
        }

        /// <summary>分配时查询预定（不消耗）。职业未注册时返回 false。</summary>
        public static bool TryGetPin(byte playerId, out RoleTemplate role)
        {
            role = null;
            try
            {
                if (!_pins.TryGetValue(playerId, out var code)) return false;
                role = RoleRegistry.GetByName(code);
                if (role == null) { _pins.Remove(playerId); return false; }
                return true;
            }
            catch (Exception ex)
            {
                LightLogger.LogError("RolePinManager.TryGetPin", ex);
                return false;
            }
        }

        /// <summary>分配成功后消耗预定。</summary>
        public static void Consume(byte playerId) => _pins.Remove(playerId);

        /// <summary>清空全部预定（换房/房主变更时）。</summary>
        public static void Clear() => _pins.Clear();

        /// <summary>按 CodeName 精确匹配，再按显示名匹配。</summary>
        private static RoleTemplate ResolveRole(string name)
        {
            var role = RoleRegistry.GetByName(name);
            if (role != null) return role;
            foreach (var r in RoleRegistry.AllRoles)
                if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                    return r;
            return null;
        }

        /// <summary>职业是否开启：配置 role.&lt;CodeName&gt;.count &gt; 0（无配置回退默认 Allocation）。</summary>
        private static int GetMaxCount(RoleTemplate role)
        {
            var item = ConfigRegistry.Get($"role.{role.CodeName}.count");
            return item != null ? item.GetInt() : role.Allocation.MaxCount;
        }

        private static PlayerControl FindPlayer(byte id)
        {
            foreach (var pc in PlayerControl.AllPlayerControls)
                if (pc.PlayerId == id) return pc;
            return null;
        }
    }
}
