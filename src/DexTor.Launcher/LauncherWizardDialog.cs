using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using DexTor.Application;

using static DexTor.Launcher.WizardDesign;

namespace DexTor.Launcher;

internal sealed partial class LauncherWizardWindow
{
    /// <summary>How a question looks: erasing a drive, or stopping a write.</summary>
    internal enum ConfirmationStyle
    {
        /// <summary>Orange icon, a close button, the facts and the warning; "Cancel" and a red "Erase".</summary>
        Erase,

        /// <summary>Red icon and a plain explanation; a red-outlined "Stop" and a green "Keep going".</summary>
        Abort
    }

    /// <summary>
    /// A question asked in the wizard's own visual language: the design's dialog card on the
    /// wizard, which is shaded - title bar included - while it is open.
    /// </summary>
    /// <remarks>
    /// The text is the same localized text it always was; it is split into a heading, the
    /// device facts and the consequence, so the one sentence that matters is not lost in a
    /// paragraph. Shown over a visible wizard the dialog covers the wizard's whole window: it
    /// paints what was there, darkened by the design's overlay, and its card on top - so the
    /// card has soft corners and a shadow, which a window of its own could not have.
    /// </remarks>
    internal sealed class ConfirmationDialog : Form, IGlowHost
    {
        private const float CardWidth = 560;
        private readonly ConfirmationStyle _style;
        private readonly bool _destructive;
        private Bitmap? _backdrop;
        private Rectangle _card;

        internal ConfirmationDialog(
            ILocalizer localizer,
            string message,
            string confirmText,
            string cancelText,
            bool destructive,
            ConfirmationStyle style = ConfirmationStyle.Erase,
            string? acknowledgement = null)
        {
            ArgumentNullException.ThrowIfNull(localizer);
            _style = style;
            _destructive = destructive;
            var (heading, facts, consequence) = SplitMessage(message);
            Text = localizer.T("launcher.wizard.window_title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = WizardDesign.Card;
            ForeColor = WizardDesign.Text;
            Font = WizardFonts.Get(14, 400);
            DoubleBuffered = true;
            KeyPreview = true;

            var width = S(CardWidth);
            var inner = width - S(24) * 2;
            var y = S(24);
            Heading = new WizardText(20, 600, 24, WizardDesign.Text)
            {
                Name = "ConfirmationHeading", Text = heading, WrapWidth = inner - S(40 + 16) - (style == ConfirmationStyle.Erase ? S(40) : 0)
            };
            Heading.Location = new Point(S(24 + 40 + 16), y + Math.Max(0, (S(40) - Heading.Height) / 2));
            Controls.Add(Heading);
            if (style == ConfirmationStyle.Erase)
            {
                var close = new CloseButton { Name = "ConfirmationClose", Location = new Point(width - S(16 + 32), S(16)) };
                close.Click += (_, _) => { DialogResult = DialogResult.No; Close(); };
                Controls.Add(close);
            }

            y += Math.Max(S(40), Heading.Height) + S(style == ConfirmationStyle.Erase && facts.Count > 0 ? 24 : 16);
            if (facts.Count > 0)
            {
                var box = new FactsBox(inner, facts) { Name = "ConfirmationFacts", Location = new Point(S(24), y) };
                Controls.Add(box);
                y += box.Height + S(16);
            }

            if (!string.IsNullOrWhiteSpace(consequence))
            {
                if (style == ConfirmationStyle.Erase)
                {
                    var notice = new GlassNotice(inner)
                    {
                        Name = "ConfirmationConsequence", Kind = Tone.Warning, Icon = WizardIcon.None, VerticalPadding = 16, Text = consequence,
                        Location = new Point(S(24), y)
                    };
                    notice.Height = notice.GetPreferredSize(Size.Empty).Height;
                    Controls.Add(notice);
                    y += notice.Height;
                }
                else
                {
                    var text = new WizardText(14, 400, 22, WizardDesign.TextSecondary)
                    {
                        Name = "ConfirmationConsequence", Text = consequence, WrapWidth = inner, Location = new Point(S(24), y)
                    };
                    Controls.Add(text);
                    y += text.Height;
                }
            }

            GlassButton confirm;
            GlassButton cancel;
            if (style == ConfirmationStyle.Abort)
            {
                confirm = CreateButton(confirmText, ButtonKind.DangerOutline, 120);
                cancel = CreateButton(cancelText, ButtonKind.Go, 200);
            }
            else
            {
                confirm = CreateButton(confirmText, destructive ? ButtonKind.Danger : ButtonKind.Go, 200);
                confirm.Trailing = "→";
                confirm.FitWidth();
                cancel = CreateButton(cancelText, ButtonKind.Secondary, 120);
            }

            confirm.Name = "ConfirmationAccept";
            confirm.DialogResult = DialogResult.Yes;
            cancel.Name = "ConfirmationCancel";
            cancel.DialogResult = DialogResult.No;

            if (!string.IsNullOrWhiteSpace(acknowledgement))
            {
                var check = new WizardCheckBox { Name = "ConfirmationAcknowledge", Text = acknowledgement, Location = new Point(S(24), y + S(20)) };
                check.Size = check.GetPreferredSize(Size.Empty);
                check.CheckedChanged += (_, _) => confirm.Enabled = check.Checked;
                confirm.Enabled = false;
                Controls.Add(check);
                y += S(20) + check.Height;
            }

            y += S(24);
            // The safe answer and the risky one keep their places: in the erase question the
            // red action is on the right, in "stop writing?" the green one - keep writing - is.
            var right = width - S(24);
            var (first, second) = style == ConfirmationStyle.Abort ? (confirm, cancel) : (cancel, confirm);
            second.Location = new Point(right - second.Width, y);
            first.Location = new Point(second.Left - S(12) - first.Width, y);
            Controls.Add(first);
            Controls.Add(second);
            y += S(40) + S(24);

            // A destructive question defaults to the safe answer: Enter and Esc both cancel.
            AcceptButton = destructive ? cancel : confirm;
            CancelButton = cancel;
            ActiveControl = destructive ? cancel : confirm;
            _card = new Rectangle(0, 0, width, y);
            ClientSize = _card.Size;
        }

        internal WizardText Heading { get; }

        /// <summary>
        /// Covers <paramref name="owner"/>'s window: what it shows now, shaded, with the card in
        /// the middle of the window.
        /// </summary>
        internal void CoverWindow(Form owner)
        {
            var frame = VisibleFrame(owner);
            // Off every screen there is nothing to copy; the wizard then draws its own client area.
            var visible = Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(frame));
            _backdrop = (visible ? CaptureScreen(frame) : null) ?? CaptureClient(owner, frame);
            var offset = new Point((frame.Width - _card.Width) / 2, (frame.Height - _card.Height) / 2);
            foreach (Control control in Controls) control.Location = new Point(control.Left + offset.X, control.Top + offset.Y);
            _card.Location = offset;
            StartPosition = FormStartPosition.Manual;
            Bounds = frame;
        }

        private static Rectangle VisibleFrame(Form owner)
        {
            if (owner.IsHandleCreated && DwmGetWindowAttribute(owner.Handle, 9, out var rect, Marshal.SizeOf<NativeRect>()) == 0)
            {
                return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            }

            return owner.Bounds;
        }

        private static Bitmap? CaptureScreen(Rectangle frame)
        {
            try
            {
                var bitmap = new Bitmap(frame.Width, frame.Height);
                using var g = Graphics.FromImage(bitmap);
                g.CopyFromScreen(frame.Location, Point.Empty, frame.Size);
                return bitmap;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
            {
                return null;
            }
        }

        /// <summary>Off screen there is nothing to copy; the wizard draws its client area instead.</summary>
        private static Bitmap CaptureClient(Form owner, Rectangle frame)
        {
            var bitmap = new Bitmap(frame.Width, frame.Height);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(WizardDesign.Window);
            try
            {
                using var client = new Bitmap(owner.ClientSize.Width, owner.ClientSize.Height);
                owner.DrawToBitmap(client, new Rectangle(Point.Empty, owner.ClientSize));
                var origin = owner.PointToScreen(Point.Empty);
                g.DrawImageUnscaled(client, origin.X - frame.X, origin.Y - frame.Y);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // A plain backdrop is enough.
            }

            return bitmap;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // The same rounded corners as the wizard's window it lies on.
            var rounded = _backdrop is null ? 1 : 2;
            _ = DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
            if (_backdrop is null)
            {
                using var outline = Rounded(new RectangleF(0, 0, Width, Height), Sf(12));
                Region = new Region(outline);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_backdrop is not null)
            {
                g.DrawImageUnscaled(_backdrop, 0, 0);
                using (var shade = new SolidBrush(Color.FromArgb((int)(OverlayOpacity * 255), Overlay))) g.FillRectangle(shade, ClientRectangle);
                // 0 24px 64px rgba(0,0,0,0.5): a wide, soft shadow below the card.
                var shadow = new RectangleF(_card.X, _card.Y + Sf(24), _card.Width, _card.Height);
                PaintOuterGlow(g, RectangleF.Inflate(shadow, -Sf(20), -Sf(20)), 12, Color.Black, 64, 0.5f);
            }

            PaintSurface(g, _card, 12, WizardDesign.Card, WizardDesign.CardBorder);
            var tile = new RectangleF(_card.X + Sf(24), _card.Y + Sf(24), Sf(40), Sf(40));
            if (_style == ConfirmationStyle.Abort || !_destructive)
            {
                PaintLitTile(g, tile, WizardDesign.ErrorTop, WizardDesign.ErrorBottom, WizardDesign.Error);
                DrawIcon(g, WizardIcon.Warning, tile, Color.White, 16);
            }
            else
            {
                PaintLitTile(g, tile, Color.FromArgb(0xFF, 0xB4, 0x5C), Color.FromArgb(0xE8, 0x82, 0x1E), WizardDesign.Warning);
                DrawIcon(g, WizardIcon.Warning, tile, Color.FromArgb(0x2A, 0x16, 0x05), 16);
            }

            PaintGoGlows(this, g);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _backdrop?.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>
        /// Splits "Heading\n\nName: value\n...\n\nConsequence" into its three parts.
        /// </summary>
        internal static (string Heading, IReadOnlyList<(string Name, string Value)> Facts, string Consequence) SplitMessage(string message)
        {
            var blocks = (message ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (blocks.Length == 0) return (string.Empty, [], string.Empty);
            var heading = blocks[0];
            var facts = new List<(string, string)>();
            var rest = new List<string>();
            foreach (var block in blocks.Skip(1))
            {
                var lines = block.Split('\n', StringSplitOptions.TrimEntries);
                var pairs = lines.Select(line => line.Split(':', 2)).ToArray();
                if (facts.Count == 0 && pairs.All(pair => pair.Length == 2 && pair[0].Length is > 0 and <= 40))
                {
                    facts.AddRange(pairs.Select(pair => (pair[0].Trim(), pair[1].Trim())));
                }
                else
                {
                    rest.Add(block);
                }
            }

            return (heading, facts, string.Join(Environment.NewLine + Environment.NewLine, rest));
        }

        /// <summary>The device facts: a row-coloured box, names in grey, values in white.</summary>
        private sealed class FactsBox : Control
        {
            private readonly IReadOnlyList<(string Name, string Value)> _facts;

            public FactsBox(int width, IReadOnlyList<(string Name, string Value)> facts)
            {
                _facts = facts;
                SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
                BackColor = Color.Transparent;
                Size = new Size(width, S(12) * 2 + facts.Count * S(28));
                Text = string.Join(Environment.NewLine, facts.Select(fact => $"{fact.Name}: {fact.Value}"));
                TabStop = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                PaintGlassParent(this, e, InvokePaintBackground);
                var g = e.Graphics;
                PaintSurface(g, ClientRectangle, 10, WizardDesign.Row, WizardDesign.RowBorder);
                using var name = WizardFonts.Get(14, 400);
                using var value = WizardFonts.Get(14, 600);
                const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                    | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
                for (var i = 0; i < _facts.Count; i++)
                {
                    var row = new Rectangle(S(20), S(12) + i * S(28), Width - S(40), S(28));
                    DesignText.DrawText(g, _facts[i].Name, name, row with { Width = S(170) }, WizardDesign.TextSecondary, flags);
                    DesignText.DrawText(g, _facts[i].Value, value, row with { X = row.X + S(176), Width = row.Width - S(176) }, WizardDesign.Text, flags);
                }
            }
        }

        /// <summary>The dialog's close button: 32 x 32, r8, a thin cross.</summary>
        private sealed class CloseButton : Button
        {
            private bool _hover;

            public CloseButton()
            {
                SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
                BackColor = Color.Transparent;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                Size = S(32, 32);
                TabStop = false;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                PaintGlassParent(this, e, InvokePaintBackground);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (_hover)
                {
                    using var path = Rounded(new RectangleF(0, 0, Width, Height), Sf(8));
                    using var face = new SolidBrush(WizardDesign.SecondaryHover);
                    g.FillPath(face, path);
                }

                var c = Width / 2f;
                var arm = Sf(6.5f);
                using var pen = new Pen(_hover ? WizardDesign.Text : WizardDesign.TextSecondary, Sf(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, c - arm, c - arm, c + arm, c + arm);
                g.DrawLine(pen, c + arm, c - arm, c - arm, c + arm);
            }
        }
    }

    /// <summary>
    /// The confirmation checkbox of the design: 18 x 18, r4, a grey 1.5 px edge; ticked it
    /// fills with lit green and a white tick. Only then may the risky action go ahead.
    /// </summary>
    private sealed class WizardCheckBox : CheckBox
    {
        private bool _hover;

        public WizardCheckBox()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            Font = WizardFonts.Get(14, 500);
            Cursor = Cursors.Hand;
            AutoSize = false;
        }

        public override Size GetPreferredSize(Size proposedSize) =>
            new(S(18 + 12) + DesignText.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 1, S(20));

        protected override void OnMouseEnter(EventArgs eventargs) { _hover = true; Invalidate(); base.OnMouseEnter(eventargs); }
        protected override void OnMouseLeave(EventArgs eventargs) { _hover = false; Invalidate(); base.OnMouseLeave(eventargs); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            PaintGlassParent(this, pevent, InvokePaintBackground);
            var g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new RectangleF(Sf(0.75f), (Height - Sf(18)) / 2f + Sf(0.75f), Sf(16.5f), Sf(16.5f));
            if (!Enabled)
            {
                using var path = Rounded(box, Sf(4));
                using var pen = new Pen(WizardDesign.CardBorder, Sf(1.5f));
                g.DrawPath(pen, path);
            }
            else if (Checked)
            {
                PaintOuterGlow(g, box, 4, WizardDesign.Green, 10, 0.45f);
                using var path = Rounded(RectangleF.Inflate(box, Sf(0.75f), Sf(0.75f)), Sf(4));
                FillLit(g, path, new PointF(box.Left + box.Width * 0.3f, box.Top + box.Height * 0.25f), WizardDesign.GreenTop, WizardDesign.GreenBottom, 0f);
                DrawTick(g, Color.White, box.Left + box.Width / 2, box.Top + box.Height / 2, Sf(18) / 20f * 0.85f, 2.2f);
            }
            else
            {
                using var path = Rounded(box, Sf(4));
                using var pen = new Pen(_hover ? WizardDesign.TextSecondary : WizardDesign.FutureStage, Sf(1.5f));
                g.DrawPath(pen, path);
            }

            DesignText.DrawText(g, Text, Font, new Rectangle(S(30), 0, Width - S(30), Height), Enabled ? WizardDesign.Text : WizardDesign.TextDisabled,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    /// <summary>Asks a question in the wizard's style; true when the operator confirmed.</summary>
    internal static bool AskConfirmation(
        IWin32Window? owner,
        ILocalizer localizer,
        string message,
        string confirmText,
        string cancelText,
        bool destructive,
        ConfirmationStyle style = ConfirmationStyle.Erase,
        string? acknowledgement = null)
    {
        using var dialog = new ConfirmationDialog(localizer, message, confirmText, cancelText, destructive, style, acknowledgement);
        if (owner is not Form { Visible: true, WindowState: not FormWindowState.Minimized } wizard)
        {
            if (owner is null) dialog.StartPosition = FormStartPosition.CenterScreen;
            return dialog.ShowDialog(owner) == DialogResult.Yes;
        }

        dialog.CoverWindow(wizard);
        return dialog.ShowDialog(wizard) == DialogResult.Yes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
}
