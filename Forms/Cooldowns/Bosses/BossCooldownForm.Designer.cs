namespace Foot_Tracker.Forms.Cooldowns.Bosses
{
    partial class BossCooldownForm
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
            BossGrid = new TableLayoutPanel();
            BossPanel = new Panel();
            BossTimer = new Label();
            BossLabel = new Label();
            BossPicture = new PictureBox();
            label1 = new Label();
            label4 = new Label();
            BossGrid.SuspendLayout();
            BossPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)BossPicture).BeginInit();
            SuspendLayout();
            // 
            // BossGrid
            // 
            BossGrid.BackColor = Color.Transparent;
            BossGrid.ColumnCount = 5;
            BossGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.666666F));
            BossGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.666666F));
            BossGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.666666F));
            BossGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.666666F));
            BossGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.666666F));
            BossGrid.Controls.Add(BossPanel, 0, 0);
            BossGrid.Location = new Point(5, 48);
            BossGrid.Name = "BossGrid";
            BossGrid.RowCount = 12;
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 7.142856F));
            BossGrid.Size = new Size(703, 612);
            BossGrid.TabIndex = 0;
            // 
            // BossPanel
            // 
            BossPanel.Controls.Add(BossTimer);
            BossPanel.Controls.Add(BossLabel);
            BossPanel.Controls.Add(BossPicture);
            BossPanel.Location = new Point(3, 3);
            BossPanel.Name = "BossPanel";
            BossPanel.Size = new Size(133, 45);
            BossPanel.TabIndex = 0;
            BossPanel.Click += BossCooldownCard_Click;
            BossPanel.Paint += panel1_Paint;
            // 
            // BossTimer
            // 
            BossTimer.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            BossTimer.Location = new Point(38, 22);
            BossTimer.Name = "BossTimer";
            BossTimer.Size = new Size(105, 23);
            BossTimer.TabIndex = 2;
            BossTimer.Text = "00:00:00";
            BossTimer.TextAlign = ContentAlignment.MiddleCenter;
            BossTimer.Click += label3_Click;
            // 
            // BossLabel
            // 
            BossLabel.Font = new Font("Segoe UI", 7F, FontStyle.Bold);
            BossLabel.Location = new Point(38, 3);
            BossLabel.Name = "BossLabel";
            BossLabel.Size = new Size(105, 23);
            BossLabel.TabIndex = 1;
            BossLabel.Text = "Boss Label";
            BossLabel.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // BossPicture
            // 
            BossPicture.Location = new Point(4, 3);
            BossPicture.Name = "BossPicture";
            BossPicture.Size = new Size(28, 42);
            BossPicture.SizeMode = PictureBoxSizeMode.Zoom;
            BossPicture.TabIndex = 0;
            BossPicture.TabStop = false;
            BossPicture.Click += pictureBox1_Click;
            // 
            // label1
            // 
            label1.Font = new Font("Segoe UI", 12F, FontStyle.Bold | FontStyle.Underline);
            label1.Location = new Point(12, 13);
            label1.Name = "label1";
            label1.Size = new Size(395, 23);
            label1.TabIndex = 1;
            label1.Text = "Boss Cooldowns";
            label1.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // label4
            // 
            label4.Location = new Point(413, 9);
            label4.Name = "label4";
            label4.Size = new Size(203, 23);
            label4.TabIndex = 2;
            label4.Text = "Time Format = Days/Hours/Minutes";
            // 
            // BossCooldownForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.MidnightBlue;
            ClientSize = new Size(713, 665);
            Controls.Add(label4);
            Controls.Add(label1);
            Controls.Add(BossGrid);
            ForeColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Name = "BossCooldownForm";
            Text = "BossCooldownForm";
            BossGrid.ResumeLayout(false);
            BossPanel.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)BossPicture).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private TableLayoutPanel BossGrid;
        private Label label1;
        private Panel BossPanel;
        private PictureBox BossPicture;
        private Label BossLabel;
        private Label BossTimer;
        private Label label4;
    }
}