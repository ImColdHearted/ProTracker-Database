namespace Foot_Tracker.Forms.ClientSelector
{
    partial class ClientSelector
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(ClientSelector));
            label1 = new Label();
            SelectClientButton = new Button();
            Client1RadioButton = new RadioButton();
            Client2RadioButton = new RadioButton();
            SuspendLayout();
            // 
            // label1
            // 
            label1.BackColor = Color.Transparent;
            label1.ForeColor = Color.White;
            label1.Location = new Point(12, 9);
            label1.Name = "label1";
            label1.Size = new Size(216, 23);
            label1.TabIndex = 2;
            label1.Text = "Select Pro Client";
            label1.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // SelectClientButton
            // 
            SelectClientButton.BackColor = Color.White;
            SelectClientButton.ForeColor = Color.Black;
            SelectClientButton.Location = new Point(76, 138);
            SelectClientButton.Name = "SelectClientButton";
            SelectClientButton.Size = new Size(73, 28);
            SelectClientButton.TabIndex = 3;
            SelectClientButton.Text = "Assign";
            SelectClientButton.UseVisualStyleBackColor = false;
            SelectClientButton.Click += button1_Click;
            // 
            // Client1RadioButton
            // 
            Client1RadioButton.AutoSize = true;
            Client1RadioButton.BackColor = Color.Transparent;
            Client1RadioButton.ForeColor = Color.White;
            Client1RadioButton.Location = new Point(84, 60);
            Client1RadioButton.Name = "Client1RadioButton";
            Client1RadioButton.Size = new Size(65, 19);
            Client1RadioButton.TabIndex = 4;
            Client1RadioButton.TabStop = true;
            Client1RadioButton.Text = "Client 1";
            Client1RadioButton.UseVisualStyleBackColor = false;
            // 
            // Client2RadioButton
            // 
            Client2RadioButton.BackColor = Color.Transparent;
            Client2RadioButton.ForeColor = Color.White;
            Client2RadioButton.Location = new Point(84, 98);
            Client2RadioButton.Name = "Client2RadioButton";
            Client2RadioButton.Size = new Size(94, 19);
            Client2RadioButton.TabIndex = 5;
            Client2RadioButton.TabStop = true;
            Client2RadioButton.Text = "Client 2";
            Client2RadioButton.UseVisualStyleBackColor = false;
            // 
            // ClientSelector
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackgroundImage = (Image)resources.GetObject("$this.BackgroundImage");
            BackgroundImageLayout = ImageLayout.Stretch;
            ClientSize = new Size(240, 195);
            Controls.Add(Client2RadioButton);
            Controls.Add(Client1RadioButton);
            Controls.Add(SelectClientButton);
            Controls.Add(label1);
            DoubleBuffered = true;
            ForeColor = Color.White;
            Name = "ClientSelector";
            Text = "ClientSelector";
            Load += ClientSelectionForm_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion
        private Label label1;
        private Button SelectClientButton;
        private RadioButton Client1RadioButton;
        private RadioButton Client2RadioButton;
    }
}