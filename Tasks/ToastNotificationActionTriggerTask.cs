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
            if (!(taskInstance.TriggerDetails is ToastNotificationActionTriggerDetail details)) return;
            var arguments = details.Argument;
            if (string.IsNullOrEmpty(arguments)) return;
            if (arguments.StartsWith("+"))
            {
                var userInput = details.UserInput.Values;
                if (userInput != null)
                {
                    var text = (string) userInput.First();
                    RfcommServerTask.SendSms(arguments, text);
                }
                else
                {
                    RfcommServerTask.DismissCall(arguments);
                }
            }
            else
            {
                RfcommServerTask.OpenApp(arguments);
            }
        }
    }
}
