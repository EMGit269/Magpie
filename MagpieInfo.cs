using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace Magpie
{
    public class MagpieInfo : GH_AssemblyInfo
    {
        public override string Name => "Magpie";
        
        // 插件图标，这里暂为空
        public override Bitmap Icon => null;
        
        public override string Description => "在 Grasshopper 中通过独立外部 Agent 运行时接入 Magpie";
        
        // 插件的唯一标识符
        public override Guid Id => new Guid("64B76D31-6152-4DC4-B6B6-E65D80D5718D");
        
        public override string AuthorName => "AI Agent";
        
        public override string AuthorContact => "auto-generated";
    }
}
