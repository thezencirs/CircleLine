// Copyright (c) 2026 thezencirs (https://github.com/thezencirs)
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CircleLine
{
    public enum PinKind
    {
        Image = 0,
        Video = 1,
        Note = 2,
        File = 3
    }

    public class PinItem
    {
        public string Id;
        public PinKind Kind;
        public int ReelIndex; // 1=Stüdyo, 2=Mimari/CAD, 3=Antigravity/Not
        public string Title;
        public string NoteText;
        public string SourcePath;
        public string CleanPngPath;
        public string SourceFormatBadge;
        public string DimensionsText;
        public Color[] Palette = new Color[0];
        public bool Selected;
        public DateTime CreatedAt;

        // Runtime & Physics (not serialized)
        public Bitmap Thumb;
        public double Angle;
        public double AngularVel;
        public float CurrentX;
        public float TargetX;
        public float PopScale = 1.0f;
    }

    public class CircleLineForm : Form
    {
        // Official 3-Color Studio Palette (#FFFFFF, #EF5F18, #261A66 / #140E38 / #1C134C)
        static readonly Color ColBgDeep = Color.FromArgb(20, 14, 56);       // #140E38
        static readonly Color ColBgPanel = Color.FromArgb(28, 19, 76);      // #1C134C
        static readonly Color ColScarlet = Color.FromArgb(38, 26, 102);     // #261A66
        static readonly Color ColBorder = Color.FromArgb(59, 42, 148);      // #3B2A94
        static readonly Color ColOrange = Color.FromArgb(239, 95, 24);      // #EF5F18
        static readonly Color ColWhite = Color.FromArgb(255, 255, 255);     // #FFFFFF
        static readonly Color ColMuted = Color.FromArgb(190, 184, 224);

        const int WM_HOTKEY = 0x0312;
        const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
        const int HK_TOGGLE_LINE = 9101;
        const int HK_SNIP_SCREEN = 9102;
        const int HK_PASTE_PIN = 9103;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppRunName = "CircleLineByThezencirs";

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObj);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        readonly string dataDir;
        readonly string pinsDir;
        readonly string stateFile;

        readonly List<PinItem> items = new List<PinItem>();
        int activeReel = 0; // 0=Tümü, 1=Circle Stüdyo, 2=3ds Max & CAD, 3=Antigravity & Not
        float scrollOffsetX = 0f;
        bool autoHideMode = false;
        bool isCollapsed = false;
        DateTime lastMouseInsideUtc = DateTime.UtcNow;

        readonly System.Windows.Forms.Timer physicsTimer;
        readonly System.Windows.Forms.Timer edgeWatchTimer;
        readonly NotifyIcon tray;
        FileSystemWatcher cmrWatcher;

        // Layout constants
        const int ExpandedHeight = 258;
        const int CollapsedWidth = 420;
        const int CollapsedHeight = 28;
        const int RopeY = 50;
        const int CardW = 148;
        const int CardH = 172;
        const int CardSpacing = 166;
        int ExpandedWidth = 1380;

        // Interaction state
        int hoveredItemIndex = -1;
        int hoveredSwatchIndex = -1;
        int hoveredCardAction = 0; // 1=Delete, 2=Select, 3=Action1(Edit/Play), 4=Action2(Frame/OCR)
        string hoveredTopBtnId = null;
        PinItem draggingItem = null;
        Point mouseDownPt;
        bool isDraggingCard = false;
        Point lastMousePt;
        string toastMessage = "";
        DateTime toastUntilUtc = DateTime.MinValue;

        struct TopButton
        {
            public string Id;
            public string Text;
            public Rectangle Bounds;
            public bool IsPrimary;
            public bool IsActive;
        }
        readonly List<TopButton> topButtons = new List<TopButton>();

        public CircleLineForm()
        {
            Updater.CleanupOld();

            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CircleLine");
            pinsDir = Path.Combine(dataDir, "pins");
            stateFile = Path.Combine(dataDir, "state.json");
            Directory.CreateDirectory(pinsDir);

            Text = "CircleLine — by thezencirs";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            AllowDrop = true;
            BackColor = ColBgDeep;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ExpandedWidth = Math.Min(Math.Max(980, wa.Width - 60), 1480);
            ApplyWindowGeometry(false);

            // Tray Icon & Context Menu
            tray = new NotifyIcon();
            tray.Text = "CircleLine — Sarkaç Fizikli Stüdyo İpi (by thezencirs)";
            tray.Icon = this.Icon ?? SystemIcons.Application;
            tray.Visible = true;
            tray.DoubleClick += delegate { ExpandBar(); Activate(); };
            BuildTrayMenu();

            // Physics Timer (60 FPS, auto-sleeps when pendulums settle -> 0% CPU!)
            physicsTimer = new System.Windows.Forms.Timer { Interval = 16 };
            physicsTimer.Tick += PhysicsTimer_Tick;

            // Lightweight Edge & Auto-Hide Watcher (200ms)
            edgeWatchTimer = new System.Windows.Forms.Timer { Interval = 180 };
            edgeWatchTimer.Tick += EdgeWatchTimer_Tick;
            edgeWatchTimer.Start();

            LoadState();
            if (items.Count == 0)
            {
                SeedStarterPins();
            }
            SetupCircleMixRecordWatcher();
            LayoutTargets(true);
            WakePhysics();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKey(Handle, HK_TOGGLE_LINE, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.L);
            RegisterHotKey(Handle, HK_SNIP_SCREEN, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.X);
            RegisterHotKey(Handle, HK_PASTE_PIN, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.V);
            UpdateRoundedRegion();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try
            {
                UnregisterHotKey(Handle, HK_TOGGLE_LINE);
                UnregisterHotKey(Handle, HK_SNIP_SCREEN);
                UnregisterHotKey(Handle, HK_PASTE_PIN);
            }
            catch { }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_TOGGLE_LINE)
                {
                    if (isCollapsed || !Visible) { Show(); ExpandBar(); }
                    else CollapseBar();
                    return;
                }
                if (id == HK_SNIP_SCREEN)
                {
                    StartScreenSnip();
                    return;
                }
                if (id == HK_PASTE_PIN)
                {
                    ExpandBar();
                    PasteFromClipboard();
                    return;
                }
            }
            base.WndProc(ref m);
        }

        void ApplyWindowGeometry(bool collapsed)
        {
            isCollapsed = collapsed;
            Rectangle scr = Screen.PrimaryScreen.Bounds;
            if (collapsed)
            {
                int w = CollapsedWidth;
                int h = CollapsedHeight;
                Bounds = new Rectangle(scr.Left + (scr.Width - w) / 2, scr.Top, w, h);
            }
            else
            {
                int w = ExpandedWidth;
                int h = ExpandedHeight;
                Bounds = new Rectangle(scr.Left + (scr.Width - w) / 2, scr.Top, w, h);
            }
            UpdateRoundedRegion();
            Invalidate();
        }

        void UpdateRoundedRegion()
        {
            if (!IsHandleCreated) return;
            IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 18, 18);
            SetWindowRgn(Handle, rgn, true);
        }

        void ExpandBar()
        {
            lastMouseInsideUtc = DateTime.UtcNow;
            if (isCollapsed)
            {
                ApplyWindowGeometry(false);
                // Give a subtle breeze when opening
                List<PinItem> vis = GetVisibleItems();
                for (int i = 0; i < vis.Count; i++)
                {
                    vis[i].AngularVel += (i % 2 == 0 ? 0.045 : -0.045);
                }
                WakePhysics();
            }
        }

        void CollapseBar()
        {
            if (!isCollapsed)
            {
                ApplyWindowGeometry(true);
            }
        }

        void ShowToast(string msg, double seconds = 2.6)
        {
            toastMessage = msg;
            toastUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            WakePhysics();
            Invalidate();
        }

        void WakePhysics()
        {
            if (!physicsTimer.Enabled)
                physicsTimer.Start();
        }

        List<PinItem> GetVisibleItems()
        {
            if (activeReel == 0) return items;
            List<PinItem> list = new List<PinItem>();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].ReelIndex == activeReel)
                    list.Add(items[i]);
            }
            return list;
        }

        void LayoutTargets(bool immediate)
        {
            List<PinItem> vis = GetVisibleItems();
            float startX = 110f - scrollOffsetX;
            for (int i = 0; i < vis.Count; i++)
            {
                vis[i].TargetX = startX + i * CardSpacing;
                if (immediate || Math.Abs(vis[i].CurrentX) < 1f)
                    vis[i].CurrentX = vis[i].TargetX;
            }
            WakePhysics();
        }

        void PhysicsTimer_Tick(object sender, EventArgs e)
        {
            bool anyMoving = false;
            List<PinItem> vis = GetVisibleItems();

            // Neighbor wave coupling + damped pendulum step
            for (int i = 0; i < vis.Count; i++)
            {
                PinItem it = vis[i];

                // Gravity restoring torque: alpha = -(g/L)*sin(theta)
                double accel = -0.024 * Math.Sin(it.Angle);

                // Subtle wave coupling with neighbors
                if (i > 0) accel += (vis[i - 1].Angle - it.Angle) * 0.0035;
                if (i + 1 < vis.Count) accel += (vis[i + 1].Angle - it.Angle) * 0.0035;

                it.AngularVel = (it.AngularVel + accel) * 0.915;
                it.Angle += it.AngularVel;

                if (it.Angle > 0.55) { it.Angle = 0.55; it.AngularVel *= -0.4; }
                if (it.Angle < -0.55) { it.Angle = -0.55; it.AngularVel *= -0.4; }

                float dx = it.TargetX - it.CurrentX;
                if (Math.Abs(dx) > 0.3f)
                {
                    it.CurrentX += dx * 0.24f;
                    // Moving horizontally along the line induces slight pendulum tilt!
                    it.AngularVel -= Math.Max(-0.025, Math.Min(0.025, dx * 0.0005));
                    anyMoving = true;
                }
                else
                {
                    it.CurrentX = it.TargetX;
                }

                if (it.PopScale < 0.995f)
                {
                    it.PopScale += (1.0f - it.PopScale) * 0.25f;
                    anyMoving = true;
                }
                else it.PopScale = 1.0f;

                if (Math.Abs(it.Angle) > 0.0018 || Math.Abs(it.AngularVel) > 0.0018)
                {
                    anyMoving = true;
                }
                else
                {
                    it.Angle = 0;
                    it.AngularVel = 0;
                }
            }

            if (DateTime.UtcNow < toastUntilUtc)
                anyMoving = true;

            Invalidate();

            // 0-RAM / 0-CPU guarantee: stop timer automatically when pendulums settle!
            if (!anyMoving)
            {
                physicsTimer.Stop();
                TrimMemoryFootprint();
            }
        }

        static void TrimMemoryFootprint()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Optimized);
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }

        void EdgeWatchTimer_Tick(object sender, EventArgs e)
        {
            Point cur = Cursor.Position;
            bool insideWindow = Bounds.Contains(cur);
            if (insideWindow)
            {
                lastMouseInsideUtc = DateTime.UtcNow;
                if (isCollapsed)
                {
                    ExpandBar();
                }
            }
            else
            {
                // If user brings mouse to the top center edge while dragging a file or in auto-hide mode
                Rectangle scr = Screen.PrimaryScreen.Bounds;
                bool nearTopCenter = cur.Y <= scr.Top + 10 && cur.X >= scr.Left + 180 && cur.X <= scr.Right - 180;
                bool leftMousePressed = (GetAsyncKeyState(0x01) & 0x8000) != 0;
                bool draggingNearTop = leftMousePressed && cur.Y <= scr.Top + 55;

                if (isCollapsed && (nearTopCenter || draggingNearTop))
                {
                    ExpandBar();
                }
                else if (!isCollapsed && autoHideMode && !isDraggingCard)
                {
                    if ((DateTime.UtcNow - lastMouseInsideUtc).TotalSeconds > 1.25)
                    {
                        CollapseBar();
                    }
                }
            }
        }

        // ====================================================================
        // RENDERING (GDI+ Double-Buffered Studio UI + Swinging Polaroid Cards)
        // ====================================================================
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            if (isCollapsed)
            {
                PaintCollapsedPill(g);
                return;
            }

            // 1. Studio Canopy Background
            using (LinearGradientBrush bg = new LinearGradientBrush(ClientRectangle, ColBgDeep, ColBgPanel, LinearGradientMode.Vertical))
            {
                g.FillRectangle(bg, ClientRectangle);
            }

            // Top Header Strip (#1C134C)
            using (SolidBrush hb = new SolidBrush(ColBgPanel))
            {
                g.FillRectangle(hb, 0, 0, Width, 38);
            }

            // Build & Draw Top Toolbar Buttons
            BuildAndPaintTopBar(g);

            // 2. Glowing Orange Studio Rope (#EF5F18) at Y = RopeY
            PaintStudioRope(g);

            // 3. Swinging Polaroid & Clothespin Cards
            List<PinItem> vis = GetVisibleItems();
            if (vis.Count == 0)
            {
                using (Font f = new Font("Segoe UI Semibold", 10f))
                using (SolidBrush b = new SolidBrush(ColMuted))
                {
                    string emptyMsg = "Bu makarada henüz mandallı öğe yok — Görsel, video veya dosyayı buraya sürükleyin ya da Ctrl+Shift+X ile ekran kesiti alın.";
                    SizeF sz = g.MeasureString(emptyMsg, f);
                    g.DrawString(emptyMsg, f, b, (Width - sz.Width) / 2f, RopeY + 75f);
                }
            }
            else
            {
                for (int i = 0; i < vis.Count; i++)
                {
                    PaintSwingingCard(g, vis[i], i == hoveredItemIndex);
                }
            }

            // 4. Outer Border
            using (Pen borderPen = new Pen(ColBorder, 1.5f))
            {
                g.DrawRectangle(borderPen, 1, 1, Width - 2, Height - 2);
            }
            using (Pen topOrange = new Pen(ColOrange, 2.5f))
            {
                g.DrawLine(topOrange, 16, 1, Width - 16, 1);
            }

            // 5. Live Toast Notification
            if (DateTime.UtcNow < toastUntilUtc && !string.IsNullOrEmpty(toastMessage))
            {
                using (Font tf = new Font("Segoe UI Bold", 9.2f))
                {
                    SizeF tsz = g.MeasureString(toastMessage, tf);
                    float tw = tsz.Width + 28f;
                    float th = 28f;
                    float tx = (Width - tw) / 2f;
                    float ty = Height - 34f;
                    using (GraphicsPath path = RoundedRect(new RectangleF(tx, ty, tw, th), 12f))
                    using (SolidBrush tb = new SolidBrush(ColOrange))
                    using (Pen tp = new Pen(ColWhite, 1.2f))
                    {
                        g.FillPath(tb, path);
                        g.DrawPath(tp, path);
                    }
                    using (SolidBrush wb = new SolidBrush(ColWhite))
                    {
                        g.DrawString(toastMessage, tf, wb, tx + 14f, ty + 5f);
                    }
                }
            }
        }

        void PaintCollapsedPill(Graphics g)
        {
            using (LinearGradientBrush bg = new LinearGradientBrush(ClientRectangle, ColBgDeep, ColScarlet, LinearGradientMode.Horizontal))
            {
                g.FillRectangle(bg, ClientRectangle);
            }
            using (Pen p = new Pen(ColOrange, 2f))
            {
                g.DrawLine(p, 0, 1, Width, 1);
                g.DrawRectangle(p, 1, 1, Width - 2, Height - 2);
            }
            using (Font f = new Font("Segoe UI Bold", 8.8f))
            using (SolidBrush wb = new SolidBrush(ColWhite))
            using (SolidBrush ob = new SolidBrush(ColOrange))
            {
                g.FillEllipse(ob, 12, 7, 13, 13);
                string txt = string.Format("CIRCLELINE  •  {0} Mandal Asılı  (Fareyi Yaklaştır / Sürükle)", items.Count);
                g.DrawString(txt, f, wb, 32, 5);
            }
        }

        void BuildAndPaintTopBar(Graphics g)
        {
            topButtons.Clear();

            // Brand Logo + Title
            using (SolidBrush ob = new SolidBrush(ColOrange))
            using (Pen wp = new Pen(ColWhite, 1.8f))
            {
                g.FillEllipse(ob, 12, 8, 20, 20);
                g.DrawEllipse(wp, 15, 11, 14, 14);
            }
            using (Font bf = new Font("Segoe UI Black", 9.5f))
            using (SolidBrush wb = new SolidBrush(ColWhite))
            {
                g.DrawString("CIRCLELINE", bf, wb, 37, 9);
            }

            int x = 134;
            string[] reelNames = new string[]
            {
                "🌟 Tümü (" + items.Count + ")",
                "🎬 Stüdyo",
                "🏗️ 3ds Max & CAD",
                "🤖 Antigravity"
            };
            for (int r = 0; r < reelNames.Length; r++)
            {
                int bw = r == 0 ? 86 : (r == 2 ? 112 : 92);
                topButtons.Add(new TopButton
                {
                    Id = "reel_" + r,
                    Text = reelNames[r],
                    Bounds = new Rectangle(x, 6, bw, 25),
                    IsActive = (activeReel == r)
                });
                x += bw + 5;
            }

            x += 8;
            AddActionBtn(ref x, "act_snip", "✂️ Kesit Al", 82, true);
            AddActionBtn(ref x, "act_paste", "📋 Yapıştır", 82, false);
            AddActionBtn(ref x, "act_note", "📝 +Not", 68, false);
            AddActionBtn(ref x, "act_cmr", "🎬 Son Kayıtlar", 104, false);
            AddActionBtn(ref x, "act_merge", "⚡ İptekileri Birleştir", 136, true);
            AddActionBtn(ref x, "act_agy", "🚀 Antigravity Paketi", 136, true);

            // Right-side window mode controls
            int rx = Width - 12;
            AddRightBtn(ref rx, "act_hide", "▲ Gizle", 62, false);
            AddRightBtn(ref rx, "act_autohide", autoHideMode ? "🧲 Oto-Gizle: AÇIK" : "📌 Sabit İp", 118, autoHideMode);

            using (Font f = new Font("Segoe UI Semibold", 8.2f))
            {
                for (int i = 0; i < topButtons.Count; i++)
                {
                    TopButton tb = topButtons[i];
                    bool hov = (tb.Id == hoveredTopBtnId);
                    Color fill = tb.IsActive ? ColOrange : (tb.IsPrimary ? Color.FromArgb(hov ? 255 : 225, ColOrange) : (hov ? ColBorder : ColScarlet));
                    using (GraphicsPath path = RoundedRect(tb.Bounds, 6f))
                    using (SolidBrush b = new SolidBrush(fill))
                    using (Pen p = new Pen(tb.IsActive || hov ? ColWhite : ColBorder, 1f))
                    {
                        g.FillPath(b, path);
                        g.DrawPath(p, path);
                    }
                    SizeF sz = g.MeasureString(tb.Text, f);
                    using (SolidBrush wb = new SolidBrush(ColWhite))
                    {
                        g.DrawString(tb.Text, f, wb, tb.Bounds.X + (tb.Bounds.Width - sz.Width) / 2f, tb.Bounds.Y + (tb.Bounds.Height - sz.Height) / 2f + 0.5f);
                    }
                }
            }
        }

        void AddActionBtn(ref int x, string id, string text, int w, bool primary)
        {
            topButtons.Add(new TopButton { Id = id, Text = text, Bounds = new Rectangle(x, 6, w, 25), IsPrimary = primary });
            x += w + 5;
        }

        void AddRightBtn(ref int rx, string id, string text, int w, bool active)
        {
            rx -= w;
            topButtons.Add(new TopButton { Id = id, Text = text, Bounds = new Rectangle(rx, 6, w, 25), IsActive = active });
            rx -= 6;
        }

        void PaintStudioRope(Graphics g)
        {
            // Left and right metallic wall anchors
            using (SolidBrush sb = new SolidBrush(ColScarlet))
            using (Pen op = new Pen(ColOrange, 2f))
            {
                g.FillEllipse(sb, 14, RopeY - 7, 14, 14);
                g.DrawEllipse(op, 14, RopeY - 7, 14, 14);
                g.FillEllipse(sb, Width - 28, RopeY - 7, 14, 14);
                g.DrawEllipse(op, Width - 28, RopeY - 7, 14, 14);
            }

            // Subtle rope glow + main braided orange studio line
            using (Pen glowPen = new Pen(Color.FromArgb(65, ColOrange), 6f))
            {
                g.DrawLine(glowPen, 22, RopeY, Width - 22, RopeY);
            }
            using (Pen ropePen = new Pen(ColOrange, 2.6f))
            {
                g.DrawLine(ropePen, 22, RopeY, Width - 22, RopeY);
            }
        }

        void PaintSwingingCard(Graphics g, PinItem item, bool isHovered)
        {
            if (item.CurrentX < -CardW || item.CurrentX > Width + CardW)
                return;

            GraphicsState st = g.Save();
            g.TranslateTransform(item.CurrentX, RopeY);
            g.RotateTransform((float)(item.Angle * 180.0 / Math.PI));
            if (Math.Abs(item.PopScale - 1.0f) > 0.01f)
            {
                g.ScaleTransform(item.PopScale, item.PopScale);
            }

            float halfW = CardW / 2f;
            RectangleF cardRect = new RectangleF(-halfW, 10f, CardW, CardH);

            // Drop shadow
            using (GraphicsPath shPath = RoundedRect(new RectangleF(cardRect.X + 4, cardRect.Y + 6, cardRect.Width, cardRect.Height), 7f))
            using (SolidBrush shBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            {
                g.FillPath(shBrush, shPath);
            }

            // Polaroid / Studio Card Body
            bool isNote = (item.Kind == PinKind.Note);
            Color cardFill = isNote ? ColScarlet : ColWhite;
            using (GraphicsPath cardPath = RoundedRect(cardRect, 7f))
            using (SolidBrush cb = new SolidBrush(cardFill))
            using (Pen cp = new Pen(item.Selected || isHovered ? ColOrange : (isNote ? ColBorder : Color.FromArgb(215, 215, 225)), item.Selected || isHovered ? 2.4f : 1.2f))
            {
                g.FillPath(cb, cardPath);
                g.DrawPath(cp, cardPath);
            }

            // Inner Content Area
            RectangleF innerRect = new RectangleF(-halfW + 8f, 18f, CardW - 16f, 100f);
            if (item.Kind == PinKind.Image || item.Kind == PinKind.Video || item.Kind == PinKind.File)
            {
                using (SolidBrush ib = new SolidBrush(ColBgDeep))
                {
                    g.FillRectangle(ib, innerRect);
                }

                if (item.Thumb != null)
                {
                    float aspect = (float)item.Thumb.Width / Math.Max(1, item.Thumb.Height);
                    float rw = innerRect.Width;
                    float rh = innerRect.Height;
                    float dw = rw, dh = rw / aspect;
                    if (dh > rh) { dh = rh; dw = rh * aspect; }
                    float dx = innerRect.X + (rw - dw) / 2f;
                    float dy = innerRect.Y + (rh - dh) / 2f;
                    g.DrawImage(item.Thumb, dx, dy, dw, dh);
                }

                // Film strip perforations if Video
                if (item.Kind == PinKind.Video)
                {
                    using (SolidBrush perf = new SolidBrush(Color.FromArgb(190, ColOrange)))
                    {
                        for (float py = innerRect.Y + 5f; py < innerRect.Bottom - 6f; py += 14f)
                        {
                            g.FillRectangle(perf, innerRect.X + 3f, py, 4f, 7f);
                            g.FillRectangle(perf, innerRect.Right - 7f, py, 4f, 7f);
                        }
                    }
                }

                // Format Badge (top-left of inner rect)
                if (!string.IsNullOrEmpty(item.SourceFormatBadge))
                {
                    using (Font bf = new Font("Segoe UI Bold", 6.8f))
                    using (SolidBrush bb = new SolidBrush(Color.FromArgb(215, ColBgDeep)))
                    using (SolidBrush wb = new SolidBrush(ColWhite))
                    {
                        SizeF bsz = g.MeasureString(item.SourceFormatBadge, bf);
                        g.FillRectangle(bb, innerRect.X + 3f, innerRect.Y + 3f, bsz.Width + 6f, 14f);
                        g.DrawString(item.SourceFormatBadge, bf, wb, innerRect.X + 6f, innerRect.Y + 3f);
                    }
                }

                // Hover Quick Action Pills (✏️ Redline / 📸 Kare Sök / 🔍 OCR)
                if (isHovered)
                {
                    string b1 = item.Kind == PinKind.Video ? "▶ Oynat" : "✏️ Çiz";
                    string b2 = item.Kind == PinKind.Video ? "📸 Kare" : "🔍 OCR";
                    PaintMiniOverlayBtn(g, new RectangleF(innerRect.X + 6f, innerRect.Bottom - 22f, 56f, 18f), b1, hoveredCardAction == 3);
                    PaintMiniOverlayBtn(g, new RectangleF(innerRect.Right - 62f, innerRect.Bottom - 22f, 56f, 18f), b2, hoveredCardAction == 4);
                }
            }
            else if (item.Kind == PinKind.Note)
            {
                using (SolidBrush nb = new SolidBrush(ColBgDeep))
                {
                    g.FillRectangle(nb, innerRect);
                }
                using (Font nf = new Font("Segoe UI", 8.0f))
                using (SolidBrush wb = new SolidBrush(ColWhite))
                {
                    RectangleF textBox = new RectangleF(innerRect.X + 6f, innerRect.Y + 6f, innerRect.Width - 12f, innerRect.Height - 12f);
                    g.DrawString(item.NoteText ?? "", nf, wb, textBox);
                }
                if (isHovered)
                {
                    PaintMiniOverlayBtn(g, new RectangleF(innerRect.X + 6f, innerRect.Bottom - 22f, 56f, 18f), "✏️ Yaz", hoveredCardAction == 3);
                    PaintMiniOverlayBtn(g, new RectangleF(innerRect.Right - 62f, innerRect.Bottom - 22f, 56f, 18f), "📋 Kopya", hoveredCardAction == 4);
                }
            }

            // Title & Bottom Strip (Dominant 5-Color Palette for Images!)
            using (Font tf = new Font("Segoe UI Bold", 7.8f))
            using (SolidBrush tb = new SolidBrush(isNote ? ColWhite : ColBgDeep))
            {
                string title = item.Title ?? "Öğe";
                if (title.Length > 21) title = title.Substring(0, 19) + "…";
                g.DrawString(title, tf, tb, -halfW + 8f, 122f);
            }

            if (item.Palette != null && item.Palette.Length > 0)
            {
                float swW = 22f;
                float swH = 18f;
                float swStart = -halfW + 10f;
                float swY = 143f;
                for (int c = 0; c < item.Palette.Length && c < 5; c++)
                {
                    RectangleF swRect = new RectangleF(swStart + c * (swW + 4.5f), swY, swW, swH);
                    using (SolidBrush sb = new SolidBrush(item.Palette[c]))
                    using (Pen sp = new Pen(isHovered && hoveredSwatchIndex == c ? ColOrange : Color.FromArgb(180, 40, 40, 60), isHovered && hoveredSwatchIndex == c ? 2f : 1f))
                    {
                        g.FillRectangle(sb, swRect);
                        g.DrawRectangle(sp, swRect.X, swRect.Y, swRect.Width, swRect.Height);
                    }
                }
            }
            else
            {
                using (Font sf = new Font("Segoe UI", 7.2f))
                using (SolidBrush sb = new SolidBrush(isNote ? ColMuted : ColScarlet))
                {
                    string sub = !string.IsNullOrEmpty(item.DimensionsText) ? item.DimensionsText : "Sürükle veya Tıkla";
                    g.DrawString(sub, sf, sb, -halfW + 8f, 145f);
                }
            }

            // Top-Right Delete '×' & Top-Left Selection Check on Card
            if (isHovered || item.Selected)
            {
                // Selection badge (top-left)
                RectangleF selR = new RectangleF(-halfW + 4f, 12f, 16f, 16f);
                using (SolidBrush sb = new SolidBrush(item.Selected ? ColOrange : ColScarlet))
                using (Pen sp = new Pen(ColWhite, 1.2f))
                {
                    g.FillEllipse(sb, selR);
                    g.DrawEllipse(sp, selR);
                }
                if (item.Selected)
                {
                    using (Font cf = new Font("Segoe UI Bold", 7.5f))
                    using (SolidBrush wb = new SolidBrush(ColWhite))
                    {
                        g.DrawString("✓", cf, wb, selR.X + 2f, selR.Y + 0.5f);
                    }
                }
            }

            if (isHovered)
            {
                RectangleF delR = new RectangleF(halfW - 20f, 12f, 16f, 16f);
                using (SolidBrush db = new SolidBrush(hoveredCardAction == 1 ? ColOrange : ColScarlet))
                using (Font df = new Font("Segoe UI Bold", 8f))
                using (SolidBrush wb = new SolidBrush(ColWhite))
                {
                    g.FillEllipse(db, delR);
                    g.DrawString("×", df, wb, delR.X + 3f, delR.Y - 0.5f);
                }
            }

            // 3D Studio Clothespin (Mandal) gripping the card at (0, 0)
            using (GraphicsPath pinPath = RoundedRect(new RectangleF(-7f, -14f, 14f, 30f), 4f))
            using (SolidBrush pb = new SolidBrush(item.Selected ? ColWhite : ColOrange))
            using (Pen pp = new Pen(ColBgDeep, 1.3f))
            {
                g.FillPath(pb, pinPath);
                g.DrawPath(pp, pinPath);
            }
            // Metallic spring ring on the clothespin
            using (SolidBrush mb = new SolidBrush(ColScarlet))
            {
                g.FillRectangle(mb, -7f, -2f, 14f, 5f);
            }

            g.Restore(st);
        }

        void PaintMiniOverlayBtn(Graphics g, RectangleF r, string text, bool hovered)
        {
            using (GraphicsPath p = RoundedRect(r, 5f))
            using (SolidBrush b = new SolidBrush(hovered ? ColOrange : Color.FromArgb(225, ColScarlet)))
            using (Pen pen = new Pen(ColWhite, 1f))
            using (Font f = new Font("Segoe UI Bold", 7.2f))
            using (SolidBrush wb = new SolidBrush(ColWhite))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
                SizeF sz = g.MeasureString(text, f);
                g.DrawString(text, f, wb, r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f);
            }
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ====================================================================
        // MOUSE, WIND PHYSICS, DRAG-REORDER & DRAG-OUT AUTO-CONVERT
        // ====================================================================
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            lastMouseInsideUtc = DateTime.UtcNow;

            if (isCollapsed)
            {
                ExpandBar();
                return;
            }

            int dx = e.X - lastMousePt.X;
            lastMousePt = e.Location;

            // If dragging a card:
            if (draggingItem != null && e.Button == MouseButtons.Left)
            {
                int moveDist = Math.Abs(e.X - mouseDownPt.X) + Math.Abs(e.Y - mouseDownPt.Y);
                if (moveDist > 6) isDraggingCard = true;

                if (isDraggingCard)
                {
                    // If user pulls the card DOWN off the line -> initiate native FileDrop / Bitmap drag to 3ds Max, AutoCAD, Revit, Photoshop, Explorer!
                    if (e.Y - mouseDownPt.Y > 42 || e.Y > Height - 15)
                    {
                        PinItem toExport = draggingItem;
                        draggingItem = null;
                        isDraggingCard = false;
                        StartExternalDragOut(toExport);
                        return;
                    }

                    // Otherwise horizontal drag along the rope reorders the clothespin!
                    draggingItem.CurrentX = e.X;
                    draggingItem.Angle = Math.Max(-0.35, Math.Min(0.35, -dx * 0.012));
                    ReorderDraggedItem(draggingItem, e.X);
                    WakePhysics();
                    Invalidate();
                    return;
                }
            }

            // Check top toolbar hover
            string newTopHover = null;
            for (int i = 0; i < topButtons.Count; i++)
            {
                if (topButtons[i].Bounds.Contains(e.Location))
                {
                    newTopHover = topButtons[i].Id;
                    break;
                }
            }

            // Check card hover & apply wind impulse!
            int prevHover = hoveredItemIndex;
            hoveredItemIndex = -1;
            hoveredSwatchIndex = -1;
            hoveredCardAction = 0;

            List<PinItem> vis = GetVisibleItems();
            for (int i = vis.Count - 1; i >= 0; i--)
            {
                PinItem it = vis[i];
                float lx = e.X - it.CurrentX;
                float ly = e.Y - RopeY;
                if (lx >= -CardW / 2f && lx <= CardW / 2f && ly >= -14f && ly <= CardH + 14f)
                {
                    hoveredItemIndex = i;

                    // Apply natural breeze / brush impulse when cursor sweeps across the card!
                    if (Math.Abs(dx) > 1)
                    {
                        double impulse = Math.Max(-0.045, Math.Min(0.045, -dx * 0.0018));
                        it.AngularVel += impulse;
                        WakePhysics();
                    }

                    // Check sub-hit regions inside card local coordinates
                    float halfW = CardW / 2f;
                    if (lx >= halfW - 22f && ly >= 10f && ly <= 30f)
                        hoveredCardAction = 1; // Delete '×'
                    else if (lx <= -halfW + 22f && ly >= 10f && ly <= 30f)
                        hoveredCardAction = 2; // Select '✓'
                    else if (ly >= 94f && ly <= 118f && lx < 0)
                        hoveredCardAction = 3; // Left quick button (✏️ / ▶)
                    else if (ly >= 94f && ly <= 118f && lx >= 0)
                        hoveredCardAction = 4; // Right quick button (📸 / 🔍 / 📋)
                    else if (it.Palette != null && it.Palette.Length > 0 && ly >= 140f && ly <= 165f)
                    {
                        float swStart = -halfW + 10f;
                        for (int c = 0; c < it.Palette.Length && c < 5; c++)
                        {
                            float sx = swStart + c * 26.5f;
                            if (lx >= sx && lx <= sx + 23f)
                            {
                                hoveredSwatchIndex = c;
                                break;
                            }
                        }
                    }
                    break;
                }
            }

            Cursor = (newTopHover != null || hoveredItemIndex >= 0) ? Cursors.Hand : Cursors.Default;
            if (newTopHover != hoveredTopBtnId || prevHover != hoveredItemIndex || hoveredSwatchIndex >= 0 || hoveredCardAction > 0)
            {
                hoveredTopBtnId = newTopHover;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            mouseDownPt = e.Location;
            isDraggingCard = false;

            if (isCollapsed)
            {
                ExpandBar();
                return;
            }

            // Top button click?
            if (e.Button == MouseButtons.Left && hoveredTopBtnId != null)
            {
                HandleTopButtonClick(hoveredTopBtnId);
                return;
            }

            List<PinItem> vis = GetVisibleItems();
            if (hoveredItemIndex >= 0 && hoveredItemIndex < vis.Count)
            {
                PinItem it = vis[hoveredItemIndex];

                // Color swatch click? (Left click = #HEX, Right click = RGB)
                if (hoveredSwatchIndex >= 0 && hoveredSwatchIndex < it.Palette.Length)
                {
                    Color c = it.Palette[hoveredSwatchIndex];
                    string val = e.Button == MouseButtons.Right
                        ? string.Format("RGB({0}, {1}, {2})", c.R, c.G, c.B)
                        : string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
                    try { Clipboard.SetText(val); } catch { }
                    it.AngularVel += 0.06;
                    ShowToast("🎨 Renk Kopyalandı: " + val);
                    return;
                }

                if (e.Button == MouseButtons.Left)
                {
                    if (hoveredCardAction == 1)
                    {
                        RemovePin(it);
                        return;
                    }
                    if (hoveredCardAction == 2)
                    {
                        it.Selected = !it.Selected;
                        it.AngularVel += 0.05;
                        WakePhysics();
                        Invalidate();
                        return;
                    }
                    if (hoveredCardAction == 3)
                    {
                        TriggerCardPrimaryAction(it);
                        return;
                    }
                    if (hoveredCardAction == 4)
                    {
                        TriggerCardSecondaryAction(it);
                        return;
                    }

                    draggingItem = it;
                }
                else if (e.Button == MouseButtons.Right)
                {
                    ShowCardContextMenu(it, e.Location);
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                tray.ContextMenuStrip.Show(PointToScreen(e.Location));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (draggingItem != null)
            {
                draggingItem = null;
                isDraggingCard = false;
                LayoutTargets(false);
                SaveState();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            List<PinItem> vis = GetVisibleItems();
            if (hoveredItemIndex >= 0 && hoveredItemIndex < vis.Count)
            {
                TriggerCardPrimaryAction(vis[hoveredItemIndex]);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            List<PinItem> vis = GetVisibleItems();
            float maxScroll = Math.Max(0f, vis.Count * CardSpacing - (Width - 200f));
            scrollOffsetX = Math.Max(0f, Math.Min(maxScroll, scrollOffsetX - (e.Delta > 0 ? 140f : -140f)));
            LayoutTargets(false);
        }

        void ReorderDraggedItem(PinItem dragged, int mouseX)
        {
            List<PinItem> vis = GetVisibleItems();
            int curIdx = items.IndexOf(dragged);
            if (curIdx < 0) return;

            for (int i = 0; i < vis.Count; i++)
            {
                PinItem other = vis[i];
                if (other == dragged) continue;
                int otherIdx = items.IndexOf(other);
                if (mouseX < other.TargetX && curIdx > otherIdx)
                {
                    items.RemoveAt(curIdx);
                    items.Insert(otherIdx, dragged);
                    LayoutTargets(false);
                    break;
                }
                if (mouseX > other.TargetX && curIdx < otherIdx)
                {
                    items.RemoveAt(curIdx);
                    items.Insert(otherIdx, dragged);
                    LayoutTargets(false);
                    break;
                }
            }
        }

        void StartExternalDragOut(PinItem item)
        {
            try
            {
                DataObject data = new DataObject();
                string dropFile = null;

                if (item.Kind == PinKind.Image && !string.IsNullOrEmpty(item.CleanPngPath) && File.Exists(item.CleanPngPath))
                {
                    dropFile = item.CleanPngPath;
                }
                else if (!string.IsNullOrEmpty(item.SourcePath) && File.Exists(item.SourcePath))
                {
                    dropFile = item.SourcePath;
                }
                else if (item.Kind == PinKind.Note && !string.IsNullOrEmpty(item.NoteText))
                {
                    string tempTxt = Path.Combine(pinsDir, "CircleLine_Not_" + item.Id + ".txt");
                    File.WriteAllText(tempTxt, item.NoteText, Encoding.UTF8);
                    dropFile = tempTxt;
                    data.SetText(item.NoteText);
                }

                if (!string.IsNullOrEmpty(dropFile) && File.Exists(dropFile))
                {
                    System.Collections.Specialized.StringCollection files = new System.Collections.Specialized.StringCollection();
                    files.Add(dropFile);
                    data.SetFileDropList(files);
                    data.SetText(dropFile);
                }

                ShowToast("📤 3ds Max / AutoCAD / Editör İçin Sürükleniyor...");
                DoDragDrop(data, DragDropEffects.Copy);
            }
            catch (Exception ex)
            {
                ShowToast("Sürükleme hatası: " + ex.Message);
            }
        }

        // ====================================================================
        // DRAG-IN & CLIPBOARD PINNING (AUTO-DECODE WEBP/HEIC/AVIF -> CLEAN PNG)
        // ====================================================================
        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);
            ExpandBar();
            if (drgevent.Data.GetDataPresent(DataFormats.FileDrop) ||
                drgevent.Data.GetDataPresent(DataFormats.Bitmap) ||
                drgevent.Data.GetDataPresent(DataFormats.UnicodeText) ||
                drgevent.Data.GetDataPresent(DataFormats.Text))
            {
                drgevent.Effect = DragDropEffects.Copy;
            }
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            try
            {
                if (drgevent.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])drgevent.Data.GetData(DataFormats.FileDrop);
                    if (files != null)
                    {
                        for (int i = 0; i < files.Length; i++)
                            PinFileFromPath(files[i], true);
                    }
                }
                else if (drgevent.Data.GetDataPresent(DataFormats.Bitmap))
                {
                    Bitmap bmp = drgevent.Data.GetData(DataFormats.Bitmap) as Bitmap;
                    if (bmp != null) PinBitmap(bmp, "Sürüklenen Görsel", "BMP");
                }
                else if (drgevent.Data.GetDataPresent(DataFormats.UnicodeText))
                {
                    string txt = drgevent.Data.GetData(DataFormats.UnicodeText) as string;
                    if (!string.IsNullOrWhiteSpace(txt)) PinNote("Hızlı Not", txt.Trim());
                }
            }
            catch (Exception ex)
            {
                ShowToast("İpe eklenemedi: " + ex.Message);
            }
        }

        void PasteFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var list = Clipboard.GetFileDropList();
                    foreach (string f in list) PinFileFromPath(f, true);
                }
                else if (Clipboard.ContainsImage())
                {
                    Image img = Clipboard.GetImage();
                    if (img != null)
                    {
                        PinBitmap(new Bitmap(img), "Pano Görseli " + DateTime.Now.ToString("HH:mm:ss"), "CLIP→PNG");
                    }
                }
                else if (Clipboard.ContainsText())
                {
                    string txt = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        PinNote("Pano Notu", txt.Trim());
                    }
                }
                else
                {
                    ShowToast("Panoda görsel, dosya veya metin bulunamadı.");
                }
            }
            catch (Exception ex)
            {
                ShowToast("Pano okunamadı: " + ex.Message);
            }
        }

        public void PinFileFromPath(string path, bool notify)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            string name = Path.GetFileNameWithoutExtension(path);

            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".tif" || ext == ".tiff" || ext == ".webp" || ext == ".heic" || ext == ".avif")
            {
                Bitmap decoded = DecodeAnyImage(path);
                if (decoded != null)
                {
                    string badge = (ext == ".webp" || ext == ".heic" || ext == ".avif" || ext == ".bmp")
                        ? ext.TrimStart('.').ToUpperInvariant() + "→PNG"
                        : ext.TrimStart('.').ToUpperInvariant();
                    PinBitmap(decoded, name, badge, path);
                    decoded.Dispose();
                    return;
                }
            }

            if (ext == ".mp4" || ext == ".mkv" || ext == ".mov" || ext == ".avi" || ext == ".webm")
            {
                PinVideoFile(path, notify);
                return;
            }

            // Other CAD / Archive / Text file
            PinItem item = new PinItem
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                Kind = PinKind.File,
                ReelIndex = (ext == ".max" || ext == ".dwg" || ext == ".dxf" || ext == ".rvt" || ext == ".skp") ? 2 : 1,
                Title = Path.GetFileName(path),
                SourcePath = path,
                SourceFormatBadge = ext.TrimStart('.').ToUpperInvariant(),
                DimensionsText = FormatBytes(new FileInfo(path).Length),
                CreatedAt = DateTime.Now,
                Angle = -0.32,
                AngularVel = 0.04,
                PopScale = 0.3f
            };
            item.Thumb = RenderFileBadgeThumb(item.SourceFormatBadge, item.Title);
            InsertPinAtFront(item, notify ? "📎 Dosya Mandallandı: " + item.Title : null);
        }

        public void PinBitmap(Bitmap bmp, string title, string badge, string originalSourcePath = null)
        {
            string id = Guid.NewGuid().ToString("N").Substring(0, 10);
            string cleanPng = Path.Combine(pinsDir, "CircleLine_" + id + ".png");
            bmp.Save(cleanPng, ImageFormat.Png);

            PinItem item = new PinItem
            {
                Id = id,
                Kind = PinKind.Image,
                ReelIndex = activeReel == 0 ? 2 : activeReel,
                Title = title,
                SourcePath = originalSourcePath ?? cleanPng,
                CleanPngPath = cleanPng,
                SourceFormatBadge = badge ?? "PNG",
                DimensionsText = bmp.Width + "×" + bmp.Height,
                Palette = ExtractDominantPalette(bmp, 5),
                CreatedAt = DateTime.Now,
                Angle = -0.35,
                AngularVel = 0.045,
                PopScale = 0.25f
            };
            item.Thumb = CreateThumbnail(bmp, 260, 200);
            InsertPinAtFront(item, "🧲 İpe Mandallandı (" + item.SourceFormatBadge + " + 5 Renk Paleti)");
        }

        public void PinVideoFile(string videoPath, bool notify)
        {
            // Avoid duplicate auto-watch pin
            for (int i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i].SourcePath, videoPath, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            string id = Guid.NewGuid().ToString("N").Substring(0, 10);
            string thumbPng = Path.Combine(pinsDir, "CircleLine_VidThumb_" + id + ".png");
            ExtractVideoFrameWithFfmpeg(videoPath, 0.8, thumbPng);

            Bitmap thumbBmp = null;
            Color[] pal = new Color[0];
            if (File.Exists(thumbPng))
            {
                try
                {
                    using (Bitmap raw = new Bitmap(thumbPng))
                    {
                        pal = ExtractDominantPalette(raw, 5);
                        thumbBmp = CreateThumbnail(raw, 260, 200);
                    }
                }
                catch { }
            }
            if (thumbBmp == null)
            {
                thumbBmp = RenderFileBadgeThumb("MP4 VİDEO", Path.GetFileName(videoPath));
            }

            PinItem item = new PinItem
            {
                Id = id,
                Kind = PinKind.Video,
                ReelIndex = 1, // Circle Stüdyo
                Title = Path.GetFileNameWithoutExtension(videoPath),
                SourcePath = videoPath,
                CleanPngPath = File.Exists(thumbPng) ? thumbPng : null,
                SourceFormatBadge = "🎬 VİDEO",
                DimensionsText = FormatBytes(new FileInfo(videoPath).Length),
                Palette = pal,
                Thumb = thumbBmp,
                CreatedAt = DateTime.Now,
                Angle = -0.36,
                AngularVel = 0.05,
                PopScale = 0.25f
            };
            InsertPinAtFront(item, notify ? "🎬 Stüdyo Videosu İpe Asıldı: " + item.Title : null);
        }

        public void PinNote(string title, string text)
        {
            string id = Guid.NewGuid().ToString("N").Substring(0, 10);
            PinItem item = new PinItem
            {
                Id = id,
                Kind = PinKind.Note,
                ReelIndex = 3, // Antigravity & Not
                Title = string.IsNullOrWhiteSpace(title) ? "Stüdyo Notu" : title,
                NoteText = text,
                SourceFormatBadge = "NOT",
                DimensionsText = text.Length + " karakter",
                CreatedAt = DateTime.Now,
                Angle = 0.32,
                AngularVel = -0.04,
                PopScale = 0.25f
            };
            InsertPinAtFront(item, "📝 Not Mandallandı");
        }

        void InsertPinAtFront(PinItem item, string toast)
        {
            items.Insert(0, item);
            scrollOffsetX = 0f;
            LayoutTargets(false);
            SaveState();
            if (!string.IsNullOrEmpty(toast))
                ShowToast(toast);
            else
                WakePhysics();
        }

        void RemovePin(PinItem item)
        {
            items.Remove(item);
            if (item.Thumb != null) { try { item.Thumb.Dispose(); } catch { } }
            LayoutTargets(false);
            SaveState();
            ShowToast("🗑️ Mandal İpten Alındı");
        }

        // ====================================================================
        // KILLER WORKFLOWS: TOP BAR & CARD ACTIONS
        // ====================================================================
        void HandleTopButtonClick(string id)
        {
            if (id.StartsWith("reel_"))
            {
                int r = int.Parse(id.Substring(5));
                activeReel = r;
                scrollOffsetX = 0f;
                LayoutTargets(false);
                WakePhysics();
                return;
            }
            switch (id)
            {
                case "act_snip":
                    StartScreenSnip();
                    break;
                case "act_paste":
                    PasteFromClipboard();
                    break;
                case "act_note":
                    PromptAndCreateNote(null);
                    break;
                case "act_cmr":
                    PullLatestCircleMixRecordings(true);
                    break;
                case "act_merge":
                    MergePinnedMediaWithFfmpeg();
                    break;
                case "act_agy":
                    BuildAntigravityContextBundle();
                    break;
                case "act_autohide":
                    autoHideMode = !autoHideMode;
                    ShowToast(autoHideMode ? "🧲 Otomatik Gizleme Açıldı (Fare Ayrılınca Kapanır)" : "📌 Sabit İp Modu Aktif");
                    SaveState();
                    break;
                case "act_hide":
                    CollapseBar();
                    break;
            }
        }

        void TriggerCardPrimaryAction(PinItem item)
        {
            if (item.Kind == PinKind.Image)
            {
                OpenRedlineEditor(item);
            }
            else if (item.Kind == PinKind.Video || item.Kind == PinKind.File)
            {
                if (!string.IsNullOrEmpty(item.SourcePath) && File.Exists(item.SourcePath))
                {
                    try { Process.Start(new ProcessStartInfo(item.SourcePath) { UseShellExecute = true }); } catch { }
                }
            }
            else if (item.Kind == PinKind.Note)
            {
                PromptAndCreateNote(item);
            }
        }

        void TriggerCardSecondaryAction(PinItem item)
        {
            if (item.Kind == PinKind.Video)
            {
                ExtractFrameFromVideoPin(item);
            }
            else if (item.Kind == PinKind.Image)
            {
                RunXRayOcrOnImagePin(item);
            }
            else if (item.Kind == PinKind.Note)
            {
                if (!string.IsNullOrEmpty(item.NoteText))
                {
                    try { Clipboard.SetText(item.NoteText); ShowToast("📋 Not Metni Panoya Kopyalandı!"); } catch { }
                }
            }
        }

        // --------------------------------------------------------------------
        // WORKFLOW 1: Circle Mix Record Live Watcher & Instant FFmpeg Merge
        // --------------------------------------------------------------------
        void SetupCircleMixRecordWatcher()
        {
            try
            {
                string cmrDir = @"C:\Users\zenci\Desktop\Circle_Mix_Record\Kayitlar_ve_Projeler";
                if (!Directory.Exists(cmrDir)) return;
                cmrWatcher = new FileSystemWatcher(cmrDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                cmrWatcher.Created += delegate (object s, FileSystemEventArgs e)
                {
                    string ext = (Path.GetExtension(e.FullPath) ?? "").ToLowerInvariant();
                    if (ext == ".mp4" || ext == ".png" || ext == ".jpg")
                    {
                        if (e.Name.StartsWith("_temp", StringComparison.OrdinalIgnoreCase)) return;
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            Thread.Sleep(900); // Wait for file write completion
                            try
                            {
                                if (IsHandleCreated)
                                    BeginInvoke((MethodInvoker)delegate { PinFileFromPath(e.FullPath, true); });
                            }
                            catch { }
                        });
                    }
                };
                cmrWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        void PullLatestCircleMixRecordings(bool feedback)
        {
            string cmrDir = @"C:\Users\zenci\Desktop\Circle_Mix_Record\Kayitlar_ve_Projeler";
            if (!Directory.Exists(cmrDir))
            {
                if (feedback) ShowToast("Circle Mix Record kayıt klasörü bulunamadı.");
                return;
            }

            try
            {
                string[] allFiles = Directory.GetFiles(cmrDir, "*.*", SearchOption.AllDirectories);
                List<FileInfo> candidates = new List<FileInfo>();
                for (int i = 0; i < allFiles.Length; i++)
                {
                    string f = allFiles[i];
                    string name = Path.GetFileName(f);
                    if (name.StartsWith("_temp", StringComparison.OrdinalIgnoreCase)) continue;
                    string ext = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                    if (ext == ".mp4" || ext == ".mkv" || ext == ".png")
                    {
                        candidates.Add(new FileInfo(f));
                    }
                }
                candidates.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

                int added = 0;
                for (int i = 0; i < candidates.Count && added < 3; i++)
                {
                    string p = candidates[i].FullName;
                    bool exists = false;
                    for (int j = 0; j < items.Count; j++)
                    {
                        if (string.Equals(items[j].SourcePath, p, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                    }
                    if (!exists)
                    {
                        PinFileFromPath(p, false);
                        added++;
                    }
                }
                if (feedback)
                {
                    ShowToast(added > 0
                        ? string.Format("🎬 Circle Mix Record'dan {0} Yeni Kayıt İpe Asıldı!", added)
                        : "🎬 Tüm Güncel Circle Mix Record Kayıtları Zaten İpte!");
                }
            }
            catch (Exception ex)
            {
                if (feedback) ShowToast("Kayıt çekme hatası: " + ex.Message);
            }
        }

        void ExtractFrameFromVideoPin(PinItem videoItem)
        {
            if (videoItem == null || string.IsNullOrEmpty(videoItem.SourcePath) || !File.Exists(videoItem.SourcePath))
                return;

            string outPng = Path.Combine(pinsDir, videoItem.Title + "_Kare_" + DateTime.Now.ToString("HHmmss") + ".png");
            if (ExtractVideoFrameWithFfmpeg(videoItem.SourcePath, 1.5, outPng) && File.Exists(outPng))
            {
                PinFileFromPath(outPng, false);
                ShowToast("📸 Videodan Net PNG Kare Söküldü ve İpe Mandallandı!");
            }
            else
            {
                ShowToast("FFmpeg ile kare sökülemedi.");
            }
        }

        void MergePinnedMediaWithFfmpeg()
        {
            string ffmpeg = FindFfmpegExe();
            if ( ffmpeg == null )
            {
                ShowToast("FFmpeg bulunamadı (Circle Mix Record klasörünü kontrol edin).");
                return;
            }

            List<PinItem> vis = GetVisibleItems();
            List<PinItem> chosen = new List<PinItem>();
            for (int i = 0; i < vis.Count; i++)
            {
                if (vis[i].Selected && (vis[i].Kind == PinKind.Video || vis[i].Kind == PinKind.Image))
                    chosen.Add(vis[i]);
            }
            if (chosen.Count == 0)
            {
                for (int i = 0; i < vis.Count && chosen.Count < 6; i++)
                {
                    if (vis[i].Kind == PinKind.Video || vis[i].Kind == PinKind.Image)
                        chosen.Add(vis[i]);
                }
            }

            if (chosen.Count == 0)
            {
                ShowToast("Birleştirmek için ipte en az 1 video veya görsel olmalı.");
                return;
            }

            ShowToast("⚡ İpteki " + chosen.Count + " Medya 60 FPS MP4 Olarak Birleştiriliyor...", 6.0);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string outDir = @"C:\Users\zenci\Desktop\Circle_Mix_Record\Kayitlar_ve_Projeler\Yatay_16x9_Kayitlar";
                    if (!Directory.Exists(outDir)) outDir = pinsDir;
                    string outMp4 = Path.Combine(outDir, "CircleLine_Stüdyo_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4");

                    StringBuilder inputs = new StringBuilder();
                    StringBuilder filter = new StringBuilder();
                    int validCount = 0;

                    for (int i = 0; i < chosen.Count; i++)
                    {
                        PinItem c = chosen[i];
                        string path = c.Kind == PinKind.Image ? c.CleanPngPath : c.SourcePath;
                        if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                        if (c.Kind == PinKind.Image)
                        {
                            inputs.AppendFormat("-loop 1 -t 2.5 -i \"{0}\" ", path);
                        }
                        else
                        {
                            inputs.AppendFormat("-i \"{0}\" ", path);
                        }
                        filter.AppendFormat("[{0}:v]scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=#140E38,setsar=1,fps=60,format=yuv420p[v{0}];", validCount);
                        validCount++;
                    }

                    if (validCount == 0) return;

                    for (int i = 0; i < validCount; i++) filter.AppendFormat("[v{0}]", i);
                    filter.AppendFormat("concat=n={0}:v=1:a=0[outv]", validCount);

                    string args = string.Format("-y -hide_banner -loglevel error {0} -filter_complex \"{1}\" -map \"[outv]\" -c:v libx264 -preset ultrafast -crf 20 \"{2}\"",
                        inputs.ToString(), filter.ToString(), outMp4);

                    ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, args)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using (Process p = Process.Start(psi))
                    {
                        p.WaitForExit(45000);
                    }

                    if (File.Exists(outMp4) && new FileInfo(outMp4).Length > 1024)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            PinVideoFile(outMp4, false);
                            ShowToast("✅ Birleştirilmiş MP4 Hazır ve İpe Mandallandı!");
                        });
                    }
                    else
                    {
                        BeginInvoke((MethodInvoker)delegate { ShowToast("Birleştirme tamamlanamadı."); });
                    }
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)delegate { ShowToast("FFmpeg hatası: " + ex.Message); });
                }
            });
        }

        // --------------------------------------------------------------------
        // WORKFLOW 2: Antigravity Smart Context Bundle (+ Live 3ds Max / AutoCAD Detection)
        // --------------------------------------------------------------------
        void BuildAntigravityContextBundle()
        {
            List<string> cadWindows = DetectOpenStudioWindows();
            List<PinItem> vis = GetVisibleItems();
            List<PinItem> selected = new List<PinItem>();
            for (int i = 0; i < vis.Count; i++)
            {
                if (vis[i].Selected) selected.Add(vis[i]);
            }
            if (selected.Count == 0)
            {
                for (int i = 0; i < Math.Min(5, vis.Count); i++) selected.Add(vis[i]);
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("### 🧺 CircleLine Stüdyo Görev Paketi (`by thezencirs`)");
            if (cadWindows.Count > 0)
            {
                sb.AppendLine("**Açık Stüdyo & CAD/3D Pencereleri:**");
                for (int i = 0; i < cadWindows.Count; i++)
                    sb.AppendLine("- `" + cadWindows[i] + "`");
                sb.AppendLine();
            }

            sb.AppendLine("**İpte Mandallı Referanslar, Dokular ve Notlar:**");
            for (int i = 0; i < selected.Count; i++)
            {
                PinItem it = selected[i];
                if (it.Kind == PinKind.Note)
                {
                    sb.AppendLine(string.Format("{0}. **[Not - {1}]**: {2}", i + 1, it.Title, it.NoteText));
                }
                else
                {
                    string path = (!string.IsNullOrEmpty(it.CleanPngPath) && File.Exists(it.CleanPngPath)) ? it.CleanPngPath : it.SourcePath;
                    string fileUrl = !string.IsNullOrEmpty(path) ? "file:///" + path.Replace('\\', '/') : "";
                    StringBuilder hexList = new StringBuilder();
                    if (it.Palette != null && it.Palette.Length > 0)
                    {
                        hexList.Append(" | Palet: ");
                        for (int c = 0; c < it.Palette.Length; c++)
                        {
                            if (c > 0) hexList.Append(", ");
                            hexList.AppendFormat("`#{0:X2}{1:X2}{2:X2}`", it.Palette[c].R, it.Palette[c].G, it.Palette[c].B);
                        }
                    }
                    sb.AppendLine(string.Format("{0}. **[{1}]** ({2}, {3}): [{4}]({5}){6}",
                        i + 1, it.Title, it.SourceFormatBadge, it.DimensionsText, Path.GetFileName(path ?? "dosya"), fileUrl, hexList.ToString()));
                    if (!string.IsNullOrEmpty(it.NoteText))
                        sb.AppendLine("   - *Mandal Notu:* " + it.NoteText);
                }
            }

            try
            {
                Clipboard.SetText(sb.ToString());
                ShowToast("🚀 Antigravity Görev Paketi Panoya Kopyalandı (Ctrl+V ile Yapıştırın)!", 3.5);
            }
            catch (Exception ex)
            {
                ShowToast("Pano hatası: " + ex.Message);
            }
        }

        static List<string> DetectOpenStudioWindows()
        {
            List<string> list = new List<string>();
            string[] keywords = new string[] { "3ds Max", "AutoCAD", "Revit", "SketchUp", "Blender", "Circle Mix Record", "Photoshop", "Unreal", "Corona" };
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                StringBuilder sb = new StringBuilder(512);
                if (GetWindowText(hWnd, sb, 512) > 0)
                {
                    string title = sb.ToString().Trim();
                    for (int i = 0; i < keywords.Length; i++)
                    {
                        if (title.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            list.Add(title);
                            break;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // --------------------------------------------------------------------
        // WORKFLOW 3: Screen Snipping (`Ctrl+Shift+X`), Redline & X-Ray OCR
        // --------------------------------------------------------------------
        void StartScreenSnip()
        {
            bool wasVisible = Visible;
            Hide();
            Thread.Sleep(140);
            try
            {
                using (SnipOverlayForm snip = new SnipOverlayForm())
                {
                    if (snip.ShowDialog() == DialogResult.OK && snip.CapturedBitmap != null)
                    {
                        Show();
                        ExpandBar();
                        PinBitmap(snip.CapturedBitmap, "Ekran Kesiti " + DateTime.Now.ToString("HH:mm:ss"), "KESİT");
                        snip.CapturedBitmap.Dispose();
                        return;
                    }
                }
            }
            finally
            {
                if (wasVisible) { Show(); ExpandBar(); }
            }
        }

        void OpenRedlineEditor(PinItem item)
        {
            string imgPath = (!string.IsNullOrEmpty(item.CleanPngPath) && File.Exists(item.CleanPngPath)) ? item.CleanPngPath : item.SourcePath;
            if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath)) return;

            using (RedlineEditorForm ed = new RedlineEditorForm(imgPath, item.Title, item.NoteText))
            {
                if (ed.ShowDialog(this) == DialogResult.OK && ed.EditedBitmap != null)
                {
                    string newPng = Path.Combine(pinsDir, "CircleLine_" + item.Id + "_redline.png");
                    ed.EditedBitmap.Save(newPng, ImageFormat.Png);
                    item.CleanPngPath = newPng;
                    item.SourceFormatBadge = "✏️ REDLINE";
                    item.Title = ed.EditedTitle;
                    item.NoteText = ed.EditedNote;
                    item.Palette = ExtractDominantPalette(ed.EditedBitmap, 5);
                    if (item.Thumb != null) item.Thumb.Dispose();
                    item.Thumb = CreateThumbnail(ed.EditedBitmap, 260, 200);
                    ed.EditedBitmap.Dispose();
                    item.AngularVel += 0.08;
                    SaveState();
                    ShowToast("✏️ Kırmızı Kalem (Redline) Çizimi Mandala Kaydedildi!");
                }
            }
        }

        void RunXRayOcrOnImagePin(PinItem item)
        {
            string imgPath = (!string.IsNullOrEmpty(item.CleanPngPath) && File.Exists(item.CleanPngPath)) ? item.CleanPngPath : item.SourcePath;
            if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath)) return;

            ShowToast("🔍 X-Ray: Görseldeki Yazı ve Ölçüler Okunuyor...", 4.0);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string extracted = RunWindowsOcrScript(imgPath);
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        try { Clipboard.SetText(extracted.Trim()); } catch { }
                        PinNote("X-Ray OCR: " + item.Title, extracted.Trim());
                        ShowToast("🔍 X-Ray OCR Metni Söküldü, Panoya Kopyalandı ve İpe Asıldı!");
                    }
                    else
                    {
                        string info = string.Format("Görsel: {0}\nBoyut: {1}\nDosya: {2}", item.Title, item.DimensionsText, imgPath);
                        try { Clipboard.SetText(info); } catch { }
                        ShowToast("Görselde okunabilir metin bulunamadı; dosya bilgileri panoya kopyalandı.");
                    }
                });
            });
        }

        static string RunWindowsOcrScript(string imagePath)
        {
            try
            {
                string ps = @"
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime]
function Await($op, $type) {
    $m = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.IsGenericMethod } | Select-Object -First 1
    $task = $m.MakeGenericMethod($type).Invoke($null, @($op))
    $task.Wait()
    return $task.Result
}
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync('" + imagePath.Replace("'", "''") + @"')) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bmp = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
$res = Await ($eng.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Write-Output $res.Text
";
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    return output != null ? output.Trim() : "";
                }
            }
            catch
            {
                return "";
            }
        }

        void PromptAndCreateNote(PinItem existing)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = existing == null ? "Yeni Mandal Notu Ekle — CircleLine" : "Mandal Notunu Düzenle — CircleLine";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.ClientSize = new Size(420, 260);
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.TopMost = true;
                dlg.BackColor = ColBgDeep;
                dlg.ForeColor = ColWhite;

                Label l1 = new Label { Text = "Mandal Başlığı:", Left = 14, Top = 14, Width = 140, ForeColor = ColWhite };
                TextBox tTitle = new TextBox { Left = 14, Top = 36, Width = 392, BackColor = ColScarlet, ForeColor = ColWhite, BorderStyle = BorderStyle.FixedSingle, Text = existing != null ? existing.Title : "Hızlı Stüdyo Notu" };

                Label l2 = new Label { Text = "Not / Komut / Ölçü Metni:", Left = 14, Top = 70, Width = 220, ForeColor = ColWhite };
                TextBox tBody = new TextBox { Left = 14, Top = 92, Width = 392, Height = 112, Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = ColScarlet, ForeColor = ColWhite, BorderStyle = BorderStyle.FixedSingle, Text = existing != null ? existing.NoteText : "" };

                Button btnOk = new Button { Text = "İpe Mandalla", Left = 296, Top = 216, Width = 110, Height = 30, BackColor = ColOrange, ForeColor = ColWhite, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
                Button btnCancel = new Button { Text = "İptal", Left = 206, Top = 216, Width = 82, Height = 30, BackColor = ColScarlet, ForeColor = ColWhite, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };

                dlg.Controls.AddRange(new Control[] { l1, tTitle, l2, tBody, btnOk, btnCancel });
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(tBody.Text))
                {
                    if (existing != null)
                    {
                        existing.Title = tTitle.Text.Trim();
                        existing.NoteText = tBody.Text.Trim();
                        existing.DimensionsText = existing.NoteText.Length + " karakter";
                        existing.AngularVel += 0.06;
                        SaveState();
                        WakePhysics();
                    }
                    else
                    {
                        PinNote(tTitle.Text.Trim(), tBody.Text.Trim());
                    }
                }
            }
        }

        // ====================================================================
        // CARD CONTEXT MENU & TRAY MENU
        // ====================================================================
        void ShowCardContextMenu(PinItem item, Point pt)
        {
            ContextMenuStrip cms = new ContextMenuStrip();
            cms.Items.Add("✏️ Aç / Kırmızı Kalem (Redline)", null, delegate { TriggerCardPrimaryAction(item); });

            if (item.Kind == PinKind.Image)
            {
                cms.Items.Add("🔍 X-Ray: Görseldeki Yazıları/Ölçüleri Sök (OCR)", null, delegate { RunXRayOcrOnImagePin(item); });
                cms.Items.Add("📋 Görseli Panoya Kopyala", null, delegate
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(item.CleanPngPath) && File.Exists(item.CleanPngPath))
                        {
                            using (Bitmap b = new Bitmap(item.CleanPngPath)) Clipboard.SetImage(b);
                            ShowToast("📋 Görsel Panoya Kopyalandı!");
                        }
                    }
                    catch { }
                });
            }
            if (item.Kind == PinKind.Video)
            {
                cms.Items.Add("📸 Videodan Net PNG Kare Sök ve İpe As", null, delegate { ExtractFrameFromVideoPin(item); });
            }

            cms.Items.Add("📂 Dosya Konumunu Explorer'da Göster", null, delegate
            {
                string p = (!string.IsNullOrEmpty(item.CleanPngPath) && File.Exists(item.CleanPngPath)) ? item.CleanPngPath : item.SourcePath;
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    Process.Start("explorer.exe", "/select,\"" + p + "\"");
            });

            ToolStripMenuItem moveMenu = new ToolStripMenuItem("🎡 Başka Makaraya Taşı");
            moveMenu.DropDownItems.Add("🎬 Circle Stüdyo", null, delegate { item.ReelIndex = 1; SaveState(); Invalidate(); });
            moveMenu.DropDownItems.Add("🏗️ 3ds Max & CAD", null, delegate { item.ReelIndex = 2; SaveState(); Invalidate(); });
            moveMenu.DropDownItems.Add("🤖 Antigravity & Not", null, delegate { item.ReelIndex = 3; SaveState(); Invalidate(); });
            cms.Items.Add(moveMenu);

            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add("🗑️ Mandalı İpten Kaldır", null, delegate { RemovePin(item); });
            cms.Show(PointToScreen(pt));
        }

        void BuildTrayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem header = new ToolStripMenuItem("CircleLine v" + Updater.CurrentText + " — by thezencirs") { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("🧺 İpi Göster / Aç (Ctrl+Shift+L)", null, delegate { Show(); ExpandBar(); });
            menu.Items.Add("✂️ Ekran Kesiti Al ve Mandalla (Ctrl+Shift+X)", null, delegate { StartScreenSnip(); });
            menu.Items.Add("📋 Panodakini İpe Mandalla (Ctrl+Shift+V)", null, delegate { ExpandBar(); PasteFromClipboard(); });
            menu.Items.Add("🎬 Circle Mix Record Son Kayıtları Çek", null, delegate { ExpandBar(); PullLatestCircleMixRecordings(true); });
            menu.Items.Add("🚀 Antigravity Görev Paketi Oluştur", null, delegate { ExpandBar(); BuildAntigravityContextBundle(); });
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem startupItem = new ToolStripMenuItem("Windows ile otomatik başlat") { Checked = IsStartupEnabled() };
            startupItem.Click += delegate
            {
                SetStartupEnabled(!startupItem.Checked);
                startupItem.Checked = IsStartupEnabled();
            };
            menu.Items.Add(startupItem);

            menu.Items.Add("🔄 Güncellemeleri denetle...", null, delegate { CheckForUpdatesInteractive(); });
            menu.Items.Add("ℹ️ Hakkında (thezencirs)", null, delegate
            {
                MessageBox.Show(
                    "CircleLine v" + Updater.CurrentText + " (by thezencirs)\n\n" +
                    "0-RAM Gerçek Sarkaç Fizikli Masaüstü İpi & Stüdyo Köprüsü\n" +
                    "• Circle Mix Record Canlı Film Şeridi & FFmpeg Birleştirici\n" +
                    "• 3ds Max / AutoCAD Otomatik PNG Dönüştürücü & 5'li Renk Paleti\n" +
                    "• Antigravity Tek Tıkla AI Görev Paketleyici\n" +
                    "• Kırmızı Kalem (Redline) & X-Ray OCR\n\n" +
                    "Geliştirici: thezencirs (https://github.com/thezencirs/CircleLine)\n" +
                    "Lisans: MIT License",
                    "CircleLine — by thezencirs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Çıkış", null, delegate { SaveState(); tray.Visible = false; Application.Exit(); });

            tray.ContextMenuStrip = menu;
        }

        static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(AppRunName) != null;
            }
            catch { return false; }
        }

        static void SetStartupEnabled(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enable) k.SetValue(AppRunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(AppRunName, false);
                }
            }
            catch { }
        }

        void CheckForUpdatesInteractive()
        {
            string err;
            Updater.Release rel = Updater.Fetch(out err);
            if (err != null)
            {
                MessageBox.Show(err, "CircleLine Güncelleme", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!Updater.IsNewer(rel))
            {
                MessageBox.Show("En güncel sürümü kullanıyorsunuz (v" + Updater.CurrentText + ").", "CircleLine Güncelleme", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("Yeni sürüm bulundu: " + rel.Tag + "\n\nŞimdi indirip güncellensin mi?", "CircleLine Güncelleme", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                string applyErr;
                if (Updater.Apply(rel, out applyErr)) Application.Exit();
                else MessageBox.Show("Güncelleme başarısız: " + applyErr, "CircleLine", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ====================================================================
        // IMAGE DECODING (WPF + GDI+), 5-COLOR PALETTE & FFMPEG HELPERS
        // ====================================================================
        static Bitmap DecodeAnyImage(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        fs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0];
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(frame);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        encoder.Save(ms);
                        ms.Position = 0;
                        using (Bitmap tmp = new Bitmap(ms))
                            return new Bitmap(tmp);
                    }
                }
            }
            catch
            {
                try
                {
                    using (Bitmap tmp = new Bitmap(path))
                        return new Bitmap(tmp);
                }
                catch { return null; }
            }
        }

        static Bitmap CreateThumbnail(Bitmap src, int maxW, int maxH)
        {
            float ratio = Math.Min((float)maxW / Math.Max(1, src.Width), (float)maxH / Math.Max(1, src.Height));
            int w = Math.Max(1, (int)(src.Width * ratio));
            int h = Math.Max(1, (int)(src.Height * ratio));
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(src, 0, 0, w, h);
            }
            return dst;
        }

        static Bitmap RenderFileBadgeThumb(string badge, string title)
        {
            Bitmap bmp = new Bitmap(240, 160, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(ColBgDeep);
                using (SolidBrush sb = new SolidBrush(ColScarlet))
                using (Pen op = new Pen(ColOrange, 2f))
                {
                    g.FillRectangle(sb, 20, 20, 200, 120);
                    g.DrawRectangle(op, 20, 20, 200, 120);
                }
                using (Font f = new Font("Segoe UI Black", 16f))
                using (SolidBrush ob = new SolidBrush(ColOrange))
                {
                    SizeF sz = g.MeasureString(badge, f);
                    g.DrawString(badge, f, ob, (240 - sz.Width) / 2f, 52f);
                }
            }
            return bmp;
        }

        static Color[] ExtractDominantPalette(Bitmap bmp, int count)
        {
            try
            {
                Dictionary<int, int> buckets = new Dictionary<int, int>();
                Dictionary<int, int[]> sums = new Dictionary<int, int[]>();

                using (Bitmap small = new Bitmap(bmp, new Size(48, 48)))
                {
                    for (int y = 0; y < small.Height; y++)
                    {
                        for (int x = 0; x < small.Width; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            if (c.A < 80) continue;
                            int key = ((c.R >> 5) << 6) | ((c.G >> 5) << 3) | (c.B >> 5);
                            int cnt;
                            if (!buckets.TryGetValue(key, out cnt))
                            {
                                buckets[key] = 1;
                                sums[key] = new int[] { c.R, c.G, c.B, 1 };
                            }
                            else
                            {
                                buckets[key] = cnt + 1;
                                int[] s = sums[key];
                                s[0] += c.R; s[1] += c.G; s[2] += c.B; s[3]++;
                            }
                        }
                    }
                }

                List<KeyValuePair<int, int>> sorted = new List<KeyValuePair<int, int>>(buckets);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

                List<Color> result = new List<Color>();
                for (int i = 0; i < sorted.Count && result.Count < count; i++)
                {
                    int[] s = sums[sorted[i].Key];
                    Color candidate = Color.FromArgb(s[0] / s[3], s[1] / s[3], s[2] / s[3]);
                    bool tooClose = false;
                    for (int j = 0; j < result.Count; j++)
                    {
                        int dr = candidate.R - result[j].R;
                        int dg = candidate.G - result[j].G;
                        int db = candidate.B - result[j].B;
                        if (dr * dr + dg * dg + db * db < 1600) { tooClose = true; break; }
                    }
                    if (!tooClose) result.Add(candidate);
                }
                while (result.Count < count && sorted.Count > result.Count)
                {
                    int[] s = sums[sorted[result.Count].Key];
                    result.Add(Color.FromArgb(s[0] / s[3], s[1] / s[3], s[2] / s[3]));
                }
                return result.ToArray();
            }
            catch
            {
                return new Color[] { ColOrange, ColScarlet, ColWhite };
            }
        }

        static string FindFfmpegExe()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"CircleRender\bin\ffmpeg.exe"),
                @"C:\Users\zenci\AppData\Local\CircleRender\bin\ffmpeg.exe",
                @"C:\Users\zenci\Desktop\Circle_Mix_Record\ffmpeg.exe",
                @"C:\Users\zenci\.gemini\antigravity\scratch\CircleMixRecord\bin\Debug\net8.0-windows\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe"
            };
            for (int i = 0; i < candidates.Length; i++)
                if (File.Exists(candidates[i])) return candidates[i];

            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] parts = pathEnv.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string p = Path.Combine(parts[i].Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        static bool ExtractVideoFrameWithFfmpeg(string videoPath, double sec, string outputPng)
        {
            string ffmpeg = FindFfmpegExe();
            if (ffmpeg == null) return false;
            try
            {
                string args = string.Format(CultureInfo.InvariantCulture,
                    "-y -hide_banner -loglevel error -ss {0:F2} -i \"{1}\" -frames:v 1 -q:v 2 \"{2}\"",
                    Math.Max(0.0, sec), videoPath, outputPng);
                ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(10000);
                }
                return File.Exists(outputPng) && new FileInfo(outputPng).Length > 256;
            }
            catch { return false; }
        }

        static string FormatBytes(long b)
        {
            if (b >= 1024 * 1024) return string.Format("{0:F1} MB", b / (1024.0 * 1024.0));
            if (b >= 1024) return string.Format("{0:F0} KB", b / 1024.0);
            return b + " B";
        }

        // ====================================================================
        // STATE PERSISTENCE & STARTER DEMO PINS
        // ====================================================================
        void SeedStarterPins()
        {
            // 1. Pull latest Circle Mix Record video/frame if available
            PullLatestCircleMixRecordings(false);

            // 2. Create a crisp architectural/studio sample swatch card so user sees the 5-color palette & pendulum immediately
            using (Bitmap demoBmp = new Bitmap(360, 240, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(demoBmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(ColBgDeep);
                using (SolidBrush b1 = new SolidBrush(ColScarlet)) g.FillRectangle(b1, 18, 18, 150, 120);
                using (SolidBrush b2 = new SolidBrush(ColOrange)) g.FillEllipse(b2, 195, 24, 130, 130);
                using (SolidBrush b3 = new SolidBrush(Color.FromArgb(46, 196, 182))) g.FillRectangle(b3, 24, 152, 140, 68);
                using (SolidBrush b4 = new SolidBrush(Color.FromArgb(255, 209, 102))) g.FillRectangle(b4, 182, 166, 150, 54);
                using (Font f = new Font("Segoe UI Black", 13f))
                using (SolidBrush wb = new SolidBrush(ColWhite))
                {
                    g.DrawString("3DS MAX & CAD", f, wb, 26, 55);
                    g.DrawString("DOKU & PALET", f, wb, 26, 82);
                }
                PinBitmap(demoBmp, "Stüdyo Renk & Doku Karti", "PBR→PNG");
            }

            // 3. Add a helpful starter note
            PinNote("CircleLine Kısayollar",
                "• Aşağı Sürükle: 3ds Max / AutoCAD'e .PNG bırak\n" +
                "• Renk Kutuları: Tıkla #HEX / Sağ Tık RGB\n" +
                "• Ctrl+Shift+X: Ekran Kesiti Mandalla\n" +
                "• Çift Tık: Kırmızı Kalem (Redline)");
        }

        void SaveState()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("AUTOHIDE=" + (autoHideMode ? "1" : "0"));
                for (int i = 0; i < items.Count; i++)
                {
                    PinItem it = items[i];
                    StringBuilder palStr = new StringBuilder();
                    if (it.Palette != null)
                    {
                        for (int c = 0; c < it.Palette.Length; c++)
                        {
                            if (c > 0) palStr.Append(",");
                            palStr.AppendFormat("{0:X2}{1:X2}{2:X2}", it.Palette[c].R, it.Palette[c].G, it.Palette[c].B);
                        }
                    }
                    string line = string.Join("\t", new string[]
                    {
                        it.Id ?? "",
                        ((int)it.Kind).ToString(),
                        it.ReelIndex.ToString(),
                        EscapeField(it.Title),
                        EscapeField(it.NoteText),
                        EscapeField(it.SourcePath),
                        EscapeField(it.CleanPngPath),
                        EscapeField(it.SourceFormatBadge),
                        EscapeField(it.DimensionsText),
                        palStr.ToString()
                    });
                    sb.AppendLine("ITEM=" + line);
                }
                File.WriteAllText(stateFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        void LoadState()
        {
            if (!File.Exists(stateFile)) return;
            try
            {
                string[] lines = File.ReadAllLines(stateFile, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    if (ln.StartsWith("AUTOHIDE="))
                    {
                        autoHideMode = ln.Substring(9).Trim() == "1";
                    }
                    else if (ln.StartsWith("ITEM="))
                    {
                        string[] parts = ln.Substring(5).Split('\t');
                        if (parts.Length < 10) continue;
                        PinItem it = new PinItem
                        {
                            Id = parts[0],
                            Kind = (PinKind)int.Parse(parts[1]),
                            ReelIndex = int.Parse(parts[2]),
                            Title = UnescapeField(parts[3]),
                            NoteText = UnescapeField(parts[4]),
                            SourcePath = UnescapeField(parts[5]),
                            CleanPngPath = UnescapeField(parts[6]),
                            SourceFormatBadge = UnescapeField(parts[7]),
                            DimensionsText = UnescapeField(parts[8]),
                            PopScale = 1.0f
                        };
                        if (!string.IsNullOrEmpty(parts[9]))
                        {
                            string[] hexes = parts[9].Split(',');
                            List<Color> cols = new List<Color>();
                            for (int c = 0; c < hexes.Length; c++)
                            {
                                if (hexes[c].Length == 6)
                                {
                                    int rgb = Convert.ToInt32(hexes[c], 16);
                                    cols.Add(Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF));
                                }
                            }
                            it.Palette = cols.ToArray();
                        }

                        string thumbFile = (!string.IsNullOrEmpty(it.CleanPngPath) && File.Exists(it.CleanPngPath)) ? it.CleanPngPath : it.SourcePath;
                        if ((it.Kind == PinKind.Image || it.Kind == PinKind.Video) && !string.IsNullOrEmpty(thumbFile) && File.Exists(thumbFile))
                        {
                            Bitmap b = DecodeAnyImage(thumbFile);
                            if (b != null)
                            {
                                it.Thumb = CreateThumbnail(b, 260, 200);
                                b.Dispose();
                            }
                        }
                        if (it.Thumb == null && it.Kind != PinKind.Note)
                        {
                            it.Thumb = RenderFileBadgeThumb(it.SourceFormatBadge ?? "DOSYA", it.Title);
                        }
                        items.Add(it);
                    }
                }
            }
            catch { }
        }

        static string EscapeField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n");
        }

        static string UnescapeField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\n", "\r\n").Replace("\\t", "\t").Replace("\\\\", "\\");
        }
    }

    // ========================================================================
    // SCREEN SNIP OVERLAY (`Ctrl + Shift + X`)
    // ========================================================================
    class SnipOverlayForm : Form
    {
        public Bitmap CapturedBitmap;
        readonly Bitmap screenSnap;
        Point startPt;
        Rectangle selRect;
        bool dragging;

        public SnipOverlayForm()
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            screenSnap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(screenSnap))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            KeyPreview = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); return; }
            dragging = true;
            startPt = e.Location;
            selRect = new Rectangle(e.Location, Size.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            selRect = new Rectangle(
                Math.Min(startPt.X, e.X),
                Math.Min(startPt.Y, e.Y),
                Math.Abs(e.X - startPt.X),
                Math.Abs(e.Y - startPt.Y));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            if (selRect.Width > 8 && selRect.Height > 8)
            {
                CapturedBitmap = new Bitmap(selRect.Width, selRect.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(CapturedBitmap))
                {
                    g.DrawImage(screenSnap, new Rectangle(0, 0, selRect.Width, selRect.Height), selRect, GraphicsUnit.Pixel);
                }
                DialogResult = DialogResult.OK;
            }
            else
            {
                DialogResult = DialogResult.Cancel;
            }
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.DrawImageUnscaled(screenSnap, 0, 0);
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, 20, 14, 56)))
            {
                g.FillRectangle(dim, ClientRectangle);
            }
            if (selRect.Width > 0 && selRect.Height > 0)
            {
                g.DrawImage(screenSnap, selRect, selRect, GraphicsUnit.Pixel);
                using (Pen p = new Pen(Color.FromArgb(239, 95, 24), 2.5f))
                {
                    g.DrawRectangle(p, selRect);
                }
                using (Font f = new Font("Segoe UI Bold", 9.5f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(239, 95, 24)))
                using (SolidBrush w = new SolidBrush(Color.White))
                {
                    string dimTxt = selRect.Width + " × " + selRect.Height + " px  (Bırakınca İpe Mandallanır)";
                    g.FillRectangle(b, selRect.X, Math.Max(0, selRect.Y - 24), 270, 22);
                    g.DrawString(dimTxt, f, w, selRect.X + 6, Math.Max(0, selRect.Y - 22));
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && screenSnap != null) screenSnap.Dispose();
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // QUICK REDLINE & ANNOTATION STUDIO (`✏️ Kırmızı Kalem`)
    // ========================================================================
    class RedlineEditorForm : Form
    {
        public Bitmap EditedBitmap;
        public string EditedTitle;
        public string EditedNote;

        readonly Bitmap canvasBmp;
        readonly PictureBox pic;
        readonly TextBox txtTitle;
        readonly TextBox txtNote;
        int toolMode = 0; // 0=Arrow, 1=Box, 2=Pen, 3=Text
        Point ptStart, ptCur;
        bool drawing;

        public RedlineEditorForm(string imagePath, string title, string note)
        {
            Text = "CircleLine — Kırmızı Kalem (Redline) & Mandal Düzenleyici";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(920, 680);
            BackColor = Color.FromArgb(20, 14, 56);
            ForeColor = Color.White;
            TopMost = true;

            using (Bitmap src = new Bitmap(imagePath))
            {
                canvasBmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(canvasBmp))
                    g.DrawImage(src, 0, 0, src.Width, src.Height);
            }

            Panel topBar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(28, 19, 76) };
            Button bArrow = MakeToolBtn("➔ Ok Çiz", 12, delegate { toolMode = 0; });
            Button bBox = MakeToolBtn("▭ Kutu Çiz", 104, delegate { toolMode = 1; });
            Button bPen = MakeToolBtn("✎ Serbest Kalem", 196, delegate { toolMode = 2; });
            Button bText = MakeToolBtn("T Etiket Yaz", 312, delegate { toolMode = 3; });

            Label lblT = new Label { Text = "Başlık:", Left = 420, Top = 14, Width = 45, ForeColor = Color.White };
            txtTitle = new TextBox { Left = 468, Top = 11, Width = 160, Text = title ?? "", BackColor = Color.FromArgb(38, 26, 102), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };

            Label lblN = new Label { Text = "Mandal Notu:", Left = 636, Top = 14, Width = 80, ForeColor = Color.White };
            txtNote = new TextBox { Left = 718, Top = 11, Width = 170, Text = note ?? "", BackColor = Color.FromArgb(38, 26, 102), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };

            topBar.Controls.AddRange(new Control[] { bArrow, bBox, bPen, bText, lblT, txtTitle, lblN, txtNote });

            Panel bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.FromArgb(28, 19, 76) };
            Button btnSave = new Button { Text = "💾 Mandala Kaydet", Width = 150, Height = 32, Left = 735, Top = 7, BackColor = Color.FromArgb(239, 95, 24), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnSave.Click += delegate
            {
                EditedBitmap = new Bitmap(canvasBmp);
                EditedTitle = txtTitle.Text.Trim();
                EditedNote = txtNote.Text.Trim();
                DialogResult = DialogResult.OK;
                Close();
            };
            bottomBar.Controls.Add(btnSave);

            pic = new PictureBox { Dock = DockStyle.Fill, Image = canvasBmp, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(20, 14, 56), Cursor = Cursors.Cross };
            pic.MouseDown += Pic_MouseDown;
            pic.MouseMove += Pic_MouseMove;
            pic.MouseUp += Pic_MouseUp;
            pic.Paint += Pic_Paint;

            Controls.Add(pic);
            Controls.Add(topBar);
            Controls.Add(bottomBar);
        }

        static Button MakeToolBtn(string text, int x, EventHandler onClick)
        {
            Button b = new Button { Text = text, Left = x, Top = 8, Width = 88, Height = 28, BackColor = Color.FromArgb(38, 26, 102), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            b.Click += onClick;
            return b;
        }

        static string PromptLabelDialog(string prompt, string defVal)
        {
            using (Form f = new Form())
            {
                f.Text = "CircleLine Etiket";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(340, 120);
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.TopMost = true;
                f.BackColor = Color.FromArgb(20, 14, 56);
                f.ForeColor = Color.White;
                Label l = new Label { Text = prompt, Left = 12, Top = 12, Width = 310, ForeColor = Color.White };
                TextBox t = new TextBox { Text = defVal, Left = 12, Top = 38, Width = 314, BackColor = Color.FromArgb(38, 26, 102), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
                Button ok = new Button { Text = "Ekle", Left = 246, Top = 74, Width = 80, Height = 28, BackColor = Color.FromArgb(239, 95, 24), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK };
                f.Controls.AddRange(new Control[] { l, t, ok });
                f.AcceptButton = ok;
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }

        Point MapToBitmap(Point p)
        {
            if (pic.Width <= 0 || pic.Height <= 0) return Point.Empty;
            float ratio = Math.Min((float)pic.Width / canvasBmp.Width, (float)pic.Height / canvasBmp.Height);
            float dispW = canvasBmp.Width * ratio;
            float dispH = canvasBmp.Height * ratio;
            float offX = (pic.Width - dispW) / 2f;
            float offY = (pic.Height - dispH) / 2f;
            int bx = (int)((p.X - offX) / ratio);
            int by = (int)((p.Y - offY) / ratio);
            return new Point(Math.Max(0, Math.Min(canvasBmp.Width - 1, bx)), Math.Max(0, Math.Min(canvasBmp.Height - 1, by)));
        }

        void Pic_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ptStart = MapToBitmap(e.Location);
            ptCur = ptStart;
            if (toolMode == 3)
            {
                string label = PromptLabelDialog("Görsel üzerine yazılacak not / ölçü:", "Ölçü / Not");
                if (!string.IsNullOrWhiteSpace(label))
                {
                    using (Graphics g = Graphics.FromImage(canvasBmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        float fontSize = Math.Max(14f, canvasBmp.Width / 45f);
                        using (Font f = new Font("Segoe UI Bold", fontSize))
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(225, 20, 14, 56)))
                        using (SolidBrush fg = new SolidBrush(Color.FromArgb(239, 95, 24)))
                        {
                            SizeF sz = g.MeasureString(label, f);
                            g.FillRectangle(bg, ptStart.X, ptStart.Y, sz.Width + 12, sz.Height + 6);
                            g.DrawString(label, f, fg, ptStart.X + 6, ptStart.Y + 3);
                        }
                    }
                    pic.Invalidate();
                }
                return;
            }
            drawing = true;
        }

        void Pic_MouseMove(object sender, MouseEventArgs e)
        {
            if (!drawing) return;
            Point next = MapToBitmap(e.Location);
            if (toolMode == 2)
            {
                using (Graphics g = Graphics.FromImage(canvasBmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float pw = Math.Max(3f, canvasBmp.Width / 220f);
                    using (Pen pen = new Pen(Color.FromArgb(239, 95, 24), pw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(pen, ptCur, next);
                    }
                }
            }
            ptCur = next;
            pic.Invalidate();
        }

        void Pic_MouseUp(object sender, MouseEventArgs e)
        {
            if (!drawing) return;
            drawing = false;
            using (Graphics g = Graphics.FromImage(canvasBmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float pw = Math.Max(4f, canvasBmp.Width / 180f);
                using (Pen pen = new Pen(Color.FromArgb(239, 95, 24), pw))
                {
                    if (toolMode == 0)
                    {
                        pen.CustomEndCap = new AdjustableArrowCap(6f, 6f, true);
                        g.DrawLine(pen, ptStart, ptCur);
                    }
                    else if (toolMode == 1)
                    {
                        Rectangle r = new Rectangle(Math.Min(ptStart.X, ptCur.X), Math.Min(ptStart.Y, ptCur.Y), Math.Abs(ptCur.X - ptStart.X), Math.Abs(ptCur.Y - ptStart.Y));
                        g.DrawRectangle(pen, r);
                    }
                }
            }
            pic.Invalidate();
        }

        void Pic_Paint(object sender, PaintEventArgs e)
        {
            if (!drawing || (toolMode != 0 && toolMode != 1)) return;
            using (Pen p = new Pen(Color.FromArgb(239, 95, 24), 2.5f))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                // Preview line on PictureBox
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\CircleLineByThezencirs_Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                CircleLineForm form = new CircleLineForm();
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (File.Exists(args[i]))
                            form.PinFileFromPath(args[i], true);
                    }
                }
                Application.Run(form);
            }
        }
    }
}
