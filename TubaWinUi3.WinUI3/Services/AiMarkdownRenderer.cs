using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TubaWinUi3.Controls.AgentChat;

namespace TubaWinUi3.Services;

public static partial class AiMarkdownRenderer
{
    public static StackPanel Render(string markdown, bool allowActions = true)
    {
        var container = new StackPanel { Spacing = 4 };
        ToolFlowThemeResources.AddPalette(container);

        // 【主题统一】本次渲染产出的动态元素（表格/推荐卡/链接/代码/列表…）统一登记到该容器的
        // 主题重刷作用域：主题变化时就地按【实际主题】重新解析画刷，不重建元素
        // → 流式内容、滚动位置、展开状态全部保留。
        var scope = ThemeRefreshScope.AttachRenderedContent(container);
        using var _ = scope.Push();

        var blocks = SplitBlocks(markdown);

        foreach (var block in blocks)
        {
            if (block.Type == BlockType.ToolRecommend)
            {
                container.Children.Add(CreateToolCard(block.Content, allowActions));
            }
            else if (block.Type == BlockType.Website)
            {
                container.Children.Add(CreateWebsiteCard(block.Content));
            }
            else if (block.Type == BlockType.Setting)
            {
                container.Children.Add(CreateSettingCard(block.Content));
            }
            else if (block.Type == BlockType.Action)
            {
                if (allowActions) container.Children.Add(CreateActionCard(block.Content));
                else
                {
                    // Message previews and history are display-only; actions use their own confirmation flow.
                    var description = ParseArg(block.Content, "desc");
                    if (!string.IsNullOrWhiteSpace(description))
                        container.Children.Add(ContentText(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }));
                }
            }
            else if (block.Type == BlockType.CodeBlock)
            {
                var rtb = new RichTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = TubaWinUi3.Services.AppFonts.WinUI };
                MarkdownTextService.RenderToRichTextBlock(rtb, block.Content);
                container.Children.Add(rtb);
            }
            else if (block.Type == BlockType.Table)
            {
                container.Children.Add(CreateTable(block.Content));
            }
            else
            {
                var rtb = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = TubaWinUi3.Services.AppFonts.WinUI };
                MarkdownTextService.RenderToRichTextBlock(rtb, block.Content);
                container.Children.Add(rtb);
            }
        }

        return container;
    }

    private static TextBlock ContentText(TextBlock text)
    {
        ThemeBrushBinder.Apply(text, TextBlock.ForegroundProperty, ToolFlowThemeResources.PrimaryText);
        return text;
    }

    private static List<MarkdownBlock> SplitBlocks(string markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var textBuffer = new System.Text.StringBuilder();

        void FlushText()
        {
            if (textBuffer.Length > 0)
            {
                var text = textBuffer.ToString().TrimEnd('\n');
                if (!string.IsNullOrWhiteSpace(text))
                {
                    blocks.AddRange(SplitTextWithActionAndCode(text));
                }
                textBuffer.Clear();
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var remaining = line;

            while (remaining.Length > 0)
            {
                var recIdx = remaining.IndexOf("[RECOMMEND_TOOL]", StringComparison.OrdinalIgnoreCase);
                var webIdx = remaining.IndexOf("[WEBSITE]", StringComparison.OrdinalIgnoreCase);
                var setIdx = remaining.IndexOf("[SETTING]", StringComparison.OrdinalIgnoreCase);

                var firstIdx = -1;
                if (recIdx >= 0 && (firstIdx < 0 || recIdx < firstIdx)) firstIdx = recIdx;
                if (webIdx >= 0 && (firstIdx < 0 || webIdx < firstIdx)) firstIdx = webIdx;
                if (setIdx >= 0 && (firstIdx < 0 || setIdx < firstIdx)) firstIdx = setIdx;

                if (firstIdx < 0)
                {
                    textBuffer.AppendLine(remaining);
                    break;
                }

                if (firstIdx > 0)
                {
                    textBuffer.AppendLine(remaining.Substring(0, firstIdx));
                }

                FlushText();

                var tag = remaining.Substring(firstIdx);
                if (tag.StartsWith("[RECOMMEND_TOOL]", StringComparison.OrdinalIgnoreCase))
                {
                    var endIdx = tag.IndexOf('\n');
                    var tagContent = endIdx >= 0 ? tag.Substring(0, endIdx) : tag;
                    blocks.Add(new MarkdownBlock(BlockType.ToolRecommend, tagContent.Trim()));
                    remaining = endIdx >= 0 ? tag.Substring(endIdx + 1) : "";
                }
                else if (tag.StartsWith("[WEBSITE]", StringComparison.OrdinalIgnoreCase))
                {
                    var endIdx = tag.IndexOf('\n');
                    var tagContent = endIdx >= 0 ? tag.Substring(0, endIdx) : tag;
                    blocks.Add(new MarkdownBlock(BlockType.Website, tagContent.Trim()));
                    remaining = endIdx >= 0 ? tag.Substring(endIdx + 1) : "";
                }
                else if (tag.StartsWith("[SETTING]", StringComparison.OrdinalIgnoreCase))
                {
                    var endIdx = tag.IndexOf('\n');
                    var tagContent = endIdx >= 0 ? tag.Substring(0, endIdx) : tag;
                    blocks.Add(new MarkdownBlock(BlockType.Setting, tagContent.Trim()));
                    remaining = endIdx >= 0 ? tag.Substring(endIdx + 1) : "";
                }
                else
                {
                    textBuffer.AppendLine(remaining);
                    break;
                }
            }
        }

        FlushText();
        return blocks;
    }

    private static List<MarkdownBlock> SplitTextWithActionAndCode(string markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        var currentLines = new List<string>();

        void FlushText()
        {
            if (currentLines.Count > 0)
            {
                blocks.Add(new MarkdownBlock(BlockType.Text, string.Join("\n", currentLines)));
                currentLines.Clear();
            }
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("[ACTION]", StringComparison.OrdinalIgnoreCase))
            {
                FlushText();
                var actionSb = new System.Text.StringBuilder();
                actionSb.AppendLine(line);
                i++;
                while (i < lines.Length)
                {
                    if (lines[i].TrimStart().StartsWith("```"))
                    {
                        actionSb.AppendLine(lines[i]);
                        i++;
                        while (i < lines.Length)
                        {
                            actionSb.AppendLine(lines[i]);
                            if (lines[i].TrimStart().StartsWith("```")) { i++; break; }
                            i++;
                        }
                        continue;
                    }
                    if (!lines[i].TrimStart().StartsWith("[") &&
                        !lines[i].TrimStart().StartsWith("{") &&
                        !lines[i].TrimStart().StartsWith("}") &&
                        !lines[i].TrimStart().StartsWith("\"") &&
                        !lines[i].TrimStart().StartsWith(",") &&
                        !string.IsNullOrWhiteSpace(lines[i]))
                    {
                        break;
                    }
                    actionSb.AppendLine(lines[i]);
                    i++;
                }
                blocks.Add(new MarkdownBlock(BlockType.Action, actionSb.ToString().TrimEnd()));
            }
            else if (trimmed.StartsWith("|") && trimmed.IndexOf('|', 1) >= 0)
            {
                FlushText();
                var tableSb = new System.Text.StringBuilder();
                while (i < lines.Length)
                {
                    var tl = lines[i].TrimStart();
                    if (!tl.StartsWith("|") || tl.IndexOf('|', 1) < 0) break;
                    tableSb.AppendLine(lines[i]);
                    i++;
                }
                blocks.Add(new MarkdownBlock(BlockType.Table, tableSb.ToString().TrimEnd()));
            }
            else if (trimmed.StartsWith("```"))
            {
                FlushText();
                var codeSb = new System.Text.StringBuilder();
                codeSb.AppendLine(line);
                i++;
                while (i < lines.Length)
                {
                    codeSb.AppendLine(lines[i]);
                    if (lines[i].TrimStart().StartsWith("```")) { i++; break; }
                    i++;
                }
                blocks.Add(new MarkdownBlock(BlockType.CodeBlock, codeSb.ToString().TrimEnd()));
            }
            else
            {
                currentLines.Add(line);
                i++;
            }
        }

        FlushText();
        return blocks;
    }

    private static Border CreateTable(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var tableRows = new List<string[]>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (IsSeparatorRow(trimmed)) continue;

            var cells = ParseTableRow(trimmed);
            if (cells.Length > 0)
                tableRows.Add(cells);
        }

        if (tableRows.Count == 0)
            return new Border();

        var colCount = tableRows.Max(r => r.Length);

        var tableGrid = new Grid
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1)
        };
        ThemeBrushBinder.Apply(tableGrid, Grid.BorderBrushProperty, ToolFlowThemeResources.Stroke);

        for (var c = 0; c < colCount; c++)
            tableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var r = 0; r < tableRows.Count; r++)
        {
            var isHeader = r == 0;
            tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (var c = 0; c < tableRows[r].Length && c < colCount; c++)
            {
                var cellText = tableRows[r][c];
                var cellRtb = new RichTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                };
                ThemeBrushBinder.Apply(cellRtb, RichTextBlock.ForegroundProperty, ToolFlowThemeResources.PrimaryText);
                var para = new Paragraph();
                MarkdownTextService.AddInlineContent(para, cellText);
                cellRtb.Blocks.Add(para);

                if (isHeader)
                {
                    foreach (var blk in cellRtb.Blocks)
                    {
                        if (blk is Paragraph p)
                        {
                            foreach (var inline in p.Inlines)
                            {
                                if (inline is Run run)
                                    run.FontWeight = FontWeights.Bold;
                                else if (inline is Span span)
                                    span.FontWeight = FontWeights.Bold;
                            }
                        }
                    }
                }

                var cellBorder = new Border
                {
                    BorderThickness = new Thickness(c > 0 ? 1 : 0, r > 0 ? 1 : 0, 0, 0),
                    Padding = new Thickness(10, 6, 10, 6),
                    Child = cellRtb
                };
                ThemeBrushBinder.Apply(cellBorder, Border.BorderBrushProperty, ToolFlowThemeResources.Stroke);

                if (isHeader)
                    ThemeBrushBinder.Apply(cellBorder, Border.BackgroundProperty, ToolFlowThemeResources.Surface);

                Grid.SetRow(cellBorder, r);
                Grid.SetColumn(cellBorder, c);
                tableGrid.Children.Add(cellBorder);
            }
        }

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Child = tableGrid
        };
    }

    private static bool IsSeparatorRow(string line)
    {
        var stripped = line.Replace("|", "").Replace("-", "").Replace(" ", "").Replace(":", "");
        return stripped.Length == 0 && line.Contains('-');
    }

    private static string[] ParseTableRow(string line)
    {
        var cells = new List<string>();
        var parts = line.Split('|');
        for (int i = 1; i < parts.Length - 1; i++)
        {
            cells.Add(parts[i].Trim());
        }
        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[^1].TrimEnd()))
            cells.Add(parts[^1].Trim());
        return cells.ToArray();
    }

    private static Border CreateToolCard(string line, bool allowActions)
    {
        var after = line.Substring("[RECOMMEND_TOOL]".Length).Trim();
        var pipeIdx = after.IndexOf('|');
        string name, reason;
        if (pipeIdx >= 0)
        {
            name = after.Substring(0, pipeIdx).Trim();
            var rest = after.Substring(pipeIdx + 1).Trim();
            reason = ParseArg(rest, "reason");
            if (string.IsNullOrWhiteSpace(reason)) reason = rest;
        }
        else
        {
            name = after.Trim();
            reason = "";
        }

        var toolPath = "";
        var isBuiltin = false;
        var builtinId = "";

        if (allowActions) try
        {
            var allTools = ToolCatalog.GetAllToolsCached();
            var tool = allTools.FirstOrDefault(t =>
                t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (tool is not null)
            {
                toolPath = tool.EffectivePath;
            }
            else
            {
                var builtin = BuiltinToolRegistry.Tools.FirstOrDefault(t =>
                    t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (builtin is not null)
                {
                    isBuiltin = true;
                    builtinId = builtin.Id;
                }
            }
        }
        catch { }

        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 10, 14, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE8F1",
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center
        };
        ThemeBrushBinder.Apply(icon, FontIcon.ForegroundProperty, ToolFlowThemeResources.Accent);
        grid.Children.Add(icon); Grid.SetColumn(icon, 0);

        var infoStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(ContentText(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.Bold,
            FontSize = 13
        }));
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var reasonText = new TextBlock
            {
                Text = reason,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            ThemeBrushBinder.Apply(reasonText, TextBlock.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
            infoStack.Children.Add(reasonText);
        }
        grid.Children.Add(infoStack); Grid.SetColumn(infoStack, 1);

        if (allowActions && !string.IsNullOrWhiteSpace(toolPath))
        {
            var launchBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new FontIcon { Glyph = "\uE72A", FontSize = 11 },
                        new TextBlock { Text = MiscTexts.T("打开"), FontSize = 12 }
                    }
                },
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(6),
                Tag = toolPath
            };
            launchBtn.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((string)launchBtn.Tag) { UseShellExecute = true });
                    launchBtn.Content = new TextBlock { Text = MiscTexts.T("已打开"), FontSize = 12 };
                    launchBtn.IsEnabled = false;
                }
                catch { }
            };
            grid.Children.Add(launchBtn); Grid.SetColumn(launchBtn, 2);
        }
        else if (!allowActions)
        {
            // A recommendation is readable in incomplete/history messages but cannot launch anything.
        }
        else if (isBuiltin)
        {
            var tip = new TextBlock
            {
                Text = MiscTexts.T("内置工具"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushBinder.Apply(tip, TextBlock.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
            grid.Children.Add(tip); Grid.SetColumn(tip, 2);
        }
        else
        {
            var tip = new TextBlock
            {
                Text = MiscTexts.T("未安装"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            ThemeBrushBinder.Apply(tip, TextBlock.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
            grid.Children.Add(tip); Grid.SetColumn(tip, 2);
        }

        var toolCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
        ThemeBrushBinder.Apply(toolCard, Border.BackgroundProperty, ToolFlowThemeResources.Surface);
        return toolCard;
    }

    private static Border CreateWebsiteCard(string line)
    {
        var after = line.Substring("[WEBSITE]".Length).Trim();
        var pipeIdx = after.IndexOf('|');
        string url, desc;
        if (pipeIdx >= 0)
        {
            url = after.Substring(0, pipeIdx).Trim();
            var rest = after.Substring(pipeIdx + 1).Trim();
            desc = ParseArg(rest, "desc");
            if (string.IsNullOrWhiteSpace(desc)) desc = rest;
        }
        else
        {
            url = after.Trim();
            desc = "";
        }

        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 10, 14, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE774",
            FontSize = 18,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212)),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(icon); Grid.SetColumn(icon, 0);

        var infoStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(desc))
        {
            infoStack.Children.Add(ContentText(new TextBlock
            {
                Text = desc,
                FontWeight = FontWeights.Bold,
                FontSize = 13
            }));
        }
        var urlText = new TextBlock
        {
            Text = url,
            FontSize = 12
        };
        ThemeBrushBinder.Apply(urlText, TextBlock.ForegroundProperty, ToolFlowThemeResources.Accent);
        infoStack.Children.Add(urlText);
        grid.Children.Add(infoStack); Grid.SetColumn(infoStack, 1);

        var openBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new FontIcon { Glyph = "\uE71B", FontSize = 11 },
                    new TextBlock { Text = MiscTexts.T("访问"), FontSize = 12 }
                }
            },
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            Tag = url
        };
        openBtn.Click += (_, _) =>
        {
            try { Pages.BrowserPage.Open((string)openBtn.Tag); } catch { }
        };
        grid.Children.Add(openBtn); Grid.SetColumn(openBtn, 2);

        var websiteCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
        ThemeBrushBinder.Apply(websiteCard, Border.BackgroundProperty, ToolFlowThemeResources.Surface);
        return websiteCard;
    }

    private static Border CreateSettingCard(string line)
    {
        var after = line.Substring("[SETTING]".Length).Trim();

        string path = ParseArg(after, "path");
        string name = ParseArg(after, "name");
        string current = ParseArg(after, "current");
        string recommend = ParseArg(after, "recommend");
        string reason = ParseArg(after, "reason");

        if (string.IsNullOrWhiteSpace(name)) name = path;

        var stack = new StackPanel { Spacing = 4, Padding = new Thickness(14, 10, 14, 10) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new FontIcon
        {
            Glyph = "\uE77B",
            FontSize = 14,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 218, 112, 0)),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(ContentText(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        }));
        stack.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(current))
        {
            var currentText = new TextBlock
            {
                Text = MiscTexts.TSub($"当前值：{current}"),
                FontSize = 12
            };
            ThemeBrushBinder.Apply(currentText, TextBlock.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
            stack.Children.Add(currentText);
        }

        if (!string.IsNullOrWhiteSpace(recommend))
        {
            var recStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var recommendedText = new TextBlock
            {
                Text = MiscTexts.TSub($"建议修改为：{recommend}"),
                FontSize = 12,
            };
            ThemeBrushBinder.Apply(recommendedText, TextBlock.ForegroundProperty, ToolFlowThemeResources.Accent);
            recStack.Children.Add(recommendedText);
            stack.Children.Add(recStack);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            var reasonText = new TextBlock
            {
                Text = MiscTexts.TSub($"理由：{reason}"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            ThemeBrushBinder.Apply(reasonText, TextBlock.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
            stack.Children.Add(reasonText);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var pathText = new TextBlock
            {
                Text = path,
                FontSize = 11,
                FontFamily = TubaWinUi3.Services.AppFonts.WinUI,
                TextWrapping = TextWrapping.Wrap
            };
            ThemeBrushBinder.Apply(pathText, TextBlock.ForegroundProperty, ToolFlowThemeResources.SecondaryText);
            stack.Children.Add(pathText);
        }

        var settingCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Child = stack
        };
        ThemeBrushBinder.Apply(settingCard, Border.BackgroundProperty, ToolFlowThemeResources.Surface);
        return settingCard;
    }

    internal static Border CreateActionCard(string content, Action<List<(AiActionStep action, bool confirmed, string result)>>? onAllResolved = null)
    {
        var idx = content.IndexOf("[ACTION]", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            content = content.Substring(idx + "[ACTION]".Length).Trim();

        var stack = new StackPanel { Spacing = 4, Padding = new Thickness(14, 10, 14, 10) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA",
            FontSize = 14,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 218, 112, 0)),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = MiscTexts.T("需要确认的操作"),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 218, 112, 0)),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(header);

        var actions = ParseActionJson(content);
        var actionStates = new List<(AiActionStep Action, Button ConfirmBtn, Button RejectBtn, TextBlock StatusTb, bool? Confirmed)>();

        Button allConfirmBtn = null!;
        Button allRejectBtn = null!;
        TextBlock globalStatus = null!;

        foreach (var action in actions)
        {
            var kindLabel = action.Kind switch
            {
                AiActionKind.RunCommand => MiscTexts.T("执行命令"),
                AiActionKind.ModifyConfig => MiscTexts.T("修改配置"),
                AiActionKind.ReadConfig => MiscTexts.T("读取配置"),
                AiActionKind.LaunchTool => MiscTexts.T("启动工具"),
                _ => MiscTexts.T("操作")
            };

            var actionStack = new StackPanel { Spacing = 2, Margin = new Thickness(24, 6, 0, 0) };
            actionStack.Children.Add(new TextBlock
            {
                Text = MiscTexts.TSub($"{kindLabel}：{action.Description}"),
                FontWeight = FontWeights.Bold,
                FontSize = 13
            });

            if (!string.IsNullOrWhiteSpace(action.Detail))
            {
                var detailText = new TextBlock
                {
                    Text = MiscTexts.TSub($"详情：{action.Detail}"),
                    FontSize = 12,
                    FontFamily = TubaWinUi3.Services.AppFonts.WinUI,
                    TextWrapping = TextWrapping.Wrap
                };
                ThemeBrushBinder.Apply(detailText, TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                actionStack.Children.Add(detailText);
            }

            if (!string.IsNullOrWhiteSpace(action.Reason))
            {
                var actionReasonText = new TextBlock
                {
                    Text = MiscTexts.TSub($"理由：{action.Reason}"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                };
                ThemeBrushBinder.Apply(actionReasonText, TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                actionStack.Children.Add(actionReasonText);
            }

            if (action.Kind == AiActionKind.RunCommand && action.TimeoutSeconds != 60)
            {
                var timeoutText = new TextBlock
                {
                    Text = MiscTexts.TSub($"超时：{action.TimeoutSeconds} 秒"),
                    FontSize = 12
                };
                ThemeBrushBinder.Apply(timeoutText, TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                actionStack.Children.Add(timeoutText);
            }

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

            var confirmBtn = new Button
            {
                Content = MiscTexts.T("确认"),
                FontSize = 12,
                Padding = new Thickness(12, 4, 12, 4),
                CornerRadius = new CornerRadius(6),
                Tag = action
            };

            var rejectBtn = new Button
            {
                Content = MiscTexts.T("拒绝"),
                FontSize = 12,
                Padding = new Thickness(12, 4, 12, 4),
                CornerRadius = new CornerRadius(6),
                Tag = action
            };

            var statusTb = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            btnRow.Children.Add(confirmBtn);
            btnRow.Children.Add(rejectBtn);
            btnRow.Children.Add(statusTb);
            actionStack.Children.Add(btnRow);

            var state = (Confirmed: (bool?)null, confirmBtn, rejectBtn, statusTb);
            var stateIndex = -1;

            confirmBtn.Click += (_, _) =>
            {
                state.Confirmed = true;
                if (stateIndex >= 0) actionStates[stateIndex] = (action, confirmBtn, rejectBtn, statusTb, true);
                confirmBtn.IsEnabled = false;
                rejectBtn.IsEnabled = false;
                statusTb.Text = MiscTexts.T("已确认 ✓");
                statusTb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 60));
                statusTb.Visibility = Visibility.Visible;
                CheckAllResolved();
            };

            rejectBtn.Click += (_, _) =>
            {
                state.Confirmed = false;
                if (stateIndex >= 0) actionStates[stateIndex] = (action, confirmBtn, rejectBtn, statusTb, false);
                confirmBtn.IsEnabled = false;
                rejectBtn.IsEnabled = false;
                statusTb.Text = MiscTexts.T("已拒绝 ✗");
                statusTb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
                statusTb.Visibility = Visibility.Visible;
                CheckAllResolved();
            };

            actionStates.Add((action, confirmBtn, rejectBtn, statusTb, null));
            stateIndex = actionStates.Count - 1;
            stack.Children.Add(actionStack);
        }

        if (actions.Count == 0)
        {
            var tb = new TextBlock
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            };
            ThemeBrushBinder.Apply(tb, TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            stack.Children.Add(tb);
            return new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 218, 112, 0)),
                CornerRadius = new CornerRadius(8),
                Child = stack
            };
        }

        allConfirmBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE73E", FontSize = 12 },
                    new TextBlock { Text = MiscTexts.TSub($"全部确认并执行（{actions.Count} 项）"), FontSize = 12 }
                }
            },
            FontSize = 12,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(24, 8, 0, 0),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
        };
        ThemeBrushBinder.Apply(allConfirmBtn, Control.BackgroundProperty, "AccentFillColorDefaultBrush");

        allRejectBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE711", FontSize = 12 },
                    new TextBlock { Text = MiscTexts.T("全部拒绝"), FontSize = 12 }
                }
            },
            FontSize = 12,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(8, 8, 0, 0)
        };

        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bottomRow.Children.Add(allConfirmBtn);
        bottomRow.Children.Add(allRejectBtn);
        stack.Children.Add(bottomRow);

        globalStatus = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(24, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(globalStatus);

        allConfirmBtn.Click += (_, _) =>
        {
            for (int i = 0; i < actionStates.Count; i++)
            {
                var (a, cb, rb, st, _) = actionStates[i];
                if (cb.IsEnabled)
                {
                    cb.IsEnabled = false;
                    rb.IsEnabled = false;
                    st.Text = MiscTexts.T("已确认 ✓");
                    st.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 60));
                    st.Visibility = Visibility.Visible;
                    actionStates[i] = (a, cb, rb, st, true);
                }
            }
            allConfirmBtn.IsEnabled = false;
            allRejectBtn.IsEnabled = false;
            ExecuteAllResolved();
        };

        allRejectBtn.Click += (_, _) =>
        {
            for (int i = 0; i < actionStates.Count; i++)
            {
                var (a, cb, rb, st, _) = actionStates[i];
                if (cb.IsEnabled)
                {
                    cb.IsEnabled = false;
                    rb.IsEnabled = false;
                    st.Text = MiscTexts.T("已拒绝 ✗");
                    st.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
                    st.Visibility = Visibility.Visible;
                    actionStates[i] = (a, cb, rb, st, false);
                }
            }
            allConfirmBtn.IsEnabled = false;
            allRejectBtn.IsEnabled = false;
            ExecuteAllResolved();
        };

        void CheckAllResolved()
        {
            for (int i = 0; i < actionStates.Count; i++)
            {
                var (a, cb, rb, st, confirmed) = actionStates[i];
                if (confirmed == null && !cb.IsEnabled)
                {
                    // was already handled by individual click
                }
                // sync state from individual clicks
                if (!cb.IsEnabled && confirmed == null)
                {
                    var wasConfirmed = st.Text.Contains("已确认", StringComparison.Ordinal) || st.Text.Contains("Confirmed", StringComparison.Ordinal);
                    actionStates[i] = (a, cb, rb, st, wasConfirmed);
                }
            }

            var allDone = actionStates.All(s => !s.ConfirmBtn.IsEnabled);
            if (allDone)
            {
                allConfirmBtn.IsEnabled = false;
                allRejectBtn.IsEnabled = false;
                ExecuteAllResolved();
            }
        }

        async void ExecuteAllResolved()
        {
            allConfirmBtn.IsEnabled = false;
            allRejectBtn.IsEnabled = false;
            globalStatus.Text = MiscTexts.T("正在执行已确认的操作...");
            globalStatus.Visibility = Visibility.Visible;

            var results = new List<(AiActionStep action, bool confirmed, string result)>();

            for (int i = 0; i < actionStates.Count; i++)
            {
                var (a, cb, rb, st, confirmed) = actionStates[i];
                var wasConfirmed = confirmed == true || (!cb.IsEnabled && (st.Text.Contains("已确认", StringComparison.Ordinal) || st.Text.Contains("Confirmed", StringComparison.Ordinal)));

                if (wasConfirmed)
                {
                    st.Text = MiscTexts.T("执行中...");
                    // 运行期状态色：一次性赋色、不登记重刷（否则主题切换会把已完成状态色覆盖回强调色）
                    ThemeBrushBinder.ApplyUntracked(st, TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                    st.Visibility = Visibility.Visible;

                    try
                    {
                        var result = await AiAssistantService.ExecuteActionAsync(a, CancellationToken.None);
                        a.Executed = true;
                        st.Text = MiscTexts.T("已执行 ✓");
                        st.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 60));
                        results.Add((a, true, result));
                    }
                    catch
                    {
                        st.Text = MiscTexts.T("执行失败");
                        st.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28));
                        results.Add((a, true, MiscTexts.T("执行失败")));
                    }
                }
                else
                {
                    results.Add((a, false, MiscTexts.T("用户拒绝执行")));
                }
            }

            var confirmedCount = results.Count(r => r.confirmed);
            var rejectedCount = results.Count(r => !r.confirmed);
            globalStatus.Text = MiscTexts.TSub($"已完成：{confirmedCount} 项已执行") +
                (rejectedCount > 0 ? MiscTexts.TSub($"，{rejectedCount} 项已拒绝") : "");

            onAllResolved?.Invoke(results);
        }

        return new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 218, 112, 0)),
            CornerRadius = new CornerRadius(8),
            Child = stack
        };
    }

    private static List<AiActionStep> ParseActionJson(string content)
    {
        var result = new List<AiActionStep>();
        var jsonStart = content.IndexOf('[');
        var jsonEnd = content.LastIndexOf(']');
        if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart) return result;

        var json = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var kindStr = elem.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                var kind = kindStr switch
                {
                    "run_command" => AiActionKind.RunCommand,
                    "write_reg" => AiActionKind.ModifyConfig,
                    "modify_config" => AiActionKind.ModifyConfig,
                    "launch_tool" => AiActionKind.LaunchTool,
                    "read_config" => AiActionKind.ReadConfig,
                    "read_reg" => AiActionKind.ReadConfig,
                    _ => AiActionKind.Info
                };

                var timeoutSec = 60;
                if (elem.TryGetProperty("timeout", out var to) && to.ValueKind == System.Text.Json.JsonValueKind.Number)
                    timeoutSec = Math.Clamp(to.GetInt32(), 5, 3600);

                result.Add(new AiActionStep
                {
                    Kind = kind,
                    Description = elem.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    Detail = elem.TryGetProperty("detail", out var dt) ? dt.GetString() ?? "" :
                            elem.TryGetProperty("cmd", out var cmd) ? cmd.GetString() ?? "" : "",
                    Reason = elem.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    TimeoutSeconds = timeoutSec,
                });
            }
        }
        catch { }
        return result;
    }

    private static string ParseArg(string args, string key)
    {
        var pattern = key + "=";
        var idx = args.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var start = idx + pattern.Length;
        var end = args.IndexOf('|', start);
        if (end < 0) end = args.Length;
        return args.Substring(start, end - start).Trim();
    }

    private enum BlockType
    {
        Text,
        CodeBlock,
        Table,
        ToolRecommend,
        Website,
        Setting,
        Action
    }

    private sealed class MarkdownBlock(BlockType type, string content)
    {
        public BlockType Type { get; } = type;
        public string Content { get; } = content;
    }
}
