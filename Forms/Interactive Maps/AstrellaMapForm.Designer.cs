namespace Foot_Tracker.Forms.Interactive_Maps
{
    partial class AstrellaMapForm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(AstrellaMapForm));
            panel2 = new Panel();
            SuspendLayout();
            // 
            // panel2
            // 
            panel2.BackgroundImage = (Image)resources.GetObject("panel2.BackgroundImage");
            panel2.BackgroundImageLayout = ImageLayout.Stretch;
            panel2.Cursor = Cursors.Hand;
            panel2.Location = new Point(278, 2);
            panel2.Name = "panel2";
            panel2.Size = new Size(707, 509);
            panel2.TabIndex = 2;
            // 
            // AstrellaMapForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.Black;
            ClientSize = new Size(988, 653);
            Controls.Add(panel2);
            Name = "AstrellaMapForm";
            Text = "AstrellaMap";
            ResumeLayout(false);
        }

        #endregion

        private Panel panel2;
    }
}