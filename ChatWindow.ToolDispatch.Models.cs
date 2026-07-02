namespace Magpie
{
    public static partial class ChatWindow
    {
        private sealed class ToolDispatchResult
        {
            public string ToolResult;
            public int AddComp;
            public int DelComp;
            public int AddConn;
            public int DelConn;
            public int AddCodeLines;
            public int DelCodeLines;
            public bool EndApiRoundAwaitingUser;
            public ApiResponse EarlyResponse;
            public string UndoSnapshotPath;
            public string UndoId;
        }
    }
}
