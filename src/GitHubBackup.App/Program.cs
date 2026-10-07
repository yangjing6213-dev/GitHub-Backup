using System.Security.Principal;

namespace GitHubBackup.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (SetupCommand.IsSetupRequest(args))
        {
            Environment.ExitCode = SetupCommand.Execute(args);
            return;
        }
        ApplicationConfiguration.Initialize();
        SingleInstanceCoordinator instance;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            instance = SingleInstanceCoordinator.Start(identity.User?.Value ?? throw new UnauthorizedAccessException());
        }
        catch (Exception) { ShowInstanceFailure(); return; }
        using (instance)
        {
            if (!instance.IsPrimary)
            {
                try { if (!instance.ActivatePrimaryAsync(CancellationToken.None).GetAwaiter().GetResult()) ShowInstanceFailure(); }
                catch (Exception) { ShowInstanceFailure(); }
                return;
            }
            var paths = AppPaths.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var workflow = new DesktopWorkflow(paths);
            using var form = new MainForm(workflow.Actions, workflow.LoadScheduleAsync, workflow.SaveScheduleAsync);
            form.Shown += (_, _) => instance.BindActivation(form.ActivateFromSecondaryInstance);
            Application.Run(form);
        }
    }

    private static void ShowInstanceFailure() => MessageBox.Show("无法安全打开或激活已有窗口。请检查是否已有应用正在运行，稍后重试。",
        "GitHub 备份", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
