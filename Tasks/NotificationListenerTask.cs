using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Storage;
using Windows.System.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Tasks
{
    public sealed class NotificationListenerTask : IBackgroundTask
    {
        private UserNotificationListener _listener;
        private ObservableCollection<UserNotification> Notifications { get; } = new ObservableCollection<UserNotification>();

        private BackgroundTaskDeferral deferral = null;
        private IBackgroundTaskInstance taskInstance = null;
        private BackgroundTaskCancellationReason cancelReason = BackgroundTaskCancellationReason.Abort;

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            // Get the deferral to prevent the task from closing prematurely
            deferral = taskInstance.GetDeferral();

            // Setup our onCanceled callback and progress
            this.taskInstance = taskInstance;
            this.taskInstance.Canceled += new BackgroundTaskCanceledEventHandler(OnCanceled);

            // Store a setting so that the app knows that the task is running. 
            ApplicationData.Current.LocalSettings.Values["IsNotificationListenerActive"] = true;

            // Get the listener
            _listener = UserNotificationListener.Current;

            UpdateNotifications();

            ApplicationData.Current.LocalSettings.Values["IsNotificationListenerActive"] = false;
            deferral.Complete();
        }

        private void OnCanceled(IBackgroundTaskInstance taskInstance, BackgroundTaskCancellationReason reason)
        {
            cancelReason = reason;

            ApplicationData.Current.LocalSettings.Values["NotificationListenerCancelationReason"] = cancelReason.ToString();
            ApplicationData.Current.LocalSettings.Values["IsNotificationListenerActive"] = false;
            // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration).
            deferral.Complete();
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

        private int FindIndexOfNotification(uint notifId)
        {
            for (var i = 0; i < Notifications.Count; i++)
            {
                if (Notifications[i].Id == notifId)
                    return i;
            }

            return -1;
        }

        private void SendMessageBg(string type, UserNotification notification)
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
