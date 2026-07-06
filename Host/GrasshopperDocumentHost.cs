using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;

namespace Magpie.Host
{
    /// <summary>
    /// 组件创建策略模式。从 ChatWindow 迁移到宿主层，避免 UI 状态耦合。
    /// </summary>
    public enum LayoutMode
    {
        Battery,
        Mixed,
        CSharpFirst
    }

    /// <summary>
    /// Grasshopper 文档宿主层 — 管理画布状态、Public ID 映射和工具执行。
    /// 从 ChatWindow 中逐步解耦迁移。
    /// </summary>
    public static partial class GrasshopperDocumentHost
    {
        // ── 画布状态缓存 ──
        public static string CachedCanvasState { get; set; }
        public static string CachedRhinoUnitSignature { get; set; }
        public static bool CanvasChanged { get; set; } = true;

        // ── 组件创建策略模式 ──
        public static LayoutMode CurrentLayoutMode { get; set; } = LayoutMode.Mixed;

        // ── Public ID 映射 ──
        public static GH_Document PublicIdBoundDocument { get; set; }
        public static Dictionary<Guid, string> PublicIdByGuid { get; } = new Dictionary<Guid, string>();
        public static Dictionary<string, Guid> GuidByPublicId { get; } = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        public static int NextComponentPublicId { get; set; } = 1;
        public static int NextGroupPublicId { get; set; } = 1;

        // ── Rhino 单位信息 ──
        public static string GetRhinoUnitSignature()
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

        public static Newtonsoft.Json.Linq.JObject BuildRhinoUnitsJson()
        {
            var rhinoDoc = Rhino.RhinoDoc.ActiveDoc;
            if (rhinoDoc == null)
            {
                return new Newtonsoft.Json.Linq.JObject { ["available"] = false };
            }

            return new Newtonsoft.Json.Linq.JObject
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

        // ── Public ID 映射管理 ──
        public static void ResetPublicIdMap(GH_Document doc)
        {
            PublicIdBoundDocument = doc;
            PublicIdByGuid.Clear();
            GuidByPublicId.Clear();
            NextComponentPublicId = 1;
            NextGroupPublicId = 1;
        }

        public static void RefreshPublicIdMap(GH_Document doc)
        {
            if (doc == null)
            {
                ResetPublicIdMap(null);
                return;
            }

            if (!ReferenceEquals(PublicIdBoundDocument, doc))
                ResetPublicIdMap(doc);

            var liveGuids = new HashSet<Guid>(doc.Objects.Select(o => o.InstanceGuid));
            foreach (var stale in PublicIdByGuid.Keys.Where(g => !liveGuids.Contains(g)).ToList())
            {
                string publicId = PublicIdByGuid[stale];
                PublicIdByGuid.Remove(stale);
                GuidByPublicId.Remove(publicId);
            }

            foreach (var obj in doc.Objects)
            {
                if (PublicIdByGuid.ContainsKey(obj.InstanceGuid))
                    continue;

                bool isGroup = obj is Grasshopper.Kernel.Special.GH_Group;
                string publicId = isGroup
                    ? "G" + NextGroupPublicId.ToString("D2")
                    : NextComponentPublicId.ToString("D2");
                if (isGroup) NextGroupPublicId++;
                else NextComponentPublicId++;

                PublicIdByGuid[obj.InstanceGuid] = publicId;
                GuidByPublicId[publicId] = obj.InstanceGuid;
            }
        }

        public static string GetPublicId(GH_Document doc, IGH_DocumentObject obj)
        {
            if (obj == null)
                return "";

            RefreshPublicIdMap(doc);
            if (PublicIdByGuid.TryGetValue(obj.InstanceGuid, out string publicId))
                return publicId;
            return obj.InstanceGuid.ToString();
        }

        public static string GetPublicId(IGH_DocumentObject obj)
        {
            return GetPublicId(Grasshopper.Instances.ActiveCanvas?.Document, obj);
        }

        public static string NormalizePublicId(string id)
        {
            string value = (id ?? "").Trim();
            if (value.StartsWith("#", StringComparison.Ordinal))
                value = value.Substring(1).Trim();
            return value;
        }

        public static bool TryResolveGuidFromPublicId(GH_Document doc, string id, out Guid guid)
        {
            guid = Guid.Empty;
            string token = NormalizePublicId(id);
            if (string.IsNullOrWhiteSpace(token))
                return false;

            if (Guid.TryParse(token, out guid))
                return true;

            RefreshPublicIdMap(doc);
            return GuidByPublicId.TryGetValue(token, out guid);
        }

        /// <summary>
        /// 把 tool 调用里使用的 public ID / GUID 字符串归一化为 GUID 字符串。
        /// 若无法解析则原样返回，保持与旧 ChatWindow.ResolveToolObjectId 兼容。
        /// </summary>
        public static string ResolveToolObjectId(string id)
        {
            var doc = Grasshopper.Instances.ActiveCanvas?.Document;
            if (doc == null || string.IsNullOrWhiteSpace(id))
                return id;

            return TryResolveGuidFromPublicId(doc, id, out Guid guid)
                ? guid.ToString()
                : id;
        }

        public static IGH_DocumentObject FindDocumentObjectByAnyId(GH_Document doc, string id)
        {
            if (doc == null)
                return null;

            if (!TryResolveGuidFromPublicId(doc, id, out Guid guid))
                return null;

            return doc.FindObject(guid, true);
        }

        public static bool ObjectMatchesAnyId(GH_Document doc, IGH_DocumentObject obj, string id)
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

        // ── 已迁移工具：check_gh_errors ──
        public static string ExecuteCheckGhErrors()
        {
            string result = "";
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                var doc = Grasshopper.Instances.ActiveCanvas?.Document;
                if (doc == null) { result = "Error: 没有打开的画布。"; return; }
                result = GetCanvasErrors(doc);
                if (string.IsNullOrEmpty(result)) result = "一切正常。";
            }));
            return result;
        }

        private static string GetCanvasErrors(GH_Document doc)
        {
            List<string> errs = new List<string>();
            foreach (var obj in doc.Objects)
            {
                if (obj is IGH_ActiveObject ao && (ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error || ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Warning))
                {
                    foreach (string m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Error)) errs.Add("Error(" + obj.Name + "): " + m);
                    foreach (string m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Warning)) errs.Add("Warning(" + obj.Name + "): " + m);
                }
            }
            return errs.Count > 0 ? "检测到报错:\n" + string.Join("\n", errs) : "";
        }
    }
}
