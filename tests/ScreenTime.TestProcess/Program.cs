namespace ScreenTime.TestProcess;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.Run(new Form { Text = "ScreenTime owned test helper", Width = 300, Height = 100 });
    }
}
