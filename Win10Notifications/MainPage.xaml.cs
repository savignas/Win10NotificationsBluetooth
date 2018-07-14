using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Background;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Win10Notifications.Models;
using Win32;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Win10Notifications
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage
    {
        private UserNotificationListener _listener;

        public string Error { get; set; }

        public ObservableCollection<UserNotification> Notifications { get; } = new ObservableCollection<UserNotification>();

        private Models.Radio _bluetooth;

        // The watcher trigger used to configure the background task registration 
        private readonly RfcommConnectionTrigger _trigger;

        // Define the raw bytes that are converted into SDP record
        private readonly byte[] _sdpRecordBlob = {
            0x35, 0x4a,  // DES len = 74 bytes

            // Vol 3 Part B 5.1.15 ServiceName
            // 34 bytes
            0x09, 0x01, 0x00, // UINT16 (0x09) value = 0x0100 [ServiceName]
            0x25, 0x1d,       // TextString (0x25) len = 29 bytes
                0x42, 0x6c, 0x75, 0x65, 0x74, 0x6f, 0x6f, 0x74, 0x68, 0x20,     // Bluetooth <sp>
                0x52, 0x66, 0x63, 0x6f, 0x6d, 0x6d, 0x20,                       // Rfcomm <sp>
                0x43, 0x68, 0x61, 0x74, 0x20,                                   // Chat <sp>
                0x53, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65,                       // Service <sp>
            // Vol 3 Part B 5.1.15 ServiceDescription
            // 40 bytes
            0x09, 0x01, 0x01, // UINT16 (0x09) value = 0x0101 [ServiceDescription]
            0x25, 0x23,       // TextString (0x25) = 33 bytes,
                0x42, 0x6c, 0x75, 0x65, 0x74, 0x6f, 0x6f, 0x74, 0x68, 0x20,     // Bluetooth <sp>
                0x52, 0x66, 0x63, 0x6f, 0x6d, 0x6d, 0x20,                       // Rfcomm <sp>
                0x43, 0x68, 0x61, 0x74, 0x20,                                   // Chat <sp>
                0x53, 0x65, 0x72, 0x76, 0x69, 0x63, 0x65, 0x20,                  // Service <sp>
                0x69, 0x6e, 0x20, 0x43, 0x23                                    // in C#
        };

        private readonly string _packageFamilyName = Package.Current.Id.FamilyName;

        private object _sendNotifications;

        private readonly ApplicationDataContainer _localSettings =
            ApplicationData.Current.LocalSettings;

        private readonly StorageFolder _localFolder =
            ApplicationData.Current.LocalFolder;

        private List<NotificationApp> _notificationApps = new List<NotificationApp>();
        private readonly List<IBackgroundTaskRegistration> _registrations = new List<IBackgroundTaskRegistration>();

        private StorageFile _notificationAppsFile;

        private byte[] _oldData;

        public MainPage()
        {
            InitializeComponent();
            InitializeRadios();
            InitializeNotificationListener();
            OpenNotificationAppsFile();

            _trigger = new RfcommConnectionTrigger();

            // Local service Id is the only mandatory field that should be used to filter a known service UUID.  
            if (_trigger.InboundConnection == null) return;
            _trigger.InboundConnection.LocalServiceId = RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid);

            // The SDP record is nice in order to populate optional name and description fields
            _trigger.InboundConnection.SdpRecord = _sdpRecordBlob.AsBuffer();

            InitializeSongTitleTimer();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (Window.Current.Content is Frame rootFrame && rootFrame.CanGoBack)
            {
                // Show UI in title bar if opted-in and in-app backstack is not empty.
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    AppViewBackButtonVisibility.Visible;
            }
            else
            {
                // Remove the UI from the title bar if in-app back stack is empty.
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    AppViewBackButtonVisibility.Collapsed;
            }
        }

        private void InitializeSongTitleTimer()
        {
            var songTitleTimer = new DispatcherTimer {Interval = TimeSpan.FromMilliseconds(500)};
            songTitleTimer.Tick += SongTitleTimer_Tick;
            songTitleTimer.Start();
        }

        private void SongTitleTimer_Tick(object sender, object e)
        {
            var winamp = Winamp.GetSongTitle();

            SongTitle.Text = winamp ?? "Winamp is not opened!";
        }

        private void ListViewNotifications_ItemClick(object sender, ItemClickEventArgs e)
        {
            var clickedNotif = (UserNotification)e.ClickedItem;

            RemoveNotification(clickedNotif.Id);
        }

        private async void ClearNotificationsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Clear all notifications. Use with caution.
                _listener.ClearNotifications();
            }
            catch (Exception ex)
            {
                Error = "Failed to clear all norifications! Error: " + ex;
            }

            await UpdateNotifications();
        }

        private async void OpenNotificationAppsFile()
        {
            _notificationAppsFile = await _localFolder.GetFileAsync("notificationApps");
            _oldData = await ReadNotificationApps();
        }

        private async void InitializeNotificationListener()
        {
            _sendNotifications = _localSettings.Values["sendNotifications"];
            if (_sendNotifications != null && !(bool) _sendNotifications) return;

            // Get the listener
            _listener = UserNotificationListener.Current;

            // And request access to the user's notifications (must be called from UI thread)
            var accessStatus = await _listener.RequestAccessAsync();

            switch (accessStatus)
            {
                // This means the user has granted access.
                case UserNotificationListenerAccessStatus.Allowed:

                    // Yay! Proceed as normal
                    _localSettings.Values["sendNotifications"] = true;
                    break;

                // This means the user has denied access.
                // Any further calls to RequestAccessAsync will instantly
                // return Denied. The user must go to the Windows settings
                // and manually allow access.
                case UserNotificationListenerAccessStatus.Denied:

                    _localSettings.Values["sendNotifications"] = false;
                    // Show UI explaining that listener features will not
                    // work until user allows access.
                    var dialog = new MessageDialog("You need to turn on access to notifications in privacy settings!\n" +
                                                   "Note: When changing settings, application could close. If it does, please open it again", "Error");
                    dialog.Commands.Add(new UICommand { Label = "Settings", Id = 0 });
                    dialog.Commands.Add(new UICommand { Label = "Close", Id = 1 });
                    dialog.CancelCommandIndex = 1;
                    dialog.DefaultCommandIndex = 1;
                    var command = await dialog.ShowAsync();
                    if ((int)command.Id == 0)
                    {
                        await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-notifications"));
                    }
                    break;

                // This means the user closed the prompt without
                // selecting either allow or deny. Further calls to
                // RequestAccessAsync will show the dialog again.
                case UserNotificationListenerAccessStatus.Unspecified:

                    // Show UI that allows the user to bring up the prompt again
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            // If background task isn't registered yet
            if (!BackgroundTaskRegistration.AllTasks.Any(i => i.Value.Name.Equals("UserNotificationChanged")))
            {
                // Specify the background task
                var builder = new BackgroundTaskBuilder()
                {
                    Name = "UserNotificationChanged"
                };

                // Set the trigger for Listener, listening to Toast Notifications
                builder.SetTrigger(new UserNotificationChangedTrigger(NotificationKinds.Toast));

                // Register the task
                builder.Register();
            }

            await UpdateNotifications();
        }

        public async Task UpdateNotifications()
        {
            try
            {
                // Get the toast notifications
                var notifsInPlatform = await _listener.GetNotificationsAsync(NotificationKinds.Toast);

                // Reverse their order since the platform returns them with oldest first, we want newest first
                notifsInPlatform = notifsInPlatform.Reverse().ToList();

                // First remove any notifications that no longer exist
                for (var i = 0; i < Notifications.Count; i++)
                {
                    var existingNotif = Notifications[i];

                    // If not in platform anymore, remove from our list
                    if (notifsInPlatform.Any(n => n.Id == existingNotif.Id)) continue;
                    Notifications.RemoveAt(i);
                    i--;
                }

                // Now our list only contains notifications that exist,
                // but it might be missing new notifications.

                for (var i = 0; i < notifsInPlatform.Count; i++)
                {
                    var platNotif = notifsInPlatform[i];

                    var indexOfExisting = FindIndexOfNotification(platNotif.Id);

                    // If we have an existing
                    if (indexOfExisting != -1)
                    {
                        // And if it's in the wrong position
                        if (i == indexOfExisting) continue;
                        // Move it to the right position
                        Notifications.Move(indexOfExisting, i);

                        // Otherwise, leave it in its place
                    }

                    // Otherwise, notification is new
                    else
                    {
                        // Insert at that position
                        Notifications.Insert(i, platNotif);

                        if (_localSettings.Values["newSettings"] != null && (bool)_localSettings.Values["newSettings"])
                        {
                            _oldData = await ReadNotificationApps();
                            _sendNotifications = _localSettings.Values["sendNotifications"];
                            _localSettings.Values["newSettings"] = false;
                        }

                        if (_notificationApps.All(x => x.Key != platNotif.AppInfo.AppUserModelId) &&
                            platNotif.AppInfo.PackageFamilyName != _packageFamilyName)
                        {
                            var notificationApp = new NotificationApp
                            {
                                Key = platNotif.AppInfo.AppUserModelId,
                                Name = platNotif.AppInfo.DisplayInfo.DisplayName,
                                Value = true
                            };
                            var stream = Stream.Null;
                            try
                            {
                                var icon = await platNotif.AppInfo.DisplayInfo.GetLogo(new Size(16, 16)).OpenReadAsync();
                                stream = icon.AsStreamForRead();
                            }
                            catch (Exception)
                            {
                                // ignored
                            }
                            var buffer = new byte[stream.Length];
                            await stream.ReadAsync(buffer, 0, buffer.Length);
                            notificationApp.IconData = buffer;
                            _notificationApps.Add(notificationApp);

                            var newData = notificationApp.Serialize();

                            var data = new byte[_oldData.Length + newData.Length];
                            Buffer.BlockCopy(_oldData, 0, data, 0, _oldData.Length);
                            Buffer.BlockCopy(newData, 0, data, _oldData.Length, newData.Length);

                            await FileIO.WriteBytesAsync(_notificationAppsFile, data);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Error = "Error updating notifications: " + ex;
            }

        }

        private async Task<byte[]> ReadNotificationApps()
        {
            try
            {
                var data = await FileIO.ReadBufferAsync(_notificationAppsFile);
                var notificationAppList = await NotificationApp.Deserialize(data.ToArray());
                _notificationApps = notificationAppList;
                return data.ToArray();
            }
            catch (Exception)
            {
                return new byte[]{};
            }
            
        }

        private int FindIndexOfNotification(uint notifId)
        {
            for (var i = 0; i < Notifications.Count; i++)
            {
                if (Notifications[i].Id == notifId)
                    return i;
            }

            return -1;
        }

        public async void RemoveNotification(uint notifId)
        {
            try
            {
                _listener.RemoveNotification(notifId);
            }

            catch (Exception ex)
            {
                ShowMessage(ex.ToString(), "Failed to dismiss notification");
            }

            await UpdateNotifications();
        }

        private static async void ShowMessage(string content, string title)
        {
            try
            {
                await new MessageDialog(content, title).ShowAsync();
            }

            catch
            {
                // ignored
            }
        }

        private async void InitializeRadios()
        {
            // An alternative to Radio.GetRadiosAsync is to use the Windows.Devices.Enumeration pattern,
            // passing Radio.GetDeviceSelector as the AQS string
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            try
            {
                var bluetoothRadio = radios.Single(x => x.Kind == RadioKind.Bluetooth);
                _bluetooth = new Models.Radio(bluetoothRadio, this);
                BluetoothSwitchList.Items?.Add(_bluetooth);
                if (!(bool)_localSettings.Values["BgServer"]) return;
                if (bluetoothRadio.State == RadioState.On)
                {
                    InitializeRfcommServerBg();
                }
                else
                {
                    // RequestAccessAsync must be called at least once from the UI thread
                    var accessLevel = await Windows.Devices.Radios.Radio.RequestAccessAsync();
                    switch (accessLevel)
                    {
                        case RadioAccessStatus.DeniedByUser:
                            var dialog = new MessageDialog("You need to turn on access to radios in privacy settings!", "Error");
                            await dialog.ShowAsync();
                            break;
                        case RadioAccessStatus.Unspecified:
                            break;
                        case RadioAccessStatus.Allowed:
                            var confimDialog = new MessageDialog("Can this app turn on your Bluetooth?", "Question");
                            confimDialog.Commands.Add(new UICommand { Label = "No", Id = 0 });
                            confimDialog.Commands.Add(new UICommand { Label = "Yes", Id = 1 });
                            confimDialog.DefaultCommandIndex = 1;
                            confimDialog.CancelCommandIndex = 0;
                            var result = await confimDialog.ShowAsync();
                            if ((int) result.Id == 1)
                            {
                                var enabled = await bluetoothRadio.SetStateAsync(RadioState.On);
                                if (enabled == RadioAccessStatus.Allowed)
                                {
                                    InitializeRfcommServerBg();
                                }
                            }
                            else
                            {
                                _localSettings.Values["BgServer"] = false;
                            }
                            break;
                        case RadioAccessStatus.DeniedBySystem:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
            catch (Exception)
            {
                NotifyUser("No Bluetooth detected!", NotifyType.ErrorMessage);
            }
        }

        /// <summary>
        /// Initializes the server using RfcommServiceProvider to advertise the Chat Service UUID and start listening
        /// for incoming connections.
        /// </summary>
        private async void InitializeRfcommServerBg()
        {
            _bluetooth.IsEnabled = false;

            var rfcommServerRegistration =
                RegisterBackgroundTask("Tasks.RfcommServerTask", "RfcommServerBackgroundTask", _trigger, null);

            _registrations.Add(rfcommServerRegistration);

            if (_sendNotifications != null && (bool) _sendNotifications)
            {
                _registrations.Add(RegisterBackgroundTask("Tasks.NotificationListenerTask", "UserNotificationChangedTriggerTask",
                    new UserNotificationChangedTrigger(NotificationKinds.Toast), null));
            } 

            _registrations.Add(RegisterBackgroundTask("Tasks.ToastNotificationHistoryChangedTriggerTask",
                "ToastNotificationHistoryChangedTriggerTask", new ToastNotificationHistoryChangedTrigger(), null));

            _registrations.Add(RegisterBackgroundTask("Tasks.ToastNotificationActionTriggerTask", "ToastNotificationsActionTriggerTask",
                new ToastNotificationActionTrigger(), null));

            AttachProgressAndCompletedHandlers(rfcommServerRegistration);

            // Applications registering for background trigger must request for permission.
            var backgroundAccessStatus = await BackgroundExecutionManager.RequestAccessAsync();

            // Even though the trigger is registered successfully, it might be blocked. Notify the user if that is the case.
            if (backgroundAccessStatus == BackgroundAccessStatus.AlwaysAllowed ||
                backgroundAccessStatus == BackgroundAccessStatus.AllowedSubjectToSystemPolicy)
            {
                _localSettings.Values["BgServer"] = true;
                NotifyUser("All background tasks registered", NotifyType.StatusMessage);
            }
            else
            {
                NotifyUser("Background tasks may be disabled for this app", NotifyType.ErrorMessage);
            }
        }

        public static BackgroundTaskRegistration RegisterBackgroundTask(string taskEntryPoint,
            string taskName,
            IBackgroundTrigger trigger,
            IBackgroundCondition condition)
        {
            foreach (var cur in BackgroundTaskRegistration.AllTasks)
            {
                if (cur.Value.Name == taskName)
                {
                    return (BackgroundTaskRegistration) cur.Value;
                }
            }

            var builder = new BackgroundTaskBuilder
            {
                Name = taskName,
                TaskEntryPoint = taskEntryPoint
            };

            builder.SetTrigger(trigger);

            if (condition != null)
            {
                builder.AddCondition(condition);
            }

            var task = builder.Register();

            return task;
        }

        /// <summary>
        /// Used to display messages to the user
        /// </summary>
        /// <param name="strMessage"></param>
        /// <param name="type"></param>
        public void NotifyUser(string strMessage, NotifyType type)
        {
            switch (type)
            {
                case NotifyType.StatusMessage:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Green);
                    break;
                case NotifyType.ErrorMessage:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Red);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
            StatusBlock.Text = strMessage;

            // Collapse the StatusBlock if it has no text to conserve real estate.
            StatusBorder.Visibility = StatusBlock.Text != string.Empty ? Visibility.Visible : Visibility.Collapsed;
        }

        public enum NotifyType
        {
            StatusMessage,
            ErrorMessage
        }

        private void DisconnectButtonBg_Click(object sender, RoutedEventArgs e)
        {
            _localSettings.Values["BgServer"] = false;
            _bluetooth.IsEnabled = true;
            ConversationListBox.Items?.Clear();

            for (var i = 0; i < _registrations.Count; i++)
            {
                if (_registrations[i] == null) continue;
                _registrations[i].Unregister(true);
                _registrations.RemoveAt(i);
                i--;
            }

            NotifyUser(
                _registrations.Count == 0
                    ? "All background tasks unregistered."
                    : "Not all background tasks unregistered.", NotifyType.StatusMessage);
        } 

        /// <summary>
        /// Called when background task defferal is completed.  This can happen for a number of reasons (both expected and unexpected).  
        /// IF this is expected, we'll notify the user.  If it's not, we'll show that this is an error.  Finally, clean up the connection by calling Disconnect().
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async void OnCompleted(BackgroundTaskRegistration sender, BackgroundTaskCompletedEventArgs args)
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("TaskCancelationReason"))
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    NotifyUser("Task cancelled unexpectedly - reason: " + settings.Values["TaskCancelationReason"], NotifyType.ErrorMessage);
                });
            }
            else
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    NotifyUser("Background task completed", NotifyType.StatusMessage);
                });
            }
            try
            {
                args.CheckResult();
            }
            catch (Exception ex)
            {
                NotifyUser(ex.Message, NotifyType.ErrorMessage);
            }
        }

        /// <summary>
        /// The background task updates the progress counter.  When that happens, this event handler gets invoked
        /// When the handler is invoked, we will display the value stored in local settings to the user.
        /// </summary>
        /// <param name="task"></param>
        /// <param name="args"></param>
        private async void OnProgress(IBackgroundTaskRegistration task, BackgroundTaskProgressEventArgs args)
        {
            if (!ApplicationData.Current.LocalSettings.Values.Keys.Contains("ReceivedMessage")) return;
            var backgroundMessage = (string)ApplicationData.Current.LocalSettings.Values["ReceivedMessage"];
            var remoteDeviceName = (string)ApplicationData.Current.LocalSettings.Values["RemoteDeviceName"];

            if (!backgroundMessage.Equals(""))
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    NotifyUser("Client Connected: " + remoteDeviceName, NotifyType.StatusMessage);
                    ConversationListBox.Items?.Add("Received: " + backgroundMessage);
                });
            }
        }

        private void AttachProgressAndCompletedHandlers(IBackgroundTaskRegistration task)
        {
            task.Progress += OnProgress;
            task.Completed += OnCompleted;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Settings));
        }

        private void NextTrackButton_OnClick(object sender, RoutedEventArgs e)
        {
            Winamp.NextTrack();
        }

        private void StopButton_OnClick(object sender, RoutedEventArgs e)
        {
            Winamp.Stop();
        }

        private void PauseButton_OnClick(object sender, RoutedEventArgs e)
        {
            Winamp.Pause();
        }

        private void PlayButton_OnClick(object sender, RoutedEventArgs e)
        {
            Winamp.Play();
        }

        private void PreviousTrackButton_OnClick(object sender, RoutedEventArgs e)
        {
            Winamp.PreviousTrack();
        }

        private void MuteButton_OnClick(object sender, RoutedEventArgs e)
        {
            Spotify.Mute();
        }
    }
}
