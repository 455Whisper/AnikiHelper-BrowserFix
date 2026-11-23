using NAudio.CoreAudioApi;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace AnikiHelper.Services.Spotify
{
    /// <summary>One row in the side box: a playback device or an app, with a 0-100 volume.</summary>
    internal sealed class AudioEntry : INotifyPropertyChanged
    {
        private readonly Action<float> apply;
        private double volume;
        private bool updatingFromSystem;

        public AudioEntry(string key, string name, float initial, Action<float> apply)
        {
            Key = key;
            Name = name;
            this.apply = apply;
            volume = Math.Round(initial * 100);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Key { get; private set; }

        public string Name { get; private set; }

        public double Volume
        {
            get { return volume; }
            set
            {
                var clamped = Math.Max(0, Math.Min(100, Math.Round(value)));
                if (Math.Abs(clamped - volume) < 0.01)
                {
                    return;
                }

                volume = clamped;
                if (!updatingFromSystem)
                {
                    try { apply?.Invoke((float)(volume / 100.0)); } catch { }
                }

                Raise("Volume");
                Raise("Percent");
            }
        }

        public string Percent
        {
            get { return volume.ToString("0") + "%"; }
        }

        public void UpdateFromSystem(float scalar)
        {
            updatingFromSystem = true;
            try
            {
                Volume = scalar * 100;
            }
            finally
            {
                updatingFromSystem = false;
            }
        }

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Reads and sets Windows playback-device and per-app volumes (Core Audio via NAudio).</summary>
    internal sealed class AudioMixerSource
    {
        private readonly ILogger logger;
        private readonly Dictionary<string, Func<float>> readers = new Dictionary<string, Func<float>>();

        public AudioMixerSource(ILogger logger)
        {
            this.logger = logger;
        }

        public List<AudioEntry> GetDevices()
        {
            var result = new List<AudioEntry>();
            readers.Clear();
            try
            {
                var enumerator = new MMDeviceEnumerator();
                string defaultId = null;
                try
                {
                    defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID;
                }
                catch
                {
                }

                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    var endpoint = device.AudioEndpointVolume;
                    var name = device.FriendlyName;
                    if (device.ID == defaultId)
                    {
                        name += "  •  Default";
                    }

                    var key = "device:" + device.ID;
                    readers[key] = () => endpoint.MasterVolumeLevelScalar;
                    result.Add(new AudioEntry(
                        key,
                        name,
                        endpoint.MasterVolumeLevelScalar,
                        v => endpoint.MasterVolumeLevelScalar = v));
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Audio] Could not list playback devices.");
            }

            return result;
        }

        public List<AudioEntry> GetApps()
        {
            var result = new List<AudioEntry>();
            readers.Clear();
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;

                var groups = new Dictionary<string, List<SimpleAudioVolume>>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        if (session.State == AudioSessionState.AudioSessionStateExpired)
                        {
                            continue;
                        }

                        var name = session.IsSystemSoundsSession ? "System sounds" : DescribeProcess((int)session.GetProcessID, session.DisplayName);
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        List<SimpleAudioVolume> list;
                        if (!groups.TryGetValue(name, out list))
                        {
                            list = new List<SimpleAudioVolume>();
                            groups[name] = list;
                            order.Add(name);
                        }

                        list.Add(session.SimpleAudioVolume);
                    }
                    catch
                    {
                    }
                }

                foreach (var name in order.OrderBy(n => n == "System sounds" ? 1 : 0).ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    var volumes = groups[name];
                    var key = "app:" + name;
                    readers[key] = () => volumes.Max(v => v.Volume);
                    result.Add(new AudioEntry(
                        key,
                        name,
                        volumes.Max(v => v.Volume),
                        v =>
                        {
                            foreach (var volume in volumes)
                            {
                                try { volume.Volume = v; } catch { }
                            }
                        }));
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Audio] Could not list app volumes.");
            }

            return result;
        }

        /// <summary>Re-reads current volumes (changes made elsewhere, e.g. with the keyboard).</summary>
        public void RefreshValues(IEnumerable<AudioEntry> entries)
        {
            foreach (var entry in entries)
            {
                Func<float> read;
                if (readers.TryGetValue(entry.Key, out read))
                {
                    try { entry.UpdateFromSystem(read()); } catch { }
                }
            }
        }

        private static string DescribeProcess(int processId, string fallback)
        {
            if (processId <= 0)
            {
                return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
            }

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    try
                    {
                        var description = process.MainModule?.FileVersionInfo?.FileDescription;
                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            return description.Trim();
                        }
                    }
                    catch
                    {
                        // Access denied for some elevated processes; fall back to the process name.
                    }

                    return process.ProcessName;
                }
            }
            catch
            {
                return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
            }
        }
    }
}
