using System;
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
using System.Linq;

namespace Tasks
{
    // A background task always implements the IBackgroundTask interface.
    public sealed class RfcommServerTask : IBackgroundTask
    {
        // Networking
        private StreamSocket socket = null;
        private DataReader reader = null;
        private DataWriter writer = null;
        private BluetoothDevice remoteDevice = null;

        private static UserNotificationListener _listener;
        private static ObservableCollection<UserNotification> Notifications { get; } = new ObservableCollection<UserNotification>();

        private BackgroundTaskDeferral deferral = null;
        private IBackgroundTaskInstance taskInstance = null;
        private BackgroundTaskCancellationReason cancelReason = BackgroundTaskCancellationReason.Abort;
        private bool cancelRequested = false;

        ThreadPoolTimer periodicTimer = null;
        /// <summary>
        /// The entry point of a background task.
        /// </summary>
        /// <param name="taskInstance">The current background task instance.</param>
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            // Get the deferral to prevent the task from closing prematurely
            deferral = taskInstance.GetDeferral();

            // Setup our onCanceled callback and progress
            this.taskInstance = taskInstance;
            this.taskInstance.Canceled += new BackgroundTaskCanceledEventHandler(OnCanceled);
            this.taskInstance.Progress = 0;

            // Store a setting so that the app knows that the task is running. 
            ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"] = true;

            periodicTimer = ThreadPoolTimer.CreatePeriodicTimer(new TimerElapsedHandler(PeriodicTimerCallback), TimeSpan.FromSeconds(1));

            _listener = UserNotificationListener.Current;

            try
            {
                RfcommConnectionTriggerDetails details = (RfcommConnectionTriggerDetails)taskInstance.TriggerDetails;
                if (details != null)
                {
                    socket = details.Socket;
                    remoteDevice = details.RemoteDevice;
                    ApplicationData.Current.LocalSettings.Values["RemoteDeviceName"] = remoteDevice.Name;

                    writer = new DataWriter(socket.OutputStream);
                    reader = new DataReader(socket.InputStream);
                }
                else
                {
                    ApplicationData.Current.LocalSettings.Values["BackgroundTaskStatus"] = "Trigger details returned null";
                    deferral.Complete();
                }

                var result = await ReceiveDataAsync();
            }
            catch (Exception ex)
            {
                reader = null;
                writer = null;
                socket = null;
                deferral.Complete();

                Debug.WriteLine("Exception occurred while initializing the connection, hr = " + ex.HResult.ToString("X"));
            }
        }

        private void OnCanceled(IBackgroundTaskInstance taskInstance, BackgroundTaskCancellationReason reason)
        {
            cancelReason = reason;
            cancelRequested = true;

            ApplicationData.Current.LocalSettings.Values["TaskCancelationReason"] = cancelReason.ToString();
            ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"] = false;
            ApplicationData.Current.LocalSettings.Values["ReceivedMessage"] = "";
            // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
            deferral.Complete();
        }

        private async Task<int> ReceiveDataAsync()
        {
            while (true)
            {
                var readLength = await reader.LoadAsync(sizeof(byte));
                if (readLength < sizeof(byte))
                {
                    ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"] = false;
                    // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
                    deferral.Complete();
                }
                var currentLength = reader.ReadByte();

                readLength = await reader.LoadAsync(currentLength);
                if (readLength < currentLength)
                {
                    ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"] = false;
                    // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration). 
                    deferral.Complete();
                }
                string message = reader.ReadString(currentLength);

                ApplicationData.Current.LocalSettings.Values["ReceivedMessage"] = message;
                taskInstance.Progress += 1;
            }
        }

        /// <summary>
        /// Periodically check if there's a new message and if there is, send it over the socket 
        /// </summary>
        /// <param name="timer"></param>
        
        private async void PeriodicTimerCallback(ThreadPoolTimer timer)
        {
            if (!cancelRequested)
            {
                string message = (string)ApplicationData.Current.LocalSettings.Values["SendMessage"];
                if (!string.IsNullOrEmpty(message))
                {
                    try
                    {
                        // Make sure that the connection is still up and there is a message to send
                        if (socket != null)
                        {
                            //writer.WriteUInt32((uint)message.Length);
                            writer.WriteString(message);
                            await writer.StoreAsync();

                            ApplicationData.Current.LocalSettings.Values["SendMessage"] = null;
                        }
                        else
                        {
                            cancelReason = BackgroundTaskCancellationReason.ConditionLoss;
                            deferral.Complete();
                        }
                    }
                    catch (Exception ex)
                    {
                        ApplicationData.Current.LocalSettings.Values["TaskCancelationReason"] = ex.Message;
                        ApplicationData.Current.LocalSettings.Values["SendMessage"] = null;
                        deferral.Complete();
                    }
                }
            }
            else
            {
                // Timer clean up
                periodicTimer.Cancel();
                //
                // Write to LocalSettings to indicate that this background task ran.
                //
                ApplicationData.Current.LocalSettings.Values["TaskCancelationReason"] = cancelReason.ToString();
            }
        }

        static public async void UpdateNotifications()
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

                        if ((bool)ApplicationData.Current.LocalSettings.Values["IsBackgroundTaskActive"])
                        {
                            SendMessageBg("1", platNotif);
                        }
                    }
                }
            }
            catch (Exception ex) { }
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

        private static void SendMessageBg(string type, UserNotification notification)
        {
            while (true)
            {
                // Get the toast binding, if present
                var toastBinding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                var id = notification.Id;
                var notifMessage = "";

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
                        catch (Exception ex) { }

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
                if (previousMessage == null || previousMessage == "")
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
