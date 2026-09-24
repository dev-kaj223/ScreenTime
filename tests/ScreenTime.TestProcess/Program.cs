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
        Application.Run(new Form { Text = "ScreenTime owned test helper", Width = 300, Height = 100 });
    }
}
