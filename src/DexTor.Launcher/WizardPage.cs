using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using static DexTor.Launcher.WizardDesign;

namespace DexTor.Launcher;

internal sealed partial class LauncherWizardWindow
{
    /// <summary>
    /// A wizard page. When what it holds is taller than the window it scrolls - by the wheel,
    /// or by the slim thumb in the margin beside it - but never with the system scroll bars:
    /// those took their width out of the page, so the rows no longer fitted, a second,
    /// horizontal bar appeared and the whole page shifted.
    /// </summary>
    /// <remarks>
    /// <see cref="Scrollable"/> says whether the page may scroll. <see cref="ScrollableControl.AutoScroll"/>
    /// stays off - the control reads it to decide on the system bars - and the page moves its
    /// content by shifting <see cref="DisplayRectangle"/>, which the flow layout places the
    /// controls in.
    /// </remarks>
    internal sealed class WizardPage : FlowLayoutPanel
    {
        private bool _scrollable;
        private int _offset;
        private int _extent;

        public WizardPage()
        {
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            BackColor = Color.Transparent;
            Padding = Padding.Empty;
            Margin = Padding.Empty;
            Thumb = new ScrollThumb(this);
        }

        /// <summary>The scroll thumb; the window puts it in the margin right of the page.</summary>
        internal ScrollThumb Thumb { get; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Scrollable
        {
            get => _scrollable;
            set
            {
                if (_scrollable == value) return;
                _scrollable = value;
                if (!value) _offset = 0;
                PerformLayout();
            }
        }

        public override Rectangle DisplayRectangle
        {
            get
            {
                var display = base.DisplayRectangle;
                display.Y -= _offset;
                return display;
            }
        }

        /// <summary>How far down the page is scrolled, in device pixels.</summary>
        internal int Offset => _offset;

        /// <summary>The height of everything on the page.</summary>
        internal int Extent => _extent;

        internal int MaxOffset => _scrollable ? Math.Max(0, _extent - ClientSize.Height) : 0;

        internal void ScrollTo(int offset)
        {
            offset = Math.Clamp(offset, 0, MaxOffset);
            if (offset == _offset) return;
            _offset = offset;
            PerformLayout();
            Invalidate(true);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            _extent = Measure();
            if (_offset > MaxOffset)
            {
                _offset = MaxOffset;
                base.OnLayout(levent);
            }

            // Thumb is null while the base constructor lays the page out.
            if (Thumb is null) return;
            if (Parent is { } host)
            {
                Thumb.Bounds = new Rectangle(host.Right + S(16), host.Top, S(8), host.Height);
            }

            Thumb.Visible = Visible && MaxOffset > 0;
            Thumb.Invalidate();
        }

        private int Measure()
        {
            var bottom = 0;
            foreach (Control control in Controls)
            {
                if (control.Visible) bottom = Math.Max(bottom, control.Bottom + control.Margin.Bottom);
            }

            return bottom + _offset + Padding.Bottom;
        }

        /// <summary>The wheel turned over anything on the page: Windows hands it up to here.</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (MaxOffset > 0)
            {
                ScrollBy(e);
                return;
            }

            base.OnMouseWheel(e);
        }

        internal void ScrollBy(MouseEventArgs e)
        {
            ScrollTo(_offset - e.Delta * S(64) / 120);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
        }

        /// <summary>Brings a control reached with Tab into view.</summary>
        private void Reveal(Control control)
        {
            if (MaxOffset <= 0 || control.IsDisposed) return;
            var top = PointToClient(control.PointToScreen(Point.Empty)).Y;
            if (top < 0) ScrollTo(_offset + top - S(8));
            else if (top + control.Height > ClientSize.Height) ScrollTo(_offset + top + control.Height - ClientSize.Height + S(8));
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            Watch(e.Control);
        }

        private void Watch(Control? control)
        {
            if (control is null) return;
            control.GotFocus += (_, _) => Reveal(control);
            control.ControlAdded += (_, added) => Watch(added.Control);
            foreach (Control child in control.Controls) Watch(child);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Thumb is not null) Thumb.Visible = Visible && MaxOffset > 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Thumb.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The page's scroll position as a slim rounded thumb in the colour of the borders. It can
    /// be dragged, and a click beside it moves the page by a screen.
    /// </summary>
    internal sealed class ScrollThumb : Control
    {
        private readonly WizardPage _page;
        private bool _hover;
        private int? _dragFrom;
        private int _dragOffset;

        public ScrollThumb(WizardPage page)
        {
            _page = page;
            Name = "WizardPageScroll";
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            TabStop = false;
            Visible = false;
        }

        private bool Active => _hover || _dragFrom is not null;

        private RectangleF ThumbBounds()
        {
            var extent = Math.Max(1, _page.Extent);
            var height = Math.Max(Sf(32), Height * (float)_page.ClientSize.Height / extent);
            var travel = Height - height;
            var top = _page.MaxOffset > 0 ? travel * _page.Offset / _page.MaxOffset : 0;
            var width = Active ? Sf(6) : Sf(4);
            return new RectangleF((Width - width) / 2f, top, width, height);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var thumb = ThumbBounds();
            if (e.Y >= thumb.Top && e.Y <= thumb.Bottom)
            {
                _dragFrom = e.Y;
                _dragOffset = _page.Offset;
                Capture = true;
            }
            else
            {
                var screen = _page.ClientSize.Height - S(40);
                _page.ScrollTo(_page.Offset + (e.Y < thumb.Top ? -screen : screen));
            }

            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragFrom is not { } from) return;
            var travel = Height - ThumbBounds().Height;
            if (travel <= 0) return;
            _page.ScrollTo(_dragOffset + (int)Math.Round((e.Y - from) * _page.MaxOffset / travel));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragFrom = null;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e) => _page.ScrollBy(e);

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var thumb = ThumbBounds();
            using var path = Rounded(thumb, thumb.Width / 2);
            using var fill = new SolidBrush(Active ? WizardDesign.TextDisabled : WizardDesign.FieldBorder);
            g.FillPath(fill, path);
        }
    }
}
