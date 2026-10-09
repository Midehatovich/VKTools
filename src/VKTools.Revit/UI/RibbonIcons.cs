using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// Значки кнопок рисуются в коде: не нужны файлы картинок и сторонние наборы иконок с их лицензиями.
    /// </summary>
    internal static class RibbonIcons
    {
        private static readonly Color Blue = Color.FromRgb(0x1F, 0x5F, 0xA8);
        private static readonly Color Gray = Color.FromRgb(0x5A, 0x63, 0x70);

        public static ImageSource Check(int size) => Draw(size, Blue, "M 7,16.5 L 13,22.5 L 25,9.5");

        private static readonly Color Teal = Color.FromRgb(0x0F, 0x7C, 0x8C);

        /// <summary>Три ветки сходятся в одну.</summary>
        public static ImageSource Merge(int size) =>
            Draw(size, Teal, "M 7,8 C 14,8 14,16 20,16 M 7,16 L 20,16 M 7,24 C 14,24 14,16 20,16 M 20,16 L 25,16");

        public static ImageSource Settings(int size) =>
            Draw(size, Gray, "M 7,10 L 25,10 M 7,16 L 25,16 M 7,22 L 25,22 M 12,7.5 L 12,12.5 M 20,13.5 L 20,18.5 M 14,19.5 L 14,24.5");

        public static ImageSource About(int size) => Draw(size, Gray, "M 16,14 L 16,24 M 16,8.5 L 16,10.5");

        /// <summary>Скруглённый квадрат цвета модуля и белый значок, нарисованный в сетке 32×32.</summary>
        private static RenderTargetBitmap Draw(int size, Color background, string glyph)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double scale = size / 32.0;
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(1, 1, 30, 30), 5, 5);
                var pen = new Pen(Brushes.White, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, Geometry.Parse(glyph));
                dc.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
