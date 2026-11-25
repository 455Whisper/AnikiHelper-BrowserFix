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

        /// <summary>Number of looks this effect has (switched with the triggers).</summary>
        public virtual int StyleCount
        {
            get { return 1; }
        }

        /// <summary>Current look, 0-based.</summary>
        public int Style { get; set; }

        public virtual string StyleName
        {
            get { return null; }
        }

        /// <summary>The display's measured refresh period (e.g. 1/59.94 s), set by the host.</summary>
        public static double FramePeriod = 1.0 / 60;

        /// <summary>
        /// True while another panel (Spotify) is open over the visualizer and shares its UI
        /// thread: effects then draw at a lower resolution to leave the panel time to respond.
        /// </summary>
        public static bool Busy;

        /// <summary>Bumped by the visualizer whenever Spotify moves to a new song.</summary>
        public static int SongChanges;

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
    /// <summary>
    /// "Ripple": a domed disc of triangulated wireframe seen from above. Sound enters at the
    /// centre and travels outward ring by ring, raising 3D peaks; colour runs from purple at the
    /// rim through cyan to green-yellow on the tallest peaks. Software-rendered like the mesh.
    /// </summary>
    internal sealed class RippleEffect : VisualEffect
    {
        // Storage for up to 120 rings; Rings and Burst draw 84, Spiral draws all 120 so its
        // step walls can be steep without turning jagged.
        private const int Rings = 120;
        private int ActiveRings => Style == 2 ? Rings : 84;
        private const int Spokes = 160;
        private const double Radius = 15.0;
        private const double DomeHeight = 2.2;
        private const double PeakScale = 2.4;
        private const double CameraHeight = 11.0;
        private const double CameraBack = 14.0;
        private const double PitchDegrees = 38;
        private const int MaxRenderWidth = 1280;

        private static readonly Color Rim = Color.FromRgb(0x6C, 0x4A, 0xFF);
        private static readonly Color Mid = Color.FromRgb(0x3A, 0xE0, 0xF0);
        private static readonly Color Peak = Color.FromRgb(0xC6, 0xFF, 0x3A);

        private readonly float[][] history = new float[Rings][];
        private readonly float[] spokeSpectrum = new float[Spokes];
        private readonly float[] px = new float[(Rings + 1) * Spokes];
        private readonly float[] py = new float[(Rings + 1) * Spokes];
        private readonly int[] pc = new int[(Rings + 1) * Spokes];
        private readonly Point[] stars;
        private int newest;
        private double step;

        private int width;
        private int height;
        private int[] red;
        private int[] green;
        private int[] blue;
        private int[] glowR;
        private int[] glowG;
        private int[] glowB;
        private int[] temp;
        private int[] pixels;
        private WriteableBitmap bitmap;

        public RippleEffect()
        {
            for (var r = 0; r < Rings; r++)
            {
                history[r] = new float[Spokes];
            }

            var random = new Random(11);
            stars = new Point[90];
            for (var i = 0; i < stars.Length; i++)
            {
                stars[i] = new Point(random.NextDouble(), random.NextDouble() * 0.3);
            }
        }

        public override string Name
        {
            get { return "Ripple"; }
        }

        public override int StyleCount
        {
            get { return 3; }
        }

        public override string StyleName
        {
            get { return Style == 0 ? "Rings" : Style == 1 ? "Burst" : "Spiral"; }
        }

        private static readonly Color SpiralLow = Color.FromRgb(0x62, 0x94, 0xCC);
        private static readonly Color SpiralMid = Color.FromRgb(0xA4, 0xEE, 0xFF);
        private static readonly Color SpiralHigh = Color.FromRgb(0xF6, 0xFF, 0xDC);
        private double spiralPhase;
        private double spiralEnergy;
        private const double SpiralTurns = 6;
        private const double StepHeight = 1.7;

        private static double SmoothStep(double a, double b, double x)
        {
            var t = Math.Max(0, Math.Min(1, (x - a) / (b - a)));
            return t * t * (3 - (2 * t));
        }

        private readonly float[] waveHistory = new float[Rings];
        // Spiral peak sites: a jittered grid of points over the surface. Each follows one part of
        // the spectrum (40% bass, 35% mids, 25% highs, spread all the way round) and raises a
        // pointed peak that falls away on every side.
        private int[] siteSpoke;
        private int[] siteAge;
        private double[] siteGain;
        private double[] siteAmp;
        private int[] vertexSite;
        private float[] vertexWeight;

        private void EnsurePeakSites()
        {
            if (siteSpoke != null)
            {
                return;
            }

            const double spacing = 1.5;
            const double reach = 1.3;
            var random = new Random(11);
            var xs = new List<double>();
            var zs = new List<double>();
            var spokes = new List<int>();
            var ages = new List<int>();
            var gains = new List<double>();
            for (var k = 0; ; k++)
            {
                var ringRadius = (k + 0.6) * spacing;
                if (ringRadius > Radius + 0.5)
                {
                    break;
                }

                var count = Math.Max(1, (int)Math.Round(2 * Math.PI * ringRadius / spacing));
                var offset = random.NextDouble();
                for (var j = 0; j < count; j++)
                {
                    var a = (j + offset + ((random.NextDouble() - 0.5) * 0.6)) / count * 2 * Math.PI;
                    var rr = ringRadius + ((random.NextDouble() - 0.5) * 0.7 * spacing);
                    xs.Add(Math.Cos(a) * rr);
                    zs.Add(Math.Sin(a) * rr);
                    var pick = random.NextDouble();
                    var around = pick < 0.4 ? random.NextDouble() * 0.15
                        : pick < 0.75 ? 0.15 + (random.NextDouble() * 0.4)
                        : 0.55 + (random.NextDouble() * 0.45);
                    var side = random.NextDouble() < 0.5 ? 1 : -1;
                    spokes.Add((int)(((Spokes / 2.0) + (side * around * Spokes / 2.0) - 0.5 + Spokes) % Spokes));
                    ages.Add(Math.Max(0, Math.Min(Rings - 1, (int)(rr / Radius * Rings))));
                    gains.Add(0.7 + (random.NextDouble() * 0.5));
                }
            }

            siteSpoke = spokes.ToArray();
            siteAge = ages.ToArray();
            siteGain = gains.ToArray();
            siteAmp = new double[siteSpoke.Length];

            // For every Spiral vertex, remember the 3 closest sites and how strongly each lifts it
            // ((1 - d/reach)^2: a pointed tip with a soft base).
            vertexSite = new int[(Rings + 1) * Spokes * 3];
            vertexWeight = new float[(Rings + 1) * Spokes * 3];
            for (var r = 0; r <= Rings; r++)
            {
                var radius = r / (double)Rings * Radius;
                for (var s = 0; s < Spokes; s++)
                {
                    var angle = s / (double)Spokes * Math.PI * 2;
                    var vx = Math.Cos(angle) * radius;
                    var vz = Math.Sin(angle) * radius;
                    var bestCell = new[] { -1, -1, -1 };
                    var bestWeight = new[] { 0.0, 0.0, 0.0 };
                    for (var c = 0; c < siteSpoke.Length; c++)
                    {
                        var dx = vx - xs[c];
                        var dz = vz - zs[c];
                        var d2 = (dx * dx) + (dz * dz);
                        if (d2 >= reach * reach)
                        {
                            continue;
                        }

                        var w = 1 - (Math.Sqrt(d2) / reach);
                        w *= w;
                        for (var k = 0; k < 3; k++)
                        {
                            if (w > bestWeight[k])
                            {
                                for (var m = 2; m > k; m--)
                                {
                                    bestWeight[m] = bestWeight[m - 1];
                                    bestCell[m] = bestCell[m - 1];
                                }

                                bestWeight[k] = w;
                                bestCell[k] = c;
                                break;
                            }
                        }
                    }

                    var vi = ((r * Spokes) + s) * 3;
                    for (var k = 0; k < 3; k++)
                    {
                        vertexSite[vi + k] = bestCell[k];
                        vertexWeight[vi + k] = (float)bestWeight[k];
                    }
                }
            }
        }

        private double colourWash;
        private double colourSpread;
        private double drainSeconds;
        private int seenSongChanges;
        private double hueBase = 0.55;

        private static Color Hsv(double hue, double saturation, double value)
        {
            hue = (hue - Math.Floor(hue)) * 6;
            var sector = (int)hue % 6;
            var f = hue - Math.Floor(hue);
            var p = value * (1 - saturation);
            var q = value * (1 - (saturation * f));
            var t = value * (1 - (saturation * (1 - f)));
            double r, g, b;
            switch (sector)
            {
                case 0: r = value; g = t; b = p; break;
                case 1: r = q; g = value; b = p; break;
                case 2: r = p; g = value; b = t; break;
                case 3: r = p; g = q; b = value; break;
                case 4: r = t; g = p; b = value; break;
                default: r = value; g = p; b = q; break;
            }

            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
        private double bassFast;
        private double bassSlow;
        private float waveNow;

        private float[] RingAt(int age)
        {
            return history[((newest - age) % Rings + Rings) % Rings];
        }

        public override void Render(DrawingContext dc, Size size, AudioFrame audio, double time, double delta)
        {
            // Spectrum around the circle, mirrored left/right, with a little extra detail.
            var half = Spokes / 2.0;
            var rings = Style == 0;
            // Colour patches: strength follows how much music there is (rises quickly, fades over
            // about a second when it goes quiet); hits nudge the colours round the wheel.
            var music = Math.Min(1.0, (audio.Bass * 0.45) + (audio.Mid * 0.45) + (audio.Onset * 0.35));
            music = Math.Pow(music, 1.3);
            colourWash += (music - colourWash) * (1 - Math.Exp(-(music > colourWash ? 6.0 : 0.9) * delta));
            hueBase += delta * (0.015 + (audio.Onset * 0.12));

            // How far the colour has spread from the centre (0..1): fills over ~3 s once music
            // plays, recedes in silence, and drains quickly when the song changes so the next
            // song spreads out from the middle again.
            if (SongChanges != seenSongChanges)
            {
                seenSongChanges = SongChanges;
                drainSeconds = 0.7;
            }

            if (drainSeconds > 0)
            {
                drainSeconds -= delta;
                colourSpread = Math.Max(0, colourSpread - (delta * 2.0));
            }
            else if (music > 0.1)
            {
                colourSpread = Math.Min(1, colourSpread + (delta * 0.33));
            }
            else
            {
                colourSpread = Math.Max(0, colourSpread - (delta * 0.5));
            }

            if (rings)
            {
                // Water: the bass alone moves the water. A bass hit lifts the centre above its
                // recent average (a crest), and the fall-back afterwards dips below it (a trough),
                // so every hit sends a round wave outward.
                var bass = (double)audio.Bass;
                var attack = bass > bassFast ? 30.0 : 6.0;
                bassFast += (bass - bassFast) * (1 - Math.Exp(-attack * delta));
                bassSlow += (bass - bassSlow) * (1 - Math.Exp(-1.3 * delta));
                var swell = (bassFast - (bassSlow * 0.85)) * 2.6;
                waveNow = (float)(Math.Max(-0.6, Math.Min(1.1, swell)) + (0.05 * Math.Sin(time * Math.PI * 1.6)));
            }

            for (var s = 0; s < Spokes; s++)
            {
                var around = Math.Abs(((s + 0.5) - half) / half);
                if (rings)
                {
                    // Everything above the bass (drums, vocals, instruments) becomes peaks
                    // around the circle that ride outward inside the waves.
                    var position = 12 + (around * (AudioFrame.BandCount - 14));
                    var i = (int)position;
                    var t = (float)(position - i);
                    var value = (audio.Bands[i] * (1 - t)) + (audio.Bands[i + 1] * t);
                    var detail = audio.Bands[24 + ((s * 29) % (AudioFrame.BandCount - 28))];
                    var level = (value * 0.6f) + (detail * 0.4f) + (audio.Onset * (0.25f + (detail * 0.35f)));
                    spokeSpectrum[s] = (float)Math.Min(1.0, (1 - Math.Exp(-2.4 * level)) / 0.9);
                }
                else
                {
                    var position = around * (AudioFrame.BandCount - 2);
                    var i = (int)position;
                    var t = (float)(position - i);
                    var value = (audio.Bands[i] * (1 - t)) + (audio.Bands[i + 1] * t);
                    var detail = audio.Bands[30 + ((s * 29) % (AudioFrame.BandCount - 34))];
                    spokeSpectrum[s] = (float)((1 - Math.Exp(-2.3 * ((value * 0.75f) + (detail * 0.25f)))) / 0.9);
                }
            }

            // Rings travel outward a little slower than bursts, like water.
            step += delta / FramePeriod * (rings ? 0.7 : 1.0) * 1.5;

            // Spiral: terraced spiral steps that turn and flow outward faster with louder music.
            var spiral = Style == 2;
            if (spiral)
            {
                var energy = (audio.Bass * 0.55) + (audio.Mid * 0.3) + (audio.Onset * 0.3);
                spiralEnergy = energy > spiralEnergy ? energy : spiralEnergy + ((energy - spiralEnergy) * (1 - Math.Exp(-4 * delta)));
                // Slow, steady turn (a quarter of the old speed) that surges a bit more with loud music.
                spiralPhase += delta * (0.07 + (spiralEnergy * spiralEnergy * 0.38));
            }
            while (step >= 1)
            {
                step -= 1;
                newest = (newest + 1) % Rings;
                Array.Copy(spokeSpectrum, history[newest], Spokes);
                waveHistory[newest] = waveNow;
            }

            var centre = history[newest];
            for (var s = 0; s < Spokes; s++)
            {
                centre[s] = Math.Max(centre[s], spokeSpectrum[s]);
            }

            if (spiral)
            {
                // Each peak site follows its own sound; sites further out hear it a little later,
                // so hits ripple outward. Soft cap: the loudest reach about 1.25 steps high.
                EnsurePeakSites();
                for (var c = 0; c < siteSpoke.Length; c++)
                {
                    var level = history[((newest - siteAge[c]) % Rings + Rings) % Rings][siteSpoke[c]];
                    var amp = Math.Pow(level, 1.2) * 2.0 * (0.5 + (spiralEnergy * 0.8)) * siteGain[c];
                    siteAmp[c] = 2.13 * (1 - Math.Exp(-amp / 2.13));
                }
            }

            waveHistory[newest] = rings && Math.Abs(waveNow) > Math.Abs(waveHistory[newest]) ? waveNow : waveHistory[newest];

            EnsureBuffers(size);
            var focal = height * 1.2;
            var centreX = width / 2.0;
            var spiralView = Style == 2;
            var centreY = height * (spiralView ? 0.38 : 0.43);
            var cameraHeight = spiralView ? 13.0 : CameraHeight;
            var cameraBack = spiralView ? 10.5 : CameraBack;
            var pitch = (spiralView ? 50 : PitchDegrees) * Math.PI / 180;
            var cosPitch = Math.Cos(pitch);
            var sinPitch = Math.Sin(pitch);
            var spin = time * 0.05;
            var lift = audio.Beat * 0.25;

            // Vertices: ring 0 is the centre point (repeated per spoke so indexing stays simple).
            var ringCount = ActiveRings;
            for (var r = 0; r <= ringCount; r++)
            {
                // Each ring drifts outward smoothly between steps.
                var radial = spiral ? r / (double)ringCount : r == 0 ? 0 : Math.Min(1.0, (r + step) / (ringCount + 1));

                var radius = radial * Radius;
                var dome = DomeHeight * (1 - (radial * radial));
                var row = r == 0 ? null : RingAt(r - 1);
                var fade = 1.0 - (radial * 0.55);
                var wave = r == 0 ? waveNow : waveHistory[((newest - (r - 1)) % Rings + Rings) % Rings];
                var crest = Math.Max(0.2, Math.Min(1.4, 0.4 + (wave * 1.2)));
                for (var s = 0; s < Spokes; s++)
                {
                    var angle = (s / (double)Spokes * Math.PI * 2) + spin;
                    var amplitude = row == null ? centre[s] : row[s];
                    double stairHeight = 0;
                    double edgeGlow = 0;
                    if (spiral)
                    {
                        // Endless spiral staircase: a 6-turn helix whose treads slope gently down
                        // around each turn (so the turns join with no seam), with a steep riser
                        // between turns. The steps flow inward and down forever; the music adds a
                        // rough texture and makes the walls glow.
                        var baseAngle = s / (double)Spokes;
                        var u = (radial * SpiralTurns) - baseAngle + spiralPhase;
                        var f = u - Math.Floor(u);
                        var riser = SmoothStep(0.92, 1.0, f);
                        var texture = spokeSpectrum[(s + (r * 7)) % Spokes] * 0.25;

                        // Sharp music peaks scattered over the flat top of each step (not on the
                        // walls). Each vertex reads a scrambled band from a different moment in the
                        // recent history, so the peaks flicker across the treads with the music.
                        // Peaks stay off the cliff edges: they fade out near the lip and the foot of
                        // each wall, so the step edges stay sharp however loud the music gets.
                        var tread = (1 - riser) * SmoothStep(0.0, 0.25, f) * (1 - SmoothStep(0.7, 0.9, f));
                        // Separate mountain-like peaks: each vertex takes the tallest of the (up to 3)
                        // nearby peak sites, weighted by how close it is to that site's tip.
                        var vi = ((r * Spokes) + s) * 3;
                        var spike = 0.0;
                        for (var k = 0; k < 3; k++)
                        {
                            var cell = vertexSite[vi + k];
                            if (cell >= 0)
                            {
                                spike = Math.Max(spike, vertexWeight[vi + k] * siteAmp[cell]);
                            }
                        }

                        spike *= tread;
                        edgeGlow = Math.Max(Math.Sin(riser * Math.PI), 1 - SmoothStep(0.0, 0.06, f));
                        stairHeight = (((radial * SpiralTurns) - f + riser) * StepHeight) + (texture * (0.4 + spiralEnergy) * (0.3 + (0.7 * tread))) + spike;
                        amplitude = (float)Math.Min(1.0, 0.3 + (0.6 * Math.Sin(riser * Math.PI) * (0.6 + spiralEnergy)) + texture + (0.1 * radial) + (spike * 0.45));
                    }

                    var peak = spiral
                        ? stairHeight - dome
                        : Style == 0
                        ? ((wave * 2.3) + (Math.Pow(amplitude, 1.35) * 1.9 * crest)) * fade
                        : Math.Pow(amplitude, 1.35) * PeakScale * fade * (r == 0 ? 0.6 : 1);
                    amplitude = Style == 0
                        ? (float)Math.Min(1.0, Math.Max(amplitude * crest, Math.Abs(wave) * 0.8))
                        : Math.Abs(amplitude);
                    var x = Math.Cos(angle) * radius;
                    var z = Math.Sin(angle) * radius;
                    var y = dome + peak + (lift * (1 - radial));

                    var dy = y - cameraHeight;
                    var dz = z + cameraBack;
                    var y2 = (dy * cosPitch) + (dz * sinPitch);
                    var z2 = Math.Max(0.5, (-dy * sinPitch) + (dz * cosPitch));
                    var index = (r * Spokes) + s;
                    px[index] = (float)(centreX + (x * focal / z2));
                    py[index] = (float)(centreY - (y2 * focal / z2));

                    // Colour: taller and closer to the centre is greener.
                    var heat = Math.Min(1.0, (amplitude * fade * 0.55) + ((1 - radial) * (1 - radial) * 0.55));
                    var color = spiral
                        ? (amplitude < 0.5 ? Lerp(SpiralLow, SpiralMid, amplitude * 2) : Lerp(SpiralMid, SpiralHigh, (amplitude - 0.5) * 2))
                        : heat < 0.5 ? Lerp(Rim, Mid, heat * 2) : Lerp(Mid, Peak, (heat - 0.5) * 2);
                    // Music colour: big soft patches of changing colour drift over the surface while
                    // music plays, and fade back to the normal colours when it goes quiet.
                    if (colourWash > 0.01 && colourSpread > 0.001)
                    {
                        var n = (Math.Sin((angle * 2) + (radial * 4) + (time * 0.35)) +
                                 Math.Sin((angle * 3) - (radial * 6) - (time * 0.27) + 1.7) +
                                 Math.Sin(angle + (radial * 9) + (time * 0.19) + 4.1)) / 3;

                        // The colour spreads out from the middle as a song starts until it covers
                        // the whole surface; n only varies the hue and strength across it.
                        var reach = SmoothStep(radial - 0.12, radial + 0.04, colourSpread * 1.16);
                        var mix = colourWash * reach * (0.7 + (0.3 * SmoothStep(-0.6, 0.4, n)));
                        if (mix > 0.01)
                        {
                            var value = Math.Max(0.4, Math.Max(color.R, Math.Max(color.G, color.B)) / 255.0);
                            var tint = Hsv(hueBase + (n * 0.45) + (radial * 0.2), 0.8, value);
                            color = Lerp(color, tint, mix * 0.9);
                        }
                    }

                    // Spiral: the walls and lips glow a little brighter, so the staircase outline
                    // reads clearly through the colours and peaks.
                    if (edgeGlow > 0.01)
                    {
                        var bright = Color.FromRgb(
                            (byte)Math.Min(255, (color.R * 1.6) + 60),
                            (byte)Math.Min(255, (color.G * 1.6) + 60),
                            (byte)Math.Min(255, (color.B * 1.6) + 60));
                        color = Lerp(color, bright, edgeGlow * 0.45);
                    }

                    pc[index] = (color.R << 16) | (color.G << 8) | color.B;
                }
            }

            Array.Clear(red, 0, red.Length);
            Array.Clear(green, 0, green.Length);
            Array.Clear(blue, 0, blue.Length);

            // Triangulated wireframe: rings, spokes and one diagonal per cell.
            for (var r = 0; r <= ringCount; r++)
            {
                for (var s = 0; s < Spokes; s++)
                {
                    var a = (r * Spokes) + s;
                    var next = (r * Spokes) + ((s + 1) % Spokes);
                    if (r > 0)
                    {
                        Line(a, next, 150);
                    }

                    if (r < ringCount)
                    {
                        var outward = ((r + 1) * Spokes) + s;
                        var diagonal = ((r + 1) * Spokes) + ((s + 1) % Spokes);
                        Line(a, outward, 150);
                        Line(a, diagonal, 90);
                    }
                }
            }

            // A few stars above the horizon.
            for (var i = 0; i < stars.Length; i++)
            {
                var sx = (int)(stars[i].X * (width - 1));
                var sy = (int)(stars[i].Y * (height - 1));
                var twinkle = (int)(120 + (80 * Math.Sin((time * 1.3) + (i * 1.7))));
                var p = (sy * width) + sx;
                red[p] += twinkle;
                green[p] += twinkle;
                blue[p] += twinkle;
            }

            Glow(red, glowR);
            Glow(green, glowG);
            Glow(blue, glowB);
            Compose();

            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            dc.DrawImage(bitmap, new Rect(0, 0, size.Width, size.Height));
        }

        private void Line(int a, int b, int strength)
        {
            var ca = pc[a];
            var cb = pc[b];
            var r = ((((ca >> 16) & 0xFF) + ((cb >> 16) & 0xFF)) * strength) >> 9;
            var g = ((((ca >> 8) & 0xFF) + ((cb >> 8) & 0xFF)) * strength) >> 9;
            var bl = (((ca & 0xFF) + (cb & 0xFF)) * strength) >> 9;
            DrawLine(px[a], py[a], px[b], py[b], r, g, bl);
        }

        private void EnsureBuffers(Size size)
        {
            // Spiral renders at full resolution so its fine walls stay crisp.
            var maxWidth = Busy ? (Style == 2 ? 1280 : 900) : (Style == 2 ? 1920 : MaxRenderWidth);
            var scale = Math.Min(1.0, maxWidth / Math.Max(1.0, size.Width));
            var w = Math.Max(16, (int)(size.Width * scale));
            var h = Math.Max(16, (int)(size.Height * scale));
            if (bitmap != null && w == width && h == height)
            {
                return;
            }

            width = w;
            height = h;
            red = new int[w * h];
            green = new int[w * h];
            blue = new int[w * h];
            pixels = new int[w * h];
            var glowSize = (w / 2) * (h / 2);
            glowR = new int[glowSize];
            glowG = new int[glowSize];
            glowB = new int[glowSize];
            temp = new int[glowSize];
            bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        }

        // Xiaolin Wu anti-aliased line, additive into the three colour buffers.
        private void DrawLine(float x0, float y0, float x1, float y1, int cr, int cg, int cb)
        {
            var w = width;
            var h = height;
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
                    var f = yy - iy;
                    Plot(x, iy, 1 - f, cr, cg, cb);
                    Plot(x, iy + 1, f, cr, cg, cb);
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
                    var f = xx - ix;
                    Plot(ix, y, 1 - f, cr, cg, cb);
                    Plot(ix + 1, y, f, cr, cg, cb);
                }
            }
        }

        private void Plot(int x, int y, float coverage, int cr, int cg, int cb)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            var p = (y * width) + x;
            red[p] += (int)(cr * coverage);
            green[p] += (int)(cg * coverage);
            blue[p] += (int)(cb * coverage);
        }

        private void Glow(int[] source, int[] glow)
        {
            var w = width;
            var gw = width / 2;
            var gh = height / 2;
            Parallel.For(0, gh, gy =>
            {
                var s = gy * 2 * w;
                var target = gy * gw;
                for (var gx = 0; gx < gw; gx++)
                {
                    var i = s + (gx * 2);
                    glow[target + gx] = (Math.Min(255, source[i]) + Math.Min(255, source[i + 1]) +
                                         Math.Min(255, source[i + w]) + Math.Min(255, source[i + w + 1])) >> 2;
                }
            });

            Blur(glow, temp, gw, gh, true);
            Blur(temp, glow, gw, gh, false);
            Blur(glow, temp, gw, gh, true);
            Blur(temp, glow, gw, gh, false);
        }

        private static void Blur(int[] source, int[] target, int gw, int gh, bool horizontal)
        {
            const int radius = 4;
            const int span = (radius * 2) + 1;
            if (horizontal)
            {
                Parallel.For(0, gh, y =>
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
            else
            {
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
        }

        private void Compose()
        {
            var w = width;
            var gw = width / 2;
            var gh = height / 2;
            Parallel.For(0, height, y =>
            {
                var row = y * w;
                var glowRow = Math.Min(gh - 1, y >> 1) * gw;
                for (var x = 0; x < w; x++)
                {
                    var g = glowRow + Math.Min(gw - 1, x >> 1);
                    var r = Math.Min(255, red[row + x] + ((glowR[g] * 3) >> 1));
                    var gr = Math.Min(255, green[row + x] + ((glowG[g] * 3) >> 1));
                    var b = Math.Min(255, blue[row + x] + ((glowB[g] * 3) >> 1));
                    var a = Math.Max(r, Math.Max(gr, b));
                    pixels[row + x] = a == 0 ? 0 : (a << 24) | (r << 16) | (gr << 8) | b;
                }
            });
        }
    }

    internal sealed class MeshGridEffect : VisualEffect
    {
        private const int Columns = 161;
        private const int Rows = 110;
        private const double RowSpacing = 0.27;
        private const double NearDistance = 2.2;
        private const double Spread = 32.0;
        private const double BowlRise = 3.5;
        private const double SideLift = 1.6;
        private const double CameraHeight = 6.0;
        private const double PitchDegrees = 34;
        private const double SwayDegrees = 5;
        // One new row per display frame: the surface scrolls in step with the TV's refresh.
        private static double StepSeconds
        {
            get { return FramePeriod; }
        }

        private static readonly float[] ColumnJitter = BuildJitter(161);

        private static float[] BuildJitter(int count)
        {
            var random = new Random(7);
            var values = new float[count];
            for (var i = 0; i < count; i++)
            {
                values[i] = 0.82f + (float)(random.NextDouble() * 0.36);
            }

            return values;
        }
        private const double HeightScale = 2.6;
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
                var smooth = (audio.Bands[i] * (1 - t)) + (audio.Bands[i + 1] * t);

                // Detail: each line also follows its own higher band (mirrored so both halves
                // match), which breaks up the broad flat hills the bass makes in the centre.
                var mirror = (int)Math.Abs(c - half);
                var detailBand = 36 + ((mirror * 37) % (AudioFrame.BandCount - 40));
                var detail = audio.Bands[detailBand];
                var centreWeight = 1.0f - (float)Math.Min(1.0, fromCentre * 2.5);
                var mixed = (smooth * (0.78f - (0.18f * centreWeight))) + (detail * (0.22f + (0.18f * centreWeight)));

                // Soft ceiling so loud bass rounds off into peaks instead of flat plateaus.
                var shaped = (float)(1 - Math.Exp(-2.4 * mixed)) / 0.909f;
                spectrum[c] = Math.Min(1.1f, shaped * ColumnJitter[mirror % ColumnJitter.Length]);
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
                var z = NearDistance + ((r + scroll) * RowSpacing);
                var row = r < filled ? RowAt(r) : null;
                var distanceFade = 1.0 - (r / (double)Rows * 0.25);
                var depth = Math.Max(0, (z - NearDistance) / (Rows * RowSpacing));
                var bowl = BowlRise * depth * depth;
                for (var c = 0; c < Columns; c++)
                {
                    var across = (c / (double)(Columns - 1)) * 2 - 1;
                    var x = across * Spread;
                    var amplitude = row == null ? 0 : row[c];

                    // Concave surface: it curves up toward the back and a little at the sides.
                    var y = (Math.Pow(amplitude, 1.5) * HeightScale * distanceFade) + bowl + (SideLift * across * across);

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

            for (var r = 0; r < Rows; r += 4)
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
            var scale = Math.Min(1.0, (Busy ? 900 : MaxRenderWidth) / Math.Max(1.0, size.Width));
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
