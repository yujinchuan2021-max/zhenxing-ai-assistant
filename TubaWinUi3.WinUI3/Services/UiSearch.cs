using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TubaWinUi3.Services;

/// <summary>
/// ZXAI：可视化树查找小工具（自检 ZxSelfTest 与 AI 页输入框预填等场景共用的按名/按类型查找）。
/// </summary>
internal static class UiSearch
{
    /// <summary>从 root 子树（含自身）找最后出现的指定类型名元素。</summary>
    internal static DependencyObject? FindLastDescendantByTypeName(DependencyObject root, string typeName)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = count - 1; i >= 0; i--)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child.GetType().Name == typeName) return child;
            var found = FindLastDescendantByTypeName(child, typeName);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>从 root 子树（含自身）找第一个出现的指定类型名元素。</summary>
    internal static DependencyObject? FindFirstDescendantByTypeName(DependencyObject root, string typeName)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child.GetType().Name == typeName) return child;
            var found = FindFirstDescendantByTypeName(child, typeName);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>从 root 子树（含自身）找最后出现的指定类型元素。</summary>
    internal static T? FindLastDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = count - 1; i >= 0; i--)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var found = FindLastDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }
}
