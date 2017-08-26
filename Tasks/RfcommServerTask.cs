using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Devices.Bluetooth.Background;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System.Threading;
using Windows.Networking.Sockets;
using Windows.Devices.Bluetooth;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.UI.Xaml.Media.Imaging;
using Tasks.Models;

namespace Tasks
{
    // A background task always implements the IBackgroundTask interface.
    public sealed class RfcommServerTask : IBackgroundTask
    {
        // Networking
        private StreamSocket _socket;
        private DataReader _reader;
        private DataWriter _writer;
        private BluetoothDevice _remoteDevice;

        private static UserNotificationListener _listener;
        private static ObservableCollection<UserNotification> Notifications { get; } = new ObservableCollection<UserNotification>();

        private BackgroundTaskDeferral _deferral;
        private IBackgroundTaskInstance _taskInstance;
        private BackgroundTaskCancellationReason _cancelReason = BackgroundTaskCancellationReason.Abort;
        private bool _cancelRequested;

        ThreadPoolTimer _periodicTimer;

        private static readonly string PackageFamilyName = Package.Current.Id.FamilyName;

        private static object _sendNotifications;

        private static readonly ApplicationDataContainer LocalSettings =
            ApplicationData.Current.LocalSettings;

        private readonly StorageFolder _localFolder =
            ApplicationData.Current.LocalFolder;

        private static List<NotificationApp> _notificationApps = new List<NotificationApp>();

        private static StorageFile _notificationAppsFile;
        /// <inheritdoc />
        /// <summary>
        /// The entry point of a background task.
        /// </summary>
        /// <param name="taskInstance">The current background task instance.</param>
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            // Get the deferral to prevent the task from closing prematurely
            _deferral = taskInstance.GetDeferral();

            // Setup our onCanceled callback and progress
            _taskInstance = taskInstance;
            _taskInstance.Canceled += OnCanceled;
            _taskInstance.Progress = 0;

            // Store a setting so that the app knows that the task is running. 
            ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"] = true;

            _periodicTimer = ThreadPoolTimer.CreatePeriodicTimer(PeriodicTimerCallback, TimeSpan.FromSeconds(1));

            _listener = UserNotificationListener.Current;

            _notificationAppsFile = await _localFolder.GetFileAsync("notificationApps");

            try
            {
                var details = (RfcommConnectionTriggerDetails)taskInstance.TriggerDetails;
                if (details != null)
                {
                    _socket = details.Socket;
                    _remoteDevice = details.RemoteDevice;
                    ApplicationData.Current.LocalSettings.Values["RemoteDeviceName"] = _remoteDevice.Name;

                    _writer = new DataWriter(_socket.OutputStream);
                    _reader = new DataReader(_socket.InputStream);
                }
                else
                {
                    ApplicationData.Current.LocalSettings.Values["BackgroundTaskStatus"] = "Trigger details returned null";
                    _deferral.Complete();
                }

                await ReceiveDataAsync();
            }
            catch (Exception ex)
            {
                _reader = null;
                _writer = null;
                _socket = null;
                _deferral.Complete();

                Debug.WriteLine("Exception occurred while initializing the connection, hr = " + ex.HResult.ToString("X"));
            }
        }

        private void OnCanceled(IBackgroundTaskInstance taskInstance, BackgroundTaskCancellationReason reason)
        {
            _cancelReason = reason;
            _cancelRequested = true;

            ApplicationData.Current.LocalSettings.Values["TaskCancelationReason"] = _cancelReason.ToString();
            ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"] = false;
            ApplicationData.Current.LocalSettings.Values["ReceivedMessage"] = "";
            // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
            _deferral.Complete();
        }

        private async Task<int> ReceiveDataAsync()
        {
            while (true)
            {
                var readLength = await _reader.LoadAsync(sizeof(byte));
                if (readLength < sizeof(byte))
                {
                    ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"] = false;
                    // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
                    _deferral.Complete();
                }
                var currentLength = _reader.ReadByte();

                readLength = await _reader.LoadAsync(currentLength);
                if (readLength < currentLength)
                {
                    ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"] = false;
                    // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
                    _deferral.Complete();
                }
                var message = _reader.ReadString(currentLength);

                ApplicationData.Current.LocalSettings.Values["ReceivedMessage"] = message;
                RemoveNotification(UInt32.Parse(message));
                _taskInstance.Progress += 1;
            }
        }

        /// <summary>
        /// Periodically check if there's a new message and if there is, send it over the socket 
        /// </summary>
        /// <param name="timer"></param>
        
        private async void PeriodicTimerCallback(ThreadPoolTimer timer)
        {
            if (!_cancelRequested)
            {
                var message = (string)ApplicationData.Current.LocalSettings.Values["SendMessage"];
                if (string.IsNullOrEmpty(message)) return;
                try
                {
                    // Make sure that the connection is still up and there is a message to send
                    if (_socket != null)
                    {
                        //writer.WriteUInt32((uint)message.Length);
                        _writer.WriteString(message);
                        await _writer.StoreAsync();

                        ApplicationData.Current.LocalSettings.Values["SendMessage"] = null;
                    }
                    else
                    {
                        _cancelReason = BackgroundTaskCancellationReason.ConditionLoss;
                        _deferral.Complete();
                    }
                }
                catch (Exception ex)
                {
                    ApplicationData.Current.LocalSettings.Values["TaskCancelationReason"] = ex.Message;
                    ApplicationData.Current.LocalSettings.Values["SendMessage"] = null;
                    _deferral.Complete();
                }
            }
            else
            {
                // Timer clean up
                _periodicTimer.Cancel();
                //
                // Write to LocalSettings to indicate that this background task ran.
                //
                ApplicationData.Current.LocalSettings.Values["TaskCancelationReason"] = _cancelReason.ToString();
            }
        }

        public static async void UpdateNotifications()
        {
            try
            {
                _sendNotifications = LocalSettings.Values["sendNotifications"];

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

                        if ((bool)ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"])
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

                            if ((bool)ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"])
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

                        var oldData = await ReadNotificationApps();

                        if (!_notificationApps.Any(x => x.Key == platNotif.AppInfo.AppUserModelId))
                        {
                            var notificationApp = new NotificationApp
                            {
                                Key = platNotif.AppInfo.AppUserModelId,
                                Name = platNotif.AppInfo.DisplayInfo.DisplayName,
                                Allowed = true
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

                            var data = new byte[oldData.Length + newData.Length];
                            System.Buffer.BlockCopy(oldData, 0, data, 0, oldData.Length);
                            System.Buffer.BlockCopy(newData, 0, data, oldData.Length, newData.Length);

                            await FileIO.WriteBytesAsync(_notificationAppsFile, data);
                        }

                        if ((bool)ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"] && platNotif.AppInfo.PackageFamilyName != PackageFamilyName &&
                            _sendNotifications != null && (bool)_sendNotifications && _notificationApps.Any(x => x.Key == platNotif.AppInfo.AppUserModelId && x.Allowed))
                        {
                            SendMessageBg("1", platNotif);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private static async Task<byte[]> ReadNotificationApps()
        {
            try
            {
                var data = await FileIO.ReadBufferAsync(_notificationAppsFile);
                var notificationAppList = await Deserialize(data.ToArray());
                _notificationApps = notificationAppList;
                return data.ToArray();
            }
            catch (Exception)
            {
                return new byte[] { };
            }

        }

        private static async Task<List<NotificationApp>> Deserialize([ReadOnlyArray] byte[] data)
        {
            using (var m = new MemoryStream(data))
            {
                using (var reader = new BinaryReader(m))
                {
                    var notificationApps = new List<NotificationApp>();

                    while (true)
                    {
                        try
                        {
                            var notificationApp = new NotificationApp
                            {
                                Key = reader.ReadString(),
                                Name = reader.ReadString(),
                                Allowed = reader.ReadBoolean()
                            };

                            var iconLenght = reader.ReadInt32();
                            if (iconLenght != 0)
                            {
                                var buffer = reader.ReadBytes(iconLenght);
                                notificationApp.IconData = buffer;
                                var stream = new MemoryStream(buffer).AsRandomAccessStream();
                                var icon = new BitmapImage();
                                await icon.SetSourceAsync(stream);

                                notificationApp.Icon = icon;
                            }

                            notificationApps.Add(notificationApp);
                        }
                        catch (Exception)
                        {
                            break;
                        }
                    }

                    return notificationApps;
                }
            }
        }

        private static int FindIndexOfNotification(uint notifId)
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

            catch (Exception)
            {
                // ignored
            }

            UpdateNotifications();
        }

        private static void SendMessageBg(string type, UserNotification notification)
        {
            while (true)
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

                var previousMessage = (string)ApplicationData.Current.LocalSettings.Values["SendMessage"];

                // Make sure previous message has been sent
                if (string.IsNullOrEmpty(previousMessage))
                {
                    // Save the current message to local settings so the background task can pick it up. 
                    ApplicationData.Current.LocalSettings.Values["SendMessage"] = notifMessage;
                }
                else
                {
                    // Do nothing until previous message has been sent.  
                    continue;
                }
                break;
            }
        }
    }   
}
