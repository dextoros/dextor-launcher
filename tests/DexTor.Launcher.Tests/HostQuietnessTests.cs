using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

using FluentAssertions;
using Xunit;

using DexTor.Application;

namespace DexTor.Launcher.Tests;

public sealed class HostQuietnessTests
{
    [DllImport("ole32.dll")]
#pragma warning disable SYSLIB1054 // the classic ComTypes interfaces are what the ROT hands back
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll")]
    private static extern int CreateClassMoniker(ref Guid classId, out IMoniker moniker);
#pragma warning restore SYSLIB1054

    private static object? FindCanceller()
    {
        GetRunningObjectTable(0, out var table).Should().Be(0);
        var classId = new Guid("331F1768-05A9-4ddd-B86E-DAE34DDC998A");
        CreateClassMoniker(ref classId, out var moniker).Should().Be(0);
        return table.IsRunning(moniker) == 0 && table.GetObject(moniker, out var found) == 0 ? found : null;
    }

    [Fact]
    public void WhileCreatingAStickAutoPlayIsDeclinedWhateverWindowIsInFront()
    {
        using (var suppressor = AutoPlaySuppressor.TryStart())
        {
            suppressor.Should().NotBeNull("the Running Object Table takes the registration");
            var canceller = FindCanceller().Should().BeAssignableTo<AutoPlaySuppressor.IQueryCancelAutoPlay>().Subject;
            canceller.AllowAutoPlay(@"F:\", 0, "", 0).Should().Be(1, "S_FALSE cancels AutoPlay for the event");
        }

        FindCanceller().Should().BeNull("AutoPlay is back to normal once creation is over");
    }

    [Theory]
    [InlineData(@"F:\", "F:", true)]
    [InlineData(@"F:\DEXTOR\system", "F:", true)]
    [InlineData(@"F:", "F:", true)]
    [InlineData(@"FF:\", "F:", false)]
    [InlineData(@"C:\Users", "F:", false)]
    [InlineData(null, "F:", false)]
    public void OnlyExplorerWindowsOnTheStickAreClosed(string? location, string drive, bool expected)
    {
        ExplorerWindows.IsOnDrive(location, drive).Should().Be(expected);
    }

    [Fact]
    public void ALockedLanguageSelectorDimsInTheWizardsColoursInsteadOfFlashingGrey()
    {
        using var selector = new LauncherBranding.ThemedLanguageSelector(
            [new LauncherBranding.LanguageOption("en", "English"), new LauncherBranding.LanguageOption("ru", "Русский")], "ru");
        selector.CreateControl();
        using var enabled = Render(selector);
        selector.Enabled = false;
        using var disabled = Render(selector);

        // Sampled away from the text and the border.
        disabled.GetPixel(disabled.Width / 2 + 30, disabled.Height / 2).ToArgb().Should().Be(WizardDesign.Row.ToArgb());
        enabled.GetPixel(enabled.Width / 2 + 30, enabled.Height / 2).ToArgb().Should().Be(WizardDesign.Secondary.ToArgb());
    }

    private static Bitmap Render(Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        return bitmap;
    }

    [Fact]
    public void WhileDexTorOsRunsTheLauncherWaitsInTheNotificationArea()
    {
        using var window = new LauncherWizardWindow(new Localizer("en"), LauncherWizardWindowTests.MinimalDependencies());
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-32000, -32000);
        window.Show();
        var move = typeof(LauncherWizardWindow).GetMethod("MoveToTray", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var restore = typeof(LauncherWizardWindow).GetMethod("RestoreFromTray", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var trayField = typeof(LauncherWizardWindow).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)!;

        move.Invoke(window, null);

        window.Visible.Should().BeFalse();
        var tray = trayField.GetValue(window).Should().BeOfType<NotifyIcon>().Subject;
        tray.Visible.Should().BeTrue();
        tray.Text.Should().Be("DexTor OS is running");
        tray.ContextMenuStrip!.Items.Cast<ToolStripItem>().Select(item => item.Text)
            .Should().Equal("Open DexTor launcher", "Shut down DexTor OS");

        restore.Invoke(window, null);

        window.Visible.Should().BeTrue();
        trayField.GetValue(window).Should().BeNull();
        window.Hide();
    }

    [Fact]
    public void WhileDexTorOsRunsMinimizingOrClosingGoesBackToTheTrayEveryTime()
    {
        using var window = new LauncherWizardWindow(new Localizer("en"), LauncherWizardWindowTests.MinimalDependencies());
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-32000, -32000);
        window.Show();
        var trayField = typeof(LauncherWizardWindow).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var restore = typeof(LauncherWizardWindow).GetMethod("RestoreFromTray", BindingFlags.Instance | BindingFlags.NonPublic)!;
        typeof(LauncherWizardWindow).GetField("_vmRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);

        for (var round = 0; round < 2; round++)
        {
            window.WindowState = FormWindowState.Minimized;
            window.Visible.Should().BeFalse($"minimizing goes to the tray (round {round + 1})");
            trayField.GetValue(window).Should().NotBeNull();
            restore.Invoke(window, null);
            window.Visible.Should().BeTrue();
            window.WindowState.Should().Be(FormWindowState.Normal);
        }

        window.Close();
        window.IsDisposed.Should().BeFalse("the launcher owns the running session");
        window.Visible.Should().BeFalse("closing while DexTor OS runs goes to the tray");
        trayField.GetValue(window).Should().NotBeNull();

        restore.Invoke(window, null);
        typeof(LauncherWizardWindow).GetField("_vmRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
        window.Hide();
    }
}
