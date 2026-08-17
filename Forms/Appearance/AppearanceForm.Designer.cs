namespace Foot_Tracker
{
    partial class AppearanceForm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(AppearanceForm));
            SaveButton = new Button();
            ClearButton = new Button();
            CustomBackgroundBox = new Panel();
            SelectCustomImageButton = new Button();
            Custom = new Label();
            BorderColorButton = new Button();
            TextColorButton = new Button();
            tableLayoutPanel1 = new TableLayoutPanel();
            panel6 = new Panel();
            label8 = new Label();
            BackgroundChoice6 = new PictureBox();
            panel5 = new Panel();
            label7 = new Label();
            BackgroundChoice5 = new PictureBox();
            panel4 = new Panel();
            label6 = new Label();
            BackgroundChoice4 = new PictureBox();
            panel3 = new Panel();
            label5 = new Label();
            BackgroundChoice3 = new PictureBox();
            panel2 = new Panel();
            Bloodred = new Label();
            BackgroundChoice2 = new PictureBox();
            panel1 = new Panel();
            label1 = new Label();
            BackgroundChoice1 = new PictureBox();
            PreviewPanel = new Panel();
            PreviewPictureBox2 = new PictureBox();
            PreviewPictureBox1 = new PictureBox();
            PreviewTitleLabel = new Label();
            CustomBackgroundBox.SuspendLayout();
            tableLayoutPanel1.SuspendLayout();
            panel6.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice6).BeginInit();
            panel5.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice5).BeginInit();
            panel4.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice4).BeginInit();
            panel3.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice3).BeginInit();
            panel2.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice2).BeginInit();
            panel1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice1).BeginInit();
            PreviewPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)PreviewPictureBox2).BeginInit();
            ((System.ComponentModel.ISupportInitialize)PreviewPictureBox1).BeginInit();
            SuspendLayout();
            // 
            // SaveButton
            // 
            SaveButton.BackColor = Color.White;
            SaveButton.Location = new Point(325, 520);
            SaveButton.Name = "SaveButton";
            SaveButton.Size = new Size(121, 23);
            SaveButton.TabIndex = 3;
            SaveButton.Text = "Save";
            SaveButton.UseVisualStyleBackColor = false;
            SaveButton.Click += SaveButton_Click;
            // 
            // ClearButton
            // 
            ClearButton.BackColor = Color.White;
            ClearButton.Location = new Point(325, 491);
            ClearButton.Name = "ClearButton";
            ClearButton.Size = new Size(121, 23);
            ClearButton.TabIndex = 4;
            ClearButton.Text = "Clear";
            ClearButton.UseVisualStyleBackColor = false;
            ClearButton.Click += ClearBackgroundButton_Click;
            // 
            // CustomBackgroundBox
            // 
            CustomBackgroundBox.BackColor = Color.Transparent;
            CustomBackgroundBox.Controls.Add(SelectCustomImageButton);
            CustomBackgroundBox.Controls.Add(Custom);
            CustomBackgroundBox.Location = new Point(12, 235);
            CustomBackgroundBox.Name = "CustomBackgroundBox";
            CustomBackgroundBox.Size = new Size(438, 96);
            CustomBackgroundBox.TabIndex = 6;
            // 
            // SelectCustomImageButton
            // 
            SelectCustomImageButton.Location = new Point(160, 47);
            SelectCustomImageButton.Name = "SelectCustomImageButton";
            SelectCustomImageButton.Size = new Size(112, 23);
            SelectCustomImageButton.TabIndex = 5;
            SelectCustomImageButton.Text = "Select Image";
            SelectCustomImageButton.UseVisualStyleBackColor = true;
            SelectCustomImageButton.Click += SelectCustomImageButton_Click;
            // 
            // Custom
            // 
            Custom.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            Custom.ForeColor = Color.White;
            Custom.Location = new Point(7, 6);
            Custom.Name = "Custom";
            Custom.Size = new Size(425, 28);
            Custom.TabIndex = 1;
            Custom.Text = "Custom Background";
            Custom.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // BorderColorButton
            // 
            BorderColorButton.BackColor = Color.White;
            BorderColorButton.Location = new Point(12, 491);
            BorderColorButton.Name = "BorderColorButton";
            BorderColorButton.Size = new Size(135, 23);
            BorderColorButton.TabIndex = 7;
            BorderColorButton.Text = "Change Border Color";
            BorderColorButton.UseVisualStyleBackColor = false;
            BorderColorButton.Click += BorderColorButton_Click;
            // 
            // TextColorButton
            // 
            TextColorButton.BackColor = Color.White;
            TextColorButton.Location = new Point(12, 520);
            TextColorButton.Name = "TextColorButton";
            TextColorButton.Size = new Size(135, 23);
            TextColorButton.TabIndex = 8;
            TextColorButton.Text = "Change Text Color";
            TextColorButton.UseVisualStyleBackColor = false;
            TextColorButton.Click += TextColorButton_Click;
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.BackColor = Color.Transparent;
            tableLayoutPanel1.ColumnCount = 3;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333321F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333321F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333321F));
            tableLayoutPanel1.Controls.Add(panel6, 2, 1);
            tableLayoutPanel1.Controls.Add(panel5, 1, 1);
            tableLayoutPanel1.Controls.Add(panel4, 0, 1);
            tableLayoutPanel1.Controls.Add(panel3, 2, 0);
            tableLayoutPanel1.Controls.Add(panel2, 1, 0);
            tableLayoutPanel1.Controls.Add(panel1, 0, 0);
            tableLayoutPanel1.Location = new Point(12, 12);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 2;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            tableLayoutPanel1.Size = new Size(438, 221);
            tableLayoutPanel1.TabIndex = 10;
            // 
            // panel6
            // 
            panel6.Controls.Add(label8);
            panel6.Controls.Add(BackgroundChoice6);
            panel6.Location = new Point(293, 113);
            panel6.Name = "panel6";
            panel6.Size = new Size(138, 102);
            panel6.TabIndex = 5;
            // 
            // label8
            // 
            label8.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            label8.ForeColor = Color.White;
            label8.Location = new Point(20, 68);
            label8.Name = "label8";
            label8.Size = new Size(100, 23);
            label8.TabIndex = 4;
            label8.Text = "Violet";
            label8.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // BackgroundChoice6
            // 
            BackgroundChoice6.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            BackgroundChoice6.BackgroundImageLayout = ImageLayout.None;
            BackgroundChoice6.Image = (Image)resources.GetObject("BackgroundChoice6.Image");
            BackgroundChoice6.Location = new Point(28, 3);
            BackgroundChoice6.Name = "BackgroundChoice6";
            BackgroundChoice6.Size = new Size(79, 62);
            BackgroundChoice6.SizeMode = PictureBoxSizeMode.StretchImage;
            BackgroundChoice6.TabIndex = 3;
            BackgroundChoice6.TabStop = false;
            BackgroundChoice6.Tag = "Violet";
            BackgroundChoice6.Click += BackgroundChoice_Click;
            // 
            // panel5
            // 
            panel5.Controls.Add(label7);
            panel5.Controls.Add(BackgroundChoice5);
            panel5.Location = new Point(148, 113);
            panel5.Name = "panel5";
            panel5.Size = new Size(137, 102);
            panel5.TabIndex = 4;
            // 
            // label7
            // 
            label7.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            label7.ForeColor = Color.White;
            label7.Location = new Point(18, 68);
            label7.Name = "label7";
            label7.Size = new Size(100, 23);
            label7.TabIndex = 3;
            label7.Text = "Pink";
            label7.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // BackgroundChoice5
            // 
            BackgroundChoice5.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            BackgroundChoice5.BackgroundImageLayout = ImageLayout.None;
            BackgroundChoice5.Image = (Image)resources.GetObject("BackgroundChoice5.Image");
            BackgroundChoice5.Location = new Point(27, 3);
            BackgroundChoice5.Name = "BackgroundChoice5";
            BackgroundChoice5.Size = new Size(78, 62);
            BackgroundChoice5.SizeMode = PictureBoxSizeMode.StretchImage;
            BackgroundChoice5.TabIndex = 2;
            BackgroundChoice5.TabStop = false;
            BackgroundChoice5.Tag = "Pink";
            BackgroundChoice5.Click += BackgroundChoice_Click;
            // 
            // panel4
            // 
            panel4.Controls.Add(label6);
            panel4.Controls.Add(BackgroundChoice4);
            panel4.Location = new Point(3, 113);
            panel4.Name = "panel4";
            panel4.Size = new Size(137, 102);
            panel4.TabIndex = 3;
            // 
            // label6
            // 
            label6.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            label6.ForeColor = Color.White;
            label6.Location = new Point(18, 68);
            label6.Name = "label6";
            label6.Size = new Size(100, 23);
            label6.TabIndex = 2;
            label6.Text = "Pride";
            label6.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // BackgroundChoice4
            // 
            BackgroundChoice4.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            BackgroundChoice4.BackgroundImageLayout = ImageLayout.None;
            BackgroundChoice4.Image = (Image)resources.GetObject("BackgroundChoice4.Image");
            BackgroundChoice4.Location = new Point(27, 0);
            BackgroundChoice4.Name = "BackgroundChoice4";
            BackgroundChoice4.Size = new Size(78, 62);
            BackgroundChoice4.SizeMode = PictureBoxSizeMode.StretchImage;
            BackgroundChoice4.TabIndex = 1;
            BackgroundChoice4.TabStop = false;
            BackgroundChoice4.Tag = "Pride";
            BackgroundChoice4.Click += BackgroundChoice_Click;
            // 
            // panel3
            // 
            panel3.Controls.Add(label5);
            panel3.Controls.Add(BackgroundChoice3);
            panel3.Location = new Point(293, 3);
            panel3.Name = "panel3";
            panel3.Size = new Size(138, 101);
            panel3.TabIndex = 2;
            // 
            // label5
            // 
            label5.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            label5.ForeColor = Color.White;
            label5.Location = new Point(20, 65);
            label5.Name = "label5";
            label5.Size = new Size(100, 23);
            label5.TabIndex = 2;
            label5.Text = "Slate Grey";
            label5.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // BackgroundChoice3
            // 
            BackgroundChoice3.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            BackgroundChoice3.BackgroundImageLayout = ImageLayout.None;
            BackgroundChoice3.Image = (Image)resources.GetObject("BackgroundChoice3.Image");
            BackgroundChoice3.Location = new Point(28, 0);
            BackgroundChoice3.Name = "BackgroundChoice3";
            BackgroundChoice3.Size = new Size(79, 62);
            BackgroundChoice3.SizeMode = PictureBoxSizeMode.StretchImage;
            BackgroundChoice3.TabIndex = 1;
            BackgroundChoice3.TabStop = false;
            BackgroundChoice3.Tag = "Slate";
            BackgroundChoice3.Click += BackgroundChoice_Click;
            // 
            // panel2
            // 
            panel2.Controls.Add(Bloodred);
            panel2.Controls.Add(BackgroundChoice2);
            panel2.Location = new Point(148, 3);
            panel2.Name = "panel2";
            panel2.Size = new Size(137, 101);
            panel2.TabIndex = 1;
            // 
            // Bloodred
            // 
            Bloodred.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            Bloodred.ForeColor = Color.White;
            Bloodred.Location = new Point(18, 65);
            Bloodred.Name = "Bloodred";
            Bloodred.Size = new Size(100, 23);
            Bloodred.TabIndex = 2;
            Bloodred.Text = "Blood Red";
            Bloodred.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // BackgroundChoice2
            // 
            BackgroundChoice2.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            BackgroundChoice2.BackgroundImageLayout = ImageLayout.None;
            BackgroundChoice2.Image = (Image)resources.GetObject("BackgroundChoice2.Image");
            BackgroundChoice2.Location = new Point(27, 0);
            BackgroundChoice2.Name = "BackgroundChoice2";
            BackgroundChoice2.Size = new Size(78, 62);
            BackgroundChoice2.SizeMode = PictureBoxSizeMode.StretchImage;
            BackgroundChoice2.TabIndex = 1;
            BackgroundChoice2.TabStop = false;
            BackgroundChoice2.Tag = "Blood";
            BackgroundChoice2.Click += BackgroundChoice_Click;
            // 
            // panel1
            // 
            panel1.Controls.Add(label1);
            panel1.Controls.Add(BackgroundChoice1);
            panel1.Location = new Point(3, 3);
            panel1.Name = "panel1";
            panel1.Size = new Size(137, 101);
            panel1.TabIndex = 0;
            // 
            // label1
            // 
            label1.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            label1.ForeColor = Color.White;
            label1.Location = new Point(18, 65);
            label1.Name = "label1";
            label1.Size = new Size(100, 23);
            label1.TabIndex = 1;
            label1.Text = "Midnight";
            label1.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // BackgroundChoice1
            // 
            BackgroundChoice1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            BackgroundChoice1.BackgroundImageLayout = ImageLayout.None;
            BackgroundChoice1.Image = (Image)resources.GetObject("BackgroundChoice1.Image");
            BackgroundChoice1.Location = new Point(27, 0);
            BackgroundChoice1.Name = "BackgroundChoice1";
            BackgroundChoice1.Size = new Size(78, 62);
            BackgroundChoice1.SizeMode = PictureBoxSizeMode.StretchImage;
            BackgroundChoice1.TabIndex = 0;
            BackgroundChoice1.TabStop = false;
            BackgroundChoice1.Tag = "Midnight";
            BackgroundChoice1.Click += BackgroundChoice_Click;
            // 
            // PreviewPanel
            // 
            PreviewPanel.BackColor = Color.Transparent;
            PreviewPanel.Controls.Add(PreviewPictureBox2);
            PreviewPanel.Controls.Add(PreviewPictureBox1);
            PreviewPanel.Controls.Add(PreviewTitleLabel);
            PreviewPanel.ForeColor = Color.White;
            PreviewPanel.Location = new Point(12, 334);
            PreviewPanel.Name = "PreviewPanel";
            PreviewPanel.Size = new Size(438, 151);
            PreviewPanel.TabIndex = 11;
            // 
            // PreviewPictureBox2
            // 
            PreviewPictureBox2.Location = new Point(187, 90);
            PreviewPictureBox2.Name = "PreviewPictureBox2";
            PreviewPictureBox2.Size = new Size(172, 40);
            PreviewPictureBox2.TabIndex = 4;
            PreviewPictureBox2.TabStop = false;
            // 
            // PreviewPictureBox1
            // 
            PreviewPictureBox1.Location = new Point(100, 90);
            PreviewPictureBox1.Name = "PreviewPictureBox1";
            PreviewPictureBox1.Size = new Size(40, 40);
            PreviewPictureBox1.TabIndex = 3;
            PreviewPictureBox1.TabStop = false;
            // 
            // PreviewTitleLabel
            // 
            PreviewTitleLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            PreviewTitleLabel.ForeColor = Color.White;
            PreviewTitleLabel.Location = new Point(7, 0);
            PreviewTitleLabel.Name = "PreviewTitleLabel";
            PreviewTitleLabel.Size = new Size(424, 140);
            PreviewTitleLabel.TabIndex = 0;
            PreviewTitleLabel.Text = "Preview\r\n\r\nthe quick brown fox jumps over the lazy dog\r\nTHE QUICK BROWN FOX JUMPS OVER THE LAZY DOG\r\n\r\n\r\n\r\n\r\n";
            PreviewTitleLabel.TextAlign = ContentAlignment.TopCenter;
            // 
            // AppearanceForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackgroundImage = (Image)resources.GetObject("$this.BackgroundImage");
            ClientSize = new Size(460, 555);
            Controls.Add(PreviewPanel);
            Controls.Add(tableLayoutPanel1);
            Controls.Add(TextColorButton);
            Controls.Add(BorderColorButton);
            Controls.Add(CustomBackgroundBox);
            Controls.Add(ClearButton);
            Controls.Add(SaveButton);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Name = "AppearanceForm";
            Text = "AppearanceForm";
            Load += AppearanceForm_Load;
            CustomBackgroundBox.ResumeLayout(false);
            tableLayoutPanel1.ResumeLayout(false);
            panel6.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice6).EndInit();
            panel5.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice5).EndInit();
            panel4.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice4).EndInit();
            panel3.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice3).EndInit();
            panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice2).EndInit();
            panel1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)BackgroundChoice1).EndInit();
            PreviewPanel.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)PreviewPictureBox2).EndInit();
            ((System.ComponentModel.ISupportInitialize)PreviewPictureBox1).EndInit();
            ResumeLayout(false);
        }

        #endregion
        private Button SaveButton;
        private Button ClearButton;
        private Panel CustomBackgroundBox;
        private Label Custom;
        private Button BorderColorButton;
        private Button TextColorButton;
        private TableLayoutPanel tableLayoutPanel1;
        private Panel panel6;
        private PictureBox BackgroundChoice6;
        private Panel panel5;
        private PictureBox BackgroundChoice5;
        private Panel panel4;
        private PictureBox BackgroundChoice4;
        private Panel panel3;
        private PictureBox BackgroundChoice3;
        private Panel panel2;
        private PictureBox BackgroundChoice2;
        private Panel panel1;
        private PictureBox BackgroundChoice1;
        private Label Bloodred;
        private Label label1;
        private Label label8;
        private Label label7;
        private Label label6;
        private Label label5;
        private Button SelectCustomImageButton;
        private Panel PreviewPanel;
        private Label PreviewTitleLabel;
        private PictureBox PreviewPictureBox2;
        private PictureBox PreviewPictureBox1;
    }
}