using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
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

        private bool sliderEnabled = true;

        public AudioEntry(string key, string name, float initial, Action<float> apply)
        {
            Key = key;
            Name = name;
            this.apply = apply;
            volume = Math.Round(initial * 100);
            ToggleMute = new Playnite.SDK.RelayCommand(() => SetMuted(!isMuted));
        }

        /// <summary>Devices can be picked as the output; apps cannot.</summary>
        public bool IsSelectable { get; set; }

        public string DeviceId { get; set; }

        public System.Windows.Input.ICommand Select { get; set; }

        private bool isMuted;

        /// <summary>Sets the Windows mute flag for this device/app.</summary>
        public Action<bool> MuteSetter { get; set; }

        public System.Windows.Input.ICommand ToggleMute { get; private set; }

        public bool IsMuted
        {
            get { return isMuted; }
        }

        public bool CanMute
        {
            get { return sliderEnabled && MuteSetter != null; }
        }

        /// <summary>Speaker with 0-3 sound waves, or the crossed-out speaker when muted.</summary>
        public string IconGlyph
        {
            get
            {
                if (isMuted)
                {
                    return "\uE74F";
                }

                return volume <= 0 ? "\uE992" : volume < 34 ? "\uE993" : volume < 67 ? "\uE994" : "\uE995";
            }
        }

        public System.Windows.Media.Brush IconBrush
        {
            get { return isMuted || !sliderEnabled ? MutedBrush : ActiveBrush; }
        }

        private static readonly System.Windows.Media.Brush ActiveBrush = FrozenBrush(0x7F, 0xB8, 0xFF);
        private static readonly System.Windows.Media.Brush MutedBrush = FrozenBrush(0x6A, 0x6A, 0x6A);

        private static System.Windows.Media.Brush FrozenBrush(byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public void SetMuted(bool muted)
        {
            if (MuteSetter == null)
            {
                return;
            }

            try { MuteSetter(muted); } catch { }
            UpdateMuteFromSystem(muted);
        }

        public void UpdateMuteFromSystem(bool muted)
        {
            if (isMuted == muted)
            {
                return;
            }

            isMuted = muted;
            Raise("IsMuted");
            Raise("IconGlyph");
            Raise("IconBrush");
        }

        public bool SliderEnabled
        {
            get { return sliderEnabled; }
            set
            {
                sliderEnabled = value;
                Raise("SliderEnabled");
                Raise("CanMute");
                Raise("IconBrush");
            }
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

                    // Dragging the volume up unmutes, like the Windows volume flyout.
                    if (isMuted && volume > 0)
                    {
                        SetMuted(false);
                    }
                }

                Raise("Volume");
                Raise("Percent");
                Raise("IconGlyph");
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
        private readonly string volumesFile;
        private readonly Dictionary<string, Func<float>> readers = new Dictionary<string, Func<float>>();
        private readonly Dictionary<string, Func<bool>> muteReaders = new Dictionary<string, Func<bool>>();
        private Dictionary<string, float> rememberedVolumes;
        private MMDeviceEnumerator notifyEnumerator;
        private DeviceNotifications deviceNotifications;
        private readonly List<KeyValuePair<AudioEndpointVolume, AudioEndpointVolumeNotificationDelegate>> volumeSubscriptions =
            new List<KeyValuePair<AudioEndpointVolume, AudioEndpointVolumeNotificationDelegate>>();

        /// <summary>A device was added, removed, enabled/disabled, or the default output changed.</summary>
        public event Action DevicesChanged;

        /// <summary>A device's volume changed anywhere in Windows (key, 0-1 volume).</summary>
        public event Action<string, float, bool> DeviceVolumeChanged;

        /// <summary>Starts listening for device changes (call once).</summary>
        public void StartWatching()
        {
            if (deviceNotifications != null)
            {
                return;
            }

            try
            {
                notifyEnumerator = new MMDeviceEnumerator();
                deviceNotifications = new DeviceNotifications(() => DevicesChanged?.Invoke());
                notifyEnumerator.RegisterEndpointNotificationCallback(deviceNotifications);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Audio] Could not watch for audio device changes.");
                deviceNotifications = null;
            }
        }

        public void StopWatching()
        {
            ClearVolumeSubscriptions();
            try
            {
                if (notifyEnumerator != null && deviceNotifications != null)
                {
                    notifyEnumerator.UnregisterEndpointNotificationCallback(deviceNotifications);
                }
            }
            catch
            {
            }

            deviceNotifications = null;
            notifyEnumerator = null;
        }

        private void ClearVolumeSubscriptions()
        {
            foreach (var pair in volumeSubscriptions)
            {
                try { pair.Key.OnVolumeNotification -= pair.Value; } catch { }
            }

            volumeSubscriptions.Clear();
        }

        private sealed class DeviceNotifications : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
        {
            private readonly Action changed;

            public DeviceNotifications(Action changed)
            {
                this.changed = changed;
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) { changed(); }

            public void OnDeviceAdded(string pwstrDeviceId) { changed(); }

            public void OnDeviceRemoved(string deviceId) { changed(); }

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render && role == Role.Multimedia)
                {
                    changed();
                }
            }

            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }

        public AudioMixerSource(ILogger logger, string dataDirectory)
        {
            this.logger = logger;
            volumesFile = System.IO.Path.Combine(dataDirectory ?? string.Empty, "device_volumes.json");
        }

        private Dictionary<string, float> Remembered
        {
            get
            {
                if (rememberedVolumes == null)
                {
                    try
                    {
                        rememberedVolumes = System.IO.File.Exists(volumesFile)
                            ? Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, float>>(System.IO.File.ReadAllText(volumesFile))
                            : null;
                    }
                    catch
                    {
                        rememberedVolumes = null;
                    }

                    rememberedVolumes = rememberedVolumes ?? new Dictionary<string, float>();
                }

                return rememberedVolumes;
            }
        }

        private void SaveRemembered()
        {
            try
            {
                System.IO.File.WriteAllText(volumesFile, Newtonsoft.Json.JsonConvert.SerializeObject(Remembered));
            }
            catch
            {
            }
        }

        /// <summary>
        /// Makes the device the Windows output for all sounds. The previous output is set to
        /// zero (its volume is remembered), and the new one gets back its remembered volume.
        /// </summary>
        public bool SwitchOutputTo(string deviceId)
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                MMDevice previous = null;
                try { previous = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
                if (previous != null && previous.ID == deviceId)
                {
                    return true;
                }

                var target = enumerator.GetDevice(deviceId);
                if (target == null)
                {
                    return false;
                }

                if (!PolicyConfig.SetDefault(deviceId))
                {
                    logger?.Warn("[AnikiHelper][Audio] Windows refused to change the default output device.");
                    return false;
                }

                if (previous != null)
                {
                    var previousVolume = previous.AudioEndpointVolume.MasterVolumeLevelScalar;
                    if (previousVolume > 0.01f)
                    {
                        Remembered[previous.ID] = previousVolume;
                    }

                    previous.AudioEndpointVolume.MasterVolumeLevelScalar = 0f;
                }

                float restore;
                if (Remembered.TryGetValue(deviceId, out restore) && restore > 0.01f)
                {
                    target.AudioEndpointVolume.MasterVolumeLevelScalar = restore;
                }
                else if (target.AudioEndpointVolume.MasterVolumeLevelScalar < 0.05f)
                {
                    target.AudioEndpointVolume.MasterVolumeLevelScalar = 0.5f;
                }

                SaveRemembered();
                return true;
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Audio] Could not switch the output device.");
                return false;
            }
        }

        public List<AudioEntry> GetDevices()
        {
            var result = new List<AudioEntry>();
            readers.Clear();
            muteReaders.Clear();
            ClearVolumeSubscriptions();
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
                    var inUse = device.ID == defaultId;
                    var name = device.FriendlyName + (inUse ? "  \u2022  In use" : string.Empty);
                    var key = "device:" + device.ID;
                    if (inUse)
                    {
                        readers[key] = () => endpoint.MasterVolumeLevelScalar;
                        muteReaders[key] = () => endpoint.Mute;
                        var entryKey = key;
                        AudioEndpointVolumeNotificationDelegate handler = data => DeviceVolumeChanged?.Invoke(entryKey, data.MasterVolume, data.Muted);
                        try
                        {
                            endpoint.OnVolumeNotification += handler;
                            volumeSubscriptions.Add(new KeyValuePair<AudioEndpointVolume, AudioEndpointVolumeNotificationDelegate>(endpoint, handler));
                        }
                        catch
                        {
                        }
                    }

                    result.Add(new AudioEntry(
                        key,
                        name,
                        inUse ? endpoint.MasterVolumeLevelScalar : 0f,
                        v =>
                        {
                            if (inUse)
                            {
                                endpoint.MasterVolumeLevelScalar = v;
                                float reported = -1;
                                try { reported = endpoint.MasterVolumeLevelScalar; } catch { }
                                logger?.Info("[AnikiHelper][Audio][VolumeCheck] Set '" + device.FriendlyName +
                                             "' to " + v.ToString("0.00") + ", Windows now reports " + reported.ToString("0.00") +
                                             " (hardware steps: " + SafeSteps(endpoint) + ")");
                            }
                        })
                    {
                        IsSelectable = true,
                        DeviceId = device.ID,
                        SliderEnabled = inUse,
                        MuteSetter = inUse ? new Action<bool>(m =>
                        {
                            endpoint.Mute = m;
                            logger?.Info("[AnikiHelper][Audio] " + (m ? "Muted '" : "Unmuted '") + device.FriendlyName + "'");
                        }) : null
                    });
                    if (inUse)
                    {
                        try { result[result.Count - 1].UpdateMuteFromSystem(endpoint.Mute); } catch { }
                    }
                }

                // The device in use first.
                result = result.OrderByDescending(e => e.SliderEnabled).ToList();
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
            muteReaders.Clear();
            ClearVolumeSubscriptions();
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
                    muteReaders[key] = () => volumes.All(v => v.Mute);
                    var appEntry = new AudioEntry(
                        key,
                        name,
                        volumes.Max(v => v.Volume),
                        v =>
                        {
                            foreach (var volume in volumes)
                            {
                                try { volume.Volume = v; } catch { }
                            }
                        })
                    {
                        MuteSetter = m =>
                        {
                            foreach (var volume in volumes)
                            {
                                try { volume.Mute = m; } catch { }
                            }
                        }
                    };
                    try { appEntry.UpdateMuteFromSystem(volumes.All(v => v.Mute)); } catch { }
                    result.Add(appEntry);
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

                Func<bool> readMute;
                if (muteReaders.TryGetValue(entry.Key, out readMute))
                {
                    try { entry.UpdateMuteFromSystem(readMute()); } catch { }
                }
            }
        }

        private static string SafeSteps(AudioEndpointVolume endpoint)
        {
            try
            {
                return endpoint.StepInformation.StepCount.ToString();
            }
            catch
            {
                return "?";
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

    internal static class PolicyConfig
    {
        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class CPolicyConfigClient
        {
        }

        [System.Runtime.InteropServices.Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [System.Runtime.InteropServices.PreserveSig] int GetMixFormat([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, IntPtr format);
            [System.Runtime.InteropServices.PreserveSig] int GetDeviceFormat([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, bool defaultFormat, IntPtr format);
            [System.Runtime.InteropServices.PreserveSig] int ResetDeviceFormat([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id);
            [System.Runtime.InteropServices.PreserveSig] int SetDeviceFormat([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
            [System.Runtime.InteropServices.PreserveSig] int GetProcessingPeriod([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, bool defaultPeriod, IntPtr defaultPeriodOut, IntPtr minimumPeriod);
            [System.Runtime.InteropServices.PreserveSig] int SetProcessingPeriod([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, IntPtr period);
            [System.Runtime.InteropServices.PreserveSig] int GetShareMode([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, IntPtr mode);
            [System.Runtime.InteropServices.PreserveSig] int SetShareMode([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, IntPtr mode);
            [System.Runtime.InteropServices.PreserveSig] int GetPropertyValue([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [System.Runtime.InteropServices.PreserveSig] int SetPropertyValue([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [System.Runtime.InteropServices.PreserveSig] int SetDefaultEndpoint([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, int role);
            [System.Runtime.InteropServices.PreserveSig] int SetEndpointVisibility([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, bool visible);
        }

        /// <summary>Sets the device as default for console, multimedia and communications.</summary>
        public static bool SetDefault(string deviceId)
        {
            object client = null;
            try
            {
                client = new CPolicyConfigClient();
                var config = (IPolicyConfig)client;
                var ok = true;
                for (var role = 0; role <= 2; role++)
                {
                    ok &= config.SetDefaultEndpoint(deviceId, role) >= 0;
                }

                return ok;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (client != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(client); } catch { }
                }
            }
        }
    }
}
