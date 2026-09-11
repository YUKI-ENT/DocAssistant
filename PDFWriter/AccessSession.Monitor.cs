namespace PDFWriter;

internal sealed record AccessPatientDisplay(bool Detected, string Status, string Text = "", string NotesText = "", string NotesStatus = "", string InstructionsText = "", string DraftText = "", string DraftStatus = "", MedicationHistory? Medication = null, AccessNotes? Clinical = null);

internal sealed partial class AccessSession
{
    internal bool MonitorTimedOut => timedOut;

    public Task<AccessPatientDisplay> PollPatientAsync(bool forceNotes = false) => RunAsync(() =>
    {
        if (!IsAccessInstalled()) return new AccessPatientDisplay(false, "Accessがインストールされていません。");
        object? running = null;
        try
        {
            // Attach only. Never start Access, open an MDB or change the current record.
            running = GetRunningAccess();
            if (running == null) return new AccessPatientDisplay(false, "電子カルテの起動を待っています。");
            var path = GetDatabasePath(running);
            var result = ReadAutomaticPatient(running, (controls, id) => ReadCachedNotes(controls, id, path, forceNotes));
            if (!result.Detected || string.IsNullOrEmpty(result.Text)) { cachedNotes = null; cachedMedication = null; }
            VerifyDatabase(running, path);
            return result;
        }
        finally { Release(running); }
    });

    internal static AccessPatientDisplay ReadAutomaticPatient(object app, Func<object, string, AccessNotes>? readNotes = null)
    {
        object? project = null, allForms = null, forms = null, form = null, controls = null;
        try
        {
            project = AccessDispatch.Get(app, "CurrentProject");
            allForms = AccessDispatch.Get(project, "AllForms");
            var count = Convert.ToInt32(AccessDispatch.Get(allForms, "Count"));
            bool found = false, loaded = false;
            for (int i = 0; i < count; i++)
            {
                object? metadata = null;
                try
                {
                    metadata = AccessDispatch.Get(allForms, "Item", i);
                    if (!string.Equals(Convert.ToString(AccessDispatch.Get(metadata, "Name")), "患者マスター", StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    loaded = Convert.ToBoolean(AccessDispatch.Get(metadata, "IsLoaded"));
                    break;
                }
                finally { Release(metadata); }
            }
            if (!found) return new(false, "起動中のAccessに患者マスターがありません。");
            if (!loaded) return new(true, "電子カルテあり · 患者マスターの表示待ち");
            forms = AccessDispatch.Get(app, "Forms");
            form = AccessDispatch.Get(forms, "Item", "患者マスター");
            if (Convert.ToBoolean(AccessDispatch.Get(form, "NewRecord")))
                return new(true, "電子カルテあり · 患者を選択してください。");
            controls = AccessDispatch.Get(form, "Controls");
            var id = ReadControlValue(controls, "カルテ番号");
            var patient = ReadPatientControls(controls, form);
            AccessNotes draft;
            try { draft = ReadDraftNotes(controls, id); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                draft = new($"当日所見を取得できません。（0x{ex.HResult:X8}）");
            }
            AccessNotes notes;
            try { notes = (readNotes ?? ReadClinicalNotes)(controls, id); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Keep verified demographics usable even if the subform is unavailable.
                notes = new($"診療記録を取得できません。受診カルテサブを確認してください。（0x{ex.HResult:X8}）");
            }
            if (ReadControlValue(controls, "カルテ番号") != id)
                throw new InvalidOperationException("取得中に患者が切り替わりました。もう一度取得してください。");
            return new(true, "電子カルテあり · 表示中の患者に追従しています。", patient, notes.Text, notes.Status, notes.Instructions, draft.Text, draft.Status, null, notes);
        }
        finally { Release(controls); Release(form); Release(forms); Release(allForms); Release(project); }
    }
}
