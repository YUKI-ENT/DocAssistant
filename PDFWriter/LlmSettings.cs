namespace PDFWriter;

public sealed class LlmPrompt
{
    public string Name { get; set; } = "新しいプロンプト";
    public string Text { get; set; } = "";
    public override string ToString() => Name;
}

public sealed class LlmSettings
{
    public string Address { get; set; } = "http://localhost";
    public int Port { get; set; } = 11434;
    public string Model { get; set; } = "";
    public int PromptIndex { get; set; }
    public List<LlmPrompt> Prompts { get; set; } =
    [
        new() { Name = "経過の要約", Text = " 総括と病歴を、それぞれ100文字、800文字以内にまとめてください。 総括は最初に記載してください。 マークダウン使用しない。存在するデータのみから作成し、ハルシネーションを起こさないように日本語で。" },
        new() { Name = "紹介状の下書き", Text = "提供された所見・投薬・処置をもとに紹介状の診療経過の下書きをマークダウンは使用せず日本語で作成してください。記載にない診断や紹介目的は推測しないでください。" },
        new() { Name = "英語紹介状", Text = "提供された所見・投薬・処置をもとに英文の診療情報提供書を英語で作成してください。マークダウンは使用せず、記載にない診断や紹介目的は推測しないでください。薬剤名も一般名が分かれば英語で、不明ならその部分だけ日本語で(不明)としてください" },
        new() { Name = "治療経過の整理", Text = "提供された記録から投薬と処置の経過を日付付きで整理してください。マークダウンは使用せず、薬剤の開始・中止を記載なく推測しないでください。" }
    ];
}
