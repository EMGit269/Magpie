using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static void AddPendingAttachments(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    _pendingAttachments.Add(CreateAttachmentItem(path));
                }
                catch (Exception ex)
                {
                    _pendingAttachments.Add(new AttachmentItem
                    {
                        Path = path,
                        FileName = System.IO.Path.GetFileName(path),
                        Kind = AttachmentKind.Unsupported,
                        MimeType = "application/octet-stream",
                        SizeBytes = System.IO.File.Exists(path) ? new FileInfo(path).Length : 0,
                        Error = "读取失败: " + ex.Message
                    });
                }
            }

            RefreshAttachmentPreview();
        }

        private static AttachmentItem CreateAttachmentItem(string path)
        {
            var file = new FileInfo(path);
            string ext = file.Extension.ToLowerInvariant();
            var item = new AttachmentItem
            {
                Path = path,
                FileName = file.Name,
                SizeBytes = file.Exists ? file.Length : 0,
                MimeType = GetMimeType(ext)
            };

            if (IsImageExtension(ext))
            {
                item.Kind = AttachmentKind.Image;
                item.Base64 = Convert.ToBase64String(System.IO.File.ReadAllBytes(path));
            }
            else if (IsTextExtension(ext))
            {
                item.Kind = AttachmentKind.Text;
                item.ExtractedText = TruncateAttachmentText(System.IO.File.ReadAllText(path, Encoding.UTF8), item.FileName);
            }
            else if (IsDocumentExtension(ext))
            {
                item.Kind = AttachmentKind.Document;
                item.ExtractedText = TruncateAttachmentText(ExtractDocumentText(path, ext), item.FileName);
            }
            else
            {
                item.Kind = AttachmentKind.Unsupported;
                item.ExtractedText = $"文件 {item.FileName} 已上传，但当前不支持读取该格式内容。";
            }

            return item;
        }

        private static AttachmentItem CreateAttachmentItemFromDataUrl(string dataUrl)
        {
            if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return null;

            int commaIndex = dataUrl.IndexOf(',');
            if (commaIndex <= 5)
                return null;

            string header = dataUrl.Substring(5, commaIndex - 5);
            string base64 = dataUrl.Substring(commaIndex + 1);
            if (string.IsNullOrWhiteSpace(base64))
                return null;

            string mimeType = "image/png";
            int semicolonIndex = header.IndexOf(';');
            if (semicolonIndex > 0)
                mimeType = header.Substring(0, semicolonIndex);
            else if (!string.IsNullOrWhiteSpace(header))
                mimeType = header;

            byte[] bytes = Convert.FromBase64String(base64);
            string extension = MimeTypeToImageExtension(mimeType);
            string tempPath = Path.Combine(
                Path.GetTempPath(),
                "MAGPIE_restore_" + DateTime.UtcNow.Ticks + "_" + Guid.NewGuid().ToString("n").Substring(0, 8) + extension);
            File.WriteAllBytes(tempPath, bytes);

            return new AttachmentItem
            {
                Path = tempPath,
                FileName = Path.GetFileName(tempPath),
                MimeType = mimeType,
                Kind = AttachmentKind.Image,
                Base64 = base64,
                SizeBytes = bytes.LongLength
            };
        }

        private static bool IsImageExtension(string ext)
        {
            return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" }.Contains(ext);
        }

        private static bool IsTextExtension(string ext)
        {
            return new[] { ".txt", ".md", ".json", ".csv", ".xml", ".ghx" }.Contains(ext);
        }

        private static bool IsDocumentExtension(string ext)
        {
            return new[] { ".pdf", ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt" }.Contains(ext);
        }

        private static string GetMimeType(string ext)
        {
            switch (ext)
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".bmp": return "image/bmp";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".pdf": return "application/pdf";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                default: return "text/plain";
            }
        }

        private static string MimeTypeToImageExtension(string mimeType)
        {
            switch ((mimeType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "image/jpeg":
                case "image/jpg":
                    return ".jpg";
                case "image/bmp":
                    return ".bmp";
                case "image/gif":
                    return ".gif";
                case "image/webp":
                    return ".webp";
                default:
                    return ".png";
            }
        }

        private static string TruncateAttachmentText(string text, string fileName)
        {
            if (string.IsNullOrWhiteSpace(text)) return $"文件 {fileName} 未提取到可读文本。";
            const int maxChars = 12000;
            if (text.Length <= maxChars) return text;
            return text.Substring(0, maxChars) + $"\n\n[附件 {fileName} 内容过长，已截断到 {maxChars} 字符。]";
        }

        private static string ExtractDocumentText(string path, string ext)
        {
            try
            {
                if (ext == ".docx") return ExtractTextFromZipXml(path, "word/document.xml");
                if (ext == ".pptx") return ExtractPptxText(path);
                if (ext == ".xlsx") return ExtractXlsxText(path);
                if (ext == ".pdf") return ExtractPdfTextBestEffort(path);
                return $"旧版 Office 文件 {System.IO.Path.GetFileName(path)} 已上传，但当前仅能稳定读取 .docx/.xlsx/.pptx。";
            }
            catch (Exception ex)
            {
                return $"文件 {System.IO.Path.GetFileName(path)} 内容解析失败: {ex.Message}";
            }
        }

        private static string ExtractTextFromZipXml(string path, string entryName)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                var entry = archive.GetEntry(entryName);
                if (entry == null) return "";
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream))
                {
                    return ExtractTextFromXml(reader.ReadToEnd());
                }
            }
        }

        private static string ExtractPptxText(string path)
        {
            var sb = new StringBuilder();
            using (var archive = ZipFile.OpenRead(path))
            {
                foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml")).OrderBy(e => e.FullName))
                {
                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream))
                    {
                        sb.AppendLine(ExtractTextFromXml(reader.ReadToEnd()));
                    }
                }
            }
            return sb.ToString();
        }

        private static string ExtractXlsxText(string path)
        {
            var sb = new StringBuilder();
            using (var archive = ZipFile.OpenRead(path))
            {
                foreach (var entry in archive.Entries.Where(e => (e.FullName.StartsWith("xl/worksheets/") || e.FullName == "xl/sharedStrings.xml") && e.FullName.EndsWith(".xml")).OrderBy(e => e.FullName))
                {
                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream))
                    {
                        string text = ExtractTextFromXml(reader.ReadToEnd());
                        if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
                    }
                }
            }
            return sb.ToString();
        }

        private static string ExtractTextFromXml(string xml)
        {
            var doc = XDocument.Parse(xml);
            return string.Join(" ", doc.DescendantNodes().OfType<XText>().Select(t => t.Value).Where(v => !string.IsNullOrWhiteSpace(v))).Trim();
        }

        private static string ExtractPdfTextBestEffort(string path)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            string raw = Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
            var matches = Regex.Matches(raw, @"\((?<text>(?:\\.|[^\\)])*)\)");
            var text = string.Join(" ", matches.Cast<Match>().Select(m => m.Groups["text"].Value.Replace("\\)", ")").Replace("\\(", "(")).Where(v => v.Length > 1));
            return string.IsNullOrWhiteSpace(text)
                ? "PDF 已上传，但未提取到可读文本。若该 PDF 为扫描件，需要 OCR 后再上传文本。"
                : text;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return Math.Round(bytes / 1024.0, 1) + " KB";
            return Math.Round(bytes / 1024.0 / 1024.0, 1) + " MB";
        }

        private static void RefreshAttachmentPreview()
        {
            if (_attachmentPreviewPanel == null) return;

            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                _attachmentPreviewPanel.Children.Clear();
                _attachmentPreviewPanel.Visibility = _pendingAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                foreach (var attachment in _pendingAttachments.ToList())
                {
                    _attachmentPreviewPanel.Children.Add(CreateAttachmentCard(attachment, true));
                }
            }));
        }

        private static FrameworkElement CreateAttachmentCard(AttachmentItem attachment, bool removable)
        {
            var border = new Border
            {
                Background = ThemeBrush(Color.FromRgb(255, 255, 255), Color.FromRgb(28, 28, 28)),
                BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(50, 50, 50)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 8, 8),
                MaxWidth = 210
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (removable) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement preview;
            if (attachment.Kind == AttachmentKind.Image && System.IO.File.Exists(attachment.Path))
            {
                preview = new Image
                {
                    Source = LoadBitmapImage(attachment.Path),
                    Width = 44,
                    Height = 44,
                    Stretch = Stretch.UniformToFill,
                    ClipToBounds = true
                };
            }
            else
            {
                preview = new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeBrush(Color.FromRgb(238, 242, 247), Color.FromRgb(42, 42, 42)),
                    Child = new TextBlock
                    {
                        Text = GetAttachmentBadge(attachment),
                        Foreground = ThemeBrush(Color.FromRgb(58, 64, 74), Color.FromRgb(230, 230, 230)),
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }
            Grid.SetColumn(preview, 0);
            grid.Children.Add(preview);

            var info = new StackPanel { Margin = new Thickness(9, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = attachment.FileName,
                Foreground = ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(238, 238, 238)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 125
            });
            info.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(attachment.Error) ? FormatFileSize(attachment.SizeBytes) : attachment.Error,
                Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(150, 150, 150)),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 125
            });
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            if (removable)
            {
                var remove = new Button
                {
                    Content = CreateCloseGlyph(ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(190, 190, 190))),
                    Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(190, 190, 190)),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    FontSize = 14,
                    Width = 22,
                    Height = 22,
                    VerticalAlignment = VerticalAlignment.Top
                };
                remove.Click += (s, e) => {
                    _pendingAttachments.Remove(attachment);
                    RefreshAttachmentPreview();
                };
                Grid.SetColumn(remove, 2);
                grid.Children.Add(remove);
            }

            border.Child = grid;
            return border;
        }

        private static FrameworkElement CreateChatImageThumbnail(AttachmentItem attachment, double size = 120)
        {
            if (attachment == null || string.IsNullOrWhiteSpace(attachment.Path) || !System.IO.File.Exists(attachment.Path))
                return new Border();

            var thumbnailBorder = new Border
            {
                Width = size,
                Height = size,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(14),
                BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromArgb(38, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = ThemeBrush(Color.FromRgb(248, 249, 251), Color.FromRgb(22, 22, 22)),
                ClipToBounds = true,
                Cursor = Cursors.Hand
            };

            thumbnailBorder.Child = new Image
            {
                Source = LoadBitmapImage(attachment.Path, 320),
                Stretch = Stretch.UniformToFill,
                SnapsToDevicePixels = true
            };

            thumbnailBorder.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowImagePreviewWindow(attachment.Path, attachment.FileName);
            };

            return thumbnailBorder;
        }

        private static FrameworkElement CreateChatImageStrip(IEnumerable<AttachmentItem> attachments)
        {
            var imageItems = (attachments ?? Enumerable.Empty<AttachmentItem>())
                .Where(a => a != null && a.Kind == AttachmentKind.Image && !string.IsNullOrWhiteSpace(a.Path) && System.IO.File.Exists(a.Path))
                .ToList();
            if (imageItems.Count == 0)
                return null;

            var wrap = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0)
            };

            foreach (var attachment in imageItems)
                wrap.Children.Add(CreateChatImageThumbnail(attachment));

            return wrap;
        }

        private static void ShowImagePreviewWindow(string path, string title = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                return;

            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    var preview = new Window
                    {
                        Title = string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileName(path) : title,
                        Width = 980,
                        Height = 760,
                        MinWidth = 520,
                        MinHeight = 420,
                        Background = new SolidColorBrush(Color.FromRgb(10, 10, 10)),
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Owner = _window
                    };

                    preview.Content = new Grid
                    {
                        Background = new SolidColorBrush(Color.FromRgb(10, 10, 10)),
                        Children =
                        {
                            new ScrollViewer
                            {
                                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                Padding = new Thickness(18),
                                Content = new Image
                                {
                                    Source = LoadBitmapImage(path, null),
                                    Stretch = Stretch.Uniform,
                                    SnapsToDevicePixels = true
                                }
                            }
                        }
                    };

                    preview.Show();
                }
                catch (Exception ex)
                {
                    AddGhLog.Warn("ShowImagePreviewWindow: " + ex.Message);
                }
            }));
        }

        private static BitmapImage LoadBitmapImage(string path, int? decodePixelWidth = 120)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            if (decodePixelWidth.HasValue && decodePixelWidth.Value > 0)
                bitmap.DecodePixelWidth = decodePixelWidth.Value;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private static string GetAttachmentBadge(AttachmentItem attachment)
        {
            string ext = System.IO.Path.GetExtension(attachment.FileName).TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(ext)) return "FILE";
            return ext.Length > 4 ? ext.Substring(0, 4) : ext;
        }

        private static string BuildImageDataUrl(string path, string mimeType)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return "";
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0)
                    return "";
                string mime = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType;
                return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("BuildImageDataUrl failed: " + ex.Message);
                return "";
            }
        }

        private static List<object> BuildUserMessageContent(string input, List<AttachmentItem> attachments, bool includeImages = true, string imageContextNote = null)
        {
            var contentArr = new List<object>();
            var textBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(input)) textBuilder.AppendLine(input);
            if (!string.IsNullOrWhiteSpace(imageContextNote))
            {
                if (textBuilder.Length > 0) textBuilder.AppendLine();
                textBuilder.AppendLine(imageContextNote.Trim());
            }

            foreach (var attachment in attachments.Where(a => a.Kind != AttachmentKind.Image))
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine($"【附件内容：{attachment.FileName}】");
                textBuilder.AppendLine(attachment.ExtractedText ?? "未提取到文本内容。");
            }

            string text = textBuilder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                contentArr.Add(new { type = "text", text = text });
            }

            foreach (var attachment in attachments.Where(a => includeImages && a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64)))
            {
                contentArr.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:{attachment.MimeType};base64,{attachment.Base64}" }
                });
            }

            if (contentArr.Count == 0)
            {
                contentArr.Add(new { type = "text", text = input });
            }

            return contentArr;
        }

        private static void AppendNonImageAttachmentText(StringBuilder textBuilder, IEnumerable<AttachmentItem> attachments)
        {
            if (textBuilder == null || attachments == null) return;

            foreach (var attachment in attachments.Where(a => a.Kind != AttachmentKind.Image))
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine($"【附件内容：{attachment.FileName}】");
                if (!string.IsNullOrWhiteSpace(attachment.ExtractedText))
                    textBuilder.AppendLine(attachment.ExtractedText);
                else if (!string.IsNullOrWhiteSpace(attachment.Error))
                    textBuilder.AppendLine("附件读取失败：" + attachment.Error);
                else
                    textBuilder.AppendLine("未提取到文本内容。");
            }
        }

        private static string BuildVisionExecutionUserText(string input, List<AttachmentItem> attachments, string visionAnalysis)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(input))
            {
                sb.AppendLine("用户原始请求：");
                sb.AppendLine(input.Trim());
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("用户发送了图片附件，但没有额外文字说明。");
                sb.AppendLine();
            }

            AppendNonImageAttachmentText(sb, attachments);
            if (attachments != null && attachments.Any(a => a.Kind != AttachmentKind.Image))
                sb.AppendLine();

            sb.AppendLine("以下图片理解来自视觉预处理模型；执行模型没有直接看到原图。");
            sb.AppendLine(_agentMode == AgentMode.Plan
                ? "这份报告只提供图片和文字中的可见事实；是否回答、出图或操作 Grasshopper，由你结合用户请求和可用工具自行判断。"
                : "这份报告只提供图片和文字中的可见事实；是否回答、出图或操作 Grasshopper，由你结合用户请求和可用工具自行判断。");
            sb.AppendLine();
            sb.AppendLine(visionAnalysis?.Trim() ?? "");
            return sb.ToString().Trim();
        }

        private static string BuildFinalVisualReviewExecutionUserText(string priorDraft, string visualReview)
        {
            var sb = new StringBuilder();
            sb.AppendLine(_agentMode == AgentMode.Plan
                ? "以下是最终截图视觉复核结果。你需要结合这份复核补充或修正实施步骤，并明确说明仍存在的偏差。"
                : "以下是最终截图视觉复核结果。你需要结合这份复核，决定是否确认完成、继续修改，或明确说明仍存在的偏差。");
            if (!string.IsNullOrWhiteSpace(priorDraft))
            {
                sb.AppendLine();
                sb.AppendLine("你在复核前的结论：");
                sb.AppendLine(priorDraft.Trim());
            }
            sb.AppendLine();
            sb.AppendLine("最终截图视觉复核：");
            sb.AppendLine(visualReview?.Trim() ?? "");
            sb.AppendLine();
            sb.AppendLine(_agentMode == AgentMode.Plan
                ? "要求：如果复核指出未达标或存在明显偏差，不要宣称已完成；应补充或修正实施步骤，并向用户明确说明当前差距。"
                : "要求：如果复核指出未达标或存在明显偏差，不要直接结束；应继续修正或向用户明确说明当前差距。");
            return sb.ToString().Trim();
        }

        private static string BuildVisionCanvasContext(string input)
        {
            try
            {
                string raw = Magpie.Host.GrasshopperDocumentHost.ExecuteGetCanvasSummary();
                if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    return "当前无可用 Grasshopper 画布上下文。";

                var root = JObject.Parse(raw);
                var components = root["components"] as JArray ?? new JArray();
                var groups = root["groups"] as JArray ?? new JArray();
                var canvasErrors = root["canvas_errors"] as JArray ?? new JArray();
                var units = root["rhino_units"] as JObject;

                var sb = new StringBuilder();
                sb.AppendLine("画布上下文：");
                sb.AppendLine($"组件={components.Count} 问题={canvasErrors.Count}");
                if (units != null)
                {
                    string modelUnit = units["model_unit_system"]?.ToString();
                    string absTol = units["model_absolute_tolerance"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(modelUnit) || !string.IsNullOrWhiteSpace(absTol))
                        sb.AppendLine($"Rhino模型单位={modelUnit ?? "未知"}；以下所有未标注尺寸均按该单位理解；模型绝对公差={absTol ?? "未知"}");
                }

                string canvasIssueText = _txtCanvasIssues?.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(canvasIssueText))
                {
                    sb.AppendLine("诊断：" + ClampVisionText(canvasIssueText, 220).Replace("\r", " ").Replace("\n", " "));
                }

                if (canvasErrors.Count > 0)
                {
                    sb.AppendLine("关键问题：");
                    foreach (var err in canvasErrors.Take(4))
                    {
                        string name = err?["name"]?.ToString();
                        string level = err?["level"]?.ToString();
                        string message = err?["message"]?.ToString();
                        sb.AppendLine($"- {name}[{level}] {ClampVisionText(message, 80)}".TrimEnd());
                    }
                }
                if (groups.Count > 0)
                {
                    sb.AppendLine("组件组：");
                    foreach (var group in groups.Take(4))
                    {
                        string groupName = group?["name"]?.ToString();
                        int memberCount = (group?["members"] as JArray)?.Count ?? 0;
                        if (!string.IsNullOrWhiteSpace(groupName))
                            sb.AppendLine($"- {groupName}（成员{memberCount}）");
                    }
                }

                var selected = new List<JToken>();
                selected.AddRange(components.Where(c => c?["runtime_messages"] is JArray).Take(3));
                selected.AddRange(components.Where(c => IsVisionContextScriptComponent(c)).Take(4));
                selected.AddRange(components.Reverse().Take(4));
                var unique = selected
                    .Where(c => c != null)
                    .GroupBy(c => c["id"]?.ToString() ?? Guid.NewGuid().ToString("n"))
                    .Select(g => g.First())
                    .Take(5)
                    .ToList();

                if (unique.Count > 0)
                {
                    sb.AppendLine("相关组件：");
                    foreach (var comp in unique)
                    {
                        string name = comp["name"]?.ToString() ?? "未知组件";
                        string nickname = comp["nickname"]?.ToString();
                        string id = comp["id"]?.ToString();
                        string idShort = string.IsNullOrWhiteSpace(id) || id.Length < 8 ? id : id.Substring(0, 8);
                        sb.AppendLine($"- {name}" + (string.IsNullOrWhiteSpace(nickname) || nickname == name ? "" : $"({nickname})") + (string.IsNullOrWhiteSpace(idShort) ? "" : $"#{idShort}"));

                        if (comp["runtime_messages"] is JArray msgs && msgs.Count > 0)
                            sb.AppendLine("  消息=" + string.Join(" | ", msgs.Take(2).Select(m => ClampVisionText(m?.ToString(), 50)).Where(m => !string.IsNullOrWhiteSpace(m))));

                        if (comp["inputs"] is JArray inputs && inputs.Count > 0)
                        {
                            var inputParts = inputs.Take(2).Select(i =>
                            {
                                string portName = i?["name"]?.ToString() ?? "?";
                                string ds = i?["data_structure"]?.ToString() ?? "未知";
                                string src = "";
                                if (i?["sources"] is JArray srcs && srcs.Count > 0)
                                    src = "<-" + string.Join(",", srcs.Take(1).Select(s => s?["name"]?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)));
                                return $"{portName}: {ds}{src}";
                            });
                            sb.AppendLine("  输入=" + string.Join(" ; ", inputParts));
                        }

                        if (comp["outputs"] is JArray outputs && outputs.Count > 0)
                        {
                            var outputParts = outputs.Take(2).Select(o =>
                            {
                                string portName = o?["name"]?.ToString() ?? "?";
                                string ds = o?["data_structure"]?.ToString();
                                string type = o?["type"]?.ToString();
                                return string.IsNullOrWhiteSpace(ds) ? $"{portName}: {type}" : $"{portName}: {ds}";
                            });
                            sb.AppendLine("  输出=" + string.Join(" ; ", outputParts));
                        }
                    }
                }

                var scripts = components
                    .Where(c => IsVisionContextScriptComponent(c))
                    .Take(3)
                    .ToList();
                if (scripts.Count > 0)
                {
                    sb.AppendLine("相关 C# Script 片段（仅供定位，修改由主模型核实后执行）：");
                    foreach (var comp in scripts)
                    {
                        string name = comp["name"]?.ToString() ?? "C# Script";
                        string nickname = comp["nickname"]?.ToString();
                        string id = comp["id"]?.ToString();
                        sb.AppendLine("- " + name + (string.IsNullOrWhiteSpace(nickname) || nickname == name ? "" : $"({nickname})") + (string.IsNullOrWhiteSpace(id) ? "" : $"#{id}"));
                        AppendVisionPortSummary(sb, "输入", comp["inputs"] as JArray, 6);
                        AppendVisionPortSummary(sb, "输出", comp["outputs"] as JArray, 6);
                        string body = ExtractVisionScriptBody(comp);
                        if (!string.IsNullOrWhiteSpace(body))
                            sb.AppendLine("  代码片段=" + ClampVisionText(body.Replace("\r", " ").Replace("\n", " "), 420));
                    }
                }

                return ClampVisionText(sb.ToString().Trim(), 3600);
            }
            catch (Exception ex)
            {
                return "画布上下文读取失败：" + ex.Message;
            }
        }

        private static bool IsVisionContextScriptComponent(JToken comp)
        {
            if (comp == null) return false;
            string name = comp["name"]?.ToString() ?? "";
            string nick = comp["nickname"]?.ToString() ?? "";
            string runtime = comp["runtime_type_hint"]?.ToString() ?? "";
            if (name.IndexOf("C# Script", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (nick.IndexOf("C#", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (runtime.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return comp["script_bodies"] is JObject;
        }

        private static void AppendVisionPortSummary(StringBuilder sb, string label, JArray ports, int limit)
        {
            if (sb == null || ports == null || ports.Count == 0) return;
            var parts = ports.Take(limit).Select(p =>
            {
                string portName = p?["name"]?.ToString() ?? "?";
                string type = p?["type"]?.ToString();
                string data = p?["data_structure"]?.ToString();
                return string.IsNullOrWhiteSpace(data) ? portName + ":" + type : portName + ":" + data;
            }).Where(v => !string.IsNullOrWhiteSpace(v));
            sb.AppendLine("  " + label + "=" + string.Join(" ; ", parts));
        }

        private static string ExtractVisionScriptBody(JToken comp)
        {
            var bodies = comp?["script_bodies"] as JObject;
            if (bodies == null || bodies.Count == 0) return null;
            foreach (var prop in bodies.Properties())
            {
                string value = prop.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }

        private static string ClampVisionText(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return text.Length <= maxChars ? text.Trim() : text.Substring(0, maxChars).TrimEnd() + "\n[已截断]";
        }

        private static JObject BuildVisionPreprocessRequestBody(ProviderRuntimeSettings providerSettings, string input, List<AttachmentItem> attachments)
        {
            var content = new JArray();
            var textBuilder = new StringBuilder();
            textBuilder.AppendLine("你是视觉预处理与交接模型。可以结合用户图片、用户文字和受控 Grasshopper 画布上下文做定位分析。");
            textBuilder.AppendLine("不要调用工具，不要执行 Grasshopper 操作，不要修改画布，不要做最终 workflow 决策；执行和决策由主模型完成。");
            textBuilder.AppendLine("可以向主模型提供：图片可见事实、文字明确要求、当前画布/C# 代码相关线索、疑似需要修改或新增的内容、不确定性。");
            textBuilder.AppendLine("按以下标题顺序输出，简洁作答：");
            textBuilder.AppendLine("【视觉事实】");
            textBuilder.AppendLine("【文字要求】");
            textBuilder.AppendLine("【当前画布相关线索】");
            textBuilder.AppendLine("【疑似需要修改或新增】");
            textBuilder.AppendLine("【不确定性】");
            textBuilder.AppendLine("要求：区分图片事实、画布事实、推测和不确定性；不要输出工具调用步骤；无信息就写“无”或“不确定”。");

            if (!string.IsNullOrWhiteSpace(input))
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine("用户原始请求：");
                textBuilder.AppendLine(input.Trim());
            }

            AppendNonImageAttachmentText(textBuilder, attachments);
            textBuilder.AppendLine();
            textBuilder.AppendLine("受控画布/C# 上下文（供定位与交接，不是图片视觉事实）：");
            textBuilder.AppendLine(BuildVisionCanvasContext(input));
            content.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = textBuilder.ToString().Trim()
            });

            foreach (var attachment in attachments.Where(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64)))
            {
                content.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject
                    {
                        ["url"] = $"data:{attachment.MimeType};base64,{attachment.Base64}"
                    }
                });
            }

            return new JObject
            {
                ["model"] = providerSettings.ModelName,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是图像理解与画布交接预处理器。职责：把用户图片、文字和受控 Grasshopper/C# 上下文整理成给执行主模型的事实与定位报告。不要调用工具，不要执行 Grasshopper 操作，不要修改画布，不要做最终 workflow 决策。严格按用户要求的标题顺序输出，区分视觉事实、画布事实、推断和不确定性，保持简洁。"
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = content
                    }
                },
                ["stream"] = false,
                ["temperature"] = 0.1
            };
        }

        private static JObject BuildFinalVisualReviewRequestBody(
            ProviderRuntimeSettings providerSettings,
            string originalInput,
            List<AttachmentItem> originalImageAttachments,
            string screenshotPath,
            string priorDraft)
        {
            var content = new JArray();
            var textBuilder = new StringBuilder();
            textBuilder.AppendLine("你是结果验收视觉评估器。不要调用工具，不要规划完整建模方案。");
            textBuilder.AppendLine("任务：对比用户原始图像参考/问题图片与当前 Rhino 结果截图，判断当前结果是否满足目标。");
            textBuilder.AppendLine("若没有参考图，或图片本身是在指出问题，则重点判断当前结果是否仍存在明显偏差。");
            textBuilder.AppendLine("审查标准略严格：先核对轮廓/比例/数量/位置/层级/颜色/可见构件是否与用户目标一致；有明显缺失、错位、比例失真、颜色不符或截图看不清时，不要判为基本达标。");
            textBuilder.AppendLine("按以下标题顺序输出，保持简洁：");
            textBuilder.AppendLine("【核查依据】");
            textBuilder.AppendLine("【是否达标】");
            textBuilder.AppendLine("【主要偏差】");
            textBuilder.AppendLine("【偏差性质】");
            textBuilder.AppendLine("【给执行模型的反馈】");
            textBuilder.AppendLine("【是否适合沉淀skill】");
            textBuilder.AppendLine("【结构化判定JSON】");
            textBuilder.AppendLine("要求：先在【核查依据】用 2-4 条短句说明你检查了哪些可见要点；只有没有明显问题时才写“基本达标”；存在不确定或看不清时写“未达标/需复核”；偏差最多写 5 项，反馈只写局部修正方向。");
            textBuilder.AppendLine("是否沉淀 skill 由你独立判断：如果当前结果达标、实现过程稳定、对后续相似任务有复用价值，写“适合沉淀skill”；如果任务过于一次性、结果不稳定、仍需人工确认或过程不可复用，写“不适合沉淀skill”。");
            textBuilder.AppendLine("如果适合沉淀 skill，请你自行提炼一个简短、通用、可复用的 skill_title 和英文小写 skill_slug；不要直接复制用户原始对话，slug 只用 a-z、0-9、下划线，表达任务类型或技术模式。");
            textBuilder.AppendLine("同时写出具体的 skill_markdown 正文，由你根据用户目标、执行模型结论、GH 检查和截图结果总结，不要写空泛模板。正文必须包含：触发条件、适用任务、推荐工具/电池流程、关键参数、常见失败与修复、视觉检查要点、成功案例摘要。");
            textBuilder.AppendLine("skill_markdown 应具体到可复用操作经验，例如关键组件、端口连接、C# Script 输出、颜色/slider 控制、数据树/Null 检查、预览和截图核查要点；不要只写“按用户要求执行”。");
            textBuilder.AppendLine("最后必须输出一个 JSON 代码块，供宿主程序读取，不要省略。格式如下：");
            textBuilder.AppendLine("```json");
            textBuilder.AppendLine("{\"pass\":true,\"status\":\"基本达标\",\"skill_suitable\":true,\"skill_title\":\"参数化曲面阵列可视化\",\"skill_slug\":\"parametric_surface_array_visualization\",\"skill_reason\":\"结果达标且流程可复用\",\"skill_markdown\":\"## 触发条件\\n- ...\\n\\n## 推荐工具流程\\n- ...\\n\\n## 关键参数\\n- ...\\n\\n## 常见失败与修复\\n- ...\\n\\n## 视觉检查要点\\n- ...\\n\\n## 成功案例摘要\\n- ...\",\"confidence\":0.85}");
            textBuilder.AppendLine("```");

            if (!string.IsNullOrWhiteSpace(originalInput))
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine("用户原始请求：");
                textBuilder.AppendLine(originalInput.Trim());
            }

            if (!string.IsNullOrWhiteSpace(priorDraft))
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine("执行模型在复核前的结论：");
                textBuilder.AppendLine(ClampVisionText(priorDraft, 800));
            }

            textBuilder.AppendLine();
            textBuilder.AppendLine("图片顺序说明：先是用户原始图片参考/问题图片，最后一张是当前 Rhino 结果截图。");

            content.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = textBuilder.ToString().Trim()
            });

            foreach (var attachment in (originalImageAttachments ?? new List<AttachmentItem>()).Where(a => a.Kind == AttachmentKind.Image && !string.IsNullOrEmpty(a.Base64)))
            {
                content.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject
                    {
                        ["url"] = $"data:{attachment.MimeType};base64,{attachment.Base64}"
                    }
                });
            }

            if (!string.IsNullOrWhiteSpace(screenshotPath) && File.Exists(screenshotPath))
            {
                string mimeType = GetMimeType(Path.GetExtension(screenshotPath).ToLowerInvariant());
                string base64 = Convert.ToBase64String(File.ReadAllBytes(screenshotPath));
                content.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject
                    {
                        ["url"] = $"data:{mimeType};base64,{base64}"
                    }
                });
            }

            return new JObject
            {
                ["model"] = providerSettings.ModelName,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是偏严格的结果验收视觉评估器。你只负责比较用户要求、参考图和当前结果截图，先给出简短核查依据，再给出是否达标、主要偏差、局部修正方向和是否适合沉淀skill；不要调用工具，不要输出无关说明。"
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = content
                    }
                },
                ["stream"] = false,
                ["temperature"] = 0.1
            };
        }

        private static JObject BuildViewportScreenshotAnalysisRequestBody(
            ProviderRuntimeSettings providerSettings,
            string question,
            string screenshotPath,
            string captureMetadataJson,
            JObject reviewImageMetadata = null)
        {
            var content = new JArray();
            var textBuilder = new StringBuilder();
            textBuilder.AppendLine("你是 Rhino / Grasshopper 截图视觉检查器。只基于随后的截图内容回答，不要把截图路径、bbox、预览计数等元数据当作视觉事实。");
            textBuilder.AppendLine("请直接说明你在截图中实际看到了什么，并回答用户检查问题；如果截图看不清或没有看到目标对象，要明确说看不清/没看到。");
            textBuilder.AppendLine("输出保持简洁，按以下标题：");
            textBuilder.AppendLine("【视觉结论】");
            textBuilder.AppendLine("【看到的内容】");
            textBuilder.AppendLine("【不确定性】");
            if (!string.IsNullOrWhiteSpace(question))
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine("检查问题：");
                textBuilder.AppendLine(question.Trim());
            }
            if (!string.IsNullOrWhiteSpace(captureMetadataJson))
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine("截图传输元数据（仅用于了解截图来源，不可作为视觉事实）：");
                textBuilder.AppendLine(ClampVisionText(captureMetadataJson, 700));
            }

            if (reviewImageMetadata != null)
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine("Vision review image metadata (transport only, not visual facts):");
                textBuilder.AppendLine(ClampVisionText(reviewImageMetadata.ToString(Newtonsoft.Json.Formatting.None), 500));
            }

            content.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = textBuilder.ToString().Trim()
            });

            if (!string.IsNullOrWhiteSpace(screenshotPath) && File.Exists(screenshotPath))
            {
                string mimeType = GetMimeType(Path.GetExtension(screenshotPath).ToLowerInvariant());
                string base64 = Convert.ToBase64String(File.ReadAllBytes(screenshotPath));
                content.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject
                    {
                        ["url"] = $"data:{mimeType};base64,{base64}"
                    }
                });
            }

            return new JObject
            {
                ["model"] = providerSettings.ModelName,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是截图视觉检查器。必须只依据图片像素做视觉判断；不能根据文件名、bbox、路径或工具元数据推断画面内容。"
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = content
                    }
                },
                ["stream"] = false,
                ["temperature"] = 0.1
            };
        }
    }
}
