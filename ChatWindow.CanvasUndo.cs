using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private sealed class CanvasUndoRecord
        {
            public string UndoId { get; set; }
            public string ToolName { get; set; }
            public string ToolCallId { get; set; }
            public string Summary { get; set; }
            public string SnapshotPath { get; set; }
            public bool IsDeltaSnapshot { get; set; }
            public string BaseUndoId { get; set; }
            public DateTime CreatedAtUtc { get; set; }
            public bool IsUndone { get; set; }
            public Button UndoButton { get; set; }
        }

        private static readonly List<CanvasUndoRecord> _canvasUndoStack = new List<CanvasUndoRecord>();
        private const int MaxCanvasUndoRecords = 30;
        private const int MaxCanvasUndoDeltaChainDepth = 8;
        private const int CanvasUndoDeltaBlockSize = 4096;
        private const int CanvasUndoDeltaMinSavings = 4096;
        private static readonly byte[] CanvasUndoDeltaMagic = Encoding.ASCII.GetBytes("ADDGHUNDO1");

        private static bool IsCanvasMutatingTool(string funcName)
        {
            switch (funcName ?? "")
            {
                case "ensure_gh_canvas":
                case "add_gh_component":
                case "connect_gh_components":
                case "remove_gh_component":
                case "set_gh_component_value":
                case "remove_gh_connection":
                case "create_component_graph":
                case "create_csharp_script_component":
                case "edit_csharp_script_component":
                case "create_script_component_graph":
                case "import_reference_gh":
                case "set_gh_component_status":
                case "set_all_csharp_script_previews":
                case "modify_gh_component_ports":
                case "modify_gh_port_data":
                case "manage_gh_groups":
                    return true;
                default:
                    return false;
            }
        }

        private static string CreateCanvasUndoSnapshot(string funcName, string callId)
        {
            string path = null;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) return;

                string dir = Path.Combine(GetProjectRootDirectory(), ".addgh", "undo");
                Directory.CreateDirectory(dir);
                string safeName = SanitizeUndoFilePart(funcName) + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".gh";
                string target = Path.Combine(dir, safeName);

                var io = new GH_DocumentIO();
                io.Document = doc;
                io.SaveQuiet(target);
                if (File.Exists(target))
                    path = target;
            }));
            return path;
        }

        private static string SanitizeUndoFilePart(string value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "tool" : value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                text = text.Replace(c, '_');
            return text.Length > 40 ? text.Substring(0, 40) : text;
        }

        private static string RegisterCanvasUndoRecord(string funcName, string callId, string snapshotPath, string toolResult)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
            {
                if (IsCanvasMutatingTool(funcName))
                    AddGhLog.Warn("Canvas undo snapshot missing for tool: " + (funcName ?? "?"));
                return null;
            }
            if (string.IsNullOrWhiteSpace(toolResult) || toolResult.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) return null;

            bool isDeltaSnapshot;
            string baseUndoId;
            string storedSnapshotPath = StoreCanvasUndoSnapshot(snapshotPath, out isDeltaSnapshot, out baseUndoId);
            if (string.IsNullOrWhiteSpace(storedSnapshotPath) || !File.Exists(storedSnapshotPath))
                return null;

            var record = new CanvasUndoRecord
            {
                UndoId = Guid.NewGuid().ToString("N"),
                ToolName = funcName ?? "",
                ToolCallId = callId ?? "",
                Summary = BuildUndoSummary(funcName, toolResult),
                SnapshotPath = storedSnapshotPath,
                IsDeltaSnapshot = isDeltaSnapshot,
                BaseUndoId = baseUndoId,
                CreatedAtUtc = DateTime.UtcNow
            };

            _canvasUndoStack.Add(record);
            while (_canvasUndoStack.Count > MaxCanvasUndoRecords)
            {
                var old = _canvasUndoStack[0];
                _canvasUndoStack.RemoveAt(0);
                TryDeleteUndoSnapshot(old);
            }
            return record.UndoId;
        }

        private static string StoreCanvasUndoSnapshot(string snapshotPath, out bool isDeltaSnapshot, out string baseUndoId)
        {
            isDeltaSnapshot = false;
            baseUndoId = null;

            var baseRecord = _canvasUndoStack.LastOrDefault(r => r != null && !string.IsNullOrWhiteSpace(r.SnapshotPath) && File.Exists(r.SnapshotPath));
            if (baseRecord == null || GetCanvasUndoSnapshotChainDepth(baseRecord) >= MaxCanvasUndoDeltaChainDepth)
                return snapshotPath;

            try
            {
                byte[] targetBytes = File.ReadAllBytes(snapshotPath);
                byte[] baseBytes = MaterializeCanvasUndoSnapshotBytes(baseRecord);
                if (targetBytes == null || baseBytes == null || targetBytes.Length == 0 || baseBytes.Length == 0)
                    return snapshotPath;

                byte[] deltaBytes = CreateCanvasUndoBinaryDelta(baseBytes, targetBytes);
                if (deltaBytes == null || deltaBytes.Length + CanvasUndoDeltaMinSavings >= targetBytes.Length)
                    return snapshotPath;

                string deltaPath = Path.ChangeExtension(snapshotPath, ".ghdelta");
                File.WriteAllBytes(deltaPath, deltaBytes);
                File.Delete(snapshotPath);

                isDeltaSnapshot = true;
                baseUndoId = baseRecord.UndoId;
                return deltaPath;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Canvas undo delta storage failed, keeping full snapshot: " + ex.Message);
                return snapshotPath;
            }
        }

        private static int GetCanvasUndoSnapshotChainDepth(CanvasUndoRecord record)
        {
            int depth = 0;
            var current = record;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (current != null && current.IsDeltaSnapshot && !string.IsNullOrWhiteSpace(current.BaseUndoId))
            {
                if (!visited.Add(current.UndoId ?? "")) break;
                depth++;
                current = _canvasUndoStack.FirstOrDefault(r => string.Equals(r?.UndoId, current.BaseUndoId, StringComparison.OrdinalIgnoreCase));
            }
            return depth;
        }

        private static string BuildUndoSummary(string funcName, string toolResult)
        {
            string name = string.IsNullOrWhiteSpace(funcName) ? "画布操作" : funcName.Trim();
            string detail = (toolResult ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (detail.Length > 90) detail = detail.Substring(0, 90) + "...";
            return string.IsNullOrWhiteSpace(detail) ? name : name + " · " + detail;
        }

        private static List<CanvasUndoRecord> GetUndoRecordsFrom(CanvasUndoRecord selected)
        {
            var records = new List<CanvasUndoRecord>();
            if (selected == null) return records;

            int start = _canvasUndoStack.IndexOf(selected);
            if (start < 0) return records;
            for (int i = start; i < _canvasUndoStack.Count; i++)
            {
                var record = _canvasUndoStack[i];
                if (record != null && !record.IsUndone)
                    records.Add(record);
            }
            return records;
        }

        private static CanvasUndoRecord FindLatestUndoableRecord()
        {
            for (int i = _canvasUndoStack.Count - 1; i >= 0; i--)
            {
                var record = _canvasUndoStack[i];
                if (record != null && !record.IsUndone)
                    return record;
            }
            return null;
        }

        private static void AttachUndoButtonToStatsCard(Border card, string undoId)
        {
            var record = _canvasUndoStack.FirstOrDefault(r => r != null && r.UndoId == undoId);
            if (record == null || card == null || record.UndoButton != null) return;

            var grid = card.Child as Grid;
            if (grid == null) return;

            if (grid.ColumnDefinitions.Count < 3)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var button = new Button
            {
                Content = "撤销",
                Tag = record.UndoId,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(10, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            button.Content = "↶ 撤销";
            button.FontSize = 11;
            button.Padding = new Thickness(9, 3, 9, 3);
            button.Margin = new Thickness(12, 0, 0, 0);
            button.Content = "↶ 撤销";
            button.Template = BuildSmallUndoButtonTemplate();
            button.Click += (s, e) => TryUndoCanvasOperation(record.UndoId);

            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
            record.UndoButton = button;
        }

        private static void AttachUnavailableUndoButtonToStatsCard(Border card)
        {
            if (card == null) return;
            var grid = card.Child as Grid;
            if (grid == null) return;

            if (grid.ColumnDefinitions.Count < 3)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var button = new Button
            {
                Content = "不可撤销",
                FontSize = 11,
                Foreground = ThemeBrush(Color.FromRgb(122, 128, 140), Color.FromRgb(130, 130, 130)),
                Background = Brushes.Transparent,
                BorderBrush = ThemeBrush(Color.FromRgb(214, 218, 225), Color.FromRgb(55, 55, 55)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = false,
                ToolTip = "这条统计没有可用的撤销快照，常见于历史会话刷新、工具失败或快照创建失败。"
            };
            button.Template = BuildSmallUndoButtonTemplate();

            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }

        private static ControlTemplate BuildSmallUndoButtonTemplate()
        {
            const string xaml =
                @"<ControlTemplate TargetType=""Button"" xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
                    <Border x:Name=""Bd"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""{TemplateBinding BorderThickness}"" CornerRadius=""6"" Padding=""{TemplateBinding Padding}"">
                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""Bd"" Property=""Background"" Value=""#2A2A2A""/>
                        </Trigger>
                        <Trigger Property=""IsEnabled"" Value=""False"">
                            <Setter Property=""Opacity"" Value=""0.55""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>";
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        private static void TryUndoCanvasOperation(string undoId)
        {
            var record = _canvasUndoStack.FirstOrDefault(r => r != null && r.UndoId == undoId);
            if (record == null || record.IsUndone) return;

            var affectedRecords = GetUndoRecordsFrom(record);
            if (affectedRecords.Count == 0) return;

            var historyConfirm = System.Windows.MessageBox.Show(
                "将强制回滚 Grasshopper 画布到这次操作执行前的状态。\n\n这会覆盖此操作之后的手动改动，并使这次及之后的 agent 画布操作都视为已撤销。\n\n是否继续？",
                "撤销历史 agent 画布操作",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (historyConfirm != MessageBoxResult.OK) return;

            string historyError = RestoreCanvasUndoSnapshot(record);
            if (!string.IsNullOrWhiteSpace(historyError))
            {
                AppendQuietDiagnosticCard("撤销画布操作", "回滚失败：" + historyError);
                return;
            }

            foreach (var affected in affectedRecords)
            {
                affected.IsUndone = true;
                if (affected.UndoButton != null)
                {
                    affected.UndoButton.Content = "已撤销";
                    affected.UndoButton.IsEnabled = false;
                }
            }

            TruncateConversationAfterCanvasUndo(record.ToolCallId, record.Summary, affectedRecords.Count);
            /* _ = new JObject
            {
                ["role"] = "system",
                ["content"] = "用户撤销了一次历史画布操作，当前 Grasshopper 画布已回退到所选操作执行前的状态；该操作及之后的 agent 画布操作不再可作为当前上下文依据。"
            }; */
            EnforceChatHistoryLimit();
            SyncActiveHistoryConversation();
            NotifyCanvasConversationChanged(true);
            AppendSystemMessage("已撤销历史画布操作：" + record.Summary);
            if (affectedRecords.Count >= 0) return;

            var latest = FindLatestUndoableRecord();
            if (!ReferenceEquals(record, latest))
            {
                AppendQuietDiagnosticCard("撤销画布操作", "只能从最近一次 agent 画布操作开始撤销。请先撤销更新的操作。");
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                "将强制回滚 Grasshopper 画布到该操作执行前的状态，可能覆盖此后你手动修改的画布内容。\n\n是否继续？",
                "撤销 agent 画布操作",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            string error = RestoreCanvasUndoSnapshot(record);
            if (!string.IsNullOrWhiteSpace(error))
            {
                AppendQuietDiagnosticCard("撤销画布操作", "回滚失败：" + error);
                return;
            }

            record.IsUndone = true;
            if (record.UndoButton != null)
            {
                record.UndoButton.Content = "已撤销";
                record.UndoButton.Content = "已撤销";
                record.UndoButton.IsEnabled = false;
            }

            PruneMessagesForUndoneTool(record.ToolCallId);
            _messages.Add(new JObject
            {
                ["role"] = "system",
                ["content"] = "用户撤销了一次画布操作，当前 Grasshopper 画布已回退到撤销后的状态。"
            });
            EnforceChatHistoryLimit();
            SyncActiveHistoryConversation();
            NotifyCanvasConversationChanged(true);
            AppendSystemMessage("已撤销：" + record.Summary);
        }

        private static string RestoreCanvasUndoSnapshot(CanvasUndoRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.SnapshotPath) || !File.Exists(record.SnapshotPath))
                return "撤销快照不存在。";

            string error = null;
            string materializedPath = null;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    materializedPath = MaterializeCanvasUndoSnapshotFile(record);
                    if (string.IsNullOrWhiteSpace(materializedPath) || !File.Exists(materializedPath))
                    {
                        error = "Undo snapshot could not be materialized.";
                        return;
                    }

                    var activeCanvas = Grasshopper.Instances.ActiveCanvas;
                    var current = activeCanvas?.Document;
                    var preservedUserReferenceData = CaptureCanvasUndoUserReferenceData(current);

                    var io = new GH_DocumentIO();
                    if (!io.Open(materializedPath))
                    {
                        error = "无法打开撤销快照。";
                        return;
                    }

                    var restored = io.Document;
                    if (restored == null)
                    {
                        error = "撤销快照没有有效 GH 文档。";
                        return;
                    }

                    RestoreCanvasUndoDocumentFileIdentity(restored, current);
                    ApplyCanvasUndoUserReferenceData(restored, preservedUserReferenceData);

                    var server = Grasshopper.Instances.DocumentServer;
                    if (server != null && current != null)
                    {
                        try { server.RemoveDocument(current); } catch { }
                    }
                    if (server != null)
                    {
                        try { server.AddDocument(restored); } catch { }
                    }
                    if (activeCanvas != null)
                    {
                        activeCanvas.Document = restored;
                        activeCanvas.Refresh();
                    }

                    ResetPublicIdMap(restored);
                    RefreshPublicIdMap(restored);
                    _canvasChanged = true;
                    _cachedCanvasState = null;
                    try { restored.ScheduleSolution(80); } catch { }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    if (record.IsDeltaSnapshot && !string.IsNullOrWhiteSpace(materializedPath))
                    {
                        try { File.Delete(materializedPath); } catch { }
                    }
                }
            }));
            return error;
        }

        private static string MaterializeCanvasUndoSnapshotFile(CanvasUndoRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.SnapshotPath) || !File.Exists(record.SnapshotPath))
                return null;
            if (!record.IsDeltaSnapshot)
                return record.SnapshotPath;

            byte[] bytes = MaterializeCanvasUndoSnapshotBytes(record);
            if (bytes == null || bytes.Length == 0)
                return null;

            string dir = Path.GetDirectoryName(record.SnapshotPath) ?? Path.Combine(GetProjectRootDirectory(), ".addgh", "undo");
            string path = Path.Combine(dir, "restore_" + record.UndoId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".gh");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static byte[] MaterializeCanvasUndoSnapshotBytes(CanvasUndoRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.SnapshotPath) || !File.Exists(record.SnapshotPath))
                return null;
            if (!record.IsDeltaSnapshot)
                return File.ReadAllBytes(record.SnapshotPath);

            var baseRecord = _canvasUndoStack.FirstOrDefault(r => string.Equals(r?.UndoId, record.BaseUndoId, StringComparison.OrdinalIgnoreCase));
            if (baseRecord == null)
                return null;

            byte[] baseBytes = MaterializeCanvasUndoSnapshotBytes(baseRecord);
            byte[] deltaBytes = File.ReadAllBytes(record.SnapshotPath);
            return ApplyCanvasUndoBinaryDelta(baseBytes, deltaBytes);
        }

        private static byte[] CreateCanvasUndoBinaryDelta(byte[] baseBytes, byte[] targetBytes)
        {
            var blockMap = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var sha = SHA256.Create())
            {
                for (int offset = 0; offset < baseBytes.Length; offset += CanvasUndoDeltaBlockSize)
                {
                    int length = Math.Min(CanvasUndoDeltaBlockSize, baseBytes.Length - offset);
                    string key = Convert.ToBase64String(sha.ComputeHash(baseBytes, offset, length)) + ":" + length.ToString();
                    if (!blockMap.ContainsKey(key))
                        blockMap.Add(key, offset);
                }

                using (var raw = new MemoryStream())
                using (var writer = new BinaryWriter(raw))
                {
                    writer.Write(CanvasUndoDeltaMagic);
                    writer.Write(baseBytes.LongLength);
                    writer.Write(targetBytes.LongLength);
                    writer.Write(sha.ComputeHash(baseBytes));
                    writer.Write(sha.ComputeHash(targetBytes));
                    writer.Write(CanvasUndoDeltaBlockSize);

                    using (var ops = new MemoryStream())
                    using (var opsWriter = new BinaryWriter(ops))
                    {
                        int opCount = 0;
                        for (int offset = 0; offset < targetBytes.Length; offset += CanvasUndoDeltaBlockSize)
                        {
                            int length = Math.Min(CanvasUndoDeltaBlockSize, targetBytes.Length - offset);
                            string key = Convert.ToBase64String(sha.ComputeHash(targetBytes, offset, length)) + ":" + length.ToString();
                            int baseOffset;
                            if (blockMap.TryGetValue(key, out baseOffset))
                            {
                                opsWriter.Write((byte)0);
                                opsWriter.Write(baseOffset);
                                opsWriter.Write(length);
                            }
                            else
                            {
                                opsWriter.Write((byte)1);
                                opsWriter.Write(length);
                                opsWriter.Write(targetBytes, offset, length);
                            }
                            opCount++;
                        }

                        writer.Write(opCount);
                        opsWriter.Flush();
                        writer.Write(ops.ToArray());
                    }
                    writer.Flush();

                    using (var compressed = new MemoryStream())
                    {
                        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, true))
                        {
                            byte[] rawBytes = raw.ToArray();
                            gzip.Write(rawBytes, 0, rawBytes.Length);
                        }
                        return compressed.ToArray();
                    }
                }
            }
        }

        private static byte[] ApplyCanvasUndoBinaryDelta(byte[] baseBytes, byte[] deltaBytes)
        {
            if (baseBytes == null || deltaBytes == null)
                return null;

            using (var compressed = new MemoryStream(deltaBytes))
            using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
            using (var raw = new MemoryStream())
            {
                gzip.CopyTo(raw);
                raw.Position = 0;
                using (var reader = new BinaryReader(raw))
                using (var sha = SHA256.Create())
                {
                    byte[] magic = reader.ReadBytes(CanvasUndoDeltaMagic.Length);
                    if (!CanvasUndoDeltaMagic.SequenceEqual(magic))
                        return null;

                    long baseLength = reader.ReadInt64();
                    long targetLength = reader.ReadInt64();
                    byte[] expectedBaseHash = reader.ReadBytes(32);
                    byte[] expectedTargetHash = reader.ReadBytes(32);
                    int blockSize = reader.ReadInt32();
                    int opCount = reader.ReadInt32();

                    if (baseLength != baseBytes.LongLength || blockSize != CanvasUndoDeltaBlockSize)
                        return null;
                    if (!expectedBaseHash.SequenceEqual(sha.ComputeHash(baseBytes)))
                        return null;

                    using (var target = new MemoryStream())
                    {
                        for (int i = 0; i < opCount; i++)
                        {
                            byte op = reader.ReadByte();
                            if (op == 0)
                            {
                                int offset = reader.ReadInt32();
                                int length = reader.ReadInt32();
                                if (offset < 0 || length < 0 || offset + length > baseBytes.Length)
                                    return null;
                                target.Write(baseBytes, offset, length);
                            }
                            else if (op == 1)
                            {
                                int length = reader.ReadInt32();
                                if (length < 0)
                                    return null;
                                byte[] data = reader.ReadBytes(length);
                                if (data.Length != length)
                                    return null;
                                target.Write(data, 0, data.Length);
                            }
                            else
                            {
                                return null;
                            }
                        }

                        byte[] targetBytes = target.ToArray();
                        if (targetBytes.LongLength != targetLength)
                            return null;
                        if (!expectedTargetHash.SequenceEqual(sha.ComputeHash(targetBytes)))
                            return null;
                        return targetBytes;
                    }
                }
            }
        }

        private static void RestoreCanvasUndoDocumentFileIdentity(Grasshopper.Kernel.GH_Document restored, Grasshopper.Kernel.GH_Document previous)
        {
            if (restored == null)
                return;

            try
            {
                string previousPath = null;
                bool hasPreviousPath = previous != null && previous.IsFilePathDefined && !string.IsNullOrWhiteSpace(previous.FilePath);
                if (hasPreviousPath)
                    previousPath = previous.FilePath;

                PropertyInfo filePathProp = restored.GetType().GetProperty("FilePath", BindingFlags.Instance | BindingFlags.Public);
                if (filePathProp != null && filePathProp.CanWrite)
                    filePathProp.SetValue(restored, previousPath, null);

                restored.IsModified = true;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("Restore canvas undo document file path failed: " + ex.Message);
            }
        }

        private sealed class CanvasUndoParamDataPatch
        {
            public Guid ParamId { get; set; }
            public object PersistentData { get; set; }
            public string ParamTypeName { get; set; }
        }

        private static List<CanvasUndoParamDataPatch> CaptureCanvasUndoUserReferenceData(Grasshopper.Kernel.GH_Document doc)
        {
            var result = new List<CanvasUndoParamDataPatch>();
            if (doc == null) return result;

            foreach (var param in doc.Objects.OfType<Grasshopper.Kernel.IGH_Param>())
            {
                try
                {
                    if (!ShouldPreserveCanvasUndoParamData(param))
                        continue;

                    object data = TryDuplicatePersistentData(param);
                    if (data == null)
                        continue;

                    result.Add(new CanvasUndoParamDataPatch
                    {
                        ParamId = param.InstanceGuid,
                        PersistentData = data,
                        ParamTypeName = param.GetType().FullName ?? ""
                    });
                }
                catch (Exception ex)
                {
                    AddGhLog.Debug("Capture undo param data skipped: " + ex.Message);
                }
            }

            return result;
        }

        private static void ApplyCanvasUndoUserReferenceData(Grasshopper.Kernel.GH_Document restored, List<CanvasUndoParamDataPatch> patches)
        {
            if (restored == null || patches == null || patches.Count == 0)
                return;

            int applied = 0;
            foreach (var patch in patches)
            {
                try
                {
                    var target = restored.FindObject(patch.ParamId, true) as Grasshopper.Kernel.IGH_Param;
                    if (target == null || !ShouldPreserveCanvasUndoParamData(target))
                        continue;

                    if (!string.Equals(target.GetType().FullName ?? "", patch.ParamTypeName ?? "", StringComparison.Ordinal))
                        continue;

                    if (TrySetPersistentData(target, patch.PersistentData))
                    {
                        try { target.ExpireSolution(false); } catch { }
                        applied++;
                    }
                }
                catch (Exception ex)
                {
                    AddGhLog.Debug("Apply undo param data skipped: " + ex.Message);
                }
            }

            if (applied > 0)
                AddGhLog.Debug("Preserved " + applied.ToString() + " user reference parameter(s) across canvas undo.");
        }

        private static bool ShouldPreserveCanvasUndoParamData(Grasshopper.Kernel.IGH_Param param)
        {
            if (param == null || param.SourceCount > 0)
                return false;

            int count = ReadPersistentDataCount(param);
            if (count <= 0)
                return false;

            string typeName = param.GetType().Name ?? "";
            switch (typeName)
            {
                case "Param_Point":
                case "Param_Vector":
                case "Param_Plane":
                case "Param_Line":
                case "Param_Circle":
                case "Param_Arc":
                case "Param_Rectangle":
                case "Param_Box":
                case "Param_Curve":
                case "Param_Surface":
                case "Param_Brep":
                case "Param_Mesh":
                case "Param_Geometry":
                    return true;
                default:
                    return false;
            }
        }

        private static int ReadPersistentDataCount(Grasshopper.Kernel.IGH_Param param)
        {
            try
            {
                PropertyInfo prop = param.GetType().GetProperty("PersistentDataCount", BindingFlags.Instance | BindingFlags.Public);
                if (prop == null) return 0;
                object value = prop.GetValue(param, null);
                return value is int count ? count : 0;
            }
            catch { return 0; }
        }

        private static object TryDuplicatePersistentData(Grasshopper.Kernel.IGH_Param param)
        {
            PropertyInfo prop = param.GetType().GetProperty("PersistentData", BindingFlags.Instance | BindingFlags.Public);
            object data = prop?.GetValue(param, null);
            if (data == null)
                return null;

            MethodInfo duplicate = data.GetType().GetMethod("Duplicate", BindingFlags.Instance | BindingFlags.Public);
            return duplicate != null ? duplicate.Invoke(data, null) : null;
        }

        private static bool TrySetPersistentData(Grasshopper.Kernel.IGH_Param param, object persistentData)
        {
            if (param == null || persistentData == null)
                return false;

            MethodInfo method = param.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m =>
                {
                    if (!string.Equals(m.Name, "SetPersistentData", StringComparison.Ordinal))
                        return false;
                    var args = m.GetParameters();
                    return args.Length == 1 && args[0].ParameterType.IsAssignableFrom(persistentData.GetType());
                });

            if (method == null)
                return false;

            method.Invoke(param, new[] { persistentData });
            return true;
        }

        private static void TruncateConversationAfterCanvasUndo(string toolCallId, string summary, int affectedCount)
        {
            int messageCut = FindCanvasUndoToolCallMessageIndex(_messages, toolCallId);
            int displayCut = FindCanvasUndoToolCallMessageIndex(_displayMessages, toolCallId);

            if (messageCut >= 0)
                _messages.RemoveRange(messageCut, _messages.Count - messageCut);
            else
                PruneMessagesForUndoneTool(toolCallId);

            if (_displayMessages != null)
            {
                if (displayCut >= 0)
                    _displayMessages.RemoveRange(displayCut, _displayMessages.Count - displayCut);
                else
                    PruneDisplayMessagesForUndoneTool(toolCallId);
            }

            string marker = "用户撤销了一次历史 Grasshopper 画布操作；对话上下文已从该操作处截断，撤销点及其之后的 agent 画布操作不再作为当前上下文依据。"
                + " 受影响操作数：" + affectedCount.ToString()
                + "。撤销摘要：" + (summary ?? "");

            var markerMessage = new JObject
            {
                ["role"] = "system",
                ["content"] = marker,
                ["addgh_context_marker"] = "canvas_undo_truncate",
                ["undone_tool_call_id"] = toolCallId ?? "",
                ["affected_canvas_operations"] = affectedCount
            };
            _messages.Add(markerMessage);
            AddDisplayMessage(markerMessage);
        }

        private static int FindCanvasUndoToolCallMessageIndex(IList<object> messages, string toolCallId)
        {
            if (messages == null || string.IsNullOrWhiteSpace(toolCallId))
                return -1;

            int toolResultIndex = -1;
            for (int i = 0; i < messages.Count; i++)
            {
                var jo = messages[i] as JObject ?? JObject.FromObject(messages[i]);
                if (MessageContainsToolCall(jo, toolCallId))
                    return i;

                string role = jo["role"]?.ToString();
                if (toolResultIndex < 0
                    && string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(jo["tool_call_id"]?.ToString(), toolCallId, StringComparison.Ordinal))
                {
                    toolResultIndex = i;
                }
            }

            return toolResultIndex;
        }

        private static bool MessageContainsToolCall(JObject jo, string toolCallId)
        {
            if (jo == null || string.IsNullOrWhiteSpace(toolCallId))
                return false;

            var calls = jo["tool_calls"] as JArray;
            if (calls == null)
                return false;

            foreach (var call in calls)
            {
                if (string.Equals(call?["id"]?.ToString(), toolCallId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static void PruneDisplayMessagesForUndoneTool(string toolCallId)
        {
            if (string.IsNullOrWhiteSpace(toolCallId) || _displayMessages == null) return;

            for (int i = _displayMessages.Count - 1; i >= 0; i--)
            {
                var jo = _displayMessages[i] as JObject ?? JObject.FromObject(_displayMessages[i]);
                string role = jo["role"]?.ToString();
                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(jo["tool_call_id"]?.ToString(), toolCallId, StringComparison.Ordinal))
                {
                    _displayMessages.RemoveAt(i);
                    break;
                }
            }

            for (int i = _displayMessages.Count - 1; i >= 0; i--)
            {
                var jo = _displayMessages[i] as JObject ?? JObject.FromObject(_displayMessages[i]);
                if (!string.Equals(jo["role"]?.ToString(), "assistant", StringComparison.OrdinalIgnoreCase)) continue;
                var calls = jo["tool_calls"] as JArray;
                if (calls == null) continue;

                bool removed = false;
                for (int j = calls.Count - 1; j >= 0; j--)
                {
                    if (string.Equals(calls[j]?["id"]?.ToString(), toolCallId, StringComparison.Ordinal))
                    {
                        calls.RemoveAt(j);
                        removed = true;
                    }
                }

                string content = jo["content"]?.ToString();
                string reasoning = jo["reasoning_content"]?.ToString();
                if (removed && calls.Count == 0 && string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(reasoning))
                    _displayMessages.RemoveAt(i);
                if (removed) break;
            }
        }

        private static void PruneMessagesForUndoneTool(string toolCallId)
        {
            if (string.IsNullOrWhiteSpace(toolCallId) || _messages == null) return;

            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                var jo = _messages[i] as JObject ?? JObject.FromObject(_messages[i]);
                string role = jo["role"]?.ToString();
                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(jo["tool_call_id"]?.ToString(), toolCallId, StringComparison.Ordinal))
                {
                    _messages.RemoveAt(i);
                    break;
                }
            }

            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                var jo = _messages[i] as JObject ?? JObject.FromObject(_messages[i]);
                if (!string.Equals(jo["role"]?.ToString(), "assistant", StringComparison.OrdinalIgnoreCase)) continue;
                var calls = jo["tool_calls"] as JArray;
                if (calls == null) continue;

                bool removed = false;
                for (int j = calls.Count - 1; j >= 0; j--)
                {
                    if (string.Equals(calls[j]?["id"]?.ToString(), toolCallId, StringComparison.Ordinal))
                    {
                        calls.RemoveAt(j);
                        removed = true;
                    }
                }

                string content = jo["content"]?.ToString();
                string reasoning = jo["reasoning_content"]?.ToString();
                if (removed && calls.Count == 0 && string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(reasoning))
                    _messages.RemoveAt(i);
                if (removed) break;
            }
        }

        private static void TryDeleteUndoSnapshot(CanvasUndoRecord record)
        {
            try
            {
                PromoteCanvasUndoDeltaChildren(record);
                if (!string.IsNullOrWhiteSpace(record?.SnapshotPath) && File.Exists(record.SnapshotPath))
                    File.Delete(record.SnapshotPath);
            }
            catch { }
        }

        private static void PromoteCanvasUndoDeltaChildren(CanvasUndoRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.UndoId))
                return;

            foreach (var child in _canvasUndoStack.Where(r => r != null && r.IsDeltaSnapshot && string.Equals(r.BaseUndoId, record.UndoId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try
                {
                    byte[] bytes = MaterializeCanvasUndoSnapshotBytes(child);
                    if (bytes == null || bytes.Length == 0)
                        continue;

                    string fullPath = Path.ChangeExtension(child.SnapshotPath, ".gh");
                    File.WriteAllBytes(fullPath, bytes);
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(child.SnapshotPath) && File.Exists(child.SnapshotPath))
                            File.Delete(child.SnapshotPath);
                    }
                    catch { }

                    child.SnapshotPath = fullPath;
                    child.IsDeltaSnapshot = false;
                    child.BaseUndoId = null;
                }
                catch (Exception ex)
                {
                    AddGhLog.Warn("Canvas undo delta promotion failed: " + ex.Message);
                }
            }
        }
    }
}
