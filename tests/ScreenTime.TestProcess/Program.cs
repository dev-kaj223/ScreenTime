namespace ScreenTime.TestProcess;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--headless"))
        {
            Application.Run(new ApplicationContext());
            return;
        }
        var form = new Form { Text = "ScreenTime owned test helper", Width = 300, Height = 100 };
        var probe = Array.IndexOf(args, "--input-probe");
        if (probe >= 0)
        {
            var path = args[probe + 1];
            var clicks = 0;
            var keys = 0;
            form.WindowState = FormWindowState.Maximized;
            form.KeyPreview = true;
            void Save() => File.WriteAllText(path, $"{clicks},{keys}"); // counts only, never input content
            form.MouseDown += (_, _) => { clicks++; Save(); };
            form.KeyDown += (_, _) => { keys++; Save(); };
            form.Shown += (_, _) => Save();
        }
        Application.Run(form);
    }
}
