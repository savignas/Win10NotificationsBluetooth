using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.UI.Notifications;

namespace Tasks
{
    public sealed  class ToastNotificationActionTriggerTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            var deferral = taskInstance.GetDeferral();

            if (!(taskInstance.TriggerDetails is ToastNotificationActionTriggerDetail details)) return;
            var arguments = details.Argument;
            var userInput = details.UserInput.Values;
            var text = (string) userInput.First();

            RfcommServerTask.SendSms(arguments, text);

            deferral.Complete();
        }
    }
}
