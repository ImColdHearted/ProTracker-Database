namespace Foot_Tracker.Forms.Counterparts
{
    partial class Counterparts
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
            flpCounterparts = new SmoothFlowLayoutPanel();
            lblTitle = new Label();
            SuspendLayout();
            // 
            // flpCounterparts
            // 
            flpCounterparts.AutoScroll = true;
            flpCounterparts.Location = new Point(12, 31);
            flpCounterparts.Name = "flpCounterparts";
            flpCounterparts.Size = new Size(1049, 771);
            flpCounterparts.TabIndex = 0;
            // 
            // lblTitle
            // 
            lblTitle.Anchor = AnchorStyles.None;
            lblTitle.Font = new Font("Segoe UI", 11F, FontStyle.Bold | FontStyle.Underline);
            lblTitle.ForeColor = Color.White;
            lblTitle.Location = new Point(1, 2);
            lblTitle.Name = "lblTitle";
            lblTitle.Size = new Size(1024, 26);
            lblTitle.TabIndex = 0;
            lblTitle.Text = "Pokemon Counterparts";
            lblTitle.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // Counterparts
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.Black;
            BackgroundImageLayout = ImageLayout.Stretch;
            ClientSize = new Size(1037, 795);
            Controls.Add(lblTitle);
            Controls.Add(flpCounterparts);
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Name = "Counterparts";
            Text = "Counterparts";
            Load += Counterparts_Load;
            ResumeLayout(false);
        }

        #endregion

        private SmoothFlowLayoutPanel flpCounterparts;
        private Label lblTitle;
    }
}