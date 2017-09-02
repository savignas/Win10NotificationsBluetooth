using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Win10Notifications.Models;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Win10Notifications
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class Settings
    {
        private readonly ApplicationDataContainer _localSettings =
            ApplicationData.Current.LocalSettings;

        private readonly StorageFolder _localFolder =
            ApplicationData.Current.LocalFolder;

        private UserNotificationListener _listener;

        private List<NotificationApp> _notificationApps = new List<NotificationApp>();

        private bool _sendNotifications;

        private int _itemsLoaded;

        private bool _loaded;

        private const string NotificationListenerTaskName = "UserNotificationChanged";
        private string notificationListenerTaskEntryPoint = "Tasks.NotificationListenerTask";
        private IBackgroundTaskRegistration _notificationListenerTaskRegistration;


        public Settings()
        {
            InitializeComponent();
            SetSendNotificationsToggle();
            ReadNotificationApps();

            SystemNavigationManager.GetForCurrentView().BackRequested += Settings_BackRequested;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (Window.Current.Content is Frame rootFrame && rootFrame.CanGoBack)
            {
                // Show UI in title bar if opted-in and in-app backstack is not empty.
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    AppViewBackButtonVisibility.Visible;
            }
            else
            {
                // Remove the UI from the title bar if in-app back stack is empty.
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    AppViewBackButtonVisibility.Collapsed;
            }
        }

        private async Task InitializeNotificationListener()
        {
            // Get the listener
            _listener = UserNotificationListener.Current;

            // And request access to the user's notifications (must be called from UI thread)
            var accessStatus = await _listener.RequestAccessAsync();

            switch (accessStatus)
            {
                // This means the user has granted access.
                case UserNotificationListenerAccessStatus.Allowed:

                    // Yay! Proceed as normal
                    break;

                // This means the user has denied access.
                // Any further calls to RequestAccessAsync will instantly
                // return Denied. The user must go to the Windows settings
                // and manually allow access.
                case UserNotificationListenerAccessStatus.Denied:

                    _sendNotifications = false;
                    // Show UI explaining that listener features will not
                    // work until user allows access.
                    var dialog = new MessageDialog("You need to turn on access to notifications in privacy settings!", "Error");
                    dialog.Commands.Add(new UICommand { Label = "Close", Id = 0 });
                    dialog.CancelCommandIndex = 0;
                    await dialog.ShowAsync();
                    break;

                // This means the user closed the prompt without
                // selecting either allow or deny. Further calls to
                // RequestAccessAsync will show the dialog again.
                case UserNotificationListenerAccessStatus.Unspecified:

                    // Show UI that allows the user to bring up the prompt again
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            if (_localSettings.Values["BgServer"] == null || !(bool) _localSettings.Values["BgServer"]) return;
            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                if (task.Value.Name != NotificationListenerTaskName) continue;
                _notificationListenerTaskRegistration = task.Value;
                break;
            }

            if (_notificationListenerTaskRegistration == null)
            {
                // Applications registering for background trigger must request for permission.
                var backgroundAccessStatus = await BackgroundExecutionManager.RequestAccessAsync();

                var builder = new BackgroundTaskBuilder {TaskEntryPoint = notificationListenerTaskEntryPoint};
                builder.SetTrigger(new UserNotificationChangedTrigger(NotificationKinds.Toast));
                builder.Name = NotificationListenerTaskName;

                try
                {
                    _notificationListenerTaskRegistration = builder.Register();
                    AttachProgressAndCompletedHandlersNotificationListener(_notificationListenerTaskRegistration);

                    // Even though the trigger is registered successfully, it might be blocked. Notify the user if that is the case.
                    if (backgroundAccessStatus != BackgroundAccessStatus.AlwaysAllowed && backgroundAccessStatus !=
                        BackgroundAccessStatus.AllowedSubjectToSystemPolicy)
                    {
                        var dialog = new MessageDialog("You need to turn on access to background in privacy settings!",
                            "Error");
                        dialog.Commands.Add(new UICommand {Label = "Close", Id = 0});
                        dialog.CancelCommandIndex = 0;
                        await dialog.ShowAsync();
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        private void AttachProgressAndCompletedHandlersNotificationListener(IBackgroundTaskRegistration task)
        {
            task.Completed += OnCompletedNotificationListener;
        }

        private void OnCompletedNotificationListener(BackgroundTaskRegistration sender, BackgroundTaskCompletedEventArgs args)
        {
            try
            {
                args.CheckResult();
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void SetSendNotificationsToggle()
        {
            var enabled = _localSettings.Values["sendNotifications"];
            if (enabled != null && (bool) enabled)
            {
                SendNotifications.IsOn = (bool) enabled;
            }
        }

        private async void ReadNotificationApps()
        {
            try
            {
                var notificationApps = await _localFolder.GetFileAsync("notificationApps");
                var data = await FileIO.ReadBufferAsync(notificationApps);
                _notificationApps = await NotificationApp.Deserialize(data.ToArray());

                _notificationApps.Sort((o1, o2) => string.Compare(o1.Name, o2.Name, StringComparison.Ordinal));

                ListViewNotificationApps.ItemsSource = _notificationApps;
            }
            catch (Exception)
            {
                ListViewNotificationApps.ItemsSource = null;
                _loaded = true;
            }
        }

        private static void GoBack()
        {
            if (!(Window.Current.Content is Frame rootFrame))
                return;

            // Navigate back if possible, and if the event has not 
            // already been handled .
            if (rootFrame.CanGoBack)
            {
                rootFrame.GoBack();
            }
        }

        private static void Settings_BackRequested(object sender,
            BackRequestedEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;
            GoBack();
        }

        private void Back_Tapped(object sender, TappedRoutedEventArgs e)
        {
            GoBack();
        }

        private void SendNotifications_Toggled(object sender, RoutedEventArgs e)
        {
            _sendNotifications = SendNotifications.IsOn;

            if (_loaded)
            {
                SaveButton.Visibility = Visibility.Visible;
            }
        }

        private void DeleteButton_Checked(object sender, RoutedEventArgs e)
        {
            var frameworkElement = sender as FrameworkElement;
            var button = sender as ToggleButton;
            if (!(frameworkElement?.Parent is Grid parent)) return;
            if (button?.IsChecked != null && (bool) button.IsChecked)
            {
                parent.Background = new SolidColorBrush(Colors.Red);
            }
            else
            {
                parent.Background = new SolidColorBrush(Colors.Transparent);
            }
            SaveButton.Visibility = Visibility.Visible;
        }

        private void DeleteAllButton_Checked(object sender, RoutedEventArgs e)
        {
            var button = sender as ToggleButton;
            if (button?.IsChecked != null && (bool) button.IsChecked)
            {
                foreach (var notificationApp in _notificationApps)
                {
                    notificationApp.Delete = true;
                }
                ListViewNotificationApps.Background = new SolidColorBrush(Colors.Red);
            }
            else
            {
                foreach (var notificationApp in _notificationApps)
                {
                    notificationApp.Delete = false;
                }
                ListViewNotificationApps.Background = new SolidColorBrush(Colors.Transparent);
            }
            SaveButton.Visibility = Visibility.Visible;
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveButton.Visibility = Visibility.Collapsed;

            if (_sendNotifications)
            {
                await InitializeNotificationListener();
            }

            SendNotifications.IsOn = _sendNotifications;
            _localSettings.Values["sendNotifications"] = _sendNotifications;

            var data = new List<byte>();
            foreach (var notificationApp in _notificationApps)
            {
                if (notificationApp.Delete) continue;
                var appData = notificationApp.Serialize();
                data.AddRange(appData);
            }

            var notificationApps = await _localFolder.GetFileAsync("notificationApps");
            await FileIO.WriteBytesAsync(notificationApps, data.ToArray());

            _loaded = false;
            DeleteAllButton.IsChecked = false;
            ListViewNotificationApps.Background = new SolidColorBrush(Colors.Transparent);
            ReadNotificationApps();

            _localSettings.Values["newSettings"] = true;
        }

        private void ToggleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                SaveButton.Visibility = Visibility.Visible;
            }
        }

        private void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            _itemsLoaded++;
            if (_itemsLoaded != _notificationApps.Count) return;
            _loaded = true;
            _itemsLoaded = 0;
        }
    }
}
