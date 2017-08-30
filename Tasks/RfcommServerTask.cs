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
using Microsoft.Toolkit.Uwp.Notifications;
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
        private static ObservableCollection<ToastNotification> AndroidNotifications { get; } = new ObservableCollection<ToastNotification>();

        private BackgroundTaskDeferral _deferral;
        private IBackgroundTaskInstance _taskInstance;
        private BackgroundTaskCancellationReason _cancelReason = BackgroundTaskCancellationReason.Abort;
        private bool _cancelRequested;

        ThreadPoolTimer _periodicTimer;

        private static readonly string PackageFamilyName = Package.Current.Id.FamilyName;

        private static readonly ApplicationDataContainer LocalSettings =
            ApplicationData.Current.LocalSettings;

        private readonly StorageFolder _localFolder =
            ApplicationData.Current.LocalFolder;

        private static List<NotificationApp> _notificationApps = new List<NotificationApp>();

        private static StorageFile _notificationAppsFile;

        private static byte[] _oldData;
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
            LocalSettings.Values["IsBackgroundTaskActive"] = true;

            _periodicTimer = ThreadPoolTimer.CreatePeriodicTimer(PeriodicTimerCallback, TimeSpan.FromSeconds(1));

            _listener = UserNotificationListener.Current;

            _notificationAppsFile = await _localFolder.GetFileAsync("notificationApps");

            _oldData = await ReadNotificationApps();

            try
            {
                var details = (RfcommConnectionTriggerDetails)taskInstance.TriggerDetails;
                if (details != null)
                {
                    _socket = details.Socket;
                    _remoteDevice = details.RemoteDevice;
                    LocalSettings.Values["RemoteDeviceName"] = _remoteDevice.Name;

                    _writer = new DataWriter(_socket.OutputStream);
                    _reader = new DataReader(_socket.InputStream);
                }
                else
                {
                    LocalSettings.Values["BackgroundTaskStatus"] = "Trigger details returned null";
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

            LocalSettings.Values["TaskCancelationReason"] = _cancelReason.ToString();
            LocalSettings.Values["IsBackgroundTaskActive"] = false;
            LocalSettings.Values["ReceivedMessage"] = "";
            // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
            _deferral.Complete();
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

            var toast = new ToastNotification(toastContent.GetXml()) { Tag = key };
            ToastNotificationManager.CreateToastNotifier().Show(toast);
            AndroidNotifications.Add(toast);
        }

        private async Task ReceiveDataAsync()
        {
            try
            {
                while (true)
                {
                    var readLength = await _reader.LoadAsync(sizeof(byte));
                    if (readLength < sizeof(byte))
                    {
                        LocalSettings.Values["IsBackgroundTaskActive"] = false;
                        // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
                        _deferral.Complete();
                    }
                    var currentLength = _reader.ReadByte();

                    readLength = await _reader.LoadAsync(currentLength);
                    if (readLength < currentLength)
                    {
                        LocalSettings.Values["IsBackgroundTaskActive"] = false;
                        // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
                        _deferral.Complete();
                    }
                    var message = _reader.ReadString(currentLength);

                    var messageParts = message.Split(';');

                    if (messageParts[0] == "0")
                    {
                        try
                        {
                            RemoveNotification(uint.Parse(messageParts[1]));
                        }
                        catch (Exception)
                        {
                            ToastNotificationManager.History.Remove(messageParts[1]);
                        }
                    }
                    else if (messageParts[0] == "1")
                    {
                        ShowNotification(messageParts[2], messageParts[3], messageParts[1]);
                    }

                    LocalSettings.Values["ReceivedMessage"] = message;
                    _taskInstance.Progress += 1;
                }
            }
            catch (Exception)
            {
                // ignored
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
                var message = (string) LocalSettings.Values["SendMessage"];
                if (string.IsNullOrEmpty(message)) return;
                try
                {
                    // Make sure that the connection is still up and there is a message to send
                    if (_socket != null)
                    {
                        //writer.WriteUInt32((uint)message.Length);
                        _writer.WriteString(message);
                        await _writer.StoreAsync();

                        LocalSettings.Values["SendMessage"] = null;
                    }
                    else
                    {
                        _cancelReason = BackgroundTaskCancellationReason.ConditionLoss;
                        _deferral.Complete();
                    }
                }
                catch (Exception ex)
                {
                    LocalSettings.Values["TaskCancelationReason"] = ex.Message;
                    LocalSettings.Values["SendMessage"] = null;
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
                LocalSettings.Values["TaskCancelationReason"] = _cancelReason.ToString();
            }
        }

        public static async void UpdateNotifications()
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

                    if ((bool) LocalSettings.Values["IsBackgroundTaskActive"] &&
                        existingNotif.AppInfo.PackageFamilyName != PackageFamilyName)
                    {
                        SendMessageBg("0", existingNotif);
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

                            if ((bool) LocalSettings.Values["IsBackgroundTaskActive"] &&
                                platNotif.AppInfo.PackageFamilyName != PackageFamilyName)
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

                        if (LocalSettings.Values["newSettings"] != null && (bool)LocalSettings.Values["newSettings"])
                        {
                            _oldData = await ReadNotificationApps();
                            LocalSettings.Values["newSettings"] = false;
                        }

                        if (_notificationApps.All(x => x.Key != platNotif.AppInfo.AppUserModelId))
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

                            var data = new byte[_oldData.Length + newData.Length];
                            System.Buffer.BlockCopy(_oldData, 0, data, 0, _oldData.Length);
                            System.Buffer.BlockCopy(newData, 0, data, _oldData.Length, newData.Length);

                            await FileIO.WriteBytesAsync(_notificationAppsFile, data);
                        }

                        if ((bool) LocalSettings.Values["IsBackgroundTaskActive"] &&
                            platNotif.AppInfo.PackageFamilyName != PackageFamilyName &&
                            _notificationApps.Any(x => x.Key == platNotif.AppInfo.AppUserModelId && x.Allowed))
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

        public static void DismissAndroidNotification()
        {
            var androidNotificationHistory = ToastNotificationManager.History.GetHistory();
            for (var i = 0; i < AndroidNotifications.Count; i++)
            {
                var existingAndroidNotif = AndroidNotifications[i];

                if (androidNotificationHistory.Any(n => n.Tag == existingAndroidNotif.Tag)) continue;
                AndroidNotifications.RemoveAt(i);
                i--;

                if ((bool) LocalSettings.Values["IsBackgroundTaskActive"])
                {
                    SendMessageBg("0;" + existingAndroidNotif.Tag);
                }
            }
        }

        private static async Task<byte[]> ReadNotificationApps()
        {
            try
            {
                var data = await FileIO.ReadBufferAsync(_notificationAppsFile);
                var notificationAppList = Deserialize(data.ToArray());
                _notificationApps = notificationAppList;
                return data.ToArray();
            }
            catch (Exception)
            {
                return new byte[] { };
            }

        }

        private static List<NotificationApp> Deserialize([ReadOnlyArray] byte[] data)
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

                var previousMessage = (string) LocalSettings.Values["SendMessage"];

                // Make sure previous message has been sent
                if (string.IsNullOrEmpty(previousMessage))
                {
                    // Save the current message to local settings so the background task can pick it up. 
                    LocalSettings.Values["SendMessage"] = notifMessage;
                }
                else
                {
                    // Do nothing until previous message has been sent.  
                    continue;
                }
                break;
            }
        }

        private static void SendMessageBg(string notifMessage)
        {
            while (true)
            {
                var previousMessage = (string) LocalSettings.Values["SendMessage"];

                // Make sure previous message has been sent
                if (string.IsNullOrEmpty(previousMessage))
                {
                    // Save the current message to local settings so the background task can pick it up. 
                    LocalSettings.Values["SendMessage"] = notifMessage;
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
