using Windows.ApplicationModel.Background;
using Windows.Storage;

namespace Tasks
{
    public sealed class NotificationListenerTask : IBackgroundTask
    {
        private BackgroundTaskDeferral _deferral;
        private IBackgroundTaskInstance _taskInstance;
        private BackgroundTaskCancellationReason _cancelReason = BackgroundTaskCancellationReason.Abort;

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            // Get the deferral to prevent the task from closing prematurely
            _deferral = taskInstance.GetDeferral();

            // Setup our onCanceled callback and progress
            _taskInstance = taskInstance;
            _taskInstance.Canceled += OnCanceled;

            // Store a setting so that the app knows that the task is running. 
            ApplicationData.Current.LocalSettings.Values["IsNotificationListenerActive"] = true;

            RfcommServerTask.UpdateNotifications();

            ApplicationData.Current.LocalSettings.Values["IsNotificationListenerActive"] = false;
            _deferral.Complete();
        }

        private void OnCanceled(IBackgroundTaskInstance taskInstance, BackgroundTaskCancellationReason reason)
        {
            _cancelReason = reason;

            ApplicationData.Current.LocalSettings.Values["NotificationListenerCancelationReason"] = _cancelReason.ToString();
            ApplicationData.Current.LocalSettings.Values["IsNotificationListenerActive"] = false;
            // Complete the background task (this raises the OnCompleted event on the corresponding BackgroundTaskRegistration).
            _deferral.Complete();
        }

    }
}
