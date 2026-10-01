namespace FyDisplayAgent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var configPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FyDisplayRemote",
            "config.json");

        var store = new ConfigStore(configPath);
        var (config, created) = store.LoadOrCreate();
        using var service = new AgentService(config, store);

        try
        {
            service.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"FyDisplay Remote could not start the local API on port {config.Port}.\n\n{ex.Message}",
                "FyDisplay Remote",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        using var form = new MainForm(service, store, created);
        Application.Run(form);
    }
}
