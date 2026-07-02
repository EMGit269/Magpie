using System;
using Magpie.Agent;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static readonly ReferenceCatalog _referenceCatalog = new ReferenceCatalog();

        private static string BuildReferenceCatalogSummary()
        {
            if (!DeploymentOptions.UseReferenceCatalogIndex) return null;
            try
            {
                return _referenceCatalog.RenderSummary(GetReferenceDirectory(), GetReferenceIndexPath());
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("BuildReferenceCatalogSummary failed: " + ex.Message);
                return null;
            }
        }

        private static void RefreshReferenceCatalog()
        {
            if (!DeploymentOptions.UseReferenceCatalogIndex) return;
            try
            {
                _referenceCatalog.LoadOrBuildIndex(GetReferenceDirectory(), GetReferenceIndexPath());
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("RefreshReferenceCatalog failed: " + ex.Message);
            }
        }
    }
}
