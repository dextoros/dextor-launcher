using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

using DexTor.Application;

namespace DexTor.Launcher;

internal static class LauncherBranding
{
    internal const string SiteUrl = "https://dextoros.com/";

    internal static Control Attach(
        Form window,
        ILocalizer localizer,
        Color? background = null,
        Action<string>? languageChanged = null)
    {
        var footerBackground = background ?? LauncherWindow.Theme.Background;
        var footer = new TableLayoutPanel
        {
            Name = "DexTorBrandFooter",
            Dock = DockStyle.Bottom,
            Height = 30,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(18, 4, 18, 3),
            Margin = Padding.Empty,
            BackColor = footerBackground
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var copyright = new Label
        {
            Name = "DexTorBrandCopyright",
            Text = FooterText(localizer),
            AutoSize = true,
            ForeColor = LauncherWindow.Theme.TextMuted,
            BackColor = footerBackground,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 8.5f),
            Anchor = AnchorStyles.Left
        };
        var language = CreateLanguageSelector(localizer, languageChanged);
        language.Margin = new Padding(0, 0, 18, 0);
        language.Anchor = AnchorStyles.Right;
        var site = new LinkLabel
        {
            Name = "DexTorBrandSiteLink",
            Text = "dextoros.com",
            Tag = SiteUrl,
            AutoSize = true,
            LinkColor = LauncherWindow.Theme.AccentSoft,
            ActiveLinkColor = LauncherWindow.Theme.TextPrimary,
            VisitedLinkColor = LauncherWindow.Theme.AccentSoft,
            BackColor = footerBackground,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 8.5f),
            Anchor = AnchorStyles.Right
        };
        site.LinkClicked += (_, _) => OpenSite();

        footer.Controls.Add(copyright, 0, 0);
        footer.Controls.Add(language, 2, 0);
        footer.Controls.Add(site, 3, 0);
        window.Controls.Add(footer);
        // WinForms lays out docked controls in reverse z-order. Keeping the footer
        // behind the Fill control makes DockStyle.Bottom reserve its own space.
        footer.SendToBack();
        return footer;
    }

    private static readonly LanguageOption[] Languages =
    [
        new("en", "English"),
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("pt-BR", "Português (Brasil)"),
        new("pl", "Polski"),
        new("tr", "Türkçe"),
        new("zh-CN", "简体中文")
    ];

    /// <summary>The language list, set to the current language, reporting a choice to <paramref name="languageChanged"/>.</summary>
    internal static ThemedLanguageSelector CreateLanguageSelector(ILocalizer localizer, Action<string>? languageChanged)
    {
        localizer.TryGetLanguage(out var currentLanguage);
        var language = new ThemedLanguageSelector(Languages, currentLanguage) { Name = "DexTorLanguageSelector" };
        language.LanguageChanged += code => languageChanged?.Invoke(code);
        return language;
    }

    internal static void OpenSite() => Process.Start(new ProcessStartInfo(SiteUrl) { UseShellExecute = true });

    /// <summary>
    /// The version of the running launcher, as it was built: "0.1.16". Every portable build
    /// used to be called the same and carry 0.1.0, so an old copy could not be told from a
    /// new one - on screen, in a journal, or in a support conversation.
    /// </summary>
    internal static string DisplayVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(LauncherBranding).Assembly;
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational;
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata >= 0 ? version[..metadata] : version;
    }

    internal static string FooterText(ILocalizer localizer) => $"{localizer.T("launcher.brand.footer")}  ·  v{DisplayVersion}";

    internal sealed class ThemedLanguageSelector : Button
    {
        private readonly LanguageOption[] _languages;
        private readonly ContextMenuStrip _menu;

        internal event Action<string>? LanguageChanged;
        internal int LanguageCount => _languages.Length;

        /// <summary>The list opens above the selector, towards the right.</summary>
        internal static ToolStripDropDownDirection MenuDirection => ToolStripDropDownDirection.AboveRight;

        internal ThemedLanguageSelector(LanguageOption[] languages, string currentLanguage)
        {
            _languages = languages;
            Width = 170;
            Height = 25;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 1;
            FlatAppearance.BorderColor = LauncherWindow.Theme.AccentSoft;
            FlatAppearance.MouseOverBackColor = LauncherWindow.Theme.Surface;
            FlatAppearance.MouseDownBackColor = LauncherWindow.Theme.Accent;
            BackColor = LauncherWindow.Theme.SurfaceLight;
            ForeColor = LauncherWindow.Theme.TextPrimary;
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 8.5f);
            TextAlign = ContentAlignment.MiddleLeft;
            Padding = new Padding(9, 0, 5, 0);
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            _menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                BackColor = WizardDesign.Row,
                ForeColor = LauncherWindow.Theme.TextPrimary,
                Renderer = new ToolStripProfessionalRenderer(new MenuColors())
            };
            foreach (var language in languages)
            {
                var item = new ToolStripMenuItem(language.Label) { Tag = language.Code, AutoSize = false, Width = Width - 2 };
                item.Click += (_, _) => SelectLanguage((string)item.Tag!);
                _menu.Items.Add(item);
            }

            // Upwards: the selector sits in the footer, and a list opening downwards ran into
            // the bottom edge of the window.
            Click += (_, _) => _menu.Show(this, new Point(0, 0), MenuDirection);
            SelectLanguage(currentLanguage, notify: false);
        }

        internal void SelectLanguage(string code, bool notify = true)
        {
            var selected = _languages.FirstOrDefault(item => item.Code == code) ?? _languages[0];
            Text = $"{selected.Label}     ▾";
            foreach (ToolStripMenuItem item in _menu.Items)
            {
                item.Checked = string.Equals(item.Tag as string, selected.Code, StringComparison.Ordinal);
            }
            if (notify) LanguageChanged?.Invoke(selected.Code);
        }

        private bool _hover;
        private bool _pressed;

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs mevent) { _pressed = true; Invalidate(); base.OnMouseDown(mevent); }

        protected override void OnMouseUp(MouseEventArgs mevent) { _pressed = false; Invalidate(); base.OnMouseUp(mevent); }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        /// <summary>
        /// Painted here in both states. A disabled flat button is drawn by Windows in its
        /// own greys, which flashed a light box into the dark footer whenever the wizard
        /// was busy; now a locked selector just dims, in the wizard's colours.
        /// </summary>
        /// <summary>
        /// Nothing: <see cref="OnPaint"/> draws all of it. The button's own background is its
        /// square BackColor, which showed as a light corner outside the rounded box.
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            // What is really under the box shows in its rounded corners. A flat fill in the
            // parent's colour - or the window's, under a transparent footer - left the
            // corners a shade off the surface around them.
            LauncherWizardWindow.PaintGlassParent(this, pevent, InvokePaintBackground);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // The design's language list: the secondary button's surface, the name 16 px in,
            // a 12 px chevron 16 px from the right; locked, it takes the plain row colours.
            var bounds = new RectangleF(0, 0, Width, Height);
            if (!Enabled)
            {
                WizardDesign.PaintDisabledButton(g, bounds);
            }
            else
            {
                WizardDesign.PaintSecondaryButton(g, bounds, _pressed ? WizardDesign.ButtonLook.Pressed : _hover ? WizardDesign.ButtonLook.Hover : WizardDesign.ButtonLook.Normal);
            }

            var textColor = Enabled ? WizardDesign.Text : WizardDesign.TextDisabled;
            var left = WizardDesign.S(16);
            DesignText.DrawText(g, Label, Font, new Rectangle(left, 0, Width - left - WizardDesign.S(40), Height), textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            LauncherWizardWindow.DrawIcon(g, WizardIcon.ChevronDown, new RectangleF(Width - WizardDesign.Sf(16 + 12), 0, WizardDesign.Sf(12), Height),
                Enabled ? WizardDesign.TextSecondary : WizardDesign.TextDisabled, 10);
            if (Focused && ShowFocusCues && Enabled)
            {
                using var focus = new Pen(Color.FromArgb(120, WizardDesign.Text)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                using var inset = WizardDesign.Rounded(new RectangleF(3, 3, Width - 7, Height - 7), 6);
                g.DrawPath(focus, inset);
            }
        }

        private string Label => Text.Replace("▾", string.Empty, StringComparison.Ordinal).Trim();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _menu.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => WizardDesign.Row;
        public override Color MenuItemSelected => WizardDesign.Tile;
        public override Color MenuItemBorder => WizardDesign.RowBorder;
        public override Color ImageMarginGradientBegin => WizardDesign.Row;
        public override Color ImageMarginGradientMiddle => WizardDesign.Row;
        public override Color ImageMarginGradientEnd => WizardDesign.Row;
    }

    internal sealed record LanguageOption(string Code, string Label)
    {
        public override string ToString() => Label;
    }
}
