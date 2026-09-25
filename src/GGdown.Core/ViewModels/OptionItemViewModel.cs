using CommunityToolkit.Mvvm.ComponentModel;
using GGdown.Sites;

namespace GGdown.ViewModels;

/// <summary>
/// 站点选项行 VM（B7）：OptionSchema 字段 + 当前值的可编辑包装。
/// Kind 决定编辑控件与 ToValue 回读类型（Boolean→bool，Text/Choice→string），
/// 值类型与站点 DefaultOptions 对齐（裁定 3）。
/// 页面按 IsBoolean/IsText 做单模板可见性切换渲染（裁定 2：简洁优先）。
/// </summary>
public sealed partial class OptionItemViewModel : ObservableObject
{
    public OptionItemViewModel(OptionField field, object? value)
    {
        Key = field.Key;
        DisplayName = field.DisplayName;
        Kind = field.Kind;
        Choices = field.Choices ?? [];
        // 当前值缺失/类型不符回退 Schema 默认值（GetSiteOptionsAsync 只回保存过的键）
        _boolValue = value as bool? ?? (field.Default as bool? ?? false);
        _textValue = value?.ToString() ?? field.Default?.ToString() ?? "";
    }

    public string Key { get; }
    public string DisplayName { get; }
    public OptionKind Kind { get; }
    public IReadOnlyList<string> Choices { get; }

    private bool _boolValue;
    public bool BoolValue { get => _boolValue; set => SetProperty(ref _boolValue, value); }

    private string _textValue = "";
    public string TextValue { get => _textValue; set => SetProperty(ref _textValue, value); }

    public bool IsBoolean => Kind == OptionKind.Boolean;
    public bool IsText => Kind == OptionKind.Text;
    public bool IsChoice => Kind == OptionKind.Choice;

    /// <summary>按 Kind 回读当前值（bool/string，与 DefaultOptions 值类型对齐）。</summary>
    public object? ToValue() => Kind switch
    {
        OptionKind.Boolean => BoolValue,
        _ => TextValue,
    };
}

