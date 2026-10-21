using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using DexTor.Application;

namespace DexTor.Launcher;

internal sealed class UsbSelectionWindow : Form
{
    private readonly ILocalizer _localizer;
    private readonly ListBox _list = new();
    public string? SelectedUsbRootPath { get; private set; }

    public UsbSelectionWindow(IReadOnlyList<string> candidates, ILocalizer localizer)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));

        Text = _localizer.T("launcher.usb_selection.title");
        Width = 540;
        Height = 300;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = _localizer.T("launcher.usb_selection.subtitle"),
            ForeColor = Color.WhiteSmoke,
            AutoSize = false,
            Height = 40,
            Dock = DockStyle.Top,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f, FontStyle.Regular, GraphicsUnit.Point)
        };

        _list.Dock = DockStyle.Fill;
        _list.SelectionMode = SelectionMode.One;
        _list.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        foreach (var item in candidates.Select(CleanDisplayName))
        {
            _list.Items.Add(item);
        }

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft
        };

        var continueButton = new Button
        {
            Text = _localizer.T("launcher.usb_selection.continue"),
            DialogResult = DialogResult.OK,
            Width = 120,
            Height = 32
        };
        continueButton.Click += (_, _) => OnContinue();

        var cancelButton = new Button
        {
            Text = _localizer.T("launcher.close"),
            DialogResult = DialogResult.Cancel,
            Width = 120,
            Height = 32
        };

        footer.Controls.Add(continueButton);
        footer.Controls.Add(cancelButton);

        root.Controls.Add(label, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        LauncherBranding.Attach(this, localizer);
        AcceptButton = continueButton;
        CancelButton = cancelButton;
    }

    private static string CleanDisplayName(string rootPath)
    {
        var label = rootPath.TrimEnd('\\');
        return string.IsNullOrWhiteSpace(label) ? rootPath : label;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
    }

    private void OnContinue()
    {
        if (_list.SelectedItem is string selectedText)
        {
            SelectedUsbRootPath = selectedText.Trim();
            return;
        }

        DialogResult = DialogResult.Cancel;
        MessageBox.Show(
            _localizer.T("launcher.usb_selection.empty"),
            Text,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        DialogResult = DialogResult.None;
    }
}
