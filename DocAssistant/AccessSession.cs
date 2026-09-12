using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.VisualBasic;

namespace DocAssistant;

public sealed record AccessControlInfo(string FormPath, string Name, string Kind, string ControlSource, string SourceObject);
public sealed record AccessInspection(string DatabasePath, string TargetForm, bool FormFound,
    IReadOnlyList<string> OpenForms, IReadOnlyList<AccessControlInfo> Controls, IReadOnlyList<string> Notes);

// All Automation objects remain on this one STA. Only strings/DTOs cross to the WPF thread.
internal sealed partial class AccessSession : IDisposable
{
    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgIDEx(string progId, out Guid clsid);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    private static object? GetRunningAccess()
    {
        int hr = CLSIDFromProgIDEx("Access.Application", out var clsid);
        if (hr < 0) hr = CLSIDFromProgID("Access.Application", out clsid);
        if (hr < 0) throw new AccessOperationException("Access.ApplicationのCLSID取得", new COMException("COM登録を取得できません。", hr));
        hr = GetActiveObject(ref clsid, IntPtr.Zero, out var instance);
        if (hr >= 0) return instance;
        Release(instance);
        if (hr == unchecked((int)0x800401E3)) return null;
        throw new AccessOperationException("起動済みAccessのGetActiveObject", new COMException("起動済みAccessを取得できません。", hr));
    }
    private readonly TaskCompletionSource<Dispatcher> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private object? application;
    private string databasePath = "";
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> diagnostics = new();
    public string Diagnostics => string.Join(Environment.NewLine, diagnostics);
    private void Trace(string message) => diagnostics.Enqueue(message);
    private volatile bool disposed, timedOut;

    public AccessSession()
    {
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            started.SetResult(dispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "DocAssistant Access COM" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    internal static bool IsAccessInstalled() => Type.GetTypeFromProgID("Access.Application") != null;
    internal static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("クライアントMDBを選択してください。");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("指定したクライアントMDBが見つかりません。", fullPath);
        if (!new[] { ".mdb", ".accdb", ".mde", ".accde" }.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Accessのデータベースファイルを選択してください。");
        return fullPath;
    }
    private async Task<T> RunAsync<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (timedOut) throw new InvalidOperationException("接続処理がタイムアウトしています。Access側のダイアログを確認し、「開く・接続」から接続し直してください。");
        var dispatcher = await started.Task;
        var task = dispatcher.InvokeAsync(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return action();
        }).Task;
        // A COM server displaying a modal dialog can block. Never block WPF shutdown.
        _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        try { return await task.WaitAsync(TimeSpan.FromSeconds(25)); }
        catch (TimeoutException)
        {
            timedOut = true;
            throw new TimeoutException("Accessから25秒以内に応答がありませんでした。ログイン画面・リンク先接続・確認ダイアログをAccess側で処理してから、接続し直してください。");
        }
    }
    public Task<AccessInspection> ConnectAsync(string path, string formName, bool openIfMissing = true) => RunAsync(() =>
    {
        var fullPath = ValidatePath(path);
        Trace("接続処理 v5 / フォーム項目はInvokeMemberによるプロパティ取得");
        if (!IsAccessInstalled()) throw new InvalidOperationException("この端末ではMicrosoft AccessのCOM登録が見つかりません。Accessがインストールされた電子カルテ端末で実行してください。");
        Release(application); application = null;
        object? candidate = null;
        try
        {
            bool openedFromFile = false;
            candidate = GetRunningAccess();
            Trace(candidate == null ? "GetActiveObject：起動済みAccessなし" : "GetActiveObject：成功");
            if (candidate != null)
            {
                var actual = Step("起動済みAccessのMDBパス確認", () => GetDatabasePath(candidate));
                Trace("起動済みMDB：" + actual);
                if (!SameDatabase(actual, fullPath))
                {
                    Release(candidate); candidate = null;
                    if (!openIfMissing) throw new InvalidOperationException($"接続したAccessのMDBが指定ファイルと異なります。\n指定：{fullPath}\n起動済み：{actual}\n起動済みMDBを選択してください。");
                }
            }
            if (candidate == null)
            {
                if (!openIfMissing) throw new InvalidOperationException("起動済みAccessに接続できません。先に32bit Accessでカルテを開き、AccessとDocAssistantを同じWindowsユーザー・同じ権限で実行してください。");
                candidate = Step("MDBファイルからAccessを開く（GetObject）", () => Interaction.GetObject(fullPath))
                    ?? throw new InvalidOperationException("Accessに接続できませんでした。");
                openedFromFile = true;
            }
            dynamic access = candidate;
            Step("接続先MDBの確認", () => { VerifyDatabase(candidate, fullPath); return true; });
            if (openedFromFile) Step("Accessの表示とUserControl設定", () =>
            {
            if (!(bool)access.UserControl)
            {
                access.Visible = true;
                access.UserControl = true;
            }
                return true;
            });
            application = candidate; candidate = null;
            databasePath = fullPath;
            Trace("接続先MDBの照合：成功。フォーム項目を取得します。");
            return Inspect(application!, databasePath, formName);
        }
        finally { Release(candidate); }
    });
    public Task<AccessInspection> RefreshAsync(string formName, bool openForm = false) => RunAsync(() =>
    {
        if (application == null) throw new InvalidOperationException("先に「開く・接続」を実行してください。");
        Step("接続先MDBの再確認", () => { VerifyDatabase(application, databasePath); return true; });
        if (openForm)
        {
            if (string.IsNullOrWhiteSpace(formName)) throw new InvalidOperationException("対象フォーム名を入力してください。");
            dynamic access = application;
            object? command = null;
            try { command = Step<object>("DoCmdの取得", () => access.DoCmd); Step("対象フォームを開く（OpenForm）", () => { ((dynamic)command).OpenForm(formName); return true; }); }
            finally { Release(command); }
        }
        return Inspect(application, databasePath, formName);
    });
    internal static bool SameDatabase(string actual, string expected) =>
        string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    internal static string GetDatabasePath(object app)
    {
        object? project = null;
        Exception? primaryFailure = null;
        try
        {
            project = ((dynamic)app).CurrentProject;
            string path = Convert.ToString(((dynamic)project).FullName) ?? "";
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { primaryFailure = ex; }
        finally { Release(project); }
        object? database = null;
        try
        {
            database = ((dynamic)app).CurrentDb();
            string path = Convert.ToString(((dynamic)database).Name) ?? "";
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("AccessのMDBパスが空です。対象MDBを開いてください。");
            return path;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            var detail = primaryFailure == null ? "CurrentProject.FullNameは空" : $"CurrentProject.FullName: 0x{primaryFailure.HResult:X8}";
            throw new AccessOperationException($"MDBパスの取得（{detail}、CurrentDb().Nameも失敗）", ex);
        }
        finally { Release(database); }
    }
    private static void VerifyDatabase(object app, string expected)
    {
        if (!SameDatabase(GetDatabasePath(app), expected))
            throw new InvalidOperationException("Accessで開いているデータベースが指定MDBと一致しません。接続先を確認してください。");
    }
    internal static T Step<T>(string stage, Func<T> action)
    {
        try { return action(); }
        catch (COMException ex) { throw new AccessOperationException(stage, ex); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) { throw new AccessOperationException(stage, ex); }
    }
    internal static AccessInspection Inspect(object app, string path, string formName)
    {
        var names = new List<string>();
        var controls = new List<AccessControlInfo>();
        var notes = new List<string>();
        object? project = null, allForms = null;
        bool found = false;
        try
        {
            project = Step<object>("CurrentProjectの取得", () => ((dynamic)app).CurrentProject);
            allForms = Step<object>("AllFormsの取得", () => ((dynamic)project).AllForms);
            int count = Step<int>("AllForms.Countの取得", () => Convert.ToInt32(((dynamic)allForms).Count));
            for (int i = 0; i < count; i++)
            {
                object? form = null;
                try
                {
                    form = Step<object>($"AllForms[{i}]の取得", () => ((dynamic)allForms)[i]);
                    bool loaded = Step<bool>($"AllForms[{i}].IsLoadedの取得", () => Convert.ToBoolean(((dynamic)form).IsLoaded));
                    if (!loaded) continue;
                    string name = Step<string>($"AllForms[{i}].Nameの取得", () => Convert.ToString(((dynamic)form).Name));
                    names.Add(name);
                    if (!string.Equals(name, formName, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                }
                finally { Release(form); }
            }
        }
        finally { Release(allForms); Release(project); }
        if (found)
        {
            object? forms = null, target = null;
            try
            {
                forms = Step<object>("Formsの取得", () => ((dynamic)app).Forms);
                target = Step<object>($"Forms[名前: {formName}]の取得", () => AccessDispatch.Get(forms, "Item", formName));
                // The live form's Name getter can fail although AllForms metadata works.
                InspectControls(target, formName, controls, notes, 0);
            }
            finally { Release(target); Release(forms); }
        }
        if (!found) notes.Add("対象フォームはまだ開いていません。Access側でカルテ画面を開いて「再読み込み」、または「対象フォームを開く」を使ってください。");
        return new AccessInspection(path, formName, found, names, controls, notes);
    }
    private static void InspectControls(object form, string formPath, List<AccessControlInfo> rows, List<string> notes, int depth)
    {
        if (depth > 8) { notes.Add(formPath + "：サブフォームの深さが上限を超えました。"); return; }
        object? controls = null;
        try
        {
            controls = Step<object>($"{formPath}: Controlsの取得（InvokeMember）", () => AccessDispatch.Get(form, "Controls"));
            int count = Step<int>($"{formPath}: Controls.Countの取得", () => Convert.ToInt32(AccessDispatch.Get(controls, "Count")));
            for (int i = 0; i < count; i++)
            {
                if (rows.Count >= 3000) { notes.Add("表示上限の3000項目に達しました。"); return; }
                object? control = null;
                try
                {
                    control = Step<object>($"{formPath}: 項目[{i}]の取得", () => AccessDispatch.Get(controls, "Item", i));
                    string name = Step<string>($"{formPath}: 項目[{i}]の名前取得", () => Convert.ToString(AccessDispatch.Get(control, "Name")) ?? "");
                    int type = Step<int>($"{formPath}/{name}: 種類取得", () => Convert.ToInt32(AccessDispatch.Get(control, "ControlType")));
                    string source = OptionalString(() => AccessDispatch.Get(control, "ControlSource"));
                    string childSource = type == 112 ? OptionalString(() => AccessDispatch.Get(control, "SourceObject")) : "";
                    rows.Add(new AccessControlInfo(formPath, name, ControlKind(type), source, childSource));
                    if (type == 112)
                    {
                        object? child = null;
                        try
                        {
                            child = AccessDispatch.Get(control, "Form");
                            InspectControls(child!, formPath + " / " + name, rows, notes, depth + 1);
                        }
                        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                        { notes.Add(formPath + " / " + name + "：サブフォームを取得できません。未読込またはリンク先の状態を確認してください。"); }
                        finally { Release(child); }
                    }
                }
                finally { Release(control); }
            }
        }
        finally { Release(controls); }
    }
    private static string OptionalString(Func<object?> get)
    {
        try { return Convert.ToString(get()) ?? ""; }
        catch (COMException ex) when (ex.HResult is not (unchecked((int)0x80010108) or unchecked((int)0x800706BA)))
        { return ""; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return ""; }
    }
    private static string ControlKind(int type) => type switch
    {
        100 => "ラベル", 101 => "四角形", 102 => "直線", 103 => "画像", 104 => "コマンドボタン",
        105 => "オプションボタン", 106 => "チェックボックス", 107 => "オプショングループ",
        108 => "連結オブジェクト", 109 => "テキストボックス", 110 => "リストボックス",
        111 => "コンボボックス", 112 => "サブフォーム", 114 => "非連結オブジェクト",
        122 => "トグルボタン", 123 => "タブ", 124 => "タブページ", _ => $"その他 ({type})"
    };
    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        _ = started.Task.ContinueWith(task =>
        {
            _ = task.Result.BeginInvoke(new Action(() =>
            {
                // Never call Quit or CloseCurrentDatabase on the clinician's Access.
                try { Release(application); application = null; }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
        }, TaskScheduler.Default);
    }
}
