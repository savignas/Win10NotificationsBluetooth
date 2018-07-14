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
using System.Text.RegularExpressions;
using System.Threading;
using Windows.ApplicationModel;
using Windows.Foundation;
using Microsoft.Toolkit.Uwp.Notifications;
using Tasks.Models;
using Win32;

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
        private static List<AndroidNotification> AndroidNotifications { get; } = new List<AndroidNotification>();

        private BackgroundTaskDeferral _deferral;
        private IBackgroundTaskInstance _taskInstance;
        private BackgroundTaskCancellationReason _cancelReason = BackgroundTaskCancellationReason.Abort;
        private bool _cancelRequested;

        //private ThreadPoolTimer _periodicTimer;
        private ThreadPoolTimer _songTitleTimer;

        private Timer _timer;

        private static readonly string PackageFamilyName = Package.Current.Id.FamilyName;

        private static readonly ApplicationDataContainer LocalSettings =
            ApplicationData.Current.LocalSettings;

        private readonly StorageFolder _localFolder =
            ApplicationData.Current.LocalFolder;

        private static List<NotificationApp> _notificationApps = new List<NotificationApp>();

        private static StorageFile _notificationAppsFile;

        private static byte[] _oldData;

        private static readonly Queue<string> SendMessages = new Queue<string>();

        private static string _songTitle;

        private static int _id;

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

            //_periodicTimer = ThreadPoolTimer.CreatePeriodicTimer(PeriodicTimerCallback, TimeSpan.FromSeconds(1));

            _songTitleTimer =
                ThreadPoolTimer.CreatePeriodicTimer(SongTitleTimerCallback, TimeSpan.FromMilliseconds(500));

            _timer = new Timer(TimerCallback, null, 500, 500);

            _listener = UserNotificationListener.Current;

            _notificationAppsFile = await _localFolder.GetFileAsync("notificationApps");
            await Logger.Logger.CreateLogFile();

            _oldData = await ReadNotificationApps();

            _id = 0;

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

                    Logger.Logger.Info("Connected to " + _remoteDevice.Name + "(" + _remoteDevice.BluetoothAddress + ")");
					
					await UpdateNotifications();
                }
                else
                {
                    LocalSettings.Values["BackgroundTaskStatus"] = "Trigger details returned null";
                    _deferral.Complete();
                }

                Logger.Logger.Info("Rfcomm Server Task instance");

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
            ToastNotificationManager.History.Clear();
            // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
            _deferral.Complete();
            Logger.Logger.Info("Rfcomm Server OnCanceled Event: " + _cancelReason);
        }

        public static void ShowNotification(string appName, string packageName, string title, string content, string key, string intent)
        {
            var androidNotification = AndroidNotifications.Find(n => n.Key == key);
            if (androidNotification != null)
            {
                androidNotification.Content = content;
            }
            else
            {
                androidNotification = new AndroidNotification
                {
                    Id = _id,
                    AppName = appName,
                    Content = content,
                    Key = key,
                    PackageName = packageName,
                    Title = title
                };
                AndroidNotifications.Add(androidNotification);
                _id++;
            }

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

                    },
                    Attribution = new ToastGenericAttributionText()
                    {
                        Text = appName
                    }
                }
            };
            var header = new ToastHeader(packageName, appName, "");
            var toastContent = new ToastContent
            {
                Header = header,
                Visual = visual,
                ActivationType = ToastActivationType.Background
            };
            var toastActions = new ToastActionsCustom();
            if (!string.IsNullOrEmpty(intent))
            {
                toastActions.Buttons.Add(new ToastButton("Open on Device", key)
                {
                    ActivationType = ToastActivationType.Background
                });
            }
            if (key.StartsWith("+"))
            {
                toastActions.Inputs.Add(new ToastTextBox("tbReply")
                {
                    PlaceholderContent = "Type a reply..."
                });
                toastActions.Buttons.Add(new ToastButton("Reply", key)
                {
                    ActivationType = ToastActivationType.Background,
                    TextBoxId = "tbReply"
                });
                    
                if (key.EndsWith("call"))
                {
                    toastActions.Buttons.Add(new ToastButtonDismiss("Dismiss Call"));
                    toastContent.Scenario = ToastScenario.IncomingCall;
                }
            }
            toastContent.Actions = toastActions;

            var toast = new ToastNotification(toastContent.GetXml())
            {
                Tag = androidNotification.Id.ToString()
            };
            ToastNotificationManager.CreateToastNotifier().Show(toast);
            Logger.Logger.Info("Showing Android notification...");
        }

        public static void SendSms(string phoneNumber, string text)
        {
            SendMessage(phoneNumber, text);
            Logger.Logger.Info("Sending SMS...");
        }

        public static void OpenApp(string key)
        {
            SendMessage(Type.Open, key);
            Logger.Logger.Info("Opening App (KEY=" + key + ") on Phone...");
        }

        public static void DismissCall(string key)
        {
            SendMessage(Type.Remove, key);
            Logger.Logger.Info("Dismissing call...");
        }

        private async Task ReceiveDataAsync()
        {
            try
            {
                while (true)
                {
                    var readLength = await _reader.LoadAsync(sizeof(int));
                    if (readLength < sizeof(int))
                    {
                        LocalSettings.Values["IsBackgroundTaskActive"] = false;
                        ToastNotificationManager.History.Clear();
                        // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
                        _deferral.Complete();
                    }
                    var currentLength = _reader.ReadInt32();

                    readLength = await _reader.LoadAsync((uint) currentLength);
                    if (readLength < currentLength)
                    {
                        LocalSettings.Values["IsBackgroundTaskActive"] = false;
                        ToastNotificationManager.History.Clear();
                        // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
                        _deferral.Complete();
                    }
                    var readMessage = _reader.ReadString((uint) currentLength);

                    var messageParts = readMessage.Split(';');
                    var notificationText = "";
                    for (var i = 7; i < messageParts.Length; i++)
                    {
                        notificationText += messageParts[i];
                        if (i + 1 < messageParts.Length)
                        {
                            notificationText += ';';
                        }
                    }

                    var textParts = new string[7];
                    var location = 0;
                    for (var i = 0; i < textParts.Length; i++)
                    {
                        textParts[i] = notificationText.Substring(location, int.Parse(messageParts[i]));
                        location += int.Parse(messageParts[i]);
                    }

                    var action = (Type) int.Parse(textParts[0]);
                    var key = textParts[1];
                    var title = textParts[2];
                    var text = textParts[3];
                    var appName = textParts[4];
                    var packageName = textParts[5];
                    var contentIntent = textParts[6];

                    switch (action)
                    {
                        case Type.Remove:
                            try
                            {
                                await RemoveNotification(uint.Parse(key));
                            }
                            catch (Exception)
                            {
                                var androidNotification = AndroidNotifications.Find(n => n.Key == key);
                                if (androidNotification != null)
                                {
                                    ToastNotificationManager.History.Remove(androidNotification.Id.ToString());
                                    AndroidNotifications.Remove(androidNotification);
                                }
                            }
                            break;
                        case Type.Add:
                            if (key.StartsWith("+"))
                            {
                                if (key.EndsWith("sms"))
                                {
                                    ShowNotification("SMS", "sms", title, text, key, null);
                                }
                                else if (key.EndsWith("call"))
                                {
                                    ShowNotification("Incoming call", "call", title, "Calling...", key, null);
                                }
                            }
                            else
                            {
                                ShowNotification(appName, packageName, title, text, key, contentIntent);
                            }
                            break;
                    }

                    LocalSettings.Values["ReceivedMessage"] = readMessage;
                    _taskInstance.Progress += 1;
                    Logger.Logger.Info("Received message: " + readMessage);
                }
            }
            catch (Exception ex)
            {
                LocalSettings.Values["TaskCancelationReason"] = ex.Message;
                _deferral.Complete();
                Logger.Logger.Error(ex.Message, ex);
            }

        }

        /*
        /// <summary>
        /// Periodically check if there's a new message and if there is, send it over the socket 
        /// </summary>
        /// <param name="timer"></param>
        
        private async void PeriodicTimerCallback(ThreadPoolTimer timer)
        {
            if (!_cancelRequested)
            if (!_cancelRequested)
            {
                string message;
                try
                {
                    message = SendMessages.Dequeue();
                }
                catch (Exception)
                {
                    return;
                }
                
                try
                {
                    // Make sure that the connection is still up and there is a message to send
                    if (_socket != null)
                    {
                        if (message == null) return;
                        _writer.WriteString(message);
                        await _writer.StoreAsync();
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
        }*/

        private async void TimerCallback(object state)
        {
            if (!_cancelRequested)
            {
                string message;
                try
                {
                    message = SendMessages.Dequeue();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    // Make sure that the connection is still up and there is a message to send
                    if (_socket != null)
                    {
                        if (message == null) return;
                        _writer.WriteString(message);
                        await _writer.StoreAsync();
                        Logger.Logger.Info("Message Sent");
                    }
                    else
                    {
                        _cancelReason = BackgroundTaskCancellationReason.ConditionLoss;
                        _deferral.Complete();
                        Logger.Logger.Info("Message was not sent. Socket is down!");
                    }
                }
                catch (Exception ex)
                {
                    LocalSettings.Values["TaskCancelationReason"] = ex.Message;
                    _deferral.Complete();
                    Logger.Logger.Error(ex.Message, ex);
                }
            }
            else
            {
                // Timer clean up
                _timer.Dispose();
                //
                // Write to LocalSettings to indicate that this background task ran.
                //
                LocalSettings.Values["TaskCancelationReason"] = _cancelReason.ToString();
                Logger.Logger.Info("TimerCallback" + _cancelReason.ToString());
            }
        }

        private void SongTitleTimerCallback(ThreadPoolTimer timer)
        {
            if (!_cancelRequested)
            {
                if (LocalSettings.Values["sendWinamp"] == null || !(bool) LocalSettings.Values["sendWinamp"]) return;
                var winamp = Winamp.GetSongTitle();

                if (winamp != null)
                {
                    if (_songTitle == winamp) return;
                    _songTitle = winamp;
                    if (_songTitle != "Paused" && _songTitle != "Stopped")
                    {
                        var elements = Regex.Split(_songTitle, @"\s-\s");
                        //SendMessages.Enqueue("1;30003;Winamp" + ";" + elements[1] + ";" + elements[0]);
                    }
                    else
                    {
                        //SendMessages.Enqueue("1;30003;Winamp;Winamp;Not Playing");
                    }
                }
                else
                {
                    if (_songTitle == null) return;
                    //SendMessages.Enqueue("0;30003");
                    _songTitle = null;
                }
            }
            else
            {
                _songTitleTimer.Cancel();
            }
        }

        public static IAsyncAction UpdateNotifications()
        {
            return Task.Run(async () =>
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

                        if ((bool)LocalSettings.Values["IsBackgroundTaskActive"] &&
                            existingNotif.AppInfo.PackageFamilyName != PackageFamilyName)
                        {
                            SendNotification(Type.Remove, existingNotif);
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

                            if ((bool)LocalSettings.Values["IsBackgroundTaskActive"] &&
                                platNotif.AppInfo.PackageFamilyName != PackageFamilyName)
                            {
                                SendNotification(Type.Add, platNotif);
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

                            if (_notificationApps.All(x => x.Key != platNotif.AppInfo.AppUserModelId) &&
                                platNotif.AppInfo.PackageFamilyName != PackageFamilyName)
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

                            if ((bool)LocalSettings.Values["IsBackgroundTaskActive"] &&
                                platNotif.AppInfo.PackageFamilyName != PackageFamilyName &&
                                _notificationApps.Any(x => x.Key == platNotif.AppInfo.AppUserModelId && x.Allowed))
                            {
                                SendNotification(Type.Add, platNotif);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }).AsAsyncAction();
        }

        public static void DismissAndroidNotification()
        {
            var androidNotificationHistory = ToastNotificationManager.History.GetHistory();
            for (var i = 0; i < _id; i++)
            {
                if (androidNotificationHistory.Any(n => n.Tag == i.ToString())) continue;

                var id = i;
                var androidNotification = AndroidNotifications.Find(n => n.Id == id);

                if ((bool)LocalSettings.Values["IsBackgroundTaskActive"] && androidNotification != null)
                {
                    SendMessage(Type.Remove, androidNotification.Key);
                }

                AndroidNotifications.Remove(androidNotification);
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

        private static async Task RemoveNotification(uint notifId)
        {
            try
            {
                _listener.RemoveNotification(notifId);
            }

            catch (Exception)
            {
                // ignored
            }

            await UpdateNotifications();
        }

        private static void SendNotification(Type type, UserNotification notification)
        {
            // Get the toast binding, if present
            var toastBinding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            var id = notification.Id;

            if (type == Type.Add)
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
                    titleText = textElements.FirstOrDefault()?.Text ?? "";

                    // We'll treat all subsequent text elements as body text,
                    // joining them together via newlines.
                    bodyText = string.Join("\n", textElements.Skip(1).Select(t => t.Text));
                }

                SendMessage(id.ToString(), titleText, bodyText, appName);
            }
            else
            {
                SendMessage(Type.Remove, id.ToString());
            }
            
        }

        private static void SendMessage(string id, string titleText, string bodyText, string appName)
        {
            var message = GenerateMessage(Type.Add, id, titleText, bodyText, appName);
            SendMessages.Enqueue(message);
        }

        private static void SendMessage(Type type, string id)
        {
            var message = GenerateMessage(type, id, "", "", "");
            SendMessages.Enqueue(message);
        }

        private static void SendMessage(string phoneNumber, string text)
        {
            var message = GenerateMessage(Type.Remove, phoneNumber, text, "", "");
            SendMessages.Enqueue(message);
        }

        private static string GenerateMessage(Type type, string id, string titleText, string bodyText, string appName)
        {
            return ((int)type).ToString().Length + ";" + id.Length + ";" + titleText.Length + ";" + bodyText.Length + ";" + appName.Length + ";" +
                   (int)type + id + titleText + bodyText + appName;
        }
    }
}
