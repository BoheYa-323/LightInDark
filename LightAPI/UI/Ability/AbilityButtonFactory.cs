using System;
using LightInDark.Game;
using LightInDark.Roles;
using UnityEngine;

namespace LightInDark.UI.Ability
{
    /// <summary>
    /// 按钮工厂：职业运行时实例创建按钮的统一入口。
    /// 所有 Create 返回已注册到 RoleButtonManager 的按钮实例。
    /// </summary>
    public static class AbilityButtonFactory
    {
        /// <summary>创建普通技能按钮（点击一次进冷却）。</summary>
        public static AbilityButton Create(RuntimeRoleTemplate role, RoleButtonConfig config, Action onClick)
            => AbilityButton.Create(role, config, onClick);

        /// <summary>创建持续效果按钮（效果期间可再点取消，见 EffectButton 配置）。</summary>
        public static EffectButton CreateEffect(RuntimeRoleTemplate role, RoleButtonConfig config, Action onClick)
            => EffectButton.Create(role, config, onClick);

        /// <summary>创建会议目标按钮（会议中为每位玩家生成目标按钮）。</summary>
        public static MeetingTargetButton CreateMeetingTarget(RuntimeRoleTemplate role,
            Action<MeetingHud, PlayerControl> onClick,
            Func<PlayerControl, bool> canAdd = null,
            Sprite icon = null,
            string sfx = null)
            => MeetingTargetButton.Create(role, onClick, canAdd, icon, sfx);

        /// <summary>创建会议右下角按钮（会议期间显示）。</summary>
        public static MeetingAbilityButton CreateMeetingAbility(RuntimeRoleTemplate role, RoleButtonConfig config, Action onClick)
            => MeetingAbilityButton.Create(role, config, onClick);
    }
}
