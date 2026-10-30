namespace ClipNest.Domain;

/// <summary>
/// 片段类型。数值写入数据库，**不得随意改动顺序**。
///
/// 类型→名称/角标的映射放在 UI 层（<c>UI.ViewModels.ClipRow</c>）：那是界面文案，
/// 要跟着语言变，不该由领域层持有。
/// </summary>
public enum ClipType
{
    Text = 0,
    Link = 1,
    Code = 2,
    Email = 3,
    Number = 4,
}
