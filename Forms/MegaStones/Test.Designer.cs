namespace Foot_Tracker.Forms.Guides.MegaStones
{
    partial class Test
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            smoothFlowLayoutPanel1 = new SmoothFlowLayoutPanel();
            webGuide = new Microsoft.Web.WebView2.WinForms.WebView2();
            smoothFlowLayoutPanel1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)webGuide).BeginInit();
            SuspendLayout();
            // 
            // smoothFlowLayoutPanel1
            // 
            smoothFlowLayoutPanel1.AutoScroll = true;
            smoothFlowLayoutPanel1.Controls.Add(webGuide);
            smoothFlowLayoutPanel1.Dock = DockStyle.Fill;
            smoothFlowLayoutPanel1.Location = new Point(0, 0);
            smoothFlowLayoutPanel1.Name = "smoothFlowLayoutPanel1";
            smoothFlowLayoutPanel1.Size = new Size(784, 785);
            smoothFlowLayoutPanel1.TabIndex = 0;
            smoothFlowLayoutPanel1.WrapContents = false;
            // 
            // webGuide
            // 
            webGuide.AllowExternalDrop = true;
            webGuide.Anchor = AnchorStyles.None;
            webGuide.CreationProperties = null;
            webGuide.DefaultBackgroundColor = Color.White;
            webGuide.Location = new Point(3, 3);
            webGuide.Name = "webGuide";
            webGuide.Size = new Size(778, 777);
            webGuide.TabIndex = 0;
            webGuide.Tag = "webGuide";
            webGuide.ZoomFactor = 1D;
            // 
            // Test
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.Black;
            ClientSize = new Size(784, 785);
            Controls.Add(smoothFlowLayoutPanel1);
            ForeColor = Color.White;
            Name = "Test";
            Text = "MegaStones";
            Load += MegaStones_Load;
            smoothFlowLayoutPanel1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)webGuide).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private SmoothFlowLayoutPanel smoothFlowLayoutPanel1;
        private Microsoft.Web.WebView2.WinForms.WebView2 webGuide;
    }
}