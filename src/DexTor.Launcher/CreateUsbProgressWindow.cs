using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

using DexTor.Application;
using DexTor.Creator;

namespace DexTor.Launcher;

internal sealed class CreateUsbProgressWindow : Form
{
    private static readonly CreatorStep[] Flow =
    [
        CreatorStep.PrepareRelease,
        CreatorStep.ConfirmErase,
        CreatorStep.FormatUsb,
        CreatorStep.WriteSystemImage,
        CreatorStep.CreateDataImage,
        CreatorStep.VerifyUsb,
        CreatorStep.ProvisionGuest,
        CreatorStep.Complete
    ];

    private readonly ILocalizer _localizer;
    private readonly string _usbRootPath;
    private readonly bool _continueExisting;
    private readonly Dictionary<CreatorStep, Label> _stepLabels = [];
    private readonly Dictionary<CreatorStep, Label> _statusLabels = [];
    private readonly ProgressBar _progressBar;
    private readonly Label _stepHint;
    private readonly Label _solutionHint;
    private readonly Button _closeButton;
    private readonly TableLayoutPanel _layout = new();
    private const int ContentWidth = 680;
    private static readonly Color WarningColor = Color.FromArgb(0xff, 0xfa, 0x96, 0x1d);
    private static readonly Color WarningTextColor = Color.FromArgb(0xff, 0xff, 0xdb, 0x84);
    private static readonly Color ErrorColor = Color.FromArgb(0xff, 0xee, 0x7f, 0x87);
    private bool _isRunning = true;


    public CreatorActionService.CreateUsbResult? Result { get; private set; }

    private CreateUsbProgressWindow(string usbRootPath, ILocalizer localizer, bool continueExisting)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _usbRootPath = usbRootPath;
        _continueExisting = continueExisting;

        Text = _localizer.T("launcher.action.create.progress.title");
        Width = 760;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = LauncherWindow.Theme.Background;
        ForeColor = LauncherWindow.Theme.TextPrimary;
        DoubleBuffered = true;
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10.2f, FontStyle.Regular, GraphicsUnit.Point);

        Paint += (_, e) => PaintBackground(e.Graphics, ClientRectangle);
        FormClosing += (_, e) =>
        {
            if (_isRunning)
            {
                e.Cancel = true;
            }
        };

        var title = new Label
        {
            Text = localizer.T("launcher.action.create.progress.title"),
            Font = new Font(Font.FontFamily, 15.5f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextPrimary,
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(2, 2, 0, 8),
            MaximumSize = new Size(ContentWidth, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true
        };

        var subtitle = new Label
        {
            Text = _localizer.T("launcher.action.create.progress.subtitle", new Dictionary<string, string>
            {
                ["drive"] = _usbRootPath
            }),
            Font = new Font(Font.FontFamily, 10.3f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextMuted,
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            Padding = new Padding(2, 0, 0, 8),
            Height = 0,
            TextAlign = ContentAlignment.TopLeft,
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true
        };

        var stepsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = false,
            AutoScroll = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 0)
        };
        BuildStepRows(stepsPanel);

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 12,
            Minimum = 0,
            Maximum = Flow.Length,
            Value = 0,
            MarqueeAnimationSpeed = 0,
            Style = ProgressBarStyle.Blocks
        };

        _stepHint = new Label
        {
            Text = string.Empty,
            Font = new Font(Font.FontFamily, 10.2f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextPrimary,
            AutoSize = false,
            Dock = DockStyle.Top,
            MaximumSize = new Size(ContentWidth, 0),
            AutoEllipsis = true,
            Padding = new Padding(0, 8, 0, 0),
            TextAlign = ContentAlignment.TopLeft,
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true
        };

        _solutionHint = new Label
        {
            Text = string.Empty,
            Font = new Font(Font.FontFamily, 9.8f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextMuted,
            AutoSize = false,
            Dock = DockStyle.Top,
            MaximumSize = new Size(ContentWidth, 0),
            AutoEllipsis = true,
            Padding = new Padding(0, 12, 0, 0),
            TextAlign = ContentAlignment.TopLeft,
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true
        };

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent,
            WrapContents = false
        };
        _closeButton = new Button
        {
            Text = localizer.T("launcher.close"),
            Size = new Size(120, 34),
            Enabled = false
        };
        _closeButton.Click += (_, _) => Close();
        footer.Controls.Add(_closeButton);

        _layout.Dock = DockStyle.Fill;
        _layout.ColumnCount = 1;
        _layout.RowCount = 7;
        _layout.Padding = new Padding(16);
        _layout.BackColor = Color.Transparent;
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.Controls.Add(title, 0, 0);
        _layout.Controls.Add(subtitle, 0, 1);
        _layout.Controls.Add(stepsPanel, 0, 2);
        _layout.Controls.Add(_stepHint, 0, 3);
        _layout.Controls.Add(_progressBar, 0, 4);
        _layout.Controls.Add(_solutionHint, 0, 5);
        _layout.Controls.Add(footer, 0, 6);
        Controls.Add(_layout);
        LauncherBranding.Attach(this, localizer);

        Shown += async (_, _) => await StartCreateAsync();
        _stepHint.Text = localizer.T("launcher.create.progress.waiting");
        Resize += (_, _) => UpdateHintLayout();
        UpdateHintLayout();
    }

    public static CreatorActionService.CreateUsbResult Run(string usbRootPath, ILocalizer localizer, bool continueExisting = true)
    {
        using var window = new CreateUsbProgressWindow(usbRootPath, localizer, continueExisting);
        _ = window.ShowDialog();
        return window.Result ?? new CreatorActionService.CreateUsbResult(
            false,
            usbRootPath,
            CreatorActionService.CreateTransaction(),
            "operation-aborted");
    }

    private void BuildStepRows(FlowLayoutPanel host)
    {
        foreach (var step in Flow)
        {
            var status = new Label
            {
                Text = "○",
                Font = new Font(Font.FontFamily, 11f, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = LauncherWindow.Theme.TextMuted,
                Width = 30,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false,
                BackColor = Color.Transparent,
                UseCompatibleTextRendering = true
            };

            var text = new Label
            {
                Text = StepLabel(step),
                Font = new Font(Font.FontFamily, 9.8f, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = LauncherWindow.Theme.TextMuted,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.TopLeft,
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                UseCompatibleTextRendering = true
            };

            var row = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 6)
            };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            row.Controls.Add(status, 0, 0);
            row.Controls.Add(text, 1, 0);

            row.Margin = new Padding(0, 0, 0, 8);
            host.Controls.Add(row);
            _statusLabels[step] = status;
            _stepLabels[step] = text;
        }
    }

    private async Task StartCreateAsync()
    {
        try
        {
            var transaction = _continueExisting
                ? CreatorActionService.TryLoadTransaction(_usbRootPath) ?? CreatorActionService.CreateTransaction()
                : CreatorActionService.CreateTransaction();
            var progress = new Progress<CreatorActionService.CreatorStepProgress>(UpdateStep);
            CreatorActionService.CreateUsbResult result;
            string? errorMessageKey = null;
            if (CreatorActionService.TryGetReleaseConfigFromEnvironment(out var releaseInfo, out errorMessageKey)
                && releaseInfo is not null)
            {
                result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                    _usbRootPath,
                    releaseInfo.Source,
                    releaseInfo.Sha256,
                    releaseInfo.DestinationPath,
                    progress,
                    failStep: null,
                    releaseManifestPath: releaseInfo.ManifestPath,
                    transaction: transaction);
            }
            else if (!string.IsNullOrWhiteSpace(errorMessageKey))
            {
                result = new CreatorActionService.CreateUsbResult(
                    false,
                    _usbRootPath,
                    CreatorActionService.CreateTransaction(),
                    _localizer.T(errorMessageKey),
                    CreatorStep.PrepareRelease,
                    errorMessageKey,
                    null,
                    CreatorActionService.GetReleaseConfigRecoveryPath());
            }
            else
            {
                result = await Task.Run(() => CreatorActionService.RunCreateFlow(
                    _usbRootPath,
                    progress,
                    failStep: null,
                    stepDelayMs: 130,
                    transaction: transaction));
            }

            Result = result;

            if (result.Success)
            {
                UpdateStep(new CreatorActionService.CreatorStepProgress(
                    CreatorStep.Complete,
                    CreatorActionService.CreatorStepStatus.Success));
                RenderFinalMessage(
                    _localizer.T("launcher.action.create.success", new Dictionary<string, string>
                    {
                        ["drive"] = result.UsbRootPath
                    }),
                    MessageType.Success);
            }
            else
            {
                var errorText = result.ErrorMessageKey is null
                    ? result.ErrorMessage ?? _localizer.T("launcher.action.create.error.unknown")
                    : _localizer.T(result.ErrorMessageKey, result.ErrorMessageArgs);

                RenderFinalMessage(
                    _localizer.T(
                        "launcher.action.create.error",
                        new Dictionary<string, string>
                        {
                            ["drive"] = result.UsbRootPath,
                            ["error"] = errorText
                        }),
                    MessageType.Error,
                    result.ErrorMessageKey,
                    result.ErrorSafetyMessageKey);

                CreateUsbRecoveryWindow.ShowIfNeeded(this, _localizer, result);
            }
        }
        catch (Exception exception)
        {
            Result = new CreatorActionService.CreateUsbResult(
                false,
                _usbRootPath,
                CreatorActionService.CreateTransaction(),
                exception.Message);
            RenderFinalMessage(
                _localizer.T(
                    "launcher.action.create.error",
                    new Dictionary<string, string>
                    {
                        ["drive"] = _usbRootPath,
                        ["error"] = exception.Message
                    }),
                MessageType.Error,
                errorMessageKey: "launcher.create.error.unknown",
                errorSafetyMessageKey: "launcher.create.error.safety.unknown");
        }
        finally
        {
            _isRunning = false;
            _closeButton.Enabled = true;
            AcceptButton = _closeButton;
        }
    }

    private void UpdateStep(CreatorActionService.CreatorStepProgress stepProgress)
    {
        var step = stepProgress.Step;
        var index = Array.IndexOf(Flow, step);
        if (index < 0)
        {
            return;
        }

        for (var i = 0; i < Flow.Length; i++)
        {
            var current = Flow[i];
            var isActive = i == index;
            var isDone = i < index;
            var isWarning = isActive && stepProgress.Status == CreatorActionService.CreatorStepStatus.Warning;
            var isError = isActive && stepProgress.Status == CreatorActionService.CreatorStepStatus.Error;
            var isSuccess = isActive && stepProgress.Status == CreatorActionService.CreatorStepStatus.Success;

            if (isDone)
            {
                _statusLabels[current].Text = "✓";
                _statusLabels[current].ForeColor = LauncherWindow.Theme.AccentSoft;
                _stepLabels[current].ForeColor = LauncherWindow.Theme.TextPrimary;
                continue;
            }

            if (isActive)
            {
                if (isWarning)
                {
                    _statusLabels[current].Text = "⚠";
                    _statusLabels[current].ForeColor = WarningColor;
                    _stepLabels[current].ForeColor = WarningTextColor;
                }
                else if (isError)
                {
                    _statusLabels[current].Text = "✖";
                    _statusLabels[current].ForeColor = ErrorColor;
                    _stepLabels[current].ForeColor = ErrorColor;
                }
                else
                {
                    _statusLabels[current].Text = isSuccess ? "✓" : "…";
                    _statusLabels[current].ForeColor = LauncherWindow.Theme.AccentSoft;
                    _stepLabels[current].ForeColor = LauncherWindow.Theme.TextPrimary;
                }

                continue;
            }

            _statusLabels[current].Text = "○";
            _statusLabels[current].ForeColor = LauncherWindow.Theme.TextMuted;
            _stepLabels[current].ForeColor = LauncherWindow.Theme.TextMuted;
        }

        var stepLabel = step == CreatorStep.Complete
            ? _localizer.T("launcher.create.progress.complete")
            : StepLabel(step);
        var currentText = _localizer.T("launcher.create.progress.current", new Dictionary<string, string>
        {
            ["step"] = stepLabel
        });
        var detailsText = stepProgress.Status is CreatorActionService.CreatorStepStatus.Warning or CreatorActionService.CreatorStepStatus.Error
            && !string.IsNullOrWhiteSpace(stepProgress.MessageKey)
            ? _localizer.T(stepProgress.MessageKey, stepProgress.MessageArgs)
            : string.Empty;

        _stepHint.Text = string.IsNullOrWhiteSpace(detailsText)
            ? currentText
            : $"{currentText}{Environment.NewLine}{detailsText}";
        _stepHint.ForeColor = stepProgress.Status == CreatorActionService.CreatorStepStatus.Error
            ? ErrorColor
            : stepProgress.Status == CreatorActionService.CreatorStepStatus.Warning
            ? WarningTextColor
            : LauncherWindow.Theme.TextPrimary;

        _solutionHint.Text = stepProgress.Status is CreatorActionService.CreatorStepStatus.Warning or CreatorActionService.CreatorStepStatus.Error
            ? $"{_localizer.T("launcher.solution.label")} {(
                stepProgress.Status == CreatorActionService.CreatorStepStatus.Warning
                    ? _localizer.T(CreatorActionService.GetCreateWarningSolutionKey(stepProgress.MessageKey))
                    : _localizer.T(CreatorActionService.GetCreateErrorSolutionKey(stepProgress.MessageKey)))}"
            : string.Empty;
        _solutionHint.ForeColor = stepProgress.Status == CreatorActionService.CreatorStepStatus.Error
            ? ErrorColor
            : stepProgress.Status == CreatorActionService.CreatorStepStatus.Warning
            ? WarningTextColor
            : LauncherWindow.Theme.TextMuted;
        _solutionHint.Visible = !string.IsNullOrWhiteSpace(_solutionHint.Text);

        _progressBar.Value = Math.Max(_progressBar.Minimum, Math.Min(index + 1, _progressBar.Maximum));
        UpdateHintLayout();
        UpdateStepLayout();
    }

    private void RenderFinalMessage(
        string message,
        MessageType type,
        string? errorMessageKey = null,
        string? errorSafetyMessageKey = null)
    {
        _stepHint.Text = message;
        _stepHint.ForeColor = type switch
        {
            MessageType.Error => ErrorColor,
            MessageType.Success => Color.FromArgb(0xff, 0xa8, 0xff, 0xc4),
            _ => LauncherWindow.Theme.TextPrimary
        };

        if (type == MessageType.Error)
        {
            var lines = new List<string>
            {
                $"{_localizer.T("launcher.solution.label")} {_localizer.T(CreatorActionService.GetCreateErrorSolutionKey(errorMessageKey))}"
            };

            if (!string.IsNullOrWhiteSpace(errorSafetyMessageKey))
            {
                lines.Add(
                    $"{_localizer.T("launcher.create.error.safety.label")} {_localizer.T(errorSafetyMessageKey)}");
            }

            _solutionHint.Text = string.Join(Environment.NewLine + Environment.NewLine, lines);
        }
        else
        {
            _solutionHint.Text = string.Empty;
        }
        _solutionHint.Visible = !string.IsNullOrWhiteSpace(_solutionHint.Text);

        _solutionHint.ForeColor = type == MessageType.Error
            ? ErrorColor
            : LauncherWindow.Theme.TextMuted;
        UpdateHintLayout();
    }

    private void UpdateStepLayout()
    {
        Invalidate();
    }

    private void UpdateHintLayout()
    {
        if (_layout.IsDisposed)
        {
            return;
        }

        var hintWidth = Math.Max(220, Math.Min(ContentWidth, _layout.ClientSize.Width - _layout.Padding.Horizontal));

        _stepHint.MaximumSize = new Size(hintWidth, 0);
        _solutionHint.MaximumSize = new Size(hintWidth, 0);
        _stepHint.Width = hintWidth;
        _solutionHint.Width = hintWidth;

        if (string.IsNullOrWhiteSpace(_stepHint.Text))
        {
            _stepHint.Height = 0;
            _stepHint.Visible = false;
        }
        else
        {
            var stepSize = _stepHint.GetPreferredSize(new Size(hintWidth, int.MaxValue));
            _stepHint.Height = Math.Min(420, Math.Max(stepSize.Height, 20));
            _stepHint.Visible = true;
        }

        if (string.IsNullOrWhiteSpace(_solutionHint.Text))
        {
            _solutionHint.Height = 0;
            _solutionHint.Visible = false;
        }
        else
        {
            var solutionSize = _solutionHint.GetPreferredSize(new Size(hintWidth, int.MaxValue));
            _solutionHint.Height = Math.Min(420, Math.Max(solutionSize.Height, 20));
            _solutionHint.Visible = true;
        }
    }

    private static void PaintBackground(Graphics graphics, Rectangle bounds)
    {
        using var background = new LinearGradientBrush(
            bounds,
            LauncherWindow.Theme.BackgroundTop,
            LauncherWindow.Theme.BackgroundBottom,
            LinearGradientMode.Vertical);
        graphics.FillRectangle(background, bounds);

        using var leftGlow = new PathGradientBrush(GetSoftGlowPath(new Point(84, 230), 300, 220))
        {
            CenterColor = LauncherWindow.Theme.GlowPrimary,
            SurroundColors = [Color.Transparent]
        };
        graphics.FillEllipse(leftGlow, new Rectangle(-12, 190, 260, 180));

        using var rightGlow = new PathGradientBrush(GetSoftGlowPath(new Point(606, 0), 260, 200))
        {
            CenterColor = LauncherWindow.Theme.GlowSoft,
            SurroundColors = [Color.Transparent]
        };
        graphics.FillEllipse(rightGlow, new Rectangle(560, -130, 280, 210));
    }

    private static System.Drawing.Drawing2D.GraphicsPath GetSoftGlowPath(Point center, int width, int height)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddEllipse(new Rectangle(center.X - width / 2, center.Y - height / 2, width, height));
        return path;
    }

    private string StepLabel(CreatorStep step)
    {
        return _localizer.T(step switch
        {
            CreatorStep.PrepareRelease => "launcher.create.step.prepare_release",
            CreatorStep.ConfirmErase => "launcher.create.step.confirm_erase",
            CreatorStep.FormatUsb => "launcher.create.step.format_usb",
            CreatorStep.WriteSystemImage => "launcher.create.step.write_system_image",
            CreatorStep.CreateDataImage => "launcher.create.step.create_data_image",
            CreatorStep.VerifyUsb => "launcher.create.step.verify_usb",
            CreatorStep.ProvisionGuest => "launcher.create.step.provision_guest",
            CreatorStep.Complete => "launcher.create.step.complete",
            _ => "launcher.action.create.progress.unknown"
        });
    }

    private enum MessageType
    {
        Info,
        Success,
        Error
    }
}
