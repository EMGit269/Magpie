using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Magpie.Agent;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static string ExecuteReadSkillFile(string fileName)
        {
            try {
                string catalogResult = ExecuteReadSkillFileWithCatalog(fileName);
                if (catalogResult != null)
                    return catalogResult;

                string skillsPath = GetSkillsDirectory();
                if (!fileName.EndsWith(".md")) fileName += ".md";
                string filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(skillsPath, System.IO.Path.GetFileName(fileName)));

                if (System.IO.File.Exists(filePath)) {
                    _contextLedger.RecordLoadedSkill(Path.GetFileNameWithoutExtension(fileName), Path.GetFileName(fileName), "read_skill_file legacy path");
                    return System.IO.File.ReadAllText(filePath, Encoding.UTF8);
                }
                return $"Error: 找不到技能文件 {fileName}";
            } catch (Exception ex) {
                return "Error: " + ex.Message;
            }
        }

        private static string ExecuteReadReferenceJson(string fileName)
        {
            try {
                if (string.IsNullOrWhiteSpace(fileName)) return "Error: file_name 不能为空。";
                if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) fileName += ".json";

                string referencePath = GetReferenceDirectory();
                string safeName = System.IO.Path.GetFileName(fileName);
                string filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(referencePath, safeName));
                string referenceFullPath = System.IO.Path.GetFullPath(referencePath);

                if (!filePath.StartsWith(referenceFullPath, StringComparison.OrdinalIgnoreCase))
                    return "Error: 非法 reference 文件路径。";

                if (!System.IO.File.Exists(filePath))
                    return $"Error: 找不到参考 JSON 文件 {safeName}";

                string json = System.IO.File.ReadAllText(filePath, Encoding.UTF8);
                _contextLedger.RecordReference(Path.GetFileNameWithoutExtension(safeName), safeName, ToolResultCompactor.Compact(json, 240));
                RefreshReferenceCatalog();
                return json;
            } catch (Exception ex) {
                return "Error: " + ex.Message;
            }
        }

        private static string ExecuteImportReferenceGh(string fileName, double? offsetX, double? offsetY, string groupName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileName)) return "Error: file_name 不能为空。";

                string name = Path.GetFileName(fileName.Trim());
                if (string.IsNullOrWhiteSpace(name)) return "Error: file_name 不能为空。";
                if (!name.EndsWith(".gh", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".ghx", StringComparison.OrdinalIgnoreCase))
                {
                    name += ".gh";
                }

                string ext = Path.GetExtension(name);
                if (!string.Equals(ext, ".gh", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ext, ".ghx", StringComparison.OrdinalIgnoreCase))
                {
                    return "Error: 只允许导入 reference 目录中的 .gh 或 .ghx 文件。";
                }

                string referencePath = GetReferenceDirectory();
                string referenceFullPath = Path.GetFullPath(referencePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string filePath = Path.GetFullPath(Path.Combine(referencePath, name));
                if (!filePath.StartsWith(referenceFullPath, StringComparison.OrdinalIgnoreCase))
                    return "Error: 非法 reference 文件路径。";
                if (!File.Exists(filePath))
                    return "Error: 找不到 reference GH 文件 " + name;

                string result = "";
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        var targetDoc = Grasshopper.Instances.ActiveCanvas?.Document;
                        if (targetDoc == null)
                        {
                            result = "Error: 没有打开的 Grasshopper 画布。";
                            return;
                        }

                        var io = new GH_DocumentIO();
                        if (!io.Open(filePath) || io.Document == null)
                        {
                            result = "Error: 无法打开 reference GH 文件 " + name;
                            return;
                        }

                        var sourceDoc = io.Document;
                        var sourceObjects = sourceDoc.Objects?.ToList() ?? new List<IGH_DocumentObject>();
                        if (sourceObjects.Count == 0)
                        {
                            result = "Error: reference GH 文件没有可导入对象。";
                            return;
                        }

                        float dx = (float)(offsetX ?? 0.0);
                        float dy = (float)(offsetY ?? 0.0);
                        if (Math.Abs(dx) > 0.001f || Math.Abs(dy) > 0.001f)
                        {
                            foreach (var obj in sourceObjects)
                            {
                                try
                                {
                                    if (obj?.Attributes == null) continue;
                                    var p = obj.Attributes.Pivot;
                                    obj.Attributes.Pivot = new PointF(p.X + dx, p.Y + dy);
                                    obj.Attributes.ExpireLayout();
                                }
                                catch (Exception ex)
                                {
                                    AddGhLog.Debug("ImportReferenceGh offset failed: " + ex.Message);
                                }
                            }
                        }

                        var before = new HashSet<Guid>(targetDoc.Objects.Select(o => o.InstanceGuid));
                        targetDoc.MergeDocument(sourceDoc, true);
                        var imported = targetDoc.Objects
                            .Where(o => o != null && !before.Contains(o.InstanceGuid))
                            .ToList();

                        GH_Group group = null;
                        if (!string.IsNullOrWhiteSpace(groupName) && imported.Count > 0)
                        {
                            group = new GH_Group
                            {
                                NickName = groupName.Trim(),
                                Colour = Color.FromArgb(80, 80, 160, 240)
                            };
                            foreach (var obj in imported)
                                group.AddObject(obj.InstanceGuid);
                            targetDoc.AddObject(group, false);
                            group.ExpireSolution(false);
                        }

                        RefreshPublicIdMap(targetDoc);
                        try { targetDoc.ScheduleSolution(150); } catch { }
                        try { Grasshopper.Instances.ActiveCanvas?.Refresh(); } catch { }

                        var payload = new JObject
                        {
                            ["status"] = "ok",
                            ["file_name"] = name,
                            ["path"] = filePath,
                            ["imported_count"] = imported.Count,
                            ["source_object_count"] = sourceObjects.Count,
                            ["offset"] = new JObject { ["x"] = dx, ["y"] = dy },
                            ["imported_ids"] = new JArray(imported.Take(80).Select(o => GetPublicId(targetDoc, o)))
                        };
                        if (group != null)
                        {
                            payload["group_id"] = GetPublicId(targetDoc, group);
                            payload["group_name"] = group.NickName;
                        }
                        if (imported.Count > 80)
                            payload["imported_ids_truncated"] = true;
                        result = payload.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    catch (Exception ex)
                    {
                        result = "Error: " + ex.Message;
                    }
                }));
                if (!result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                {
                    _contextLedger.RecordReference(Path.GetFileNameWithoutExtension(name), name, result);
                    RefreshReferenceCatalog();
                }
                return result;
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }

        private static string ExecuteCreateGhSkill(string fileName, string name, string description, string content)
        {
            try {
                string skillsPath = GetSkillsDirectory();
                if (!System.IO.Directory.Exists(skillsPath)) System.IO.Directory.CreateDirectory(skillsPath);
                if (!fileName.EndsWith(".md")) fileName += ".md";
                string filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(skillsPath, System.IO.Path.GetFileName(fileName)));

                string fileContent = $"---\nname: {name}\ndescription: {description}\n---\n\n{content}";
                System.IO.File.WriteAllText(filePath, fileContent, Encoding.UTF8);
                UpsertSkillCatalogEntry(System.IO.Path.GetFileName(filePath), "experimental", false);

                Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                    UpdateSkillLibraryUI();
                }));

                return $"技能 '{name}' 已成功保存至 {fileName}。";
            } catch (Exception ex) {
                return "Error: " + ex.Message;
            }
        }
    }
}
