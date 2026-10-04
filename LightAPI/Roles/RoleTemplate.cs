using System;
using LightInDark.Configuration;
using LightInDark.Core;
using LightInDark.Documents;

namespace LightInDark.Roles
{
    /// <summary>
    /// 职业模板（定义侧）：一个职业一个类，全局只保留一个定义实例（约定 MyRole 单例）。
    /// 数据用虚属性声明，键支持 {CodeName} 占位符（如 "role.{CodeName}.intro"）。
    /// 运行时行为写在 <see cref="RuntimeRoleTemplate"/> 子类（由 <see cref="CreateRuntime"/> 返回）。
    /// 必须重写 <see cref="CodeName"/> 与 <see cref="CreateRuntime"/>。
    /// </summary>
    public abstract class RoleTemplate
    {
        // ---- 基础定义 ----

        /// <summary>唯一内部名（语言/配置键前缀）。必须重写。</summary>
        public abstract string CodeName { get; }

        /// <summary>职业颜色（名字/开场白/配置头着色）。</summary>
        public virtual LightInDark.Color Color => LightInDark.Color.White;

        /// <summary>阵营。</summary>
        public virtual RoleCategory RoleCategory => RoleCategory.Crewmate;

        /// <summary>注册序号（RPC 用）。</summary>
        public int Id { get; internal set; }

        // ---- 文案（翻译键，支持 {CodeName} 占位符）----

        /// <summary>职业名语言键。</summary>
        public virtual string NameKey => "role.{CodeName}.name";

        /// <summary>一句话简介语言键。</summary>
        public virtual string ShortDescribe => "role.{CodeName}.shortdes";

        /// <summary>职业描述语言键（按 <see cref="DocumentType"/> 渲染）。</summary>
        public virtual string Describe => "role.{CodeName}.des";

        /// <summary>开场白语言键。</summary>
        public virtual string Intro => "role.{CodeName}.intro";

        /// <summary>描述文档的渲染格式。</summary>
        public virtual RoleDocumentType DocumentType => RoleDocumentType.Normal;

        /// <summary>开场音效：相对路径 mp3（YouAreText 出现时播放），null 不播放。</summary>
        public virtual string IntroSFX => null;

        /// <summary>职业来源标注（如移植自某 MOD），null 不标。</summary>
        public virtual string From => null;

        // ---- 分配与能力 ----

        /// <summary>分配参数（默认不参与随机分配）。</summary>
        public virtual AllocationParameters Allocation => default;

        /// <summary>默认参数（分配时随 RPC 下发）。</summary>
        public virtual int[] DefaultArguments => Array.Empty<int>();

        /// <summary>能否被分配机随机分配（false = 仅作兜底/手动指定）。</summary>
        public virtual bool CanBeAssigned => true;

        /// <summary>任务数量（由原版游戏设置传入的占位值）。</summary>
        public virtual int TaskCount => 1;

        /// <summary>能否报告尸体（硬编码能力，不可被设置更改）。</summary>
        public virtual bool CanReport => true;

        /// <summary>能否召开紧急会议（硬编码能力，不可被设置更改）。</summary>
        public virtual bool CanCallEmergencyMeeting => true;

        /// <summary>职业专属配置项（键自动加 role.&lt;CodeName&gt;. 前缀；通用数量/概率由注册器附加）。</summary>
        public virtual RoleConfigItem[] RoleConfiguration => Array.Empty<RoleConfigItem>();

        /// <summary>职业立绘（帮助详情左上角），null 不显示。</summary>
        public virtual UnityEngine.Sprite IconImage => null;

        // ---- 文案解析 ----

        /// <summary>解析键中的 {CodeName} 占位符。</summary>
        public string ResolveKey(string key) => key?.Replace("{CodeName}", CodeName) ?? "";

        /// <summary>显示名（按语言键解析，缺省回退 CodeName）。</summary>
        public string Name => LightInDark.Language.Language.GetStringOrKey(ResolveKey(NameKey), CodeName);

        /// <summary>一句话简介（按语言键解析）。</summary>
        public string ShortDescribeText => LightInDark.Language.Language.GetStringOrKey(ResolveKey(ShortDescribe), "");

        /// <summary>职业描述（按语言键解析，仅 Normal 格式使用）。</summary>
        public string DescribeText => LightInDark.Language.Language.GetStringOrKey(ResolveKey(Describe), "");

        /// <summary>
        /// 职业描述全文（按 DocumentType 分发）：Normal 走语言键；
        /// Html/MarkDown 时 Describe 存嵌入资源路径（如 "./Resources/Docs/x.html"），经 RoleDocument 渲染。
        /// </summary>
        public string GetDocumentText() => DocumentType switch
        {
            RoleDocumentType.Html => Documents.RoleDocument.Load(ResolveKey(Describe), GetType().Assembly, Documents.RoleDocument.RenderHtml),
            RoleDocumentType.MarkDown => Documents.RoleDocument.Load(ResolveKey(Describe), GetType().Assembly, Documents.RoleDocument.RenderMarkdown),
            _ => DescribeText,
        };

        /// <summary>开场白（按语言键解析）。</summary>
        public string IntroText => LightInDark.Language.Language.GetStringOrKey(ResolveKey(Intro), "");

        // ---- 运行时创建 ----

        /// <summary>创建绑定指定玩家的运行时实例（每个职业必须实现，返回其 Runtime 子类）。</summary>
        public abstract RuntimeRoleTemplate CreateRuntime(global::PlayerControl owner);

        /// <summary>框架内部创建入口：调用职业的 CreateRuntime 后激活并登记事件。</summary>
        internal RuntimeRoleTemplate CreateRuntimeInternal(Game.Player player)
        {
            var runtime = CreateRuntime(player.Control);
            runtime?.Activate();
            return runtime;
        }
    }
}
