using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

using DexTor.Application;

namespace DexTor.Launcher;

public static class CreateUsbRecoveryWindow
{
    public static void ShowIfNeeded(IWin32Window owner, ILocalizer localizer, CreatorActionService.CreateUsbResult result)
    {
        if (!IsManifestOrConfigReleaseError(result))
        {
            return;
        }

        var isReleaseError = IsReleaseError(result);
        using var window = new Form
        {
            Text = localizer.T(isReleaseError
                ? "launcher.create.recovery.title_release"
                : "launcher.create.recovery.title"),
            Width = 720,
            Height = 310,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };
        var message = new Label
        {
            Text = localizer.T(isReleaseError
                ? "launcher.create.recovery.description_release"
                : "launcher.create.recovery.description"),
            AutoSize = false,
            Location = new System.Drawing.Point(14, 12),
            Width = 680,
            Height = 90,
            MaximumSize = new System.Drawing.Size(680, 0),
            TextAlign = System.Drawing.ContentAlignment.TopLeft
        };

        var openFolderButton = new Button
        {
            Text = localizer.T(isReleaseError
                ? "launcher.create.recovery.open_release_source_folder"
                : "launcher.create.recovery.open_manifest_folder"),
            Location = new System.Drawing.Point(14, 114),
            Width = 220,
            Height = 32,
            Enabled = false
        };

        var openFolderHint = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Location = new System.Drawing.Point(14, 152),
            Width = 680,
            Height = 48,
            TextAlign = System.Drawing.ContentAlignment.TopLeft
        };

        var closeButton = new Button
        {
            Text = localizer.T("launcher.close"),
            Location = new System.Drawing.Point(588, 200),
            Width = 110,
            Height = 34
        };

        var manifestPath = result.RecoveryPath;
        if (TryGetRecoveryTarget(manifestPath, out var openTarget))
        {
            openFolderButton.Enabled = true;
            openFolderHint.Text = $"{localizer.T("launcher.create.recovery.path")}: {manifestPath}";
            openFolderButton.Click += (_, _) => OpenFileOrFolder(openTarget);
        }
        else if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            openFolderHint.Text = $"{localizer.T("launcher.create.recovery.path")}: {manifestPath}";
        }

        closeButton.Click += (_, _) => window.Close();

        window.Controls.Add(message);
        window.Controls.Add(openFolderButton);
        window.Controls.Add(openFolderHint);
        window.Controls.Add(closeButton);
        LauncherBranding.Attach(window, localizer);

        window.Shown += (_, _) => openFolderButton.Focus();

        window.ShowDialog(owner);
    }

    private static bool IsManifestOrConfigReleaseError(CreatorActionService.CreateUsbResult result)
    {
        if (result.Success || string.IsNullOrWhiteSpace(result.ErrorMessageKey))
        {
            return false;
        }

        return result.ErrorMessageKey.StartsWith("launcher.create.error.release_manifest_", StringComparison.Ordinal)
            || result.ErrorMessageKey == "launcher.create.error.release"
            || result.ErrorMessageKey == "launcher.create.error.release_config";
    }

    private static bool IsReleaseError(CreatorActionService.CreateUsbResult result)
    {
        if (result.Success || string.IsNullOrWhiteSpace(result.ErrorMessageKey))
        {
            return false;
        }

        return result.ErrorMessageKey == "launcher.create.error.release"
            || result.ErrorMessageKey == "launcher.create.error.release_config";
    }

    private static bool TryGetRecoveryTarget(string? recoveryPath, out string target)
    {
        target = null!;

        if (string.IsNullOrWhiteSpace(recoveryPath))
        {
            return false;
        }

        if (Uri.TryCreate(recoveryPath, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                var filePath = uri.LocalPath;
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return false;
                }

                filePath = Uri.UnescapeDataString(filePath);
                if (!string.IsNullOrWhiteSpace(uri.Host)
                    && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    if (uri.IsUnc)
                    {
                        if (filePath.StartsWith("\\\\localhost\\", StringComparison.OrdinalIgnoreCase))
                        {
                            filePath = filePath["\\\\localhost\\".Length..];
                        }
                    }
                    else if (filePath.Length > 1 && filePath[0] == '/')
                    {
                        filePath = filePath[1..];
                    }
                }
                else if (uri.IsUnc)
                {
                    filePath = $"\\\\{uri.Host}{filePath.Replace('/', '\\')}";
                }
                else if (uri.Host is not null && uri.Host.Length == 0 && filePath.Length > 3
                    && filePath[0] == '/' && char.IsLetter(filePath[1]) && filePath[2] == ':')
                {
                    filePath = filePath[1..];
                }
                else if (!uri.IsUnc
                    && filePath.Length > 1
                    && filePath[0] == '/'
                    && filePath[1] == '\\')
                {
                    filePath = filePath[1..];
                }

                if (Directory.Exists(filePath))
                {
                    target = filePath;
                    return true;
                }

                if (File.Exists(filePath))
                {
                    target = Path.GetDirectoryName(filePath) ?? filePath;
                    return true;
                }

                return false;
            }

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                target = recoveryPath;
                return true;
            }

            return false;
        }

        var normalizedPath = Path.GetFullPath(recoveryPath);
        if (Directory.Exists(normalizedPath))
        {
            target = normalizedPath;
            return true;
        }

        if (File.Exists(normalizedPath))
        {
            target = Path.GetDirectoryName(normalizedPath) ?? normalizedPath;
            return true;
        }

        return false;
    }

    private static void OpenFileOrFolder(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
