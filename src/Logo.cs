// AllFind logo: a gradient glass magnifier on a transparent background, handle to the bottom-left.
using System;
using System.Windows;
using System.Windows.Media;

namespace AllFind
{
    static class Logo
    {
        static Brush Ink()
        {
            return new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromRgb(0x4F, 0xB0, 0xFF), 0.0),
                new GradientStop(Color.FromRgb(0x63, 0x6A, 0xFF), 0.5),
                new GradientStop(Color.FromRgb(0xA4, 0x4B, 0xF5), 1.0) }, new Point(1, 0), new Point(0, 1));
        }

        // detail = false for tiny sizes (drops the small outer sparkle and reflection)
        public static void Draw(DrawingContext dc, double s, bool detail)
        {
            dc.PushTransform(new MatrixTransform(-1, 0, 0, 1, s, 0)); // mirror left-to-right
            var ink = Ink();
            var c = new Point(s * 0.42, s * 0.42);
            double R = s * 0.27, t = s * 0.12;

            if (detail)
            {
                // soft drop shadow for depth on any background
                var sh = new SolidColorBrush(Color.FromArgb(50, 30, 20, 90));
                var shp = new Pen(sh, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                double d = s * 0.025;
                dc.DrawEllipse(null, shp, new Point(c.X + d, c.Y + d * 1.4), R, R);
                dc.DrawLine(new Pen(sh, t * 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    new Point(c.X + R * 0.72 + d, c.Y + R * 0.72 + d * 1.4), new Point(s * 0.86 + d, s * 0.86 + d * 1.4));
            }

            // tinted glass inside the lens
            var glass = new RadialGradientBrush(Color.FromArgb(150, 0x9C, 0xC8, 0xFF), Color.FromArgb(70, 0x7A, 0x6A, 0xFF))
            { Center = new Point(0.35, 0.3), GradientOrigin = new Point(0.3, 0.25), RadiusX = 0.8, RadiusY = 0.8 };
            dc.DrawEllipse(glass, null, c, R, R);

            // handle
            dc.DrawLine(new Pen(ink, t * 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                new Point(c.X + R * 0.72, c.Y + R * 0.72), new Point(s * 0.86, s * 0.86));
            // ring
            dc.DrawEllipse(null, new Pen(ink, t), c, R, R);

            if (detail)
            {
                // glass reflection arc
                var arc = new StreamGeometry();
                double rr = R - t * 0.95;
                using (var g = arc.Open())
                {
                    g.BeginFigure(Polar(c, rr, 200), false, false);
                    g.ArcTo(Polar(c, rr, 255), new Size(rr, rr), 0, false, SweepDirection.Clockwise, true, false);
                }
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), s * 0.035) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
                Sparkle(dc, Brushes.White, new Point(c.X + R * 0.08, c.Y + R * 0.06), R * 0.5);
                Sparkle(dc, ink, new Point(c.X + R * 1.12, c.Y - R * 1.12), R * 0.3);
            }
            else Sparkle(dc, Brushes.White, new Point(c.X + R * 0.05, c.Y + R * 0.05), R * 0.55);
            dc.Pop();
        }

        static Point Polar(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        static void Sparkle(DrawingContext dc, Brush b, Point p, double a)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(p.X, p.Y - a), true, true);
                ctx.QuadraticBezierTo(p, new Point(p.X + a, p.Y), true, true);
                ctx.QuadraticBezierTo(p, new Point(p.X, p.Y + a), true, true);
                ctx.QuadraticBezierTo(p, new Point(p.X - a, p.Y), true, true);
                ctx.QuadraticBezierTo(p, new Point(p.X, p.Y - a), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(b, null, g);
        }

        public static DrawingImage Image(double s, bool detail)
        {
            var dg = new DrawingGroup();
            using (var dc = dg.Open()) { dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, s, s)); Draw(dc, s, detail); }
            var img = new DrawingImage(dg); img.Freeze();
            return img;
        }
    }
}
