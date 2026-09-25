using GGdown.Sites;
using GGdown.ViewModels;

namespace GGdown.Tests.ViewModels;

/// <summary>站点选项行 VM（Task 9 自 SettingsViewModelTests 拆出）：三种 Kind 的 ToValue 往返 + 元数据映射。</summary>
public class OptionItemViewModelTests
{
    [Fact]
    public void OptionItem_ToValue_roundtrips_three_kinds()
    {
        var boolItem = new OptionItemViewModel(
            new OptionField("videos", OptionKind.Boolean, true, "同时下载视频和动图"), true);
        Assert.Equal("videos", boolItem.Key);
        Assert.Equal("同时下载视频和动图", boolItem.DisplayName);
        Assert.Equal(OptionKind.Boolean, boolItem.Kind);
        Assert.True(boolItem.BoolValue);
        Assert.Equal(true, boolItem.ToValue());
        boolItem.BoolValue = false;
        Assert.Equal(false, boolItem.ToValue()); // 编辑后按 Kind 回读 bool

        var textItem = new OptionItemViewModel(
            new OptionField("filename", OptionKind.Text, "{tweet_id}", "文件命名模板"), "abc_{num}");
        Assert.Equal("abc_{num}", textItem.TextValue);
        Assert.Equal("abc_{num}", textItem.ToValue());
        textItem.TextValue = "{x}_{y}";
        Assert.Equal("{x}_{y}", textItem.ToValue()); // 编辑后按 Kind 回读 string

        var choiceItem = new OptionItemViewModel(
            new OptionField("rate", OptionKind.Choice, "1", "速率", ["1", "2"]), "2");
        Assert.Equal("2", choiceItem.TextValue);
        Assert.Equal("2", choiceItem.ToValue());
        Assert.True(choiceItem.IsChoice);
        Assert.False(choiceItem.IsText);
        Assert.Equal(["1", "2"], choiceItem.Choices);
    }
}
