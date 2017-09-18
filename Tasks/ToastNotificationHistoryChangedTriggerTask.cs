using Windows.ApplicationModel.Background;
using Windows.UI.Notifications;

namespace Tasks
{
    public sealed class ToastNotificationHistoryChangedTriggerTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            if (!(taskInstance.TriggerDetails is ToastNotificationHistoryChangedTriggerDetail details))
                return;

            RfcommServerTask.DismissAndroidNotification();
        }
    }
}
