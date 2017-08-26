using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
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
        private UserNotificationListener _listener;

        private readonly ApplicationDataContainer _localSettings =
            ApplicationData.Current.LocalSettings;

        private readonly StorageFolder _localFolder =
            ApplicationData.Current.LocalFolder;

        private List<NotificationApp> _notificationApps = new List<NotificationApp>();

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

                ListViewNotificationApps.ItemsSource = _notificationApps;
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private async void InitializeNotificationListener()
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
                    _localSettings.Values["sendNotifications"] = true;
                    SendNotifications.IsEnabled = true;
                    break;

                // This means the user has denied access.
                // Any further calls to RequestAccessAsync will instantly
                // return Denied. The user must go to the Windows settings
                // and manually allow access.
                case UserNotificationListenerAccessStatus.Denied:

                    SendNotifications.IsOn = false;
                    _localSettings.Values["sendNotifications"] = false;
                    SendNotifications.IsEnabled = true;
                    // Show UI explaining that listener features will not
                    // work until user allows access.
                    var dialog = new MessageDialog("You need to turn on access to notifications in privacy settings!", "Error");
                    dialog.Commands.Add(new UICommand { Label = "Close", Id = 0 });
                    await dialog.ShowAsync();
                    break;

                // This means the user closed the prompt without
                // selecting either allow or deny. Further calls to
                // RequestAccessAsync will show the dialog again.
                case UserNotificationListenerAccessStatus.Unspecified:

                    // Show UI that allows the user to bring up the prompt again
                    break;
            }
        }

        private async void GoBack()
        {
            var data = new List<byte>();
            foreach (var notificationApp in _notificationApps)
            {
                if (notificationApp.Delete) continue;
                var appData = notificationApp.Serialize();
                data.AddRange(appData);
            }

            var notificationApps = await _localFolder.GetFileAsync("notificationApps");
            await FileIO.WriteBytesAsync(notificationApps, data.ToArray());

            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
                return;

            // Navigate back if possible, and if the event has not 
            // already been handled .
            if (rootFrame.CanGoBack)
            {
                rootFrame.GoBack();
            }
        }

        private void Settings_BackRequested(object sender,
            BackRequestedEventArgs e)
        {
            if (e.Handled == false)
            {
                e.Handled = true;
                GoBack();
            }
        }

        private void Back_Tapped(object sender, TappedRoutedEventArgs e)
        {
            GoBack();
        }

        private void SendNotifications_Toggled(object sender, RoutedEventArgs e)
        {
            SendNotifications.IsEnabled = false;
            if (SendNotifications.IsOn)
            {
                InitializeNotificationListener();
            }
            else
            {
                _localSettings.Values["sendNotifications"] = false;
                SendNotifications.IsEnabled = true;
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var frameworkElement = sender as FrameworkElement;
            var button = sender as ToggleButton;
            if (!(frameworkElement?.Tag is NotificationApp app) || !(frameworkElement.Parent is Grid parent)) return;
            if (button?.IsChecked != null && (bool) button.IsChecked)
            {
                app.Delete = true;
                parent.Background = new SolidColorBrush(Colors.Red);
            }
            else
            {
                app.Delete = false;
                parent.Background = new SolidColorBrush(Colors.Transparent);
            }
        }

        private void DeleteAllButton_Click(object sender, RoutedEventArgs e)
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
        }
    }
}
