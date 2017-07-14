using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Store;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Radios;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Win10Notifications
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private UserNotificationListener _listener;

        public string Error { get; set; }

        public ObservableCollection<UserNotification> Notifications { get; } = new ObservableCollection<UserNotification>();

        private RadioModel _bluetooth;

        private StreamSocket _socket;
        private DataWriter _writer;
        private RfcommServiceProvider _rfcommProvider;
        private StreamSocketListener _socketListener;

        // The background task registration for the background advertisement watcher 
        private IBackgroundTaskRegistration taskRegistration;
        // The watcher trigger used to configure the background task registration 
        private RfcommConnectionTrigger trigger;
        // A name is given to the task in order for it to be identifiable across context. 
        private string taskName = "Rfcomm_BackgroundTask";
        // Entry point for the background task. 
        private string taskEntryPoint = "Tasks.RfcommServerTask";

        // Define the raw bytes that are converted into SDP record
        private byte[] sdpRecordBlob = new byte[]
        {
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

        private StorageFile file;

        public MainPage()
        {
            this.InitializeComponent();
            this.InitializeNotificationListener();
            this.InitializeRadios();

            trigger = new RfcommConnectionTrigger();

            // Local service Id is the only mandatory field that should be used to filter a known service UUID.  
            trigger.InboundConnection.LocalServiceId = RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid);

            // The SDP record is nice in order to populate optional name and description fields
            trigger.InboundConnection.SdpRecord = sdpRecordBlob.AsBuffer();
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

        private void ListenButtonBg_Click(object sender, RoutedEventArgs e)
        {
            InitializeRfcommServerBg();
        }

        private void ClearNotificationsButton_Click(object sender, RoutedEventArgs e)
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

            UpdateNotifications();
        }

        private async void InitializeNotificationListener()
        {
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

                    // Show UI explaining that listener features will not
                    // work until user allows access.
                    var dialog = new MessageDialog("You need to turn on access to notifications in privacy settings!", "Error");
                    dialog.Commands.Add(new UICommand { Label = "Close", Id = 0 });
                    var res = await dialog.ShowAsync();
                    if ((int)res.Id == 0)
                    {
                        Application.Current.Exit();
                    }
                    break;

                // This means the user closed the prompt without
                // selecting either allow or deny. Further calls to
                // RequestAccessAsync will show the dialog again.
                case UserNotificationListenerAccessStatus.Unspecified:

                    // Show UI that allows the user to bring up the prompt again
                    break;
            }

            var backgroundStatus = await BackgroundExecutionManager.RequestAccessAsync();

            switch (backgroundStatus)
            {
                case BackgroundAccessStatus.DeniedByUser:
                    var dialog = new MessageDialog("You need to turn on access to background apps in privacy settings!", "Error");
                    await dialog.ShowAsync();
                    break;
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

            UpdateNotifications();
        }

        public async void UpdateNotifications()
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
                    if (!notifsInPlatform.Any(n => n.Id == existingNotif.Id))
                    {
                        Notifications.RemoveAt(i);
                        i--;

                        if (DisconnectButton.IsEnabled)
                        {
                            SendMessage("0", existingNotif);
                        }
                        else if (DisconnectButtonBg.IsEnabled)
                        {
                            SendMessageBg("0", existingNotif);
                        }
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
                        if (i != indexOfExisting)
                        {
                            // Move it to the right position
                            Notifications.Move(indexOfExisting, i);

                            if (DisconnectButton.IsEnabled)
                            {
                                SendMessage("2", platNotif);
                            }
                            else if (DisconnectButtonBg.IsEnabled)
                            {
                                SendMessageBg("2", platNotif);
                            }
                        }

                        // Otherwise, leave it in its place
                    }

                    // Otherwise, notification is new
                    else
                    {
                        // Insert at that position
                        Notifications.Insert(i, platNotif);

                        if (DisconnectButton.IsEnabled)
                        {
                            SendMessage("1", platNotif);
                        }
                        else if (DisconnectButtonBg.IsEnabled)
                        {
                            SendMessageBg("1", platNotif);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Error = "Error updating notifications: " + ex;
            }

        }

        private async void CreateFile()
        {
            var folder = ApplicationData.Current.LocalFolder;
            file = await folder.CreateFileAsync("win10notifications.txt",
                CreationCollisionOption.ReplaceExisting);
        }

        private async void WriteFile(string text)
        {
            if (file != null)
                await FileIO.AppendTextAsync(file, text + '\n');
        }

        private void SendNotificationsOnConnect()
        {
            foreach (var notification in Notifications)
            {
                SendMessage("1", notification);
            }
        }

        private void SendNotificationsOnConnectBg()
        {
            foreach (var notification in Notifications)
            {
                SendMessageBg("1", notification);
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

        public void RemoveNotification(uint notifId)
        {
            try
            {
                _listener.RemoveNotification(notifId);
            }

            catch (Exception ex)
            {
                ShowMessage(ex.ToString(), "Failed to dismiss notification");
            }

            UpdateNotifications();
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
            // RequestAccessAsync must be called at least once from the UI thread
            var accessLevel = await Radio.RequestAccessAsync();
            switch (accessLevel)
            {
                case RadioAccessStatus.DeniedByUser:
                    var dialog = new MessageDialog("You need to turn on access to radios in privacy settings!", "Error");
                    await dialog.ShowAsync();
                    break;
            }

            // An alternative to Radio.GetRadiosAsync is to use the Windows.Devices.Enumeration pattern,
            // passing Radio.GetDeviceSelector as the AQS string
            var radios = await Radio.GetRadiosAsync();
            foreach (var radio in radios)
            {
                if (radio.Name == "Bluetooth")
                {
                    _bluetooth = new RadioModel(radio, this);
                    BluetoothSwitchList.Items.Add(_bluetooth);
                    if (radio.State == RadioState.Disabled || radio.State == RadioState.Off)
                    {
                        ListenButton.IsEnabled = false;
                        ListenButtonBg.IsEnabled = false;
                    }
                }
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
            ListenButtonBg.IsEnabled = false;
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
                ListenButtonBg.IsEnabled = true;
                _bluetooth.IsEnabled = true;
                return;
            }


            // Create a listener for this service and start listening
            _socketListener = new StreamSocketListener();
            _socketListener.ConnectionReceived += OnConnectionReceived;
            var rfcomm = _rfcommProvider.ServiceId.AsString();

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
                ListenButtonBg.IsEnabled = true;
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
            ListenButtonBg.IsEnabled = false;
            DisconnectButtonBg.IsEnabled = true;
            ListenButton.IsEnabled = false;
            _bluetooth.IsEnabled = false;

            // Registering a background trigger if it is not already registered. Rfcomm Chat Service will now be advertised in the SDP record
            // First get the existing tasks to see if we already registered for it

            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                if (task.Value.Name == taskName)
                {
                    taskRegistration = task.Value;
                    break;
                }
            }

            if (taskRegistration != null)
            {
                NotifyUser("Background watcher already registered.", NotifyType.StatusMessage);
                return;
            }
            else
            {
                // Applications registering for background trigger must request for permission.
                BackgroundAccessStatus backgroundAccessStatus = await BackgroundExecutionManager.RequestAccessAsync();

                var builder = new BackgroundTaskBuilder();
                builder.TaskEntryPoint = taskEntryPoint;
                builder.SetTrigger(trigger);
                builder.Name = taskName;

                try
                {
                    taskRegistration = builder.Register();
                    AttachProgressAndCompletedHandlers(taskRegistration);

                    // Even though the trigger is registered successfully, it might be blocked. Notify the user if that is the case.
                    if ((backgroundAccessStatus == BackgroundAccessStatus.AlwaysAllowed) || (backgroundAccessStatus == BackgroundAccessStatus.AllowedSubjectToSystemPolicy))
                    {
                        NotifyUser("Background watcher registered.", NotifyType.StatusMessage);
                    }
                    else
                    {
                        NotifyUser("Background tasks may be disabled for this app", NotifyType.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    NotifyUser("Background task not registered",
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
            StatusBorder.Visibility = (StatusBlock.Text != String.Empty) ? Visibility.Visible : Visibility.Collapsed;
            if (StatusBlock.Text != String.Empty)
            {
                StatusBorder.Visibility = Visibility.Visible;
                StatusPanel.Visibility = Visibility.Visible;
            }
            else
            {
                StatusBorder.Visibility = Visibility.Collapsed;
                StatusPanel.Visibility = Visibility.Collapsed;
            }
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

        private async void SendMessage(string type, UserNotification notification)
        {
            // Get the toast binding, if present
            var toastBinding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var id = notification.Id;
            var notifMessage = "";

            if (type == "1")
            {
                var titleText = "No title";
                var bodyText = "";

                if (toastBinding != null)
                {
                    // And then get the text elements from the toast binding
                    var textElements = toastBinding.GetTextElements();

                    // Treat the first text element as the title text
                    titleText = textElements.FirstOrDefault()?.Text;

                    // We'll treat all subsequent text elements as body text,
                    // joining them together via newlines.
                    bodyText = string.Join("\n", textElements.Skip(1).Select(t => t.Text));
                }

                notifMessage = type + ";" + id + ";" + titleText + ";" + bodyText;
            }
            else
            {
                notifMessage = type + ";" + id;
            }

            // There's no need to send a zero length message
            if (notifMessage.Length != 0)
            {
                // Make sure that the connection is still up and there is a message to send
                if (_socket != null)
                {
                    //_writer.WriteUInt32((uint)notifMessage.Length);
                    _writer.WriteString(notifMessage);

                    ConversationListBox.Items.Add("Sent: " + notifMessage);
                    WriteFile("Sent: " + notifMessage);

                    await _writer.StoreAsync();
                }
                else
                {
                    NotifyUser("No clients connected, please wait for a client to connect before attempting to send a notification", NotifyType.StatusMessage);
                }
            }
        }

        private void SendMessageBg(string type, UserNotification notification)
        {
            while (true)
            {
                // Get the toast binding, if present
                var toastBinding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                var id = notification.Id;

                var titleText = "No title";
                var bodyText = "";

                if (toastBinding != null)
                {
                    // And then get the text elements from the toast binding
                    var textElements = toastBinding.GetTextElements();

                    // Treat the first text element as the title text
                    titleText = textElements.FirstOrDefault()?.Text;

                    // We'll treat all subsequent text elements as body text,
                    // joining them together via newlines.
                    bodyText = string.Join("\n", textElements.Skip(1).Select(t => t.Text));
                }

                var notifMessage = type + ";" + id + ";" + titleText + ";" + bodyText;

                var previousMessage = (string) ApplicationData.Current.LocalSettings.Values["SendMessage"];

                // Make sure previous message has been sent
                if (previousMessage == null || previousMessage == "")
                {
                    // Save the current message to local settings so the background task can pick it up. 
                    ApplicationData.Current.LocalSettings.Values["SendMessage"] = notifMessage;

                    ConversationListBox.Items.Add("Sent: " + notifMessage);
                    WriteFile("Sent: " + notifMessage);
                }
                else
                {
                    // Do nothing until previous message has been sent.  
                    continue;
                }
                break;
            }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            DisconnectClick();
            NotifyUser("Disconnected.", NotifyType.StatusMessage);
        }

        private void DisconnectButtonBg_Click(object sender, RoutedEventArgs e)
        {
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
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    NotifyUser("Task cancelled unexpectedly - reason: " + settings.Values["TaskCancelationReason"].ToString(), NotifyType.ErrorMessage);
                });
            }
            else
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
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
            DisconnectBg();
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
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                ListenButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                ListenButtonBg.IsEnabled = true;
                _bluetooth.IsEnabled = true;
                ConversationListBox.Items.Clear();
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
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                ConversationListBox.Items.Clear();
                InitializeRfcommServer();
            });
        }

        private async void DisconnectBgClick()
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                ListenButtonBg.IsEnabled = true;
                DisconnectButtonBg.IsEnabled = false;
                ListenButton.IsEnabled = true;
                _bluetooth.IsEnabled = true;
                ConversationListBox.Items.Clear();

                // Unregistering the background task will remove the Rfcomm Chat Service from the SDP record and stop listening for incoming connections
                // First get the existing tasks to see if we already registered for it
                if (taskRegistration != null)
                {
                    taskRegistration.Unregister(true);
                    taskRegistration = null;
                    NotifyUser("Background watcher unregistered.", NotifyType.StatusMessage);
                }
                else
                {
                    // At this point we assume we haven't found any existing tasks matching the one we want to unregister
                    NotifyUser("No registered background watcher found.", NotifyType.StatusMessage);
                }
            });
        }

        private async void DisconnectBg()
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                ConversationListBox.Items.Clear();

                // Unregistering the background task will remove the Rfcomm Chat Service from the SDP record and stop listening for incoming connections
                // First get the existing tasks to see if we already registered for it
                if (taskRegistration != null)
                {
                    taskRegistration.Unregister(true);
                    taskRegistration = null;
                    NotifyUser("Background watcher unregistered.", NotifyType.StatusMessage);
                }
                else
                {
                    // At this point we assume we haven't found any existing tasks matching the one we want to unregister
                    NotifyUser("No registered background watcher found.", NotifyType.StatusMessage);
                }
                InitializeRfcommServerBg();
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

            if (ApplicationData.Current.LocalSettings.Values.Keys.Contains("ReceivedMessage"))
            {
                string backgroundMessage = (string)ApplicationData.Current.LocalSettings.Values["ReceivedMessage"];
                string remoteDeviceName = (string)ApplicationData.Current.LocalSettings.Values["RemoteDeviceName"];

                if (!backgroundMessage.Equals(""))
                {
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        NotifyUser("Client Connected: " + remoteDeviceName, NotifyType.StatusMessage);
                        CreateFile();
                        ConversationListBox.Items.Add("Received: " + backgroundMessage);
                        WriteFile("Received: " + backgroundMessage);
                        RemoveNotification(UInt32.Parse(backgroundMessage));
                        SendNotificationsOnConnectBg();
                    });
                }
            }
        }

        private void AttachProgressAndCompletedHandlers(IBackgroundTaskRegistration task)
        {
            task.Progress += new BackgroundTaskProgressEventHandler(OnProgress);
            task.Completed += new BackgroundTaskCompletedEventHandler(OnCompleted);
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
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
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

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                NotifyUser("Connected to Client: " + remoteDevice.Name, NotifyType.StatusMessage);
                DisconnectButton.Content = "Disconnect from " + remoteDevice.Name;
                CreateFile();
                SendNotificationsOnConnect();
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

                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        ConversationListBox.Items.Add("Received: " + message);
                        WriteFile("Received: " + message);
                        RemoveNotification(UInt32.Parse(message));
                    });
                }
                // Catch exception HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED).
                catch (Exception ex) when ((uint)ex.HResult == 0x800703E3)
                {
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        NotifyUser("Client (" + remoteDevice.Name + ") Disconnected Successfully", NotifyType.StatusMessage);
                        DisconnectButton.Content = "Disconnect Foreground Server";
                    });
                    break;
                }
            }

            reader.DetachStream();
            if (remoteDisconnection)
            {
                Disconnect();
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    NotifyUser("Client (" + remoteDevice.Name + ") disconnected", NotifyType.StatusMessage);
                    DisconnectButton.Content = "Disconnect Foreground Server";
                });
            }
        }

        private void Toggle_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var bluetoothSwitch = (ToggleSwitch) sender;
            if (bluetoothSwitch.IsEnabled)
            {
                if (_bluetooth.IsRadioOn == false)
                {
                    ListenButton.IsEnabled = false;
                    ListenButtonBg.IsEnabled = false;
                }
                else
                {
                    ListenButton.IsEnabled = true;
                    ListenButtonBg.IsEnabled = true;
                }
            }
            else
            {
                ListenButton.IsEnabled = false;
                ListenButtonBg.IsEnabled = false;
            }
        }
    }
}
