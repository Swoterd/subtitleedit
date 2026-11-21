using Nikse.SubtitleEdit.Core.Common;
using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Nikse.SubtitleEdit.Logic.VideoPlayers.MpcHC
{
    public class MpcHc : VideoPlayer, IDisposable
    {
        private readonly object _locker = new object();

        private const string ModePlay = "0";
        private const string ModePause = "1";
        private string _playMode = string.Empty;
        private int _loaded;
        private IntPtr _mpcHandle = IntPtr.Zero;
        private IntPtr _mainWindowHandle = IntPtr.Zero;
        private IntPtr _videoHandle = IntPtr.Zero;
        private IntPtr _videoPanelHandle = IntPtr.Zero;
        private ProcessStartInfo _startInfo;
        private Process _process;
        private IntPtr _messageHandlerHandle = IntPtr.Zero;
        private string _videoFileName;
        private Timer _positionTimer;
        private double _positionInSeconds;
        private double? _queuedSeekPosition;
        private double? _activeSeekPosition;
        private bool _resumeAfterSeek;
        private bool _seekContinuationScheduled;
        private double _durationInSeconds;
        private double _currentPlayRate = 1.0;
        private MessageHandlerWindow _form;
        private Control _ownerControl;
        private int _lastLoggedResizeWidth = -1;
        private int _lastLoggedResizeHeight = -1;
        private Timer _hideMpcTimer = new Timer();
        private int _hideMpcTimerCount;

        public override string PlayerName => "MPC-HC";

        private int _volume = 75;

        public override int Volume
        {
            get => _volume;
            set
            {
                // MPC-HC moves from 0-100 in steps of 5
                for (int i = 0; i < 100; i += 5)
                {
                    SendMpcMessage(MpcHcCommand.DecreaseVolume);
                }

                for (_volume = 0; _volume < value; _volume += 5)
                {
                    SendMpcMessage(MpcHcCommand.IncreaseVolume);
                }
            }
        }

        public override double Duration => _durationInSeconds;

        public override double CurrentPosition
        {
            get => _positionInSeconds;
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || _mpcHandle == IntPtr.Zero)
                {
                    return;
                }

                var resume = _resumeAfterSeek || IsPlaying;
                if (!_activeSeekPosition.HasValue && IsPlaying)
                {
                    Pause();
                }

                value = Math.Max(0, _durationInSeconds > 0 ? Math.Min(value, _durationInSeconds) : value);
                _positionInSeconds = value;
                _queuedSeekPosition = value;
                _resumeAfterSeek = resume;
                _playMode = resume ? ModePlay : ModePause;
                ContinuePendingSeek();
            }
        }

        public override double PlayRate
        {
            get => _currentPlayRate;
            set
            {
                if (value >= 0 && value <= 3.0)
                {
                    SendMpcMessage(MpcHcCommand.SetSpeed, value.ToString(CultureInfo.InvariantCulture));
                    _currentPlayRate = value;
                }
            }
        }

        public override void Play()
        {
            _playMode = ModePlay;
            if (_activeSeekPosition.HasValue || _queuedSeekPosition.HasValue || _seekContinuationScheduled)
            {
                _resumeAfterSeek = true;
                return;
            }

            SendPlayerCommand(MpcHcCommand.PlayWindowCommand);
        }

        public override void Pause()
        {
            _resumeAfterSeek = false;
            _playMode = ModePause;
            SendPlayerCommand(MpcHcCommand.PauseWindowCommand);
        }

        public override void Stop()
        {
            _resumeAfterSeek = false;
            _queuedSeekPosition = null;
            _activeSeekPosition = null;
            _playMode = ModePause;
            SendMpcMessage(MpcHcCommand.Stop);
        }

        private void ContinuePendingSeek()
        {
            if (_activeSeekPosition.HasValue || _seekContinuationScheduled || _mpcHandle == IntPtr.Zero || _loaded < 2)
            {
                return;
            }

            if (_queuedSeekPosition.HasValue)
            {
                var position = _queuedSeekPosition.Value;
                _queuedSeekPosition = null;
                _activeSeekPosition = position;
                SendMpcMessage(MpcHcCommand.SetPosition, position.ToString("0.000", CultureInfo.InvariantCulture));
            }
            else if (_resumeAfterSeek)
            {
                _resumeAfterSeek = false;
                Play();
            }
        }

        private void CompletePendingSeek(double position)
        {
            if (!_activeSeekPosition.HasValue)
            {
                return;
            }

            _activeSeekPosition = null;
            if (!_queuedSeekPosition.HasValue)
            {
                _positionInSeconds = position;
            }

            // The notification arrives inside MPC's seek command. Continue after its call returns.
            if (!_seekContinuationScheduled && _form != null && !_form.IsDisposed && _form.IsHandleCreated)
            {
                _seekContinuationScheduled = true;
                _form.BeginInvoke(new Action(() =>
                {
                    _seekContinuationScheduled = false;
                    ContinuePendingSeek();
                }));
            }
        }

        private void SendPlayerCommand(int command)
        {
            if (_mpcHandle != IntPtr.Zero)
            {
                // The public Play/Pause API posts WM_COMMAND internally and can race a following seek.
                const int wmCommand = 0x0111;
                NativeMethods.SendMessage(_mpcHandle, wmCommand, new IntPtr(command), IntPtr.Zero);
            }
        }

        public override bool IsPaused => _playMode == ModePause;

        public override bool IsPlaying => _playMode == ModePlay;

        public override void Initialize(Control ownerControl, string videoFileName, EventHandler onVideoLoaded, EventHandler onVideoEnded)
        {
            if (ownerControl == null)
            {
                return;
            }

            VideoFileName = videoFileName;
            OnVideoLoaded = onVideoLoaded;
            OnVideoEnded = onVideoEnded;

            _ownerControl = ownerControl;
            _form = new MessageHandlerWindow();
            _form.OnCopyData += OnCopyData;
            _form.Show();
            _form.Hide();
            _videoPanelHandle = ownerControl.Handle;
            _messageHandlerHandle = _form.Handle;
            _videoFileName = videoFileName;
            _positionTimer = new Timer { Interval = 100 };
            _positionTimer.Tick += PositionTimerTick;
            _startInfo = new ProcessStartInfo
            {
                FileName = GetMpcFileName(),
                Arguments = "/new /minimized /slave " + _messageHandlerHandle
            };
            _process = Process.Start(_startInfo);
            _process?.WaitForInputIdle();
            CacheMainWindowHandle();
        }

        private void PositionTimerTick(object sender, EventArgs e)
        {
            SendMpcMessage(MpcHcCommand.GetCurrentPosition);
        }

        private void OnCopyData(object sender, EventArgs e)
        {
            var message = (Message)sender;
            var cds = (NativeMethods.CopyDataStruct)Marshal.PtrToStructure(message.LParam, typeof(NativeMethods.CopyDataStruct));
            var command = cds.dwData.ToUInt32();
            var param = Marshal.PtrToStringAuto(cds.lpData);
            var multiParam = new string[0];
            if (param != null)
            {
                multiParam = param.Split('|');
            }

            switch (command)
            {
                case MpcHcCommand.Connect:
                    _positionTimer?.Stop();
                    _mpcHandle = (IntPtr)Convert.ToInt64(Marshal.PtrToStringAuto(cds.lpData));
                    SendMpcMessage(MpcHcCommand.OpenFile, _videoFileName);
                    _positionTimer?.Start();
                    break;
                case MpcHcCommand.PlayMode:
                    _playMode = _activeSeekPosition.HasValue || _queuedSeekPosition.HasValue || _seekContinuationScheduled
                        ? (_resumeAfterSeek ? ModePlay : ModePause)
                        : param;
                    if (param == ModePlay && _loaded == 0)
                    {
                        _loaded = 1;
                        if (!HijackMpcHc())
                        {
                            Application.DoEvents();
                            HijackMpcHc();
                        }
                    }
                    HideMpcPlayerWindow();
                    break;
                case MpcHcCommand.NowPlaying:
                    if (_loaded == 1)
                    {
                        _loaded = 2;

                        _durationInSeconds = 5000;
                        if (multiParam.Length >= 5 && double.TryParse(multiParam[4].Replace(",", ".").Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
                        {
                            _durationInSeconds = d;
                        }
                        else if (multiParam.Length >= 1 && double.TryParse(multiParam[multiParam.Length - 1].Replace(",", ".").Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out d))
                        {
                            _durationInSeconds = d;
                        }

                        ResizeToOwnerControl();
                        OnVideoLoaded?.Invoke(this, EventArgs.Empty);

                        SendMpcMessage(MpcHcCommand.SetSubtitleTrack, "-1");

                        // be sure to hide MPC
                        _hideMpcTimerCount = 20;
                        _hideMpcTimer.Interval = 100;
                        _hideMpcTimer.Tick += (o, args) =>
                        {
                            _hideMpcTimer.Stop();
                            if (_hideMpcTimerCount > 0)
                            {
                                Application.DoEvents();
                                HideMpcPlayerWindow();
                                _hideMpcTimerCount--;
                                _hideMpcTimer.Start();
                            }
                        };
                        _hideMpcTimer.Start();
                        Pause();
                        ContinuePendingSeek();
                    }
                    break;
                case MpcHcCommand.NotifyEndOfStream:
                    OnVideoEnded?.Invoke(this, EventArgs.Empty);

                    break;
                case MpcHcCommand.NotifySeek:
                    if (double.TryParse(param?.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var seekPosition))
                    {
                        CompletePendingSeek(seekPosition);
                    }
                    break;
                case MpcHcCommand.CurrentPosition:
                    if (!_activeSeekPosition.HasValue && !_queuedSeekPosition.HasValue && !string.IsNullOrWhiteSpace(param))
                    {
                        if (double.TryParse(param.Replace(",", "."), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
                        {
                            _positionInSeconds = d;
                        }
                    }
                    break;
            }
        }

        private void ResizeToOwnerControl()
        {
            if (_ownerControl == null || _ownerControl.IsDisposed)
            {
                return;
            }

            Resize(_ownerControl.Width, _ownerControl.Height);
        }

        private void HideMpcPlayerWindow()
        {
            var handle = GetMainWindowHandle();
            if (handle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.ShowWindow(handle, NativeMethods.ShowWindowCommands.Hide);
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, -9999, -9999, 0, 0,
                NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE |
                NativeMethods.SetWindowPosFlags.SWP_NOZORDER |
                NativeMethods.SetWindowPosFlags.SWP_NOOWNERZORDER);
        }

        private IntPtr GetMainWindowHandle()
        {
            if (_mainWindowHandle != IntPtr.Zero)
            {
                return _mainWindowHandle;
            }

            return CacheMainWindowHandle();
        }

        private IntPtr CacheMainWindowHandle()
        {
            if (_process == null || _process.HasExited)
            {
                return IntPtr.Zero;
            }

            _process.Refresh();
            var handle = _process.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                _mainWindowHandle = handle;
            }

            return _mainWindowHandle;
        }

        internal static bool GetWindowHandle(IntPtr windowHandle, ArrayList windowHandles)
        {
            windowHandles.Add(windowHandle);
            return true;
        }

        private ArrayList GetChildWindows()
        {
            return GetChildWindows(GetMainWindowHandle());
        }

        private static ArrayList GetChildWindows(IntPtr parentHandle)
        {
            var windowHandles = new ArrayList();
            NativeMethods.EnumedWindow callBackPtr = GetWindowHandle;
            NativeMethods.EnumChildWindows(parentHandle, callBackPtr, windowHandles);
            return windowHandles;
        }

        private static bool IsWindowMpcHcVideo(IntPtr hWnd)
        {
            var className = new StringBuilder(256);
            int returnCode = NativeMethods.GetClassName(hWnd, className, className.Capacity); // Get the window class name
            if (returnCode != 0)
            {
                var cName = className.ToString();
                return cName.EndsWith(":b:0000000000010003:0000000000000006:0000000000000000") || // MPC-HC 64-bit video class???
                       cName.EndsWith(":b:0000000000010004:0000000000000006:0000000000000000") || // MPC-HC 64-bit video class???
                       cName.EndsWith(":b:0000000000010005:0000000000000006:0000000000000000") || // MPC-HC 64-bit video class???
                       cName.EndsWith(":b:0000000000010006:0000000000000006:0000000000000000") || // MPC-HC 64-bit video class???
                       cName.EndsWith(":b:0000000000010007:0000000000000006:0000000000000000") || // MPC-HC 64-bit video class???
                       cName.EndsWith(":b:00010003:00000006:00000000");                           // MPC-HC 32-bit video class???
            }

            return false;
        }

        private bool HijackMpcHc()
        {
            IntPtr handle = GetMainWindowHandle();
            var handles = GetChildWindows();
            foreach (var h in handles)
            {
                if (IsWindowMpcHcVideo((IntPtr)h))
                {
                    _videoHandle = (IntPtr)h;
                    NativeMethods.SetParent((IntPtr)h, _videoPanelHandle);
                    NativeMethods.SetWindowPos(handle, IntPtr.Zero, -9999, -9999, 0, 0,
                        NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE |
                        NativeMethods.SetWindowPosFlags.SWP_NOZORDER |
                        NativeMethods.SetWindowPosFlags.SWP_NOOWNERZORDER);
                    return true;
                }
            }

            SeLogger.Error("Unable to find mpc video window");
            return false;
        }

        public override void Resize(int width, int height)
        {
            if (_process == null || _videoHandle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.ShowWindow(GetMainWindowHandle(), NativeMethods.ShowWindowCommands.ShowNoActivate);
            // Raise the renderer inside its video panel, without activating it or its owner.
            NativeMethods.SetWindowPos(_videoHandle, (IntPtr)NativeMethods.SpecialWindowHandles.HWND_TOP, 0, 0, width, height,
                NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE |
                NativeMethods.SetWindowPosFlags.SWP_NOOWNERZORDER);
            HideMpcPlayerWindow();
        }

        private static void ResizeChildWindows(IntPtr parentHandle, int width, int height)
        {
            foreach (var child in GetChildWindows(parentHandle))
            {
                var childHandle = (IntPtr)child;
                NativeMethods.ShowWindow(childHandle, NativeMethods.ShowWindowCommands.ShowNoActivate);
                NativeMethods.MoveWindow(childHandle, 0, 0, width, height, true);
                NativeMethods.SetWindowPos(
                    childHandle,
                    IntPtr.Zero,
                    0,
                    0,
                    width,
                    height,
                    NativeMethods.SetWindowPosFlags.SWP_NOZORDER |
                    NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE |
                    NativeMethods.SetWindowPosFlags.SWP_SHOWWINDOW |
                    NativeMethods.SetWindowPosFlags.SWP_FRAMECHANGED);
                NotifyResize(childHandle, width, height);
            }
        }

        private static void NotifyResize(IntPtr handle, int width, int height)
        {
            var sizeParam = new IntPtr((height << 16) | (width & 0xffff));
            NativeMethods.SendMessage(handle, NativeMethods.WindowsMessageSize, IntPtr.Zero, sizeParam);
            NativeMethods.InvalidateRect(handle, IntPtr.Zero, true);
        }

        private void LogResizeState(string phase, int width, int height, bool force = false)
        {
            if (!force && _lastLoggedResizeWidth == width && _lastLoggedResizeHeight == height)
            {
                return;
            }

            _lastLoggedResizeWidth = width;
            _lastLoggedResizeHeight = height;

            try
            {
                var processState = _process == null ? "null" : (_process.HasExited ? "exited" : "running");
                var mainWindowHandle = GetMainWindowHandle();

                var rectText = "n/a";
                if (_videoHandle != IntPtr.Zero && NativeMethods.GetWindowRect(_videoHandle, out var rect))
                {
                    rectText = $"{rect.Width}x{rect.Height}@{rect.Left},{rect.Top}";
                }

                var mainRectText = "n/a";
                if (mainWindowHandle != IntPtr.Zero && NativeMethods.GetWindowRect(mainWindowHandle, out var mainRect))
                {
                    mainRectText = $"{mainRect.Width}x{mainRect.Height}@{mainRect.Left},{mainRect.Top}";
                }

                var childText = GetChildWindowDebugText(_videoHandle);
                var fileName = Path.Combine(Configuration.DataDirectory, "video_resize_log.txt");
                File.AppendAllText(fileName,
                    $"{DateTime.Now:O} MpcHc.Resize.{phase} " +
                    $"requested={width}x{height}, process={processState}, mainHandle=0x{mainWindowHandle.ToInt64():X}, " +
                    $"mainRect={mainRectText}, videoHandle=0x{_videoHandle.ToInt64():X}, panelHandle=0x{_videoPanelHandle.ToInt64():X}, " +
                    $"videoRect={rectText}, children={childText}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostic logging must never affect playback.
            }
        }

        private static string GetChildWindowDebugText(IntPtr parentHandle)
        {
            if (parentHandle == IntPtr.Zero)
            {
                return "n/a";
            }

            var result = new StringBuilder();
            foreach (var child in GetChildWindows(parentHandle))
            {
                var childHandle = (IntPtr)child;
                var className = new StringBuilder(256);
                NativeMethods.GetClassName(childHandle, className, className.Capacity);
                var rectText = "n/a";
                if (NativeMethods.GetWindowRect(childHandle, out var rect))
                {
                    rectText = $"{rect.Width}x{rect.Height}@{rect.Left},{rect.Top}";
                }

                if (result.Length > 0)
                {
                    result.Append("; ");
                }

                result.Append("0x");
                result.Append(childHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture));
                result.Append(":");
                result.Append(className);
                result.Append(":");
                result.Append(rectText);
            }

            return result.Length == 0 ? "none" : result.ToString();
        }

        public static string GetMpcFileName()
        {
            return GetMpcFileName("_nvo") ?? GetMpcFileName(string.Empty);
        }

        private static string GetMpcFileNameInner(string fileNameSuffix, string prefix)
        {
            if (IntPtr.Size == 8) // 64-bit
            {
                var fileName = $"{prefix}64{fileNameSuffix}.exe";
                var path = Path.Combine(Configuration.BaseDirectory, prefix.ToUpperInvariant(), fileName);
                if (File.Exists(path))
                {
                    return path;
                }

                if (!string.IsNullOrEmpty(Configuration.Settings.General.MpcHcLocation))
                {
                    path = Configuration.Settings.General.MpcHcLocation;
                    if (File.Exists(path) && path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }

                    if (Directory.Exists(Configuration.Settings.General.MpcHcLocation))
                    {
                        path = Path.Combine(Configuration.Settings.General.MpcHcLocation, fileName);
                        if (File.Exists(path))
                        {
                            return path;
                        }

                        path = Path.Combine(Configuration.Settings.General.MpcHcLocation, prefix.ToUpperInvariant(), fileName);
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                }

                path = RegistryUtil.GetValue(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{2ACBF1FA-F5C3-4B19-A774-B22A31F231B9}_is1", "InstallLocation");
                if (path != null)
                {
                    path = Path.Combine(path, fileName);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }

                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), prefix.ToUpperInvariant(), fileName);
                if (File.Exists(path))
                {
                    return path;
                }

                path = $@"C:\Program Files\{prefix.ToUpperInvariant()}\{fileName}";
                if (File.Exists(path))
                {
                    return path;
                }

                path = $@"C:\Program Files\{prefix.ToUpperInvariant()} x64\{fileName}";
                if (File.Exists(path))
                {
                    return path;
                }

                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $@"K-Lite Codec Pack\{prefix.ToUpperInvariant()}\{fileName}");
                if (File.Exists(path))
                {
                    return path;
                }

                path = $@"C:\Program Files (x86)\K-Lite Codec Pack\{prefix.ToUpperInvariant()}64\{fileName}";
                if (File.Exists(path))
                {
                    return path;
                }

                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $@"K-Lite Codec Pack\{prefix.ToUpperInvariant()}\{fileName}");
                if (File.Exists(path))
                {
                    return path;
                }

                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $@"K-Lite\{prefix.ToUpperInvariant()}\{fileName}");
                if (File.Exists(path))
                {
                    return path;
                }

                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $@"K-Lite\{prefix.ToUpperInvariant()}64\{fileName}");
                if (File.Exists(path))
                {
                    return path;
                }

                path = $@"C:\Program Files (x86)\{prefix.ToUpperInvariant()}\{fileName}";
                if (File.Exists(path))
                {
                    return path;
                }
            }
            else // 32-bit
            {
                var fileName = $"mpc-hc{fileNameSuffix}.exe";
                var path = Path.Combine(Configuration.BaseDirectory, prefix.ToUpperInvariant(), fileName);
                if (File.Exists(path))
                {
                    return path;
                }

                if (!string.IsNullOrEmpty(Configuration.Settings.General.MpcHcLocation))
                {
                    path = Configuration.Settings.General.MpcHcLocation;
                    if (File.Exists(path) && path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }

                    if (Directory.Exists(Configuration.Settings.General.MpcHcLocation))
                    {
                        path = Path.Combine(Configuration.Settings.General.MpcHcLocation, fileName);
                        if (File.Exists(path))
                        {
                            return path;
                        }

                        path = Path.Combine(Configuration.Settings.General.MpcHcLocation, prefix.ToUpperInvariant(), fileName);
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                }

                path = RegistryUtil.GetValue(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{2624B969-7135-4EB1-B0F6-2D8C397B45F7}_is1", "InstallLocation");
                if (path != null)
                {
                    path = Path.Combine(path, fileName);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }

                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), prefix.ToUpperInvariant(), fileName);
                if (File.Exists(path))
                {
                    return path;
                }

                path = $@"C:\Program Files (x86)\{prefix.ToUpperInvariant()}\{fileName}";
                if (File.Exists(path))
                {
                    return path;
                }

                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $@"K-Lite Codec Pack\{prefix.ToUpperInvariant()}\{fileName}");
                if (File.Exists(path))
                {
                    return path;
                }

                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $@"K-Lite\{prefix.ToUpperInvariant()}\{fileName}");
                if (File.Exists(path))
                {
                    return path;
                }

                path = $@"C:\Program Files\{prefix.ToUpperInvariant()}\{fileName}";
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static string GetMpcFileName(string fileNameSuffix)
        {
            if (!Configuration.IsRunningOnWindows) //short circuit on Linux to resolve issues with read-only filesystems
            {
                return null;
            }

            var mpcFileName = GetMpcFileNameInner(fileNameSuffix, "mpc-hc");
            if (string.IsNullOrEmpty(mpcFileName))
            {
                mpcFileName = GetMpcFileNameInner(fileNameSuffix, "mpc-be");
            }

            return mpcFileName;
        }

        public static bool IsInstalled => true;

        public override void DisposeVideoPlayer()
        {
            Dispose();
        }

        public override event EventHandler OnVideoLoaded;

        public override event EventHandler OnVideoEnded;

        private void ReleaseUnmanagedResources()
        {
            try
            {
                lock (_locker)
                {
                    if (_mpcHandle != IntPtr.Zero)
                    {
                        SendMpcMessage(MpcHcCommand.CloseApplication);
                        _mpcHandle = IntPtr.Zero;
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        ~MpcHc()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    // release managed resources
                    _queuedSeekPosition = null;
                    _activeSeekPosition = null;
                    _resumeAfterSeek = false;
                    if (_positionTimer != null)
                    {
                        _positionTimer.Stop();
                        _positionTimer.Dispose();
                        _positionTimer = null;
                    }
                    if (_hideMpcTimer != null)
                    {
                        _hideMpcTimer.Stop();
                        _hideMpcTimer.Dispose();
                        _hideMpcTimer = null;
                    }

                    if (_form != null)
                    {
                        _form.OnCopyData -= OnCopyData;
                        //_form.Dispose(); this gives an error when doing File -> Exit...
                        _form = null;
                    }

                    if (_process != null)
                    {
                        _process.Dispose();
                        _process = null;
                    }
                    _startInfo = null;
                }
                ReleaseUnmanagedResources();
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message);
            }
        }

        private void SendMpcMessage(uint command)
        {
            SendMpcMessage(command, string.Empty);
        }

        private void SendMpcMessage(uint command, string parameter)
        {
            if (_mpcHandle == IntPtr.Zero || _messageHandlerHandle == IntPtr.Zero)
            {
                return;
            }

            parameter += (char)0;
            NativeMethods.CopyDataStruct cds;
            cds.dwData = (UIntPtr)command;
            cds.cbData = parameter.Length * Marshal.SystemDefaultCharSize;
            cds.lpData = Marshal.StringToCoTaskMemAuto(parameter);
            try
            {
                NativeMethods.SendMessage(_mpcHandle, NativeMethods.WindowsMessageCopyData, _messageHandlerHandle, ref cds);
            }
            finally
            {
                Marshal.FreeCoTaskMem(cds.lpData);
            }
        }

    }
}
