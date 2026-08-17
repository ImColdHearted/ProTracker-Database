namespace Foot_Tracker
{
    partial class PokemonSelectorForm
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
            flpPokemon = new SmoothFlowLayoutPanel();
            btnSelect = new Button();
            toolStrip1 = new ToolStrip();
            toolStripLabel1 = new ToolStripLabel();
            txtSearch = new ToolStripTextBox();
            toolStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // flpPokemon
            // 
            flpPokemon.Anchor = AnchorStyles.None;
            flpPokemon.AutoScroll = true;
            flpPokemon.ForeColor = Color.White;
            flpPokemon.Location = new Point(25, 38);
            flpPokemon.Name = "flpPokemon";
            flpPokemon.Size = new Size(499, 412);
            flpPokemon.TabIndex = 0;
            // 
            // btnSelect
            // 
            btnSelect.Location = new Point(142, 456);
            btnSelect.Name = "btnSelect";
            btnSelect.Size = new Size(178, 23);
            btnSelect.TabIndex = 1;
            btnSelect.Text = "Select This Pokemon";
            btnSelect.UseVisualStyleBackColor = true;
            btnSelect.Click += btnSelect_Click;
            // 
            // toolStrip1
            // 
            toolStrip1.Anchor = AnchorStyles.None;
            toolStrip1.AutoSize = false;
            toolStrip1.Dock = DockStyle.None;
            toolStrip1.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip1.Items.AddRange(new ToolStripItem[] { toolStripLabel1, txtSearch });
            toolStrip1.Location = new Point(82, 7);
            toolStrip1.Name = "toolStrip1";
            toolStrip1.Size = new Size(329, 25);
            toolStrip1.TabIndex = 2;
            toolStrip1.Text = "toolStrip1";
            // 
            // toolStripLabel1
            // 
            toolStripLabel1.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            toolStripLabel1.Margin = new Padding(5, 1, 0, 2);
            toolStripLabel1.Name = "toolStripLabel1";
            toolStripLabel1.Size = new Size(104, 22);
            toolStripLabel1.Text = "Search Pokemon:";
            // 
            // txtSearch
            // 
            txtSearch.AutoSize = false;
            txtSearch.Name = "txtSearch";
            txtSearch.Size = new Size(180, 25);
            txtSearch.TextChanged += txtSearch_TextChanged;
            // 
            // PokemonSelectorForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.Black;
            ClientSize = new Size(491, 483);
            Controls.Add(toolStrip1);
            Controls.Add(btnSelect);
            Controls.Add(flpPokemon);
            Name = "PokemonSelectorForm";
            Text = "PokemonSelectorForm";
            Load += PokemonSelectorForm_Load;
            toolStrip1.ResumeLayout(false);
            toolStrip1.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private SmoothFlowLayoutPanel flpPokemon;
        private Button btnSelect;
        private ToolStrip toolStrip1;
        private ToolStripLabel toolStripLabel1;
        private ToolStripTextBox txtSearch;
    }
}