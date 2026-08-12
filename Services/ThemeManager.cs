using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public sealed class ThemeToolStripRenderer : ToolStripProfessionalRenderer
    {
        private readonly Color _backgroundColor;
        private readonly Color _textColor;
        private readonly Color _borderColor;

        public ThemeToolStripRenderer(
            Color backgroundColor,
            Color textColor,
            Color borderColor)
        {
            _backgroundColor = backgroundColor;
            _textColor = textColor;
            _borderColor = borderColor;
        }

        protected override void OnRenderToolStripBackground(
            ToolStripRenderEventArgs e)
        {
            using SolidBrush brush = new(_backgroundColor);

            e.Graphics.FillRectangle(
                brush,
                e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(
            ToolStripRenderEventArgs e)
        {
            // Leave empty to remove the default white border line.
        }

        protected override void OnRenderItemText(
            ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = _textColor;

            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(
            ToolStripSeparatorRenderEventArgs e)
        {
            using Pen pen = new(_borderColor);

            int middle =
                e.Item.ContentRectangle.Top +
                e.Item.ContentRectangle.Height / 2;

            e.Graphics.DrawLine(
                pen,
                e.Item.ContentRectangle.Left,
                middle,
                e.Item.ContentRectangle.Right,
                middle);
        }
    }

    public static class ThemeManager
    {
        public static AppearanceSettings Current { get; private set; } =
            AppearanceSettingsRepository.Load();

        public static void Reload()
        {
            Current = AppearanceSettingsRepository.Load();
        }

        public static void ApplyToForm(Form form)
        {
            ApplyBackground(form);
            ApplyToControlTree(form);
            ApplyBorderColor(form);
            ApplyToolStripTheme(form);

            form.Invalidate(true);
            form.Refresh();
        }

        public static string GetBackgroundPath(string backgroundId)
        {
            if (string.IsNullOrWhiteSpace(backgroundId))
                return string.Empty;

            return Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Custom",
                $"{backgroundId}.png");
        }

        private static string GetCurrentBackgroundPath()
        {
            if (Current.UseCustomBackground &&
                !string.IsNullOrWhiteSpace(Current.CustomBackgroundPath) &&
                File.Exists(Current.CustomBackgroundPath))
            {
                return Current.CustomBackgroundPath;
            }

            return GetBackgroundPath(Current.BackgroundId);
        }

        private static void ApplyBorderColor(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is TableLayoutPanel tableLayoutPanel)
                {
                    tableLayoutPanel.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;
                    tableLayoutPanel.Paint -= DrawThemeBorder;
                    tableLayoutPanel.Paint += DrawThemeBorder;
                    tableLayoutPanel.Invalidate();
                }

                ApplyBorderColor(control);
            }
        }
        private static void ApplyBackground(Form form)
        {
            string fullPath =
                GetCurrentBackgroundPath();

            form.BackgroundImage?.Dispose();
            form.BackgroundImage = null;

            if (!File.Exists(fullPath))
                return;

            using Image source = Image.FromFile(fullPath);

            form.BackgroundImage = new Bitmap(source);
            form.BackgroundImageLayout = ImageLayout.Stretch;
        }
        private static void DrawThemeBorder(
    object? sender,
    PaintEventArgs e)
        {
            if (sender is not Control control)
                return;

            using Pen pen = new(
                Current.BorderColor,
                1);

            Rectangle rectangle = control.ClientRectangle;

            rectangle.Width -= 1;
            rectangle.Height -= 1;

            e.Graphics.DrawRectangle(
                pen,
                rectangle);
        }

        private static void ApplyToControlTree(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is Label ||
                    control is Button ||
                    control is CheckBox ||
                    control is RadioButton)
                {
                    control.ForeColor = Current.TextColor;
                }

                if (control is Label)
                {
                    control.BackColor = Color.Transparent;
                }

                if (control is FlowLayoutPanel flowPanel)
                {
                    flowPanel.BackColor = GetThemeBackgroundColor();
                }

                ApplyToControlTree(control);
            }
        }
        private static Color GetThemeBackgroundColor()
        {
            return Current.BackgroundColor;
        }
        private static void ApplyToolStripTheme(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is ToolStrip toolStrip)
                {
                    toolStrip.RenderMode =
                        ToolStripRenderMode.Professional;

                    toolStrip.Renderer =
                        new ThemeToolStripRenderer(
                            Color.Transparent,
                            Current.TextColor,
                            Current.BorderColor);

                    toolStrip.BackColor =
                        Color.Transparent;

                    toolStrip.ForeColor =
                        Current.TextColor;

                    toolStrip.Invalidate();
                }

                ApplyToolStripTheme(control);
            }
        }
    }
}