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

            RfcommServerTask.UpdateNotifications();

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

    }
}
