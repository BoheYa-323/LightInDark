namespace LightInDark.Documents;

/// <summary>
/// 描述文档时使用的文本格式。
/// </summary>
public enum RoleDocumentType
{
    // 默认为这个值。会去 RoleTemplate.Describe 里给的翻译键去对应语言找，支持正常富文本，比如<b><i><br>等。不建议使用\n换行。
    Normal,
    // 需要去 RoleTemplate 指定的 HTML 路径拿内容，高度自定义化。
    Html,
    // 去 RoleTemplate 指定的 md 文件路径取得内容并渲染。
    MarkDown
}
