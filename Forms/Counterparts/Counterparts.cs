using Foot_Tracker.Services;
using Foot_Tracker.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace Foot_Tracker.Forms.Counterparts
{
    public partial class Counterparts : Form
    {
        private readonly string groupName;
        private CounterpartHoverForm? hoverForm;
        private Panel? selectedCounterpartCard;

        private Dictionary<string, List<CounterpartEntry>> counterpartGroups =
            new(StringComparer.OrdinalIgnoreCase);

        public Counterparts(string groupName)
        {
            InitializeComponent();
            this.groupName = groupName;
            typeof(FlowLayoutPanel)
    .GetProperty(
        "DoubleBuffered",
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic
    )
    ?.SetValue(flpCounterparts, true);

            SetStyle(
    ControlStyles.AllPaintingInWmPaint |
    ControlStyles.UserPaint |
    ControlStyles.OptimizedDoubleBuffer,
    true
);

            UpdateStyles();
        }

        private void Counterparts_Load(object sender, EventArgs e)
        {
            LoadCounterpartData();
            DisplayCounterpartGroup(groupName);
        }
        private void LoadCounterpartData()
        {
            string jsonPath = Path.Combine(
                AppContext.BaseDirectory,
                "DataFiles",
                "counterparts.json"
            );

            if (!File.Exists(jsonPath))
            {
                MessageBox.Show(
                    $"Counterpart data was not found:\n{jsonPath}",
                    "Missing Data",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );

                return;
            }

            try
            {
                string json = File.ReadAllText(jsonPath);

                counterpartGroups =
                    JsonSerializer.Deserialize<
                        Dictionary<string, List<CounterpartEntry>>
                    >(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }
                    )
                    ?? new Dictionary<string, List<CounterpartEntry>>(
                        StringComparer.OrdinalIgnoreCase
                    );
            }
            catch (JsonException ex)
            {
                MessageBox.Show(
                    $"The counterparts JSON could not be read:\n{ex.Message}",
                    "Invalid JSON",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
        private void DisplayCounterpartGroup(string groupName)
        {
            flpCounterparts.SuspendLayout();

            try
            {
                ClearCounterpartControls();

                lblTitle.Text = $"{groupName} Counterpart Pokémon";

                if (!counterpartGroups.TryGetValue(groupName, out var entries))
                {
                    MessageBox.Show(
                        $"No counterpart data was found for {groupName}.",
                        "No Data",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );

                    return;
                }

                foreach (var entry in entries)
                {
                    flpCounterparts.Controls.Add(
                        CreateCounterpartCard(entry)
                    );
                }
            }
            finally
            {
                flpCounterparts.ResumeLayout(true);
                flpCounterparts.Invalidate();
                flpCounterparts.Update();
            }
        }
        private Control CreateCounterpartCard(CounterpartEntry entry)
        {
            var card = new Panel
            {
                Width = 130,
                Height = 165,
                Margin = new Padding(8),
                BackColor = Color.Transparent,
                Tag = entry
            };

            var pictureBox = new PictureBox
            {
                Width = 120,
                Height = 120,
                Left = 5,
                Top = 5,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = entry
            };

            var nameLabel = new Label
            {
                Width = 120,
                Height = 35,
                Left = 5,
                Top = 126,
                Text = entry.Name,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.TopCenter,
                AutoEllipsis = true,
                Cursor = Cursors.Hand,
                Tag = entry
            };

            LoadCounterpartImage(pictureBox, entry.Image);

            card.Click += CounterpartCard_Click;
            pictureBox.Click += CounterpartCard_Click;
            nameLabel.Click += CounterpartCard_Click;

            card.Controls.Add(pictureBox);
            card.Controls.Add(nameLabel);

            return card;
        }

        private void SelectCounterpartCard(
            Panel card,
            CounterpartEntry counterpart)
        {
            // Clicking the selected counterpart again:
            // deselect it and close the popup.
            if (selectedCounterpartCard == card)
            {
                card.BorderStyle =
                    BorderStyle.None;

                selectedCounterpartCard =
                    null;

                if (hoverForm != null &&
                    !hoverForm.IsDisposed)
                {
                    hoverForm.Hide();
                }

                return;
            }

            // Remove previous selection highlight.
            if (selectedCounterpartCard != null &&
                !selectedCounterpartCard.IsDisposed)
            {
                selectedCounterpartCard.BorderStyle =
                    BorderStyle.None;
            }

            selectedCounterpartCard =
                card;

            card.BorderStyle =
                BorderStyle.Fixed3D;

            // Find the sprite belonging to this card.
            PictureBox? pictureBox =
                card.Controls
                    .OfType<PictureBox>()
                    .FirstOrDefault();

            Image? imageCopy =
                null;

            if (pictureBox?.Image != null)
            {
                imageCopy =
                    new Bitmap(
                        pictureBox.Image
                    );
            }

            // Reuse the counterpart popup you already built.
            hoverForm ??=
                new CounterpartHoverForm();

            hoverForm.DisplayEntry(
                counterpart,
                imageCopy
            );

            PositionHoverForm(
                hoverForm,
                card
            );

            if (!hoverForm.Visible)
            {
                hoverForm.Show(this);
            }
            else
            {
                hoverForm.Invalidate();
                hoverForm.BringToFront();
            }
        }
        private static void LoadCounterpartImage(
    PictureBox pictureBox,
    string relativeImagePath)
        {
            if (string.IsNullOrWhiteSpace(relativeImagePath))
                return;

            string normalizedPath = relativeImagePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            string fullPath = Path.Combine(
                AppContext.BaseDirectory,
                normalizedPath
            );

            if (!File.Exists(fullPath))
            {
                pictureBox.BackColor = Color.DarkRed;
                pictureBox.Tag = fullPath;
                return;
            }

            try
            {
                // Prevents Image.FromFile from locking the image file.
                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read
                );

                using var sourceImage = Image.FromStream(stream);
                pictureBox.Image = new Bitmap(sourceImage);
            }
            catch
            {
                pictureBox.BackColor = Color.DarkRed;
            }
        }
        private void CounterpartCard_Click(
            object? sender,
            EventArgs e)
        {
            if (sender is not Control control ||
                control.Tag is not CounterpartEntry entry)
            {
                return;
            }

            Panel? card =
                control as Panel ??
                control.Parent as Panel;

            if (card == null)
                return;

            SelectCounterpartCard(
                card,
                entry
            );
        }
        private void ClearCounterpartControls()
        {
            foreach (Control control in flpCounterparts.Controls)
            {
                DisposeImages(control);
                control.Dispose();
            }

            flpCounterparts.Controls.Clear();
        }

        private static void DisposeImages(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                DisposeImages(child);

                if (child is PictureBox pictureBox)
                {
                    pictureBox.Image?.Dispose();
                    pictureBox.Image = null;
                }
            }
        }
        private static PictureBox? FindPictureBox(Control control)
        {
            if (control is PictureBox pictureBox)
                return pictureBox;

            if (control.Parent is Panel panel)
            {
                return panel.Controls
                    .OfType<PictureBox>()
                    .FirstOrDefault();
            }

            return null;
        }
        private static void PositionHoverForm(
            Form hoverForm,
            Control sourceControl)
        {
            const int horizontalGap = 12;

            Point sourceLocation =
                sourceControl.PointToScreen(Point.Empty);

            Rectangle screenArea =
                Screen.FromControl(sourceControl).WorkingArea;

            int x =
                sourceLocation.X +
                sourceControl.Width +
                horizontalGap;

            int y = sourceLocation.Y;

            // Keep the popup within the bottom of the screen.
            if (y + hoverForm.Height > screenArea.Bottom)
            {
                y = screenArea.Bottom - hoverForm.Height;
            }

            if (y < screenArea.Top)
            {
                y = screenArea.Top;
            }

            // If there is not enough room on the right,
            // place it to the left of the card.
            if (x + hoverForm.Width > screenArea.Right)
            {
                x =
                    sourceLocation.X -
                    hoverForm.Width -
                    horizontalGap;
            }

            hoverForm.Location = new Point(x, y);
        }
        private bool IsMouseOverCounterpartCard()
        {
            Point clientPoint =
                flpCounterparts.PointToClient(Cursor.Position);

            Control? control =
                flpCounterparts.GetChildAtPoint(clientPoint);

            if (control == null)
                return false;

            return control.Tag is CounterpartEntry ||
                   control.Parent?.Tag is CounterpartEntry;
        }
    }
    }
