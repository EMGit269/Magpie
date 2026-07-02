using System;
using System.IO;
using Magpie.Agent;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static readonly SkillCatalog _skillCatalog = new SkillCatalog();

        private static string BuildSkillCatalogSummary()
        {
            if (!DeploymentOptions.UseSkillCatalogIndex) return null;
            try
            {
                string skillsPath = GetSkillsDirectory();
                if (!Directory.Exists(skillsPath)) return "";
                return _skillCatalog.RenderSummary(skillsPath);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("BuildSkillCatalogSummary failed: " + ex.Message);
                return null;
            }
        }

        private static string ExecuteReadSkillFileWithCatalog(string fileName)
        {
            if (!DeploymentOptions.UseSkillCatalogIndex) return null;
            try
            {
                string skillsPath = GetSkillsDirectory();
                if (string.IsNullOrWhiteSpace(fileName)) return "Error: file_name 不能为空。";

                var entry = _skillCatalog.FindByFileName(skillsPath, fileName);
                if (entry == null)
                    return null;

                string body = _skillCatalog.LoadSkillBody(skillsPath, entry);
                if (!body.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    _contextLedger.RecordLoadedSkill(entry.Id, entry.FileName, "read_skill_file");
                return body;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("ExecuteReadSkillFileWithCatalog failed: " + ex.Message);
                return null;
            }
        }

        private static void UpsertSkillCatalogEntry(string fileName, string quality = null, bool? verified = null)
        {
            if (!DeploymentOptions.UseSkillCatalogIndex) return;
            try
            {
                string skillsPath = GetSkillsDirectory();
                var entry = _skillCatalog.BuildEntryFromFile(skillsPath, fileName, quality, verified);
                if (entry == null) return;
                _skillCatalog.Upsert(skillsPath, entry);
                AddGhLog.Debug("Skill catalog upserted: " + entry.FileName + " -> " + entry.Id);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("UpsertSkillCatalogEntry failed: " + ex.Message);
            }
        }
    }
}
