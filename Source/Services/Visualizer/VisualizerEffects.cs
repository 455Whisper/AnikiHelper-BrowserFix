using System;
using System.Collections.Generic;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        private const int Columns = 161;
        private const int Rows = 70;
        private const double RowSpacing = 0.42;
        private const double NearDistance = 2.2;
        private const double Spread = 26.0;
        private const double CameraHeight = 6.0;
        private const double PitchDegrees = 34;
        private const double SwayDegrees = 5;
        private const double StepSeconds = 1.0 / 40;
        private const double HeightScale = 4.0;
        private const int MaxRenderWidth = 1280;

        private static readonly Color NearColor = Color.FromRgb(0x5E, 0xE3, 0xE8);
        private static readonly Color MidColor = Color.FromRgb(0x4C, 0xF0, 0xC0);
        private static readonly Color FarColor = Color.FromRgb(0x3C, 0xFF, 0x8C);

        private readonly float[][] history = new float[Rows][];
        private readonly float[] spectrum = new float[Columns];
        private readonly float[] pointX = new float[Rows * Columns];
        private readonly float[] pointY = new float[Rows * Columns];
        private int newest;
        private int filled;
        private double scroll;

        // Software surface: lines are rasterised straight into a pixel buffer, so the cost per
        // frame stays flat no matter how tall or tangled the waves get (WPF geometry does not).
        private int bufferWidth;
        private int bufferHeight;
        private int glowWidth;
        private int glowHeight;
        private int[] intensity;
        private int[] glow;
        private int[] glowTemp;
        private int[] pixels;
        private int[] rowColor;
        private WriteableBitmap bitmap;

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

        private void SampleSpectrum(AudioFrame audio)
        {
            var half = (Columns - 1) / 2.0;
            var maxBand = AudioFrame.BandCount - 2;
            for (var c = 0; c < Columns; c++)
            {
                var fromCentre = Math.Abs(c - half) / half;
                var position = Math.Pow(fromCentre, 0.85) * maxBand;
                var i = (int)position;
                var t = (float)(position - i);
                spectrum[c] = (audio.Bands[i] * (1 - t)) + (audio.Bands[i + 1] * t);
            }
        }

        public override void Render(DrawingContext dc, Size size, AudioFrame audio, double time, double delta)
        {
            SampleSpectrum(audio);

            scroll += delta / StepSeconds;
            while (scroll >= 1 || filled == 0)
            {
                scroll = Math.Max(0, scroll - 1);
                newest = (newest + 1) % Rows;
                Array.Copy(spectrum, history[newest], Columns);
                filled = Math.Min(Rows, filled + 1);
            }

            var live = history[newest];
            for (var c = 0; c < Columns; c++)
            {
                live[c] = Math.Max(live[c], spectrum[c]);
            }

            EnsureBuffers(size);
            var width = bufferWidth;
            var height = bufferHeight;
            var focal = height * 0.9;
            var centreX = width / 2.0;
            var centreY = height * 0.47;

            var pitch = PitchDegrees * Math.PI / 180;
            var yaw = SwayDegrees * Math.PI / 180 * Math.Sin(time * 0.12);
            var cosPitch = Math.Cos(pitch);
            var sinPitch = Math.Sin(pitch);
            var cosYaw = Math.Cos(yaw);
            var sinYaw = Math.Sin(yaw);
            var cameraHeight = CameraHeight + (audio.Beat * 0.12) + (audio.Onset * 0.08);

            for (var r = 0; r < Rows; r++)
            {
                var z = NearDistance + ((r - scroll) * RowSpacing);
                var row = r < filled ? RowAt(r) : null;
                var distanceFade = 1.0 - (r / (double)Rows * 0.25);
                for (var c = 0; c < Columns; c++)
                {
                    var x = ((c / (double)(Columns - 1)) * 2 - 1) * Spread;
                    var amplitude = row == null ? 0 : row[c];
                    var y = Math.Pow(amplitude, 1.3) * HeightScale * distanceFade;

                    var dy = y - cameraHeight;
                    var x1 = (x * cosYaw) - (z * sinYaw);
                    var z1 = (x * sinYaw) + (z * cosYaw);
                    var y2 = (dy * cosPitch) + (z1 * sinPitch);
                    var z2 = Math.Max(0.3, (-dy * sinPitch) + (z1 * cosPitch));
                    var index = (r * Columns) + c;
                    pointX[index] = (float)(centreX + (x1 * focal / z2));
                    pointY[index] = (float)(centreY - (y2 * focal / z2));
                }
            }

            Array.Clear(intensity, 0, intensity.Length);

            // Lines into the distance (bright) and every third cross line (faint).
            for (var c = 0; c < Columns; c++)
            {
                for (var r = 0; r < Rows - 1; r++)
                {
                    var a = (r * Columns) + c;
                    var b = a + Columns;
                    DrawLine(pointX[a], pointY[a], pointX[b], pointY[b], 210);
                }
            }

            for (var r = 0; r < Rows; r += 3)
            {
                for (var c = 0; c < Columns - 1; c++)
                {
                    var a = (r * Columns) + c;
                    DrawLine(pointX[a], pointY[a], pointX[a + 1], pointY[a + 1], 60);
                }
            }

            BuildGlow();
            Compose();

            bitmap.WritePixels(new Int32Rect(0, 0, bufferWidth, bufferHeight), pixels, bufferWidth * 4, 0);
            dc.DrawImage(bitmap, new Rect(0, 0, size.Width, size.Height));
        }

        private void EnsureBuffers(Size size)
        {
            var scale = Math.Min(1.0, MaxRenderWidth / Math.Max(1.0, size.Width));
            var w = Math.Max(16, (int)(size.Width * scale));
            var h = Math.Max(16, (int)(size.Height * scale));
            if (bitmap != null && w == bufferWidth && h == bufferHeight)
            {
                return;
            }

            bufferWidth = w;
            bufferHeight = h;
            glowWidth = w / 2;
            glowHeight = h / 2;
            intensity = new int[w * h];
            pixels = new int[w * h];
            glow = new int[glowWidth * glowHeight];
            glowTemp = new int[glowWidth * glowHeight];
            bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
            RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.Linear);

            // Colour per screen row: blue up close (bottom), green toward the horizon (top).
            rowColor = new int[h];
            var top = h * 0.12;
            for (var y = 0; y < h; y++)
            {
                var t = Math.Max(0, Math.Min(1, (h - y) / (double)(h - top)));
                Color color;
                if (t < 0.40)
                {
                    color = NearColor;
                }
                else if (t < 0.62)
                {
                    color = Lerp(NearColor, MidColor, (t - 0.40) / 0.22);
                }
                else if (t < 0.85)
                {
                    color = Lerp(MidColor, FarColor, (t - 0.62) / 0.23);
                }
                else
                {
                    color = FarColor;
                }

                rowColor[y] = (color.R << 16) | (color.G << 8) | color.B;
            }
        }

        // Xiaolin Wu anti-aliased line, additive into the intensity buffer.
        private void DrawLine(float x0, float y0, float x1, float y1, int strength)
        {
            var w = bufferWidth;
            var h = bufferHeight;
            if ((x0 < 0 && x1 < 0) || (y0 < 0 && y1 < 0) || (x0 >= w && x1 >= w) || (y0 >= h && y1 >= h))
            {
                return;
            }

            var dx = x1 - x0;
            var dy = y1 - y0;
            if (Math.Abs(dx) > w * 2 || Math.Abs(dy) > h * 2)
            {
                return;
            }

            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                if (x1 < x0)
                {
                    var tx = x0; x0 = x1; x1 = tx;
                    var ty = y0; y0 = y1; y1 = ty;
                }

                var gradient = Math.Abs(dx) < 0.0001f ? 0 : (y1 - y0) / (x1 - x0);
                var start = Math.Max(0, (int)Math.Ceiling(x0));
                var end = Math.Min(w - 1, (int)Math.Floor(x1));
                var yy = y0 + (gradient * (start - x0));
                for (var x = start; x <= end; x++, yy += gradient)
                {
                    var iy = (int)Math.Floor(yy);
                    var frac = yy - iy;
                    if (iy >= 0 && iy < h)
                    {
                        intensity[(iy * w) + x] += (int)(strength * (1 - frac));
                    }

                    if (iy + 1 >= 0 && iy + 1 < h)
                    {
                        intensity[((iy + 1) * w) + x] += (int)(strength * frac);
                    }
                }
            }
            else
            {
                if (y1 < y0)
                {
                    var tx = x0; x0 = x1; x1 = tx;
                    var ty = y0; y0 = y1; y1 = ty;
                }

                var gradient = (x1 - x0) / (y1 - y0);
                var start = Math.Max(0, (int)Math.Ceiling(y0));
                var end = Math.Min(h - 1, (int)Math.Floor(y1));
                var xx = x0 + (gradient * (start - y0));
                for (var y = start; y <= end; y++, xx += gradient)
                {
                    var ix = (int)Math.Floor(xx);
                    var frac = xx - ix;
                    var rowStart = y * w;
                    if (ix >= 0 && ix < w)
                    {
                        intensity[rowStart + ix] += (int)(strength * (1 - frac));
                    }

                    if (ix + 1 >= 0 && ix + 1 < w)
                    {
                        intensity[rowStart + ix + 1] += (int)(strength * frac);
                    }
                }
            }
        }

        private void BuildGlow()
        {
            var w = bufferWidth;
            var gw = glowWidth;
            var gh = glowHeight;

            // Half resolution copy.
            Parallel.For(0, gh, gy =>
            {
                var source = gy * 2 * w;
                var target = gy * gw;
                for (var gx = 0; gx < gw; gx++)
                {
                    var s = source + (gx * 2);
                    var sum = Math.Min(255, intensity[s]) + Math.Min(255, intensity[s + 1]) +
                              Math.Min(255, intensity[s + w]) + Math.Min(255, intensity[s + w + 1]);
                    glow[target + gx] = sum >> 2;
                }
            });

            // Two box-blur passes in each direction approximate a soft gaussian glow.
            BoxBlurHorizontal(glow, glowTemp, 4);
            BoxBlurVertical(glowTemp, glow, 4);
            BoxBlurHorizontal(glow, glowTemp, 4);
            BoxBlurVertical(glowTemp, glow, 4);
        }

        private void BoxBlurHorizontal(int[] source, int[] target, int radius)
        {
            var gw = glowWidth;
            var span = (radius * 2) + 1;
            Parallel.For(0, glowHeight, y =>
            {
                var row = y * gw;
                var sum = 0;
                for (var x = -radius; x <= radius; x++)
                {
                    sum += source[row + Math.Max(0, Math.Min(gw - 1, x))];
                }

                for (var x = 0; x < gw; x++)
                {
                    target[row + x] = sum / span;
                    sum += source[row + Math.Min(gw - 1, x + radius + 1)] - source[row + Math.Max(0, x - radius)];
                }
            });
        }

        private void BoxBlurVertical(int[] source, int[] target, int radius)
        {
            var gw = glowWidth;
            var gh = glowHeight;
            var span = (radius * 2) + 1;
            Parallel.For(0, gw, x =>
            {
                var sum = 0;
                for (var y = -radius; y <= radius; y++)
                {
                    sum += source[(Math.Max(0, Math.Min(gh - 1, y)) * gw) + x];
                }

                for (var y = 0; y < gh; y++)
                {
                    target[(y * gw) + x] = sum / span;
                    sum += source[(Math.Min(gh - 1, y + radius + 1) * gw) + x] - source[(Math.Max(0, y - radius) * gw) + x];
                }
            });
        }

        private void Compose()
        {
            var w = bufferWidth;
            var gw = glowWidth;
            var gh = glowHeight;
            Parallel.For(0, bufferHeight, y =>
            {
                var color = rowColor[y];
                var cr = (color >> 16) & 0xFF;
                var cg = (color >> 8) & 0xFF;
                var cb = color & 0xFF;
                var row = y * w;
                var glowRow = Math.Min(gh - 1, y >> 1) * gw;
                for (var x = 0; x < w; x++)
                {
                    var v = intensity[row + x] + ((glow[glowRow + Math.Min(gw - 1, x >> 1)] * 3) >> 1);
                    if (v <= 0)
                    {
                        pixels[row + x] = 0;
                        continue;
                    }

                    if (v > 255)
                    {
                        v = 255;
                    }

                    pixels[row + x] = (v << 24) | (((cr * v) / 255) << 16) | (((cg * v) / 255) << 8) | ((cb * v) / 255);
                }
            });
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
            ringHistory[head] = Math.Min(1f, (audio.Bass * 0.45f) + (audio.Vocal * 0.45f) + (audio.Onset * 0.3f) + (audio.Beat * 0.25f));

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

            burst = Math.Max(burst * Math.Exp(-3.5 * delta), Math.Max(audio.Beat, audio.Onset * 0.7));
            var centre = new Point(size.Width / 2, size.Height / 2);
            var scale = Math.Min(size.Width, size.Height) / 1080.0;
            var swirl = 0.25 + (audio.Vocal * 2.2);

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
