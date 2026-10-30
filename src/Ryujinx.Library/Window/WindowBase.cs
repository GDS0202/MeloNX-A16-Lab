using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Callbacks;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.Input.Native;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AntiAliasing = Ryujinx.Common.Configuration.AntiAliasing;
using ScalingFilter = Ryujinx.Common.Configuration.ScalingFilter;
using Switch = Ryujinx.HLE.Switch;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Buffers.Binary;
using Ryujinx.Common.Utilities;
using Ryujinx.Graphics.Texture;
// using Ryujinx.Ava.UI.Models;


namespace Ryujinx.Library
{
    abstract partial class WindowBase: IHostUIHandler, IDisposable
    {
        protected const int DefaultWidth = 1280;
        protected const int DefaultHeight = 720;
        // Telemetry cadence only: this never changes emulation or presentation FPS.
        private const int StatisticsUpdatesPerSecond = 2;

        private static readonly ConcurrentQueue<Action> _mainThreadActions = new();

        public static void QueueMainThreadAction(Action action)
        {
            _mainThreadActions.Enqueue(action);
        }

        public bool _isPaused;
        public ManualResetEvent _pauseEvent;

        public NpadManager NpadManager;
        public TouchScreenManager TouchScreenManager;
        public Switch Device;
        public IRenderer Renderer;

        public IntPtr WindowHandle;

        public IHostUITheme HostUITheme { get; }
        public static int Width { get; private set; }
        public static int Height { get; private set; }
        public int DisplayId { get; set; }
        public bool IsFullscreen { get; set; }
        public bool IsExclusiveFullscreen { get; set; }
        public int ExclusiveFullscreenWidth { get; set; }
        public int ExclusiveFullscreenHeight { get; set; }
        public AntiAliasing AntiAliasing { get; set; }
        public ScalingFilter ScalingFilter { get; set; }
        public int ScalingFilterLevel { get; set; }
        public float MetalFxUpscaleFactor { get; set; } = 2.0f;

        public NativeTouchDriver MouseDriver;
        private readonly InputManager _inputManager;
        private readonly GraphicsDebugLevel _glLogLevel;
        private readonly Stopwatch _chrono;
        private readonly long _ticksPerFrame;
        private readonly CancellationTokenSource _gpuCancellationTokenSource;
        public ManualResetEvent _exitEvent;
        public ManualResetEvent _gpuDoneEvent;
        private readonly AutoResetEvent _inputUpdatedEvent;
        private volatile StatisticsRequest? _latestStatistics;
        private CancellationTokenSource? _statisticsCts;


        private long _ticks;
        public volatile bool _isActive;
        private volatile bool _isStopped;
        private int _exitRequested;
        private int _disposed;
        private Thread _renderLoopThread;
        private LiveGraphicsSettings _pendingGraphics;

        public bool _ranFirstFrame;

        private string _gpuDriverName;

        public AspectRatio _aspectRatio;
        private readonly bool _enableMouse;


        public WindowBase(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode)
        {
            MouseDriver = new NativeTouchDriver(hideCursorMode);
            _inputManager = inputManager;
            _inputManager.SetMouseDriver(MouseDriver);
            NpadManager = _inputManager.CreateNpadManager();
            TouchScreenManager = _inputManager.CreateTouchScreenManager();
            _glLogLevel = glLogLevel;
            _chrono = new Stopwatch();
            _ticksPerFrame = Stopwatch.Frequency / StatisticsUpdatesPerSecond;
            _gpuCancellationTokenSource = new CancellationTokenSource();
            _exitEvent = new ManualResetEvent(false);
            _gpuDoneEvent = new ManualResetEvent(false);
            _inputUpdatedEvent = new AutoResetEvent(false);
            _pauseEvent = new ManualResetEvent(true);
            _aspectRatio = aspectRatio;
            _enableMouse = enableMouse;
            HostUITheme = new NativeHostUiTheme();
            NativeGamepadDriver.OnInputUpdated += SignalInputUpdated;

            Width = DefaultWidth;
            Height = DefaultHeight;
        }

        private void SignalInputUpdated()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _inputUpdatedEvent.Set(); }
            catch (ObjectDisposedException) { } // A publisher may already have copied the event delegate.
        }

        public void Initialize(Switch device, List<InputConfig> inputConfigs, bool enableKeyboard, bool enableMouse)
        {
            Device = device;

            IRenderer renderer = Device.Gpu.Renderer;

            if (renderer is ThreadedRenderer tr)
            {
                renderer = tr.BaseRenderer;
            }

            Renderer = renderer;

            NpadManager.Initialize(device, inputConfigs, enableKeyboard, enableMouse);
            TouchScreenManager.Initialize(device);
        }

        [UnmanagedCallersOnly(EntryPoint = "set_view_size")]
        static unsafe void set_view_size(int width, int height)
        {
            Width = width;
            Height = height;


        }

        private void InitializeWindow()
        {
            Width = ExclusiveFullscreenWidth;
            Height = ExclusiveFullscreenHeight;

            MouseDriver.SetClientSize(Width, Height);
        }

        protected abstract void InitializeWindowRenderer();

        protected abstract void InitializeRenderer();

        protected abstract void FinalizeWindowRenderer();

        protected abstract void SwapBuffers();

        private string GetGpuDriverName()
        {
            return Renderer.GetHardwareInfo().GpuDriver;
        }

        private void SetAntiAliasing()
        {
            Renderer?.Window.SetAntiAliasing(AntiAliasing);
        }

        private void SetScalingFilter()
        {
            Renderer?.Window.SetScalingFilter(ScalingFilter);
            Renderer?.Window.SetScalingFilterLevel(ScalingFilterLevel);
            Renderer?.Window.SetMetalFxUpscaleFactor(MetalFxUpscaleFactor);
        }

        public void Render()
        {
            InitializeWindowRenderer();

            Device.Gpu.Renderer.Initialize(_glLogLevel);

            InitializeRenderer();

            SetAntiAliasing();

            SetScalingFilter();

            _gpuDriverName = GetGpuDriverName();

            _ranFirstFrame = false;

            Device.Gpu.Renderer.RunLoop(() =>
            {
                Device.Gpu.SetGpuThread();
                try
                {
                    Device.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);

                    while (_isActive)
                    {
                        if (_isStopped)
                        {
                            return;
                        }

                        _pauseEvent.WaitOne();
                        if (_isStopped) break;
                        ApplyPendingGraphics();

                        _ticks += _chrono.ElapsedTicks;

                        _chrono.Restart();

                        if (Device.WaitFifo())
                        {
                            Device.Statistics.RecordFifoStart();
                            Device.ProcessFrame();
                            Device.Statistics.RecordFifoEnd();
                        }

                        if (Device.PresentLoop(SwapBuffers))
                        {
                            if (!_ranFirstFrame)
                            {
                                _ranFirstFrame = true;
                                SendStatistics(false);
                             }

                        }

                        if (_ticks >= _ticksPerFrame)
                        {
                            SendStatistics();
                            _ticks = 0; // No catch-up bursts of UI callbacks after a pause or a slow frame.
                        }
                    }

                }
                catch (OperationCanceledException) when (_isStopped) { }
                finally
                {
                    try
                    {
                        if (Device.Gpu.Renderer is ThreadedRenderer threaded) threaded.FlushThreadedCommands();
                    }
                    finally { _gpuDoneEvent.Set(); }
                }
            });

            FinalizeWindowRenderer();
        }

        public bool QueueGraphicsSettings(LiveGraphicsSettings settings)
        {
            if (_isStopped || settings == null) return false;
            Interlocked.Exchange(ref _pendingGraphics, settings);
            return true;
        }

        private void ApplyPendingGraphics()
        {
            LiveGraphicsSettings settings = Interlocked.Exchange(ref _pendingGraphics, null);
            if (settings == null) return;
            GraphicsConfig.ResScale = settings.Scale;
            GraphicsConfig.MaxAnisotropy = settings.Anisotropy;
            GraphicsConfig.EnableAsyncShaderCompilation = settings.AsyncShaders;
            Device.Configuration.AspectRatio = settings.AspectRatio;
            _aspectRatio = settings.AspectRatio;
            Device.SetVolume(settings.Volume);
            // ThreadedWindow setters are no-ops in this fork. Queue one action on the
            // backend using the renderer's existing synchronous command mechanism.
            Device.Gpu.Renderer.BackgroundContextAction(() =>
            {
                Renderer.Window.SetAntiAliasing(settings.AntiAliasing);
                Renderer.Window.SetScalingFilter(settings.Filter);
                Renderer.Window.SetScalingFilterLevel(settings.FilterLevel);
                Renderer.Window.SetMetalFxUpscaleFactor(settings.MetalFxScale);
            });
        }

        public void Exit()
        {
            if (Interlocked.Exchange(ref _exitRequested, 1) != 0) return;
            _isStopped = true;
            _isActive = false;
            _gpuCancellationTokenSource.Cancel();
            _pauseEvent.Set();
            _inputUpdatedEvent.Set();
            // Signal only. Execute/ExecutionEntrypoint own all resource disposal after draining GPU work.
        }

        public static void ProcessMainThreadQueue()
        {
            while (_mainThreadActions.TryDequeue(out Action action))
            {
                action();
            }
        }

        public void MainLoop()
        {
            while (_isActive && !_isStopped)
            {
                UpdateFrame();

                ProcessMainThreadQueue();

                // Controller events still wake immediately. On A16, fallback polling at 250 Hz
                // leaves CPU time for emulation instead of scanning HID 1000 times/s.
                _inputUpdatedEvent.WaitOne(HostPerformance.IsA16 ? 4 : 1);
            }

            _exitEvent.Set();
        }

        private bool UpdateFrame()
        {
            if (!_isActive)
            {
                return true;
            }

            if (_isStopped)
            {
                return false;
            }

            NpadManager.Update();

            bool hasTouch;

            MouseDriver.SetClientSize(Width, Height);

            hasTouch = TouchScreenManager.UpdateMultiTouch(_inputManager.MouseDriver.GetGamepad("") as ITouchScreen, true);

            if (!hasTouch)
            {
                TouchScreenManager.Update(false);
            }

            Device.Hid.DebugPad.Update();

            MouseDriver.UpdatePosition();

            return true;
        }

        public void Execute()
        {
            _chrono.Restart();
            _isActive = true;

            InitializeWindow();

            _renderLoopThread = new Thread(Render)
            {
                Name = "GUI.RenderLoop",
            };
            _renderLoopThread.Start();

            MainLoop();

            _gpuDoneEvent.WaitOne();
            Exit();
        }

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            userText = null;

            if (OperatingSystem.IsIOS())
            {
                string result = null;
                using ManualResetEventSlim inputReceived = new(false);

                AlertHelper.ShowAlertWithTextInput(args.HeaderText, args.SubtitleText, args.GuideText, inputText =>
                {
                    result = inputText;
                    inputReceived.Set();
                });

                inputReceived.Wait();
                userText = result;
            }

            return true;
        }

        public void SendStatistics(bool before = true)
        {
            Span<byte> bytes = stackalloc byte[StatisticsPacket.Size];
            StatisticsPacket.Write(bytes,
                before ? Device.Statistics.GetGameFrameRate() : 0,
                before ? Device.Statistics.GetGameFrameTime() : 0,
                _ranFirstFrame,
                before ? Device.Statistics.GetFifoPercent() : 0,
                PresentationStatistics.Latest, TranscodeDiskCache.TotalHits,
                HostPerformance.ThermalState, HostPerformance.LowPowerMode);

            unsafe {
                fixed (byte* ptr = bytes)
                {
                    CallbackRegistry.Invoke("push_statistics", ptr, bytes.Length);
                }
            }
        }

        public bool DisplayMessageDialog(string title, string message)
        {
            if (OperatingSystem.IsIOS())
            {
                Console.WriteLine($"Alert: {title}, message: {message}");

                AlertHelper.ShowAlert(title, message, false);
            }

            return true;
        }
    
        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
        {
            string playerCount = args.PlayerCountMin == args.PlayerCountMax
                ? $"exactly {args.PlayerCountMin}"
                : $"{args.PlayerCountMin}-{args.PlayerCountMax}";

            string message = $"Application requests {playerCount} player(s) with:\n\n"
                           + $"TYPES: {args.SupportedStyles}\n\n"
                           + $"PLAYERS: {string.Join(", ", args.SupportedPlayers)}\n\n"
                           + (args.IsDocked ? "Docked mode set. Handheld is also invalid.\n\n" : "")
                           + "Please reconfigure Input now and then press OK.";

            return DisplayMessageDialog("Controller Applet", message);
        }

        public bool DisplayCabinetDialog(out string userText)
        {
            userText = null;

            if (OperatingSystem.IsIOS())
            {
                string result = null;
                using ManualResetEventSlim inputReceived = new(false);

                AlertHelper.ShowAlertWithTextInput("Amiibo", "Enter a name for the Amiibo", string.Empty, inputText =>
                {
                    result = inputText;
                    inputReceived.Set();
                });

                inputReceived.Wait();
                userText = result;
            }

            return true;
        }

        public void DisplayCabinetMessageDialog()
        {
            DisplayMessageDialog("Amiibo", "Please scan your Amiibo now.");
        }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText, (uint Module, uint Description)? errorCode = null)
        {
            string errorSuffix = errorCode.HasValue
                ? $"\n\nError Code: {errorCode.Value.Module:X4}-{errorCode.Value.Description:X4}"
                : string.Empty;

            Logger.Error?.Print(LogClass.Application, $"{title}: {message}{errorSuffix}");

            DisplayMessageDialog(title, message + errorSuffix);

            return true;
        }

        public IDynamicTextInputHandler CreateDynamicTextInputHandler()
        {
            return new NativeDynamicTextInputHandler();
        }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
        {
            device.Configuration.UserChannelPersistence.ExecuteProgram(kind, value);

            Exit();
        }

        public Ryujinx.HLE.HOS.Services.Account.Acc.UserProfile ShowPlayerSelectDialog()
        {
            return AccountSaveDataManager.GetLastUsedUser();
        }

        public void TakeScreenshot()
        {
            Logger.Warning?.Print(LogClass.Application, "TakeScreenshot is not supported in headless mode.");
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _isActive = false;
                _inputUpdatedEvent.Set();
                // DisposeGpu has already stopped the backend loop at this point.
                if (_renderLoopThread != null && _renderLoopThread != Thread.CurrentThread) _renderLoopThread.Join();
                NativeGamepadDriver.OnInputUpdated -= SignalInputUpdated;
                TouchScreenManager?.Dispose();
                NpadManager?.Dispose();
                _pauseEvent.Dispose();
                _inputUpdatedEvent.Dispose();
                _gpuDoneEvent.Dispose();
                _exitEvent.Dispose();
                _gpuCancellationTokenSource.Dispose();
            }
        }

        internal sealed class StatisticsRequest
        {
            public double FPS { get; set; } = 0d;
            public double FrameTime { get; set; } = 0d;
            public bool Started { get; set; } = false;
            public double FIFO { get; set; } = 0d;
        }
    }
}
