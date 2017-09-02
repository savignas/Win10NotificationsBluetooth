using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Background;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Microsoft.Toolkit.Uwp.Notifications;
using Win10Notifications.Models;

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
        private ObservableCollection<ToastNotification> AndroidNotifications { get; } = new ObservableCollection<ToastNotification>();

        private Models.Radio _bluetooth;

        private StreamSocket _socket;
        private DataWriter _writer;
        private RfcommServiceProvider _rfcommProvider;
        private StreamSocketListener _socketListener;

        // The background task registration for the background advertisement watcher 
        private IBackgroundTaskRegistration _taskRegistration;
        private IBackgroundTaskRegistration _notificationListenerTaskRegistration;
        private IBackgroundTaskRegistration _historyTaskRegistration;
        // The watcher trigger used to configure the background task registration 
        private readonly RfcommConnectionTrigger _trigger;
        // A name is given to the task in order for it to be identifiable across context. 
        private const string TaskName = "Rfcomm_BackgroundTask";

        private const string NotificationListenerTaskName = "UserNotificationChanged";

        private const string HistoryTaskName = "ToastNotificationHistoryChangedTriggerTask";

        // Entry point for the background task. 
        private const string TaskEntryPoint = "Tasks.RfcommServerTask";

        private const string NotificationListenerTaskEntryPoint = "Tasks.NotificationListenerTask";

        private const string HistoryTaskEntryPoint = "Tasks.ToastNotificationHistoryChangedTriggerTask";

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

        private StorageFile _notificationAppsFile;

        private byte[] _oldData;

        public MainPage()
        {
            InitializeComponent();
            InitializeRadios();
            InitializeNotificationListener();
            InitializeHistoryChangedTask();
            OpenNotificationAppsFile();

            _trigger = new RfcommConnectionTrigger();

            // Local service Id is the only mandatory field that should be used to filter a known service UUID.  
            if (_trigger.InboundConnection == null) return;
            _trigger.InboundConnection.LocalServiceId = RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid);

            // The SDP record is nice in order to populate optional name and description fields
            _trigger.InboundConnection.SdpRecord = _sdpRecordBlob.AsBuffer();
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

        private void ListViewNotifications_ItemClick(object sender, ItemClickEventArgs e)
        {
            var clickedNotif = (UserNotification)e.ClickedItem;

            RemoveNotification(clickedNotif.Id);
        }

        private void ListenButton_Click(object sender, RoutedEventArgs e)
        {
            InitializeRfcommServer();
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

        private static void InitializeHistoryChangedTask()
        {
            // If background task isn't registered yet
            if (!BackgroundTaskRegistration.AllTasks.Any(i => i.Value.Name.Equals("HistoryChanged")))
            {
                // Specify the background task
                var builder = new BackgroundTaskBuilder()
                {
                    Name = "HistoryChanged"
                };

                // Set the trigger for Listener, listening to Toast Notifications
                builder.SetTrigger(new ToastNotificationHistoryChangedTrigger());

                // Register the task
                builder.Register();
            }
        }

        private async void OpenNotificationAppsFile()
        {
            _notificationAppsFile = await _localFolder.GetFileAsync("notificationApps");
            _oldData = await ReadNotificationApps();
        }

        private async void InitializeNotificationListener()
        {
            _sendNotifications = _localSettings.Values["sendNotifications"];
            if (_sendNotifications == null || !(bool) _localSettings.Values["sendNotifications"]) return;

            // Get the listener
            _listener = UserNotificationListener.Current;

            // And request access to the user's notifications (must be called from UI thread)
            var accessStatus = await _listener.RequestAccessAsync();

            switch (accessStatus)
            {
                // This means the user has granted access.
                case UserNotificationListenerAccessStatus.Allowed:

                    // Yay! Proceed as normal
                    break;

                // This means the user has denied access.
                // Any further calls to RequestAccessAsync will instantly
                // return Denied. The user must go to the Windows settings
                // and manually allow access.
                case UserNotificationListenerAccessStatus.Denied:

                    _localSettings.Values["sendNotifications"] = false;
                    // Show UI explaining that listener features will not
                    // work until user allows access.
                    var dialog = new MessageDialog("You need to turn on access to notifications in privacy settings!", "Error");
                    dialog.Commands.Add(new UICommand { Label = "Close", Id = 0 });
                    dialog.CancelCommandIndex = 0;
                    await dialog.ShowAsync();
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
            if (!BackgroundTaskRegistration.AllTasks.Any(i => i.Value.Name.Equals("UserNotificationChanged2")))
            {
                // Specify the background task
                var builder = new BackgroundTaskBuilder()
                {
                    Name = "UserNotificationChanged2"
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

                    if (existingNotif.AppInfo.PackageFamilyName == _packageFamilyName || _sendNotifications == null ||
                        !(bool) _sendNotifications) continue;
                    if (DisconnectButton.IsEnabled)
                    {
                        SendMessage("0", existingNotif);
                    }
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

                        if (platNotif.AppInfo.PackageFamilyName == _packageFamilyName ||
                            _sendNotifications == null || !(bool) _sendNotifications) continue;
                        if (DisconnectButton.IsEnabled)
                        {
                            SendMessage("2", platNotif);
                        }

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
                            System.Buffer.BlockCopy(_oldData, 0, data, 0, _oldData.Length);
                            System.Buffer.BlockCopy(newData, 0, data, _oldData.Length, newData.Length);

                            await FileIO.WriteBytesAsync(_notificationAppsFile, data);
                        }

                        if (platNotif.AppInfo.PackageFamilyName == _packageFamilyName || _sendNotifications == null ||
                            !(bool) _sendNotifications ||
                            !_notificationApps.Any(x => x.Key == platNotif.AppInfo.AppUserModelId && x.Value)) continue;
                        if (DisconnectButton.IsEnabled)
                        {
                            SendMessage("1", platNotif);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Error = "Error updating notifications: " + ex;
            }

        }

        public void DismissAndroidNotification()
        {
            var androidNotificationHistory = ToastNotificationManager.History.GetHistory();
            for (var i = 0; i < AndroidNotifications.Count; i++)
            {
                var existingAndroidNotif = AndroidNotifications[i];

                if (androidNotificationHistory.Any(n => n.Tag == existingAndroidNotif.Tag)) continue;
                AndroidNotifications.RemoveAt(i);
                i--;

                if (DisconnectButton.IsEnabled)
                {
                    SendMessage("0;" + existingAndroidNotif.Tag);
                }
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

        private void SendNotificationsOnConnect()
        {
            foreach (var notification in Notifications)
            {
                SendMessage("1", notification);
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

        public void ShowNotification(string title, string content, string key)
        {
            var visual = new ToastVisual
            {
                BindingGeneric = new ToastBindingGeneric
                {
                    Children =
                    {
                        new AdaptiveText
                        {
                            Text = title
                        },
                        new AdaptiveText
                        {
                            Text = content
                        }
                    }
                }
            };
            var toastContent = new ToastContent
            {
                Visual = visual
            };

            var toast = new ToastNotification(toastContent.GetXml()) {Tag = key};
            ToastNotificationManager.CreateToastNotifier().Show(toast);
            AndroidNotifications.Add(toast);
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
                    ListenButton.IsEnabled = false;
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
        private async void InitializeRfcommServer()
        {
            ListenButton.IsEnabled = false;
            DisconnectButton.IsEnabled = true;
            _bluetooth.IsEnabled = false;

            try
            {
                _rfcommProvider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid));
            }
            // Catch exception HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE).
            catch (Exception ex) when ((uint)ex.HResult == 0x800710DF)
            {
                // The Bluetooth radio may be off.
                NotifyUser("Make sure your Bluetooth Radio is on: " + ex.Message, NotifyType.ErrorMessage);
                ListenButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                _bluetooth.IsEnabled = true;
                return;
            }


            // Create a listener for this service and start listening
            _socketListener = new StreamSocketListener();
            _socketListener.ConnectionReceived += OnConnectionReceived;
            _rfcommProvider.ServiceId.AsString();

            await _socketListener.BindServiceNameAsync(_rfcommProvider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

            // Set the SDP attributes and start Bluetooth advertising
            InitializeServiceSdpAttributes(_rfcommProvider);

            try
            {
                _rfcommProvider.StartAdvertising(_socketListener, true);
            }
            catch (Exception e)
            {
                // If you aren't able to get a reference to an RfcommServiceProvider, tell the user why.  Usually throws an exception if user changed their privacy settings to prevent Sync w/ Devices.  
                NotifyUser(e.Message, NotifyType.ErrorMessage);
                ListenButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                _bluetooth.IsEnabled = true;
                return;
            }

            NotifyUser("Listening for incoming connections", NotifyType.StatusMessage);

        }

        /// <summary>
        /// Initializes the server using RfcommServiceProvider to advertise the Chat Service UUID and start listening
        /// for incoming connections.
        /// </summary>
        private async void InitializeRfcommServerBg()
        {
            _bluetooth.IsEnabled = false;

            // Registering a background trigger if it is not already registered. Rfcomm Chat Service will now be advertised in the SDP record
            // First get the existing tasks to see if we already registered for it

            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                if (task.Value.Name != TaskName) continue;
                _taskRegistration = task.Value;
                break;
            }

            if (_taskRegistration != null)
            {
                NotifyUser("Background watcher already registered.", NotifyType.StatusMessage);
            }
            // Applications registering for background trigger must request for permission.
            var backgroundAccessStatus = await BackgroundExecutionManager.RequestAccessAsync();

            var builder = new BackgroundTaskBuilder { TaskEntryPoint = TaskEntryPoint };
            builder.SetTrigger(_trigger);
            builder.Name = TaskName;

            try
            {
                _taskRegistration = builder.Register();
                AttachProgressAndCompletedHandlers(_taskRegistration);

                // Even though the trigger is registered successfully, it might be blocked. Notify the user if that is the case.
                if (backgroundAccessStatus == BackgroundAccessStatus.AlwaysAllowed ||
                    backgroundAccessStatus == BackgroundAccessStatus.AllowedSubjectToSystemPolicy)
                {
                    NotifyUser("Background watcher registered.", NotifyType.StatusMessage);
                    // Registering a background trigger if it is not already registered. Rfcomm Chat Service will now be advertised in the SDP record
                    // First get the existing tasks to see if we already registered for it

                    _localSettings.Values["BgServer"] = true;
                }
                else
                {
                    NotifyUser("Background tasks may be disabled for this app", NotifyType.ErrorMessage);
                }
            }
            catch (Exception)
            {
                NotifyUser("Background task not registered",
                    NotifyType.ErrorMessage);
            }

            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                if (task.Value.Name != HistoryTaskName) continue;
                _historyTaskRegistration = task.Value;
                break;
            }

            if (_historyTaskRegistration != null)
            {
                NotifyUser("History background watcher already registered.", NotifyType.StatusMessage);
            }

            builder = new BackgroundTaskBuilder { TaskEntryPoint = HistoryTaskEntryPoint };
            builder.SetTrigger(new ToastNotificationHistoryChangedTrigger());
            builder.Name = TaskName;

            try
            {
                _historyTaskRegistration = builder.Register();

                // Even though the trigger is registered successfully, it might be blocked. Notify the user if that is the case.
                if (backgroundAccessStatus == BackgroundAccessStatus.AlwaysAllowed ||
                    backgroundAccessStatus == BackgroundAccessStatus.AllowedSubjectToSystemPolicy)
                {
                    NotifyUser("History background watcher registered.", NotifyType.StatusMessage);
                    // Registering a background trigger if it is not already registered. Rfcomm Chat Service will now be advertised in the SDP record
                    // First get the existing tasks to see if we already registered for it
                }
                else
                {
                    NotifyUser("Background tasks may be disabled for this app", NotifyType.ErrorMessage);
                }
            }
            catch (Exception)
            {
                NotifyUser("History background task not registered",
                    NotifyType.ErrorMessage);
            }

            if (_sendNotifications == null || !(bool) _sendNotifications) return;
            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                if (task.Value.Name != NotificationListenerTaskName) continue;
                _notificationListenerTaskRegistration = task.Value;
                break;
            }

            if (_notificationListenerTaskRegistration != null)
            {
                NotifyUser("Notification listener already registered.", NotifyType.StatusMessage);
            }
            else
            {
                builder = new BackgroundTaskBuilder {TaskEntryPoint = NotificationListenerTaskEntryPoint};
                builder.SetTrigger(new UserNotificationChangedTrigger(NotificationKinds.Toast));
                builder.Name = NotificationListenerTaskName;

                try
                {
                    _notificationListenerTaskRegistration = builder.Register();
                    _notificationListenerTaskRegistration.Completed += OnCompletedNotificationListener;

                    // Even though the trigger is registered successfully, it might be blocked. Notify the user if that is the case.
                    if (backgroundAccessStatus == BackgroundAccessStatus.AlwaysAllowed || backgroundAccessStatus ==
                        BackgroundAccessStatus.AllowedSubjectToSystemPolicy)
                    {
                        NotifyUser("Notification listener registered.", NotifyType.StatusMessage);
                    }
                    else
                    {
                        NotifyUser("Background tasks may be disabled for this app", NotifyType.ErrorMessage);
                    }
                }
                catch (Exception)
                {
                    NotifyUser("Notification listener not registered",
                        NotifyType.ErrorMessage);
                }
            }
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
            }
            StatusBlock.Text = strMessage;

            // Collapse the StatusBlock if it has no text to conserve real estate.
            StatusBorder.Visibility = StatusBlock.Text != string.Empty ? Visibility.Visible : Visibility.Collapsed;
            //StatusBorder.Visibility = StatusBlock.Text != string.Empty ? Visibility.Visible : Visibility.Collapsed;
        }

        public enum NotifyType
        {
            StatusMessage,
            ErrorMessage
        };

        /// <summary>
        /// Creates the SDP record that will be revealed to the Client device when pairing occurs.  
        /// </summary>
        /// <param name="rfcommProvider">The RfcommServiceProvider that is being used to initialize the server</param>
        private static void InitializeServiceSdpAttributes(RfcommServiceProvider rfcommProvider)
        {
            var sdpWriter = new DataWriter();

            // Write the Service Name Attribute.
            sdpWriter.WriteByte(Constants.SdpServiceNameAttributeType);

            // The length of the UTF-8 encoded Service Name SDP Attribute.
            sdpWriter.WriteByte((byte)Constants.SdpServiceName.Length);

            // The UTF-8 encoded Service Name value.
            sdpWriter.UnicodeEncoding = UnicodeEncoding.Utf8;
            sdpWriter.WriteString(Constants.SdpServiceName);

            // Set the SDP Attribute on the RFCOMM Service Provider.
            rfcommProvider.SdpRawAttributes.Add(Constants.SdpServiceNameAttributeId, sdpWriter.DetachBuffer());
        }

        private static string GetNotificationMessage(string type, UserNotification notification)
        {
            // Get the toast binding, if present
            var toastBinding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var id = notification.Id;
            string notifMessage;

            if (type == "1")
            {
                var appName = "";
                var titleText = "No title";
                var bodyText = "";

                if (toastBinding != null)
                {
                    try
                    {
                        appName = notification.AppInfo.DisplayInfo.DisplayName;
                    }
                    catch (Exception)
                    {
                        // ignored
                    }

                    // And then get the text elements from the toast binding
                    var textElements = toastBinding.GetTextElements();

                    // Treat the first text element as the title text
                    titleText = textElements.FirstOrDefault()?.Text;

                    // We'll treat all subsequent text elements as body text,
                    // joining them together via newlines.
                    bodyText = string.Join("\n", textElements.Skip(1).Select(t => t.Text));
                }

                notifMessage = type + ";" + id + ";" + appName + ";" + titleText + ";" + bodyText;
            }
            else
            {
                notifMessage = type + ";" + id;
            }

            return notifMessage;
        }

        private async void SendMessage(string type, UserNotification notification)
        {
            var notifMessage = GetNotificationMessage(type, notification);

            // There's no need to send a zero length message
            if (notifMessage.Length == 0) return;
            // Make sure that the connection is still up and there is a message to send
            if (_socket != null)
            {
                _writer.WriteString(notifMessage);

                await _writer.StoreAsync();
            }
            else
            {
                NotifyUser("No clients connected", NotifyType.StatusMessage);
            }
        }

        private async void SendMessage(string notifMessage)
        {
            // There's no need to send a zero length message
            if (notifMessage.Length != 0)
            {
                // Make sure that the connection is still up and there is a message to send
                if (_socket != null)
                {
                    _writer.WriteString(notifMessage);

                    ConversationListBox.Items?.Add("Sent: " + notifMessage);

                    await _writer.StoreAsync();
                }
                else
                {
                    NotifyUser("No clients connected, please wait for a client to connect before attempting to send a notification", NotifyType.StatusMessage);
                }
            }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            DisconnectClick();
            NotifyUser("Disconnected.", NotifyType.StatusMessage);
        }

        private void DisconnectButtonBg_Click(object sender, RoutedEventArgs e)
        {
            _localSettings.Values["BgServer"] = false;
            DisconnectBgClick();
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

        private async void OnCompletedNotificationListener(BackgroundTaskRegistration sender, BackgroundTaskCompletedEventArgs args)
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.ContainsKey("NotificationListenerCancelationReason"))
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    NotifyUser("Notification listener cancelled unexpectedly - reason: " + settings.Values["NotificationListenerCancelationReason"], NotifyType.ErrorMessage);
                });
            }
            else
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    NotifyUser("Notification listener completed", NotifyType.StatusMessage);
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

        private async void DisconnectClick()
        {
            if (_rfcommProvider != null)
            {
                _rfcommProvider.StopAdvertising();
                _rfcommProvider = null;
            }

            if (_socketListener != null)
            {
                _socketListener.Dispose();
                _socketListener = null;
            }

            if (_writer != null)
            {
                _writer.DetachStream();
                _writer = null;
            }

            if (_socket != null)
            {
                _socket.Dispose();
                _socket = null;
            }
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ListenButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                _bluetooth.IsEnabled = true;
                ConversationListBox.Items?.Clear();
            });
        }

        private async void Disconnect()
        {
            if (_rfcommProvider != null)
            {
                _rfcommProvider.StopAdvertising();
                _rfcommProvider = null;
            }

            if (_socketListener != null)
            {
                _socketListener.Dispose();
                _socketListener = null;
            }

            if (_writer != null)
            {
                _writer.DetachStream();
                _writer = null;
            }

            if (_socket != null)
            {
                _socket.Dispose();
                _socket = null;
            }
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ConversationListBox.Items?.Clear();
                InitializeRfcommServer();
            });
        }

        private async void DisconnectBgClick()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ListenButton.IsEnabled = true;
                _bluetooth.IsEnabled = true;
                ConversationListBox.Items?.Clear();

                // Unregistering the background task will remove the Rfcomm Chat Service from the SDP record and stop listening for incoming connections
                // First get the existing tasks to see if we already registered for it
                if (_taskRegistration != null)
                {
                    _taskRegistration.Unregister(true);
                    _taskRegistration = null;
                    NotifyUser("Background watcher unregistered.", NotifyType.StatusMessage);
                }
                else
                {
                    // At this point we assume we haven't found any existing tasks matching the one we want to unregister
                    NotifyUser("No registered background watcher found.", NotifyType.StatusMessage);
                }
                // Unregistering the background task will remove the Rfcomm Chat Service from the SDP record and stop listening for incoming connections
                // First get the existing tasks to see if we already registered for it
                if (_historyTaskRegistration != null)
                {
                    _historyTaskRegistration.Unregister(true);
                    _historyTaskRegistration = null;
                    NotifyUser("History background watcher unregistered.", NotifyType.StatusMessage);
                }
                else
                {
                    // At this point we assume we haven't found any existing tasks matching the one we want to unregister
                    NotifyUser("No registered background watcher found.", NotifyType.StatusMessage);
                }
                // Unregistering the background task will remove the Rfcomm Chat Service from the SDP record and stop listening for incoming connections
                // First get the existing tasks to see if we already registered for it
                if (_notificationListenerTaskRegistration != null)
                {
                    _notificationListenerTaskRegistration.Unregister(true);
                    _notificationListenerTaskRegistration = null;
                    NotifyUser("Notification listener unregistered.", NotifyType.StatusMessage);
                }
                else
                {
                    // At this point we assume we haven't found any existing tasks matching the one we want to unregister
                    NotifyUser("No registered notification listener found.", NotifyType.StatusMessage);
                }
            });
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

        /// <summary>
        /// Invoked when the socket listener accepts an incoming Bluetooth connection.
        /// </summary>
        /// <param name="sender">The socket listener that accepted the connection.</param>
        /// <param name="args">The connection accept parameters, which contain the connected socket.</param>
        private async void OnConnectionReceived(
            StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            // Don't need the listener anymore
            _socketListener.Dispose();
            _socketListener = null;

            try
            {
                _socket = args.Socket;
            }
            catch (Exception e)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    NotifyUser(e.Message, NotifyType.ErrorMessage);
                });
                Disconnect();
                return;
            }

            // Note - this is the supported way to get a Bluetooth device from a given socket
            var remoteDevice = await BluetoothDevice.FromHostNameAsync(_socket.Information.RemoteHostName);

            _writer = new DataWriter(_socket.OutputStream);
            var reader = new DataReader(_socket.InputStream);
            var remoteDisconnection = false;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                NotifyUser("Connected to Client: " + remoteDevice.Name, NotifyType.StatusMessage);
                DisconnectButton.Content = "Disconnect from " + remoteDevice.Name;
                if (_sendNotifications != null && (bool) _sendNotifications)
                {
                    SendNotificationsOnConnect();
                }
            });

            // Infinite read buffer loop
            while (true)
            {
                try
                {
                    // Based on the protocol we've defined, the first uint is the size of the message
                    var readLength = await reader.LoadAsync(sizeof(byte));

                    // Check if the size of the data is expected (otherwise the remote has already terminated the connection)
                    if (readLength < sizeof(byte))
                    {
                        remoteDisconnection = true;
                        break;
                    }
                    var currentLength = (uint) reader.ReadByte();

                    // Load the rest of the message since you already know the length of the data expected.  
                    readLength = await reader.LoadAsync(currentLength);

                    // Check if the size of the data is expected (otherwise the remote has already terminated the connection)
                    if (readLength < currentLength)
                    {
                        remoteDisconnection = true;
                        break;
                    }
                    var message = reader.ReadString(currentLength);

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        ConversationListBox.Items?.Add("Received: " + message);
                        var messageParts = message.Split(';');

                        if (messageParts[0] == "0")
                            try
                            {
                                RemoveNotification(uint.Parse(messageParts[1]));
                            }
                            catch (Exception)
                            {
                                ToastNotificationManager.History.Remove(messageParts[1]);
                            }
                        else if (messageParts[0] == "1")
                        {
                            ShowNotification(messageParts[2], messageParts[3], messageParts[1]);
                        }
                    });
                }
                // Catch exception HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED).
                catch (Exception ex) when ((uint)ex.HResult == 0x800703E3)
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        NotifyUser("Client (" + remoteDevice.Name + ") Disconnected Successfully", NotifyType.StatusMessage);
                        DisconnectButton.Content = "Disconnect Foreground Server";
                    });
                    break;
                }
            }

            reader.DetachStream();
            if (!remoteDisconnection) return;
            Disconnect();
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                NotifyUser("Client (" + remoteDevice.Name + ") disconnected", NotifyType.StatusMessage);
                DisconnectButton.Content = "Disconnect Foreground Server";
            });
        }

        private void Toggle_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var bluetoothSwitch = (ToggleSwitch) sender;
            ListenButton.IsEnabled = bluetoothSwitch.IsEnabled && _bluetooth.IsRadioOn;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Settings));
        }
    }
}
