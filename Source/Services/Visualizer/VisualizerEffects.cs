using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AnikiHelper.Services.Visualizer
{
    internal abstract class VisualEffect
    {
        public abstract string Name { get; }

        public abstract void Render(DrawingContext dc, Size size, AudioFrame audio, double time, double delta);

        protected static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (byte)(a.A + ((b.A - a.A) * t)),
                (byte)(a.R + ((b.R - a.R) * t)),
                (byte)(a.G + ((b.G - a.G) * t)),
                (byte)(a.B + ((b.B - a.B) * t)));
        }

        protected static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        protected static readonly Color Cyan = Color.FromRgb(0x5E, 0xE3, 0xE8);
        protected static readonly Color Violet = Color.FromRgb(0x7D, 0x5C, 0xC4);
        protected static readonly Color Pink = Color.FromRgb(0xE8, 0x5E, 0xB8);
        protected static readonly Color Ice = Color.FromRgb(0xE8, 0xF8, 0xFF);
    }

    /// <summary>
    /// A wireframe landscape seen in perspective. Each row is a past moment of the music,
    /// so peaks roll toward the horizon; bass lifts the middle, treble the edges.
    /// </summary>
    internal sealed class MeshGridEffect : VisualEffect
    {
        private const int Columns = 84;
        private const int Rows = 46;
        private const int DepthBuckets = 9;
        private const double RowSpacing = 0.55;
        private const double NearDistance = 3.2;
        private const double Spread = 22.0;
        private const double CameraHeight = 5.5;
        private const double PitchDegrees = 30;
        private const double SwayDegrees = 6;
        private const double StepSeconds = 1.0 / 30;

        // Everything is allocated once and reused: no per-frame garbage, so no periodic
        // garbage-collection pauses (the "lag every few seconds").
        private readonly float[][] history = new float[Rows][];
        private readonly Point[,] points = new Point[Rows, Columns];
        private int newest;
        private int filled;
        private double scroll;
        private Pen[] bucketPens;
        private Pen columnPen;

        public MeshGridEffect()
        {
            for (var r = 0; r < Rows; r++)
            {
                history[r] = new float[Columns];
            }
        }

        public override string Name
        {
            get { return "Mesh grid"; }
        }

        private float[] RowAt(int age)
        {
            return history[((newest - age) % Rows + Rows) % Rows];
        }

        public override void Render(DrawingContext dc, Size size, AudioFrame audio, double time, double delta)
        {
            EnsurePens();

            scroll += delta / StepSeconds;
            while (scroll >= 1 || filled == 0)
            {
                scroll = Math.Max(0, scroll - 1);
                newest = (newest + 1) % Rows;
                var snapshot = history[newest];
                for (var c = 0; c < Columns; c++)
                {
                    // Mirror the spectrum: bass in the centre, treble toward both edges.
                    var fromCentre = Math.Abs((c - ((Columns - 1) / 2.0)) / ((Columns - 1) / 2.0));
                    var band = (int)Math.Min(AudioFrame.BandCount - 1, fromCentre * 48);
                    snapshot[c] = audio.Bands[band];
                }

                filled = Math.Min(Rows, filled + 1);
            }

            // Keep the newest row live between steps so peaks react immediately.
            var live = history[newest];
            for (var c = 0; c < Columns; c++)
            {
                var fromCentre = Math.Abs((c - ((Columns - 1) / 2.0)) / ((Columns - 1) / 2.0));
                var band = (int)Math.Min(AudioFrame.BandCount - 1, fromCentre * 48);
                live[c] = Math.Max(live[c], audio.Bands[band]);
            }

            var width = size.Width;
            var height = size.Height;
            var focal = height * 0.95;
            var centreX = width / 2;
            var centreY = height * 0.5;

            // Camera looks down at 30 degrees and slowly sways left/right.
            var pitch = PitchDegrees * Math.PI / 180;
            var yaw = SwayDegrees * Math.PI / 180 * Math.Sin(time * 0.12);
            var cosPitch = Math.Cos(pitch);
            var sinPitch = Math.Sin(pitch);
            var cosYaw = Math.Cos(yaw);
            var sinYaw = Math.Sin(yaw);
            var cameraHeight = CameraHeight + (audio.Beat * 0.15);

            for (var r = 0; r < Rows; r++)
            {
                var z = NearDistance + ((r - scroll) * RowSpacing);
                var row = r < filled ? RowAt(r) : null;
                for (var c = 0; c < Columns; c++)
                {
                    var x = ((c / (double)(Columns - 1)) * 2 - 1) * Spread;
                    var amplitude = row == null ? 0 : row[c];
                    var y = amplitude * amplitude * 2.2;

                    var dy = y - cameraHeight;
                    var x1 = (x * cosYaw) - (z * sinYaw);
                    var z1 = (x * sinYaw) + (z * cosYaw);
                    var y2 = (dy * cosPitch) + (z1 * sinPitch);
                    var z2 = Math.Max(0.5, (-dy * sinPitch) + (z1 * cosPitch));
                    points[r, c] = new Point(centreX + (x1 * focal / z2), centreY - (y2 * focal / z2));
                }
            }

            // Rows grouped by depth: a handful of shapes per frame instead of one per row.
            var rowsPerBucket = (int)Math.Ceiling(Rows / (double)DepthBuckets);
            for (var bucket = DepthBuckets - 1; bucket >= 0; bucket--)
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    var first = bucket * rowsPerBucket;
                    var last = Math.Min(Rows, first + rowsPerBucket);
                    for (var r = first; r < last; r++)
                    {
                        context.BeginFigure(points[r, 0], false, false);
                        for (var c = 1; c < Columns; c++)
                        {
                            context.LineTo(points[r, c], true, false);
                        }
                    }
                }

                geometry.Freeze();
                dc.DrawGeometry(null, bucketPens[bucket], geometry);
            }

            var columns = new StreamGeometry();
            using (var context = columns.Open())
            {
                for (var c = 0; c < Columns; c += 2)
                {
                    context.BeginFigure(points[Rows - 1, c], false, false);
                    for (var r = Rows - 2; r >= 0; r--)
                    {
                        context.LineTo(points[r, c], true, false);
                    }
                }
            }

            columns.Freeze();
            dc.DrawGeometry(null, columnPen, columns);
        }

        private void EnsurePens()
        {
            if (bucketPens != null)
            {
                return;
            }

            bucketPens = new Pen[DepthBuckets];
            for (var b = 0; b < DepthBuckets; b++)
            {
                var depth = b / (double)(DepthBuckets - 1);
                var color = Lerp(Cyan, Violet, depth);
                color.A = (byte)(235 * (1 - (depth * 0.8)));
                var pen = new Pen(Frozen(color), 1.7 - (depth * 0.9));
                pen.Freeze();
                bucketPens[b] = pen;
            }

            var columnColor = Lerp(Cyan, Violet, 0.5);
            columnColor.A = 80;
            columnPen = new Pen(Frozen(columnColor), 0.9);
            columnPen.Freeze();
        }
    }

    /// <summary>
    /// A field of dots. Loudness travels outward from the centre in rings, and each
    /// direction around the centre listens to its own slice of the spectrum.
    /// </summary>
    internal sealed class DotGridEffect : VisualEffect
    {
        private const double Spacing = 36;
        private const int Shades = 24;
        private const int HistoryLength = 140;

        private readonly float[] ringHistory = new float[HistoryLength];
        private int head;
        private Brush[] shades;

        public override string Name
        {
            get { return "Dot grid"; }
        }

        public override void Render(DrawingContext dc, Size size, AudioFrame audio, double time, double delta)
        {
            if (shades == null)
            {
                shades = new Brush[Shades];
                for (var i = 0; i < Shades; i++)
                {
                    var t = i / (double)(Shades - 1);
                    var color = t < 0.6 ? Lerp(Color.FromArgb(70, 0x3A, 0x3F, 0x8F), Cyan, t / 0.6) : Lerp(Cyan, Ice, (t - 0.6) / 0.4);
                    shades[i] = Frozen(color);
                }
            }

            head = (head + 1) % HistoryLength;
            ringHistory[head] = Math.Min(1f, (audio.Bass * 0.65f) + (audio.Mid * 0.35f) + (audio.Beat * 0.35f));

            var centre = new Point(size.Width / 2, size.Height / 2);
            var maxDistance = Math.Sqrt((centre.X * centre.X) + (centre.Y * centre.Y));
            var columns = (int)(size.Width / Spacing) + 2;
            var rows = (int)(size.Height / Spacing) + 2;
            var offsetX = (size.Width - ((columns - 1) * Spacing)) / 2;
            var offsetY = (size.Height - ((rows - 1) * Spacing)) / 2;

            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < columns; col++)
                {
                    var x = offsetX + (col * Spacing);
                    var y = offsetY + (row * Spacing);
                    var dx = x - centre.X;
                    var dy = y - centre.Y;
                    var distance = Math.Sqrt((dx * dx) + (dy * dy)) / maxDistance;

                    // Value from a moment ago: rings travel outward.
                    var age = (int)(distance * (HistoryLength - 10));
                    var ring = ringHistory[((head - age) % HistoryLength + HistoryLength) % HistoryLength];

                    var angle = (Math.Atan2(dy, dx) + Math.PI) / (2 * Math.PI);
                    var band = (int)(Math.Abs((angle * 2) - 1) * (AudioFrame.BandCount - 1));
                    var value = (ring * 0.7) + (audio.Bands[band] * 0.3 * (1 - distance));
                    value = Math.Max(0, Math.Min(1, value));

                    var radius = 1.4 + (value * 8.5);
                    var shade = shades[(int)(value * (Shades - 1))];
                    dc.DrawEllipse(shade, null, new Point(x, y), radius, radius);
                }
            }
        }
    }

    /// <summary>
    /// Glowing particles orbiting the centre in loose clouds. Mids speed up the swirl,
    /// bass swells the clouds, beats push everything outward for a moment.
    /// </summary>
    internal sealed class ParticleEffect : VisualEffect
    {
        private const int Count = 1300;

        private struct Particle
        {
            public double Angle;
            public double Radius;
            public double Speed;
            public double Phase;
            public double Size;
            public int Shade;
        }

        private Particle[] particles;
        private Brush[] palette;
        private double burst;

        public override string Name
        {
            get { return "Particles"; }
        }

        public override void Render(DrawingContext dc, Size size, AudioFrame audio, double time, double delta)
        {
            if (particles == null)
            {
                Create();
            }

            burst = Math.Max(burst * Math.Exp(-3.5 * delta), audio.Beat);
            var centre = new Point(size.Width / 2, size.Height / 2);
            var scale = Math.Min(size.Width, size.Height) / 1080.0;
            var swirl = 0.25 + (audio.Mid * 1.8);

            for (var i = 0; i < particles.Length; i++)
            {
                var p = particles[i];
                p.Angle += p.Speed * swirl * delta;
                particles[i] = p;

                // Clouds: radius wobbles with each particle's own phase.
                var wobble = Math.Sin((time * 0.7) + p.Phase) * 40 * (0.4 + audio.Treble);
                var radius = ((p.Radius * (1 + (audio.Bass * 0.35) + (burst * 0.18))) + wobble) * scale;
                var x = centre.X + (Math.Cos(p.Angle) * radius * 1.45);
                var y = centre.Y + (Math.Sin(p.Angle) * radius * 0.85);
                var dot = (p.Size + (audio.Bass * 2.2) + (burst * 1.5)) * scale;
                dc.DrawEllipse(palette[p.Shade], null, new Point(x, y), dot, dot);
            }
        }

        private void Create()
        {
            var random = new Random(7);
            particles = new Particle[Count];
            for (var i = 0; i < Count; i++)
            {
                var ring = random.NextDouble();
                particles[i] = new Particle
                {
                    Angle = random.NextDouble() * Math.PI * 2,
                    Radius = 170 + (Math.Pow(ring, 0.7) * 620),
                    Speed = (0.15 + (random.NextDouble() * 0.5)) * (random.NextDouble() < 0.85 ? 1 : -1),
                    Phase = random.NextDouble() * Math.PI * 2,
                    Size = 1.1 + (random.NextDouble() * 2.4),
                    Shade = random.Next(12)
                };
            }

            palette = new Brush[12];
            for (var i = 0; i < 12; i++)
            {
                var t = i / 11.0;
                var color = t < 0.5 ? Lerp(Violet, Cyan, t * 2) : Lerp(Cyan, Pink, (t - 0.5) * 2);
                color.A = (byte)(150 + (i * 8));
                palette[i] = Frozen(color);
            }
        }
    }
}
