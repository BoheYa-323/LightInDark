using AmongUs.GameOptions;
using LightInDark.Configuration;
using LightInDark.Core;
using LightInDark.Roles;

namespace Light.Roles.Crewmates;

/// <summary>普通船员：未分配自定义职业时的兜底职业。</summary>
public class Crewmate : RoleTemplate
{
    public static readonly Crewmate MyRole = new();

    public override string CodeName => "crewmate";
    public override RoleCategory RoleCategory => RoleCategory.Crewmate;

    public override RuntimeRoleTemplate CreateRuntime(PlayerControl owner)
        => new RuntimeInstance(owner, this);

    public class RuntimeInstance : RuntimeRoleTemplate
    {
        public override RoleTemplate Role => MyRole;

        public RuntimeInstance(PlayerControl owner, RoleTemplate template) : base(owner, template) { }

        protected override void OnActivated()
        {
            var control = MyPlayer?.Control;
            if (control == null) return;
            RoleManager.Instance.SetRole(control, RoleTypes.Crewmate);
        }
    }
}
