using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace Foot_Tracker
{
    public class DoubleBufferedFlowLayoutPanel : FlowLayoutPanel
    {
        public DoubleBufferedFlowLayoutPanel()
        {
            DoubleBuffered = true;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true
            );

            UpdateStyles();
        }
    }
    public class SmoothFlowLayoutPanel : FlowLayoutPanel
    {
        const int SB_VERT = 1;
        const int SB_HORZ = 0;

        [DllImport("user32.dll")]
        static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            ShowScrollBar(this.Handle, SB_VERT, false);
            ShowScrollBar(this.Handle, SB_HORZ, false);
        }
    }
}