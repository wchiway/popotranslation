using System;
using System.Threading;
using Microsoft.UI.Xaml;

namespace popotranslation
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private const string InstanceMutexName = @"Local\PopoTranslation.SingleInstance";
        private const string ActivateEventName = @"Local\PopoTranslation.Activate";
        private Mutex? _instanceMutex;
        private EventWaitHandle? _activateEvent;
        private RegisteredWaitHandle? _activateWait;
        private MainWindow? _window;
        private bool _isPrimary;

        /// <summary>
        /// Initializes the singleton application object. This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
            _isPrimary = createdNew;
            if (!createdNew)
            {
                // A second process only wakes the existing instance and exits below.
                try
                {
                    using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                    activate.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // The owner is exiting; the operating system will release the mutex.
                }
                _instanceMutex.Dispose();
                _instanceMutex = null;
                return;
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                (_, _) =>
                {
                    var window = _window;
                    if (window is null) return;
                    window.DispatcherQueue.TryEnqueue(window.ShowSettings);
                },
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            if (!_isPrimary)
            {
                Exit();
                return;
            }

            _window = new MainWindow();
            _window.Activate();
        }
    }
}
