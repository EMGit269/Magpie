using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.IO;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Grasshopper.Kernel;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Script;
using Rhino.Geometry;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static string ExecuteEnsureGhCanvas()
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    var currentDoc = Grasshopper.Instances.ActiveCanvas?.Document;
                    if (currentDoc != null)
                    {
                        result = "当前已存在可用 Grasshopper 画布。";
                        return;
                    }

                    try
                    {
                        var editor = Grasshopper.Instances.DocumentEditor;
                        if (editor != null)
                        {
                            var showMethod = editor.GetType().GetMethod("Show", Type.EmptyTypes);
                            showMethod?.Invoke(editor, null);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddGhLog.Debug("DocumentEditor.Show fallback: " + ex.Message);
                    }

                    var doc = new Grasshopper.Kernel.GH_Document();
                    bool addedToServer = false;

                    var server = Grasshopper.Instances.DocumentServer;
                    if (server != null)
                    {
                        foreach (var method in server.GetType().GetMethods().Where(m => m.Name == "AddDocument"))
                        {
                            var parameters = method.GetParameters();
                            if (parameters.Length == 0 || !parameters[0].ParameterType.IsAssignableFrom(typeof(Grasshopper.Kernel.GH_Document))) continue;

                            object[] callArgs = new object[parameters.Length];
                            callArgs[0] = doc;
                            for (int i = 1; i < parameters.Length; i++)
                            {
                                callArgs[i] = parameters[i].ParameterType == typeof(bool) ? (object)true : Type.Missing;
                            }

                            method.Invoke(server, callArgs);
                            addedToServer = true;
                            break;
                        }
                    }

                    var canvas = Grasshopper.Instances.ActiveCanvas;
                    if (canvas != null)
                    {
                        var docProp = canvas.GetType().GetProperty("Document");
                        if (docProp != null && docProp.CanWrite)
                        {
                            docProp.SetValue(canvas, doc, null);
                        }
                        canvas.Refresh();
                    }

                    result = addedToServer
                        ? "未检测到可用画布，已新建空白 Grasshopper 画布。"
                        : "未检测到可用画布，已创建空白 Grasshopper 文档，但未能加入文档服务器。";
                }
                catch (Exception ex)
                {
                    result = "Error: 新建 Grasshopper 画布失败 - " + ex.Message;
                }
            }));
            return result;
        }

        private static string GetRhinoUnitSignature()
        {
            var rhinoDoc = Rhino.RhinoDoc.ActiveDoc;
            if (rhinoDoc == null) return "no-rhino-doc";
            return string.Join("|",
                rhinoDoc.ModelUnitSystem,
                rhinoDoc.PageUnitSystem,
                rhinoDoc.ModelAbsoluteTolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                rhinoDoc.ModelRelativeTolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                rhinoDoc.ModelAngleToleranceDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                rhinoDoc.PageAbsoluteTolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                rhinoDoc.PageRelativeTolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                rhinoDoc.PageAngleToleranceDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static JObject BuildRhinoUnitsJson()
        {
            var rhinoDoc = Rhino.RhinoDoc.ActiveDoc;
            if (rhinoDoc == null)
            {
                return new JObject { ["available"] = false };
            }

            return new JObject
            {
                ["available"] = true,
                ["model_unit_system"] = rhinoDoc.ModelUnitSystem.ToString(),
                ["model_unit_system_value"] = (int)rhinoDoc.ModelUnitSystem,
                ["page_unit_system"] = rhinoDoc.PageUnitSystem.ToString(),
                ["page_unit_system_value"] = (int)rhinoDoc.PageUnitSystem,
                ["model_absolute_tolerance"] = rhinoDoc.ModelAbsoluteTolerance,
                ["model_relative_tolerance"] = rhinoDoc.ModelRelativeTolerance,
                ["model_angle_tolerance_degrees"] = rhinoDoc.ModelAngleToleranceDegrees,
                ["page_absolute_tolerance"] = rhinoDoc.PageAbsoluteTolerance,
                ["page_relative_tolerance"] = rhinoDoc.PageRelativeTolerance,
                ["page_angle_tolerance_degrees"] = rhinoDoc.PageAngleToleranceDegrees
            };
        }

        private static void ResetPublicIdMap(Grasshopper.Kernel.GH_Document doc)
        {
            _publicIdBoundDocument = doc;
            _publicIdByGuid.Clear();
            _guidByPublicId.Clear();
            _nextComponentPublicId = 1;
            _nextGroupPublicId = 1;
        }

        private static void RefreshPublicIdMap(Grasshopper.Kernel.GH_Document doc)
        {
            if (doc == null)
            {
                ResetPublicIdMap(null);
                return;
            }

            if (!ReferenceEquals(_publicIdBoundDocument, doc))
                ResetPublicIdMap(doc);

            var liveGuids = new HashSet<Guid>(doc.Objects.Select(o => o.InstanceGuid));
            foreach (var stale in _publicIdByGuid.Keys.Where(g => !liveGuids.Contains(g)).ToList())
            {
                string publicId = _publicIdByGuid[stale];
                _publicIdByGuid.Remove(stale);
                _guidByPublicId.Remove(publicId);
            }

            foreach (var obj in doc.Objects)
            {
                if (_publicIdByGuid.ContainsKey(obj.InstanceGuid))
                    continue;

                bool isGroup = obj is Grasshopper.Kernel.Special.GH_Group;
                string publicId = isGroup
                    ? "G" + _nextGroupPublicId.ToString("D2")
                    : _nextComponentPublicId.ToString("D2");
                if (isGroup) _nextGroupPublicId++;
                else _nextComponentPublicId++;

                _publicIdByGuid[obj.InstanceGuid] = publicId;
                _guidByPublicId[publicId] = obj.InstanceGuid;
            }
        }

        private static string GetPublicId(Grasshopper.Kernel.GH_Document doc, Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            if (obj == null)
                return "";

            RefreshPublicIdMap(doc);
            if (_publicIdByGuid.TryGetValue(obj.InstanceGuid, out string publicId))
                return publicId;
            return obj.InstanceGuid.ToString();
        }

        private static string GetPublicId(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            return GetPublicId(Grasshopper.Instances.ActiveCanvas?.Document, obj);
        }

        private static string NormalizePublicId(string id)
        {
            string value = (id ?? "").Trim();
            if (value.StartsWith("#", StringComparison.Ordinal))
                value = value.Substring(1).Trim();
            return value;
        }

        private static bool TryResolveGuidFromPublicId(Grasshopper.Kernel.GH_Document doc, string id, out Guid guid)
        {
            guid = Guid.Empty;
            string token = NormalizePublicId(id);
            if (string.IsNullOrWhiteSpace(token))
                return false;

            if (Guid.TryParse(token, out guid))
                return true;

            RefreshPublicIdMap(doc);
            return _guidByPublicId.TryGetValue(token, out guid);
        }

        private static Grasshopper.Kernel.IGH_DocumentObject FindDocumentObjectByAnyId(Grasshopper.Kernel.GH_Document doc, string id)
        {
            if (doc == null)
                return null;

            if (!TryResolveGuidFromPublicId(doc, id, out Guid guid))
                return null;

            return doc.FindObject(guid, true);
        }

        private static bool ObjectMatchesAnyId(Grasshopper.Kernel.GH_Document doc, Grasshopper.Kernel.IGH_DocumentObject obj, string id)
        {
            if (obj == null)
                return false;

            string token = NormalizePublicId(id);
            if (string.IsNullOrWhiteSpace(token))
                return false;

            if (obj.InstanceGuid.ToString().Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;

            return string.Equals(GetPublicId(doc, obj), token, StringComparison.OrdinalIgnoreCase);
        }

        // ── 共享序列化 helper（不改变任何字段结构）──────────────────────────
        private static JObject BuildComponentJson(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            return BuildComponentJson(obj, true);
        }

        private static string ExtractPortDescriptionTag(string description, string tag)
        {
            if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(tag)) return "";
            var matches = System.Text.RegularExpressions.Regex.Matches(
                description,
                @"(?:^|;)\s*(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<value>[^;]+)");
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (string.Equals(match.Groups["key"].Value, tag, StringComparison.OrdinalIgnoreCase))
                    return match.Groups["value"].Value.Trim();
            }
            return "";
        }

        private static string GetPortSemanticLabel(Grasshopper.Kernel.IGH_Param param)
        {
            if (param == null) return "";
            return ExtractPortDescriptionTag(param.Description, "label");
        }

        private static void AppendPortMetadataJson(JObject target, Grasshopper.Kernel.IGH_Param param, bool includeCSharpVariable = false)
        {
            if (target == null || param == null) return;

            string description = (param.Description ?? "").Trim();
            string semanticLabel = GetPortSemanticLabel(param);
            string displayName = !string.IsNullOrWhiteSpace(semanticLabel)
                ? semanticLabel
                : (!string.IsNullOrWhiteSpace(param.NickName) ? param.NickName : param.Name);
            string semanticType = ExtractPortDescriptionTag(description, "type");

            if (!string.IsNullOrWhiteSpace(param.NickName) && !string.Equals(param.NickName, param.Name, StringComparison.Ordinal))
                target["nickname"] = param.NickName;
            if (!string.IsNullOrWhiteSpace(description))
                target["description"] = description;
            if (!string.IsNullOrWhiteSpace(semanticLabel))
                target["semantic_label"] = semanticLabel;
            if (!string.IsNullOrWhiteSpace(semanticType))
                target["semantic_type"] = semanticType;
            if (!string.IsNullOrWhiteSpace(displayName))
                target["display_name"] = displayName;
            if (includeCSharpVariable && !string.IsNullOrWhiteSpace(semanticLabel) && !string.IsNullOrWhiteSpace(param.Name))
                target["csharp_variable"] = param.Name;
        }

        private static JObject BuildComponentJson(Grasshopper.Kernel.IGH_DocumentObject obj, bool includeScriptBodies)
        {
            var j = new JObject();
            j["name"]     = obj.Name;
            j["nickname"] = obj.NickName;
            j["id"]       = GetPublicId(obj);
            j["guid"]     = obj.InstanceGuid.ToString();
            j["pivot"]    = new JObject { { "x", Math.Round(obj.Attributes.Pivot.X) }, { "y", Math.Round(obj.Attributes.Pivot.Y) } };
            if (IsGraphMapperObject(obj)) j["graph_mapper_type"] = CurrentGraphMapperTypeName(obj) ?? "";
            if (obj is IGH_ActiveObject ao && ao.RuntimeMessageLevel != GH_RuntimeMessageLevel.Blank)
            {
                var msgs = new JArray();
                foreach (string m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Error))   msgs.Add("Error: " + m);
                foreach (string m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Warning)) msgs.Add("Warning: " + m);
                j["runtime_messages"] = msgs;
            }
            if (obj is Grasshopper.Kernel.IGH_Component comp)
            {
                var inputs = new JArray();
                for (int i = 0; i < comp.Params.Input.Count; i++)
                {
                    var param = comp.Params.Input[i];
                    var pj = new JObject { ["index"] = i, ["name"] = param.Name, ["type"] = param.TypeName };
                    AppendPortMetadataJson(pj, param);
                    if (param.VolatileDataCount > 0) pj["data_structure"] = $"Tree ({param.VolatileData.PathCount} branches, {param.VolatileData.DataCount} items total)";
                    else pj["data_structure"] = "Empty";
                    if (param.DataMapping == Grasshopper.Kernel.GH_DataMapping.Flatten) pj["is_flattened"] = true;
                    if (param.DataMapping == Grasshopper.Kernel.GH_DataMapping.Graft)   pj["is_grafted"]  = true;
                    if (param.Reverse)  pj["is_reversed"]  = true;
                    if (param.Simplify) pj["is_simplified"] = true;
                    AppendParamDataPreviewJson(pj, param);
                    var srcs = new JArray();
                    foreach (var src in param.Sources) {
                        var so = src.Attributes.GetTopLevel.DocObject;
                        srcs.Add(new JObject { { "id", GetPublicId(so) }, { "guid", so.InstanceGuid.ToString() }, { "output_index", (so is Grasshopper.Kernel.IGH_Component sc) ? sc.Params.Output.IndexOf(src) : 0 }, { "name", so.Name } });
                    }
                    pj["sources"] = srcs;
                    if (param.SourceCount == 0 && param.VolatileDataCount > 0) pj["has_internal_data"] = true;
                    inputs.Add(pj);
                }
                j["inputs"] = inputs;
                var outputs = new JArray();
                for (int i = 0; i < comp.Params.Output.Count; i++)
                {
                    var param = comp.Params.Output[i];
                    var pj = new JObject { { "index", i }, { "name", param.Name }, { "type", param.TypeName } };
                    AppendPortMetadataJson(pj, param, IsCSharpScriptComponent(obj));
                    if (param.VolatileDataCount > 0) pj["data_structure"] = $"Tree ({param.VolatileData.PathCount} branches, {param.VolatileData.DataCount} items total)";
                    if (param.DataMapping == Grasshopper.Kernel.GH_DataMapping.Flatten) pj["is_flattened"] = true;
                    if (param.DataMapping == Grasshopper.Kernel.GH_DataMapping.Graft)   pj["is_grafted"]  = true;
                    if (param.Reverse)  pj["is_reversed"]  = true;
                    if (param.Simplify) pj["is_simplified"] = true;
                    AppendParamDataPreviewJson(pj, param);
                    outputs.Add(pj);
                }
                j["outputs"] = outputs;
            }
            else if (obj is Grasshopper.Kernel.IGH_Param pm)
            {
                j["type"] = pm.TypeName;
                AppendPortMetadataJson(j, pm);
                if (pm.VolatileDataCount > 0) j["data_structure"] = $"Tree ({pm.VolatileData.PathCount} branches, {pm.VolatileData.DataCount} items total)";
                if (pm.DataMapping == Grasshopper.Kernel.GH_DataMapping.Flatten) j["is_flattened"] = true;
                if (pm.DataMapping == Grasshopper.Kernel.GH_DataMapping.Graft)   j["is_grafted"]  = true;
                if (pm.Reverse)  j["is_reversed"]  = true;
                if (pm.Simplify) j["is_simplified"] = true;
                AppendParamDataPreviewJson(j, pm);
                var srcs = new JArray();
                foreach (var src in pm.Sources) {
                    var so = src.Attributes.GetTopLevel.DocObject;
                    srcs.Add(new JObject { { "id", GetPublicId(so) }, { "guid", so.InstanceGuid.ToString() }, { "output_index", (so is Grasshopper.Kernel.IGH_Component sc) ? sc.Params.Output.IndexOf(src) : 0 }, { "name", so.Name } });
                }
                j["sources"] = srcs;
            }
            if (includeScriptBodies)
                AppendScriptBodiesToComponentJson(j, obj);
            return j;
        }

        private const int ParamDataPreviewBranchLimit = 4;
        private const int ParamDataPreviewItemsPerBranchLimit = 4;
        private const int ParamDataPreviewSequenceLimit = 6;
        private const int ParamDataPreviewTextLimit = 160;
        private const int ParamDataPreviewDigits = 6;

        private static void AppendParamDataPreviewJson(JObject target, Grasshopper.Kernel.IGH_Param param)
        {
            if (target == null || param == null || param.VolatileDataCount <= 0)
                return;

            try
            {
                var preview = BuildParamDataPreviewJson(param);
                if (preview != null)
                    target["data_preview"] = preview;
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("AppendParamDataPreviewJson: " + ex.Message);
            }
        }

        private static JObject BuildParamDataPreviewJson(Grasshopper.Kernel.IGH_Param param)
        {
            var tree = param.VolatileData;
            if (tree == null || tree.DataCount <= 0)
                return null;

            var preview = new JObject
            {
                ["branch_count"] = tree.PathCount,
                ["item_count"] = tree.DataCount,
                ["branch_limit"] = ParamDataPreviewBranchLimit,
                ["items_per_branch_limit"] = ParamDataPreviewItemsPerBranchLimit
            };

            var branches = new JArray();
            int branchCount = Math.Min(tree.PathCount, ParamDataPreviewBranchLimit);
            for (int branchIndex = 0; branchIndex < branchCount; branchIndex++)
            {
                var path = tree.get_Path(branchIndex);
                IList branch = tree.get_Branch(branchIndex);
                int itemCount = branch?.Count ?? 0;
                var branchJson = new JObject
                {
                    ["path"] = path?.ToString() ?? "{?}",
                    ["item_count"] = itemCount
                };

                var items = new JArray();
                int itemLimit = Math.Min(itemCount, ParamDataPreviewItemsPerBranchLimit);
                for (int itemIndex = 0; itemIndex < itemLimit; itemIndex++)
                    items.Add(SerializeGrasshopperDataItem(branch[itemIndex]));

                branchJson["items"] = items;
                if (itemCount > ParamDataPreviewItemsPerBranchLimit)
                    branchJson["truncated"] = true;
                branches.Add(branchJson);
            }

            preview["branches"] = branches;
            if (tree.PathCount > ParamDataPreviewBranchLimit)
                preview["truncated"] = true;
            return preview;
        }

        private static JToken SerializeGrasshopperDataItem(object item)
        {
            if (item == null)
                return JValue.CreateNull();

            if (item is Grasshopper.Kernel.Types.IGH_Goo goo)
                return SerializeGrasshopperGoo(goo);

            return SerializeRuntimeValue(item, item.GetType().Name);
        }

        private static JToken SerializeGrasshopperGoo(Grasshopper.Kernel.Types.IGH_Goo goo)
        {
            if (goo == null)
                return JValue.CreateNull();

            try
            {
                object scriptValue = goo.ScriptVariable();
                if (scriptValue != null && !ReferenceEquals(scriptValue, goo))
                {
                    var token = SerializeRuntimeValue(scriptValue, FirstNonEmpty(goo.TypeName, scriptValue.GetType().Name));
                    if (token != null)
                        return token;
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("SerializeGrasshopperGoo ScriptVariable: " + ex.Message);
            }

            return new JObject
            {
                ["type"] = FirstNonEmpty(goo.TypeName, goo.GetType().Name),
                ["text"] = TruncatePreviewText(goo.ToString(), ParamDataPreviewTextLimit)
            };
        }

        private static JToken SerializeRuntimeValue(object value, string typeName = null)
        {
            if (value == null)
                return JValue.CreateNull();

            if (value is string s)
                return new JValue(TruncatePreviewText(s, ParamDataPreviewTextLimit));
            if (value is bool b)
                return new JValue(b);
            if (value is int i32)
                return new JValue(i32);
            if (value is long i64)
                return new JValue(i64);
            if (value is float f32)
                return new JValue(RoundNumber(f32));
            if (value is double f64)
            {
                if (double.IsNaN(f64) || double.IsInfinity(f64))
                    return new JObject { ["type"] = FirstNonEmpty(typeName, "Number"), ["text"] = f64.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                return new JValue(RoundNumber(f64));
            }
            if (value is decimal dec)
                return new JValue(Math.Round(dec, ParamDataPreviewDigits));
            if (value is Guid guid)
                return new JValue(guid.ToString());
            if (value is Enum enumValue)
                return new JValue(enumValue.ToString());

            if (value is Point3d point3d)
                return BuildPoint3dJson(typeName, point3d);
            if (value is Point3f point3f)
                return BuildPoint3dJson(typeName, new Point3d(point3f));
            if (value is Point2d point2d)
                return new JObject
                {
                    ["type"] = FirstNonEmpty(typeName, nameof(Point2d)),
                    ["x"] = RoundNumber(point2d.X),
                    ["y"] = RoundNumber(point2d.Y)
                };
            if (value is Vector3d vector3d)
                return BuildVector3dJson(typeName, vector3d);
            if (value is Vector3f vector3f)
                return BuildVector3dJson(typeName, new Vector3d(vector3f));
            if (value is Vector2d vector2d)
                return new JObject
                {
                    ["type"] = FirstNonEmpty(typeName, nameof(Vector2d)),
                    ["x"] = RoundNumber(vector2d.X),
                    ["y"] = RoundNumber(vector2d.Y),
                    ["length"] = RoundNumber(vector2d.Length)
                };
            if (value is Plane plane)
                return new JObject
                {
                    ["type"] = FirstNonEmpty(typeName, nameof(Plane)),
                    ["origin"] = BuildPoint3dJson("Point3d", plane.Origin),
                    ["x_axis"] = BuildVector3dJson("Vector3d", plane.XAxis),
                    ["y_axis"] = BuildVector3dJson("Vector3d", plane.YAxis),
                    ["z_axis"] = BuildVector3dJson("Vector3d", plane.ZAxis)
                };
            if (value is Interval interval)
                return BuildIntervalJson(typeName, interval);
            if (value is Line line)
                return new JObject
                {
                    ["type"] = FirstNonEmpty(typeName, nameof(Line)),
                    ["from"] = BuildPoint3dJson("Point3d", line.From),
                    ["to"] = BuildPoint3dJson("Point3d", line.To),
                    ["length"] = RoundNumber(line.Length)
                };
            if (value is Circle circle)
                return new JObject
                {
                    ["type"] = FirstNonEmpty(typeName, nameof(Circle)),
                    ["center"] = BuildPoint3dJson("Point3d", circle.Center),
                    ["radius"] = RoundNumber(circle.Radius),
                    ["circumference"] = RoundNumber(circle.Circumference),
                    ["plane"] = SerializeRuntimeValue(circle.Plane, nameof(Plane))
                };
            if (value is Arc arc)
                return new JObject
                {
                    ["type"] = FirstNonEmpty(typeName, nameof(Arc)),
                    ["start"] = BuildPoint3dJson("Point3d", arc.StartPoint),
                    ["end"] = BuildPoint3dJson("Point3d", arc.EndPoint),
                    ["mid"] = BuildPoint3dJson("Point3d", arc.MidPoint),
                    ["radius"] = RoundNumber(arc.Radius),
                    ["length"] = RoundNumber(arc.Length)
                };
            if (value is Rectangle3d rectangle)
                return new JObject
                {
                    ["type"] = FirstNonEmpty(typeName, nameof(Rectangle3d)),
                    ["plane"] = SerializeRuntimeValue(rectangle.Plane, nameof(Plane)),
                    ["x"] = BuildIntervalJson("Interval", rectangle.X),
                    ["y"] = BuildIntervalJson("Interval", rectangle.Y),
                    ["width"] = RoundNumber(rectangle.Width),
                    ["height"] = RoundNumber(rectangle.Height)
                };
            if (value is Box box)
                return new JObject
                {
                    ["type"] = FirstNonEmpty(typeName, nameof(Box)),
                    ["plane"] = SerializeRuntimeValue(box.Plane, nameof(Plane)),
                    ["x"] = BuildIntervalJson("Interval", box.X),
                    ["y"] = BuildIntervalJson("Interval", box.Y),
                    ["z"] = BuildIntervalJson("Interval", box.Z),
                    ["bounding_box"] = BuildBoundingBoxJson(box.BoundingBox)
                };
            if (value is Curve curve)
                return BuildCurveJson(typeName, curve);
            if (value is Surface surface)
                return BuildSurfaceJson(typeName, surface);
            if (value is Brep brep)
                return BuildBrepJson(typeName, brep);
            if (value is Mesh mesh)
                return BuildMeshJson(typeName, mesh);
            if (value is GeometryBase geometry)
                return new JObject
                {
                    ["type"] = FirstNonEmpty(typeName, geometry.GetType().Name),
                    ["geometry_type"] = geometry.GetType().Name,
                    ["bounding_box"] = BuildBoundingBoxJson(geometry.GetBoundingBox(true))
                };
            if (value is IEnumerable sequence)
                return BuildSequencePreviewJson(typeName, sequence);

            return new JObject
            {
                ["type"] = FirstNonEmpty(typeName, value.GetType().Name),
                ["text"] = TruncatePreviewText(value.ToString(), ParamDataPreviewTextLimit)
            };
        }

        private static JObject BuildPoint3dJson(string typeName, Point3d point)
        {
            return new JObject
            {
                ["type"] = FirstNonEmpty(typeName, nameof(Point3d)),
                ["x"] = RoundNumber(point.X),
                ["y"] = RoundNumber(point.Y),
                ["z"] = RoundNumber(point.Z)
            };
        }

        private static JObject BuildVector3dJson(string typeName, Vector3d vector)
        {
            return new JObject
            {
                ["type"] = FirstNonEmpty(typeName, nameof(Vector3d)),
                ["x"] = RoundNumber(vector.X),
                ["y"] = RoundNumber(vector.Y),
                ["z"] = RoundNumber(vector.Z),
                ["length"] = RoundNumber(vector.Length)
            };
        }

        private static JObject BuildIntervalJson(string typeName, Interval interval)
        {
            return new JObject
            {
                ["type"] = FirstNonEmpty(typeName, nameof(Interval)),
                ["t0"] = RoundNumber(interval.T0),
                ["t1"] = RoundNumber(interval.T1),
                ["length"] = RoundNumber(interval.Length)
            };
        }

        private static JObject BuildCurveJson(string typeName, Curve curve)
        {
            var json = new JObject
            {
                ["type"] = FirstNonEmpty(typeName, curve.GetType().Name),
                ["curve_type"] = curve.GetType().Name,
                ["domain"] = BuildIntervalJson("Interval", curve.Domain),
                ["is_closed"] = curve.IsClosed,
                ["point_at_start"] = BuildPoint3dJson("Point3d", curve.PointAtStart),
                ["point_at_end"] = BuildPoint3dJson("Point3d", curve.PointAtEnd),
                ["bounding_box"] = BuildBoundingBoxJson(curve.GetBoundingBox(true))
            };

            try { json["length"] = RoundNumber(curve.GetLength()); } catch { }
            try { json["is_planar"] = curve.IsPlanar(); } catch { }
            return json;
        }

        private static JObject BuildSurfaceJson(string typeName, Surface surface)
        {
            var u = surface.Domain(0);
            var v = surface.Domain(1);
            double um = (u.T0 + u.T1) * 0.5;
            double vm = (v.T0 + v.T1) * 0.5;

            var json = new JObject
            {
                ["type"] = FirstNonEmpty(typeName, surface.GetType().Name),
                ["surface_type"] = surface.GetType().Name,
                ["u_domain"] = BuildIntervalJson("Interval", u),
                ["v_domain"] = BuildIntervalJson("Interval", v),
                ["is_closed_u"] = surface.IsClosed(0),
                ["is_closed_v"] = surface.IsClosed(1),
                ["sample_uv"] = new JObject
                {
                    ["u"] = RoundNumber(um),
                    ["v"] = RoundNumber(vm)
                },
                ["point_at_mid"] = BuildPoint3dJson("Point3d", surface.PointAt(um, vm)),
                ["bounding_box"] = BuildBoundingBoxJson(surface.GetBoundingBox(true))
            };

            try { json["normal_at_mid"] = BuildVector3dJson("Vector3d", surface.NormalAt(um, vm)); } catch { }
            try
            {
                json["u_iso_length_at_v_start"] = TryGetSurfaceIsoCurveLength(surface, 0, v.T0);
                json["u_iso_length_at_v_mid"] = TryGetSurfaceIsoCurveLength(surface, 0, vm);
                json["u_iso_length_at_v_end"] = TryGetSurfaceIsoCurveLength(surface, 0, v.T1);
                json["v_iso_length_at_u_start"] = TryGetSurfaceIsoCurveLength(surface, 1, u.T0);
                json["v_iso_length_at_u_mid"] = TryGetSurfaceIsoCurveLength(surface, 1, um);
                json["v_iso_length_at_u_end"] = TryGetSurfaceIsoCurveLength(surface, 1, u.T1);
            }
            catch { }
            return json;
        }

        private static JToken TryGetSurfaceIsoCurveLength(Surface surface, int direction, double parameter)
        {
            if (surface == null)
                return JValue.CreateNull();

            try
            {
                Curve iso = surface.IsoCurve(direction, parameter);
                if (iso == null)
                    return JValue.CreateNull();

                using (iso)
                {
                    return new JValue(RoundNumber(iso.GetLength()));
                }
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        private static JObject BuildBrepJson(string typeName, Brep brep)
        {
            var json = new JObject
            {
                ["type"] = FirstNonEmpty(typeName, brep.GetType().Name),
                ["brep_type"] = brep.GetType().Name,
                ["face_count"] = brep.Faces.Count,
                ["edge_count"] = brep.Edges.Count,
                ["vertex_count"] = brep.Vertices.Count,
                ["bounding_box"] = BuildBoundingBoxJson(brep.GetBoundingBox(true))
            };

            try { json["is_solid"] = brep.IsSolid; } catch { }
            return json;
        }

        private static JObject BuildMeshJson(string typeName, Mesh mesh)
        {
            return new JObject
            {
                ["type"] = FirstNonEmpty(typeName, mesh.GetType().Name),
                ["mesh_type"] = mesh.GetType().Name,
                ["vertex_count"] = mesh.Vertices.Count,
                ["face_count"] = mesh.Faces.Count,
                ["bounding_box"] = BuildBoundingBoxJson(mesh.GetBoundingBox(true))
            };
        }

        private static JObject BuildSequencePreviewJson(string typeName, IEnumerable sequence)
        {
            var items = new JArray();
            int count = 0;
            bool truncated = false;
            foreach (object item in sequence)
            {
                if (count >= ParamDataPreviewSequenceLimit)
                {
                    truncated = true;
                    break;
                }

                items.Add(SerializeRuntimeValue(item, item?.GetType().Name));
                count++;
            }

            var json = new JObject
            {
                ["type"] = FirstNonEmpty(typeName, "Sequence"),
                ["items"] = items
            };
            if (truncated)
                json["truncated"] = true;
            return json;
        }

        private static JObject BuildBoundingBoxJson(BoundingBox bbox)
        {
            if (!bbox.IsValid)
                return new JObject { ["valid"] = false };

            return new JObject
            {
                ["valid"] = true,
                ["min"] = BuildPoint3dJson("Point3d", bbox.Min),
                ["max"] = BuildPoint3dJson("Point3d", bbox.Max)
            };
        }

        private static double RoundNumber(double value)
        {
            return Math.Round(value, ParamDataPreviewDigits);
        }

        private static string TruncatePreviewText(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars < 1 || text.Length <= maxChars)
                return text ?? "";
            return text.Substring(0, maxChars) + "...";
        }

        // ── 摘要：仅 id/name/pivot + 首条报错，不含端口 ──────────────────────
        private static void GetComponentIssueCounts(Grasshopper.Kernel.IGH_DocumentObject obj, out int errorCount, out int warningCount, out string firstIssue)
        {
            errorCount = 0;
            warningCount = 0;
            firstIssue = null;
            if (!(obj is IGH_ActiveObject ao) || ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Blank) return;

            var errs = ao.RuntimeMessages(GH_RuntimeMessageLevel.Error);
            var warns = ao.RuntimeMessages(GH_RuntimeMessageLevel.Warning);
            errorCount = errs?.Count ?? 0;
            warningCount = warns?.Count ?? 0;

            if (errorCount > 0) firstIssue = errs[0];
            else if (warningCount > 0) firstIssue = warns[0];
        }

        private static bool ComponentHasConnections(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            if (obj is Grasshopper.Kernel.IGH_Component comp)
            {
                foreach (var p in comp.Params.Input) if (p.SourceCount > 0) return true;
                foreach (var p in comp.Params.Output) if (p.Recipients.Count > 0) return true;
                return false;
            }
            if (obj is Grasshopper.Kernel.IGH_Param param)
                return param.SourceCount > 0 || param.Recipients.Count > 0;
            return false;
        }

        private static bool ComponentHasPortName(Grasshopper.Kernel.IGH_DocumentObject obj, string portNameContains)
        {
            string needle = (portNameContains ?? "").Trim();
            if (needle.Length == 0) return true;

            bool HasName(string a, string b)
            {
                return (!string.IsNullOrEmpty(a) && a.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrEmpty(b) && b.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            bool HasPortText(Grasshopper.Kernel.IGH_Param p)
            {
                return p != null && (HasName(p.Name, p.NickName)
                    || HasName(GetPortSemanticLabel(p), p.Description));
            }

            if (obj is Grasshopper.Kernel.IGH_Component comp)
            {
                foreach (var p in comp.Params.Input)
                    if (HasPortText(p)) return true;
                foreach (var p in comp.Params.Output)
                    if (HasPortText(p)) return true;
                return false;
            }

            if (obj is Grasshopper.Kernel.IGH_Param param)
                return HasPortText(param);

            return false;
        }

        private static bool ComponentLooksLikeScript(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            if (obj == null) return false;
            if (IsCSharpScriptComponent(obj)) return true;

            string[] probes =
            {
                obj.Name ?? "",
                obj.NickName ?? "",
                obj.GetType()?.Name ?? ""
            };
            foreach (string probe in probes)
            {
                if (probe.IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (probe.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (probe.IndexOf("ghpython", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (probe.IndexOf("evaluate", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (probe.IndexOf("expression", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (probe.IndexOf("c#", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (probe.IndexOf("vb", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            try { return GhEnumerateScriptPayloadStrings(obj).Count > 0; }
            catch { return false; }
        }

        private static JObject BuildComponentQuerySummary(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            GetComponentIssueCounts(obj, out int errorCount, out int warningCount, out string firstIssue);
            var jo = new JObject
            {
                ["id"] = GetPublicId(obj),
                ["guid"] = obj.InstanceGuid.ToString(),
                ["name"] = obj.Name,
                ["nickname"] = obj.NickName,
                ["pivot"] = new JObject { { "x", Math.Round(obj.Attributes.Pivot.X) }, { "y", Math.Round(obj.Attributes.Pivot.Y) } },
                ["is_script"] = ComponentLooksLikeScript(obj),
                ["has_connections"] = ComponentHasConnections(obj)
            };

            if (obj is Grasshopper.Kernel.IGH_Component comp)
            {
                jo["kind"] = "component";
                jo["input_count"] = comp.Params.Input.Count;
                jo["output_count"] = comp.Params.Output.Count;
            }
            else if (obj is Grasshopper.Kernel.IGH_Param param)
            {
                jo["kind"] = "param";
                jo["type"] = param.TypeName;
                jo["source_count"] = param.SourceCount;
                jo["recipient_count"] = param.Recipients.Count;
            }
            else
            {
                jo["kind"] = "object";
            }

            if (errorCount > 0) jo["error_count"] = errorCount;
            if (warningCount > 0) jo["warning_count"] = warningCount;
            if (!string.IsNullOrWhiteSpace(firstIssue)) jo["first_issue"] = firstIssue;
            return jo;
        }

        private static List<Grasshopper.Kernel.IGH_DocumentObject> CollectComponentContextObjects(
            GH_Document doc,
            Grasshopper.Kernel.IGH_DocumentObject target,
            int depth)
        {
            var orderedIds = new List<Guid>();
            var visited = new HashSet<Guid>();

            void Traverse(Grasshopper.Kernel.IGH_DocumentObject obj, int remaining)
            {
                if (obj == null || remaining <= 0) return;
                if (obj is Grasshopper.Kernel.IGH_Component comp)
                {
                    foreach (var p in comp.Params.Input)
                    {
                        foreach (var s in p.Sources)
                        {
                            var nb = s.Attributes?.GetTopLevel?.DocObject;
                            if (nb == null || !visited.Add(nb.InstanceGuid)) continue;
                            orderedIds.Add(nb.InstanceGuid);
                            Traverse(nb, remaining - 1);
                        }
                    }
                    foreach (var p in comp.Params.Output)
                    {
                        foreach (var r in p.Recipients)
                        {
                            var nb = r.Attributes?.GetTopLevel?.DocObject;
                            if (nb == null || !visited.Add(nb.InstanceGuid)) continue;
                            orderedIds.Add(nb.InstanceGuid);
                            Traverse(nb, remaining - 1);
                        }
                    }
                }
                else if (obj is Grasshopper.Kernel.IGH_Param param)
                {
                    foreach (var s in param.Sources)
                    {
                        var nb = s.Attributes?.GetTopLevel?.DocObject;
                        if (nb == null || !visited.Add(nb.InstanceGuid)) continue;
                        orderedIds.Add(nb.InstanceGuid);
                        Traverse(nb, remaining - 1);
                    }
                    foreach (var r in param.Recipients)
                    {
                        var nb = r.Attributes?.GetTopLevel?.DocObject;
                        if (nb == null || !visited.Add(nb.InstanceGuid)) continue;
                        orderedIds.Add(nb.InstanceGuid);
                        Traverse(nb, remaining - 1);
                    }
                }
            }

            visited.Add(target.InstanceGuid);
            orderedIds.Add(target.InstanceGuid);
            Traverse(target, Math.Max(0, depth));

            var result = new List<Grasshopper.Kernel.IGH_DocumentObject>();
            foreach (var guid in orderedIds)
            {
                var obj = doc.FindObject(guid, true);
                if (obj != null) result.Add(obj);
            }
            return result;
        }


        private static string ExecuteGetCanvasSummary()
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }
                var arr = new JArray();
                foreach (var obj in doc.Objects)
                {
                    if (obj is Grasshopper.Kernel.Special.GH_Group) continue;
                    var j = new JObject {
                        ["id"]    = GetPublicId(doc, obj),
                        ["guid"]  = obj.InstanceGuid.ToString(),
                        ["name"]  = obj.Name,
                        ["pivot"] = new JObject { { "x", Math.Round(obj.Attributes.Pivot.X) }, { "y", Math.Round(obj.Attributes.Pivot.Y) } }
                    };
                    if (obj is IGH_ActiveObject ao && ao.RuntimeMessageLevel != GH_RuntimeMessageLevel.Blank)
                    {
                        var errs = ao.RuntimeMessages(GH_RuntimeMessageLevel.Error);
                        var warn = ao.RuntimeMessages(GH_RuntimeMessageLevel.Warning);
                        if (errs.Count > 0)      j["error"] = "❌ " + errs[0];
                        else if (warn.Count > 0) j["error"] = "⚠️ " + warn[0];
                    }
                    arr.Add(j);
                }
                var groups = new JArray();
                foreach (var g in doc.Objects.OfType<Grasshopper.Kernel.Special.GH_Group>()) {
                    var members = new JArray();
                    foreach (var member in g.Objects())
                    {
                        members.Add(member != null ? GetPublicId(doc, member) : "");
                    }
                    groups.Add(new JObject { ["id"] = GetPublicId(doc, g), ["guid"] = g.InstanceGuid.ToString(), ["name"] = g.NickName, ["members"] = members });
                }
                result = new JObject
                {
                    ["rhino_units"] = BuildRhinoUnitsJson(),
                    ["components"] = arr,
                    ["groups"] = groups
                }.ToString(Formatting.None);
            }));
            return result;
        }

        // ── 上下文：目标 + 前后各 depth 层邻居（完整详情）───────────────────


        private static void SyncCodeIssuesStripHeightToInputArea()
        {
            if (_codeCanvasIssuesHost == null || _inputAreaBorder == null) return;
            double h = _inputAreaBorder.ActualHeight;
            if (double.IsNaN(h) || h < 1) return;
            _codeCanvasIssuesHost.Height = h;
        }

        private static void ScheduleCodeSurfaceRefreshFromCanvas()
        {
            if (_window?.Dispatcher == null) return;

            Action armTimer = () =>
            {
                if (_codeSurfaceDebounceTimer != null)
                {
                    _codeSurfaceDebounceTimer.Stop();
                    _codeSurfaceDebounceTimer = null;
                }
                _codeSurfaceDebounceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                _codeSurfaceDebounceTimer.Tick += (_, __) =>
                {
                    if (_codeSurfaceDebounceTimer != null)
                    {
                        _codeSurfaceDebounceTimer.Stop();
                        _codeSurfaceDebounceTimer = null;
                    }
                    Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        UpdateCodeView();
                    }));
                };
                _codeSurfaceDebounceTimer.Start();
            };

            if (_window.Dispatcher.CheckAccess())
                armTimer();
            else
                _window.Dispatcher.Invoke(armTimer);
        }





        private static void OnGhDocObjectsChanged(object sender, GH_DocObjectEventArgs e)
        {
            ScheduleCodeSurfaceRefreshFromCanvas();
        }

        private static void OnGhDocSolutionEnd(object sender, GH_SolutionEventArgs e)
        {
            ScheduleCodeSurfaceRefreshFromCanvas();
        }

        private static void OnGhCanvasDocumentChanged(object sender, GH_CanvasDocumentChangedEventArgs e)
        {
            AttachGrasshopperDocumentForCodeRefresh(e?.NewDocument);
            ScheduleCodeSurfaceRefreshFromCanvas();
        }

        private static void DetachGrasshopperDocumentForCodeRefresh()
        {
            if (_codeSurfaceHookedDoc == null) return;
            try {
                _codeSurfaceHookedDoc.ObjectsAdded -= OnGhDocObjectsChanged;
                _codeSurfaceHookedDoc.ObjectsDeleted -= OnGhDocObjectsChanged;
                _codeSurfaceHookedDoc.SolutionEnd -= OnGhDocSolutionEnd;
            } catch (Exception ex) {
                AddGhLog.Warn("DetachGrasshopperDocumentForCodeRefresh: " + ex.Message);
            }
            _codeSurfaceHookedDoc = null;
        }

        private static void AttachGrasshopperDocumentForCodeRefresh(GH_Document doc)
        {
            if (doc == _codeSurfaceHookedDoc) return;
            DetachGrasshopperDocumentForCodeRefresh();
            _codeSurfaceHookedDoc = doc;
            if (doc == null) return;
            doc.ObjectsAdded += OnGhDocObjectsChanged;
            doc.ObjectsDeleted += OnGhDocObjectsChanged;
            doc.SolutionEnd += OnGhDocSolutionEnd;
        }

        private static void DetachCodeSurfaceCanvasHookOnly()
        {
            if (_codeSurfaceHookedCanvas == null) return;
            try { _codeSurfaceHookedCanvas.DocumentChanged -= OnGhCanvasDocumentChanged; }
            catch (Exception ex) { AddGhLog.Warn("Detach canvas hook: " + ex.Message); }
            _codeSurfaceHookedCanvas = null;
        }

        private static void AttachCodeSurfaceCanvasHook()
        {
            var canvas = Grasshopper.Instances.ActiveCanvas as GH_Canvas;
            if (canvas == null) return;
            if (canvas == _codeSurfaceHookedCanvas) return;
            DetachCodeSurfaceCanvasHookOnly();
            _codeSurfaceHookedCanvas = canvas;
            _codeSurfaceHookedCanvas.DocumentChanged += OnGhCanvasDocumentChanged;
        }

        private static void TeardownGrasshopperCodeSurfaceHooks()
        {
            try {
                DetachCodeSurfaceCanvasHookOnly();
                DetachGrasshopperDocumentForCodeRefresh();
                if (_codeSurfaceDebounceTimer != null)
                {
                    _codeSurfaceDebounceTimer.Stop();
                    _codeSurfaceDebounceTimer = null;
                }
            } catch (Exception ex) {
                AddGhLog.Warn("TeardownGrasshopperCodeSurfaceHooks: " + ex.Message);
            }
        }

        private static void StartGrasshopperCodeSurfaceHooks()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try {
                    AttachCodeSurfaceCanvasHook();
                    AttachGrasshopperDocumentForCodeRefresh(Grasshopper.Instances.ActiveCanvas?.Document);
                } catch (Exception ex) {
                    AddGhLog.Warn("StartGrasshopperCodeSurfaceHooks: " + ex.Message);
                }
            }));
        }

        private static void UpdateCodePanelCanvasIssues()
        {
            if (_txtCanvasIssues == null) return;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (!_isCodeVisible) return;

                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) {
                    _txtCanvasIssues.Text = "当前无激活的 Grasshopper 文档。";
                    return;
                }

                string err = GetCanvasErrors(doc)?.Trim();
                if (string.IsNullOrEmpty(err))
                    _txtCanvasIssues.Foreground = ThemeBrush(Color.FromRgb(92, 98, 110), Color.FromRgb(140, 140, 140));
                else
                    _txtCanvasIssues.Foreground = ThemeBrush(Color.FromRgb(28, 32, 38), Color.FromRgb(200, 200, 200));
                _txtCanvasIssues.Text = string.IsNullOrEmpty(err)
                    ? "画布暂无组件级 Error / Warning 运行时提示。"
                    : err;
            }));
        }

        private static void UpdateCodeView()
        {
            if (!_isCodeVisible || _richCodeView == null) return;

            if (!_isJsonMode)
            {
                string raw = Magpie.Host.GrasshopperDocumentHost.ExecuteGetCanvasSummary();
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try {
                        // 尝试在 UI 上进行格式化展示，即使 AI 接收的是压缩版
                        var obj = JsonConvert.DeserializeObject(raw);
                        SetRichCodeViewContent(_richCodeView, JsonConvert.SerializeObject(obj, Formatting.Indented));
                    } catch (Exception ex) {
                        AddGhLog.Debug("UpdateCodeView JSON indent failed: " + ex.Message);
                        SetRichCodeViewContent(_richCodeView, raw);
                    }
                    UpdateCodePanelCanvasIssues();
                }));
                return;
            }

            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try {
                    var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                    if (doc == null) {
                        SetRichCodeViewContent(_richCodeView, "// 没有激活的画布", asPlainComment: true);
                        return;
                    }

                    var graph = new JObject();
                    if (DeploymentOptions.IncludeCanvasExportTimestamp)
                        graph["timestamp"] = DateTime.Now.ToString("HH:mm:ss");
                    graph["object_count"] = doc.ObjectCount;

                    var components = new JArray();
                    foreach (var obj in doc.Objects)
                    {
                        var compJson = new JObject();
                        compJson["name"] = obj.Name;
                        compJson["nickname"] = obj.NickName;
                        compJson["id"] = GetPublicId(doc, obj);
                        compJson["guid"] = obj.InstanceGuid.ToString();
                        compJson["pivot"] = new JObject { { "x", Math.Round(obj.Attributes.Pivot.X) }, { "y", Math.Round(obj.Attributes.Pivot.Y) } };
                        AppendSliderStateJson(obj, compJson);

                        if (obj is Grasshopper.Kernel.IGH_Component comp)
                        {
                            var inputs = new JArray();
                            foreach (var param in comp.Params.Input)
                            {
                                var paramJson = new JObject();
                                paramJson["name"] = param.Name;
                                paramJson["nickname"] = param.NickName;
                                AppendPortMetadataJson(paramJson, param);

                                var sources = new JArray();
                                foreach (var source in param.Sources)
                                {
                                    sources.Add(GetPublicId(doc, source.Attributes.GetTopLevel.DocObject));
                                }
                                paramJson["sources"] = sources;
                                inputs.Add(paramJson);
                            }
                            compJson["inputs"] = inputs;

                            var outputs = new JArray();
                            foreach (var param in comp.Params.Output)
                            {
                                var paramJson = new JObject();
                                paramJson["name"] = param.Name;
                                paramJson["nickname"] = param.NickName;
                                AppendPortMetadataJson(paramJson, param, IsCSharpScriptComponent(obj));
                                outputs.Add(paramJson);
                            }
                            compJson["outputs"] = outputs;
                        }
                        else if (obj is Grasshopper.Kernel.IGH_Param param)
                        {
                            AppendPortMetadataJson(compJson, param);
                            var sources = new JArray();
                            foreach (var source in param.Sources)
                            {
                                sources.Add(GetPublicId(doc, source.Attributes.GetTopLevel.DocObject));
                            }
                            compJson["sources"] = sources;
                        }

                        components.Add(compJson);
                    }
                    graph["components"] = components;

                    SetRichCodeViewContent(_richCodeView, graph.ToString(Formatting.Indented));
                } finally {
                    UpdateCodePanelCanvasIssues();
                }
            }));
        }

        /// <summary>
        /// 优先按名称从组件库创建实例；若提供合法 component_guid 则按类型 GUID 创建（用于同名或脚本类）。
        /// </summary>
        private static Grasshopper.Kernel.IGH_DocumentObject InstantiateDocumentObjectFromLibrary(string name, string componentGuid)
        {
            if (!string.IsNullOrWhiteSpace(componentGuid) && Guid.TryParse(componentGuid.Trim(), out Guid cid)) {
                var emitted = Grasshopper.Instances.ComponentServer.EmitObject(cid) as Grasshopper.Kernel.IGH_DocumentObject;
                if (emitted != null) return emitted;
            }
            if (string.IsNullOrWhiteSpace(name)) return null;
            var proxy = FindComponentProxy(name);
            return proxy?.CreateInstance() as Grasshopper.Kernel.IGH_DocumentObject;
        }

        private const string DefaultGraphMapperType = "Bezier";

        private static bool IsGraphMapperObject(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            return obj is Grasshopper.Kernel.Special.GH_GraphMapper;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return null;
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }

        private static string GetGraphMapperTypeRequest(JToken token, string valueFallback = null)
        {
            if (token == null) return FirstNonEmpty(valueFallback, DefaultGraphMapperType);
            return FirstNonEmpty(
                token["graph_mapper_type"]?.ToString(),
                token["graph_type"]?.ToString(),
                token["mapper_type"]?.ToString(),
                valueFallback,
                DefaultGraphMapperType);
        }

        private static string CurrentGraphMapperTypeName(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            var mapper = obj as Grasshopper.Kernel.Special.GH_GraphMapper;
            return mapper?.Graph?.Name;
        }

        private static Grasshopper.Kernel.GH_GraphProxy FindGraphMapperProxy(string keyword)
        {
            var proxies = Grasshopper.Instances.ComponentServer?.GraphProxies;
            if (proxies == null) return null;

            string wanted = (keyword ?? DefaultGraphMapperType).Trim();
            if (wanted.Length == 0) wanted = DefaultGraphMapperType;

            var list = proxies.ToList();
            var exact = list.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            exact = list.FirstOrDefault(p => string.Equals(p.Type?.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            return list.FirstOrDefault(p =>
                (!string.IsNullOrWhiteSpace(p.Name) && p.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrWhiteSpace(p.Description) && p.Description.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrWhiteSpace(p.Type?.Name) && p.Type.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static string DescribeGraphMapperTypes(int maxNames = 20)
        {
            var proxies = Grasshopper.Instances.ComponentServer?.GraphProxies;
            if (proxies == null || proxies.Count == 0) return "";
            return " 可用类型：" + string.Join(", ", proxies.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(maxNames));
        }

        private static bool TrySetGraphMapperType(Grasshopper.Kernel.IGH_DocumentObject obj, string graphType, out string detail)
        {
            detail = "";
            var mapper = obj as Grasshopper.Kernel.Special.GH_GraphMapper;
            if (mapper == null)
            {
                detail = "Error: 该电池不是 Graph Mapper。";
                return false;
            }

            string requested = FirstNonEmpty(graphType, DefaultGraphMapperType);
            var proxy = FindGraphMapperProxy(requested);
            if (proxy == null)
            {
                detail = "Error: 找不到 Graph Mapper 类型 '" + requested + "'。" + DescribeGraphMapperTypes();
                return false;
            }

            var graph = Grasshopper.Instances.ComponentServer.EmitGraph(proxy.GUID);
            if (graph == null)
            {
                detail = "Error: 无法创建 Graph Mapper 类型 '" + proxy.Name + "'。";
                return false;
            }

            try { graph.PrepareForUse(); } catch { }

            if (mapper.Container == null)
                mapper.Container = new Grasshopper.Kernel.Graphs.GH_GraphContainer(graph, 0.0, 1.0, 0.0, 1.0);
            else
                mapper.Container.Graph = graph;

            try { mapper.Container.PrepareForUse(); } catch { }
            mapper.ExpireSolution(true);
            try { mapper.Attributes?.ExpireLayout(); } catch { }

            detail = "Graph Mapper 类型=" + proxy.Name;
            return true;
        }

        private static bool IsScriptModeAuxiliaryComponentAllowed(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            if (_layoutMode != LayoutMode.CSharpFirst) return true;
            string category = obj?.Category ?? "";
            return string.Equals(category, "Params", StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, "Display", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCSharpFirstDirectComponent(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            string category = obj?.Category ?? "";
            return string.Equals(category, "Params", StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, "Display", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsValidCSharpFirstHelperReason(string reason)
        {
            return string.Equals(reason?.Trim(), "component_more_efficient", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason?.Trim(), "user_requested_component", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValidateCSharpFirstComponentCreation(Grasshopper.Kernel.IGH_DocumentObject obj, string requestedName, string reason, out string error)
        {
            error = null;
            if (_layoutMode != LayoutMode.CSharpFirst)
                return true;

            if (IsCSharpFirstDirectComponent(obj))
                return true;

            if (IsValidCSharpFirstHelperReason(reason))
                return true;

            string displayName = !string.IsNullOrWhiteSpace(requestedName) ? requestedName : (obj?.Name ?? "component");
            string category = string.IsNullOrWhiteSpace(obj?.Category) ? "Unknown" : obj.Category;
            error = "Error: C# priority mode allows Params and Display components directly. "
                + displayName + " belongs to " + category
                + " and requires csharp_first_helper_reason: component_more_efficient or user_requested_component.";
            return false;
        }

        private static string BuildScriptModeAuxiliaryComponentError(Grasshopper.Kernel.IGH_DocumentObject obj, string requestedName)
        {
            string displayName = !string.IsNullOrWhiteSpace(requestedName) ? requestedName : (obj?.Name ?? "该电池");
            string category = string.IsNullOrWhiteSpace(obj?.Category) ? "未知" : obj.Category;
            return "Error: C# 优先模式下，非脚本辅助电池只允许使用 Params 或 Display 分类；"
                + displayName + " 属于 " + category + "，已拒绝创建。核心建模逻辑请写入 C# Script 电池。";
        }

        private static string ExecuteAddGhComponent(string name, float x, float y, string label = null, string componentGuid = null, string graphMapperType = null, string value = null, double? min = null, double? max = null, int? decimals = null, string csharpFirstHelperReason = null, string csharpFirstHelperReasonDetail = null)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }

                var obj = InstantiateDocumentObjectFromLibrary(name, componentGuid);

                if (obj == null) {
                    result = !string.IsNullOrWhiteSpace(componentGuid)
                        ? "Error: component_guid 无效或未加载对应的电池类型。"
                        : "Error: 找不到电池 '" + name + "'。";
                    return;
                }

                if (!ValidateCSharpFirstComponentCreation(obj, name, csharpFirstHelperReason, out string csharpFirstReasonError))
                {
                    result = csharpFirstReasonError;
                    return;
                }

                obj.CreateAttributes();
                obj.Attributes.Pivot = new System.Drawing.PointF(x, y);
                if (!string.IsNullOrEmpty(label)) obj.NickName = label;
                obj.Attributes.ExpireLayout();

                string graphMapperDetail = null;
                if (IsGraphMapperObject(obj) && !TrySetGraphMapperType(obj, FirstNonEmpty(graphMapperType, DefaultGraphMapperType), out graphMapperDetail))
                {
                    result = graphMapperDetail;
                    return;
                }

                doc.AddObject(obj, false);

                if (obj is Grasshopper.Kernel.Special.GH_NumberSlider slider)
                {
                    if (min.HasValue) slider.Slider.Minimum = (decimal)min.Value;
                    if (max.HasValue) slider.Slider.Maximum = (decimal)max.Value;
                    if (decimals.HasValue) slider.Slider.DecimalPlaces = Math.Max(0, Math.Min(10, decimals.Value));
                    if (!string.IsNullOrEmpty(value) && decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal d))
                        slider.Slider.Value = ClampSliderValue(slider, d);
                }
                else if (obj is Grasshopper.Kernel.Special.GH_Panel panel && !string.IsNullOrEmpty(value))
                {
                    panel.UserText = value;
                }

                try { doc.ScheduleSolution(150); }
                catch (Exception ex) { AddGhLog.Warn("ExecuteAddGhComponent Schedule failed: " + ex.Message); }
                string displayName = !string.IsNullOrWhiteSpace(name) ? name : (obj.Name ?? "组件");
                result = "已添加 " + displayName + " (ID: " + GetPublicId(doc, obj) + ").";
                if (!string.IsNullOrWhiteSpace(graphMapperDetail)) result += " " + graphMapperDetail + "。";
            }));
            return result;
        }

        private static IEnumerable<string> GetPortMatchTexts(Grasshopper.Kernel.IGH_Param param)
        {
            if (param == null) yield break;
            if (!string.IsNullOrWhiteSpace(param.Name)) yield return param.Name.Trim();
            if (!string.IsNullOrWhiteSpace(param.NickName)) yield return param.NickName.Trim();
            string semanticLabel = GetPortSemanticLabel(param);
            if (!string.IsNullOrWhiteSpace(semanticLabel)) yield return semanticLabel.Trim();
            if (!string.IsNullOrWhiteSpace(param.Description)) yield return param.Description.Trim();
        }

        private static string DescribePortCandidate(int index, Grasshopper.Kernel.IGH_Param param)
        {
            if (param == null) return "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string semanticLabel = GetPortSemanticLabel(param);
            string display = !string.IsNullOrWhiteSpace(semanticLabel) ? semanticLabel : (param.NickName ?? param.Name ?? "");
            return "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + (param.Name ?? "")
                + (string.IsNullOrWhiteSpace(display) || string.Equals(display, param.Name, StringComparison.Ordinal) ? "" : " (" + display + ")");
        }

        private static bool TryResolveConnectPort(Grasshopper.Kernel.IGH_DocumentObject obj, bool output, int index, string label, out Grasshopper.Kernel.IGH_Param param, out int resolvedIndex, out string error)
        {
            param = null;
            resolvedIndex = index;
            error = "";
            if (obj == null)
            {
                error = "object not found";
                return false;
            }

            var ports = new List<Grasshopper.Kernel.IGH_Param>();
            if (obj is Grasshopper.Kernel.IGH_Component comp)
            {
                var source = output ? comp.Params.Output : comp.Params.Input;
                foreach (var p in source) ports.Add(p);
            }
            else if (obj is Grasshopper.Kernel.IGH_Param standalone)
            {
                ports.Add(standalone);
            }

            if (ports.Count == 0)
            {
                error = output ? "source has no output ports" : "target has no input ports";
                return false;
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                if (index < 0 || index >= ports.Count)
                {
                    error = "port index out of range. Candidates: " + string.Join(", ", ports.Select((p, i) => DescribePortCandidate(i, p)));
                    return false;
                }
                param = ports[index];
                resolvedIndex = index;
                return true;
            }

            string needle = label.Trim();
            var exact = ports.Select((p, i) => new { Port = p, Index = i })
                .Where(x => GetPortMatchTexts(x.Port).Any(t => string.Equals(t, needle, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var matches = exact.Count > 0
                ? exact
                : ports.Select((p, i) => new { Port = p, Index = i })
                    .Where(x => GetPortMatchTexts(x.Port).Any(t => t.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

            if (matches.Count == 1)
            {
                param = matches[0].Port;
                resolvedIndex = matches[0].Index;
                return true;
            }

            if (matches.Count > 1)
                error = "port label is ambiguous: " + needle + ". Matches: " + string.Join(", ", matches.Select(x => DescribePortCandidate(x.Index, x.Port)));
            else
                error = "port label not found: " + needle + ". Candidates: " + string.Join(", ", ports.Select((p, i) => DescribePortCandidate(i, p)));
            return false;
        }



        private static bool GhScriptMetaExcludedName(string pn)
        {
            if (string.IsNullOrEmpty(pn)) return true;
            foreach (var ex in new[] {
                "NickName", "Category", "SubCategory", "Description", "Keywords", "InstanceDescription",
                "Path", "FileName", "Url", "Message", "ToolTip", "IconDisplayName", "LanguageName"
            })
                if (pn.Equals(ex, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool GhScriptNameLooksLikePayload(string pn)
        {
            if (GhScriptMetaExcludedName(pn)) return false;
            // 支持完整接口名如 RhinoCodePlatform.GH.IScriptComponent.Text
            string shortName = pn.Contains('.') ? pn.Substring(pn.LastIndexOf('.') + 1) : pn;
            // GhPython / Rhino「Python 3 Script」等：可执行正文在 Text，不用子串「Text」以免误匹配如 Texture。
            if (string.Equals(shortName, "Text", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var part in new[] {
                "Code", "Script", "Formula", "Expression", "Source", "Snippet", "Program", "Definition",
                "Logic", "Statement", "Body", "Python", "CSharp", "Csharp", "VB", "VBA", "IronPython", "Compile",
                "ScriptSource", "Editor", "UserCode", "RawCode", "TextBody", "Document", "PyCode", "Roslyn"
            })
                if (shortName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static int GhScriptMemberPreference(string pn)
        {
            string sn = pn.Contains('.') ? pn.Substring(pn.LastIndexOf('.') + 1) : pn;
            if (string.Equals(sn, "Code", StringComparison.OrdinalIgnoreCase)) return 500;
            if (string.Equals(sn, "Script", StringComparison.OrdinalIgnoreCase)) return 490;
            if (string.Equals(sn, "Text", StringComparison.OrdinalIgnoreCase)) return 485;
            if (sn.IndexOf("Formula", StringComparison.OrdinalIgnoreCase) >= 0) return 480;
            if (sn.IndexOf("Expression", StringComparison.OrdinalIgnoreCase) >= 0) return 470;
            if (sn.IndexOf("ScriptSource", StringComparison.OrdinalIgnoreCase) >= 0) return 460;
            if (sn.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0) return 450;
            if (sn.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0) return 440;
            if (sn.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0 || sn.IndexOf("Csharp", StringComparison.OrdinalIgnoreCase) >= 0) return 430;
            if (sn.IndexOf("VB", StringComparison.OrdinalIgnoreCase) >= 0) return 420;
            if (sn.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0) return 400;
            if (sn.IndexOf("Content", StringComparison.OrdinalIgnoreCase) >= 0) return 350;
            if (sn.IndexOf("Body", StringComparison.OrdinalIgnoreCase) >= 0) return 340;
            return 100;
        }

        private static string GhTruncateScriptSnippet(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length <= maxChars) return text;
            return text.Substring(0, maxChars) + "\n...[truncated " + (text.Length - maxChars) + " chars]";
        }

        /// <summary> 枚举电池实例上「像脚本正文」的可读 string 属性/字段（顺序已按启发式偏好排好）。 </summary>
        private static List<(string label, string text, int pref)> GhEnumerateScriptPayloadStrings(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            var results = new List<(string label, string text, int pref)>();
            if (obj == null) return results;

            var propCandidates = new List<System.Reflection.PropertyInfo>();
            var fieldCandidates = new List<System.Reflection.FieldInfo>();
            var seenProp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFieldSig = new HashSet<string>();

            for (Type t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (prop.PropertyType != typeof(string)) continue;
                    if (prop.GetIndexParameters().Length != 0) continue;
                    if (!GhScriptNameLooksLikePayload(prop.Name)) continue;
                    if (!seenProp.Add(prop.Name)) continue;
                    propCandidates.Add(prop);
                }

                foreach (var fld in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (fld.FieldType != typeof(string)) continue;
                    if (!GhScriptNameLooksLikePayload(fld.Name)) continue;
                    string sig = (t.FullName ?? t.Name) + "::" + fld.Name;
                    if (!seenFieldSig.Add(sig)) continue;
                    fieldCandidates.Add(fld);
                }
            }

            foreach (var prop in propCandidates.OrderByDescending(p => GhScriptMemberPreference(p.Name)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    object v = prop.GetGetMethod(true)?.Invoke(obj, null);
                    if (v is string sv && !string.IsNullOrEmpty(sv))
                        results.Add((prop.Name + " (prop)", sv, GhScriptMemberPreference(prop.Name)));
                }
                catch (Exception ex) { AddGhLog.Debug("GhEnumerateScriptPayloadStrings prop " + prop.Name + ": " + ex.Message); }
            }

            foreach (var fld in fieldCandidates.OrderByDescending(f => GhScriptMemberPreference(f.Name)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    object v = fld.GetValue(obj);
                    if (v is string sv && !string.IsNullOrEmpty(sv))
                        results.Add((fld.Name + " (field)", sv, GhScriptMemberPreference(fld.Name)));
                }
                catch (Exception ex) { AddGhLog.Debug("GhEnumerateScriptPayloadStrings field " + fld.Name + ": " + ex.Message); }
            }

            return results.OrderByDescending(x => x.pref).ThenBy(x => x.label, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>read_source：不打开 GH_ScriptEditor（GetSourceCode 易在未就绪时崩溃），与 get_gh_components.script_bodies 同源反射读取。 </summary>
        private static string GhReadScriptSourceViaReflection(Grasshopper.Kernel.IGH_DocumentObject obj, int readCap, int maxPerMember)
        {
            var items = GhEnumerateScriptPayloadStrings(obj);
            var jo = new JObject();
            jo["via"] = "component_reflection";
            jo["runtime_type_hint"] = obj?.GetType()?.Name ?? "";

            if (items.Count == 0)
            {
                jo["script_bodies"] = new JObject();
                jo["primary_key"] = "";
                jo["primary_for_edit"] = "";
                jo["truncated"] = false;
                jo["hint"] = "未反射到脚本类 string 成员；若为内置 C# Script 仍可用 open_focus 人工查看，或换 get_gh_components/correct property。";
                return jo.ToString(Formatting.None);
            }

            bool truncated = false;
            var bag = new JObject();
            int approxTotal = 0;
            foreach (var (label, text, _) in items)
            {
                string s = text;
                if (s.Length > maxPerMember)
                {
                    s = GhTruncateScriptSnippet(s, maxPerMember);
                    truncated = true;
                }

                int bump = (label?.Length ?? 0) + s.Length + 40;
                if (approxTotal + bump > readCap)
                {
                    truncated = true;
                    break;
                }

                bag[label] = s;
                approxTotal += bump;
            }

            var best = items[0];
            string primary = best.text;
            if (primary.Length > maxPerMember)
            {
                primary = GhTruncateScriptSnippet(primary, maxPerMember);
                truncated = true;
            }

            jo["script_bodies"] = bag;
            jo["primary_key"] = best.label;
            jo["primary_for_edit"] = primary;
            jo["truncated"] = truncated;
            jo["hint"] = "与 get_gh_components 的 script_bodies 同源；不调用 GH_ScriptEditor。内置 C# 改代码仍用 set_source_commit（只替换首个可编辑块）或 property 精确写入。";
            return jo.ToString(Formatting.None);
        }

        /// <summary>尝试通过反射直接写入脚本电池的内容，优先 m_codeBlocks，其次按启发式匹配 string 属性/字段。</summary>
        private static bool TrySetNativeScriptContentViaReflection(Grasshopper.Kernel.IGH_DocumentObject obj, string newCode)
        {
            if (obj == null || newCode == null) return false;
            Type t = obj.GetType();
            if (t == null) return false;

            try
            {
                // 先尝试找到并修改 GH_CodeBlocks 相关的属性/字段
                var codeBlocksField = FindInstanceFieldInHierarchy(t, "m_codeBlocks");
                if (codeBlocksField != null)
                {
                    var currentBlocks = codeBlocksField.GetValue(obj);
                    if (currentBlocks != null)
                    {
                        try
                        {
                            GH_CodeBlocks blocks = currentBlocks as GH_CodeBlocks;
                            if (blocks != null)
                            {
                                GH_CodeBlocks merged = GhBuildCodeBlocksReplacingFirstMutable(blocks, newCode);
                                codeBlocksField.SetValue(obj, merged);
                                AddGhLog.Debug("Successfully set native script via m_codeBlocks field");
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            AddGhLog.Debug("Failed to set via m_codeBlocks: " + ex.Message);
                        }
                    }
                }

                // 备选：按启发式枚举所有 string 属性/字段（支持完整接口名如 RhinoCodePlatform.GH.IScriptComponent.Text）
                var propCandidates = new List<System.Reflection.PropertyInfo>();
                var fieldCandidates = new List<System.Reflection.FieldInfo>();
                var seenProp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenFieldSig = new HashSet<string>();

                for (Type tt = t; tt != null && tt != typeof(object); tt = tt.BaseType)
                {
                    foreach (var prop in tt.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (prop.PropertyType != typeof(string)) continue;
                        if (!seenProp.Add(prop.Name)) continue;
                        if (prop.GetIndexParameters().Length != 0) continue;
                        if (prop.GetSetMethod(true) == null) continue;
                        if (!GhScriptNameLooksLikePayload(prop.Name)) continue;
                        propCandidates.Add(prop);
                    }
                    foreach (var fld in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (fld.FieldType != typeof(string)) continue;
                        string sig = (tt.FullName ?? tt.Name) + "::" + fld.Name;
                        if (!seenFieldSig.Add(sig)) continue;
                        if (!GhScriptNameLooksLikePayload(fld.Name)) continue;
                        fieldCandidates.Add(fld);
                    }
                }

                foreach (var prop in propCandidates.OrderByDescending(p => GhScriptMemberPreference(p.Name)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        prop.GetSetMethod(true).Invoke(obj, new object[] { newCode });
                        AddGhLog.Debug("Successfully set script via property: " + prop.Name);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        AddGhLog.Debug("Failed to set via property " + prop.Name + ": " + ex.Message);
                    }
                }

                foreach (var fld in fieldCandidates.OrderByDescending(f => GhScriptMemberPreference(f.Name)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        fld.SetValue(obj, newCode);
                        AddGhLog.Debug("Successfully set script via field: " + fld.Name);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        AddGhLog.Debug("Failed to set via field " + fld.Name + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("TrySetNativeScriptContentViaReflection failed: " + ex.Message);
            }

            return false;
        }

        /// <summary>把脚本/表达式类电池中能读到的 string 成员填入 script_bodies（截断以适应 token）。</summary>
        private static void AppendScriptBodiesToComponentJson(JObject compJson, Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            if (obj == null || compJson == null) return;

            const int maxTotalApprox = 24000;
            const int maxPerMember = 12000;
            var bag = new JObject();
            int used = 0;

            bool TryPut(string logicalName, string raw)
            {
                if (string.IsNullOrEmpty(raw)) return false;
                string s = GhTruncateScriptSnippet(raw, maxPerMember);
                int bump = logicalName.Length + s.Length + 40;
                if (used + bump > maxTotalApprox) return false;
                bag[logicalName] = s;
                used += bump;
                return true;
            }

            foreach (var entry in GhEnumerateScriptPayloadStrings(obj))
                TryPut(entry.label, entry.text);

            if (bag.Count > 0) {
                compJson["script_bodies"] = bag;
                compJson["runtime_type_hint"] = obj.GetType()?.Name ?? "";
            }
        }

        private static void FinalizeGrasshopperScriptMutation(GH_Document doc, Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            if (doc == null) return;
            try { obj?.ExpireSolution(true); }
            catch (Exception ex) { AddGhLog.Debug("FinalizeGrasshopperScriptMutation Expire failed: " + ex.Message); }
            try { doc.ScheduleSolution(120); }
            catch (Exception ex) { AddGhLog.Warn("FinalizeGrasshopperScriptMutation Schedule(120) failed: " + ex.Message); }
            // A second delayed solve helps freshly edited script bodies and recent port changes settle
            // without requiring the agent to spend another tool call on recompute_gh_canvas.
            try { doc.ScheduleSolution(420); }
            catch (Exception ex) { AddGhLog.Warn("FinalizeGrasshopperScriptMutation Schedule(420) failed: " + ex.Message); }
            try { Grasshopper.Instances.ActiveCanvas?.Refresh(); }
            catch (Exception ex) { AddGhLog.Debug("FinalizeGrasshopperScriptMutation Refresh failed: " + ex.Message); }
        }

        private static void WaitForUiResponsiveDelay(int milliseconds)
        {
            if (milliseconds <= 0) return;
            var dispatcher = _window?.Dispatcher;
            if (dispatcher != null && dispatcher.CheckAccess())
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background,
                    dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(milliseconds)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    frame.Continue = false;
                };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            else
            {
                System.Threading.Thread.Sleep(milliseconds);
            }
        }

        private static FieldInfo FindInstanceFieldInHierarchy(Type type, string fieldName)
        {
            if (type == null || string.IsNullOrWhiteSpace(fieldName)) return null;
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var field = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }

        private static bool GhHasWritableStringMember(Grasshopper.Kernel.IGH_DocumentObject obj, string name)
        {
            if (obj == null || string.IsNullOrWhiteSpace(name)) return false;
            for (Type t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (prop.PropertyType != typeof(string)) continue;
                    if (prop.GetIndexParameters().Length != 0) continue;
                    if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (prop.GetSetMethod(true) != null) return true;
                }
                foreach (var fld in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (fld.FieldType != typeof(string)) continue;
                    if (!fld.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    return true;
                }
            }
            return false;
        }

        private static bool TrySetScriptMemberExact(Grasshopper.Kernel.IGH_DocumentObject obj, string member, string text, out string detail)
        {
            detail = null;
            if (obj == null || string.IsNullOrWhiteSpace(member) || text == null) return false;
            member = member.Trim();
            member = member.Replace("[prop]", "").Replace("[field]", "").Trim();

            // 模型常误把 Python 3 Script / GhPython 正文写进 Description；可执行源码在 Text。
            if (member.Equals("Description", StringComparison.OrdinalIgnoreCase) && GhHasWritableStringMember(obj, "Text"))
                member = "Text";

            for (Type t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (prop.PropertyType != typeof(string)) continue;
                    if (prop.GetIndexParameters().Length != 0) continue;
                    if (!prop.Name.Equals(member, StringComparison.OrdinalIgnoreCase)) continue;
                    var setter = prop.GetSetMethod(true);
                    if (setter == null) continue;
                    try {
                        setter.Invoke(obj, new object[] { text });
                        detail = prop.Name + " (prop)";
                        return true;
                    } catch (Exception ex) {
                        AddGhLog.Debug("TrySetScriptMemberExact prop " + prop.Name + ": " + ex.Message);
                        return false;
                    }
                }

                foreach (var fld in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (fld.FieldType != typeof(string)) continue;
                    if (!fld.Name.Equals(member, StringComparison.OrdinalIgnoreCase)) continue;
                    try {
                        fld.SetValue(obj, text);
                        detail = fld.Name + " (field)";
                        return true;
                    } catch (Exception ex) {
                        AddGhLog.Debug("TrySetScriptMemberExact field " + fld.Name + ": " + ex.Message);
                        return false;
                    }
                }
            }
            return false;
        }

        /// <summary> 脚本/表达式类电池：按 string 属性/字段名启发式写入。 </summary>
        private static bool TrySetGrasshopperScriptOrFormula(Grasshopper.Kernel.IGH_DocumentObject obj, string text, out string detail)
        {
            detail = null;
            if (obj == null || text == null) return false;

            // 跳过内置 C#/VB 脚本电池（有 m_codeBlocks 字段），避免破坏内部结构
            Type tt = obj.GetType();
            if (tt != null && FindInstanceFieldInHierarchy(tt, "m_codeBlocks") != null)
                return false;

            var propCandidates = new List<System.Reflection.PropertyInfo>();
            var fieldCandidates = new List<System.Reflection.FieldInfo>();
            var seenProp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFieldSig = new HashSet<string>();

            for (Type t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (prop.PropertyType != typeof(string)) continue;
                    if (!seenProp.Add(prop.Name)) continue;
                    if (prop.GetIndexParameters().Length != 0) continue;
                    if (prop.GetSetMethod(true) == null) continue;
                    if (!GhScriptNameLooksLikePayload(prop.Name)) continue;
                    propCandidates.Add(prop);
                }

                foreach (var fld in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (fld.FieldType != typeof(string)) continue;
                    string sig = (t.FullName ?? t.Name) + "::" + fld.Name;
                    if (!seenFieldSig.Add(sig)) continue;
                    if (!GhScriptNameLooksLikePayload(fld.Name)) continue;
                    fieldCandidates.Add(fld);
                }
            }

            foreach (var prop in propCandidates.OrderByDescending(p => GhScriptMemberPreference(p.Name)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                try {
                    prop.GetSetMethod(true).Invoke(obj, new object[] { text });
                    detail = prop.Name + " (prop)";
                    return true;
                } catch (Exception ex) {
                    AddGhLog.Debug("TrySetGrasshopperScriptOrFormula prop " + prop?.Name + ": " + ex.Message);
                }
            }

            foreach (var fld in fieldCandidates.OrderByDescending(f => GhScriptMemberPreference(f.Name)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                try {
                    fld.SetValue(obj, text);
                    detail = fld.Name + " (field)";
                    return true;
                } catch (Exception ex) {
                    AddGhLog.Debug("TrySetGrasshopperScriptOrFormula field " + fld?.Name + ": " + ex.Message);
                }
            }

            return false;
        }

        /// <summary> 用于错误提示：列出实例上可写的 string 属性与字段名。 </summary>
        private static string DescribeWritableStringProperties(Grasshopper.Kernel.IGH_DocumentObject obj, int maxNames = 20)
        {
            if (obj == null) return "";
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (Type t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (prop.PropertyType != typeof(string)) continue;
                    if (prop.GetIndexParameters().Length != 0) continue;
                    if (prop.GetSetMethod(true) == null) continue;
                    if (!seen.Add(prop.Name)) continue;
                    names.Add(prop.Name + "[prop]");
                    if (names.Count >= maxNames) break;
                }
                if (names.Count >= maxNames) break;
            }
            for (Type t = obj.GetType(); t != null && t != typeof(object) && names.Count < maxNames; t = t.BaseType)
            {
                foreach (var fld in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (fld.FieldType != typeof(string)) continue;
                    if (!seen.Add(fld.Name)) continue;
                    names.Add(fld.Name + "[field]");
                    if (names.Count >= maxNames) break;
                }
            }
            return names.Count == 0 ? "" : " 可写 string 成员：" + string.Join(", ", names);
        }

        private static void AppendSliderStateJson(Grasshopper.Kernel.IGH_DocumentObject obj, JObject compJson)
        {
            if (!(obj is Grasshopper.Kernel.Special.GH_NumberSlider slider) || compJson == null)
                return;

            compJson["component_kind"] = "number_slider";
            compJson["slider"] = new JObject
            {
                ["current"] = (double)slider.Slider.Value,
                ["minimum"] = (double)slider.Slider.Minimum,
                ["maximum"] = (double)slider.Slider.Maximum,
                ["decimals"] = slider.Slider.DecimalPlaces
            };
        }

        private static decimal ClampSliderValue(Grasshopper.Kernel.Special.GH_NumberSlider slider, decimal value)
        {
            if (slider == null) return value;
            decimal min = slider.Slider.Minimum;
            decimal max = slider.Slider.Maximum;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }


        private static string ExecuteModifyGhPortData(string id, bool isInput, int index, string operation)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }
                if (!Guid.TryParse(id, out Guid guid)) { result = "Error: ID 格式错误。"; return; }
                var obj = doc.FindObject(guid, true);
                if (obj == null) { result = "Error: 找不到电池。"; return; }

                Grasshopper.Kernel.IGH_Param param = null;
                if (obj is Grasshopper.Kernel.IGH_Component comp) {
                    var list = isInput ? comp.Params.Input : comp.Params.Output;
                    if (index >= 0 && index < list.Count) param = list[index];
                } else if (obj is Grasshopper.Kernel.IGH_Param p) {
                    param = p;
                }

                if (param == null) { result = "Error: 端口越界或不支持。"; return; }

                switch (operation.ToLower())
                {
                    case "flatten":
                        param.DataMapping = Grasshopper.Kernel.GH_DataMapping.Flatten;
                        break;
                    case "graft":
                        param.DataMapping = Grasshopper.Kernel.GH_DataMapping.Graft;
                        break;
                    case "simplify":
                        param.Simplify = !param.Simplify;
                        break;
                    case "reverse":
                        param.Reverse = !param.Reverse;
                        break;
                    case "none":
                        param.DataMapping = Grasshopper.Kernel.GH_DataMapping.None;
                        break;
                }

                param.ExpireSolution(true);
                try { doc.ScheduleSolution(150); }
                catch (Exception ex) { AddGhLog.Warn("ExecuteModifyGhPortData Schedule failed: " + ex.Message); }
                result = "端口数据操作成功。";
            }));
            return result;
        }

        private static string ExecuteRemoveGhConnection(string fromId, int fromIndex, string toId, int toIndex)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }
                if (!Guid.TryParse(fromId, out Guid guidFrom) || !Guid.TryParse(toId, out Guid guidTo)) { result = "Error: ID 格式错误。"; return; }
                var objFrom = doc.FindObject(guidFrom, true);
                var objTo = doc.FindObject(guidTo, true);
                if (objFrom == null || objTo == null) { result = "Error: 找不到电池。"; return; }
                Grasshopper.Kernel.IGH_Param sourceParam = (objFrom is Grasshopper.Kernel.IGH_Component cF) ? (fromIndex < cF.Params.Output.Count ? cF.Params.Output[fromIndex] : null) : (objFrom as Grasshopper.Kernel.IGH_Param);
                Grasshopper.Kernel.IGH_Param targetParam = (objTo is Grasshopper.Kernel.IGH_Component cT) ? (toIndex < cT.Params.Input.Count ? cT.Params.Input[toIndex] : null) : (objTo as Grasshopper.Kernel.IGH_Param);
                if (sourceParam == null || targetParam == null) { result = "Error: 端口越界。"; return; }
                targetParam.RemoveSource(sourceParam);
                try { doc.ScheduleSolution(150); }
                catch (Exception ex) { AddGhLog.Warn("ExecuteRemoveGhConnection Schedule failed: " + ex.Message); }
                result = "连线已断开。";
                result += GetCanvasErrors(doc);
            }));
            return result;
        }

        private static Grasshopper.Kernel.IGH_ObjectProxy FindComponentProxy(string name)
        {
            List<Grasshopper.Kernel.IGH_ObjectProxy> exactMatches = new List<Grasshopper.Kernel.IGH_ObjectProxy>();
            foreach (var p in Grasshopper.Instances.ComponentServer.ObjectProxies)
            {
                if (p.Obsolete) continue;
                // 第一优先级：完整名称精确匹配
                if (p.Desc.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) {
                    exactMatches.Add(p);
                }
            }

            // 如果没找到名称匹配，再尝试昵称匹配
            if (exactMatches.Count == 0)
            {
                foreach (var p in Grasshopper.Instances.ComponentServer.ObjectProxies)
                {
                    if (p.Obsolete) continue;
                    if (p.Desc.NickName.Equals(name, StringComparison.OrdinalIgnoreCase)) {
                        exactMatches.Add(p);
                    }
                }
            }

            Grasshopper.Kernel.IGH_ObjectProxy proxy = null;
            if (exactMatches.Count > 0)
            {
                // 优先选择 Grasshopper 原生电池：检查描述里的分类和作者
                foreach (var p in exactMatches)
                {
                    string desc = p.Desc.ToString() ?? "";
                    string category = p.Desc.Category ?? "";
                    string subCategory = p.Desc.SubCategory ?? "";

                    // 原生 Grasshopper 的常见分类
                    bool isNative = category.StartsWith("Math") || category.StartsWith("Sets") ||
                                    category.StartsWith("Vector") || category.StartsWith("Curve") ||
                                    category.StartsWith("Surface") || category.StartsWith("Mesh") ||
                                    category.StartsWith("Intersect") || category.StartsWith("Transform") ||
                                    category.StartsWith("Display") || category.StartsWith("Params") ||
                                    desc.Contains("McNeel") || desc.Contains("David Rutten");

                    if (isNative)
                    {
                        proxy = p;
                        break;
                    }
                }
                // 如果没找到原生的，就用第一个
                if (proxy == null) proxy = exactMatches[0];
            }
            else
            {
                // 模糊匹配
                foreach (var p in Grasshopper.Instances.ComponentServer.ObjectProxies) {
                    if (p.Obsolete) continue;
                    if (p.Desc.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { proxy = p; break; }
                }
            }
            return proxy;
        }

        private static Grasshopper.Kernel.IGH_ObjectProxy FindExactComponentProxyByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var p in Grasshopper.Instances.ComponentServer.ObjectProxies)
            {
                if (p.Obsolete) continue;
                if (string.Equals(p.Desc.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Desc.NickName, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        private static string ResolveScriptComponentName(string mode)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            if (m == "csharp" || m == "cs" || m == "c#") return "C# Script";
            if (m == "python" || m == "py") return "Python 3 Script";
            return null;
        }

        private static string GetCSharpOutputPortName(int index)
        {
            if (index < 0) return "b";
            const string letters = "abcdefghijklmnopqrstuvwxyz";
            int shifted = index + 1;
            if (shifted < letters.Length) return letters[shifted].ToString();
            return "out" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static JArray BuildCSharpOutputPortsFromCount(int count)
        {
            count = Math.Max(1, Math.Min(26, count));
            var outputs = new JArray();
            for (int i = 0; i < count; i++)
            {
                outputs.Add(new JObject
                {
                    ["name"] = GetCSharpOutputPortName(i),
                    ["type_hint"] = "Auto-inferred C# output"
                });
            }
            return outputs;
        }

        private static JArray BuildCSharpOutputPortsFromLabels(JArray outputLabels)
        {
            int count = outputLabels == null ? 0 : Math.Min(26, outputLabels.Count);
            var outputs = new JArray();
            for (int i = 0; i < count; i++)
            {
                var spec = outputLabels != null && i < outputLabels.Count ? outputLabels[i] as JObject : null;
                string name = spec?["name"]?.ToString();
                string label = spec?["label"]?.ToString();
                string typeHint = spec?["type_hint"]?.ToString();
                string variableName = !string.IsNullOrWhiteSpace(name)
                    ? name.Trim()
                    : (!string.IsNullOrWhiteSpace(label) && IsValidCSharpIdentifier(label.Trim())
                        ? label.Trim()
                        : GetCSharpOutputPortName(i));
                outputs.Add(new JObject
                {
                    ["name"] = variableName,
                    ["label"] = label ?? "",
                    ["type_hint"] = typeHint ?? ""
                });
            }
            return outputs;
        }

        private static bool IsValidCSharpIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.Trim();
            if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
            for (int i = 1; i < name.Length; i++)
            {
                if (!(char.IsLetterOrDigit(name[i]) || name[i] == '_')) return false;
            }

            var keywords = new HashSet<string>(StringComparer.Ordinal)
            {
                "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
                "continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern",
                "false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface",
                "internal","is","lock","long","namespace","new","null","object","operator","out","override","params",
                "private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc",
                "static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked",
                "unsafe","ushort","using","virtual","void","volatile","while"
            };
            return !keywords.Contains(name);
        }

        private sealed class CSharpTypedInputBinding
        {
            public string Name;
            public string AliasName;
            public string TypeHint;
            public string Declaration;
        }

        private const string CSharpTypedAliasBlockStart = "// <addgh:auto-typed-inputs>";
        private const string CSharpTypedAliasBlockEnd = "// </addgh:auto-typed-inputs>";

        private static string NormalizeCSharpTypeHint(string typeHint)
        {
            string norm = (typeHint ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(norm)) return "";
            norm = norm.Replace(" ", "").Replace("_", "");
            switch (norm)
            {
                case "number":
                case "float":
                case "double":
                    return "double";
                case "double[]":
                case "doublelist":
                case "list<double>":
                case "list<number>":
                    return "doublelist";
                case "int":
                case "integer":
                    return "int";
                case "int[]":
                case "integer[]":
                case "intlist":
                case "integerlist":
                case "list<int>":
                case "list<integer>":
                    return "intlist";
                case "bool":
                case "boolean":
                    return "bool";
                case "string":
                case "text":
                    return "string";
                case "point":
                case "point3d":
                    return "point3d";
                case "vector":
                case "vector3d":
                    return "vector3d";
                case "curve":
                    return "curve";
                case "curve[]":
                case "curvelist":
                case "list<curve>":
                    return "curvelist";
                case "circle":
                    return "circle";
                case "circle[]":
                case "circles":
                case "circlelist":
                case "list<circle>":
                    return "circlelist";
                case "brep":
                    return "brep";
                case "mesh":
                    return "mesh";
                case "plane":
                    return "plane";
                default:
                    return "";
            }
        }

        private static List<CSharpTypedInputBinding> BuildCSharpTypedInputBindings(JArray inputs, List<string> warnings = null)
        {
            var bindings = new List<CSharpTypedInputBinding>();
            if (inputs == null) return bindings;

            foreach (var token in inputs.OfType<JObject>())
            {
                string name = token["name"]?.ToString()?.Trim();
                if (!IsValidCSharpIdentifier(name)) continue;

                string normalized = NormalizeCSharpTypeHint(token["type_hint"]?.ToString());

                string alias = "__addgh_in_" + name;
                string declaration = "object " + alias + " = __addgh_unwrap(" + name + ");";
                switch (normalized)
                {
                    case "double":
                        declaration = "double " + alias + " = __addgh_to_double(" + name + ");";
                        break;
                    case "doublelist":
                        declaration = "System.Collections.Generic.List<double> " + alias + " = __addgh_to_double_list(" + name + ");";
                        break;
                    case "int":
                        declaration = "int " + alias + " = __addgh_to_int(" + name + ");";
                        break;
                    case "intlist":
                        declaration = "System.Collections.Generic.List<int> " + alias + " = __addgh_to_int_list(" + name + ");";
                        break;
                    case "bool":
                        declaration = "bool " + alias + " = __addgh_to_bool(" + name + ");";
                        break;
                    case "string":
                        declaration = "string " + alias + " = __addgh_to_string(" + name + ");";
                        break;
                    case "point3d":
                        declaration = "object __addgh_raw_" + name + " = __addgh_unwrap(" + name + "); Rhino.Geometry.Point3d " + alias + " = __addgh_raw_" + name + " is Rhino.Geometry.Point3d v_" + name + " ? v_" + name + " : Rhino.Geometry.Point3d.Unset;";
                        break;
                    case "vector3d":
                        declaration = "object __addgh_raw_" + name + " = __addgh_unwrap(" + name + "); Rhino.Geometry.Vector3d " + alias + " = __addgh_raw_" + name + " is Rhino.Geometry.Vector3d v_" + name + " ? v_" + name + " : Rhino.Geometry.Vector3d.Unset;";
                        break;
                    case "curve":
                        declaration = "Rhino.Geometry.Curve " + alias + " = __addgh_to_curve(" + name + ");";
                        break;
                    case "curvelist":
                        declaration = "System.Collections.Generic.List<Rhino.Geometry.Curve> " + alias + " = __addgh_to_curve_list(" + name + ");";
                        break;
                    case "circle":
                        declaration = "Rhino.Geometry.Circle " + alias + " = __addgh_to_circle(" + name + ");";
                        break;
                    case "circlelist":
                        declaration = "System.Collections.Generic.List<Rhino.Geometry.Circle> " + alias + " = __addgh_to_circle_list(" + name + ");";
                        break;
                    case "brep":
                        declaration = "Rhino.Geometry.Brep " + alias + " = __addgh_unwrap(" + name + ") as Rhino.Geometry.Brep;";
                        break;
                    case "mesh":
                        declaration = "Rhino.Geometry.Mesh " + alias + " = __addgh_unwrap(" + name + ") as Rhino.Geometry.Mesh;";
                        break;
                    case "plane":
                        declaration = "object __addgh_raw_" + name + " = __addgh_unwrap(" + name + "); Rhino.Geometry.Plane " + alias + " = __addgh_raw_" + name + " is Rhino.Geometry.Plane v_" + name + " ? v_" + name + " : Rhino.Geometry.Plane.Unset;";
                        break;
                }

                bindings.Add(new CSharpTypedInputBinding
                {
                    Name = name,
                    AliasName = alias,
                    TypeHint = normalized,
                    Declaration = declaration
                });
            }

            if (bindings.Count > 0)
                warnings?.Add("C# input aliases were injected per port. Ports with recognized type_hint use defensive typed aliases; ports without type_hint use unwrapped object aliases.");
            return bindings;
        }

        private static string BuildCSharpTypedInputHelperBlock()
        {
            var sb = new StringBuilder();
            sb.AppendLine("System.Func<object, object> __addgh_unwrap = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    object current = value;");
            sb.AppendLine("    for (int __addgh_i = 0; __addgh_i < 6 && current != null; __addgh_i++)");
            sb.AppendLine("    {");
            sb.AppendLine("        var goo = current as Grasshopper.Kernel.Types.IGH_Goo;");
            sb.AppendLine("        if (goo != null)");
            sb.AppendLine("        {");
            sb.AppendLine("            object scriptValue = goo.ScriptVariable();");
            sb.AppendLine("            if (scriptValue != null && !object.ReferenceEquals(scriptValue, current))");
            sb.AppendLine("            {");
            sb.AppendLine("                current = scriptValue;");
            sb.AppendLine("                continue;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("        var prop = current.GetType().GetProperty(\"Value\", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);");
            sb.AppendLine("        if (prop == null || prop.GetIndexParameters().Length != 0) break;");
            sb.AppendLine("        object next = prop.GetValue(current, null);");
            sb.AppendLine("        if (next == null || object.ReferenceEquals(next, current)) break;");
            sb.AppendLine("        current = next;");
            sb.AppendLine("    }");
            sb.AppendLine("    return current;");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, string> __addgh_to_string = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    return raw == null ? string.Empty : raw.ToString();");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, double> __addgh_to_double = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    if (raw == null) return 0.0;");
            sb.AppendLine("    if (raw is double) return (double)raw;");
            sb.AppendLine("    if (raw is float) return (double)(float)raw;");
            sb.AppendLine("    if (raw is int) return (double)(int)raw;");
            sb.AppendLine("    if (raw is long) return (double)(long)raw;");
            sb.AppendLine("    if (raw is decimal) return (double)(decimal)raw;");
            sb.AppendLine("    double parsed;");
            sb.AppendLine("    if (double.TryParse(raw.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out parsed)) return parsed;");
            sb.AppendLine("    if (double.TryParse(raw.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out parsed)) return parsed;");
            sb.AppendLine("    return 0.0;");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, int> __addgh_to_int = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    if (raw == null) return 0;");
            sb.AppendLine("    if (raw is int) return (int)raw;");
            sb.AppendLine("    if (raw is long) return (int)(long)raw;");
            sb.AppendLine("    double parsed = __addgh_to_double(raw);");
            sb.AppendLine("    return (int)System.Math.Round(parsed);");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, bool> __addgh_to_bool = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    if (raw == null) return false;");
            sb.AppendLine("    if (raw is bool) return (bool)raw;");
            sb.AppendLine("    bool parsed;");
            sb.AppendLine("    if (bool.TryParse(raw.ToString(), out parsed)) return parsed;");
            sb.AppendLine("    double numeric;");
            sb.AppendLine("    if (double.TryParse(raw.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out numeric)) return System.Math.Abs(numeric) > double.Epsilon;");
            sb.AppendLine("    return false;");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, System.Collections.IEnumerable> __addgh_to_sequence = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    if (raw == null || raw is string) return null;");
            sb.AppendLine("    return raw as System.Collections.IEnumerable;");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, Rhino.Geometry.Curve> __addgh_to_curve = null;");
            sb.AppendLine("__addgh_to_curve = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    if (raw == null) return null;");
            sb.AppendLine("    if (raw is Rhino.Geometry.Curve) return raw as Rhino.Geometry.Curve;");
            sb.AppendLine("    if (raw is Rhino.Geometry.Circle) return ((Rhino.Geometry.Circle)raw).ToNurbsCurve();");
            sb.AppendLine("    if (raw is Rhino.Geometry.Arc) return ((Rhino.Geometry.Arc)raw).ToNurbsCurve();");
            sb.AppendLine("    var seq = raw as System.Collections.IEnumerable;");
            sb.AppendLine("    if (seq != null && !(raw is string))");
            sb.AppendLine("    {");
            sb.AppendLine("        foreach (object item in seq)");
            sb.AppendLine("        {");
            sb.AppendLine("            Rhino.Geometry.Curve crv = __addgh_to_curve(item);");
            sb.AppendLine("            if (crv != null) return crv;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    return null;");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, System.Collections.Generic.List<Rhino.Geometry.Curve>> __addgh_to_curve_list = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    var list = new System.Collections.Generic.List<Rhino.Geometry.Curve>();");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    var seq = raw as System.Collections.IEnumerable;");
            sb.AppendLine("    if (seq != null && !(raw is string))");
            sb.AppendLine("    {");
            sb.AppendLine("        foreach (object item in seq)");
            sb.AppendLine("        {");
            sb.AppendLine("            Rhino.Geometry.Curve crv = __addgh_to_curve(item);");
            sb.AppendLine("            if (crv != null) list.Add(crv);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    else");
            sb.AppendLine("    {");
            sb.AppendLine("        Rhino.Geometry.Curve crv = __addgh_to_curve(raw);");
            sb.AppendLine("        if (crv != null) list.Add(crv);");
            sb.AppendLine("    }");
            sb.AppendLine("    return list;");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, Rhino.Geometry.Circle> __addgh_to_circle = null;");
            sb.AppendLine("__addgh_to_circle = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    if (raw is Rhino.Geometry.Circle) return (Rhino.Geometry.Circle)raw;");
            sb.AppendLine("    if (raw is Rhino.Geometry.Curve)");
            sb.AppendLine("    {");
            sb.AppendLine("        Rhino.Geometry.Circle circle;");
            sb.AppendLine("        if (((Rhino.Geometry.Curve)raw).TryGetCircle(out circle)) return circle;");
            sb.AppendLine("    }");
            sb.AppendLine("    var seq = raw as System.Collections.IEnumerable;");
            sb.AppendLine("    if (seq != null && !(raw is string))");
            sb.AppendLine("    {");
            sb.AppendLine("        foreach (object item in seq)");
            sb.AppendLine("        {");
            sb.AppendLine("            Rhino.Geometry.Circle circle = __addgh_to_circle(item);");
            sb.AppendLine("            if (circle.IsValid) return circle;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    return Rhino.Geometry.Circle.Unset;");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, System.Collections.Generic.List<Rhino.Geometry.Circle>> __addgh_to_circle_list = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    var list = new System.Collections.Generic.List<Rhino.Geometry.Circle>();");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    var seq = raw as System.Collections.IEnumerable;");
            sb.AppendLine("    if (seq != null && !(raw is string))");
            sb.AppendLine("    {");
            sb.AppendLine("        foreach (object item in seq)");
            sb.AppendLine("        {");
            sb.AppendLine("            Rhino.Geometry.Circle circle = __addgh_to_circle(item);");
            sb.AppendLine("            if (circle.IsValid) list.Add(circle);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("    else");
            sb.AppendLine("    {");
            sb.AppendLine("        Rhino.Geometry.Circle circle = __addgh_to_circle(raw);");
            sb.AppendLine("        if (circle.IsValid) list.Add(circle);");
            sb.AppendLine("    }");
            sb.AppendLine("    return list;");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, System.Collections.Generic.List<double>> __addgh_to_double_list = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    var list = new System.Collections.Generic.List<double>();");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    var seq = raw as System.Collections.IEnumerable;");
            sb.AppendLine("    if (seq != null && !(raw is string)) foreach (object item in seq) list.Add(__addgh_to_double(item));");
            sb.AppendLine("    else list.Add(__addgh_to_double(raw));");
            sb.AppendLine("    return list;");
            sb.AppendLine("};");
            sb.AppendLine("System.Func<object, System.Collections.Generic.List<int>> __addgh_to_int_list = value =>");
            sb.AppendLine("{");
            sb.AppendLine("    var list = new System.Collections.Generic.List<int>();");
            sb.AppendLine("    object raw = __addgh_unwrap(value);");
            sb.AppendLine("    var seq = raw as System.Collections.IEnumerable;");
            sb.AppendLine("    if (seq != null && !(raw is string)) foreach (object item in seq) list.Add(__addgh_to_int(item));");
            sb.AppendLine("    else list.Add(__addgh_to_int(raw));");
            sb.AppendLine("    return list;");
            sb.AppendLine("};");
            return sb.ToString();
        }

        private static List<CSharpTypedInputBinding> BuildCSharpTypedInputBindingsFromComponent(Grasshopper.Kernel.IGH_DocumentObject obj, List<string> warnings = null)
        {
            var inputs = new JArray();
            if (!(obj is Grasshopper.Kernel.IGH_Component comp)) return new List<CSharpTypedInputBinding>();
            foreach (var param in comp.Params.Input)
            {
                var jo = new JObject();
                jo["name"] = param?.Name ?? "";
                string desc = param?.Description ?? "";
                string typeHint = desc;
                const string tag = "[type_hint]";
                int idx = desc.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) typeHint = desc.Substring(idx + tag.Length).Trim();
                int semi = typeHint.IndexOf(';');
                if (semi >= 0) typeHint = typeHint.Substring(0, semi).Trim();
                inputs.Add(new JObject
                {
                    ["name"] = jo["name"],
                    ["type_hint"] = typeHint
                });
            }
            return BuildCSharpTypedInputBindings(inputs, warnings);
        }

        private static string ApplyCSharpTypedInputBindingsToBody(string body, List<CSharpTypedInputBinding> bindings, List<string> warnings = null)
        {
            body = StripExistingCSharpTypedInputAliasBlock(body ?? "");
            if (string.IsNullOrWhiteSpace(body) || bindings == null || bindings.Count == 0)
                return body ?? "";

            string rewritten = body;
            foreach (var binding in bindings.OrderByDescending(b => b.Name.Length))
            {
                rewritten = System.Text.RegularExpressions.Regex.Replace(
                    rewritten,
                    @"\b" + System.Text.RegularExpressions.Regex.Escape(binding.Name) + @"\b",
                    binding.AliasName);
            }

            var prefix = new StringBuilder();
            prefix.AppendLine(CSharpTypedAliasBlockStart);
            prefix.AppendLine("// Auto-injected defensive typed aliases from C# input type hints");
            prefix.Append(BuildCSharpTypedInputHelperBlock());
            foreach (var binding in bindings)
                prefix.AppendLine(binding.Declaration);
            prefix.AppendLine(CSharpTypedAliasBlockEnd);
            prefix.AppendLine();

            warnings?.Add("body 中的输入名已自动改写为强类型别名，避免重复手写 object -> 标量/几何转换。");
            return prefix + rewritten.TrimStart();
        }

        private static string StripExistingCSharpTypedInputAliasBlock(string body)
        {
            if (string.IsNullOrEmpty(body)) return body ?? "";

            string markedPattern =
                @"(?ms)^[ \t]*" + System.Text.RegularExpressions.Regex.Escape(CSharpTypedAliasBlockStart) +
                @".*?^[ \t]*" + System.Text.RegularExpressions.Regex.Escape(CSharpTypedAliasBlockEnd) +
                @"[ \t]*(?:\r?\n){0,2}";
            string stripped = System.Text.RegularExpressions.Regex.Replace(body, markedPattern, "");

            const string legacyHeader = "// Auto-injected typed aliases from C# input type hints";
            int legacy = stripped.IndexOf(legacyHeader, StringComparison.Ordinal);
            if (legacy < 0) return stripped;

            string newline = stripped.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            int lineStart = stripped.LastIndexOf('\n', Math.Max(0, legacy - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            int scan = stripped.IndexOf('\n', legacy);
            if (scan < 0) return stripped.Substring(0, lineStart).TrimEnd();
            scan += 1;

            while (scan < stripped.Length)
            {
                int lineEnd = stripped.IndexOf('\n', scan);
                if (lineEnd < 0) lineEnd = stripped.Length;
                string line = stripped.Substring(scan, lineEnd - scan).Trim();

                if (line.Length == 0)
                {
                    scan = Math.Min(stripped.Length, lineEnd + 1);
                    break;
                }

                if (line.IndexOf("__addgh_in_", StringComparison.Ordinal) < 0)
                    break;

                scan = Math.Min(stripped.Length, lineEnd + 1);
            }

            return stripped.Substring(0, lineStart).TrimEnd() + newline + stripped.Substring(scan).TrimStart();
        }

        private static List<string> GetCSharpOutputVariableNames(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            var names = new List<string>();
            if (!(obj is Grasshopper.Kernel.IGH_Component comp)) return names;

            foreach (var param in comp.Params.Output)
            {
                string name = (param?.Name ?? param?.NickName ?? "").Trim();
                if (string.Equals(name, "out", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsValidCSharpIdentifier(name)) continue;
                if (!names.Contains(name)) names.Add(name);
            }

            return names;
        }

        private static bool TryValidateCSharpOutputUsage(string body, IEnumerable<string> outputNames, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(body) || outputNames == null) return true;

            string[] mutatingMembers = { "Add", "AddRange", "Clear", "Insert", "Remove", "RemoveAt", "RemoveRange" };
            foreach (string outputName in outputNames)
            {
                foreach (string member in mutatingMembers)
                {
                    string pattern = @"\b" + System.Text.RegularExpressions.Regex.Escape(outputName) + @"\s*\.\s*" + member + @"\s*\(";
                    var match = System.Text.RegularExpressions.Regex.Match(body, pattern);
                    if (!match.Success) continue;

                    error = "C# Script output '" + outputName + "' is a ref object parameter; do not call "
                        + outputName + "." + member + "(...). Collect values in a local List<T> or variable, then assign "
                        + outputName + " = result at the end.";
                    return false;
                }
            }

            return true;
        }

        private static void ApplyPortMetadata(Grasshopper.Kernel.IGH_Param param, JToken specToken, bool forceCSharpOutputName = false, int portIndex = 0, List<string> warnings = null)
        {
            if (param == null || specToken == null) return;
            string name = specToken["name"]?.ToString();
            string label = specToken["label"]?.ToString();
            string typeHint = specToken["type_hint"]?.ToString();
            if (!string.IsNullOrWhiteSpace(typeHint))
                TryApplyRuntimeTypeHint(param, typeHint, warnings);
            string typeHintDescription = "";
            if (!string.IsNullOrWhiteSpace(typeHint))
            {
                string trimmedTypeHint = typeHint.Trim();
                typeHintDescription = !string.IsNullOrWhiteSpace(NormalizeCSharpTypeHint(trimmedTypeHint))
                    ? "[type_hint] " + trimmedTypeHint
                    : trimmedTypeHint;
            }
            if (forceCSharpOutputName)
            {
                string forced = GetCSharpOutputPortName(portIndex);
                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name.Trim(), forced, StringComparison.Ordinal))
                    warnings?.Add("C# 输出端口 " + name.Trim() + " 已规范为 " + forced + "；原名称写入 Description。");
                param.Name = forced;
                param.NickName = forced;
                var descParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(name)) descParts.Add("label: " + name.Trim());
                if (!string.IsNullOrWhiteSpace(typeHint)) descParts.Add("type: " + typeHint.Trim());
                if (descParts.Count > 0) param.Description = string.Join("; ", descParts);
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                param.Name = name.Trim();
                param.NickName = name.Trim();
                var descParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(label) && !string.Equals(label.Trim(), name.Trim(), StringComparison.Ordinal))
                    descParts.Add("label: " + label.Trim());
                if (!string.IsNullOrWhiteSpace(typeHint))
                    descParts.Add(typeHintDescription);
                if (descParts.Count > 0)
                    param.Description = string.Join("; ", descParts);
            }
            else if (!string.IsNullOrWhiteSpace(typeHint))
            {
                param.Description = typeHintDescription;
            }
            param.Attributes?.ExpireLayout();
        }

        private static string NormalizeCSharpScriptSourceForMutableBlock(string source, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(source)) return source ?? "";
            string text = source.Replace("\r\n", "\n").Replace('\r', '\n');
            int runIdx = text.IndexOf("RunScript", StringComparison.Ordinal);
            if (runIdx < 0 || text.IndexOf("Script_Instance", StringComparison.Ordinal) < 0)
                return source;

            int open = text.IndexOf('{', runIdx);
            if (open < 0) return source;

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        string body = text.Substring(open + 1, i - open - 1).Trim('\n', '\r');
                        warnings?.Add("检测到 C# 完整模板，已仅保留 RunScript 方法内部逻辑，默认 using/class/签名模板未替换。");
                        return body;
                    }
                }
            }

            return source;
        }

        private static bool TryFindRunScriptBodyBounds(string source, out int bodyStart, out int bodyEnd)
        {
            bodyStart = -1;
            bodyEnd = -1;
            if (string.IsNullOrEmpty(source)) return false;

            int runIdx = source.IndexOf("RunScript", StringComparison.Ordinal);
            if (runIdx < 0) return false;

            int open = source.IndexOf('{', runIdx);
            if (open < 0) return false;

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                char ch = source[i];
                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        bodyStart = open + 1;
                        bodyEnd = i;
                        return bodyEnd >= bodyStart;
                    }
                }
            }

            return false;
        }

        private static string IndentCSharpBodyForTemplate(string body, string indent)
        {
            string norm = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', '\r');
            if (string.IsNullOrEmpty(norm)) return "";

            var lines = norm.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0) lines[i] = indent + lines[i];
                else lines[i] = indent;
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static bool TrySetCSharpBodyByReplacingRunScriptInStringMembers(Grasshopper.Kernel.IGH_DocumentObject obj, string body, out string detail)
        {
            detail = null;
            if (obj == null || body == null) return false;

            for (Type t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0 && p.GetSetMethod(true) != null)
                    .OrderByDescending(p => GhScriptMemberPreference(p.Name)))
                {
                    if (!GhScriptNameLooksLikePayload(prop.Name)) continue;
                    try
                    {
                        string current = prop.GetGetMethod(true)?.Invoke(obj, null) as string;
                        if (!TryReplaceRunScriptBodyInSource(current, body, out string updated)) continue;
                        prop.GetSetMethod(true).Invoke(obj, new object[] { updated });
                        detail = prop.Name + " (prop RunScript body)";
                        return true;
                    }
                    catch (Exception ex) { AddGhLog.Debug("TrySetCSharpBodyByReplacingRunScript prop " + prop.Name + ": " + ex.Message); }
                }

                foreach (var fld in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(f => f.FieldType == typeof(string))
                    .OrderByDescending(f => GhScriptMemberPreference(f.Name)))
                {
                    if (!GhScriptNameLooksLikePayload(fld.Name)) continue;
                    try
                    {
                        string current = fld.GetValue(obj) as string;
                        if (!TryReplaceRunScriptBodyInSource(current, body, out string updated)) continue;
                        fld.SetValue(obj, updated);
                        detail = fld.Name + " (field RunScript body)";
                        return true;
                    }
                    catch (Exception ex) { AddGhLog.Debug("TrySetCSharpBodyByReplacingRunScript field " + fld.Name + ": " + ex.Message); }
                }
            }

            return false;
        }

        private static bool TryReplaceRunScriptBodyInSource(string source, string body, out string updated)
        {
            updated = null;
            if (!TryFindRunScriptBodyBounds(source, out int bodyStart, out int bodyEnd)) return false;

            int lineStart = source.LastIndexOf('\n', Math.Max(0, bodyStart - 1));
            string newline = source.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string indent = "        ";
            if (lineStart >= 0)
            {
                int i = lineStart + 1;
                var sb = new StringBuilder();
                while (i < source.Length && (source[i] == ' ' || source[i] == '\t'))
                {
                    sb.Append(source[i]);
                    i++;
                }
                if (sb.Length > 0) indent = sb.ToString();
            }

            string replacement = newline + IndentCSharpBodyForTemplate(body, indent) + newline + indent.Substring(0, Math.Max(0, indent.Length - 4));
            updated = source.Substring(0, bodyStart) + replacement + source.Substring(bodyEnd);
            return true;
        }

        private static bool TrySetCSharpScriptBodyIntoTemplate(Grasshopper.Kernel.IGH_DocumentObject obj, string source, List<string> warnings)
        {
            if (TrySetCSharpScriptBodyPreservingTemplate(obj, source, warnings))
                return true;

            if (TrySetCSharpBodyByReplacingRunScriptInStringMembers(obj, source, out string detail))
            {
                warnings?.Add("C# Script body was written by replacing the existing RunScript body in " + detail + ".");
                return true;
            }

            warnings?.Add("C# Script editable code block or full RunScript template was not found; refused unsafe full-template replacement.");
            return false;
        }

        private static bool TrySetCSharpScriptBodyPreservingTemplate(Grasshopper.Kernel.IGH_DocumentObject obj, string source, List<string> warnings)
        {
            string body = NormalizeCSharpScriptSourceForMutableBlock(source, warnings);
            Type t = obj?.GetType();
            if (t == null) return false;

            try
            {
                var codeBlocksField = FindInstanceFieldInHierarchy(t, "m_codeBlocks");
                if (codeBlocksField != null && codeBlocksField.GetValue(obj) is GH_CodeBlocks blocks)
                {
                    codeBlocksField.SetValue(obj, GhBuildCodeBlocksReplacingFirstMutable(blocks, body));
                    return true;
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Debug("TrySetCSharpScriptBodyPreservingTemplate m_codeBlocks: " + ex.Message);
            }

            warnings?.Add("C# Script editable code block was not found; refused full-template replacement.");
            return false;
        }

        private static bool TryReadCSharpScriptBodyPreservingTemplate(Grasshopper.Kernel.IGH_DocumentObject obj, out string body, out string detail)
        {
            body = "";
            detail = "";
            Type t = obj?.GetType();
            if (t == null) return false;

            try
            {
                var codeBlocksField = FindInstanceFieldInHierarchy(t, "m_codeBlocks");
                if (codeBlocksField != null && codeBlocksField.GetValue(obj) is GH_CodeBlocks blocks)
                {
                    for (int i = 0; i < blocks.Count; i++)
                    {
                        GH_CodeBlock block = blocks[i];
                        if (block == null || block.ReadOnly) continue;
                        body = string.Join(Environment.NewLine, block.Lines ?? Array.Empty<string>());
                        detail = "m_codeBlocks[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                        return true;
                    }
                    detail = "m_codeBlocks has no editable block.";
                }
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                AddGhLog.Debug("TryReadCSharpScriptBodyPreservingTemplate m_codeBlocks: " + ex.Message);
            }

            return false;
        }

        private static bool TryConfigureScriptPorts(Grasshopper.Kernel.IGH_DocumentObject obj, JArray inputs, JArray outputs, bool csharpMode, List<string> warnings)
        {
            if (!(obj is Grasshopper.Kernel.IGH_Component comp))
            {
                warnings?.Add((obj?.NickName ?? obj?.Name ?? "脚本电池") + " 不是可配置端口的组件。");
                return false;
            }

            if (!(obj is Grasshopper.Kernel.IGH_VariableParameterComponent vpc))
            {
                warnings?.Add((obj.NickName ?? obj.Name ?? "脚本电池") + " 不支持动态端口，已保留默认端口。");
            }
            else
            {
                bool Resize(IList<Grasshopper.Kernel.IGH_Param> list, Grasshopper.Kernel.GH_ParameterSide side, int target)
                {
                    while (list.Count < target)
                    {
                        var created = vpc.CreateParameter(side, list.Count);
                        if (created == null) return false;
                        if (side == Grasshopper.Kernel.GH_ParameterSide.Input) comp.Params.RegisterInputParam(created);
                        else comp.Params.RegisterOutputParam(created);
                    }
                    while (list.Count > target)
                    {
                        int last = list.Count - 1;
                        if (!vpc.CanRemoveParameter(side, last)) return false;
                        comp.Params.UnregisterParameter(list[last]);
                    }
                    return true;
                }

                int inputTarget = inputs?.Count ?? comp.Params.Input.Count;
                int outputTarget = outputs?.Count ?? comp.Params.Output.Count;
                if (!Resize(comp.Params.Input, Grasshopper.Kernel.GH_ParameterSide.Input, inputTarget))
                    warnings?.Add((obj.NickName ?? obj.Name ?? "脚本电池") + " 输入端口数量未能完全调整。");
                if (!Resize(comp.Params.Output, Grasshopper.Kernel.GH_ParameterSide.Output, outputTarget))
                    warnings?.Add((obj.NickName ?? obj.Name ?? "脚本电池") + " 输出端口数量未能完全调整。");

                try { vpc.VariableParameterMaintenance(); } catch (Exception ex) { warnings?.Add("端口维护失败：" + ex.Message); }
                try { comp.Params.OnParametersChanged(); } catch (Exception ex) { warnings?.Add("端口刷新失败：" + ex.Message); }
            }

            for (int i = 0; inputs != null && i < inputs.Count && i < comp.Params.Input.Count; i++)
                ApplyPortMetadata(comp.Params.Input[i], inputs[i], false, i, warnings);
            for (int i = 0; outputs != null && i < outputs.Count && i < comp.Params.Output.Count; i++)
                ApplyPortMetadata(comp.Params.Output[i], outputs[i], csharpMode, i, warnings);

            return true;
        }

        private static bool TryConfigureCSharpScriptPortsAfterDefaultCreate(Grasshopper.Kernel.IGH_DocumentObject obj, JArray inputs, JArray requestedOutputs, List<string> warnings)
        {
            if (!(obj is Grasshopper.Kernel.IGH_Component comp))
            {
                warnings?.Add((obj?.NickName ?? obj?.Name ?? "C# Script") + " is not a configurable component.");
                return false;
            }

            if (!(obj is Grasshopper.Kernel.IGH_VariableParameterComponent vpc))
            {
                warnings?.Add((obj.NickName ?? obj.Name ?? "C# Script") + " does not support dynamic ports; default ports were preserved.");
                return false;
            }

            bool changed = false;

            bool AddPort(Grasshopper.Kernel.GH_ParameterSide side, out Grasshopper.Kernel.IGH_Param created)
            {
                created = null;
                int index = side == Grasshopper.Kernel.GH_ParameterSide.Input ? comp.Params.Input.Count : comp.Params.Output.Count;
                created = vpc.CreateParameter(side, index);
                if (created == null) return false;
                if (side == Grasshopper.Kernel.GH_ParameterSide.Input) comp.Params.RegisterInputParam(created);
                else comp.Params.RegisterOutputParam(created);
                changed = true;
                return true;
            }

            if (inputs != null)
            {
                while (comp.Params.Input.Count < inputs.Count)
                {
                    if (!AddPort(Grasshopper.Kernel.GH_ParameterSide.Input, out _))
                    {
                        warnings?.Add("Failed to add one or more C# input ports; existing default inputs were preserved.");
                        break;
                    }
                }
            }

            var outputTargets = requestedOutputs ?? new JArray();
            var requestedOutputParams = new List<Grasshopper.Kernel.IGH_Param>();
            for (int i = 0; i < outputTargets.Count; i++)
            {
                string requestedName = outputTargets[i]?["name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(requestedName))
                    requestedName = GetCSharpOutputPortName(i);
                var existing = comp.Params.Output.FirstOrDefault(p =>
                    string.Equals(p.Name, requestedName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.NickName, requestedName, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    if (!AddPort(Grasshopper.Kernel.GH_ParameterSide.Output, out existing))
                    {
                        warnings?.Add("Failed to add C# output port " + requestedName + "; default out/a outputs were preserved.");
                        continue;
                    }
                }

                requestedOutputParams.Add(existing);
            }

            if (changed)
            {
                try { vpc.VariableParameterMaintenance(); } catch (Exception ex) { warnings?.Add("C# port maintenance failed: " + ex.Message); }
                try { comp.Params.OnParametersChanged(); } catch (Exception ex) { warnings?.Add("C# port refresh failed: " + ex.Message); }
            }

            for (int i = 0; inputs != null && i < inputs.Count && i < comp.Params.Input.Count; i++)
                ApplyPortMetadata(comp.Params.Input[i], inputs[i], false, i, warnings);

            for (int i = 0; i < outputTargets.Count && i < requestedOutputParams.Count; i++)
                ApplyPortMetadata(requestedOutputParams[i], outputTargets[i], false, i, warnings);

            return true;
        }

        private static bool IsCSharpScriptComponent(Grasshopper.Kernel.IGH_DocumentObject obj)
        {
            if (obj == null) return false;
            string name = obj.Name ?? "";
            string nick = obj.NickName ?? "";
            if (name.IndexOf("C#", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Script", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (nick.IndexOf("C#", StringComparison.OrdinalIgnoreCase) >= 0 && nick.IndexOf("Script", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (TryReflectGhScriptLanguage(obj, out GH_ScriptLanguage lang, out _) && lang == GH_ScriptLanguage.CS)
                return true;
            return obj.GetType()?.GetField("m_codeBlocks", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
        }



        private static string ExecuteCreateScriptComponentGraph(string mode, JArray scripts, JArray components, JArray connections, string groupName = null)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }

                string scriptComponentName = ResolveScriptComponentName(mode);
                if (string.IsNullOrWhiteSpace(scriptComponentName))
                {
                    result = "Error: mode 必须是 csharp 或 python。";
                    return;
                }

                if (scriptComponentName == "C# Script")
                {
                    result = "Error: C# Script must be created with create_csharp_script_component so ports are configured before only the RunScript body is written.";
                    return;
                }

                if (scripts == null || scripts.Count == 0)
                {
                    result = "Error: scripts 至少需要一个脚本电池定义。";
                    return;
                }

                var aliasSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var scriptProxy = FindExactComponentProxyByName(scriptComponentName);
                if (scriptProxy == null)
                {
                    result = scriptComponentName == "Python 3 Script"
                        ? "Error: 找不到 Python 3 Script 电池。混合模式只适配 Rhino 8 Python 3 Script，请确认已安装并启用该组件。"
                        : "Error: 找不到 C# Script 电池。请确认 Grasshopper 脚本组件已加载。";
                    return;
                }

                foreach (var s in scripts)
                {
                    string alias = s["alias_id"]?.ToString();
                    if (string.IsNullOrWhiteSpace(alias)) { result = "Error: 每个脚本电池都必须提供 alias_id。"; return; }
                    if (!aliasSet.Add(alias)) { result = "Error: alias_id 重复：" + alias; return; }
                    var probe = scriptProxy.CreateInstance() as Grasshopper.Kernel.IGH_DocumentObject;
                    if (probe == null) { result = "Error: 无法实例化 " + scriptComponentName + "。"; return; }
                    if (!(probe is Grasshopper.Kernel.IGH_Component)) { result = "Error: " + scriptComponentName + " 不是可连线组件。"; return; }
                }

                if (components != null)
                {
                    foreach (var c in components)
                    {
                        string alias = c["alias_id"]?.ToString();
                        if (string.IsNullOrWhiteSpace(alias)) { result = "Error: 每个辅助电池都必须提供 alias_id。"; return; }
                        if (!aliasSet.Add(alias)) { result = "Error: alias_id 重复：" + alias; return; }
                        string name = c["name"]?.ToString();
                        string cguid = c["component_guid"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(cguid))
                        {
                            result = "Error: 辅助电池 " + alias + " 必须提供 name 或 component_guid。";
                            return;
                        }
                        var probe = InstantiateDocumentObjectFromLibrary(name ?? "", cguid);
                        if (probe == null)
                        {
                            result = "Error: 无法实例化辅助电池 " + alias + "。";
                            return;
                        }
                        if (!IsScriptModeAuxiliaryComponentAllowed(probe))
                        {
                            result = BuildScriptModeAuxiliaryComponentError(probe, name ?? alias);
                            return;
                        }
                    }
                }

                var createdObjs = new Dictionary<string, Grasshopper.Kernel.IGH_DocumentObject>(StringComparer.OrdinalIgnoreCase);
                var aliasMap = new JObject();
                var warnings = new List<string>();
                int scriptWriteOk = 0;

                foreach (var s in scripts)
                {
                    string alias = s["alias_id"]?.ToString();
                    string label = s["label"]?.ToString();
                    string source = s["source"]?.ToString() ?? "";
                    float x = s["x"]?.ToObject<float>() ?? 0f;
                    float y = s["y"]?.ToObject<float>() ?? 0f;

                    var obj = scriptProxy.CreateInstance() as Grasshopper.Kernel.IGH_DocumentObject;
                    obj.CreateAttributes();
                    obj.Attributes.Pivot = new System.Drawing.PointF(x, y);
                    if (!string.IsNullOrWhiteSpace(label)) obj.NickName = label;
                    doc.AddObject(obj, false);

                    JArray outputSpecs = s["outputs"] as JArray;
                    if (scriptComponentName == "C# Script")
                    {
                        if (outputSpecs != null && outputSpecs.Count > 0)
                            warnings.Add("C# mode is no longer handled by create_script_component_graph; use create_csharp_script_component.");
                        int outputCount = s["output_count"]?.ToObject<int?>() ?? 1;
                        outputSpecs = BuildCSharpOutputPortsFromCount(outputCount);
                    }

                    TryConfigureScriptPorts(obj, s["inputs"] as JArray, outputSpecs, scriptComponentName == "C# Script", warnings);

                    bool wrote = false;
                    if (scriptComponentName == "C# Script")
                    {
                        wrote = TrySetCSharpScriptBodyIntoTemplate(obj, source, warnings);
                    }
                    else
                    {
                        wrote = TrySetScriptMemberExact(obj, "Text", source, out _);
                        if (!wrote) wrote = TrySetGrasshopperScriptOrFormula(obj, source, out _);
                    }

                    if (wrote)
                    {
                        scriptWriteOk++;
                        FinalizeGrasshopperScriptMutation(doc, obj);
                    }
                    else
                    {
                        warnings.Add("脚本源码未能写入：" + alias);
                    }

                    createdObjs[alias] = obj;
                    aliasMap[alias] = obj.InstanceGuid.ToString();
                }

                if (components != null)
                {
                    foreach (var c in components)
                    {
                        string name = c["name"]?.ToString();
                        string cguid = c["component_guid"]?.ToString();
                        string label = c["label"]?.ToString();
                        float x = c["x"]?.ToObject<float>() ?? 0;
                        float y = c["y"]?.ToObject<float>() ?? 0;
                        string val = c["value"]?.ToString();
                        string graphMapperType = GetGraphMapperTypeRequest(c, val);
                        double? min = c["min"]?.ToObject<double>();
                        double? max = c["max"]?.ToObject<double>();
                        int? decimals = c["decimals"]?.ToObject<int>();
                        string alias = c["alias_id"]?.ToString();

                        var obj = InstantiateDocumentObjectFromLibrary(name ?? "", cguid);
                        obj.CreateAttributes();
                        obj.Attributes.Pivot = new System.Drawing.PointF(x, y);
                        if (!string.IsNullOrEmpty(label)) obj.NickName = label;
                        bool isGraphMapper = IsGraphMapperObject(obj);
                        if (isGraphMapper && !TrySetGraphMapperType(obj, graphMapperType, out string graphMapperDetail))
                        {
                            result = graphMapperDetail;
                            return;
                        }
                        doc.AddObject(obj, false);

                        if (obj is Grasshopper.Kernel.Special.GH_NumberSlider slider)
                        {
                            if (min.HasValue) slider.Slider.Minimum = (decimal)min.Value;
                            if (max.HasValue) slider.Slider.Maximum = (decimal)max.Value;
                            if (decimals.HasValue) slider.Slider.DecimalPlaces = Math.Max(0, Math.Min(10, decimals.Value));
                            if (!string.IsNullOrEmpty(val) && decimal.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal d))
                                slider.Slider.Value = ClampSliderValue(slider, d);
                        }
                        else if (obj is Grasshopper.Kernel.Special.GH_Panel panel && !string.IsNullOrEmpty(val))
                        {
                            panel.UserText = val;
                        }
                        else if (isGraphMapper)
                        {
                        }

                        createdObjs[alias] = obj;
                        aliasMap[alias] = obj.InstanceGuid.ToString();
                    }
                }

                int connected = 0;
                if (connections != null)
                {
                    foreach (var conn in connections)
                    {
                        if (createdObjs.TryGetValue(conn["from_alias"]?.ToString(), out var f) && createdObjs.TryGetValue(conn["to_alias"]?.ToString(), out var t))
                        {
                            int fIdx = conn["from_index"]?.ToObject<int>() ?? 0;
                            int tIdx = conn["to_index"]?.ToObject<int>() ?? 0;
                            var sP = (f is Grasshopper.Kernel.IGH_Component cF) ? (fIdx >= 0 && fIdx < cF.Params.Output.Count ? cF.Params.Output[fIdx] : null) : (f as Grasshopper.Kernel.IGH_Param);
                            var tP = (t is Grasshopper.Kernel.IGH_Component cT) ? (tIdx >= 0 && tIdx < cT.Params.Input.Count ? cT.Params.Input[tIdx] : null) : (t as Grasshopper.Kernel.IGH_Param);
                            if (sP != null && tP != null)
                            {
                                tP.AddSource(sP);
                                connected++;
                            }
                            else
                            {
                                warnings.Add("连线端口越界：" + conn["from_alias"] + " -> " + conn["to_alias"]);
                            }
                        }
                        else
                        {
                            warnings.Add("连线引用了不存在的 alias：" + conn["from_alias"] + " -> " + conn["to_alias"]);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(groupName) && createdObjs.Count > 0)
                {
                    var group = new Grasshopper.Kernel.Special.GH_Group();
                    group.NickName = groupName;
                    group.Colour = System.Drawing.Color.FromArgb(80, 100, 150, 250);
                    foreach (var obj in createdObjs.Values) group.AddObject(obj.InstanceGuid);
                    doc.AddObject(group, false);
                    group.ExpireSolution(true);
                }

                try { doc.ScheduleSolution(150); }
                catch (Exception ex) { AddGhLog.Warn("ExecuteCreateScriptComponentGraph Schedule failed: " + ex.Message); }

                var payload = new JObject
                {
                    ["status"] = "ok",
                    ["mode"] = scriptComponentName == "C# Script" ? "csharp" : "python",
                    ["created_scripts"] = scripts.Count,
                    ["created_components"] = components?.Count ?? 0,
                    ["created_connections"] = connected,
                    ["script_write_ok"] = scriptWriteOk,
                    ["aliases"] = aliasMap,
                    ["warnings"] = new JArray(warnings)
                };
                string errors = GetCanvasErrors(doc);
                if (!string.IsNullOrWhiteSpace(errors)) payload["canvas_errors"] = errors;
                result = payload.ToString(Formatting.None);
            }));
            return result;
        }


        private static string ExecuteCaptureRhinoViewport(string framing, int? width, int? height, double? paddingRatio)
        {
            if (IsScreenshotCaptureTemporarilyDisabled())
                return "Error: capture_rhino_viewport is disabled.";

            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                var rhinoDoc = Rhino.RhinoDoc.ActiveDoc;
                if (rhinoDoc == null) { result = "Error: 没有打开的 Rhino 文档。"; return; }

                var view = rhinoDoc.Views.ActiveView ?? rhinoDoc.Views.FirstOrDefault();
                if (view == null) { result = "Error: 没有可用的 Rhino 视口。"; return; }

                string framingMode = string.IsNullOrWhiteSpace(framing) ? "auto" : framing.Trim().ToLowerInvariant();
                int captureWidth = Math.Max(320, Math.Min(4096, width ?? 1600));
                int captureHeight = Math.Max(240, Math.Min(4096, height ?? 900));
                double pad = paddingRatio ?? 0.12;
                if (double.IsNaN(pad) || double.IsInfinity(pad)) pad = 0.12;
                pad = Math.Max(0.0, Math.Min(0.5, pad));

                var ghDoc = Grasshopper.Instances.ActiveCanvas?.Document;
                Rhino.Geometry.BoundingBox targetBox = Rhino.Geometry.BoundingBox.Empty;
                string bboxSource = "current_view";
                int previewCount = 0;
                int rhinoObjectCount = 0;
                bool needsZoom = false;

                bool ghOk = TryGetGrasshopperPreviewBoundingBox(ghDoc, out Rhino.Geometry.BoundingBox ghBox, out previewCount);
                bool rhinoOk = TryGetRhinoDocumentBoundingBox(rhinoDoc, out Rhino.Geometry.BoundingBox rhinoBox, out rhinoObjectCount);

                switch (framingMode)
                {
                    case "gh_preview":
                        if (!ghOk) { result = "Error: 当前没有可用于取景的 Grasshopper 预览几何。"; return; }
                        targetBox = ghBox;
                        bboxSource = "gh_preview";
                        needsZoom = true;
                        break;
                    case "rhino_doc":
                        if (!rhinoOk) { result = "Error: 当前 Rhino 文档中没有可用于取景的可见对象。"; return; }
                        targetBox = rhinoBox;
                        bboxSource = "rhino_doc";
                        needsZoom = true;
                        break;
                    case "current_view":
                        needsZoom = false;
                        bboxSource = "current_view";
                        break;
                    default:
                        if (ghOk)
                        {
                            targetBox = ghBox;
                            bboxSource = "gh_preview";
                            needsZoom = true;
                        }
                        else if (rhinoOk)
                        {
                            targetBox = rhinoBox;
                            bboxSource = "rhino_doc";
                            needsZoom = true;
                        }
                        else
                        {
                            needsZoom = false;
                            bboxSource = "current_view";
                        }
                        break;
                }

                if (needsZoom)
                {
                    Rhino.Geometry.BoundingBox fitted = ExpandBoundingBoxForViewportCapture(targetBox, rhinoDoc, pad);
                    if (!fitted.IsValid)
                    {
                        result = "Error: 视口取景失败，未得到有效的包围盒。";
                        return;
                    }

                    try
                    {
                        view.ActiveViewport.ZoomBoundingBox(fitted);
                        view.Redraw();
                        rhinoDoc.Views.Redraw();
                    }
                    catch (Exception ex)
                    {
                        result = "Error: 视口缩放失败 - " + ex.Message;
                        return;
                    }
                }

                try
                {
                    var capture = new Rhino.Display.ViewCapture
                    {
                        Width = captureWidth,
                        Height = captureHeight,
                        ScaleScreenItems = false,
                        DrawAxes = false,
                        DrawGrid = false,
                        DrawGridAxes = false
                    };

                    using (var bitmap = capture.CaptureToBitmap(view))
                    {
                        if (bitmap == null)
                        {
                            result = "Error: Rhino 视口截图失败，未返回位图。";
                            return;
                        }

                        string dir = GetViewportCaptureDirectory();
                        Directory.CreateDirectory(dir);
                        string filePath = Path.Combine(dir, "rhino_capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                        bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                        string mimeType = GetMimeType(Path.GetExtension(filePath).ToLowerInvariant());
                        string dataUrl = "data:" + mimeType + ";base64," + Convert.ToBase64String(File.ReadAllBytes(filePath));

                        var payload = new JObject
                        {
                            ["path"] = filePath,
                            ["dataUrl"] = dataUrl,
                            ["mimeType"] = mimeType,
                            ["width"] = captureWidth,
                            ["height"] = captureHeight,
                            ["framing"] = framingMode,
                            ["bbox_source"] = bboxSource,
                            ["gh_preview_count"] = previewCount,
                            ["rhino_object_count"] = rhinoObjectCount,
                            ["reasoning_warning"] = "This tool returns screenshot transport metadata only. Do not use bbox/path/count values for geometric or visual reasoning unless the screenshot is reviewed by a vision model."
                        };
                        if (needsZoom && targetBox.IsValid)
                        {
                            payload["bbox"] = new JObject
                            {
                                ["min"] = new JObject
                                {
                                    ["x"] = Math.Round(targetBox.Min.X, 4),
                                    ["y"] = Math.Round(targetBox.Min.Y, 4),
                                    ["z"] = Math.Round(targetBox.Min.Z, 4)
                                },
                                ["max"] = new JObject
                                {
                                    ["x"] = Math.Round(targetBox.Max.X, 4),
                                    ["y"] = Math.Round(targetBox.Max.Y, 4),
                                    ["z"] = Math.Round(targetBox.Max.Z, 4)
                                }
                            };
                        }
                        if (!needsZoom)
                            payload["note"] = "未自动缩放，直接截取当前视图。";
                        else if (bboxSource == "gh_preview")
                            payload["note"] = "已按 Grasshopper 预览几何自动取景，适合检查当前建模结果。";
                        else if (bboxSource == "rhino_doc")
                            payload["note"] = "未找到有效 GH 预览范围，已退回 Rhino 文档对象范围自动取景。";

                        result = payload.ToString(Formatting.None);
                    }
                }
                catch (Exception ex)
                {
                    result = "Error: Rhino 视口截图失败 - " + ex.Message;
                }
            }));
            return result;
        }

        private static bool TryGetGrasshopperPreviewBoundingBox(Grasshopper.Kernel.GH_Document doc, out Rhino.Geometry.BoundingBox bbox, out int previewCount)
        {
            bbox = Rhino.Geometry.BoundingBox.Empty;
            previewCount = 0;
            if (doc == null) return false;

            foreach (var obj in doc.Objects)
            {
                if (!(obj is Grasshopper.Kernel.IGH_PreviewObject po)) continue;
                if (po.Hidden) continue;

                Rhino.Geometry.BoundingBox clip;
                try { clip = po.ClippingBox; }
                catch { continue; }
                if (!clip.IsValid) continue;

                if (previewCount == 0) bbox = clip;
                else bbox.Union(clip);
                previewCount++;
            }

            return previewCount > 0 && bbox.IsValid;
        }

        private static bool TryGetRhinoDocumentBoundingBox(Rhino.RhinoDoc rhinoDoc, out Rhino.Geometry.BoundingBox bbox, out int objectCount)
        {
            bbox = Rhino.Geometry.BoundingBox.Empty;
            objectCount = 0;
            if (rhinoDoc == null) return false;

            foreach (var obj in rhinoDoc.Objects)
            {
                if (obj == null || obj.IsDeleted || obj.IsHidden) continue;
                var geometry = obj.Geometry;
                if (geometry == null) continue;

                Rhino.Geometry.BoundingBox gbox;
                try { gbox = geometry.GetBoundingBox(true); }
                catch { continue; }
                if (!gbox.IsValid) continue;

                if (objectCount == 0) bbox = gbox;
                else bbox.Union(gbox);
                objectCount++;
            }

            return objectCount > 0 && bbox.IsValid;
        }

        private static Rhino.Geometry.BoundingBox ExpandBoundingBoxForViewportCapture(Rhino.Geometry.BoundingBox bbox, Rhino.RhinoDoc rhinoDoc, double paddingRatio)
        {
            if (!bbox.IsValid) return Rhino.Geometry.BoundingBox.Empty;

            double absTol = Math.Max(rhinoDoc?.ModelAbsoluteTolerance ?? 0.01, 0.001);
            var min = bbox.Min;
            var max = bbox.Max;
            var center = bbox.Center;

            double dx = max.X - min.X;
            double dy = max.Y - min.Y;
            double dz = max.Z - min.Z;
            double dominant = Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)), Math.Abs(dz));
            double minAxisSize = Math.Max(absTol * 20.0, dominant > 0 ? dominant * 0.02 : 1.0);

            if (Math.Abs(dx) < minAxisSize) { min.X = center.X - minAxisSize / 2.0; max.X = center.X + minAxisSize / 2.0; }
            if (Math.Abs(dy) < minAxisSize) { min.Y = center.Y - minAxisSize / 2.0; max.Y = center.Y + minAxisSize / 2.0; }
            if (Math.Abs(dz) < minAxisSize) { min.Z = center.Z - minAxisSize / 2.0; max.Z = center.Z + minAxisSize / 2.0; }

            var expanded = new Rhino.Geometry.BoundingBox(min, max);
            var diagonal = expanded.Diagonal;
            double sx = Math.Max(Math.Abs(diagonal.X) * paddingRatio, absTol * 5.0);
            double sy = Math.Max(Math.Abs(diagonal.Y) * paddingRatio, absTol * 5.0);
            double sz = Math.Max(Math.Abs(diagonal.Z) * paddingRatio, absTol * 5.0);

            min = expanded.Min;
            max = expanded.Max;
            min.X -= sx; min.Y -= sy; min.Z -= sz;
            max.X += sx; max.Y += sy; max.Z += sz;
            return new Rhino.Geometry.BoundingBox(min, max);
        }

        private static string GetViewportCaptureDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "Magpie", "captures");
        }

        /// <summary>
        /// 内置 C#/VB 脚本编辑器使用多块源码（只读模板 + RunScript 等可编辑段）。整块替换成单个 block 会破坏结构导致 Rhino 崩溃。
        /// </summary>
        private static GH_CodeBlocks GhBuildCodeBlocksReplacingFirstMutable(GH_CodeBlocks baseline, string text)
        {
            string norm = text == null ? "" : text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] newLines = norm.Length == 0 ? Array.Empty<string>() : norm.Split('\n');

            if (baseline == null || baseline.Count == 0)
            {
                var fb = new GH_CodeBlocks();
                fb.Add(new GH_CodeBlock(newLines, false));
                fb.MergeConsecutiveBlocks();
                return fb;
            }

            var merged = new GH_CodeBlocks();
            bool replacedFirstMutable = false;

            for (int i = 0; i < baseline.Count; i++)
            {
                GH_CodeBlock b = baseline[i];
                bool ro = b.ReadOnly;
                string[] copyLines = (b.Lines ?? Enumerable.Empty<string>()).ToArray();

                if (!ro && !replacedFirstMutable)
                {
                    merged.Add(new GH_CodeBlock(newLines, false));
                    replacedFirstMutable = true;
                }
                else
                    merged.Add(new GH_CodeBlock(copyLines, ro));
            }

            if (!replacedFirstMutable)
                merged.Add(new GH_CodeBlock(newLines, false));

            merged.MergeConsecutiveBlocks();
            return merged;
        }

        private static GH_ScriptLanguage ParseGhNativeScriptLanguageHint(string hint)
        {
            if (string.IsNullOrWhiteSpace(hint) || string.Equals(hint, "auto", StringComparison.OrdinalIgnoreCase))
                return GH_ScriptLanguage.CS;
            string h = hint.Trim();
            if (h.StartsWith("vb", StringComparison.OrdinalIgnoreCase)) return GH_ScriptLanguage.VB;
            return GH_ScriptLanguage.CS;
        }

        private static bool TryReflectGhScriptLanguage(Grasshopper.Kernel.IGH_DocumentObject obj, out GH_ScriptLanguage lang, out string fromMember)
        {
            fromMember = null;
            lang = GH_ScriptLanguage.CS;
            if (obj == null) return false;
            Type tEnum = typeof(GH_ScriptLanguage);
            for (Type t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (p.PropertyType != tEnum) continue;
                    try
                    {
                        object v = p.GetValue(obj);
                        if (v is GH_ScriptLanguage sl && sl != GH_ScriptLanguage.None)
                        {
                            lang = sl;
                            fromMember = p.Name;
                            return true;
                        }
                    }
                    catch (Exception ex) { AddGhLog.Debug("TryReflectGhScriptLanguage: " + ex.Message); }
                }
            }
            return false;
        }

        private static GH_ScriptLanguage ResolveGhNativeScriptLanguage(Grasshopper.Kernel.IGH_DocumentObject obj, string hint)
        {
            if (TryReflectGhScriptLanguage(obj, out GH_ScriptLanguage refl, out _))
                return refl;

            if (obj is Grasshopper.Kernel.IGH_ActiveObject act)
            {
                string nick = act.NickName ?? "";
                if (nick.IndexOf("vb", StringComparison.OrdinalIgnoreCase) >= 0)
                    return GH_ScriptLanguage.VB;
            }

            return ParseGhNativeScriptLanguageHint(hint);
        }

        private static bool TryPerformGhScriptEditorOk(GH_ScriptEditor editor)
        {
            if (editor == null) return false;
            try
            {
                PropertyInfo pi = typeof(GH_ScriptEditor).GetProperty("OKButton", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi?.GetValue(editor) is System.Windows.Forms.Button ok)
                {
                    ok.PerformClick();
                    return true;
                }
            }
            catch (Exception ex) { AddGhLog.Debug("TryPerformGhScriptEditorOk: " + ex.Message); }
            return false;
        }

        private static bool IsGhScriptEditorDisposed(GH_ScriptEditor editor)
        {
            if (editor == null) return true;
            try
            {
                var pi = editor.GetType().GetProperty("IsDisposed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi?.GetValue(editor) is bool disposed) return disposed;
            }
            catch (Exception ex) { AddGhLog.Debug("IsGhScriptEditorDisposed: " + ex.Message); }
            return false;
        }

        private static bool TrySetGhScriptEditorProperty(GH_ScriptEditor editor, string name, object value)
        {
            if (editor == null || string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                var pi = editor.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi == null || !pi.CanWrite) return false;
                pi.SetValue(editor, value);
                return true;
            }
            catch (Exception ex) { AddGhLog.Debug("TrySetGhScriptEditorProperty " + name + ": " + ex.Message); }
            return false;
        }

        private static bool TryInvokeGhScriptEditorMethod(GH_ScriptEditor editor, string name)
        {
            if (editor == null || string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                var mi = editor.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (mi == null) return false;
                mi.Invoke(editor, null);
                return true;
            }
            catch (Exception ex) { AddGhLog.Debug("TryInvokeGhScriptEditorMethod " + name + ": " + ex.Message); }
            return false;
        }

        /// <summary>
        /// 在脚本编辑器 UI 线程上同步执行（避免 Show 后立刻改控件与点 OK 时序错乱）。
        /// </summary>
        private static void GhScriptEditorRunOnUi(GH_ScriptEditor editor, Action work)
        {
            if (editor == null || work == null) return;
            if (IsGhScriptEditorDisposed(editor)) return;
            void Do() { if (!IsGhScriptEditorDisposed(editor)) work(); }

            try
            {
                var invokeRequired = editor.GetType().GetProperty("InvokeRequired", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (invokeRequired?.GetValue(editor) is bool required && required)
                {
                    var invoke = editor.GetType().GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Delegate) }, null);
                    if (invoke != null)
                    {
                        invoke.Invoke(editor, new object[] { (Action)Do });
                        return;
                    }
                }
            }
            catch (Exception ex) { AddGhLog.Debug("GhScriptEditorRunOnUi invoke: " + ex.Message); }

            Do();
        }

        /// <summary> OK 已把脚本写回电池并会自行触发求解；此处仅轻量排队，避免与编辑器内部 NewSolution 重入。 </summary>
        private static void AfterNativeScriptEditorCommit(GH_Document doc)
        {
            try { doc?.ScheduleSolution(80); } catch (Exception ex) { AddGhLog.Debug("AfterNativeScriptEditorCommit Schedule: " + ex.Message); }
            try { Grasshopper.Instances.ActiveCanvas?.Refresh(); } catch { }
        }

        private static string ExecuteGhNativeScriptEditor(string id, string mode, string code, string languageHint)
        {
            const int readCap = 150000;
            string result = "";
            string mraw = mode?.Trim() ?? "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    var canvas = Grasshopper.Instances.ActiveCanvas;
                    var doc = canvas?.Document;
                    if (canvas == null || doc == null) { result = "Error: 没有打开的画布。"; return; }

                    bool isOpen = mraw.Equals("open_focus", StringComparison.OrdinalIgnoreCase);
                    bool isRead = mraw.Equals("read_source", StringComparison.OrdinalIgnoreCase);
                    bool isSet = mraw.Equals("set_source_commit", StringComparison.OrdinalIgnoreCase);
                    if (!isOpen && !isRead && !isSet)
                    {
                        result = "Error: mode 必须是 open_focus、read_source 或 set_source_commit。";
                        return;
                    }

                    if (!Guid.TryParse(id, out Guid guid)) { result = "Error: ID 格式错误。"; return; }
                    var obj = doc.FindObject(guid, true);
                    if (obj == null) { result = "Error: 找不到电池。"; return; }

                    if (isSet && code == null) { result = "Error: set_source_commit 需要 code 参数。"; return; }

                    if (isRead)
                    {
                        int perMember = Math.Min(readCap, 120000);
                        result = GhReadScriptSourceViaReflection(obj, readCap, perMember);
                        return;
                    }

                    GH_ScriptEditor existing = GH_ScriptEditor.FindScriptEditor(obj);
                    GH_ScriptLanguage lang = ResolveGhNativeScriptLanguage(obj, languageHint);

                    GH_ScriptEditor editor = existing;
                    if (editor == null)
                    {
                        try
                        {
                            editor = new GH_ScriptEditor(lang, obj);
                        }
                        catch (Exception ex)
                        {
                            result = "Error: 无法创建原生 GH_ScriptEditor（该宿主可能不是内置 C#/VB Script，例如 GhPython 或 RhinoCode；请改用 set_gh_component_value）：" + ex.Message;
                            return;
                        }
                    }

                    if (isOpen)
                    {
                        TrySetGhScriptEditorProperty(editor, "WindowState", System.Windows.Forms.FormWindowState.Normal);
                        TrySetGhScriptEditorProperty(editor, "StartPosition", System.Windows.Forms.FormStartPosition.CenterParent);
                        if (!editor.Visible)
                            editor.Show(Grasshopper.Instances.DocumentEditor);
                        TryInvokeGhScriptEditorMethod(editor, "BringToFront");
                        TryInvokeGhScriptEditorMethod(editor, "Activate");
                        result = "已打开或聚焦原生脚本编辑器。" + GetCanvasErrors(doc);
                        return;
                    }

                    // 对于 set_source_commit，先尝试使用反射直接修改电池，避免打开编辑器窗口（这是崩溃的主要原因）
                    bool directSetSuccess = false;
                    try
                    {
                        directSetSuccess = TrySetNativeScriptContentViaReflection(obj, code);
                        if (directSetSuccess)
                        {
                            obj.ExpireSolution(true);
                            try { doc.ScheduleSolution(150); }
                            catch (Exception ex) { AddGhLog.Warn("SetNativeScript Schedule failed: " + ex.Message); }
                            try { Grasshopper.Instances.ActiveCanvas?.Refresh(); }
                            catch (Exception ex) { AddGhLog.Debug("SetNativeScript Refresh failed: " + ex.Message); }
                            result = "已直接写入脚本内容（避免了编辑器窗口）。" + GetCanvasErrors(doc);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        AddGhLog.Warn("Direct set native script failed, falling back to editor: " + ex.Message);
                    }

                    // 如果反射失败，作为备选方案使用编辑器（但要更安全）
                    if (isSet && !editor.Visible)
                    {
                        try
                        {
                            TrySetGhScriptEditorProperty(editor, "StartPosition", System.Windows.Forms.FormStartPosition.Manual);
                            TrySetGhScriptEditorProperty(editor, "ShowInTaskbar", false);
                            TrySetGhScriptEditorProperty(editor, "Location", new System.Drawing.Point(-10000, -10000));
                            editor.Show(Grasshopper.Instances.DocumentEditor);
                            // 给一点时间让窗口初始化
                            System.Threading.Thread.Sleep(50);
                        }
                        catch (Exception ex)
                        {
                            AddGhLog.Warn("Failed to show editor offscreen: " + ex.Message);
                        }
                    }

                    bool okClicked = false;
                    try
                    {
                        GhScriptEditorRunOnUi(editor, () =>
                        {
                            GH_CodeBlocks baseline = editor.GetSourceCode();
                            GH_CodeBlocks merged = GhBuildCodeBlocksReplacingFirstMutable(baseline, code);
                            editor.SetSourceCode(merged);
                            okClicked = TryPerformGhScriptEditorOk(editor);
                        });
                    }
                    catch (Exception ex)
                    {
                        AddGhLog.Warn("Editor OK click failed: " + ex.Message);
                        okClicked = false;
                    }

                    // 尝试安全关闭编辑器窗口
                    try
                    {
                        if (editor.Visible && existing == null)
                        {
                            TryInvokeGhScriptEditorMethod(editor, "Hide");
                            editor.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        AddGhLog.Debug("Editor close failed: " + ex.Message);
                    }

                    if (!okClicked)
                    {
                        result = "Error: 无法通过原生编辑器提交脚本（Grasshopper 版本可能受限）。";
                        return;
                    }

                    AfterNativeScriptEditorCommit(doc);
                    result = "已通过原生脚本编辑器写入并提交。" + GetCanvasErrors(doc);
                }
                catch (Exception ex)
                {
                    result = "Error: gh_native_script_editor — " + ex.Message;
                    AddGhLog.Warn("ExecuteGhNativeScriptEditor: " + ex.Message);
                }
            }));

            return result;
        }

        private static string ExecuteSetGhComponentStatus(string id, bool? preview, bool? enabled)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }
                if (!Guid.TryParse(id, out Guid guid)) { result = "Error: ID 格式错误。"; return; }
                var obj = doc.FindObject(guid, true);
                if (obj == null) { result = "Error: 找不到电池。"; return; }

                if (preview.HasValue && obj is Grasshopper.Kernel.IGH_PreviewObject po) po.Hidden = !preview.Value;
                if (enabled.HasValue && obj is Grasshopper.Kernel.IGH_ActiveObject ao) ao.Locked = !enabled.Value;

                obj.ExpireSolution(true);
                try { doc.ScheduleSolution(150); }
                catch (Exception ex) { AddGhLog.Warn("ExecuteSetGhComponentStatus Schedule failed: " + ex.Message); }
                result = "状态更新成功。";
            }));
            return result;
        }

        private static string ExecuteSetAllCSharpScriptPreviews(bool? preview)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }
                if (!preview.HasValue) { result = "Error: preview 参数必填。"; return; }

                int affected = 0;
                foreach (var obj in doc.Objects)
                {
                    if (!IsCSharpScriptComponent(obj)) continue;
                    if (!(obj is Grasshopper.Kernel.IGH_PreviewObject po)) continue;
                    po.Hidden = !preview.Value;
                    try { obj.ExpireSolution(false); } catch { }
                    affected++;
                }

                try { doc.ScheduleSolution(120); } catch (Exception ex) { AddGhLog.Debug("ExecuteSetAllCSharpScriptPreviews Schedule(120): " + ex.Message); }
                try { doc.ScheduleSolution(360); } catch (Exception ex) { AddGhLog.Debug("ExecuteSetAllCSharpScriptPreviews Schedule(360): " + ex.Message); }
                try { Grasshopper.Instances.ActiveCanvas?.Refresh(); } catch { }

                var payload = new JObject
                {
                    ["status"] = "ok",
                    ["preview"] = preview.Value,
                    ["affected_csharp_scripts"] = affected,
                    ["note"] = preview.Value
                        ? "已开启所有 C# Script 预览。"
                        : "已关闭所有 C# Script 预览，适合截图或视觉复核前清理过程预览。"
                };
                result = payload.ToString(Formatting.None);
            }));
            return result;
        }

        private static bool IsScreenshotCaptureTemporarilyDisabled()
        {
            return false;
        }

        private static string ExecutePrepareVisualReviewPreview(string sourceId, int sourceOutputIndex, string label)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }
                if (!Guid.TryParse(sourceId, out Guid sourceGuid)) { result = "Error: source_id 无效。"; return; }

                var sourceObj = doc.FindObject(sourceGuid, true);
                if (sourceObj == null) { result = "Error: 找不到 source_id 对应的组件。"; return; }

                Grasshopper.Kernel.IGH_Param sourceParam = null;
                if (sourceObj is Grasshopper.Kernel.IGH_Component sourceComp)
                {
                    if (sourceOutputIndex < 0 || sourceOutputIndex >= sourceComp.Params.Output.Count)
                    {
                        result = "Error: source_output_index 超出输出端口范围。";
                        return;
                    }
                    sourceParam = sourceComp.Params.Output[sourceOutputIndex];
                }
                else if (sourceObj is Grasshopper.Kernel.IGH_Param sourceSingleParam)
                {
                    sourceParam = sourceSingleParam;
                }
                else
                {
                    result = "Error: source_id 不是可预览输出源。";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_visualReviewPreviewComponentId) && Guid.TryParse(_visualReviewPreviewComponentId, out Guid oldGuid))
                {
                    try
                    {
                        var oldObj = doc.FindObject(oldGuid, true);
                        if (oldObj != null)
                            doc.RemoveObject(oldObj, false);
                    }
                    catch (Exception ex)
                    {
                        AddGhLog.Debug("ExecutePrepareVisualReviewPreview remove old helper: " + ex.Message);
                    }
                }

                var previewProxy = FindExactComponentProxyByName("Geometry");
                if (previewProxy == null)
                {
                    result = "Error: 找不到 Geometry 参数电池，无法创建视觉预览出口。";
                    return;
                }

                var previewObj = previewProxy.CreateInstance() as Grasshopper.Kernel.IGH_DocumentObject;
                if (!(previewObj is Grasshopper.Kernel.IGH_Param previewParam))
                {
                    result = "Error: Geometry 参数电池无法实例化为可连接参数。";
                    return;
                }

                previewObj.CreateAttributes();
                var srcPivot = sourceObj.Attributes?.Pivot ?? new System.Drawing.PointF(0, 0);
                previewObj.Attributes.Pivot = new System.Drawing.PointF(srcPivot.X + 240, srcPivot.Y + 10);
                previewObj.NickName = string.IsNullOrWhiteSpace(label) ? "VisualReviewPreview" : label.Trim();
                doc.AddObject(previewObj, false);

                try { previewParam.AddSource(sourceParam); }
                catch (Exception ex)
                {
                    try { doc.RemoveObject(previewObj, false); } catch { }
                    result = "Error: 预览出口连接失败 - " + ex.Message;
                    return;
                }

                if (previewObj is Grasshopper.Kernel.IGH_PreviewObject previewable)
                    previewable.Hidden = false;

                string previewCleanup = ExecuteSetAllCSharpScriptPreviews(false);
                _visualReviewPreviewComponentId = previewObj.InstanceGuid.ToString();
                _visualReviewTargetSourceId = sourceId;
                _visualReviewTargetOutputIndex = sourceOutputIndex;
                try { doc.ScheduleSolution(120); } catch (Exception ex) { AddGhLog.Debug("ExecutePrepareVisualReviewPreview Schedule(120): " + ex.Message); }
                try { doc.ScheduleSolution(360); } catch (Exception ex) { AddGhLog.Debug("ExecutePrepareVisualReviewPreview Schedule(360): " + ex.Message); }
                try { Grasshopper.Instances.ActiveCanvas?.Refresh(); } catch { }

                var payload = new JObject
                {
                    ["status"] = "ok",
                    ["preview_component_id"] = GetPublicId(doc, previewObj),
                    ["preview_component_guid"] = previewObj.InstanceGuid.ToString(),
                    ["preview_label"] = previewObj.NickName,
                    ["source_id"] = sourceId,
                    ["source_output_index"] = sourceOutputIndex,
                    ["preview_cleanup"] = string.IsNullOrWhiteSpace(previewCleanup) ? "skipped" : previewCleanup
                };
                result = payload.ToString(Formatting.None);
            }));
            return result;
        }

        private static void RefreshCSharpTypedAliasesAfterPortChange(Grasshopper.Kernel.IGH_DocumentObject obj, List<string> warnings)
        {
            if (!IsCSharpScriptComponent(obj)) return;

            if (!TryReadCSharpScriptBodyPreservingTemplate(obj, out string currentBody, out string detail))
            {
                warnings?.Add("C# Script body was not refreshed after port change because the editable body block could not be read.");
                return;
            }

            var bindings = BuildCSharpTypedInputBindingsFromComponent(obj, warnings);
            string rewritten = ApplyCSharpTypedInputBindingsToBody(currentBody, bindings, warnings);
            if (!TrySetCSharpScriptBodyIntoTemplate(obj, rewritten, warnings))
                warnings?.Add("C# Script body was not refreshed after port change because the editable body block could not be written.");
        }

        private static string ExecuteModifyGhComponentPorts(string id, bool isInput, string action, string portName = null, int? index = null, string typeHint = null)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }
                if (!Guid.TryParse(id, out Guid guid)) { result = "Error: ID 格式错误。"; return; }
                var obj = doc.FindObject(guid, true);
                if (!(obj is Grasshopper.Kernel.IGH_VariableParameterComponent vpc)) { result = "Error: 该电池不支持动态端口。"; return; }
                if (_layoutMode == LayoutMode.CSharpFirst && IsCSharpScriptComponent(obj))
                {
                    result = "Error: C# priority mode does not allow modify_gh_component_ports on C# Script components. Change C# Script interfaces through create_csharp_script_component or edit_csharp_script_component so ports, body, and aliases stay synchronized.";
                    return;
                }

                var comp = obj as Grasshopper.Kernel.IGH_Component;
                if (comp == null) { result = "Error: 无法作为组件处理。"; return; }

                var warnings = new List<string>();

                if (action == "add") {
                    if (isInput) {
                        var newParam = vpc.CreateParameter(Grasshopper.Kernel.GH_ParameterSide.Input, comp.Params.Input.Count);
                        comp.Params.RegisterInputParam(newParam);
                        ApplyPortMetadata(newParam, new JObject
                        {
                            ["name"] = string.IsNullOrWhiteSpace(portName) ? newParam?.Name : portName.Trim(),
                            ["type_hint"] = typeHint ?? ""
                        }, false, comp.Params.Input.Count - 1, warnings);
                    } else {
                        var newParam = vpc.CreateParameter(Grasshopper.Kernel.GH_ParameterSide.Output, comp.Params.Output.Count);
                        comp.Params.RegisterOutputParam(newParam);
                        ApplyPortMetadata(newParam, new JObject
                        {
                            ["name"] = string.IsNullOrWhiteSpace(portName) ? newParam?.Name : portName.Trim(),
                            ["type_hint"] = typeHint ?? ""
                        }, false, comp.Params.Output.Count - 1, warnings);
                    }
                } else if (action == "remove") {
                    var list = isInput ? comp.Params.Input : comp.Params.Output;
                    if (list.Count > 0) {
                        int removeIndex = -1;
                        Grasshopper.Kernel.IGH_Param param = null;
                        string targetName = string.IsNullOrWhiteSpace(portName) ? null : portName.Trim();

                        if (!string.IsNullOrWhiteSpace(targetName)) {
                            for (int i = 0; i < list.Count; i++) {
                                var candidate = list[i];
                                if (candidate == null) continue;

                                bool match = (!string.IsNullOrWhiteSpace(candidate.Name) && candidate.Name.Trim().Equals(targetName, StringComparison.OrdinalIgnoreCase))
                                    || (!string.IsNullOrWhiteSpace(candidate.NickName) && candidate.NickName.Trim().Equals(targetName, StringComparison.OrdinalIgnoreCase));
                                if (match) {
                                    if (removeIndex >= 0) {
                                        result = "Error: 端口名称不唯一，请改用 index。";
                                        return;
                                    }
                                    removeIndex = i;
                                    param = candidate;
                                }
                            }

                            if (removeIndex < 0) {
                                result = "Error: 未找到名称为 '" + targetName + "' 的端口。";
                                return;
                            }
                        } else if (index.HasValue) {
                            removeIndex = index.Value;
                            if (removeIndex < 0 || removeIndex >= list.Count) {
                                result = "Error: 端口索引超出范围。";
                                return;
                            }
                            param = list[removeIndex];
                        } else {
                            removeIndex = list.Count - 1;
                            param = list[removeIndex];
                        }

                        if (vpc.CanRemoveParameter(isInput ? Grasshopper.Kernel.GH_ParameterSide.Input : Grasshopper.Kernel.GH_ParameterSide.Output, removeIndex)) {
                            comp.Params.UnregisterParameter(param);
                        } else { result = "Error: 无法删除该端口。"; return; }
                    }
                }

                vpc.VariableParameterMaintenance();
                comp.Params.OnParametersChanged();
                RefreshCSharpTypedAliasesAfterPortChange(obj, warnings);
                obj.ExpireSolution(true);
                try { doc.ScheduleSolution(150); }
                catch (Exception ex) { AddGhLog.Warn("ExecuteModifyGhComponentPorts Schedule failed: " + ex.Message); }
                if (warnings.Count > 0)
                {
                    var payload = new JObject
                    {
                        ["status"] = "ok",
                        ["warnings"] = new JArray(warnings)
                    };
                    result = payload.ToString(Formatting.None);
                }
                else
                    result = "端口修改成功。";
            }));
            return result;
        }

        private static string ExecuteManageGhGroups(string action, List<string> ids, string groupId, string name)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }

                if (action == "create") {
                    var group = new Grasshopper.Kernel.Special.GH_Group();
                    group.NickName = name ?? "Group";
                    group.Colour = System.Drawing.Color.FromArgb(80, 250, 150, 100); // 默认橘色
                    if (ids != null) {
                        foreach (var id in ids) if (Guid.TryParse(id, out Guid g)) group.AddObject(g);
                    }
                    doc.AddObject(group, false);
                    group.ExpireSolution(true);
                    result = "已创建组 '" + group.NickName + "' (ID: " + GetPublicId(doc, group) + ")。";
                } else if (action == "ungroup") {
                    if (Guid.TryParse(groupId, out Guid gId)) {
                        var obj = doc.FindObject(gId, true);
                        if (obj is Grasshopper.Kernel.Special.GH_Group) {
                            doc.RemoveObject(obj, false);
                            result = "已解散组。";
                        } else result = "Error: 找不到该组。";
                    }
                } else if (action == "add_to_group" || action == "remove_from_group") {
                    if (Guid.TryParse(groupId, out Guid gId)) {
                        var obj = doc.FindObject(gId, true);
                        if (obj is Grasshopper.Kernel.Special.GH_Group group) {
                            if (ids != null) {
                                foreach (var id in ids) {
                                    if (Guid.TryParse(id, out Guid g)) {
                                        if (action == "add_to_group") group.AddObject(g);
                                        else group.RemoveObject(g);
                                    }
                                }
                            }
                            group.ExpireSolution(true);
                            result = "组员已更新。";
                        } else result = "Error: 找不到该组。";
                    }
                }
                try { doc.ScheduleSolution(150); }
                catch (Exception ex) { AddGhLog.Warn("ExecuteManageGhGroups Schedule failed: " + ex.Message); }
            }));
            return result;
        }

        private static string ExecuteManageGhGroupsUnified(string action, List<string> ids, string groupId, string name)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: No active Grasshopper canvas."; return; }

                string op = (action ?? "").Trim().ToLowerInvariant();
                bool changed = false;

                if (op == "create" || op == "group" || op == "create_group") {
                    var group = new Grasshopper.Kernel.Special.GH_Group();
                    group.NickName = string.IsNullOrWhiteSpace(name) ? "Group" : name.Trim();
                    group.Colour = System.Drawing.Color.FromArgb(80, 250, 150, 100);

                    int added = 0;
                    foreach (var guid in ReadExistingDocumentObjectGuids(doc, ids, includeGroups: false)) {
                        group.AddObject(guid);
                        added++;
                    }

                    doc.AddObject(group, false);
                    group.ExpireSolution(true);
                    changed = true;
                    result = "Created group '" + group.NickName + "' (ID: " + GetPublicId(doc, group) + ", members: " + added.ToString() + ").";
                } else if (op == "ungroup" || op == "delete_group" || op == "remove_group") {
                    var targetGroupIds = new List<string>();
                    if (!string.IsNullOrWhiteSpace(groupId)) targetGroupIds.Add(groupId);
                    if (ids != null) targetGroupIds.AddRange(ids.Where(v => !string.IsNullOrWhiteSpace(v)));

                    var groups = ResolveGhGroups(doc, targetGroupIds);
                    if (groups.Count == 0) {
                        result = "Error: No matching group found to ungroup.";
                        return;
                    }

                    var names = new List<string>();
                    foreach (var group in groups) {
                        names.Add(string.IsNullOrWhiteSpace(group.NickName) ? GetPublicId(doc, group) : group.NickName);
                        doc.RemoveObject(group, false);
                    }
                    changed = true;
                    result = "Ungrouped " + groups.Count.ToString() + " group(s): " + string.Join(", ", names) + ". Members were left on the canvas.";
                } else if (op == "add_to_group" || op == "add" || op == "remove_from_group" || op == "remove") {
                    var group = ResolveSingleGhGroup(doc, groupId);
                    if (group == null) {
                        result = "Error: Target group not found.";
                        return;
                    }

                    int touched = 0;
                    foreach (var guid in ReadExistingDocumentObjectGuids(doc, ids, includeGroups: false)) {
                        if (op == "add_to_group" || op == "add")
                            group.AddObject(guid);
                        else
                            group.RemoveObject(guid);
                        touched++;
                    }

                    group.ExpireSolution(true);
                    changed = true;
                    result = "Updated group '" + group.NickName + "' (ID: " + GetPublicId(doc, group) + ", affected members: " + touched.ToString() + ").";
                } else {
                    result = "Error: Unsupported group action. Use create, add_to_group, remove_from_group, or ungroup.";
                    return;
                }

                if (changed) {
                    RefreshPublicIdMap(doc);
                    try { doc.ScheduleSolution(150); }
                    catch (Exception ex) { AddGhLog.Warn("ExecuteManageGhGroupsUnified Schedule failed: " + ex.Message); }
                }
            }));
            return result;
        }

        private static List<Guid> ReadExistingDocumentObjectGuids(Grasshopper.Kernel.GH_Document doc, IEnumerable<string> ids, bool includeGroups)
        {
            var result = new List<Guid>();
            if (doc == null || ids == null) return result;

            foreach (var id in ids) {
                if (!Guid.TryParse(id, out Guid guid)) continue;
                var obj = doc.FindObject(guid, true);
                if (obj == null) continue;
                if (!includeGroups && obj is Grasshopper.Kernel.Special.GH_Group) continue;
                if (!result.Contains(guid)) result.Add(guid);
            }
            return result;
        }

        private static Grasshopper.Kernel.Special.GH_Group ResolveSingleGhGroup(Grasshopper.Kernel.GH_Document doc, string groupId)
        {
            if (doc == null || string.IsNullOrWhiteSpace(groupId)) return null;
            if (!Guid.TryParse(groupId, out Guid guid)) return null;
            return doc.FindObject(guid, true) as Grasshopper.Kernel.Special.GH_Group;
        }

        private static List<Grasshopper.Kernel.Special.GH_Group> ResolveGhGroups(Grasshopper.Kernel.GH_Document doc, IEnumerable<string> groupIds)
        {
            var result = new List<Grasshopper.Kernel.Special.GH_Group>();
            if (doc == null || groupIds == null) return result;

            foreach (var id in groupIds) {
                var group = ResolveSingleGhGroup(doc, id);
                if (group != null && !result.Any(g => g.InstanceGuid == group.InstanceGuid))
                    result.Add(group);
            }
            return result;
        }

        private static string ExecuteSearchComponentLibrary(string keyword)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                List<string> ms = new List<string>();
                foreach (var p in Grasshopper.Instances.ComponentServer.ObjectProxies) {
                    if (p.Obsolete) continue;
                    if (p.Desc.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) {
                        ms.Add("- " + p.Desc.Name + " (" + p.Desc.NickName + ")");
                        if (ms.Count > 15) break;
                    }
                }
                result = ms.Count > 0 ? string.Join("\n", ms) : "未找到匹配电池。";
            }));
            return result;
        }

        private static string ExecuteSearchGhComponentCatalog(string query, int maxResults, string categoryContains = null)
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                if (string.IsNullOrWhiteSpace(query)) {
                    result = "Error: query 不能为空。";
                    return;
                }
                if (maxResults <= 0) maxResults = 30;
                if (maxResults > 200) maxResults = 200;

                string q = query.Trim();
                string catFilter = categoryContains?.Trim();
                var matches = new JArray();

                foreach (var p in Grasshopper.Instances.ComponentServer.ObjectProxies) {
                    if (p.Obsolete) continue;
                    string name = p.Desc?.Name ?? "";
                    string nick = p.Desc?.NickName ?? "";
                    string cat = p.Desc?.Category ?? "";
                    string sub = p.Desc?.SubCategory ?? "";

                    if (!string.IsNullOrEmpty(catFilter)) {
                        bool inCat = (cat.IndexOf(catFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                            || (sub.IndexOf(catFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!inCat) continue;
                    }

                    bool hit = name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || nick.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || cat.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || sub.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!hit) continue;

                    matches.Add(new JObject {
                        ["name"] = name,
                        ["nickname"] = nick,
                        ["guid"] = p.Guid.ToString(),
                        ["category"] = cat,
                        ["subcategory"] = sub
                    });

                    if (matches.Count >= maxResults) break;
                }

                var wrap = new JObject {
                    ["count"] = matches.Count,
                    ["items"] = matches
                };
                result = wrap.ToString(Formatting.None);
            }));
            return result;
        }

        private static string GetCanvasErrors(Grasshopper.Kernel.GH_Document doc)
        {
            List<string> errs = new List<string>();
            foreach (var obj in doc.Objects) {
                if (obj is Grasshopper.Kernel.IGH_ActiveObject ao && (ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error || ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Warning)) {
                    foreach (string m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Error)) errs.Add("Error(" + obj.Name + "): " + m);
                    foreach (string m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Warning)) errs.Add("Warning(" + obj.Name + "): " + m);
                }
            }
            return errs.Count > 0 ? "检测到报错:\n" + string.Join("\n", errs) : "";
        }
    }
}
