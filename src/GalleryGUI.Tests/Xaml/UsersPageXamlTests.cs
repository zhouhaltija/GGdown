using System.Xml.Linq;

namespace GalleryGUI.Tests.Xaml;

/// <summary>
/// 静态约束：CommunityToolkit DataGrid 7.1.2 的 DataGridColumnHeader.OnContentChanged
/// 在 Header 为 UIElement 时抛 NotSupportedException，WinUI 再包装成 0xC000027B 白屏闪退。
/// </summary>
public class UsersPageXamlTests
{
    [Fact]
    public void DataGrid_column_Header_must_not_contain_UIElement()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "XamlFixtures", "UsersPage.xaml");
        Assert.True(File.Exists(path), $"missing fixture: {path}");

        var doc = XDocument.Load(path);
        var violations = doc.Descendants()
            .Where(e => e.Name.LocalName.EndsWith(".Header", StringComparison.Ordinal)
                        && e.Parent is not null
                        && e.Parent.Name.LocalName.Contains("DataGrid", StringComparison.Ordinal))
            .SelectMany(header => header.Elements()
                .Select(child => $"{header.Name.LocalName} contains <{child.Name.LocalName}>"))
            .ToList();

        Assert.True(
            violations.Count == 0,
            "DataGrid column Header cannot be a UIElement (NotSupportedException → 0xC000027B). Use HeaderStyle ContentTemplate.\n"
            + string.Join("\n", violations));
    }

    [Fact]
    public void Toolbar_has_select_all_deselect_and_invert_buttons()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "XamlFixtures", "UsersPage.xaml"));
        Assert.Contains("Content=\"全选\"", xaml);
        Assert.Contains("Content=\"取消全选\"", xaml);
        Assert.Contains("Content=\"反选\"", xaml);
        Assert.Contains("SelectAllCommand", xaml);
        Assert.Contains("DeselectAllCommand", xaml);
        Assert.Contains("InvertSelectionCommand", xaml);
        Assert.Contains("从剪贴板添加", xaml);
        Assert.Contains("刷新资料", xaml);
        Assert.Contains("DownloadHighlightsCommand", xaml);
    }

    [Fact]
    public void Row_download_button_binds_downloading_text()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "XamlFixtures", "UsersPage.xaml"));
        Assert.Contains("DownloadButtonText", xaml);
        Assert.DoesNotContain("OnSelectAllChecked", xaml);
    }
}
