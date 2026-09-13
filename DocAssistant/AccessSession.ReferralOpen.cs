using System.Globalization;
using System.Runtime.InteropServices;

namespace DocAssistant;

internal sealed partial class AccessSession
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    internal Task OpenReferralAsync(ReferralPatient patient, ReferralLetter letter) => RunAsync(() =>
    {
        object? app = null;
        try
        {
            app = GetRunningAccess() ?? throw new InvalidOperationException("Accessで患者を表示してください。");
            OpenReferralForm(app, patient, letter);
            var window = new IntPtr(Convert.ToInt64(AccessDispatch.Get(app, "hWndAccessApp")));
            if (IsIconic(window)) ShowWindowAsync(window, 9);
            SetForegroundWindow(window);
            return true;
        }
        finally { Release(app); }
    });

    internal static void OpenReferralForm(object app, ReferralPatient patient, ReferralLetter letter)
    {
        if (letter.Number is not long number || number < 0 || letter.ChartNumber / 10 != patient.ChartNumber / 10)
            throw new InvalidOperationException("保存済みの紹介状を選択してください。");
        VerifyReferralPatient(app, patient);
        object? command = null, forms = null, form = null, controls = null;
        try
        {
            command = AccessDispatch.Get(app, "DoCmd");
            if (!IsReferralFormOpen(app))
            {
                string where = $"[紹介番号] = {number.ToString(CultureInfo.InvariantCulture)} AND [カルテ番号] = {letter.ChartNumber.ToString(CultureInfo.InvariantCulture)}";
                AccessDispatch.Call(command, "OpenForm", "紹介状", 0, Type.Missing, where, Type.Missing, 0, Type.Missing);
            }
            // An already-open form is never closed, saved or moved automatically.
            // Open/Current events may also override the requested filter; verify the actual row.
            forms = AccessDispatch.Get(app, "Forms");
            form = AccessDispatch.Get(forms, "Item", "紹介状");
            controls = AccessDispatch.Get(form, "Controls");
            if (Convert.ToBoolean(AccessDispatch.Get(form, "NewRecord")) ||
                ParseChartNumber(ReadControlValue(controls, "紹介番号")) != number ||
                ParseChartNumber(ReadControlValue(controls, "カルテ番号")) != letter.ChartNumber)
                throw new InvalidOperationException("Accessで別の紹介状が開いているか、対象を表示できませんでした。Accessの紹介状フォームを閉じてから、もう一度開いてください。");
            VerifyReferralPatient(app, patient);
            AccessDispatch.Call(command, "SelectObject", 2, "紹介状", false);
            AccessDispatch.Call(command, "Restore");
        }
        finally { Release(controls); Release(form); Release(forms); Release(command); }
    }

    private static bool IsReferralFormOpen(object app)
    {
        object? project = null, forms = null;
        try
        {
            project = AccessDispatch.Get(app, "CurrentProject"); forms = AccessDispatch.Get(project, "AllForms");
            for (int i = 0; i < Convert.ToInt32(AccessDispatch.Get(forms, "Count")); i++)
            {
                object? form = null;
                try
                {
                    form = AccessDispatch.Get(forms, "Item", i);
                    if (Convert.ToString(AccessDispatch.Get(form, "Name")) == "紹介状")
                        return Convert.ToBoolean(AccessDispatch.Get(form, "IsLoaded"));
                }
                finally { Release(form); }
            }
            return false;
        }
        finally { Release(forms); Release(project); }
    }
}
