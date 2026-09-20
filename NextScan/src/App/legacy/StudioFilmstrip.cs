// =============================================================================
// NextScan Studio - Multi-page session filmstrip
// Plan ref: MASTER_PLAN section 13.1.
//
// Horizontal thumbnail carousel for batch scans, supporting page navigation,
// reordering, 90-degree rotations, and deletion.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NextScan.Core;

namespace NextScan.App
{
    public class StudioPageItem
    {
        public RawImage Raw;
        public Bitmap Thumbnail;
        public int Rotation = 0; // 0, 90, 180, 270
    }

    public class StudioFilmstrip : Control
    {
        public event EventHandler SelectedPageChanged;
        public event EventHandler PagesModified;

        List<StudioPageItem> _pages = new List<StudioPageItem>();
        int _selectedIndex = -1;
        int _scrollOffset = 0;

        const int ThumbWidth = 72;
        const int ThumbHeight = 96;
        const int CardMargin = 10;

        public List<StudioPageItem> Pages { get { return _pages; } }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (value >= 0 && value < _pages.Count)
                {
                    if (_selectedIndex != value)
                    {
                        _selectedIndex = value;
                        EnsureVisible(_selectedIndex);
                        Invalidate();
                        if (SelectedPageChanged != null) SelectedPageChanged(this, EventArgs.Empty);
                    }
                }
                else if (_pages.Count == 0)
                {
                    _selectedIndex = -1;
                    Invalidate();
                    if (SelectedPageChanged != null) SelectedPageChanged(this, EventArgs.Empty);
                }
            }
        }

        public StudioPageItem SelectedPage
        {
            get { return (_selectedIndex >= 0 && _selectedIndex < _pages.Count) ? _pages[_selectedIndex] : null; }
        }

        public StudioFilmstrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            Height = 118;
            BackColor = Color.FromArgb(20, 24, 32);
            Cursor = Cursors.Hand;
        }

        public void AddPage(RawImage raw)
        {
            if (raw == null || !raw.IsValid) return;

            StudioPageItem item = new StudioPageItem();
            item.Raw = raw;

            // Generate thumbnail
            using (Bitmap full = raw.ToBitmap())
            {
                if (full != null)
                {
                    Bitmap thumb = new Bitmap(ThumbWidth, ThumbHeight);
                    using (Graphics g = Graphics.FromImage(thumb))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        float aspect = (float)full.Width / full.Height;
                        int tw = ThumbWidth;
                        int th = (int)(tw / aspect);
                        if (th > ThumbHeight)
                        {
                            th = ThumbHeight;
                            tw = (int)(th * aspect);
                        }
                        int tx = (ThumbWidth - tw) / 2;
                        int ty = (ThumbHeight - th) / 2;
                        g.DrawImage(full, tx, ty, tw, th);
                        using (Pen p = new Pen(Color.FromArgb(180, 190, 205), 1f))
                        {
                            g.DrawRectangle(p, tx, ty, tw - 1, th - 1);
                        }
                    }
                    item.Thumbnail = thumb;
                }
            }

            _pages.Add(item);
            SelectedIndex = _pages.Count - 1;
            if (PagesModified != null) PagesModified(this, EventArgs.Empty);
        }

        public void RotateSelected(int angleDegrees)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _pages.Count) return;
            StudioPageItem p = _pages[_selectedIndex];
            p.Rotation = (p.Rotation + angleDegrees + 360) % 360;

            if (p.Thumbnail != null)
            {
                RotateFlipType rft = (angleDegrees == 90 || angleDegrees == -270) ?
                    RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone;
                p.Thumbnail.RotateFlip(rft);
            }
            Invalidate();
            if (SelectedPageChanged != null) SelectedPageChanged(this, EventArgs.Empty);
            if (PagesModified != null) PagesModified(this, EventArgs.Empty);
        }

        public void DeleteSelected()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _pages.Count) return;
            _pages.RemoveAt(_selectedIndex);
            if (_selectedIndex >= _pages.Count) _selectedIndex = _pages.Count - 1;
            Invalidate();
            if (SelectedPageChanged != null) SelectedPageChanged(this, EventArgs.Empty);
            if (PagesModified != null) PagesModified(this, EventArgs.Empty);
        }

        public void ClearAll()
        {
            _pages.Clear();
            _selectedIndex = -1;
            Invalidate();
            if (SelectedPageChanged != null) SelectedPageChanged(this, EventArgs.Empty);
            if (PagesModified != null) PagesModified(this, EventArgs.Empty);
        }

        void EnsureVisible(int index)
        {
            int cardW = ThumbWidth + CardMargin * 2;
            int cardLeft = index * cardW + _scrollOffset;
            int cardRight = cardLeft + cardW;

            if (cardLeft < 10)
            {
                _scrollOffset = -(index * cardW) + 10;
            }
            else if (cardRight > Width - 10)
            {
                _scrollOffset = -(index * cardW + cardW - Width + 10);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Top divider
            using (Pen divPen = new Pen(Color.FromArgb(40, 48, 62), 1f))
            {
                g.DrawLine(divPen, 0, 0, Width, 0);
            }

            if (_pages.Count == 0)
            {
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(100, 115, 135)))
                using (Font f = new Font("Segoe UI", 9f))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("Session Pages (Empty) \u2014 Scanned pages will collect here", f, tb, ClientRectangle, sf);
                }
                return;
            }

            int cardW = ThumbWidth + CardMargin * 2;
            int startX = 14 + _scrollOffset;

            for (int i = 0; i < _pages.Count; i++)
            {
                int x = startX + i * cardW;
                int y = (Height - ThumbHeight) / 2;
                Rectangle cardRect = new Rectangle(x - 4, y - 4, ThumbWidth + 8, ThumbHeight + 8);
                bool isSelected = (i == _selectedIndex);

                // Card background / selection highlight
                if (isSelected)
                {
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(30, 45, 68)))
                    using (Pen sp = new Pen(Color.FromArgb(0, 180, 255), 2f))
                    {
                        g.FillRectangle(sb, cardRect);
                        g.DrawRectangle(sp, cardRect);
                    }
                }
                else
                {
                    using (SolidBrush cb = new SolidBrush(Color.FromArgb(26, 32, 42)))
                    using (Pen cp = new Pen(Color.FromArgb(45, 55, 72), 1f))
                    {
                        g.FillRectangle(cb, cardRect);
                        g.DrawRectangle(cp, cardRect);
                    }
                }

                // Draw thumbnail
                if (_pages[i].Thumbnail != null)
                {
                    g.DrawImage(_pages[i].Thumbnail, x, y, ThumbWidth, ThumbHeight);
                }

                // Page badge: [1], [2], etc.
                string badge = (i + 1).ToString();
                using (Font bf = new Font("Segoe UI", 8f, FontStyle.Bold))
                using (SolidBrush bgb = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                using (SolidBrush btb = new SolidBrush(Color.White))
                {
                    SizeF bSize = g.MeasureString(badge, bf);
                    RectangleF bRect = new RectangleF(x + 2, y + 2, Math.Max(16, bSize.Width + 4), bSize.Height);
                    g.FillRectangle(bgb, bRect);
                    g.DrawString(badge, bf, btb, bRect.X + 2, bRect.Y);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            int cardW = ThumbWidth + CardMargin * 2;
            int startX = 14 + _scrollOffset;

            for (int i = 0; i < _pages.Count; i++)
            {
                int x = startX + i * cardW;
                int y = (Height - ThumbHeight) / 2;
                Rectangle cardRect = new Rectangle(x - 4, y - 4, ThumbWidth + 8, ThumbHeight + 8);
                if (cardRect.Contains(e.Location))
                {
                    SelectedIndex = i;
                    return;
                }
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scrollOffset += (e.Delta > 0 ? 60 : -60);
            int maxOffset = 0;
            int minOffset = Math.Min(0, Width - (_pages.Count * (ThumbWidth + CardMargin * 2) + 40));
            _scrollOffset = Math.Max(minOffset, Math.Min(maxOffset, _scrollOffset));
            Invalidate();
        }
    }
}
