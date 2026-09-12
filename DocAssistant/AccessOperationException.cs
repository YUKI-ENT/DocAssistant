namespace DocAssistant;
internal sealed class AccessOperationException : Exception
{
    public string Stage { get; }
    public AccessOperationException(string stage, Exception inner)
        : base($"失敗した処理：{stage}\nHRESULT：0x{inner.HResult:X8}\n{inner.Message}", inner)
    {
        Stage = stage;
        HResult = inner.HResult;
    }
}
