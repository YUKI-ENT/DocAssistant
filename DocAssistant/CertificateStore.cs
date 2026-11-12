using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DocAssistant;

internal sealed record CertificateHistoryEntry(string DocumentId, int Revision, string Patient, string ChartNumber,
    string SavedAt, string IssueId, string FileName, bool Exported)
{
    public override string ToString() => $"{DateTime.Parse(SavedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime():yyyy/MM/dd HH:mm}  {Patient}  [{ChartNumber}]  第{Revision}版  " +
        (IssueId == "" ? "下書き" : Exported ? "発行済み" : "PDF保存済み・出力待ち");
}
internal sealed record CertificateIssue(string Id, MedicalCertificate Data);

// Keep storage behind an interface so a future server API can replace the local database.
internal interface ICertificateStore
{
    string Path { get; }
    string EnvironmentId { get; }
    MedicalCertificate Save(MedicalCertificate data);
    CertificateIssue PrepareIssue(MedicalCertificate data, byte[] pdf, string fileName);
    IReadOnlyList<CertificateHistoryEntry> History(string? patientGroup = null);
    MedicalCertificate Load(string documentId, int revision);
    MedicalCertificate? Latest(string patientGroup);
    byte[] LoadPdf(string issueId);
    void RecordExport(string issueId, string path);
    void Backup(string path);
}

internal sealed class SqliteCertificateStore : ICertificateStore
{
    private const int ApplicationId = 0x44414354;
    public string Path { get; }
    public string EnvironmentId { get; }
    internal SqliteCertificateStore(string path)
    {
        Path = ValidatePath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var connection = Open();
        long app = Convert.ToInt64(Scalar(connection, "PRAGMA application_id"));
        long version = Convert.ToInt64(Scalar(connection, "PRAGMA user_version"));
        if (app != ApplicationId)
        {
            if (app != 0 || Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='table'")) != 0)
                throw new InvalidDataException("本アプリの診断書DBではありません。");
            using var transaction = connection.BeginTransaction();
            Execute(connection, """
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE documents (id TEXT PRIMARY KEY, latest INTEGER NOT NULL, patient_group TEXT NOT NULL, chart TEXT NOT NULL, name TEXT NOT NULL);
                CREATE TABLE revisions (document_id TEXT NOT NULL REFERENCES documents(id), revision INTEGER NOT NULL, saved_at TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(document_id,revision));
                CREATE TABLE issues (id TEXT PRIMARY KEY, document_id TEXT NOT NULL, revision INTEGER NOT NULL, created_at TEXT NOT NULL, filename TEXT NOT NULL, pdf BLOB NOT NULL, sha256 TEXT NOT NULL, FOREIGN KEY(document_id,revision) REFERENCES revisions(document_id,revision));
                CREATE TABLE exports (id INTEGER PRIMARY KEY, issue_id TEXT NOT NULL REFERENCES issues(id), exported_at TEXT NOT NULL, path TEXT NOT NULL);
                CREATE INDEX documents_patient ON documents(patient_group);
                CREATE INDEX issues_revision ON issues(document_id,revision);
                PRAGMA application_id=1145127764;
                PRAGMA user_version=1;
                """, transaction);
            Execute(connection, "INSERT INTO metadata VALUES('environment',$id)", transaction, ("$id", Guid.NewGuid().ToString("N")));
            transaction.Commit();
        }
        else if (version != 1) throw new InvalidDataException("このバージョンでは開けない診断書DBです。");
        EnvironmentId = (string?)Scalar(connection, "SELECT value FROM metadata WHERE key='environment'") ?? throw new InvalidDataException("DB識別子がありません。");
    }

    internal static string ValidatePath(string path)
    {
        var absolute = System.IO.Path.GetFullPath(path);
        if (absolute.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(System.IO.Path.GetPathRoot(absolute)!).DriveType == DriveType.Network)
            throw new ArgumentException("診断書DBはこのPCのローカルドライブに保存してください。共有フォルダーへの直接配置には対応していません。");
        return absolute;
    }
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, DefaultTimeout = 3, ForeignKeys = true }.ToString());
        c.Open();
        Execute(c, "PRAGMA synchronous=FULL");
        return c;
    }
    private static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? t, params (string, object)[] values)
    {
        var command = c.CreateCommand(); command.CommandText = sql; command.Transaction = t;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        return command;
    }
    private static object? Scalar(SqliteConnection c, string sql) { using var cmd = Command(c, sql, null); return cmd.ExecuteScalar(); }
    private static int Execute(SqliteConnection c, string sql, SqliteTransaction? t = null, params (string, object)[] values)
    { using var cmd = Command(c, sql, t, values); return cmd.ExecuteNonQuery(); }

    private MedicalCertificate Save(SqliteConnection c, SqliteTransaction t, MedicalCertificate source)
    {
        var data = MedicalCertificate.Parse(JsonSerializer.Serialize(source));
        if (data.EnvironmentId != "" && data.EnvironmentId != EnvironmentId) throw new InvalidOperationException("別の保存DBの文書です。コピーして新規作成してください。");
        data.EnvironmentId = EnvironmentId;
        using var check = Command(c, "SELECT latest FROM documents WHERE id=$id", t, ("$id", data.DocumentId));
        object? current = check.ExecuteScalar();
        if ((current == null && data.Revision != 0) || (current != null && Convert.ToInt32(current) != data.Revision))
            throw new InvalidOperationException("この文書は別の操作で更新されています。履歴から最新の版を開くか、コピーして新規作成してください。");
        if (current == null)
            Execute(c, "INSERT INTO documents VALUES($id,0,$group,$chart,$name)", t, ("$id", data.DocumentId), ("$group", data.RsbasePatientId()), ("$chart", data.ChartNumber), ("$name", data.Name));
        else
        {
            using var identity = Command(c, "SELECT chart FROM documents WHERE id=$id", t, ("$id", data.DocumentId));
            if ((string?)identity.ExecuteScalar() != data.ChartNumber) throw new InvalidOperationException("保存済み文書の患者番号は変更できません。新規文書として作成してください。");
        }
        data.Revision++;
        Execute(c, "INSERT INTO revisions VALUES($id,$revision,$time,$json)", t, ("$id", data.DocumentId), ("$revision", data.Revision),
            ("$time", DateTime.UtcNow.ToString("O")), ("$json", JsonSerializer.Serialize(data)));
        Execute(c, "UPDATE documents SET latest=$revision,name=$name WHERE id=$id", t, ("$revision", data.Revision), ("$name", data.Name), ("$id", data.DocumentId));
        return data;
    }
    public MedicalCertificate Save(MedicalCertificate data)
    {
        using var c = Open(); using var t = c.BeginTransaction();
        var saved = Save(c, t, data); t.Commit(); return saved;
    }
    public CertificateIssue PrepareIssue(MedicalCertificate data, byte[] pdf, string fileName)
    {
        if (pdf.Length < 5 || System.Text.Encoding.ASCII.GetString(pdf, 0, 5) != "%PDF-") throw new InvalidDataException("PDFデータが不正です。");
        using var c = Open(); using var t = c.BeginTransaction();
        var saved = Save(c, t, data); var id = Guid.NewGuid().ToString("N");
        Execute(c, "INSERT INTO issues VALUES($id,$doc,$revision,$time,$name,$pdf,$hash)", t,
            ("$id", id), ("$doc", saved.DocumentId), ("$revision", saved.Revision), ("$time", DateTime.UtcNow.ToString("O")),
            ("$name", System.IO.Path.GetFileName(fileName)), ("$pdf", pdf), ("$hash", Convert.ToHexString(SHA256.HashData(pdf))));
        t.Commit(); return new(id, saved);
    }
    public IReadOnlyList<CertificateHistoryEntry> History(string? patientGroup = null)
    {
        using var c = Open();
        using var cmd = Command(c, """
            SELECT r.document_id,r.revision,json_extract(r.json,'$.Name'),d.chart,r.saved_at,coalesce(i.id,''),coalesce(i.filename,''),
                   EXISTS(SELECT 1 FROM exports e WHERE e.issue_id=i.id)
            FROM revisions r JOIN documents d ON d.id=r.document_id
            LEFT JOIN issues i ON i.document_id=r.document_id AND i.revision=r.revision
            WHERE $all=1 OR d.patient_group=$group ORDER BY r.saved_at DESC,r.revision DESC
            """, null, ("$all", patientGroup == null ? 1 : 0), ("$group", patientGroup ?? ""));
        using var reader = cmd.ExecuteReader(); var entries = new List<CertificateHistoryEntry>();
        while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7)));
        return entries;
    }
    public MedicalCertificate Load(string documentId, int revision)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT json FROM revisions WHERE document_id=$id AND revision=$revision", null, ("$id", documentId), ("$revision", revision));
        return MedicalCertificate.Parse((string?)cmd.ExecuteScalar() ?? throw new InvalidOperationException("文書がありません。"));
    }
    public MedicalCertificate? Latest(string patientGroup)
    {
        if (string.IsNullOrWhiteSpace(patientGroup)) return null;
        using var c = Open(); using var cmd = Command(c, """
            SELECT r.json FROM documents d JOIN revisions r ON r.document_id=d.id AND r.revision=d.latest
            WHERE d.patient_group=$group ORDER BY r.saved_at DESC,r.rowid DESC LIMIT 1
            """, null, ("$group", patientGroup));
        var json = cmd.ExecuteScalar() as string;
        return json == null ? null : MedicalCertificate.Parse(json);
    }
    public byte[] LoadPdf(string issueId)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT pdf,sha256 FROM issues WHERE id=$id", null, ("$id", issueId));
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) throw new InvalidOperationException("発行時PDFがありません。");
        var pdf = (byte[])reader[0];
        if (Convert.ToHexString(SHA256.HashData(pdf)) != reader.GetString(1)) throw new InvalidDataException("保存PDFの整合性を確認できません。");
        return pdf;
    }
    public void RecordExport(string issueId, string path)
    {
        using var c = Open(); Execute(c, "INSERT INTO exports(issue_id,exported_at,path) VALUES($id,$time,$path)", null,
            ("$id", issueId), ("$time", DateTime.UtcNow.ToString("O")), ("$path", System.IO.Path.GetFullPath(path)));
    }
    public void Backup(string path)
    {
        if (string.Equals(System.IO.Path.GetFullPath(path), Path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("稼働中のDBとは別のファイルを指定してください。");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = Open())
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
            { destination.Open(); source.BackupDatabase(destination); }
            VerifyBackup(temp); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void VerifyBackup(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open();
        if (Convert.ToInt64(Scalar(c, "PRAGMA application_id")) != ApplicationId || Convert.ToInt64(Scalar(c, "PRAGMA user_version")) != 1 ||
            (string?)Scalar(c, "PRAGMA quick_check") != "ok" || Scalar(c, "PRAGMA foreign_key_check") != null)
            throw new InvalidDataException("有効な診断書DBのバックアップではありません。");
        if (Convert.ToInt64(Scalar(c, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('metadata','documents','revisions','issues','exports')")) != 5)
            throw new InvalidDataException("診断書DBの構造が不正です。");
    }
    internal void Restore(string backupPath)
    {
        VerifyBackup(backupPath);
        if (string.Equals(System.IO.Path.GetFullPath(backupPath), Path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("別のバックアップファイルを指定してください。");
        Backup(Path + ".before-restore-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "." + Guid.NewGuid().ToString("N") + ".sqlite");
        var source = new SqliteCertificateStore(backupPath);
        source.Backup(Path);
    }
}
