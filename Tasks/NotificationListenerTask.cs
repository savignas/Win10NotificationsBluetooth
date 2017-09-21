using System;
using Windows.ApplicationModel.Background;

namespace Tasks
{
    public sealed class NotificationListenerTask : IBackgroundTask
    {
        private BackgroundTaskDeferral _deferral;

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            // Get the deferral to prevent the task from closing prematurely
            _deferral = taskInstance.GetDeferral();

            await RfcommServerTask.UpdateNotifications();

            _deferral.Complete();
        }
    }
}
